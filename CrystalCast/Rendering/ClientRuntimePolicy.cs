namespace CrystalCast.Rendering;

internal static class ClientRuntimePolicy
{
    public static bool CanStart(bool isLoggedIn)
    {
        return isLoggedIn;
    }
}
