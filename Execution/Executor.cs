using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Restocker.Common;
using Restocker.Data;

namespace Restocker.Execution;

/// <summary>
/// 自前ステートマシンによるリテイナー巡回・操作の実行器（Y モード）。
///
/// 0.1.x で実装済みのフロー:
///   - <see cref="ExecutionMode.RefreshAll"/>: 各リテイナーを呼び出し → SellList を一瞬開いて
///     RetainerWatcher が snapshot を取り直すのを待ち → 閉じる → 終了 → 次。
///
/// Reprice / NewListing は AwaitingSellDialog / ConfirmingSellDialog 経由で
/// 個別アクションを処理するパスがあるが、価格セットや qty セットの addon 操作は
/// addon 構造体の AskingPrice / Quantity を直接叩く方式で書いている (Marketbuddy 流)。
/// </summary>
public sealed unsafe class Executor : IDisposable
{
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly Restocker.Market.MarketCache? marketCache;
    /// <summary>FetchMarketPrice 完了後に発火するコールバック。RepriceTab が -1ギル を当てる用。</summary>
    public System.Action? OnFetchMarketCompleted;

    public ExecutionState State { get; private set; } = ExecutionState.Idle;
    public ExecutionMode Mode { get; private set; }
    public string? StatusMessage { get; private set; }
    public int CompletedJobs { get; private set; }
    public int TotalJobs => jobs.Count;
    public int CompletedActions { get; private set; }
    public int TotalActions { get; private set; }
    public bool IsRunning => State != ExecutionState.Idle && State != ExecutionState.Done && State != ExecutionState.Stopped;
    public string CurrentStateLabel => State.ToString();

    private readonly List<RetainerVisitJob> jobs = new();
    private int jobCursor;
    private readonly Queue<PlannedAction> currentJobActions = new();
    private readonly Queue<int> slotsToReadPrice = new();
    private string? readingSnapshotKey;
    private bool cancelRequested;
    /// <summary>このリテイナージョブ内で MoveToRetainerMarket に使った market slot。
    /// サーバ反映ラグで同じスロットを再ターゲットして "swap" 動作になることを防ぐ。</summary>
    private readonly HashSet<int> usedMarketSlots = new();

    /// <summary>AwaitingNewListing 用: 直前の MoveToRetainerMarket の対象 market slot。</summary>
    private int pendingListingSlot = -1;
    private uint pendingListingItemId;
    private bool pendingListingIsHQ;
    private int pendingListingQuantity;

    /// <summary>PreStagingSaddle 用: 最後の saddle slot 移動を発火した時刻。
    /// 最終 staging からの settle 時間を保証するため。</summary>
    private DateTime? lastStageAt;

    /// <summary>FetchAwaitingSellDialog で listing slot click を再送した記録。</summary>
    private bool fetchSlotClickRetried;
    private static readonly TimeSpan SaddleSettleAfterStage = TimeSpan.FromSeconds(3);

    private DateTime nextStepNoEarlierThan = DateTime.MinValue;
    private static readonly TimeSpan StepThrottle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SnapshotWait = TimeSpan.FromMilliseconds(700);
    /// <summary>テキスト一致の SelectString エントリが見つからなかった時に諦めるタイムアウト。</summary>
    private static readonly TimeSpan SelectStringTimeout = TimeSpan.FromSeconds(4);
    private DateTime? waitingSince;

    public Executor(IFramework framework, IPluginLog log, Configuration configuration, Restocker.Market.MarketCache? marketCache = null)
    {
        this.framework = framework;
        this.log = log;
        this.configuration = configuration;
        this.marketCache = marketCache;
        framework.Update += Tick;
    }

    public void Dispose() => framework.Update -= Tick;

    public void StartRefreshAll()
    {
        if (IsRunning) { log.Warning("[Restocker] Executor.Start while already running"); return; }
        var retainers = ActiveRetainersInDisplayOrder();
        jobs.Clear();
        foreach (var r in retainers)
        {
            jobs.Add(new RetainerVisitJob { RetainerName = r, Actions = new List<PlannedAction>() });
        }
        Begin(ExecutionMode.RefreshAll);
    }

    /// <summary>
    /// 単一アイテムの市場相場をインベントリ経由で取得する (ListTab 用)。
    /// AgentInventoryContext で「マーケットに出品」ダイアログを開き、
    /// ComparePrices ボタンを叩いて ItemSearchResult を出し、
    /// MarketWatcher が拾ったところで close。
    /// </summary>
    public void StartFetchMarketPriceForItem(uint itemId, bool isHQ)
    {
        if (IsRunning) { log.Warning("[Restocker] Executor.Start while already running"); return; }
        var rm = RetainerManager.Instance();
        var activeName = rm != null && rm->IsReady && rm->GetActiveRetainer() != null
            ? rm->GetActiveRetainer()->NameString
            : string.Empty;
        if (string.IsNullOrEmpty(activeName))
        {
            log.Warning("[Restocker] StartFetchMarketPriceForItem: no active retainer");
            return;
        }
        var actions = new List<PlannedAction>
        {
            new PlannedAction
            {
                Kind = PlannedActionKind.FetchMarketPriceViaInventory,
                ItemId = itemId,
                IsHQ = isHQ,
            },
        };
        jobs.Clear();
        jobs.Add(new RetainerVisitJob { RetainerName = activeName, Actions = actions });
        Begin(ExecutionMode.ApplyActions);
    }

    /// <summary>
    /// 指定リテイナーで該当 (itemId, isHQ) が出品中なら、その listing slot を開いて
    /// ComparePrices で市場価格を MarketCache に 1 件だけ取り込む。
    /// 完了後 <see cref="OnFetchMarketCompleted"/> を発火する。
    /// </summary>
    public bool StartFetchMarketPriceForListing(string retainerKey, uint itemId, bool isHQ)
    {
        if (IsRunning) { log.Warning("[Restocker] Executor.Start while already running"); return false; }
        if (!configuration.Snapshots.TryGetValue(retainerKey, out var snap)) return false;
        var listing = snap.Listings.FirstOrDefault(l => l.ItemId == itemId && l.IsHQ == isHQ);
        if (listing == null) return false;

        var actions = new List<PlannedAction>
        {
            new PlannedAction
            {
                Kind = PlannedActionKind.FetchMarketPrice,
                RetainerKey = retainerKey,
                ItemId = itemId,
                IsHQ = isHQ,
                ListingIndex = listing.ListingIndex,
            },
        };
        jobs.Clear();
        jobs.Add(new RetainerVisitJob { RetainerName = snap.RetainerName, Actions = actions });
        Begin(ExecutionMode.ApplyActions);
        return true;
    }

