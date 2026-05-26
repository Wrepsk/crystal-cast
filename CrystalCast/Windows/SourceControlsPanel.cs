using CrystalCast.Rendering;

namespace CrystalCast.Windows;

internal sealed class SourceControlsPanel
{
    private readonly LocalVideoSourceControlsPanel localVideoPanel = new();
    private readonly BrowserSourceControlsPanel browserPanel;

    public SourceControlsPanel(WorldScreenManager renderer)
    {
        browserPanel = new BrowserSourceControlsPanel(renderer);
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
        browserPanel.ClearScreen(screenId);
    }

    private bool DrawBrowserSource(BrowserScreenProfile screen)
    {
        return browserPanel.Draw(screen);
    }
}
