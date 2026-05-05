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

    private readonly List<RetainerVisitJob> jobs = new();
    private int jobCursor;
    private readonly Queue<PlannedAction> currentJobActions = new();
    private bool cancelRequested;

    private DateTime nextStepNoEarlierThan = DateTime.MinValue;
    private static readonly TimeSpan StepThrottle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SnapshotWait = TimeSpan.FromMilliseconds(700);

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
        if (jobs.Count == 0) { State = ExecutionState.Done; return; }
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
                case ExecutionState.AwaitingSellDialog: TickAwaitingSellDialog(); break;
                case ExecutionState.ConfirmingSellDialog: TickConfirmingSellDialog(); break;
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
        if (!AddonHelper.IsOpen("SelectString")) return;
        // 「マーケットに出品を任せる」が含まれている SelectString = リテイナーのメニューと判定
        if (!SelectStringHelper.HasEntry(AddonText.HaveRetainerSellItems)) return;
        State = ExecutionState.OpeningSellList;
        Throttle();
    }

    private void TickOpeningSellList()
    {
        if (!SelectStringHelper.ClickEntryByText(AddonText.HaveRetainerSellItems)) return;
        State = ExecutionState.AwaitingSellList;
        Throttle();
    }

    private void TickAwaitingSellList()
    {
        if (!AddonHelper.IsOpen("RetainerSellList")) return;

        // PostRequestedUpdate で snapshot が拾われるまで一拍待つ
        Wait(SnapshotWait);

        // このジョブのアクションを queue 化（Refresh は空のまま）
        currentJobActions.Clear();
        foreach (var a in jobs[jobCursor].Actions) currentJobActions.Enqueue(a);

        State = ExecutionState.PerformingAction;
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
                // 出品中スロット click → RetainerSell が出る
                ClickListingSlot(action.ListingIndex);
                State = ExecutionState.AwaitingSellDialog;
                Throttle();
                return;
            case PlannedActionKind.NewListing:
                // 新規出品はインベントリ側からの「マーケットに出品」コンテキスト発火が要る。
                // 0.1.x の段階では ContextMenu 操作の addon シーケンスを完成しきれていないため
                // 1 件だけ未実装として skip + 警告ログ。
                log.Warning($"[Restocker] NewListing action skipped (not yet implemented in 0.1.x): item={action.ItemId} qty={action.Quantity}");
                currentJobActions.Dequeue();
                CompletedActions++;
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
        if (action.Kind == PlannedActionKind.Reprice)
        {
            // 価格欄を newPrice に書き換え。Quantity は触らない。
            if (sell->AskingPrice != null)
            {
                sell->AskingPrice->SetValue((int)Math.Min(action.UnitPrice, int.MaxValue));
            }
        }
        State = ExecutionState.ConfirmingSellDialog;
        Throttle();
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
        if (!AddonHelper.IsOpen("SelectString")) return;
        State = ExecutionState.DismissingRetainer;
        Throttle();
    }

    private void TickDismissingRetainer()
    {
        if (!SelectStringHelper.ClickEntryByText(AddonText.Quit)) return;
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
