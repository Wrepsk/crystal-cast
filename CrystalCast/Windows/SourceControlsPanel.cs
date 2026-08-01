using CrystalCast.Rendering;

namespace CrystalCast.Windows;

internal sealed class SourceControlsPanel
{
    private readonly BrowserSourceControlsPanel browserPanel;

    public SourceControlsPanel(WorldScreenManager renderer)
    {
        browserPanel = new BrowserSourceControlsPanel(renderer);
    }

    public bool Draw(BrowserScreenProfile activeScreen) => browserPanel.Draw(activeScreen);

    public void ClearScreen(string screenId)
    {
        browserPanel.ClearScreen(screenId);
    }
}
