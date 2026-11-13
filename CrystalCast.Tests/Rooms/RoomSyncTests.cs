using CrystalCast.Ipc;
using CrystalCast.Rooms;

namespace CrystalCast.Tests.Rooms;

public sealed class RoomCodeTests
{
    [Theory]
    [InlineData("ABCD2345", "ABCD2345")]
    [InlineData("abcd-2345", "ABCD2345")]
    [InlineData(" ab cd 23 45 ", "ABCD2345")]
    public void TypedCodesAreNormalized(string input, string expected)
    {
        Assert.True(RoomCode.TryNormalize(input, out var code));
        Assert.Equal(expected, code);
        Assert.Equal("ABCD-2345", RoomCode.Format(code));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC2345")]
    [InlineData("ABCD23456")]
    [InlineData("ABCD234O")]
    [InlineData("ABCD2341")]
    [InlineData("ABCD234I")]
    public void BadCodesAreRejected(string input)
    {
        Assert.False(RoomCode.TryNormalize(input, out _));
    }
}

public sealed class RoomLocationTests
{
    private static readonly RoomLocation Venue = new()
    {
        WorldId = 73, TerritoryId = 341, Ward = 4, Plot = 12, Room = 0, Division = 1, Indoors = true,
    };

    [Fact]
    public void SamePlaceMatches()
    {
        Assert.True(Venue.SamePlaceAs(Venue with { }));
    }

    [Fact]
    public void AnotherWardOfTheSameDistrictDoesNotMatch()
    {
        Assert.False(Venue.SamePlaceAs(Venue with { Ward = 5 }));
        Assert.False(Venue.SamePlaceAs(Venue with { Plot = 13 }));
        Assert.False(Venue.SamePlaceAs(Venue with { WorldId = 74 }));
        Assert.False(Venue.SamePlaceAs(Venue with { Indoors = false }));
    }

    [Fact]
    public void OutdoorsTheWholeWardMatches()
    {
        var host = new RoomLocation { WorldId = 73, TerritoryId = 641, Ward = 4, Division = 1 };

        // Standing on another plot, or a host on an older version that still sends its plot.
        Assert.True((host with { Plot = 12 }).SamePlaceAs(host with { Plot = 3, Room = 0 }));

        Assert.False(host.SamePlaceAs(host with { Ward = 5 }));

        // The subdivision is a copy of the ward with the same coordinates.
        Assert.False(host.SamePlaceAs(host with { Division = 2 }));
    }

    [Fact]
    public void IndoorsTheHouseOrApartmentMustMatch()
    {
        var house = new RoomLocation { WorldId = 73, TerritoryId = 652, Ward = 4, Plot = 12, Room = 0, Division = 1, Indoors = true };

        Assert.True(house.SamePlaceAs(house with { }));
        Assert.False(house.SamePlaceAs(house with { Plot = 13 }));
        Assert.False(house.SamePlaceAs(house with { Room = 3 }));
        Assert.False(house.SamePlaceAs(house with { Ward = 5 }));
        Assert.False(house.SamePlaceAs(house with { Division = 2 }));
    }

    [Fact]
    public void InstancesOfAZoneAreDifferentPlaces()
    {
        var limsa = new RoomLocation { WorldId = 73, TerritoryId = 129, Instance = 1 };

        Assert.True(limsa.SamePlaceAs(limsa with { }));
        Assert.False(limsa.SamePlaceAs(limsa with { Instance = 2 }));
        Assert.False(limsa.SamePlaceAs(limsa with { TerritoryId = 132 }));
    }

    [Fact]
    public void UnknownLocationsNeverMatch()
    {
        Assert.False(new RoomLocation().SamePlaceAs(new RoomLocation()));
    }
}

public sealed class RoomHostSendPolicyTests
{
    private static RoomScreenState Playing(long positionMs) => new()
    {
        Name = "Stage",
        Url = "https://www.youtube.com/watch?v=abc",
        Provider = BrowserSourceProviderKind.YouTube,
        PositionMs = positionMs,
        Rate = 1.0f,
        Location = new RoomLocation { WorldId = 1, TerritoryId = 2 },
    };

    [Fact]
    public void FirstStateIsSentAsAChange()
    {
        var policy = new RoomHostSendPolicy();
        Assert.Equal((true, true), policy.Decide(Playing(0), 1_000));
    }

    [Fact]
    public void NormalPlaybackOnlySendsHeartbeats()
    {
        var policy = new RoomHostSendPolicy();
        policy.Decide(Playing(0), 1_000);

        Assert.Equal((false, false), policy.Decide(Playing(5_000), 6_000));
        Assert.Equal((true, false), policy.Decide(Playing(10_000), 11_000));
    }

    [Fact]
    public void PausingIsSentRightAway()
    {
        var policy = new RoomHostSendPolicy();
        policy.Decide(Playing(0), 1_000);

        var paused = Playing(500);
        paused.Paused = true;
        Assert.Equal((true, true), policy.Decide(paused, 1_500));
    }

