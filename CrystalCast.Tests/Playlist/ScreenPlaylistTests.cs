using System.Reflection;
using System.Text.Json;
using CrystalCast.Browser;
using CrystalCast.Ipc;
using CrystalCast.Playlist;
using CrystalCast.Sources;
using CrystalCast.Video;
using Dalamud.Plugin.Services;

namespace CrystalCast.Tests;

public sealed class ScreenPlaylistPolicyTests
{
    private const string First = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private const string Second = "https://vimeo.com/76979871";
    private const string Third = "https://www.dailymotion.com/video/x8abcd1";

    [Fact]
    public void LinksBecomeEntriesForTheirSite()
    {
        Assert.True(ScreenPlaylistPolicy.TryCreateEntry(Second, BrowserSourceProviderKind.YouTube, false, "", null, out var entry, out var error), error);
        Assert.Equal(BrowserSourceProviderKind.Vimeo, entry.ProviderKind);
        Assert.Equal(Second, entry.Url);
        Assert.Equal(string.Empty, entry.Title);
        Assert.False(string.IsNullOrWhiteSpace(entry.SourceName));
        Assert.Equal(entry.SourceName, ScreenPlaylistPolicy.DisplayTitle(entry));
        Assert.False(entry.AddedByIpc);
    }

    [Fact]
    public void BareIdsAreRejectedWhenOnlyLinksAreAllowed()
    {
        Assert.False(ScreenPlaylistPolicy.TryCreateEntry("dQw4w9WgXcQ", BrowserSourceProviderKind.GenericWeb, true, "Sync", null, out _, out _));
        Assert.True(ScreenPlaylistPolicy.TryCreateEntry("dQw4w9WgXcQ", BrowserSourceProviderKind.YouTube, false, "", null, out _, out _));
    }

    [Fact]
    public void PlaysInListOrderThenStops()
    {
        var screen = ScreenWith(First, Second, Third);
        var random = new Random(1);

        Assert.Equal(First, PlayNext(screen, random));
        Assert.Equal(Second, PlayNext(screen, random));
        Assert.Equal(Third, PlayNext(screen, random));
        Assert.False(ScreenPlaylistPolicy.HasNext(screen));
        Assert.False(ScreenPlaylistPolicy.TryPickNext(screen, random, out _));
    }

    [Fact]
    public void AfterPlayNowItContinuesBelowThenWrapsToUnplayedEntries()
    {
        var screen = ScreenWith(First, Second, Third);
        ScreenPlaylistPolicy.Load(screen, screen.Playlist[1]);

        var random = new Random(1);
        Assert.Equal(Third, PlayNext(screen, random));
        Assert.Equal(First, PlayNext(screen, random));
        Assert.False(ScreenPlaylistPolicy.HasNext(screen));
    }

    [Fact]
    public void LoopPlaylistStartsOverFromTheTop()
    {
        var screen = ScreenWith(First, Second);
        screen.PlaylistLoop = true;
        var random = new Random(1);
        PlayNext(screen, random);
        PlayNext(screen, random);

        Assert.True(ScreenPlaylistPolicy.TryPickNext(screen, random, out var pick));
        Assert.True(pick.StartsOver);
        Assert.Equal(First, pick.Entry.Url);

        ScreenPlaylistPolicy.Load(screen, pick.Entry, pick.StartsOver);
        Assert.False(screen.Playlist[1].Played);
        Assert.True(screen.Playlist[0].Played);
    }

    [Fact]
    public void LoopingASingleVideoReplaysIt()
    {
        var screen = ScreenWith(First);
        screen.PlaylistLoop = true;
        var random = new Random(1);
        Assert.Equal(First, PlayNext(screen, random));
        Assert.Equal(First, PlayNext(screen, random));
    }

    [Fact]
    public void ShufflePlaysEveryVideoOnce()
    {
        var urls = Enumerable.Range(0, 10).Select(i => $"https://example.com/page{i}").ToArray();
        var screen = ScreenWith(urls);
        screen.PlaylistShuffle = true;
        var random = new Random(42);

        var played = new List<string>();
        while (ScreenPlaylistPolicy.TryPickNext(screen, random, out var pick))
        {
            ScreenPlaylistPolicy.Load(screen, pick.Entry, pick.StartsOver);
            played.Add(pick.Entry.Url);
        }

        Assert.Equal(urls.OrderBy(url => url), played.OrderBy(url => url));
    }

