using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Restocker;

/// <summary>
/// リテイナーベル（RetainerList addon）の表示状態を 150ms ポーリングし、
/// 開閉に合わせて MainWindow の表示/非表示を同期させる。RepeatBuy の ShopWatcher 流。
/// </summary>
public sealed unsafe class BellWatcher : IDisposable
{
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly Func<bool> isEnabled;
    private readonly Action<bool> setMainWindowOpen;

    private bool lastVisible;
    private DateTime nextPoll = DateTime.MinValue;
    private readonly TimeSpan pollInterval = TimeSpan.FromMilliseconds(150);

    public BellWatcher(
        IFramework framework,
        IGameGui gameGui,
        Action<bool> setMainWindowOpen,
        Func<bool> isEnabled)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.setMainWindowOpen = setMainWindowOpen;
        this.isEnabled = isEnabled;

        framework.Update += OnUpdate;
    }

    public bool IsBellOpen()
    {
        var addon = gameGui.GetAddonByName("RetainerList");
        if (addon.Address == nint.Zero) return false;
        var unitBase = (AtkUnitBase*)addon.Address;
        return unitBase != null && unitBase->IsVisible;
    }

    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextPoll) return;
        nextPoll = now + pollInterval;

        if (!isEnabled()) return;

        var visible = IsBellOpen();
        if (visible != lastVisible)
        {
            setMainWindowOpen(visible);
            lastVisible = visible;
        }
    }

    public void Dispose() => framework.Update -= OnUpdate;
}
