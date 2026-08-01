namespace CrystalCast.Video;

internal static class BrowserCandidateLifetime
{
    public static bool TryUse(IVideoFrameSource candidate, Action action, out Exception? error)
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
                Plugin.Log.Debug(disposeException, "Failed to dispose rejected CrystalCast browser candidate.");
            }

            return false;
        }
    }
}
