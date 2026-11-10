namespace CrystalCast;

public enum ScreenOutputMode
{
    /// <summary>
    /// Removed: ImGui doesn't layer screens correctly with the game. Kept so old configurations load; it is
    /// never used to draw.
    /// </summary>
    ImGuiOverlay = 0,
    NativeOverlay = 1,
    SceneComposite = 2,
}

internal static class ScreenOutputModePolicy
{
    private const int LegacySceneComposite = 3;

    public static ScreenOutputMode Normalize(ScreenOutputMode mode)
    {
        return (int)mode switch
        {
            // A saved ImGui overlay choice moves to the platform default.
            (int)ScreenOutputMode.ImGuiOverlay => Configuration.DefaultOutputMode,
            (int)ScreenOutputMode.NativeOverlay => ScreenOutputMode.NativeOverlay,
            (int)ScreenOutputMode.SceneComposite or LegacySceneComposite => ScreenOutputMode.SceneComposite,
            _ => Configuration.DefaultOutputMode,
        };
    }
}
