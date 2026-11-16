namespace CrystalCast.Windows.Panels;

/// <summary>
/// A tab that belongs to one kind of source, such as Twitch's chat. The main window shows it after the fixed
/// tabs while the selected screen plays a source it applies to.
/// </summary>
internal interface ISourceTab : IDisposable
{
    string Label { get; }

    bool AppliesTo(BrowserScreenProfile screen);

    /// <summary>True when the screen plays this tab's site; that tab comes first when several show.</summary>
    bool IsPlaying(BrowserScreenProfile screen);

    /// <summary>Called every frame the main window draws, whether or not this tab is shown.</summary>
    void Update(BrowserScreenProfile? activeScreen);

    /// <summary>Draws the tab. Returns true when the screen's settings changed.</summary>
    bool Draw(BrowserScreenProfile screen);
}