    [Fact]
    public void VideoLoopIsOffOnlyWhileAnotherVideoWaits()
    {
        var screen = ScreenWith(First, Second);
        screen.LoopYouTube = true;
        ScreenPlaylistPolicy.Load(screen, screen.Playlist[0]);
        Assert.False(BrowserSourceProviderRegistry.GetRuntimeSettings(screen).Loop);

        ScreenPlaylistPolicy.Remove(screen, screen.Playlist[1].EntryId);
        Assert.True(BrowserSourceProviderRegistry.GetRuntimeSettings(screen).Loop);
    }

    [Fact]
    public void LoadingAnotherLinkEndsThePlaylistsHoldOnTheScreen()
    {
        var screen = ScreenWith(First, Second);
        ScreenPlaylistPolicy.Load(screen, screen.Playlist[0]);
        Assert.False(ScreenPlaylistPolicy.Normalize(screen));

        screen.YouTubeUrl = "https://www.youtube.com/watch?v=aaaaaaaaaaa";
        Assert.True(ScreenPlaylistPolicy.Normalize(screen));
        Assert.Equal(string.Empty, screen.PlaylistCurrentEntryId);
    }

    [Fact]
    public void RemovingThePlayingEntryLeavesTheVideoPlaying()
    {
        var screen = ScreenWith(First, Second);
        ScreenPlaylistPolicy.Load(screen, screen.Playlist[0]);
        Assert.True(ScreenPlaylistPolicy.Remove(screen, screen.Playlist[0].EntryId));

        Assert.Equal(string.Empty, screen.PlaylistCurrentEntryId);
        Assert.Equal(First, screen.YouTubeUrl);
    }

    [Fact]
    public void WebPagesFromPluginsStillNeedApproval()
    {
        var screen = new BrowserScreenProfile();
        Assert.True(ScreenPlaylistPolicy.TryCreateEntry("https://example.com/page", BrowserSourceProviderKind.GenericWeb, true, "Sync", null, out var fromPlugin, out _));
        Assert.True(ScreenPlaylistPolicy.TryAdd(screen, fromPlugin, out _));
        ScreenPlaylistPolicy.Load(screen, fromPlugin);
        Assert.True(screen.RequiresGenericWebIpcApproval);

        Assert.True(ScreenPlaylistPolicy.TryCreateEntry("https://example.com/other", BrowserSourceProviderKind.GenericWeb, false, "", null, out var fromUser, out _));
        Assert.True(ScreenPlaylistPolicy.TryAdd(screen, fromUser, out _));
        ScreenPlaylistPolicy.Load(screen, fromUser);
        Assert.False(screen.RequiresGenericWebIpcApproval);
    }

    [Fact]
    public void ThePlaylistHasATotalLimit()
    {
        var screen = new BrowserScreenProfile();
        for (var i = 0; i < ScreenPlaylistPolicy.MaxEntries; i++)
            Assert.True(ScreenPlaylistPolicy.TryAdd(screen, UserEntry($"https://example.com/{i}"), out _));

        Assert.False(ScreenPlaylistPolicy.TryAdd(screen, UserEntry("https://example.com/more"), out _));
    }

    [Fact]
    public void ThePluginDecidesWhetherTheUserMayEditItsScreensPlaylist()
    {
        var pluginScreen = new BrowserScreenProfile { CreatedByIpc = true };
        Assert.False(ScreenPlaylistPolicy.AllowsUserAdds(pluginScreen));

        var request = new ScreenIpcMutationRequest { PlaylistAllowUserAdds = true };
        Assert.True(ScreenPatchApplier.TryApplyScreenMutation(pluginScreen, request, out var updated, out var error), error);
        Assert.True(updated.PlaylistAllowUserAdds);
        Assert.True(ScreenPlaylistPolicy.AllowsUserAdds(updated));
        Assert.True(updated.Clone().PlaylistAllowUserAdds);
        Assert.Contains(ScreenIpcChangeKind.Playlist, ScreenChangePublisher.GetMutationChangeKinds(request));

        // Leaving it out keeps it.
        Assert.True(ScreenPatchApplier.TryApplyScreenMutation(updated, new ScreenIpcMutationRequest(), out var unchanged, out _));
        Assert.True(unchanged.PlaylistAllowUserAdds);
    }

