using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Restocker.Common;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Market;

namespace Restocker.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Executor executor;
    private readonly RepriceTab repriceTab;
    private readonly ListTab listTab;
    private readonly ConfirmDialog confirmDialog = new();

    public MainWindow(Configuration configuration, Executor executor, MarketWatcher marketWatcher)
        : base($"{Strings.WindowTitle}##MainWindow",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.configuration = configuration;
        this.executor = executor;
        this.repriceTab = new RepriceTab(configuration, executor, confirmDialog, marketWatcher.Cache);
        this.listTab = new ListTab(configuration, executor, confirmDialog);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 420),
            MaximumSize = new Vector2(2400, 1600),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        confirmDialog.Draw();
        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##restocker-tabs"))
        {
            if (ImGui.BeginTabItem(Strings.TabReprice))
            {
                repriceTab.Draw();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Strings.TabList))
            {
                listTab.Draw();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Strings.TabSettings))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawHeader()
    {
        var snapshots = configuration.Snapshots.Values.ToList();
        var freshest = snapshots.Count == 0
            ? Strings.HeaderUnknown
            : snapshots.Max(s => s.LastRefreshedUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        ImGui.TextUnformatted($"{Strings.HeaderCachedRetainers} {snapshots.Count} {Strings.HeaderRetainers} / {Strings.HeaderLastUpdated} {freshest}");

        ImGui.SameLine();
        if (executor.IsRunning)
        {
            var statusLabel = executor.Mode == ExecutionMode.RefreshAll
                ? string.Format(Strings.HeaderRefreshing, executor.CompletedJobs, executor.TotalJobs)
                : string.Format(Strings.ApplyProgress, executor.CompletedActions, executor.TotalActions, executor.CompletedJobs, executor.TotalJobs);
            ImGui.TextUnformatted(statusLabel);
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.HeaderStop)) executor.Cancel();
            if (!string.IsNullOrEmpty(executor.CurrentStateLabel))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"[{executor.CurrentStateLabel}]");
            }
        }
        else
        {
            var bellOpen = AddonHelper.IsOpen("RetainerList");
            if (!bellOpen) ImGui.BeginDisabled();
            if (ImGui.SmallButton(Strings.HeaderRefreshAll))
            {
                if (Executor.ActiveRetainersInDisplayOrder().Count == 0)
                {
                    Plugin.NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
                    {
                        Title = Strings.WindowTitle,
                        Content = Strings.ToastNoRetainersInBell,
                        Type = Dalamud.Interface.ImGuiNotification.NotificationType.Warning,
                    });
                }
                else
                {
                    executor.StartRefreshAll();
                }
            }
            if (!bellOpen) ImGui.EndDisabled();
            if (!bellOpen)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(" + Strings.ToastBellNotOpen + ")");
            }
        }

        // 直近の停止理由を見える化
        if (!string.IsNullOrEmpty(executor.StatusMessage))
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.4f, 1f), $"⚠ {executor.StatusMessage}");
        }
    }

    private void DrawSettingsTab()
    {
        var autoOpen = configuration.AutoOpenOnBell;
        if (ImGui.Checkbox(Strings.SettingsAutoOpenOnBell, ref autoOpen))
        {
            configuration.AutoOpenOnBell = autoOpen;
            configuration.Save();
        }

        ImGui.Spacing();

        var delta = configuration.UndercutDelta;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt(Strings.SettingsUndercutDelta, ref delta, 0))
        {
            if (delta < 0) delta = 0;
            configuration.UndercutDelta = delta;
            configuration.Save();
        }

        ImGui.Spacing();

        var langIdx = configuration.Language;
        var labels = new[]
        {
            Strings.SettingsLanguageAuto,
            Strings.LanguageNameEnglish,
            Strings.LanguageNameJapanese,
            Strings.LanguageNameGerman,
            Strings.LanguageNameFrench,
            Strings.LanguageNameChinese,
            Strings.LanguageNameKorean,
        };
        // langIdx: -1 = auto / 0..5 = explicit。コンボの 0 番目を auto にマップする。
        var combo = langIdx + 1;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo(Strings.SettingsLanguage, ref combo, labels, labels.Length))
        {
            configuration.Language = combo - 1;
            configuration.Save();
            Strings.SetLanguage(configuration.ResolveLanguage(Plugin.ClientState.ClientLanguage));
        }
    }
}
