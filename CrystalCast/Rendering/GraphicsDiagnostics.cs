namespace CrystalCast.Rendering;

internal static class GraphicsDiagnostics
{
    private static int enabled;

    public static bool Enabled
    {
        get => Volatile.Read(ref enabled) != 0;
        set => Volatile.Write(ref enabled, value ? 1 : 0);
    }
}
