namespace CrystalCast.Video;

internal enum CaptureFrameAction
{
    DrainPaused,
    DrainEmpty,
    Recreate,
    Publish,
    DrainFpsLimited,
}

internal static class CaptureFrameCompletion
{
    public static void Complete(
        CaptureFrameAction action,
        Action publish,
        Action close,
        Action recreate)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(recreate);

        try
        {
            if (action == CaptureFrameAction.Publish)
                publish();
        }
        finally
        {
            close();
        }

        if (action == CaptureFrameAction.Recreate)
            recreate();
    }
}

internal static class CaptureFrameProcessingPolicy
{
    public static CaptureFrameAction Decide(
        bool captureEnabled,
        int currentWidth,
        int currentHeight,
        int frameWidth,
        int frameHeight)
    {
        if (!captureEnabled)
            return CaptureFrameAction.DrainPaused;
        if (frameWidth <= 0 || frameHeight <= 0)
            return CaptureFrameAction.DrainEmpty;
        if (frameWidth != currentWidth || frameHeight != currentHeight)
            return CaptureFrameAction.Recreate;

        return CaptureFrameAction.Publish;
    }
}
