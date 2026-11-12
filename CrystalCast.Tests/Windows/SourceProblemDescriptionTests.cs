using CrystalCast.Windows.Panels;
using CrystalCast.Windows.Style;

namespace CrystalCast.Tests;

public sealed class SourceProblemDescriptionTests
{
    [Fact]
    public void HealthyCaptureDiagnosticsAreNotAProblem()
    {
        const string status = "capture 30/60 fps; cadence 35,9ms avg, p95 63,5, max 102,4, late 151/240; 0,1 ms; 12 ms frame age; "
            + "WGC event; 1920x1080; paused False; events 3699; published 2535; drained 1164; FPS-limited 0; resizes 4; "
            + "mutex busy 1160; retired 0; callback errors 0; event age 0 ms; publish age 0 ms; last fatal none; "
            + "WGC rgb 64/64 y247, a64/64 avg255; FINAL FANTASY XIV";

        Assert.False(SourceControlUi.TryDescribeSourceProblem(status, out _, out _, out _));
    }

    [Fact]
    public void FailingSegmentBecomesTitleAndDetail()
    {
        const string status = "capture 0/60 fps; cadence pending; 0 ms; no captured frame; WebView2 init failed: Object reference not set to an instance of an object.; player idle";

        Assert.True(SourceControlUi.TryDescribeSourceProblem(status, out var kind, out var title, out var detail));
        Assert.Equal(BannerKind.Error, kind);
        Assert.Equal("WebView2 init failed", title);
        Assert.Equal("Object reference not set to an instance of an object.", detail);
    }

    [Fact]
    public void CaptureFallbackIsAWarning()
    {
        const string status = "capture 30/30 fps; WebView2 window capture unavailable, using JPEG fallback: host window was not created; player ready";

        Assert.True(SourceControlUi.TryDescribeSourceProblem(status, out var kind, out var title, out _));
        Assert.Equal(BannerKind.Warning, kind);
        Assert.Equal("WebView2 window capture unavailable, using JPEG fallback", title);
    }

    [Theory]
    [InlineData("last fatal none", false)]
    [InlineData("last fatal DXGI_ERROR_DEVICE_REMOVED", true)]
    [InlineData("callback errors 0", false)]
    [InlineData("native WebKitGTK browser unavailable: libwebkitgtk-6.0 not found", true)]
    [InlineData("12 ms frame age", false)]
    public void ClassifiesSegments(string segment, bool isProblem)
    {
        Assert.Equal(isProblem, SourceControlUi.ClassifyStatusSegment(segment) != null);
    }
}
