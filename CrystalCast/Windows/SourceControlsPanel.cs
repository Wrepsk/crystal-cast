using CrystalCast.Rendering;

namespace CrystalCast.Windows;

internal sealed class SourceControlsPanel
{
    private readonly LocalVideoSourceControlsPanel localVideoPanel = new();
    private readonly Dictionary<BrowserSourceProviderKind, IBrowserSourceControlsPanel> browserPanels;

    public SourceControlsPanel(WorldScreenManager renderer)
    {
        IBrowserSourceControlsPanel[] panels =
        [
            new YouTubeSourceControlsPanel(renderer),
            new TwitchSourceControlsPanel(renderer),
            new DailymotionSourceControlsPanel(renderer),
        ];

        browserPanels = panels.ToDictionary(panel => panel.ProviderKind);
    }

    public bool Draw(Configuration config, BrowserScreenProfile activeScreen)
    {
        return config.SourceKind switch
        {
            ScreenSourceKind.LocalVideo => localVideoPanel.Draw(config),
            ScreenSourceKind.YouTubeBrowser => DrawBrowserSource(activeScreen),
            _ => false,
        };
    }

    public void ClearScreen(string screenId)
    {
        foreach (var panel in browserPanels.Values)
            panel.ClearScreen(screenId);
    }

    private bool DrawBrowserSource(BrowserScreenProfile screen)
    {
        if (!browserPanels.TryGetValue(screen.ProviderKind, out var panel))
            panel = browserPanels[BrowserSourceProviderKind.YouTube];

        return panel.Draw(screen);
    }
}
