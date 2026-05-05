using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Restocker.Common;
using Restocker.Execution;
using Restocker.Localization;

namespace Restocker.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Executor executor;
    private readonly RepriceTab repriceTab;
    private readonly ListTab listTab;

    public MainWindow(Configuration configuration, Executor executor)
        : base($"{Strings.WindowTitle}##MainWindow",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.configuration = configuration;
        this.executor = executor;
        this.repriceTab = new RepriceTab(configuration, executor);
        this.listTab = new ListTab(configuration, executor);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 420),
            MaximumSize = new Vector2(2400, 1600),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
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
        if (executor.IsRunning && executor.Mode == ExecutionMode.RefreshAll)
        {
            ImGui.TextUnformatted(string.Format(Strings.HeaderRefreshing, executor.CompletedJobs, executor.TotalJobs));
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.HeaderStop)) executor.Cancel();
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
