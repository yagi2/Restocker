using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Restocker.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public MainWindow(Configuration configuration)
        : base("Restocker##MainWindow",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.configuration = configuration;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 360),
            MaximumSize = new Vector2(2400, 1600),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextUnformatted("Restocker — リテイナーマーケット一括操作");
        ImGui.Separator();
        ImGui.TextDisabled("MVP 雛形。次コミット以降で UI を肉付けする。");

        ImGui.Spacing();
        ImGui.TextUnformatted($"Snapshots: {configuration.Snapshots.Count} 件キャッシュ済み");
    }
}