    [Fact]
    public void TheUsersOwnScreensIgnoreThePluginSetting()
    {
        var userScreen = new BrowserScreenProfile();
        Assert.True(ScreenPlaylistPolicy.AllowsUserAdds(userScreen));

        var request = new ScreenIpcMutationRequest { PlaylistAllowUserAdds = false };
        Assert.True(ScreenPatchApplier.TryApplyScreenMutation(userScreen, request, out var updated, out var error), error);
        Assert.True(ScreenPlaylistPolicy.AllowsUserAdds(updated));
    }

    [Fact]
    public void OnAPluginsScreenTheCreatorCanAddAndTheSwitchIsIgnored()
    {
        var screen = new BrowserScreenProfile { CreatedByIpc = true, IpcOwnerId = "Creator", PlaylistAllowIpcAdds = true };

        Assert.True(ScreenPlaylistPolicy.CanIpcAdd(screen, "Creator"));
        Assert.False(ScreenPlaylistPolicy.CanIpcAdd(screen, "Other"));
        Assert.False(ScreenPlaylistPolicy.CanIpcAdd(screen, ""));

        screen.SourceControlsLocked = true;
        screen.SourceControlsOwnerId = "Other";
        Assert.True(ScreenPlaylistPolicy.CanIpcAdd(screen, "Other"));
    }

    [Fact]
    public void PluginsNeedTheUsersSwitchOrTheLockToAdd()
    {
        var screen = new BrowserScreenProfile();
        Assert.False(ScreenPlaylistPolicy.CanIpcAdd(screen, "Sync"));

        screen.PlaylistAllowIpcAdds = true;
        Assert.True(ScreenPlaylistPolicy.CanIpcAdd(screen, "Sync"));

        screen.PlaylistAllowIpcAdds = false;
        screen.SourceControlsOwnerId = "Sync";
        Assert.False(ScreenPlaylistPolicy.CanIpcAdd(screen, "Sync"));

        screen.SourceControlsLocked = true;
        Assert.True(ScreenPlaylistPolicy.CanIpcAdd(screen, "Sync"));
        Assert.False(ScreenPlaylistPolicy.CanIpcAdd(screen, "Other"));
        Assert.False(ScreenPlaylistPolicy.CanIpcAdd(screen, ""));
    }

    [Fact]
    public void PluginsRemoveOnlyTheirOwnEntriesUnlessTheyHoldTheLock()
    {
        var screen = new BrowserScreenProfile();
        var mine = IpcEntry("https://example.com/mine", "A");
        var user = UserEntry("https://example.com/user");

        Assert.True(ScreenPlaylistPolicy.CanIpcRemove(screen, mine, "A"));
        Assert.False(ScreenPlaylistPolicy.CanIpcRemove(screen, mine, "B"));
        Assert.False(ScreenPlaylistPolicy.CanIpcRemove(screen, user, "A"));
        Assert.False(ScreenPlaylistPolicy.CanIpcRemove(screen, user, ""));

        screen.SourceControlsLocked = true;
        screen.SourceControlsOwnerId = "B";
        Assert.True(ScreenPlaylistPolicy.CanIpcRemove(screen, mine, "B"));
        Assert.True(ScreenPlaylistPolicy.CanIpcRemove(screen, user, "B"));
    }

