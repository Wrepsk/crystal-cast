using CrystalCast.Video;
using Xunit;

namespace CrystalCast.Tests;

public sealed class WebView2ScreencastPolicyTests
{
    [Fact]
    public void DefaultCapsFramesAt1080p()
    {
        var settings = WebView2ScreencastPolicy.Resolve(3840, 2160, JpegCaptureResolution.Full1080p, overrideValue: null);

        Assert.True(settings.Enabled);
        Assert.Equal(1920, settings.MaxWidth);
        Assert.Equal(1080, settings.MaxHeight);
        Assert.Equal(WebView2ScreencastPolicy.Quality, settings.Quality);
    }

    [Fact]
    public void ReducedSettingCapsFramesAt720p()
    {
        var settings = WebView2ScreencastPolicy.Resolve(1920, 1080, JpegCaptureResolution.Reduced720p, overrideValue: null);

        Assert.True(settings.Enabled);
        Assert.Equal(1280, settings.MaxWidth);
        Assert.Equal(720, settings.MaxHeight);
        Assert.Contains("720p setting", settings.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SmallerScreensKeepTheirOwnSize()
    {
        var settings = WebView2ScreencastPolicy.Resolve(960, 540, JpegCaptureResolution.Full1080p, overrideValue: null);

        Assert.Equal(960, settings.MaxWidth);
        Assert.Equal(540, settings.MaxHeight);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData(" OFF ")]
    public void OverrideCanTurnScreencastOff(string value)
    {
        var settings = WebView2ScreencastPolicy.Resolve(1920, 1080, JpegCaptureResolution.Full1080p, value);

        Assert.False(settings.Enabled);
        Assert.Contains(WebView2ScreencastPolicy.OverrideVariable, settings.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1080", JpegCaptureResolution.Reduced720p, 1080)]
    [InlineData("720", JpegCaptureResolution.Full1080p, 720)]
    public void OverrideWinsOverTheSetting(string value, JpegCaptureResolution setting, int expectedHeight)
    {
        var settings = WebView2ScreencastPolicy.Resolve(1920, 1080, setting, value);

        Assert.Equal(expectedHeight, settings.MaxHeight);
    }

    [Fact]
    public void UnknownOverrideUsesTheSetting()
    {
        var settings = WebView2ScreencastPolicy.Resolve(1920, 1080, JpegCaptureResolution.Full1080p, "banana");

        Assert.True(settings.Enabled);
        Assert.Equal(1920, settings.MaxWidth);
    }

    [Fact]
    public void UnknownSavedResolutionNormalizesTo1080p()
    {
        Assert.Equal(JpegCaptureResolution.Full1080p, BrowserMediaEnginePolicy.Normalize((JpegCaptureResolution)42));
        Assert.Equal(JpegCaptureResolution.Reduced720p, BrowserMediaEnginePolicy.Normalize(JpegCaptureResolution.Reduced720p));
    }

    [Fact]
    public void ChangingTheResolutionRecreatesTheSource()
    {
        var screen = new BrowserScreenProfile();
        var full = BrowserSourceProviderRegistry.BuildFrameSourceSignature(screen, BrowserMediaEngine.Auto, JpegCaptureResolution.Full1080p);
        var reduced = BrowserSourceProviderRegistry.BuildFrameSourceSignature(screen, BrowserMediaEngine.Auto, JpegCaptureResolution.Reduced720p);

        Assert.NotEqual(full, reduced);
    }

    [Fact]
    public void DisplayRateTargetsKeepEveryUnevenFrame()
    {
        var pacer = new ScreencastFramePacer();
        double[] arrivals = [0, 10, 33, 40, 50, 66, 70, 100];

        Assert.All(arrivals, now => Assert.True(pacer.ShouldAccept(now, 60f)));
    }

    [Fact]
    public void ThirtyFpsTargetKeepsAboutHalfOfASixtyFpsPage()
    {
        var pacer = new ScreencastFramePacer();
        var random = new Random(7);
        var accepted = 0;
        for (var frame = 0; frame < 600; frame++)
        {
            var jitter = random.NextDouble() * 8 - 4;
            if (pacer.ShouldAccept(frame * 1000.0 / 60 + jitter, 30f))
                accepted++;
        }

        Assert.InRange(accepted, 285, 315);
    }

    [Fact]
    public void ThrottledPacerDoesNotBurstAfterAPause()
    {
        var pacer = new ScreencastFramePacer();
        Assert.True(pacer.ShouldAccept(0, 30f));
        Assert.True(pacer.ShouldAccept(2000, 30f));
        Assert.False(pacer.ShouldAccept(2005, 30f));
    }
}
