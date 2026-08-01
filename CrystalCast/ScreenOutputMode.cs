namespace CrystalCast;

public enum ScreenOutputMode
{
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
            (int)ScreenOutputMode.ImGuiOverlay => ScreenOutputMode.ImGuiOverlay,
            (int)ScreenOutputMode.NativeOverlay => ScreenOutputMode.NativeOverlay,
            (int)ScreenOutputMode.SceneComposite or LegacySceneComposite => ScreenOutputMode.SceneComposite,
            _ => Configuration.DefaultOutputMode,
        };
    }
}