    [Fact]
    public void IpcUpdatesKeepThePlaylistButCopiesDropThePermission()
    {
        var screen = ScreenWith(First, Second);
        screen.PlaylistAllowIpcAdds = true;
        screen.PlaylistShuffle = true;
        ScreenPlaylistPolicy.Load(screen, screen.Playlist[0]);

        var clone = screen.Clone();
        Assert.Equal(screen.Playlist.Select(e => e.EntryId), clone.Playlist.Select(e => e.EntryId));
        Assert.Equal(screen.PlaylistCurrentEntryId, clone.PlaylistCurrentEntryId);
        Assert.True(clone.PlaylistAllowIpcAdds);
        Assert.NotSame(screen.Playlist[0], clone.Playlist[0]);

        var copy = screen.CloneAsNew("Copy");
        Assert.Equal(2, copy.Playlist.Count);
        Assert.True(copy.PlaylistShuffle);
        Assert.False(copy.PlaylistAllowIpcAdds);
        Assert.Equal(string.Empty, copy.PlaylistCurrentEntryId);
        Assert.All(copy.Playlist, entry => Assert.False(entry.Played));
    }

    [Fact]
    public void NormalizeRepairsLoadedPlaylists()
    {
        var screen = new BrowserScreenProfile
        {
            Playlist =
            [
                new PlaylistEntry { EntryId = "same", Url = First },
                new PlaylistEntry { EntryId = "same", Url = First },
                new PlaylistEntry { Url = "" },
                new PlaylistEntry { Url = First, ProviderKind = (BrowserSourceProviderKind)99 },
            ],
            PlaylistCurrentEntryId = "missing",
        };

        Assert.True(ScreenPlaylistPolicy.Normalize(screen));
        Assert.Equal(2, screen.Playlist.Count);
        Assert.NotEqual(screen.Playlist[0].EntryId, screen.Playlist[1].EntryId);
        Assert.Equal(string.Empty, screen.PlaylistCurrentEntryId);
    }

    [Fact]
    public void EntriesFromTheFirstBuildGetTheirAutoNameMoved()
    {
        var screen = new BrowserScreenProfile
        {
            Playlist =
            [
                new PlaylistEntry { Url = "https://www.youtube.com/watch?v=RiR77AI4uQ8", Title = "Video ID: RiR77AI4uQ8" },
                new PlaylistEntry { Url = "https://vimeo.com/76979871", ProviderKind = BrowserSourceProviderKind.Vimeo, Title = "Chosen by a plugin" },
            ],
        };

        Assert.True(ScreenPlaylistPolicy.Normalize(screen));
        Assert.Equal(string.Empty, screen.Playlist[0].Title);
        Assert.Equal("Video ID: RiR77AI4uQ8", screen.Playlist[0].SourceName);
        Assert.Equal("Chosen by a plugin", screen.Playlist[1].Title);

        screen.Playlist[0].MetadataTitle = "The real title";
        Assert.Equal("The real title", ScreenPlaylistPolicy.DisplayTitle(screen.Playlist[0]));
        Assert.False(ScreenPlaylistPolicy.Normalize(screen));
    }

    [Fact]
    public void MoveReorders()
    {
        var screen = ScreenWith(First, Second, Third);
        Assert.True(ScreenPlaylistPolicy.Move(screen, screen.Playlist[2].EntryId, 0));
        Assert.Equal([Third, First, Second], screen.Playlist.Select(entry => entry.Url));
        Assert.False(ScreenPlaylistPolicy.Move(screen, screen.Playlist[0].EntryId, -5));
    }

    internal static BrowserScreenProfile ScreenWith(params string[] urls)
    {
        var screen = new BrowserScreenProfile();
        foreach (var url in urls)
            Assert.True(ScreenPlaylistPolicy.TryAdd(screen, UserEntry(url), out _));

        return screen;
    }

    private static string PlayNext(BrowserScreenProfile screen, Random random)
    {
        Assert.True(ScreenPlaylistPolicy.TryPickNext(screen, random, out var pick));
        ScreenPlaylistPolicy.Load(screen, pick.Entry, pick.StartsOver);
        return pick.Entry.Url;
    }

    private static PlaylistEntry UserEntry(string url)
    {
        Assert.True(ScreenPlaylistPolicy.TryCreateEntry(url, BrowserSourceProviderKind.GenericWeb, false, "", null, out var entry, out var error), error);
        return entry;
    }

    private static PlaylistEntry IpcEntry(string url, string owner)
    {
        Assert.True(ScreenPlaylistPolicy.TryCreateEntry(url, BrowserSourceProviderKind.GenericWeb, true, owner, null, out var entry, out var error), error);
        return entry;
    }
}