    [Fact]
    public void SeeksAreSentRightAway()
    {
        var policy = new RoomHostSendPolicy();
        policy.Decide(Playing(0), 1_000);

        Assert.Equal((true, true), policy.Decide(Playing(60_000), 1_250));
    }

    [Fact]
    public void MovingTheScreenIsSentRightAway()
    {
        var policy = new RoomHostSendPolicy();
        policy.Decide(Playing(0), 1_000);

        var moved = Playing(250);
        moved.PositionX = 3.0f;
        Assert.Equal((true, true), policy.Decide(moved, 1_250));
    }
}

public sealed class RoomClockTests
{
    [Fact]
    public void FastestRoundTripSetsTheOffset()
    {
        var clock = new RoomClock();
        clock.AddPong(sentAtLocalMs: 1_000, relayTimeMs: 51_100, receivedAtLocalMs: 1_200);
        clock.AddPong(sentAtLocalMs: 2_000, relayTimeMs: 52_020, receivedAtLocalMs: 2_040);

        Assert.True(clock.IsSynced);
        Assert.Equal(50_000, clock.RelayNow(0));
    }

    [Fact]
    public void RelayTimesOnlyGuessUntilAPongArrives()
    {
        var clock = new RoomClock();
        clock.AddRelayTime(10_000, 1_000);
        Assert.Equal(9_000, clock.RelayNow(0));

        clock.AddPong(1_000, 5_010, 1_020);
        clock.AddRelayTime(99_999, 1_000);
        Assert.Equal(4_000, clock.RelayNow(0));
    }
}

public sealed class RoomHostPresenceTests
{
    [Fact]
    public void ViewersLeaveAfterTheHostIsGoneForThreeMinutes()
    {
        var presence = new RoomHostPresence();
        presence.Observe(hostConnected: false, nowMs: 0);

        Assert.False(presence.ShouldLeave(RoomHostPresence.LeaveAfterMs - 1));
        Assert.True(presence.ShouldLeave(RoomHostPresence.LeaveAfterMs));
    }

    [Fact]
    public void AHostWhoComesBackStopsTheClock()
    {
        var presence = new RoomHostPresence();
        presence.Observe(hostConnected: false, nowMs: 0);
        presence.Observe(hostConnected: true, nowMs: 60_000);
        presence.Observe(hostConnected: false, nowMs: 120_000);

        Assert.False(presence.ShouldLeave(RoomHostPresence.LeaveAfterMs + 60_000));
        Assert.True(presence.ShouldLeave(120_000 + RoomHostPresence.LeaveAfterMs));
    }

    [Fact]
    public void TheViewersOwnDisconnectDoesNotCount()
    {
        var presence = new RoomHostPresence();
        presence.Observe(hostConnected: false, nowMs: 0);
        presence.Reset();

        Assert.False(presence.ShouldLeave(RoomHostPresence.LeaveAfterMs * 2));

        presence.Observe(hostConnected: false, nowMs: 500_000);
        Assert.False(presence.ShouldLeave(500_000 + RoomHostPresence.LeaveAfterMs - 1));
    }

    [Fact]
    public void AnOnlineHostNeverTriggersALeave()
    {
        var presence = new RoomHostPresence();
        presence.Observe(hostConnected: true, nowMs: 0);

        Assert.False(presence.ShouldLeave(long.MaxValue / 2));
    }
}

public sealed class RoomViewerPlanTests
{
    private static readonly RoomLocation Venue = new() { WorldId = 73, TerritoryId = 341, Ward = 4, Plot = 12 };

    private static RoomScreenState State(long positionMs, bool paused = false) => new()
    {
        Name = "Stage",
        Location = Venue,
        Url = "https://www.youtube.com/watch?v=abc",
        Provider = BrowserSourceProviderKind.YouTube,
        PositionMs = positionMs,
        Paused = paused,
        Rate = 1.0f,
        WidthMeters = 4.0f,
        HeightMeters = 2.25f,
    };

    [Fact]
    public void ExpectedPositionMovesOnWhilePlaying()
    {
        Assert.Equal(13_000, RoomViewerPlan.ExpectedPositionMs(State(10_000), 1_000, 4_000));
        Assert.Equal(10_000, RoomViewerPlan.ExpectedPositionMs(State(10_000, paused: true), 1_000, 4_000));
    }

    [Fact]
    public void FirstApplyCreatesTheScreenAtTheHostsPosition()
    {
        var applied = new RoomViewerApplied();
        var request = RoomViewerPlan.Build("ABCD2345", State(30_000), 1_000, 1_000, Venue, null, 0, applied);

        Assert.Equal("room-ABCD2345", request.ScreenId);
        Assert.True(request.CreateIfMissing);
        Assert.True(request.Enabled);
        Assert.True(request.SourceControlsLocked);
        Assert.Equal(RoomViewerPlan.OwnerId, request.OwnerId);
        Assert.Equal("https://www.youtube.com/watch?v=abc", request.Source!.Url);
        Assert.Equal(30_000, request.Source.PositionMs);
        Assert.Equal(ScreenPlacementMode.World, request.Placement!.Mode);
    }

