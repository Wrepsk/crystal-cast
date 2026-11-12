using System.Diagnostics;
using CrystalCast.Browser.WebView2;

namespace CrystalCast.Tests;

public sealed class WebView2ProcessTrackerTests
{
    [Fact]
    public void NeverEndsAProcessThatIsNotAWebView2Browser()
    {
        using var self = Process.GetCurrentProcess();
        WebView2ProcessTracker.Track(self.Id);

        Assert.Equal(0, WebView2ProcessTracker.EndRemaining(TimeSpan.Zero));
        Assert.False(self.HasExited);
    }

    [Fact]
    public void ARestartedProcessWithTheSameIdIsNotTheTrackedOne()
    {
        using var self = Process.GetCurrentProcess();

        Assert.False(WebView2ProcessTracker.IsSameProcess(self, self.StartTime.AddSeconds(-5)));
    }

    [Fact]
    public void InvalidIdsAreIgnored()
    {
        WebView2ProcessTracker.Track(0);
        WebView2ProcessTracker.Track(-1);

        Assert.Equal(0, WebView2ProcessTracker.EndRemaining(TimeSpan.Zero));
    }
}