    /// <summary>
    /// 指定したリテイナーの listings から「(itemId, isHQ) ユニーク × 1 listing slot」を抽出し、
    /// それぞれを開いて ComparePrices で市場価格を MarketCache に取り込む。
    /// 価格変更は行わない。完了後 <see cref="OnFetchMarketCompleted"/> を発火する。
    /// </summary>
    public void StartFetchMarketPricesForRetainer(string retainerKey)
    {
        if (IsRunning) { log.Warning("[Restocker] Executor.Start while already running"); return; }
        if (!configuration.Snapshots.TryGetValue(retainerKey, out var snap))
        {
            log.Warning($"[Restocker] StartFetchMarketPricesForRetainer: unknown key {retainerKey}");
            return;
        }
        var seen = new HashSet<(uint, bool)>();
        var actions = new List<PlannedAction>();
        foreach (var l in snap.Listings.OrderBy(l => l.ListingIndex))
        {
            if (!seen.Add((l.ItemId, l.IsHQ))) continue;
            actions.Add(new PlannedAction
            {
                Kind = PlannedActionKind.FetchMarketPrice,
                RetainerKey = retainerKey,
                ItemId = l.ItemId,
                IsHQ = l.IsHQ,
                ListingIndex = l.ListingIndex,
            });
        }
        if (actions.Count == 0) { log.Info("[Restocker] no items to fetch"); return; }
        jobs.Clear();
        jobs.Add(new RetainerVisitJob { RetainerName = snap.RetainerName, Actions = actions });
        Begin(ExecutionMode.ApplyActions);
    }

    public void StartApplyActions(IEnumerable<PlannedAction> actions)
    {
        if (IsRunning) { log.Warning("[Restocker] Executor.Start while already running"); return; }
        // RetainerKey ごとにグループ化、各リテイナー名を解決
        jobs.Clear();
        foreach (var grp in actions.GroupBy(a => a.RetainerKey))
        {
            if (!configuration.Snapshots.TryGetValue(grp.Key, out var snap))
            {
                log.Warning($"[Restocker] unknown retainer key in plan: {grp.Key}");
                continue;
            }
            jobs.Add(new RetainerVisitJob
            {
                RetainerName = snap.RetainerName,
                Actions = grp.OrderBy(a => a.Kind).ThenBy(a => a.ListingIndex).ToList(),
            });
        }
        Begin(ExecutionMode.ApplyActions);
    }

    private void Begin(ExecutionMode mode)
    {
        Mode = mode;
        jobCursor = 0;
        CompletedJobs = 0;
        CompletedActions = 0;
        TotalActions = jobs.Sum(j => j.Actions.Count);
        cancelRequested = false;
        StatusMessage = null;
        waitingSince = null;
        usedMarketSlots.Clear();
        if (jobs.Count == 0) { State = ExecutionState.Done; return; }

        // smart-start: 既に対象リテイナーの SellList を開いている場合は
        // ベル巡回をスキップして直接 PerformingAction から走る
        if (AddonHelper.IsOpen("RetainerSellList"))
        {
            var rm = RetainerManager.Instance();
            var activeName = rm != null ? rm->GetActiveRetainer()->NameString : null;
            var firstMatchIdx = jobs.FindIndex(j => j.RetainerName == activeName);
            if (firstMatchIdx >= 0)
            {
                jobCursor = firstMatchIdx;
                currentJobActions.Clear();
                foreach (var a in jobs[jobCursor].Actions) currentJobActions.Enqueue(a);
                State = ExecutionState.PerformingAction;
                log.Info($"[Restocker] Executor start (smart): mode={mode}, jobs={jobs.Count}, actions={TotalActions}, starting at active retainer '{activeName}'");
                return;
            }
        }

        State = ExecutionState.SelectingRetainer;
        log.Info($"[Restocker] Executor start: mode={mode}, jobs={jobs.Count}, actions={TotalActions}");
    }

    public void Cancel()
    {
        if (!IsRunning) return;
        cancelRequested = true;
        log.Info("[Restocker] Executor cancel requested");
    }

    private void Tick(IFramework _)
    {
        if (!IsRunning) return;
        if (cancelRequested) { Stop("cancelled"); return; }
        if (DateTime.UtcNow < nextStepNoEarlierThan) return;

        try
        {
            switch (State)
            {
                case ExecutionState.SelectingRetainer: TickSelectingRetainer(); break;
                case ExecutionState.AwaitingSelectString: TickAwaitingSelectString(); break;
                case ExecutionState.OpeningSellList: TickOpeningSellList(); break;
                case ExecutionState.AwaitingSellList: TickAwaitingSellList(); break;
                case ExecutionState.PerformingAction: TickPerformingAction(); break;
                case ExecutionState.ReadingPrices: TickReadingPrices(); break;
                case ExecutionState.AwaitingSellDialogForReading: TickAwaitingSellDialogForReading(); break;
                case ExecutionState.AwaitingSellListAfterReading: TickAwaitingSellListAfterReading(); break;
                case ExecutionState.AwaitingContextMenu: TickAwaitingContextMenu(); break;
                case ExecutionState.ClickingPutUpForSale: TickClickingPutUpForSale(); break;
                case ExecutionState.AwaitingSellDialog: TickAwaitingSellDialog(); break;
                case ExecutionState.ConfirmingSellDialog: TickConfirmingSellDialog(); break;
                case ExecutionState.AwaitingSaddleMove: TickAwaitingSaddleMove(); break;
                case ExecutionState.PreStagingSaddle: TickPreStagingSaddle(); break;
                case ExecutionState.AwaitingNewListing: TickAwaitingNewListing(); break;
                case ExecutionState.FetchAwaitingSellDialog: TickFetchAwaitingSellDialog(); break;
                case ExecutionState.FetchAwaitingMarketData: TickFetchAwaitingMarketData(); break;
                case ExecutionState.FetchAwaitingSellListAfter: TickFetchAwaitingSellListAfter(); break;
                case ExecutionState.ClosingSellList: TickClosingSellList(); break;
                case ExecutionState.AwaitingSelectStringAfterSell: TickAwaitingSelectStringAfterSell(); break;
                case ExecutionState.DismissingRetainer: TickDismissingRetainer(); break;
                case ExecutionState.AwaitingDismissed: TickAwaitingDismissed(); break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] Executor tick exception");
            Stop($"tick exception: {ex.GetType().Name}");
        }
    }

