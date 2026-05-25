namespace CrystalCast.Windows;

internal interface IBrowserSourceControlsPanel
{
    BrowserSourceProviderKind ProviderKind { get; }
    bool Draw(BrowserScreenProfile screen);
    void ClearScreen(string screenId);
}
