using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Restocker.Execution;
using Restocker.Localization;

namespace Restocker.Windows;

/// <summary>
/// Executor が走っている間だけ自動表示する小型の進捗ダイアログ。
/// 「Pre-staging」「Awaiting new listing」など内部 state が見えないと
/// 固まったように感じるため、現在何をしているかを常時可視化する。
/// </summary>
public sealed class ProgressWindow : Window
{
    private readonly Executor executor;

    public ProgressWindow(Executor executor)
        : base("Restocker — Running##restocker-progress",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        this.executor = executor;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 80),
            MaximumSize = new Vector2(560, 240),
        };
        // 開閉は executor.IsRunning 連動 (PreOpenCheck) で制御
        IsOpen = false;
    }

    public override void PreOpenCheck()
    {
        IsOpen = executor.IsRunning;
    }

    public override void Draw()
    {
        // モード行
        var modeLabel = executor.Mode == ExecutionMode.RefreshAll ? Strings.ProgressModeRefresh : Strings.ProgressModeApply;
        ImGui.TextUnformatted($"{Strings.ProgressMode}: {modeLabel}");

        // state 行
        ImGui.TextUnformatted($"{Strings.ProgressStep}: {LocalizeState(executor.State)}");

        // ジョブ進捗
        if (executor.TotalJobs > 0)
        {
            var jobFrac = executor.TotalJobs > 0 ? (float)executor.CompletedJobs / executor.TotalJobs : 0f;
            ImGui.ProgressBar(jobFrac, new Vector2(-1, 0),
                string.Format(Strings.ProgressJobs, executor.CompletedJobs, executor.TotalJobs));
        }

        // アクション進捗
        if (executor.TotalActions > 0)
        {
            var actFrac = (float)executor.CompletedActions / executor.TotalActions;
            ImGui.ProgressBar(actFrac, new Vector2(-1, 0),
                string.Format(Strings.ProgressActions, executor.CompletedActions, executor.TotalActions));
        }

        if (!string.IsNullOrEmpty(executor.StatusMessage))
        {
            ImGui.Separator();
            ImGui.TextDisabled(executor.StatusMessage);
        }

        ImGui.Separator();
        if (ImGui.Button(Strings.HeaderStop + "##progress-cancel"))
        {
            executor.Cancel();
        }
    }

    private static string LocalizeState(ExecutionState s) => s switch
    {
        ExecutionState.Idle => "—",
        ExecutionState.SelectingRetainer => Strings.StateSelectingRetainer,
        ExecutionState.AwaitingSelectString => Strings.StateAwaitingSelectString,
        ExecutionState.OpeningSellList => Strings.StateOpeningSellList,
        ExecutionState.AwaitingSellList => Strings.StateAwaitingSellList,
        ExecutionState.PerformingAction => Strings.StatePerformingAction,
        ExecutionState.AwaitingContextMenu => Strings.StateAwaitingContextMenu,
        ExecutionState.ClickingPutUpForSale => Strings.StateClickingPutUpForSale,
        ExecutionState.ReadingPrices => Strings.StateReadingPrices,
        ExecutionState.AwaitingSellDialogForReading => Strings.StateAwaitingSellDialog,
        ExecutionState.AwaitingSellListAfterReading => Strings.StateAwaitingSellList,
        ExecutionState.AwaitingSellDialog => Strings.StateAwaitingSellDialog,
        ExecutionState.ConfirmingSellDialog => Strings.StateConfirmingSellDialog,
        ExecutionState.AwaitingSaddleMove => Strings.StateAwaitingSaddleMove,
        ExecutionState.PreStagingSaddle => Strings.StatePreStagingSaddle,
        ExecutionState.AwaitingNewListing => Strings.StateAwaitingNewListing,
        ExecutionState.FetchAwaitingSellDialog => Strings.StateAwaitingSellDialog,
        ExecutionState.FetchAwaitingMarketData => Strings.StateFetchAwaitingMarketData,
        ExecutionState.FetchAwaitingSellListAfter => Strings.StateAwaitingSellList,
        ExecutionState.ClosingSellList => Strings.StateClosingSellList,
        ExecutionState.AwaitingSelectStringAfterSell => Strings.StateAwaitingSelectString,
        ExecutionState.DismissingRetainer => Strings.StateDismissingRetainer,
        ExecutionState.AwaitingDismissed => Strings.StateAwaitingDismissed,
        ExecutionState.Done => Strings.StateDone,
        ExecutionState.Stopped => Strings.StateStopped,
        _ => s.ToString(),
    };
}
