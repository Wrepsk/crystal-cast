namespace CrystalCast.Video;

internal static class BrowserCaptureFailureDiagnostics
{
    public static string Format(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var root = exception.GetBaseException();
        return $"{root.GetType().Name} (0x{root.HResult:X8}): {root.Message}";
    }
}
