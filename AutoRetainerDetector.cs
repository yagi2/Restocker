using System;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Restocker.Localization;

namespace Restocker;

/// <summary>
/// AutoRetainer プラグインがロードされているかを起動時に検出し、
/// もし併用なら「巡回が衝突するかも」とトースト警告を 1 回だけ出す。
/// ブロックはせず、あくまで通知のみ（ユーザー設計判断）。
/// </summary>
public sealed class AutoRetainerDetector
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly INotificationManager notifications;
    private readonly IPluginLog log;

    private bool warned;

    public AutoRetainerDetector(
        IDalamudPluginInterface pluginInterface,
        INotificationManager notifications,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.notifications = notifications;
        this.log = log;
    }

    /// <summary>AutoRetainer が同居していたら 1 度だけトーストで警告する。</summary>
    public void WarnIfPresent()
    {
        if (warned) return;
        if (!IsAutoRetainerLoaded()) return;
        warned = true;

        log.Info("[Restocker] AutoRetainer detected; both manage retainer summon flow — coordinate manually.");
        notifications.AddNotification(new Notification
        {
            Title = Strings.WindowTitle,
            Content = Strings.ToastAutoRetainerPresent,
            Type = NotificationType.Warning,
            InitialDuration = TimeSpan.FromSeconds(8),
        });
    }

    public bool IsAutoRetainerLoaded()
    {
        foreach (var p in pluginInterface.InstalledPlugins)
        {
            if (p.IsLoaded && string.Equals(p.InternalName, "AutoRetainer", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
