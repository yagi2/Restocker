using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Restocker.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly RepriceTab repriceTab;

    public MainWindow(Configuration configuration)
        : base("Restocker##MainWindow",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.configuration = configuration;
        this.repriceTab = new RepriceTab(configuration);
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
            if (ImGui.BeginTabItem("リプライス"))
            {
                repriceTab.Draw();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("新規出品"))
            {
                DrawListTabPlaceholder();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("設定"))
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
            ? "未取得"
            : snapshots.Max(s => s.LastRefreshedUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        ImGui.TextUnformatted($"キャッシュ: {snapshots.Count} リテイナー / 最終更新 {freshest}");
        ImGui.SameLine();
        ImGui.BeginDisabled();
        ImGui.SmallButton("全リテイナー更新（未実装）");
        ImGui.EndDisabled();
    }

    private void DrawListTabPlaceholder()
    {
        ImGui.TextDisabled("新規分割出品画面は次コミット以降で実装。");
        var totalInv = configuration.Snapshots.Values.Sum(s => s.Inventory.Count);
        ImGui.TextUnformatted($"集計（参考）: インベントリ品 {totalInv} 件");
    }

    private void DrawSettingsTab()
    {
        var autoOpen = configuration.AutoOpenOnBell;
        if (ImGui.Checkbox("リテイナーベル展開時にウィンドウを自動表示", ref autoOpen))
        {
            configuration.AutoOpenOnBell = autoOpen;
            configuration.Save();
        }
    }
}
