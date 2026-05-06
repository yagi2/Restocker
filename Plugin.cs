using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Restocker.Windows;
using Restocker.Localization;
using Restocker.Execution;
using Restocker.Market;

namespace Restocker;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;

    private const string CommandName = "/restocker";

    public static Configuration Configuration { get; private set; } = null!;
    public static Plugin Instance { get; private set; } = null!;
    public RetainerWatcher RetainerWatcher { get; private set; } = null!;
    public readonly WindowSystem WindowSystem = new("Restocker");

    private MainWindow MainWindow { get; }
    private ProgressWindow ProgressWindow { get; }
    private BellWatcher BellWatcher { get; }
    private AutoRetainerDetector ArDetector { get; }
    private Executor Executor { get; }
    private MarketWatcher MarketWatcher { get; }

    public Plugin()
    {
        Instance = this;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Strings.SetLanguage(Configuration.ResolveLanguage(ClientState.ClientLanguage));

        MarketWatcher = new MarketWatcher(AddonLifecycle, Framework, Log);
        Executor = new Executor(Framework, Log, Configuration, MarketWatcher.Cache);

        MainWindow = new MainWindow(Configuration, Executor, MarketWatcher);
        WindowSystem.AddWindow(MainWindow);
        ProgressWindow = new ProgressWindow(Executor);
        WindowSystem.AddWindow(ProgressWindow);

        RetainerWatcher = new RetainerWatcher(
            Configuration,
            AddonLifecycle,
            PlayerState,
            ObjectTable,
            DataManager,
            Log
        );

        BellWatcher = new BellWatcher(
            Framework,
            GameGui,
            open => MainWindow.IsOpen = open,
            () => Configuration.AutoOpenOnBell,
            () => RetainerWatcher.CaptureCharacterSnapshot()
        );

        ArDetector = new AutoRetainerDetector(PluginInterface, NotificationManager, Log);
        ArDetector.WarnIfPresent();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Restocker window."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        // 設定タブは MainWindow 内にあるので OpenConfigUi も同じウィンドウへ
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        MarketWatcher.Dispose();
        Executor.Dispose();
        BellWatcher.Dispose();
        RetainerWatcher.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        ToggleMainUi();
    }

    public void ToggleMainUi() => MainWindow.Toggle();
}