    private void Throttle() => nextStepNoEarlierThan = DateTime.UtcNow + StepThrottle;
    private void Wait(TimeSpan span) => nextStepNoEarlierThan = DateTime.UtcNow + span;
    private void Stop(string reason)
    {
        State = ExecutionState.Stopped;
        StatusMessage = reason;
        log.Warning($"[Restocker] Executor stopped: {reason}");
    }

    // ---------------- 状態ごと ----------------

    private void TickSelectingRetainer()
    {
        if (jobCursor >= jobs.Count) { State = ExecutionState.Done; return; }

        var addon = AddonHelper.GetVisible("RetainerList");
        if (addon == null) { Stop("RetainerList not visible"); return; }

        var job = jobs[jobCursor];
        var ordered = ActiveRetainersInDisplayOrder();
        var idx = ordered.IndexOf(job.RetainerName);
        if (idx < 0) { Stop($"retainer '{job.RetainerName}' not in bell list"); return; }

        Callback.Fire(addon, true, 2, (uint)idx, Callback.ZeroAtkValue, Callback.ZeroAtkValue);
        log.Debug($"[Restocker] click retainer #{idx} = {job.RetainerName}");
        State = ExecutionState.AwaitingSelectString;
        Throttle();
    }

    private void TickAwaitingSelectString()
    {
        if (!AddonHelper.IsOpen("SelectString")) { waitingSince = null; return; }
        if (waitingSince == null) waitingSince = DateTime.UtcNow;

        var idx = SelectStringHelper.FindEntryIndex(AddonText.IsHaveRetainerSellItemsEntry);
        if (idx >= 0)
        {
            var entries = SelectStringHelper.EnumerateEntries();
            var matched = idx < entries.Count ? entries[idx] : "?";
            log.Information($"[Restocker] SelectString matched [{idx}] = '{matched}' (all: {string.Join(" | ", entries)})");
            waitingSince = null;
            State = ExecutionState.OpeningSellList;
            Throttle();
            return;
        }

        if (DateTime.UtcNow - waitingSince.Value > SelectStringTimeout)
        {
            var entries = SelectStringHelper.EnumerateEntries();
            log.Warning($"[Restocker] sell-items entry not found. Entries seen: {string.Join(" | ", entries)}");
            Stop("sell-items entry not found in retainer menu (see /xllog for entries)");
        }
    }

    private void TickOpeningSellList()
    {
        if (!SelectStringHelper.ClickEntry(AddonText.IsHaveRetainerSellItemsEntry)) return;
        State = ExecutionState.AwaitingSellList;
        Throttle();
    }

    private void TickAwaitingSellList()
    {
        if (!AddonHelper.IsOpen("RetainerSellList")) return;

        // 新しいリテイナーに入ったので市場スロットの占有メモはリセット
        usedMarketSlots.Clear();

        // PostRequestedUpdate で snapshot が拾われるまで一拍待つ
        Wait(SnapshotWait);

        // Refresh / Apply 共通: RetainerWatcher が PostRequestedUpdate で
        // GetRetainerMarketPrice 経由で各 listing の現在価格を埋めてくれるので、
        // ここでは PerformingAction に進めば良い (Refresh は Actions 空 → 即 close)。
        currentJobActions.Clear();
        foreach (var a in jobs[jobCursor].Actions) currentJobActions.Enqueue(a);

        // saddle source の NewListing が混じっていれば、必要量を bag に pre-staging
        // してから出品 phase に入る。1 件ずつ stage→listing をやると server で
        // staging のトランザクションを完了する前に出品コマンドが届いて拒否される
        // ことが観測されているため、staging を一括で先に終わらせる方式にした。
        if (jobs[jobCursor].Actions.Any(a =>
            a.Kind == PlannedActionKind.NewListing && a.SourceKey.EndsWith(".saddle")))
        {
            log.Information("[Restocker] saddle source detected, entering PreStagingSaddle");
            State = ExecutionState.PreStagingSaddle;
            waitingSince = null;
            return;
        }

        State = ExecutionState.PerformingAction;
    }

    /// <summary>
    /// 現在ジョブの全 NewListing(saddle source) 必要量を集計し、
    /// 既に bag にある分との差分だけ saddle → bag に staging する。
    /// 1 tick = 1 saddle slot 移動 + Wait。すべて満たされたら PerformingAction へ。
    /// </summary>
    private void TickPreStagingSaddle()
    {
        // (item, hq) ごとの必要総量
        var needs = new Dictionary<(uint itemId, bool isHQ), int>();
        foreach (var a in currentJobActions)
        {
            if (a.Kind != PlannedActionKind.NewListing) continue;
            if (!a.SourceKey.EndsWith(".saddle")) continue;
            var k = (a.ItemId, a.IsHQ);
            needs[k] = (needs.TryGetValue(k, out var v) ? v : 0) + a.Quantity;
        }

        var im = InventoryManager.Instance();
        if (im == null) { Stop("InventoryManager null"); return; }

        // 1 種類でも不足していれば 1 saddle slot を bag に運んで return
        foreach (var (k, need) in needs)
        {
            var inBag = TotalInCharBag(k.itemId, k.isHQ);
            if (inBag >= need) continue;

            log.Information($"[Restocker] pre-stage need item={k.itemId} hq={k.isHQ} need={need} inBag={inBag}, moving 1 saddle slot");
            if (!StageOneSaddleSlot(k.itemId, k.isHQ))
            {
                log.Warning($"[Restocker] pre-stage cannot stage item={k.itemId} hq={k.isHQ} (no saddle slot or no free bag slot). Continuing with what we have.");
                continue;
            }
            lastStageAt = DateTime.UtcNow;
            // server 側のトランザクションを待つ
            Wait(TimeSpan.FromMilliseconds(1000));
            return;
        }

        // 全てのアイテム需要が満たされた → 最後の staging から settle 時間を確保
        if (lastStageAt.HasValue)
        {
            var elapsed = DateTime.UtcNow - lastStageAt.Value;
            if (elapsed < SaddleSettleAfterStage)
            {
                var remaining = SaddleSettleAfterStage - elapsed;
                log.Information($"[Restocker] pre-staging satisfied, settling {remaining.TotalMilliseconds:F0}ms before listings");
                Wait(remaining);
                return;
            }
        }

        log.Information("[Restocker] pre-staging complete, starting listings");
        lastStageAt = null;
        State = ExecutionState.PerformingAction;
        Throttle();
    }

