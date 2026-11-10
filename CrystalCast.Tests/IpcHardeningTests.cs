using CrystalCast.Sync;

namespace CrystalCast.Tests;

public sealed class IpcHardeningTests
{
    [Fact]
    public void IpcSettingAGenericWebAddressOnAUserScreenNeedsApproval()
    {
        var screen = new BrowserScreenProfile();
        Assert.False(screen.CreatedByIpc);
        var request = new ScreenIpcMutationRequest
        {
            Provider = BrowserSourceProviderKind.GenericWeb,
            GenericWeb = new GenericWebScreenPatchDto { Url = "https://example.com/page" },
        };

        Assert.True(ScreenPatchApplier.TryApplyScreenMutation(screen, request, out var updated, out var error), error);

        Assert.False(updated.CreatedByIpc);
        Assert.True(updated.GenericWebUrlFromIpc);
        Assert.True(updated.RequiresGenericWebIpcApproval);
    }

    [Fact]
    public void SourceOnlyIpcUpdateAlsoMarksTheAddress()
    {
        var screen = new BrowserScreenProfile();
        var request = new ScreenIpcSourceUpdateRequest
        {
            Provider = BrowserSourceProviderKind.GenericWeb,
            GenericWeb = new GenericWebScreenPatchDto { Url = "https://example.com/page" },
        };

        Assert.True(ScreenPatchApplier.TryApplySourceMutation(screen, request, out var updated, out var error), error);

        Assert.True(updated.RequiresGenericWebIpcApproval);
    }

    [Fact]
    public void UserScreensWithTheirOwnAddressDoNotNeedApproval()
    {
        var screen = new BrowserScreenProfile
        {
            ProviderKind = BrowserSourceProviderKind.GenericWeb,
            GenericWebUrl = "https://example.com/page",
        };

        Assert.False(screen.RequiresGenericWebIpcApproval);
        Assert.False(screen.Clone().RequiresGenericWebIpcApproval);
    }

    [Fact]
    public void TheMarkSurvivesCopies()
    {
        var screen = new BrowserScreenProfile
        {
            ProviderKind = BrowserSourceProviderKind.GenericWeb,
            GenericWebUrlFromIpc = true,
        };

        Assert.True(screen.Clone().RequiresGenericWebIpcApproval);
        Assert.True(screen.CloneAsNew("copy").RequiresGenericWebIpcApproval);
    }

    [Fact]
    public void OtherProvidersNeverNeedGenericWebApproval()
    {
        var screen = new BrowserScreenProfile
        {
            ProviderKind = BrowserSourceProviderKind.YouTube,
            CreatedByIpc = true,
            GenericWebUrlFromIpc = true,
        };

        Assert.False(screen.RequiresGenericWebIpcApproval);
    }

    [Fact]
    public void OversizedRequestsAreRejected()
    {
        Assert.True(IpcPayloadLimit.TryCheck(new string('a', ScreenStateIpc.MaxPayloadBytes), out _));
        Assert.False(IpcPayloadLimit.TryCheck(new string('a', ScreenStateIpc.MaxPayloadBytes + 1), out var error));
        Assert.Contains("at most", error, StringComparison.Ordinal);

        // Multi-byte characters count by their UTF-8 size.
        Assert.False(IpcPayloadLimit.TryCheck(new string('é', ScreenStateIpc.MaxPayloadBytes / 2 + 1), out _));
        Assert.True(IpcPayloadLimit.TryCheck(null, out _));
    }

    [Fact]
    public void LongScreenIdsAreRejected()
    {
        Assert.True(IpcPayloadLimit.TryCheckScreenId(Guid.NewGuid().ToString("N"), out _));
        Assert.False(IpcPayloadLimit.TryCheckScreenId(new string('a', IpcPayloadLimit.MaxScreenIdLength + 1), out _));
    }
}

public sealed class ScreenPlaybackWatcherTests
{
    private static ScreenStateEnvelope State(
        ScreenPlaybackState playbackState,
        long positionMs,
        long hostTimestampUnixMs,
        string title = "Video",
        float rate = 1.0f)
    {
        return new ScreenStateEnvelope
        {
            ScreenId = "screen",
            Source = new ScreenSourceState { Kind = ScreenSourceKind.Browser, Provider = "YouTube", Title = title },
            Playback = new ScreenPlaybackStateDto
            {
                State = playbackState,
                PositionMs = positionMs,
                DurationMs = 60_000,
                Rate = rate,
                HostTimestampUnixMs = hostTimestampUnixMs,
            },
        };
    }

