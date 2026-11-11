namespace CrystalCast.Rendering;

/// <summary>
/// Whether screens stay visible when the player hides the game UI. Dalamud stops a plugin's draw callback
/// while the UI is hidden, and that callback also drives the screens, so keeping them means asking Dalamud
/// to keep calling it and hiding only CrystalCast's own windows. Only scene composite can use this: the
/// native overlay is part of the game's UI and is hidden with it. Scene composite draws through the same
/// render hook under Wine, so this applies on Linux too.
/// </summary>
internal static class HiddenUiPolicy
{
    /// <summary>Whether CrystalCast should keep drawing screens while the game UI is hidden.</summary>
    public static bool KeepsScreens(Configuration configuration)
        => configuration.KeepScreensWhenUiHidden && configuration.OutputMode == ScreenOutputMode.SceneComposite;

    /// <summary>
    /// Whether CrystalCast's windows should draw this frame. They hide with the game UI only when CrystalCast
    /// asked Dalamud to keep drawing; otherwise Dalamud decides as usual (players can turn its hiding off).
    /// </summary>
    public static bool ShowsWindows(bool gameUiHidden, bool keepsScreens) => !(gameUiHidden && keepsScreens);
}
