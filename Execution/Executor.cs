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

    private DateTime nextStepNoEarlierThan = DateTime.MinValue;
    private static readonly TimeSpan StepThrottle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SnapshotWait = TimeSpan.FromMilliseconds(700);
    /// <summary>テキスト一致の SelectString エントリが見つからなかった時に諦めるタイムアウト。</summary>
    private static readonly TimeSpan SelectStringTimeout = TimeSpan.FromSeconds(4);
    private DateTime? waitingSince;

    public Executor(IFramework framework, IPluginLog log, Configuration configuration)
    {
        this.framework = framework;
        this.log = log;
        this.configuration = configuration;
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

        if (SelectStringHelper.HasEntry(AddonText.IsHaveRetainerSellItemsEntry))
        {
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

        // PostRequestedUpdate で snapshot が拾われるまで一拍待つ
        Wait(SnapshotWait);

        // Refresh / Apply 共通: RetainerWatcher が PostRequestedUpdate で
        // GetRetainerMarketPrice 経由で各 listing の現在価格を埋めてくれるので、
        // ここでは PerformingAction に進めば良い (Refresh は Actions 空 → 即 close)。
        currentJobActions.Clear();
        foreach (var a in jobs[jobCursor].Actions) currentJobActions.Enqueue(a);
        State = ExecutionState.PerformingAction;
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
                // 1) リテイナー bag / キャラ bag (staging 済み含む) なら MoveToRetainerMarket 直接
                if (ExecuteDirectListing(action))
                {
                    CompletedActions++;
                    currentJobActions.Dequeue();
                    Throttle();
                    return;
                }
                // 2) サドル source の場合は staging が要る
                {
                    var sourceKey = string.IsNullOrEmpty(action.SourceKey) ? action.RetainerKey : action.SourceKey;
                    if (sourceKey.EndsWith(".saddle") && TryStageSaddleToCharBag(action))
                    {
                        // staging 発火 → AwaitingSaddleMove で着地を待つ。action は queue 先頭のまま
                        State = ExecutionState.AwaitingSaddleMove;
                        Throttle();
                        return;
                    }
                }
                // 3) どうしても無理 → skip
                log.Warning($"[Restocker] NewListing skip (no source slot): item={action.ItemId} src={action.SourceKey}");
                currentJobActions.Dequeue();
                Throttle();
                return;
        }
        Stop($"unknown action kind {action.Kind}");
    }

    private void ClickListingSlot(int slot)
    {
        var addon = AddonHelper.GetVisible("RetainerSellList");
        if (addon == null) { Stop("RetainerSellList not visible at click slot"); return; }
        // RetainerSellList の listing クリック: Callback.Fire(addon, true, 0, slot)
        // （ECommons の AddonRetainerSellList wrapper に準ずる）
        Callback.Fire(addon, true, 0, (uint)slot);
        log.Debug($"[Restocker] click listing slot {slot}");
    }

    private void TickAwaitingSellDialog()
    {
        var addon = AddonHelper.GetVisible("RetainerSell");
        if (addon == null) return;
        var sell = (AddonRetainerSell*)addon;

        var action = currentJobActions.Peek();
        if (sell->AskingPrice != null)
        {
            sell->AskingPrice->SetValue((int)Math.Min(action.UnitPrice, int.MaxValue));
        }
        if (action.Kind == PlannedActionKind.NewListing && sell->Quantity != null)
        {
            sell->Quantity->SetValue(action.Quantity);
        }
        State = ExecutionState.ConfirmingSellDialog;
        Throttle();
    }

    private void TickAwaitingContextMenu()
    {
        if (!AddonHelper.IsOpen("ContextMenu")) return;
        State = ExecutionState.ClickingPutUpForSale;
        Throttle();
    }

    private void TickClickingPutUpForSale()
    {
        if (!ContextMenuHelper.ClickEntry(AddonText.IsPutUpForSaleEntry)) return;
        State = ExecutionState.AwaitingSellDialog;
        Throttle();
    }

    /// <summary>
    /// MoveToRetainerMarket で 1 件出品する。
    /// SourceKey は「ヒント」として候補 container 順序に使うだけで、
    /// 結局は実機の InventoryManager 全体から item を見つけたら即発火する。
    /// (UI のセクション操作ミスや snapshot stale を吸収するため。)
    /// </summary>
    private bool ExecuteDirectListing(PlannedAction action)
    {
        var im = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (im == null) return false;

        // 出品先の RetainerMarket で空きスロットを探す
        var market = im->GetInventoryContainer(InventoryType.RetainerMarket);
        if (market == null || !market->IsLoaded) return false;
        var dstSlot = -1;
        for (var i = 0; i < market->Size; i++)
        {
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
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != action.ItemId) continue;
                var hq = (item->Flags & FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != action.IsHQ) continue;
                if (item->Quantity < action.Quantity) continue;

                im->MoveToRetainerMarket(
                    t, (ushort)i,
                    InventoryType.RetainerMarket, (ushort)dstSlot,
                    (uint)action.Quantity,
                    (uint)Math.Min(action.UnitPrice, uint.MaxValue));
                log.Debug($"[Restocker] MoveToRetainerMarket src={t}#{i}({item->Quantity}) -> market#{dstSlot} qty={action.Quantity} price={action.UnitPrice}");
                return true;
            }
        }

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

    /// <summary>
    /// サドル所在のアイテムを「出品 1 件分だけ」キャラバッグに移動する。
    /// SplitItem で正確に必要数だけ切り出してから MoveItemSlot で空きスロットへ。
    /// 成功時 true。
    /// </summary>
    private bool TryStageSaddleToCharBag(PlannedAction action)
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;

        // 1) 該当アイテムを持つサドルスロットを探す
        var saddleSrcType = (InventoryType)0;
        var saddleSrcSlot = -1;
        var saddleSrcQty = 0;
        foreach (var t in SaddleContainers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != action.ItemId) continue;
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != action.IsHQ) continue;
                if (item->Quantity < action.Quantity) continue;
                saddleSrcType = t;
                saddleSrcSlot = i;
                saddleSrcQty = (int)item->Quantity;
                break;
            }
            if (saddleSrcSlot >= 0) break;
        }
        if (saddleSrcSlot < 0)
        {
            log.Warning($"[Restocker] saddle staging: no saddle slot has item={action.ItemId} hq={action.IsHQ} qty>={action.Quantity}");
            return false;
        }

        // 2) キャラバッグの空きスロットを探す
        var charBagType = (InventoryType)0;
        var freeCharSlot = -1;
        foreach (var t in CharBagContainers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null) continue;
                if (item->ItemId == 0)
                {
                    charBagType = t;
                    freeCharSlot = i;
                    break;
                }
            }
            if (freeCharSlot >= 0) break;
        }
        if (freeCharSlot < 0)
        {
            log.Warning("[Restocker] saddle staging: no free char bag slot");
            return false;
        }

        // 3) 必要数だけ切り出して空きスロットへ。
        //    saddleSrcQty == action.Quantity の場合は丸ごと移動。
        //    それ以上の場合は SplitItem で qty を切り出してから移動。
        if (saddleSrcQty == action.Quantity)
        {
            var ret = im->MoveItemSlot(saddleSrcType, (ushort)saddleSrcSlot,
                charBagType, (ushort)freeCharSlot, false);
            log.Debug($"[Restocker] stage (whole) saddle={saddleSrcType}#{saddleSrcSlot} → char={charBagType}#{freeCharSlot} ret={ret}");
        }
        else
        {
            // 余剰分が char bag に残らないよう、必要数だけ split
            var split = im->SplitItem(saddleSrcType, (ushort)saddleSrcSlot, action.Quantity);
            log.Debug($"[Restocker] SplitItem saddle={saddleSrcType}#{saddleSrcSlot} qty={action.Quantity} ret={split}");
            // 直後の MoveItemSlot で free スロットへ着地させる（SplitItem の挙動として、
            // src スロットが分かれているはずなので同じ srcSlot を再指定する形にする）
            var ret = im->MoveItemSlot(saddleSrcType, (ushort)saddleSrcSlot,
                charBagType, (ushort)freeCharSlot, false);
            log.Debug($"[Restocker] move-after-split → char={charBagType}#{freeCharSlot} ret={ret}");
        }
        waitingSince = null;
        return true;
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
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->ItemId != action.ItemId) continue;
                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != action.IsHQ) continue;
                if (item->Quantity < action.Quantity) continue;
                waitingSince = null;
                State = ExecutionState.PerformingAction;
                Throttle();
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
        // Confirm ボタン click: AddonRetainerSell の confirm callback ID は 0
        Callback.Fire(addon, true, 0);
        log.Debug("[Restocker] confirmed RetainerSell dialog");

        currentJobActions.Dequeue();
        CompletedActions++;
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
