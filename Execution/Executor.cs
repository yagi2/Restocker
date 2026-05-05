using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Restocker.Data;

namespace Restocker.Execution;

/// <summary>
/// 計画 (PlannedAction の列) を **自前のステートマシン** で逐次実行する。
/// AutoRetainer に依存しない (Y モード)。
///
/// ⚠️ 0.1.0 の段階では UI のクリック発火（FireCallback の正確なパラメータ）が
/// in-game 検証なしに確定できないため、各クリック action は **TODO 実装** の
/// プレースホルダになっている。状態機械の遷移ロジック自体は完成しており、
/// クリック関数を 1 つずつ実装すれば動かせる骨格。
/// </summary>
public sealed unsafe class Executor : IDisposable
{
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;

    public ExecutionState State { get; private set; } = ExecutionState.Idle;
    public string? StatusMessage { get; private set; }
    public int CompletedSteps { get; private set; }
    public int TotalSteps => queue.Count + CompletedSteps;
    public bool IsRunning => State != ExecutionState.Idle && State != ExecutionState.Done && State != ExecutionState.Stopped;

    private readonly Queue<PlannedAction> queue = new();
    private string? currentRetainerKey;
    private bool cancelRequested;

    // 連続フレーム間の throttle
    private DateTime nextStepNoEarlierThan = DateTime.MinValue;
    private static readonly TimeSpan StepThrottle = TimeSpan.FromMilliseconds(250);

    public Executor(IFramework framework, IGameGui gameGui, IPluginLog log, Configuration configuration)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.log = log;
        this.configuration = configuration;

        framework.Update += Tick;
    }

    public void Dispose() => framework.Update -= Tick;

    public void Start(IEnumerable<PlannedAction> actions)
    {
        if (IsRunning)
        {
            log.Warning("[Restocker] Executor.Start while already running; ignored");
            return;
        }
        queue.Clear();
        // リテイナーごとにまとめて並べる（巡回回数を最小化）
        foreach (var a in actions.OrderBy(a => a.RetainerKey).ThenBy(a => a.Kind).ThenBy(a => a.ListingIndex))
            queue.Enqueue(a);
        CompletedSteps = 0;
        cancelRequested = false;
        State = ExecutionState.AwaitingBell;
        StatusMessage = null;
        log.Info($"[Restocker] Executor start: {queue.Count} actions queued");
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

        if (cancelRequested)
        {
            Stop("cancelled by user");
            return;
        }

        if (DateTime.UtcNow < nextStepNoEarlierThan) return;

        try
        {
            switch (State)
            {
                case ExecutionState.AwaitingBell: TickAwaitingBell(); break;
                case ExecutionState.SelectingRetainer: TickSelectingRetainer(); break;
                case ExecutionState.AwaitingSelectString: TickAwaitingSelectString(); break;
                case ExecutionState.OpeningSellList: TickOpeningSellList(); break;
                case ExecutionState.AwaitingSellList: TickAwaitingSellList(); break;
                case ExecutionState.PerformingAction: TickPerformingAction(); break;
                case ExecutionState.ClosingSellList: TickClosingSellList(); break;
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
    private void Stop(string reason)
    {
        State = ExecutionState.Stopped;
        StatusMessage = reason;
        log.Warning($"[Restocker] Executor stopped: {reason}");
    }

    // -------- 状態ごとの実装 --------

    private void TickAwaitingBell()
    {
        if (!IsAddonOpen("RetainerList")) { Stop("RetainerList addon not open"); return; }
        // 次のアクションのリテイナーをターゲットにする
        if (queue.Count == 0) { State = ExecutionState.Done; return; }
        var next = queue.Peek();
        currentRetainerKey = next.RetainerKey;
        State = ExecutionState.SelectingRetainer;
        Throttle();
    }

    private void TickSelectingRetainer()
    {
        if (currentRetainerKey == null) { Stop("no retainer key"); return; }
        if (!configuration.Snapshots.TryGetValue(currentRetainerKey, out var snap))
        {
            Stop($"unknown retainer key {currentRetainerKey}");
            return;
        }

        // TODO(in-game): RetainerList addon から snap.RetainerName を index で特定し
        //                FireCallback で選択する。AutoRetainer の AddonMaster.RetainerList を参照
        log.Warning($"[Restocker] TODO: ClickRetainer({snap.RetainerName})");
        Stop("ClickRetainer not implemented");
    }

    private void TickAwaitingSelectString()
    {
        if (!IsAddonOpen("SelectString")) return; // wait one more frame
        State = ExecutionState.OpeningSellList;
        Throttle();
    }

    private void TickOpeningSellList()
    {
        // TODO(in-game): SelectString から「マーケットに商品を出す」を選ぶ
        log.Warning("[Restocker] TODO: ClickSellListEntry");
        Stop("ClickSellListEntry not implemented");
    }

    private void TickAwaitingSellList()
    {
        if (!IsAddonOpen("RetainerSellList")) return;
        State = ExecutionState.PerformingAction;
        Throttle();
    }

    private void TickPerformingAction()
    {
        if (queue.Count == 0 || queue.Peek().RetainerKey != currentRetainerKey)
        {
            // このリテイナーぶんのアクションは終わり
            State = ExecutionState.ClosingSellList;
            Throttle();
            return;
        }
        var action = queue.Dequeue();
        // TODO(in-game): kind に応じて
        //   Reprice -> 該当 listing slot を click → RetainerSell が出たら NumericInput.SetValue → Confirm
        //   NewListing -> インベントリの該当アイテムを sell → RetainerSell の qty/price 入力 → Confirm
        log.Warning($"[Restocker] TODO: PerformAction Kind={action.Kind} item={action.ItemId} qty={action.Quantity} price={action.UnitPrice}");
        Stop("PerformAction not implemented");
    }

    private void TickClosingSellList()
    {
        // TODO(in-game): RetainerSellList を閉じる (FireCallback で -1 など)
        log.Warning("[Restocker] TODO: CloseSellList");
        Stop("CloseSellList not implemented");
    }

    private void TickDismissingRetainer()
    {
        // TODO(in-game): SelectString の「終了」を選ぶ
        log.Warning("[Restocker] TODO: DismissRetainer");
        Stop("DismissRetainer not implemented");
    }

    private void TickAwaitingDismissed()
    {
        if (IsAddonOpen("SelectString") || IsAddonOpen("RetainerSellList")) return;
        if (!IsAddonOpen("RetainerList")) return;
        State = ExecutionState.AwaitingBell;
        Throttle();
    }

    private bool IsAddonOpen(string name)
    {
        var addon = gameGui.GetAddonByName(name);
        if (addon.Address == nint.Zero) return false;
        var unitBase = (AtkUnitBase*)addon.Address;
        return unitBase != null && unitBase->IsVisible;
    }
}