    [Fact]
    public void FirstLookOnlyRecords()
    {
        var watcher = new ScreenPlaybackWatcher();
        Assert.Empty(watcher.Observe(State(ScreenPlaybackState.Playing, 0, 1000), 1000));
    }

    [Fact]
    public void NormalPlaybackIsNotAChange()
    {
        var watcher = new ScreenPlaybackWatcher();
        watcher.Observe(State(ScreenPlaybackState.Playing, 10_000, 1000), 1000);

        Assert.Empty(watcher.Observe(State(ScreenPlaybackState.Playing, 10_250, 1250), 1250));
        Assert.Empty(watcher.Observe(State(ScreenPlaybackState.Playing, 11_600, 2500), 2500));
    }

    [Fact]
    public void DoubleSpeedPlaybackIsNotAChange()
    {
        var watcher = new ScreenPlaybackWatcher();
        watcher.Observe(State(ScreenPlaybackState.Playing, 10_000, 1000, rate: 2.0f), 1000);

        Assert.Empty(watcher.Observe(State(ScreenPlaybackState.Playing, 14_000, 3000, rate: 2.0f), 3000));
    }

    [Fact]
    public void PausingIsAPlaybackChange()
    {
        var watcher = new ScreenPlaybackWatcher();
        watcher.Observe(State(ScreenPlaybackState.Playing, 10_000, 1000), 1000);

        Assert.Equal([ScreenIpcChangeKind.Playback], watcher.Observe(State(ScreenPlaybackState.Paused, 10_200, 1200), 1200));
    }

    [Theory]
    [InlineData(40_000)]
    [InlineData(2_000)]
    public void SeekingIsAPlaybackChange(long newPositionMs)
    {
        var watcher = new ScreenPlaybackWatcher();
        watcher.Observe(State(ScreenPlaybackState.Playing, 10_000, 1000), 1000);

        Assert.Equal([ScreenIpcChangeKind.Playback], watcher.Observe(State(ScreenPlaybackState.Playing, newPositionMs, 1250), 1250));
    }

    [Fact]
    public void SeekingWhilePausedIsAPlaybackChange()
    {
        var watcher = new ScreenPlaybackWatcher();
        watcher.Observe(State(ScreenPlaybackState.Paused, 10_000, 1000), 1000);

        Assert.Empty(watcher.Observe(State(ScreenPlaybackState.Paused, 10_000, 5000), 5000));
        Assert.Equal([ScreenIpcChangeKind.Playback], watcher.Observe(State(ScreenPlaybackState.Paused, 30_000, 5250), 5250));
    }

    [Fact]
    public void NewTitleIsASourceChange()
    {
        var watcher = new ScreenPlaybackWatcher();
        watcher.Observe(State(ScreenPlaybackState.Playing, 10_000, 1000), 1000);

        var changes = watcher.Observe(State(ScreenPlaybackState.Playing, 10_250, 1250, title: "Next video"), 1250);

        Assert.Equal([ScreenIpcChangeKind.Source], changes);
    }

    [Fact]
    public void ForgottenScreensStartFresh()
    {
        var watcher = new ScreenPlaybackWatcher();
        watcher.Observe(State(ScreenPlaybackState.Playing, 10_000, 1000), 1000);
        watcher.Retain(new HashSet<string>());

        Assert.Empty(watcher.Observe(State(ScreenPlaybackState.Paused, 50_000, 2000), 2000));
    }

    [Fact]
    public void ObservedChangesGoOnlyToAnnouncedScreens()
    {
        var configuration = new Configuration();
        var screen = Assert.IsType<BrowserScreenProfile>(configuration.GetActiveBrowserScreen());
        var messages = new List<string>();
        var publisher = new ScreenChangePublisher(configuration, "runtime-session", messages.Add);
        var state = new ScreenStateEnvelope
        {
            ScreenId = screen.ScreenId,
            Source = new ScreenSourceState { Kind = ScreenSourceKind.Browser },
        };

        Assert.False(publisher.SendObservedChanges(state, [ScreenIpcChangeKind.Playback]));
        Assert.Empty(messages);

        Assert.True(publisher.MaybeSendScreenChanged(state, null, null));
        Assert.True(publisher.SendObservedChanges(state, [ScreenIpcChangeKind.Playback]));
        Assert.Equal(2, messages.Count);
        Assert.Contains("\"changes\":[\"Playback\"]", messages[1], StringComparison.Ordinal);

        // The regular publish after it sees nothing new.
        Assert.False(publisher.MaybeSendScreenChanged(state, null, null));
    }
}
