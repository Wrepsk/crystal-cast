using CrystalCast.Sync;

namespace CrystalCast.Tests;

public sealed class RemoteScreenStateStoreTests
{
    [Fact]
    public void SameScreenIdFromDifferentSessionsDoesNotCollide()
    {
        long now = 1_000_000;
        var store = new RemoteScreenStateStore(() => now);

        Assert.Equal(RemoteScreenApplyResult.Applied, store.Apply(CreateState("owner-a", "screen", 1, now), "local", out _));
        Assert.Equal(RemoteScreenApplyResult.Applied, store.Apply(CreateState("owner-b", "screen", 1, now), "local", out _));

        Assert.Equal(2, store.GetSnapshot().Count);
    }

    [Fact]
    public void DuplicateCheckIsScopedToOwnerAndScreen()
    {
        long now = 1_000_000;
        var store = new RemoteScreenStateStore(() => now);
        store.Apply(CreateState("owner", "screen", 10, now), "local", out _);

        var result = store.Apply(CreateState("owner", "screen", 10, now), "local", out _);

        Assert.Equal(RemoteScreenApplyResult.IgnoredDuplicate, result);
        Assert.Single(store.GetSnapshot());
    }

    [Fact]
    public void LowerSequenceIsStale()
    {
        long now = 1_000_000;
        var store = new RemoteScreenStateStore(() => now);
        store.Apply(CreateState("owner", "screen", 10, now), "local", out _);

        Assert.Equal(RemoteScreenApplyResult.IgnoredStale, store.Apply(CreateState("owner", "screen", 9, now), "local", out _));
    }

    [Fact]
    public void RejectsNewScreensAtCapacityButStillAllowsExistingScreenUpdates()
    {
        long now = 1_000_000;
        var store = new RemoteScreenStateStore(() => now);
        for (var i = 0; i < RemoteScreenStateStore.MaxRemoteScreens; i++)
            Assert.Equal(RemoteScreenApplyResult.Applied, store.Apply(CreateState("owner", $"screen-{i}", 1, now), "local", out _));

        Assert.Equal(RemoteScreenApplyResult.RejectedCapacity, store.Apply(CreateState("owner", "overflow", 1, now), "local", out var error));
        Assert.NotEmpty(error);
        Assert.Equal(RemoteScreenApplyResult.Applied, store.Apply(CreateState("owner", "screen-0", 2, now), "local", out _));
    }

    [Fact]
    public void ExpiresScreensByLocalReceiptTime()
    {
        long now = 1_000_000;
        var store = new RemoteScreenStateStore(() => now);
        store.Apply(CreateState("owner", "screen", 1, now), "local", out _);

        now += RemoteScreenStateStore.RemoteScreenTtlMs;

        Assert.Empty(store.GetSnapshot());
    }

    [Fact]
    public void RejectsInvalidStateBeforeStoringIt()
    {
        long now = 1_000_000;
        var store = new RemoteScreenStateStore(() => now);
        var state = CreateState("owner", "screen", 1, now);
        state.Source.Url = "file:///local/video.mp4";

        Assert.Equal(RemoteScreenApplyResult.RejectedInvalid, store.Apply(state, "local", out var error));
        Assert.NotEmpty(error);
        Assert.Empty(store.GetSnapshot());
    }

    [Fact]
    public void IgnoresStateFromCurrentRuntimeSession()
    {
        long now = 1_000_000;
        var store = new RemoteScreenStateStore(() => now);

        Assert.Equal(RemoteScreenApplyResult.IgnoredSelf, store.Apply(CreateState("local", "screen", 1, now), "local", out _));
        Assert.Empty(store.GetSnapshot());
    }

    internal static ScreenStateEnvelope CreateState(string ownerSessionId, string screenId, long sequence, long nowUnixMs)
    {
        return new ScreenStateEnvelope
        {
            ScreenId = screenId,
            OwnerSessionId = ownerSessionId,
            Sequence = sequence,
            TimestampUnixMs = nowUnixMs,
            Rotation = QuaternionDto.Identity,
            SizeMeters = new Vector2Dto(3.0f, 1.7f),
            Source = new ScreenSourceState
            {
                Kind = ScreenSourceKind.Browser,
                Provider = BrowserSourceProviderKind.YouTube.ToString(),
                Url = "https://www.youtube.com/watch?v=abc123",
            },
            Playback = new ScreenPlaybackStateDto
            {
                Rate = 1.0f,
                HostTimestampUnixMs = nowUnixMs,
            },
            Visual = new ScreenVisualState
            {
                FadeStartMeters = 35.0f,
                FadeStopMeters = 60.0f,
            },
        };
    }
}