public sealed class ScreenPlaylistAdvancerTests
{
    private const string Current = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private const string Next = "https://vimeo.com/76979871";

    private long now = 1_000;
    private MediaPlaybackTelemetry? telemetry;

    [Fact]
    public void MovesOnWhenAVideoItWasPlayingEnds()
    {
        var (configuration, screen, advancer) = Setup();
        Report(ScreenPlaybackState.Playing);
        Assert.Empty(advancer.Update(configuration));

        Report(ScreenPlaybackState.Stopped, ended: true);
        var advanced = Assert.Single(advancer.Update(configuration));

        Assert.Equal(screen.ScreenId, advanced.ScreenId);
        Assert.False(advanced.SameSource);
        Assert.Equal(BrowserSourceProviderKind.Vimeo, screen.ProviderKind);
        Assert.Equal(Next, screen.VimeoUrl);
        Assert.Equal(screen.Playlist[0].EntryId, screen.PlaylistCurrentEntryId);
    }

    [Fact]
    public void AnEndingItNeverSawPlayingDoesNotCount()
    {
        // A stream that is offline from the start reports the same as one that just ended.
        var (configuration, _, advancer) = Setup();
        Report(ScreenPlaybackState.Stopped, ended: true);
        Assert.Empty(advancer.Update(configuration));
    }

    [Fact]
    public void ReportsFromThePreviousVideoAreIgnored()
    {
        var (configuration, screen, advancer) = Setup();
        Report(ScreenPlaybackState.Playing);
        advancer.Update(configuration);

        // The user loads another link; the old player's last reports arrive afterwards.
        var reportTime = now;
        now += 100;
        screen.YouTubeUrl = "https://www.youtube.com/watch?v=aaaaaaaaaaa";
        telemetry = new MediaPlaybackTelemetry { State = ScreenPlaybackState.Playing, HostTimestampUnixMs = reportTime };
        advancer.Update(configuration);
        telemetry = new MediaPlaybackTelemetry { State = ScreenPlaybackState.Stopped, Ended = true, HostTimestampUnixMs = reportTime };
        Assert.Empty(advancer.Update(configuration));
    }

    [Fact]
    public void APausedScreenDoesNotMoveOn()
    {
        var (configuration, screen, advancer) = Setup();
        Report(ScreenPlaybackState.Playing);
        advancer.Update(configuration);
        screen.PlaybackPaused = true;
        Report(ScreenPlaybackState.Stopped, ended: true);
        Assert.Empty(advancer.Update(configuration));
    }

    [Fact]
    public void EachEndingIsHandledOnce()
    {
        // A one-video playlist on loop reloads the same link, which the player has to restart.
        var (configuration, screen, advancer) = Setup();
        screen.Playlist.Clear();
        Assert.True(ScreenPlaylistPolicy.TryAdd(screen, Entry(Current), out _));
        screen.PlaylistLoop = true;
        Report(ScreenPlaybackState.Playing);
        advancer.Update(configuration);

        Report(ScreenPlaybackState.Stopped, ended: true);
        var advanced = Assert.Single(advancer.Update(configuration));
        Assert.True(advanced.SameSource);
        Assert.Empty(advancer.Update(configuration));
    }

    private (Configuration, BrowserScreenProfile, ScreenPlaylistAdvancer) Setup()
    {
        var screen = new BrowserScreenProfile { YouTubeUrl = Current };
        Assert.True(ScreenPlaylistPolicy.TryAdd(screen, Entry(Next), out _));
        var configuration = new Configuration { Enabled = true };
        configuration.BrowserScreens.Clear();
        configuration.BrowserScreens.Add(screen);
        var advancer = new ScreenPlaylistAdvancer(_ => telemetry, () => now, new Random(1));
        return (configuration, screen, advancer);
    }

    private void Report(ScreenPlaybackState state, bool ended = false)
    {
        now += 250;
        telemetry = new MediaPlaybackTelemetry { State = state, Ended = ended, HostTimestampUnixMs = now };
    }