    /// <summary>キャラバッグ内の対象アイテムの合計数量。</summary>
    private int TotalInCharBag(uint itemId, bool isHQ)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        var total = 0;
        foreach (var t in CharBagContainers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != itemId) continue;
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != isHQ) continue;
                total += (int)item->Quantity;
            }
        }
        return total;
    }

    /// <summary>該当アイテムを持つ saddle slot を 1 つ char bag の空き slot に丸ごと移動。</summary>
    private bool StageOneSaddleSlot(uint itemId, bool isHQ)
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;

        foreach (var st in SaddleContainers)
        {
            var sc = im->GetInventoryContainer(st);
            if (sc == null) continue;
            for (var si = 0; si < sc->Size; si++)
            {
                var sit = sc->GetInventorySlot(si);
                if (sit == null || sit->ItemId != itemId) continue;
                var hq = (sit->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != isHQ) continue;
                if (sit->Quantity == 0) continue;

                foreach (var bt in CharBagContainers)
                {
                    var bc = im->GetInventoryContainer(bt);
                    if (bc == null) continue;
                    for (var bi = 0; bi < bc->Size; bi++)
                    {
                        var bit = bc->GetInventorySlot(bi);
                        if (bit != null && bit->ItemId == 0)
                        {
                            var ret = im->MoveItemSlot(st, (ushort)si, bt, (ushort)bi, true);
                            log.Information($"[Restocker] pre-stage {st}#{si}({sit->Quantity}) → {bt}#{bi} ret={ret}");
                            return true;
                        }
                    }
                }
                log.Warning("[Restocker] pre-stage: no free char bag slot");
                return false;
            }
        }
        return false;
    }

    private void TickReadingPrices()
    {
        if (slotsToReadPrice.Count == 0)
        {
            // 全 listing 読み終わり → snapshot 保存して closing へ
            if (!string.IsNullOrEmpty(readingSnapshotKey)) configuration.Save();
            readingSnapshotKey = null;
            State = ExecutionState.ClosingSellList;
            Throttle();
            return;
        }

        var slot = slotsToReadPrice.Peek();
        ClickListingSlot(slot);
        State = ExecutionState.AwaitingSellDialogForReading;
        Throttle();
    }

    private void TickAwaitingSellDialogForReading()
    {
        var addon = AddonHelper.GetVisible("RetainerSell");
        if (addon == null) return;
        var sell = (AddonRetainerSell*)addon;

        var price = ReadAskingPrice(sell);
        var slot = slotsToReadPrice.Dequeue();
        if (price > 0 && !string.IsNullOrEmpty(readingSnapshotKey)
            && configuration.Snapshots.TryGetValue(readingSnapshotKey, out var snap))
        {
            var listing = snap.Listings.FirstOrDefault(l => l.ListingIndex == slot);
            if (listing != null) listing.UnitPrice = price;
            log.Debug($"[Restocker] read price slot={slot} value={price}");
        }
        else
        {
            log.Warning($"[Restocker] could not read AskingPrice for slot={slot}");
        }

        // RetainerSell ダイアログを Cancel で閉じる（変更を保存しない）
        Callback.Fire(addon, true, -1);
        State = ExecutionState.AwaitingSellListAfterReading;
        Throttle();
    }

    private void TickAwaitingSellListAfterReading()
    {
        if (AddonHelper.IsOpen("RetainerSell")) return;
        if (!AddonHelper.IsOpen("RetainerSellList")) return;
        State = ExecutionState.ReadingPrices;
        Throttle();
    }

    private static long ReadAskingPrice(AddonRetainerSell* sell)
    {
        if (sell == null || sell->AskingPrice == null) return 0;
        var node = sell->AskingPrice->AtkTextNode;
        if (node == null) return 0;
        var text = node->NodeText.ToString() ?? string.Empty;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text) if (c >= '0' && c <= '9') sb.Append(c);
        return long.TryParse(sb.ToString(), out var v) ? v : 0;
    }

    private void TickPerformingAction()
    {
        // Refresh モードまたは Apply モードでアクション完了 → SellList を閉じる
        if (currentJobActions.Count == 0)
        {
            State = ExecutionState.ClosingSellList;
            Throttle();
            return;
        }

        var action = currentJobActions.Peek();
        switch (action.Kind)
        {
            case PlannedActionKind.FetchMarketPrice:
                // listing 行クリックは ContextMenu を開く。そこから「価格を変更する」
                // を選んで初めて RetainerSell ダイアログが開く。
                marketCache?.GetType(); // suppress unused warning if cache disabled
                Plugin.Instance?.SetMarketWatcherExpected(action.ItemId, action.IsHQ);
                ClickListingSlot(action.ListingIndex);
                State = ExecutionState.AwaitingContextMenu;
                waitingSince = null;
                Throttle();
                return;
            case PlannedActionKind.FetchMarketPriceViaInventory:
                if (!OpenContextMenuForAnySlot(action.ItemId, action.IsHQ))
                {
                    log.Warning($"[Restocker] FetchMarketPriceViaInventory: no inventory slot has item={action.ItemId} hq={action.IsHQ}");
                    currentJobActions.Dequeue();
                    Throttle();
                    return;
                }
                State = ExecutionState.AwaitingContextMenu;
                waitingSince = null;
                Throttle();
                return;
            case PlannedActionKind.Reprice:
                // InventoryManager.SetRetainerMarketPrice で直接価格を書き換え。
                // RetainerSell ダイアログを開かないので桁違いに速い。
                {
                    var im = InventoryManager.Instance();
                    if (im != null)
                    {
                        im->SetRetainerMarketPrice((short)action.ListingIndex,
                            (uint)Math.Min(action.UnitPrice, uint.MaxValue));
                        log.Debug($"[Restocker] SetRetainerMarketPrice slot={action.ListingIndex} price={action.UnitPrice}");
                        CompletedActions++;
                    }
                    currentJobActions.Dequeue();
                    Throttle();
                    return;
                }
            case PlannedActionKind.NewListing:
                // 新規出品は MoveToRetainerMarket の **直接 API** で 1 ショットで処理する。
                // ContextMenu 経由は「マーケットに出品」エントリが現在の FFXIV 環境で
                // 出ない条件があり信頼できない (確認: 7.x で表示されないケースあり)。
                // 直接 API なら RetainerSellList が開いた状態 + リテイナー召喚中で
                // server 側でアイテム移動と出品が同時にコミットされる。
                {
                    var sellList = AddonHelper.GetVisible("RetainerSellList");
                    var retLarge = AddonHelper.GetVisible("InventoryRetainerLarge");
                    log.Information($"[Restocker] NewListing: RetainerSellList visible={sellList != null}, InventoryRetainerLarge visible={retLarge != null}, item={action.ItemId} hq={action.IsHQ} qty={action.Quantity} price={action.UnitPrice} src={action.SourceKey}");
                    if (sellList == null)
                    {
                        log.Warning("[Restocker] NewListing: RetainerSellList is not open, cannot list");
                        currentJobActions.Dequeue();
                        Throttle();
                        return;
                    }

                    // saddle source は AwaitingSellList → PreStagingSaddle で
                    // 必要量を bag に運んでからこの phase に入っている前提なので、
                    // 個別 staging はもう不要。bag に該当アイテムが無ければ skip。
                    if (!ExecuteDirectListing(action, suppressWarning: false, dstSlotOut: out var dstSlot))
                    {
                        log.Warning($"[Restocker] NewListing skip (direct listing failed): item={action.ItemId} hq={action.IsHQ}");
                        currentJobActions.Dequeue();
                        Throttle();
                        return;
                    }
                    // server 反映を確認するまで次に進まない (race condition で動作不良になるため)
                    pendingListingSlot = dstSlot;
                    pendingListingItemId = action.ItemId;
                    pendingListingIsHQ = action.IsHQ;
                    pendingListingQuantity = action.Quantity;
                    State = ExecutionState.AwaitingNewListing;
                    waitingSince = DateTime.UtcNow;
                    Throttle();
                    return;
                }
        }
        Stop($"unknown action kind {action.Kind}");
    }

    private void ClickListingSlot(int slot)
    {
        var addon = AddonHelper.GetVisible("RetainerSellList");
        if (addon == null) { Stop("RetainerSellList not visible at click slot"); return; }
        // RetainerSellList の listing 行 click は 3 引数 (case, slot, 1)。
        // 第 3 引数のフラグが無いと addon 側で row click として解釈されず無視される。
        Callback.Fire(addon, true, 0, slot, 1);
        log.Information($"[Restocker] click listing slot {slot} on RetainerSellList");
    }

    private void TickAwaitingSellDialog()
    {
        var addon = AddonHelper.GetVisible("RetainerSell");
        if (addon == null) return;
        var sell = (AddonRetainerSell*)addon;

        var action = currentJobActions.Peek();
        // ECommons 流: AskingPrice 設定は Callback.Fire(addon, true, 2, value)
        Callback.Fire(addon, true, 2, (int)Math.Min(action.UnitPrice, int.MaxValue));
        if (action.Kind == PlannedActionKind.NewListing)
        {
            // Quantity は event 3 で設定
            Callback.Fire(addon, true, 3, action.Quantity);
        }
        log.Information($"[Restocker] RetainerSell set price={action.UnitPrice} qty={action.Quantity}");
        State = ExecutionState.ConfirmingSellDialog;
        Throttle();
    }

    private void TickAwaitingContextMenu()
    {
        if (!AddonHelper.IsOpen("ContextMenu"))
        {
            if (waitingSince == null) waitingSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - waitingSince.Value > TimeSpan.FromSeconds(3))
            {
                // 1 件詰まっても残り全部を巻き込みたくないので、stop ではなく skip。
                log.Warning("[Restocker] ContextMenu did not open, skipping this action");
                if (currentJobActions.Count > 0) currentJobActions.Dequeue();
                CompletedActions++;
                Plugin.Instance?.ClearMarketWatcherExpected();
                waitingSince = null;
                State = ExecutionState.PerformingAction;
                Throttle();
            }
            return;
        }
        waitingSince = null;
        State = ExecutionState.ClickingPutUpForSale;
        Throttle();
    }

    private void TickClickingPutUpForSale()
    {
        if (waitingSince == null) waitingSince = DateTime.UtcNow;
        var nextKind = currentJobActions.Count > 0 ? currentJobActions.Peek().Kind : PlannedActionKind.NewListing;

        // FetchMarketPrice (= 既存出品の listing 行クリック後) は「価格を変更する」を選ぶ。
        // それ以外 (NewListing 系) は「マーケットに出品する」を選ぶ。
        Predicate<string> predicate = nextKind == PlannedActionKind.FetchMarketPrice
            ? AddonText.IsAdjustPriceEntry
            : AddonText.IsPutUpForSaleEntry;
        var label = nextKind == PlannedActionKind.FetchMarketPrice ? "adjust-price" : "put-up-for-sale";

        if (ContextMenuHelper.ClickEntry(predicate))
        {
            waitingSince = null;
            State = nextKind switch
            {
                PlannedActionKind.FetchMarketPrice => ExecutionState.FetchAwaitingSellDialog,
                PlannedActionKind.FetchMarketPriceViaInventory => ExecutionState.FetchAwaitingSellDialog,
                _ => ExecutionState.AwaitingSellDialog,
            };
            Throttle();
            return;
        }
        if (DateTime.UtcNow - waitingSince!.Value > TimeSpan.FromSeconds(3))
        {
            var entries = ContextMenuHelper.EnumerateEntries();
            log.Warning($"[Restocker] {label} entry not found in ContextMenu. Entries: {string.Join(" | ", entries)}");
            ContextMenuHelper.Close();
            Stop($"{label} entry not found in ContextMenu (see /xllog)");
        }
    }

    /// <summary>
    /// 任意のインベントリ (retainer pages / char bag / saddle) に該当アイテムを持つ
    /// slot を 1 つ見つけ、AgentInventoryContext.OpenForItemSlot で右クリック相当を発火。
    /// 主に FetchMarketPriceViaInventory 用 (NewListing は SourceKey 制約があるので別関数)。
    /// </summary>
    private bool OpenContextMenuForAnySlot(uint itemId, bool isHQ)
    {
        var im = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (im == null) return false;
        var sellListAddon = AddonHelper.GetVisible("RetainerSellList");
        var addonId = sellListAddon != null ? sellListAddon->Id : (ushort)0;

        // 優先順: キャラバッグ → リテイナーバッグ → サドル
        // サドルは右クリックで「マーケットに出品」が出ない仕様なので最後の手段。
        // そもそも本プラグインから預け入れは不要、char bag からそのまま出品できる
        InventoryType[] all =
        {
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4,
            InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
            InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
            InventoryType.SaddleBag1, InventoryType.SaddleBag2,
            InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
        };
        foreach (var t in all)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != itemId) continue;
                var hq = (item->Flags & FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != isHQ) continue;
                if (item->Quantity == 0) continue;

                var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance();
                if (agent == null) return false;
                agent->OpenForItemSlot(t, i, 0, addonId);
                log.Debug($"[Restocker] open context for item={itemId} via {t}#{i}");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// MoveToRetainerMarket で 1 件出品する。
    /// SourceKey は「ヒント」として候補 container 順序に使うだけで、
    /// 結局は実機の InventoryManager 全体から item を見つけたら即発火する。
    /// (UI のセクション操作ミスや snapshot stale を吸収するため。)
    /// </summary>
    private bool ExecuteDirectListing(PlannedAction action) => ExecuteDirectListing(action, suppressWarning: false, dstSlotOut: out _);

    private bool ExecuteDirectListing(PlannedAction action, bool suppressWarning, out int dstSlotOut)
    {
        dstSlotOut = -1;
        var im = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (im == null) return false;

        if (action.Quantity <= 0)
        {
            log.Warning($"[Restocker] ExecuteDirectListing: quantity={action.Quantity} invalid, skipping");
            return false;
        }
        if (action.UnitPrice <= 0)
        {
            log.Warning($"[Restocker] ExecuteDirectListing: unitPrice={action.UnitPrice} invalid, skipping (set price first)");
            return false;
        }

        // 出品先の RetainerMarket で空きスロットを探す。
        // usedMarketSlots に入っているスロットは「ついさっき使った」のでサーバ反映前
        // でもターゲットしないことで swap 動作を防ぐ。
        var market = im->GetInventoryContainer(InventoryType.RetainerMarket);
        if (market == null) return false;
        var dstSlot = -1;
        for (var i = 0; i < market->Size; i++)
        {
            if (usedMarketSlots.Contains(i)) continue;
            var s = market->GetInventorySlot(i);
            if (s == null) continue;
            if (s->ItemId == 0) { dstSlot = i; break; }
        }
        if (dstSlot < 0) { log.Warning("[Restocker] no free RetainerMarket slot"); return false; }

        var sourceKey = string.IsNullOrEmpty(action.SourceKey) ? action.RetainerKey : action.SourceKey;
        var candidates = ResolveSearchOrder(sourceKey);

        foreach (var t in candidates)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != action.ItemId) continue;
                var hq = (item->Flags & FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != action.IsHQ) continue;
                if (item->Quantity < action.Quantity) continue;

                var price = (uint)Math.Min(action.UnitPrice, uint.MaxValue);
                var srcQtyBefore = item->Quantity;
                im->MoveToRetainerMarket(
                    t, (ushort)i,
                    InventoryType.RetainerMarket, (ushort)dstSlot,
                    (uint)action.Quantity,
                    price);
                usedMarketSlots.Add(dstSlot);
                dstSlotOut = dstSlot;
                // 直後に client 側の市場 slot を確認: ここで反映されないなら API が no-op
                var srcAfter = c->GetInventorySlot(i);
                var marketAfter = market->GetInventorySlot(dstSlot);
                log.Information(
                    $"[Restocker] listed src={t}#{i}(qty before={srcQtyBefore} after={srcAfter->Quantity}) -> market#{dstSlot} (item={marketAfter->ItemId} qty={marketAfter->Quantity}) requested qty={action.Quantity} price={price}");
                return true;
            }
        }

        if (suppressWarning) return false;

        // 失敗時は、各 container に該当 item があれば（qty 不足でも）情報を残す
        log.Warning($"[Restocker] no source slot with itemId={action.ItemId} hq={action.IsHQ} qty>={action.Quantity} (src hint={sourceKey}, searched={string.Join(",", candidates)})");
        foreach (var t in candidates)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var it = c->GetInventorySlot(i);
                if (it == null || it->ItemId == 0) continue;
                if (it->ItemId == action.ItemId)
                {
                    var hq = (it->Flags & FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.HighQuality) != 0;
                    log.Information($"  found {t}#{i} item={it->ItemId} hq={hq} qty={it->Quantity} (need hq={action.IsHQ} qty>={action.Quantity})");
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 検索順序: source ヒントに沿って優先するが、見つからなければ反対側も walk する。
    /// .saddle の場合は char bag (staging 後) のみ。
    /// </summary>
    private static InventoryType[] ResolveSearchOrder(string sourceKey)
    {
        if (sourceKey.EndsWith(".saddle"))
        {
            // 直接サドルから動かないので staging 後の char bag のみ見る
            return CharBagContainers;
        }
        if (sourceKey.StartsWith("char."))
        {
            // ヒントは char bag、ただし retainer bag にあれば fallback で使う
            return CombineUnique(CharBagContainers, RetainerInventoryContainersStatic);
        }
        // retainer 由来 → retainer bag を優先、見つからなければ char bag も
        return CombineUnique(RetainerInventoryContainersStatic, CharBagContainers);
    }

    private static InventoryType[] CombineUnique(InventoryType[] a, InventoryType[] b)
    {
        var seen = new HashSet<InventoryType>();
        var list = new List<InventoryType>(a.Length + b.Length);
        foreach (var x in a) if (seen.Add(x)) list.Add(x);
        foreach (var x in b) if (seen.Add(x)) list.Add(x);
        return list.ToArray();
    }

    private static readonly InventoryType[] RetainerInventoryContainersStatic =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    };

    private static InventoryType[] SourceContainersFor(string sourceKey)
    {
        if (sourceKey.StartsWith("char."))
        {
            // saddle / bag どちらの場合も MoveToRetainerMarket は char bag からしか動かない。
            // saddle source は staging で char bag に運んでから出品するため、
            // 検索対象も char bag のみ (見つからなければ caller が staging に走る)。
            return new[]
            {
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4,
            };
        }
        // retainer source -> retainer's own pages
        return new[]
        {
            InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
            InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
        };
    }

    private static readonly InventoryType[] SaddleContainers =
    {
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    };

    private static readonly InventoryType[] CharBagContainers =
    {
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    };

    /// <summary>キャラバッグに該当アイテムが minQuantity 以上入った slot があるか。</summary>
    private bool HasItemInCharBag(uint itemId, bool isHQ, int minQuantity)
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;
        foreach (var t in CharBagContainers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != itemId) continue;
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != isHQ) continue;
                if (item->Quantity >= minQuantity) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// サドル → キャラバッグへ **1 action ぶんだけ** スレッディングする。
    /// 連続発射するとサーバ反映が間に合わず IsLoaded フリッピングして
    /// 同じ saddle slot を再ターゲットしてしまうため、per-action にする。
    /// 1 saddle slot を 1 char bag 空きスロットへ MoveItemSlot するだけ。
    /// </summary>
    private bool TryStageSaddleToCharBag(PlannedAction action)
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;

        // 1) 該当アイテムを持つ saddle slot を 1 つ探す (qty>0、HQ 一致)
        var saddleType = (InventoryType)0;
        var saddleSlot = -1;
        var saddleQty = 0;
        foreach (var t in SaddleContainers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != action.ItemId) continue;
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != action.IsHQ) continue;
                if (item->Quantity == 0) continue;
                saddleType = t;
                saddleSlot = i;
                saddleQty = (int)item->Quantity;
                break;
            }
            if (saddleSlot >= 0) break;
        }
        if (saddleSlot < 0)
        {
            log.Warning($"[Restocker] saddle stage: no saddle slot has item={action.ItemId} hq={action.IsHQ}");
            return false;
        }

        // 2) 空きキャラバッグスロットを探す
        var bagType = (InventoryType)0;
        var bagSlot = -1;
        foreach (var bt in CharBagContainers)
        {
            var bc = im->GetInventoryContainer(bt);
            if (bc == null) continue;
            for (var bi = 0; bi < bc->Size; bi++)
            {
                var bit = bc->GetInventorySlot(bi);
                if (bit != null && bit->ItemId == 0)
                {
                    bagType = bt;
                    bagSlot = bi;
                    break;
                }
            }
            if (bagSlot >= 0) break;
        }
        if (bagSlot < 0)
        {
            log.Warning("[Restocker] saddle stage: no free char bag slot");
            return false;
        }

        // 3) 1 saddle slot 丸ごと → char bag 空きスロットへ
        var ret = im->MoveItemSlot(saddleType, (ushort)saddleSlot,
            bagType, (ushort)bagSlot, true);
        log.Information($"[Restocker] stage saddle {saddleType}#{saddleSlot}({saddleQty}) → char {bagType}#{bagSlot} ret={ret}");
        waitingSince = null;
        return true;
    }

    // ---------------- FetchMarketPrice flow ----------------

    private void TickFetchAwaitingSellDialog()
    {
        var addon = AddonHelper.GetVisible("RetainerSell");
        if (addon == null)
        {
            if (waitingSince == null)
            {
                waitingSince = DateTime.UtcNow;
                fetchSlotClickRetried = false;
            }
            var elapsed = DateTime.UtcNow - waitingSince.Value;
            // 2.5s 経って開いていなければ 1 回だけ click 再送
            if (!fetchSlotClickRetried && elapsed > TimeSpan.FromSeconds(2.5))
            {
                var action = currentJobActions.Peek();
                log.Warning($"[Restocker] RetainerSell did not open within 2.5s; retrying click on slot {action.ListingIndex}");
                ClickListingSlot(action.ListingIndex);
                fetchSlotClickRetried = true;
            }
            if (elapsed > TimeSpan.FromSeconds(6))
            {
                var action = currentJobActions.Peek();
                log.Warning($"[Restocker] RetainerSell never opened for item={action.ItemId} hq={action.IsHQ} slot={action.ListingIndex}, skipping. (RetainerSellList open={AddonHelper.IsOpen("RetainerSellList")})");
                currentJobActions.Dequeue();
                CompletedActions++;
                waitingSince = null;
                fetchSlotClickRetried = false;
                State = ExecutionState.PerformingAction;
                Throttle();
            }
            return;
        }
        waitingSince = null;
        fetchSlotClickRetried = false;

        var act = currentJobActions.Peek();
        // ComparePrices を click。AddonRetainerSell の event id 4 (Marketbuddy 慣例)
        Callback.Fire(addon, true, 4);
        log.Information($"[Restocker] clicked ComparePrices for item={act.ItemId} hq={act.IsHQ} (slot={act.ListingIndex})");
        State = ExecutionState.FetchAwaitingMarketData;
        waitingSince = DateTime.UtcNow;
        Throttle();
    }

    private void TickFetchAwaitingMarketData()
    {
        var action = currentJobActions.Peek();
        var ready = marketCache != null && marketCache.HasData(action.ItemId, action.IsHQ);
        var timeout = waitingSince != null && DateTime.UtcNow - waitingSince.Value > TimeSpan.FromSeconds(6);
        if (!ready && !timeout) return;

        if (!ready) log.Warning($"[Restocker] market fetch timeout for item={action.ItemId} hq={action.IsHQ} (ItemSearchResult open={AddonHelper.IsOpen("ItemSearchResult")})");
        else log.Information($"[Restocker] market data ready for item={action.ItemId} hq={action.IsHQ}, lowest={marketCache!.GetLowest(action.ItemId, action.IsHQ)}");

        // 1 件分の fetch 完了。expected フラグはクリアして、次の listing に進む。
        Plugin.Instance?.ClearMarketWatcherExpected();

        // ItemSearchResult を閉じる → RetainerSell に戻る
        var sr = AddonHelper.GetVisible("ItemSearchResult");
        if (sr != null) Callback.Fire(sr, true, -1);
        // RetainerSell も Cancel で閉じる (価格変更は別フェーズで SetRetainerMarketPrice 直で行うので)
        var sell = AddonHelper.GetVisible("RetainerSell");
        if (sell != null) Callback.Fire(sell, true, -1);

        State = ExecutionState.FetchAwaitingSellListAfter;
        waitingSince = null;
        Throttle();
    }

    private void TickFetchAwaitingSellListAfter()
    {
        if (AddonHelper.IsOpen("ItemSearchResult")) return;
        if (AddonHelper.IsOpen("RetainerSell")) return;
        if (!AddonHelper.IsOpen("RetainerSellList")) return;

        currentJobActions.Dequeue();
        CompletedActions++;
        // 全 fetch 完了時の callback 発火 (1 ジョブ・最後のアクション直後でも問題無い)
        if (currentJobActions.Count == 0)
        {
            try { OnFetchMarketCompleted?.Invoke(); }
            catch (Exception ex) { log.Error(ex, "[Restocker] OnFetchMarketCompleted threw"); }
        }
        State = ExecutionState.PerformingAction;
        Throttle();
    }

    private void TickAwaitingNewListing()
    {
        var im = InventoryManager.Instance();
        if (im == null) { Stop("InventoryManager null"); return; }
        var market = im->GetInventoryContainer(InventoryType.RetainerMarket);
        if (market == null) { Stop("RetainerMarket container null"); return; }

        // 直前に出品した market slot に該当アイテムが入って qty が一致したら confirm
        var slot = pendingListingSlot >= 0 && pendingListingSlot < market->Size
            ? market->GetInventorySlot(pendingListingSlot)
            : null;
        if (slot != null && slot->ItemId == pendingListingItemId && slot->Quantity >= pendingListingQuantity)
        {
            var hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            if (hq == pendingListingIsHQ)
            {
                CompletedActions++;
                currentJobActions.Dequeue();
                pendingListingSlot = -1;
                State = ExecutionState.PerformingAction;
                Throttle();
                return;
            }
        }

        if (waitingSince == null) waitingSince = DateTime.UtcNow;
        if (DateTime.UtcNow - waitingSince.Value > TimeSpan.FromSeconds(8))
        {
            log.Warning($"[Restocker] new listing not confirmed in market#{pendingListingSlot} for item={pendingListingItemId} hq={pendingListingIsHQ} qty={pendingListingQuantity} (server lag or rejected). Skipping.");
            currentJobActions.Dequeue();
            pendingListingSlot = -1;
            waitingSince = null;
            State = ExecutionState.PerformingAction;
            Throttle();
        }
    }

    private void TickAwaitingSaddleMove()
    {
        if (currentJobActions.Count == 0)
        {
            State = ExecutionState.PerformingAction;
            return;
        }
        var action = currentJobActions.Peek();
        var im = InventoryManager.Instance();
        if (im == null) { Stop("InventoryManager null"); return; }

        foreach (var t in CharBagContainers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != action.ItemId) continue;
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != action.IsHQ) continue;
                if (item->Quantity < action.Quantity) continue;
                waitingSince = null;
                State = ExecutionState.PerformingAction;
                // staging 直後に MoveToRetainerMarket を投げると server がトランザクション
                // を完了する前で出品が暗黙 reject されるケースを観測した
                // (10 件 OK の後 staging 経由になるとすべて confirmation 失敗)。
                // 追加で 1 秒 wait して server 側のコミットを待つ。
                Wait(TimeSpan.FromMilliseconds(1000));
                return;
            }
        }

        if (waitingSince == null) waitingSince = DateTime.UtcNow;
        if (DateTime.UtcNow - waitingSince.Value > TimeSpan.FromSeconds(5))
        {
            Stop("saddle staging timed out (server roundtrip too slow / move failed)");
        }
    }

    private void TickConfirmingSellDialog()
    {
        var addon = AddonHelper.GetVisible("RetainerSell");
        if (addon == null) { Stop("RetainerSell not visible at confirm"); return; }
        var sell = (AddonRetainerSell*)addon;
        // Confirm ボタンを ECommons 流の ReceiveEvent で click (FireCallback だと
        // server 側に届かないことがある)
        var ok = ButtonClick.Click(sell->Confirm, addon);
        if (!ok)
        {
            log.Warning("[Restocker] Confirm click failed");
            Stop("Confirm click failed");
            return;
        }
        log.Information("[Restocker] confirmed RetainerSell dialog");

        var dequeued = currentJobActions.Dequeue();
        CompletedActions++;
        // 新規出品の場合、市場スロットは server が次に空いてるところを使うはずなので
        // 我々の usedMarketSlots 追跡は厳密には不要だが、Reprice 用に Reprice 経路は別
        State = ExecutionState.PerformingAction;
        Throttle();
    }

    private void TickClosingSellList()
    {
        var addon = AddonHelper.GetVisible("RetainerSellList");
        if (addon == null)
        {
            // 既に閉じている = OK
            State = ExecutionState.AwaitingSelectStringAfterSell;
            Throttle();
            return;
        }
        Callback.Fire(addon, true, -1);
        log.Debug("[Restocker] closed RetainerSellList");
        State = ExecutionState.AwaitingSelectStringAfterSell;
        Throttle();
    }

    private void TickAwaitingSelectStringAfterSell()
    {
        if (!AddonHelper.IsOpen("SelectString")) { waitingSince = null; return; }
        if (waitingSince == null) waitingSince = DateTime.UtcNow;
        if (SelectStringHelper.HasEntry(AddonText.IsQuitEntry))
        {
            waitingSince = null;
            State = ExecutionState.DismissingRetainer;
            Throttle();
            return;
        }
        if (DateTime.UtcNow - waitingSince.Value > SelectStringTimeout)
        {
            var entries = SelectStringHelper.EnumerateEntries();
            log.Warning($"[Restocker] quit entry not found. Entries seen: {string.Join(" | ", entries)}");
            Stop("quit entry not found in retainer menu");
        }
    }

    private void TickDismissingRetainer()
    {
        if (!SelectStringHelper.ClickEntry(AddonText.IsQuitEntry)) return;
        State = ExecutionState.AwaitingDismissed;
        Throttle();
    }

    private void TickAwaitingDismissed()
    {
        if (AddonHelper.IsOpen("SelectString") || AddonHelper.IsOpen("RetainerSell") || AddonHelper.IsOpen("RetainerSellList"))
            return;
        if (!AddonHelper.IsOpen("RetainerList")) return;

        CompletedJobs++;
        jobCursor++;
        if (jobCursor >= jobs.Count)
        {
            State = ExecutionState.Done;
            log.Info($"[Restocker] Executor done: {CompletedJobs} retainers, {CompletedActions} actions");
            return;
        }
        State = ExecutionState.SelectingRetainer;
        Throttle();
    }

    /// <summary>RetainerManager から DisplayOrder 順に並べた現役リテイナー名リスト。</summary>
    public static List<string> ActiveRetainersInDisplayOrder()
    {
        var result = new List<(string Name, int DisplayOrder)>();
        var m = RetainerManager.Instance();
        if (m == null || !m->IsReady) return new List<string>();
        for (var i = 0; i < m->Retainers.Length; i++)
        {
            var r = m->Retainers[i];
            if (r.RetainerId == 0) continue;
            var name = r.NameString;
            if (string.IsNullOrEmpty(name)) continue;
            var displayOrder = m->DisplayOrder.IndexOf((byte)i);
            result.Add((name, displayOrder));
        }
        return result.OrderBy(x => x.DisplayOrder).Select(x => x.Name).ToList();
    }
}
