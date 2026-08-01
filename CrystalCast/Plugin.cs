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
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("CrystalCast");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
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
            GameGui,
            Log,
            placementResolver,
            new BrowserFrameSourceFactory(Log, PluginInterface.AssemblyLocation.Directory?.FullName));
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.AttachPersistence(configuration => PluginInterface.SavePluginConfig(configuration));
        if (Configuration.Normalize())
            Configuration.Save();

        renderer = new WorldScreenManager(Configuration, services);
        ipc = new ScreenStateIpc(Configuration, renderer, services);
        mainWindow = new MainWindow(this, renderer, ipc, placementResolver);
        configWindow = new ConfigWindow(this, renderer, ipc);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

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
        ipc.Dispose();
        renderer.Dispose();
        Configuration.FlushPendingSave();
    }

    public void ToggleMainUi() => mainWindow.Toggle();
    public void ToggleConfigUi() => configWindow.Toggle();

    private void OnCommand(string command, string args) => ToggleMainUi();
    private void OnSettingsCommand(string command, string args) => ToggleConfigUi();

    private void OnDraw()
    {
        windowSystem.Draw();
        renderer.DrawWorld();
        Configuration.ProcessPendingSave();
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
