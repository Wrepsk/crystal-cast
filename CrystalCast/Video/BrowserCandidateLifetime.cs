namespace CrystalCast.Video;

using Dalamud.Plugin.Services;

internal static class BrowserCandidateLifetime
{
    public static bool TryUse(
        IVideoFrameSource candidate,
        Action action,
        out Exception? error,
        IPluginLog? log = null)
    {
        try
        {
            action();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            try
            {
                candidate.Dispose();
            }
            catch (Exception disposeException)
            {
                log?.Debug(disposeException, "Failed to dispose rejected CrystalCast browser candidate.");
            }

            return false;
        }
    }
}
