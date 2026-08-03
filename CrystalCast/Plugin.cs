using CrystalCast.Rendering;
using CrystalCast.Sync;
using CrystalCast.Video;
using CrystalCast.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CrystalCast;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/crystalcast";
    private const string SettingsCommandName = "/ccsettings";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("CrystalCast");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly WineWebView2SetupWindow? wineWebView2SetupWindow;
    private readonly FirstRunGuideWindow firstRunGuideWindow;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateIpc ipc;

    public Plugin()
    {
        try
        {
            var browserDataClearStatus = BrowserProfileManager.ApplyPendingClearRequest();
            if (!string.IsNullOrEmpty(browserDataClearStatus))
                Log.Information("{BrowserDataClearStatus}", browserDataClearStatus);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not apply the pending CrystalCast browser-data clear request; it will be retried on the next load.");
        }

        var placementResolver = new ScreenPlacementResolver(ObjectTable, ClientState);
        var services = new CrystalCastServices(
            PluginInterface,
            TextureProvider,
            ClientState,
            ObjectTable,
            Framework,
            GameGui,
            Log,
            placementResolver,
            new BrowserFrameSourceFactory(Log));
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.AttachPersistence(configuration => PluginInterface.SavePluginConfig(configuration));
        if (Configuration.Normalize())
            Configuration.Save();

        renderer = new WorldScreenManager(Configuration, services);
        ipc = new ScreenStateIpc(Configuration, renderer, services);
        mainWindow = new MainWindow(this, renderer, ipc, placementResolver);
        configWindow = new ConfigWindow(this, renderer, ipc);
#if DEBUG
        wineWebView2SetupWindow = new WineWebView2SetupWindow(this);
#else
        wineWebView2SetupWindow = WineEnvironment.IsWine ? new WineWebView2SetupWindow(this) : null;
#endif
        firstRunGuideWindow = new FirstRunGuideWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        if (wineWebView2SetupWindow != null)
            windowSystem.AddWindow(wineWebView2SetupWindow);
        windowSystem.AddWindow(firstRunGuideWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the CrystalCast world-screen controls.",
        });
        CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnSettingsCommand)
        {
            HelpMessage = "Open the CrystalCast settings window.",
        });

        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        ClientState.TerritoryChanged += OnTerritoryChanged;

        Log.Information("CrystalCast loaded.");
    }

    public Configuration Configuration { get; }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.TerritoryChanged -= OnTerritoryChanged;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(SettingsCommandName);
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();
        wineWebView2SetupWindow?.Dispose();
        firstRunGuideWindow.Dispose();
        ipc.Dispose();
        renderer.Dispose();
        Configuration.FlushPendingSave();
    }

    public void ToggleMainUi() => mainWindow.Toggle();
    public void ToggleConfigUi() => configWindow.Toggle();

    internal void ShowWineWebView2Setup()
    {
        if (wineWebView2SetupWindow == null)
            return;

        Configuration.WineWebView2SetupDismissed = false;
        Configuration.Save();
        wineWebView2SetupWindow.IsOpen = true;
    }

    internal void ShowFirstRunGuide()
    {
        firstRunGuideWindow.Show();
    }

    private void OnCommand(string command, string args) => ToggleMainUi();
    private void OnSettingsCommand(string command, string args) => ToggleConfigUi();

    private void OnDraw()
    {
        UpdateFirstRunGuide();
        windowSystem.Draw();
        renderer.DrawWorld();
        Configuration.ProcessPendingSave();
    }

    private void UpdateFirstRunGuide()
    {
        if (Configuration.FirstRunGuideCompleted)
            return;

        if (firstRunGuideWindow.WasShown)
        {
            if (!firstRunGuideWindow.IsOpen)
                firstRunGuideWindow.Complete();

            return;
        }

        if (FirstRunGuidePolicy.ShouldShow(
                Configuration.FirstRunGuideCompleted,
                Configuration.Enabled,
                wineWebView2SetupWindow?.IsOpen == true))
        {
            firstRunGuideWindow.Show();
        }
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        if (!PauseOwnedWorldScreensOnZoneChange())
            return;

        Configuration.Save();
        ipc.PublishLocalState();
    }

    private bool PauseOwnedWorldScreensOnZoneChange()
    {
        var changed = false;
        Configuration.Normalize();
        foreach (var screen in Configuration.BrowserScreens.Where(screen => !screen.CreatedByIpc && screen.Placement.Mode == ScreenPlacementMode.World))
        {
            if (screen.PlaybackPaused)
                continue;

            screen.PlaybackPaused = true;
            renderer.TryPauseDynamicSource(screen);
            changed = true;
        }

        return changed;
    }
}
