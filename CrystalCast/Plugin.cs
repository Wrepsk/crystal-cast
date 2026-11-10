using CrystalCast.Playlist;
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

    /// <summary>Downloads and deletes the WebView2 copy CrystalCast keeps for Wine.</summary>
    internal WebView2RuntimeDownloader WebView2Downloader { get; } = new(
        message => Log.Information("{WebView2Setup}", message),
        WineEnvironment.IsWine);
    private readonly FirstRunGuideWindow firstRunGuideWindow;
    private readonly GenericWebIpcApprovalWindow genericWebIpcApprovalWindow;
    private readonly GenericWebIpcApprovalService genericWebIpcApprovals;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateIpc ipc;
    private readonly ScreenPlaylistAdvancer playlistAdvancer;
    private readonly PlaylistMetadataService playlistMetadata;
    private readonly CrystalCastFonts fonts;

    public Plugin()
    {
        var webView2TrashStatus = WebView2FixedRuntime.EmptyTrash();
        if (webView2TrashStatus != null)
            Log.Information("{WebView2Setup}", webView2TrashStatus);

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
        genericWebIpcApprovals = new GenericWebIpcApprovalService();
        var services = new CrystalCastServices(
            PluginInterface,
            TextureProvider,
            ClientState,
            ObjectTable,
            Framework,
            GameGui,
            Log,
            placementResolver,
            new BrowserFrameSourceFactory(Log),
            genericWebIpcApprovals);
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.AttachPersistence(configuration => PluginInterface.SavePluginConfig(configuration));
        if (Configuration.Normalize())
            Configuration.Save();

        fonts = new CrystalCastFonts(PluginInterface);
        CrystalCastUiTheme.Fonts = fonts;

        renderer = new WorldScreenManager(Configuration, services);
        playlistAdvancer = new ScreenPlaylistAdvancer(renderer.GetPlaybackTelemetry);
        playlistMetadata = new PlaylistMetadataService(TextureProvider, Log, renderer.GetPlaybackTelemetry);
        ipc = new ScreenStateIpc(Configuration, renderer, services, playlistAdvancer);
        mainWindow = new MainWindow(this, renderer, ipc, placementResolver, playlistAdvancer, playlistMetadata);
        configWindow = new ConfigWindow(this, renderer, ipc);
#if DEBUG
        wineWebView2SetupWindow = new WineWebView2SetupWindow(this);
#else
        wineWebView2SetupWindow = WineEnvironment.IsWine ? new WineWebView2SetupWindow(this) : null;
#endif
        firstRunGuideWindow = new FirstRunGuideWindow(this);
        genericWebIpcApprovalWindow = new GenericWebIpcApprovalWindow(genericWebIpcApprovals);
        if (!ClientRuntimePolicy.CanStart(ClientState.IsLoggedIn) && wineWebView2SetupWindow != null)
            wineWebView2SetupWindow.IsOpen = false;
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        if (wineWebView2SetupWindow != null)
            windowSystem.AddWindow(wineWebView2SetupWindow);
        windowSystem.AddWindow(firstRunGuideWindow);
        windowSystem.AddWindow(genericWebIpcApprovalWindow);

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
        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        Log.Information("CrystalCast loaded.");
    }

    public Configuration Configuration { get; }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(SettingsCommandName);
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();
        wineWebView2SetupWindow?.Dispose();
        WebView2Downloader.Dispose();
        firstRunGuideWindow.Dispose();
        genericWebIpcApprovalWindow.Dispose();
        ipc.Dispose();
        playlistMetadata.Dispose();
        renderer.Dispose();
        EndLeftoverBrowsers();
        genericWebIpcApprovals.Dispose();
        CrystalCastUiTheme.Fonts = null;
        fonts.Dispose();
        Configuration.FlushPendingSave();
    }

    /// <summary>
    /// Screens close their browsers as they shut down. Any CrystalCast browser still running after a short wait
    /// was missed, and would otherwise keep playing with the plugin off.
    /// </summary>
    private static void EndLeftoverBrowsers()
    {
        try
        {
            var ended = WebView2ProcessTracker.EndRemaining(TimeSpan.FromSeconds(1.5));
            if (ended > 0)
                Log.Information("Ended {Count} CrystalCast WebView2 browser process(es) still running at unload.", ended);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not check for leftover CrystalCast WebView2 browser processes.");
        }
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

    /// <summary>Closes every browser screen, then deletes the downloaded WebView2 in the background.</summary>
    internal void RemoveDownloadedWebView2()
    {
        WebView2Downloader.StartRemoval(renderer.ReleaseAllBrowserRuntimes);
    }

    internal void ShowFirstRunGuide()
    {
        firstRunGuideWindow.Show();
    }

    internal void ResetIpcWebDomainMemory()
    {
        genericWebIpcApprovals.ResetSessionDecisions();
    }

    private void OnCommand(string command, string args) => ToggleMainUi();
    private void OnSettingsCommand(string command, string args) => ToggleConfigUi();

    private void OnDraw()
    {
        if (ClientRuntimePolicy.CanStart(ClientState.IsLoggedIn))
        {
            UpdateFirstRunGuide();
            genericWebIpcApprovalWindow.RefreshState();
            AdvancePlaylists();
        }
        windowSystem.Draw();
        renderer.DrawWorld();
        Configuration.ProcessPendingSave();
    }

    /// <summary>Moves screens whose video just ended to their next playlist video, and fills in video details.</summary>
    private void AdvancePlaylists()
    {
        try
        {
            var advanced = playlistAdvancer.Update(Configuration);
            if (advanced.Count > 0)
            {
                Configuration.Save();
                foreach (var advance in advanced)
                    ipc.PublishPlaylistAdvance(advance);
            }

            var described = playlistMetadata.Update(Configuration);
            if (described.Count > 0)
            {
                Configuration.Save();
                foreach (var screenId in described)
                    ipc.PublishPlaylistChange(screenId);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Moving a CrystalCast screen to its next playlist video failed.");
        }
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

    private void OnLogin()
    {
        if (wineWebView2SetupWindow != null
            && WineWebView2SetupWindow.ShouldOpenAutomatically(Configuration))
        {
            wineWebView2SetupWindow.IsOpen = true;
        }
    }

    private void OnLogout(int type, int code)
    {
        renderer.SuspendForLogout();
        wineWebView2SetupWindow?.IsOpen = false;
        firstRunGuideWindow.Suspend();
        genericWebIpcApprovalWindow.Suspend();
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
