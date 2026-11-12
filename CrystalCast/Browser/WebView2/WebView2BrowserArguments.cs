namespace CrystalCast.Browser.WebView2;

/// <summary>Chromium switches for every CrystalCast WebView2 environment.</summary>
internal static class WebView2BrowserArguments
{
    /// <summary>
    /// CrystalCast keeps its WebView2 windows behind the game. Chromium would otherwise treat
    /// them as covered background windows and slow down page rendering, timers and video,
    /// which shows up as captures below the video's frame rate. On laptops with two GPUs,
    /// Windows may also run the browser on the integrated GPU while the game and capture use
    /// the discrete one; the browser is asked to use the high-performance GPU instead.
    /// </summary>
    public const string KeepRenderingWhileHidden =
        "--force_high_performance_gpu "
        + "--disable-backgrounding-occluded-windows "
        + "--disable-renderer-backgrounding "
        + "--disable-background-timer-throttling "
        + "--disable-features=CalculateNativeWinOcclusion";

    public static string Combine(string? providerArguments)
    {
        return string.IsNullOrWhiteSpace(providerArguments)
            ? KeepRenderingWhileHidden
            : $"{providerArguments.Trim()} {KeepRenderingWhileHidden}";
    }
}
