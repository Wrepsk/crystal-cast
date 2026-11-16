using CrystalCast.Browser;
using CrystalCast.Sources;
using CrystalCast.Video;

namespace CrystalCast.Tests.Video;

public sealed class SignInReloadTests
{
    [Fact]
    public void ForgetsTheOldPagesReportWhenThePlayerReloads()
    {
        BrowserSourceDescriptors.YouTube.TryParse("https://www.youtube.com/watch?v=dQw4w9WgXcQ", out var source);
        var state = new BrowserPlayerState(BrowserSourceDescriptors.YouTube, source, 1.0f);
        state.UpdateTelemetry(ScreenPlaybackState.Playing, 90_000, 200_000, 1.0f, "A video");
        Assert.True(PlaybackResumePolicy.IsSettled(state.Telemetry));
        Assert.NotNull(PlaybackResumePolicy.Capture("https://www.youtube.com/watch?v=dQw4w9WgXcQ", state.Telemetry));

        state.ResetForReload();

        // Until the reloaded player reports, nothing may take this for its position, so the resume waits.
        Assert.False(PlaybackResumePolicy.IsSettled(state.Telemetry));
        Assert.Equal(0, state.Telemetry.PositionMs);
        Assert.NotEqual(ScreenPlaybackState.Playing, state.Telemetry.State);
    }
}