    private static PlaylistEntry Entry(string url)
    {
        Assert.True(ScreenPlaylistPolicy.TryCreateEntry(url, BrowserSourceProviderKind.GenericWeb, false, "", null, out var entry, out _));
        return entry;
    }
}

public sealed class PlaylistEndedTelemetryTests
{
    [Theory]
    [InlineData("""{"type":"status","nonce":"n","state":0}""", true)]
    [InlineData("""{"type":"status","nonce":"n","state":-1}""", false)]
    [InlineData("""{"type":"status","nonce":"n","state":1}""", false)]
    [InlineData("""{"type":"status","nonce":"n","state":0,"finished":false}""", false)]
    [InlineData("""{"type":"status","nonce":"n","state":0,"finished":true}""", true)]
    public void StateZeroMeansEndedUnlessThePageSaysOtherwise(string message, bool ended)
    {
        Assert.True(BrowserSourceDescriptors.YouTube.TryParse("https://www.youtube.com/watch?v=dQw4w9WgXcQ", out var source));
        var state = new BrowserPlayerState(BrowserSourceDescriptors.YouTube, source, 1.0f);
        using var document = JsonDocument.Parse(message);

        state.HandleMessage(document.RootElement, "n", 1.0f);

        Assert.Equal(ended, state.Telemetry.Ended);
    }
}

