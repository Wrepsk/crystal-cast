using CrystalCast.Rooms;
using CrystalCast.Windows.Panels;

namespace CrystalCast.Tests.Rooms;

public sealed class RoomPlaylistSyncTests
{
    private static BrowserScreenProfile HostScreen()
    {
        var screen = new BrowserScreenProfile { PlaylistShuffle = true, PlaylistAllowRoomAdds = true };
        screen.Playlist.Add(new PlaylistEntry
        {
            EntryId = "one",
            ProviderKind = BrowserSourceProviderKind.YouTube,
            Url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ",
            MetadataTitle = "First video",
            Author = "Channel",
            ThumbnailUrl = "https://i.ytimg.com/vi/abc/hqdefault.jpg",
            DurationSeconds = 212,
        });
        screen.Playlist.Add(new PlaylistEntry
        {
            EntryId = "two",
            ProviderKind = BrowserSourceProviderKind.Vimeo,
            Url = "https://vimeo.com/76979871",
            Played = true,
        });
        screen.PlaylistCurrentEntryId = "two";
        return screen;
    }

    private static BrowserScreenProfile JoinedScreen() => new()
    {
        CreatedByIpc = true,
        IpcOwnerId = RoomViewerPlan.OwnerId,
        SourceControlsLocked = true,
        SourceControlsOwnerId = "Room ABCD-2345",
    };

    [Fact]
    public void ViewersGetACopyOfTheHostsPlaylist()
    {
        var joined = JoinedScreen();
        Assert.True(RoomPlaylistSync.Mirror(joined, RoomPlaylistSync.BuildState(HostScreen())));

        Assert.Equal(["one", "two"], joined.Playlist.Select(entry => entry.EntryId));
        Assert.Equal(BrowserSourceProviderKind.YouTube, joined.Playlist[0].ProviderKind);
        Assert.Equal(BrowserSourceProviderKind.Vimeo, joined.Playlist[1].ProviderKind);
        Assert.True(joined.Playlist[1].Played);
        Assert.Equal("two", joined.PlaylistCurrentEntryId);
        Assert.True(joined.PlaylistShuffle);
        Assert.True(joined.PlaylistAllowUserAdds);

        // Only links travel; the viewer looks up titles and thumbnails itself.
        Assert.All(joined.Playlist, entry => Assert.False(entry.MetadataLookedUp));
        Assert.Equal(string.Empty, joined.Playlist[0].MetadataTitle);
    }

    [Fact]
    public void DetailsAViewerLookedUpAreKept()
    {
        var joined = JoinedScreen();
        var host = HostScreen();
        RoomPlaylistSync.Mirror(joined, RoomPlaylistSync.BuildState(host));
        joined.Playlist[0].MetadataTitle = "First video";
        joined.Playlist[0].MetadataLookedUp = true;

        host.Playlist[0].Played = true;
        Assert.True(RoomPlaylistSync.Mirror(joined, RoomPlaylistSync.BuildState(host)));

        Assert.Equal("First video", joined.Playlist[0].MetadataTitle);
        Assert.True(joined.Playlist[0].MetadataLookedUp);
        Assert.True(joined.Playlist[0].Played);
    }

    [Fact]
    public void AFullPlaylistFitsInOneRelayMessage()
    {
        var host = new BrowserScreenProfile();
        for (var i = 0; i < CrystalCast.Playlist.ScreenPlaylistPolicy.MaxEntries; i++)
        {
            host.Playlist.Add(new PlaylistEntry
            {
                ProviderKind = BrowserSourceProviderKind.YouTube,
                Url = $"https://www.youtube.com/watch?v=aqz-KE-bp{i:00}&list=PL590L5WQmH8fJ54F369BLDSqIwcs-TCfs",
                MetadataTitle = new string('x', 300),
            });
        }

        var state = new RoomScreenState { Playlist = RoomPlaylistSync.BuildState(host) };
        var json = CrystalCast.Ipc.IpcJsonService.Serialize(new { type = "state", change = true, state });

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) < 32 * 1024, $"{json.Length} bytes");
    }

    [Fact]
    public void MirroringTheSamePlaylistAgainChangesNothing()
    {
        var joined = JoinedScreen();
        var state = RoomPlaylistSync.BuildState(HostScreen());
        RoomPlaylistSync.Mirror(joined, state);

        Assert.False(RoomPlaylistSync.Mirror(joined, state));
    }

    [Fact]
    public void TheHostsSwitchControlsAdding()
    {
        var host = HostScreen();
        Assert.True(RoomPlaylistSync.TryAcceptAdd(host, "https://www.youtube.com/watch?v=aqz-KE-bpKQ", out var error), error);
        Assert.Equal(3, host.Playlist.Count);
        Assert.Equal(RoomPlaylistSync.AddedBy, host.Playlist[^1].AddedBy);

        host.PlaylistAllowRoomAdds = false;
        Assert.False(RoomPlaylistSync.TryAcceptAdd(host, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", out _));
        Assert.Equal(3, host.Playlist.Count);
    }

    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("not a link")]
    [InlineData("abc123")]
    public void OnlyVideoSiteLinksAreTakenFromTheRoom(string link)
    {
        var host = HostScreen();
        Assert.False(RoomPlaylistSync.TryAcceptAdd(host, link, out _));
        Assert.Equal(2, host.Playlist.Count);
    }

    [Fact]
    public void PlaylistChangesAreSentRightAway()
    {
        var policy = new RoomHostSendPolicy();
        var host = HostScreen();
        var state = new RoomScreenState { Location = new RoomLocation { WorldId = 1, TerritoryId = 2 }, Playlist = RoomPlaylistSync.BuildState(host) };
        policy.Decide(state, 1_000);

        RoomPlaylistSync.TryAcceptAdd(host, "https://www.youtube.com/watch?v=aqz-KE-bpKQ", out _);
        state.Playlist = RoomPlaylistSync.BuildState(host);

        Assert.Equal((true, true), policy.Decide(state, 1_250));
    }

    [Fact]
    public void AJoinedScreenNamesTheRoomAndOpensAddingWhenTheHostAllows()
    {
        var joined = JoinedScreen();
        var closed = PlaylistPanel.DescribeBlocks(joined);
        Assert.Contains("Room ABCD-2345", closed.Add, StringComparison.Ordinal);
        Assert.Contains("Room ABCD-2345", closed.Controls, StringComparison.Ordinal);

        joined.PlaylistAllowUserAdds = true;
        var open = PlaylistPanel.DescribeBlocks(joined);
        Assert.Null(open.Add);
        Assert.NotNull(open.Controls);
        Assert.NotNull(open.Remove);
    }
}