    [Fact]
    public void TheScreenStaysOffElsewhere()
    {
        var request = RoomViewerPlan.Build("ABCD2345", State(0), 0, 0, Venue with { Ward = 9 }, null, 0, new RoomViewerApplied());
        Assert.False(request.Enabled);
    }

    [Fact]
    public void AnUnchangedLinkIsNotReloaded()
    {
        var applied = new RoomViewerApplied();
        RoomViewerPlan.Build("ABCD2345", State(0), 0, 0, Venue, null, 0, applied);

        var request = RoomViewerPlan.Build("ABCD2345", State(1_000), 1_000, 1_000, Venue, 1_000, 10_000, applied);

        Assert.Null(request.Source!.Url);
        Assert.Null(request.Source.PositionMs);
    }

    [Fact]
    public void DriftBeyondTheToleranceSeeksOncePerCooldown()
    {
        var applied = new RoomViewerApplied();
        RoomViewerPlan.Build("ABCD2345", State(0), 0, 0, Venue, null, 0, applied);

        var first = RoomViewerPlan.Build("ABCD2345", State(20_000), 0, 0, Venue, 10_000, 10_000, applied);
        Assert.Equal(20_000, first.Source!.PositionMs);

        var again = RoomViewerPlan.Build("ABCD2345", State(20_000), 0, 0, Venue, 10_000, 12_000, applied);
        Assert.Null(again.Source!.PositionMs);
    }

    [Fact]
    public void PausingSeeksToTheHostsPausePoint()
    {
        var applied = new RoomViewerApplied();
        RoomViewerPlan.Build("ABCD2345", State(0), 0, 0, Venue, null, 0, applied);

        var request = RoomViewerPlan.Build("ABCD2345", State(42_000, paused: true), 0, 0, Venue, 41_900, 1_000, applied);

        Assert.True(request.Source!.PlaybackPaused);
        Assert.Equal(42_000, request.Source.PositionMs);
    }
}

public sealed class RoomRelayAddressTests
{
    [Theory]
    [InlineData("https://relay.example.workers.dev", "wss://relay.example.workers.dev/rooms/ABCD2345/socket?role=viewer")]
    [InlineData("http://127.0.0.1:8787/", "ws://127.0.0.1:8787/rooms/ABCD2345/socket?role=viewer")]
    [InlineData("https://example.com/relay/", "wss://example.com/relay/rooms/ABCD2345/socket?role=viewer")]
    public void ViewerSocketAddresses(string relay, string expected)
    {
        Assert.True(RoomRelayApi.TryParseRelayUri(relay, out var uri));
        Assert.Equal(expected, RoomRelayConnection.BuildSocketUri(uri, "ABCD2345", null).ToString());
    }

    [Fact]
    public void HostKeysAreEscaped()
    {
        Assert.True(RoomRelayApi.TryParseRelayUri("https://relay.example", out var uri));
        Assert.EndsWith("role=host&key=a%2Bb", RoomRelayConnection.BuildSocketUri(uri, "ABCD2345", "a+b").ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyAddressUsesTheCrystalCastRelay(string? configured)
    {
        Assert.True(RoomRelayApi.TryResolveRelay(configured, out var uri));
        Assert.Equal(new Uri(RoomRelayApi.DefaultRelayUrl), uri);
    }

    [Fact]
    public void AConfiguredAddressWins()
    {
        Assert.True(RoomRelayApi.TryResolveRelay("http://127.0.0.1:8787", out var uri));
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.False(RoomRelayApi.TryResolveRelay("not a url", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relay.example")]
    [InlineData("ftp://relay.example")]
    [InlineData("https://user:pass@relay.example")]
    public void BadRelayAddressesAreRejected(string relay)
    {
        Assert.False(RoomRelayApi.TryParseRelayUri(relay, out _));
    }

    [Fact]
    public void RelayMessagesParse()
    {
        var message = IpcJsonService.Deserialize<RoomRelayMessage>(
            "{\"type\":\"state\",\"receivedAt\":123,\"state\":{\"name\":\"Stage\",\"provider\":\"Twitch\",\"positionMs\":5,\"location\":{\"worldId\":73,\"territoryId\":341,\"ward\":4}}}");

        Assert.NotNull(message);
        Assert.Equal("state", message.Type);
        Assert.Equal(123, message.ReceivedAt);
        Assert.Equal(BrowserSourceProviderKind.Twitch, message.State!.Provider);
        Assert.Equal(4, message.State.Location.Ward);
        Assert.Equal(-1, message.State.Location.Plot);
    }
}