public sealed class ScreenPlaylistIpcServiceTests
{
    private readonly Configuration configuration = new() { Enabled = true };
    private readonly BrowserScreenProfile screen = new() { YouTubeUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" };
    private readonly List<PlaylistAdvance> advances = [];
    private readonly ScreenPlaylistIpcService service;

    public ScreenPlaylistIpcServiceTests()
    {
        configuration.BrowserScreens.Clear();
        configuration.BrowserScreens.Add(screen);
        var advancer = new ScreenPlaylistAdvancer(_ => null, random: new Random(1));
        service = new ScreenPlaylistIpcService(configuration, advancer, advances.Add, _ => { }, NoOpLog.Create());
    }

    [Fact]
    public void ThePluginThatCreatedAScreenManagesItsPlaylist()
    {
        screen.CreatedByIpc = true;
        screen.IpcOwnerId = "Creator";
        Assert.True(Call(service.AddJson, new { screenId = screen.ScreenId, ownerId = "Creator", url = "https://vimeo.com/76979871" }).Success);
        Assert.False(Call(service.AddJson, new { screenId = screen.ScreenId, ownerId = "Other", url = "https://vimeo.com/76979871" }).Success);

        Assert.False(Call(service.SkipJson, new { screenId = screen.ScreenId, ownerId = "Other" }).Success);
        var skipped = Call(service.SkipJson, new { screenId = screen.ScreenId, ownerId = "Creator" });
        Assert.True(skipped.Success, skipped.Error);

        var options = Call(service.SetOptionsJson, new { screenId = screen.ScreenId, ownerId = "Creator", loop = true });
        Assert.True(options.Success, options.Error);
        Assert.True(screen.PlaylistLoop);
    }

    [Fact]
    public void PlaylistStateSaysWhatIsActuallyAllowed()
    {
        screen.PlaylistAllowIpcAdds = true;
        var userScreen = IpcJsonService.Deserialize<ScreenIpcPlaylistResponse>(service.GetJson(screen.ScreenId))!.Playlist!;
        Assert.True(userScreen.AllowIpcAdds);
        Assert.True(userScreen.AllowUserAdds);

        screen.CreatedByIpc = true;
        var pluginScreen = IpcJsonService.Deserialize<ScreenIpcPlaylistResponse>(service.GetJson(screen.ScreenId))!.Playlist!;
        Assert.False(pluginScreen.AllowIpcAdds);
        Assert.False(pluginScreen.AllowUserAdds);

        screen.PlaylistAllowUserAdds = true;
        Assert.True(IpcJsonService.Deserialize<ScreenIpcPlaylistResponse>(service.GetJson(screen.ScreenId))!.Playlist!.AllowUserAdds);
    }

    [Fact]
    public void AddIsRefusedUntilTheUserAllowsIt()
    {
        var refused = Call(service.AddJson, new { screenId = screen.ScreenId, ownerId = "Sync", url = "https://vimeo.com/76979871" });
        Assert.False(refused.Success);
        Assert.Empty(screen.Playlist);

        screen.PlaylistAllowIpcAdds = true;
        var added = Call(service.AddJson, new { screenId = screen.ScreenId, ownerId = "Sync", url = "https://vimeo.com/76979871", title = "A video" });
        Assert.True(added.Success, added.Error);
        var entry = Assert.Single(added.Playlist!.Entries);
        Assert.Equal(added.EntryId, entry.EntryId);
        Assert.Equal("Vimeo", entry.Provider);
        Assert.Equal("A video", entry.Title);
        Assert.True(entry.AddedByIpc);
        Assert.Equal("Sync", entry.AddedBy);
    }

    [Fact]
    public void BareIdsAreRefused()
    {
        screen.PlaylistAllowIpcAdds = true;
        Assert.False(Call(service.AddJson, new { screenId = screen.ScreenId, ownerId = "Sync", url = "dQw4w9WgXcQ" }).Success);
    }

    [Fact]
    public void OnlyTheLockOwnerCanSkip()
    {
        screen.PlaylistAllowIpcAdds = true;
        Assert.True(Call(service.AddJson, new { screenId = screen.ScreenId, ownerId = "Sync", url = "https://vimeo.com/76979871" }).Success);

        Assert.False(Call(service.SkipJson, new { screenId = screen.ScreenId, ownerId = "Sync" }).Success);

        screen.SourceControlsLocked = true;
        screen.SourceControlsOwnerId = "Sync";
        var skipped = Call(service.SkipJson, new { screenId = screen.ScreenId, ownerId = "Sync" });
        Assert.True(skipped.Success, skipped.Error);
        Assert.Single(advances);
        Assert.Equal(BrowserSourceProviderKind.Vimeo, screen.ProviderKind);
    }

    [Fact]
    public void PluginsCannotRemoveOtherPluginsVideos()
    {
        screen.PlaylistAllowIpcAdds = true;
        var added = Call(service.AddJson, new { screenId = screen.ScreenId, ownerId = "A", url = "https://vimeo.com/76979871" });

        Assert.False(Call(service.RemoveJson, new { screenId = screen.ScreenId, ownerId = "B", entryId = added.EntryId }).Success);
        Assert.True(Call(service.RemoveJson, new { screenId = screen.ScreenId, ownerId = "A", entryId = added.EntryId }).Success);
        Assert.Empty(screen.Playlist);
    }

    [Fact]
    public void ThePermissionCannotBeSetOverIpc()
    {
        screen.SourceControlsLocked = true;
        screen.SourceControlsOwnerId = "Sync";
        var result = Call(service.SetOptionsJson, new { screenId = screen.ScreenId, ownerId = "Sync", shuffle = true, allowIpcAdds = true });
        Assert.True(result.Success, result.Error);
        Assert.True(screen.PlaylistShuffle);
        Assert.False(screen.PlaylistAllowIpcAdds);
    }

    [Fact]
    public void GetReturnsThePlaylist()
    {
        var response = IpcJsonService.Deserialize<ScreenIpcPlaylistResponse>(service.GetJson(screen.ScreenId))!;
        Assert.True(response.Success);
        Assert.Equal(ScreenPlaylistPolicy.MaxEntries, response.Playlist!.MaxEntries);

        Assert.False(IpcJsonService.Deserialize<ScreenIpcPlaylistResponse>(service.GetJson("missing"))!.Success);
    }

    private static ScreenIpcPlaylistResponse Call(Func<string, string> call, object request)
    {
        var json = JsonSerializer.Serialize(request);
        return IpcJsonService.Deserialize<ScreenIpcPlaylistResponse>(call(json))!;
    }
}

/// <summary>An <see cref="IPluginLog"/> that discards everything.</summary>
public class NoOpLog : DispatchProxy
{
    public static IPluginLog Create() => Create<IPluginLog, NoOpLog>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var returnType = targetMethod?.ReturnType;
        return returnType is { IsValueType: true } && returnType != typeof(void) ? Activator.CreateInstance(returnType) : null;
    }
}
