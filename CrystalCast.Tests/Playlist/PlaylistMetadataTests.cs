using System.Net;
using CrystalCast.Playlist;
using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class PlaylistOEmbedTests
{
    [Fact]
    public void ReadsYouTubeAnswers()
    {
        const string json = """
            {"title":"Rick Astley - Never Gonna Give You Up","author_name":"Rick Astley",
             "thumbnail_url":"https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg","type":"video"}
            """;
        var metadata = PlaylistOEmbed.Parse(BrowserSourceProviderKind.YouTube, json);

        Assert.NotNull(metadata);
        Assert.Equal("Rick Astley - Never Gonna Give You Up", metadata.Title);
        Assert.Equal("Rick Astley", metadata.Author);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", metadata.ThumbnailUrl);
        Assert.Equal(0, metadata.DurationSeconds);
    }

    [Fact]
    public void ReadsVimeoLength()
    {
        const string json = """{"title":"The New Vimeo Player","author_name":"Vimeo","thumbnail_url":"https://i.vimeocdn.com/video/452001751-640.jpg","duration":62}""";
        Assert.Equal(62, PlaylistOEmbed.Parse(BrowserSourceProviderKind.Vimeo, json)!.DurationSeconds);
    }

    [Theory]
    [InlineData("http://i.ytimg.com/vi/x/hqdefault.jpg")]
    [InlineData("https://evil.example/vi/x/hqdefault.jpg")]
    [InlineData("https://i.ytimg.com.evil.example/x.jpg")]
    [InlineData("https://i.ytimg.com:8443/x.jpg")]
    [InlineData("https://i.vimeocdn.com/video/1.jpg")]
    public void ThumbnailsOffTheSitesImageHostAreDropped(string thumbnail)
    {
        var json = $$"""{"title":"A video","thumbnail_url":"{{thumbnail}}"}""";
        var metadata = PlaylistOEmbed.Parse(BrowserSourceProviderKind.YouTube, json);

        Assert.NotNull(metadata);
        Assert.Equal(string.Empty, metadata.ThumbnailUrl);
    }

    [Theory]
    [InlineData("""{"author_name":"No title"}""")]
    [InlineData("""{"title":"   "}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    public void AnswersWithoutATitleAreIgnored(string json)
    {
        Assert.Null(PlaylistOEmbed.Parse(BrowserSourceProviderKind.YouTube, json));
    }

    [Fact]
    public void TitlesAreCleanedAndShortened()
    {
        var json = $$"""{"title":"Line\nbreak {{new string('x', 500)}}"}""";
        var title = PlaylistOEmbed.Parse(BrowserSourceProviderKind.YouTube, json)!.Title;

        Assert.DoesNotContain('\n', title);
        Assert.Equal(ScreenPlaylistPolicy.MaxTitleLength, title.Length);
    }

    [Fact]
    public void RequestsGoToTheSitesOwnEndpoint()
    {
        Assert.True(PlaylistOEmbed.TryBuildRequestUri(Entry("https://vimeo.com/76979871"), out var vimeo));
        Assert.Equal("vimeo.com", vimeo.Host);
        Assert.Contains(Uri.EscapeDataString("https://vimeo.com/76979871"), vimeo.AbsoluteUri);

        Assert.True(PlaylistOEmbed.TryBuildRequestUri(Entry("https://www.dailymotion.com/video/x8abcd1"), out var dailymotion));
        Assert.Equal("www.dailymotion.com", dailymotion.Host);
    }

    [Fact]
    public void BareIdsAreTurnedIntoLinks()
    {
        var entry = Entry("dQw4w9WgXcQ", BrowserSourceProviderKind.YouTube);
        Assert.True(PlaylistOEmbed.TryBuildRequestUri(entry, out var uri));
        Assert.Equal("www.youtube.com", uri.Host);
        Assert.Contains("dQw4w9WgXcQ", Uri.UnescapeDataString(uri.Query));
    }

    [Fact]
    public void YouTubeGetsThePlainWatchLink()
    {
        Assert.True(PlaylistOEmbed.TryBuildRequestUri(Entry("https://www.youtube.com/shorts/mWI6yfErSM0"), out var shorts));
        Assert.Contains("https://www.youtube.com/watch?v=mWI6yfErSM0", Uri.UnescapeDataString(shorts.Query));

        Assert.True(PlaylistOEmbed.TryBuildRequestUri(Entry("https://youtu.be/dQw4w9WgXcQ"), out var shortLink));
        Assert.Contains("https://www.youtube.com/watch?v=dQw4w9WgXcQ", Uri.UnescapeDataString(shortLink.Query));
    }

    [Fact]
    public void TwitchChannelsAreDescribedFromTheLink()
    {
        Assert.True(PlaylistOEmbed.TryDescribeLocally(Entry("https://www.twitch.tv/Shenpai"), out var metadata));
        Assert.Equal("shenpai", metadata.Title);
        Assert.Equal("https://static-cdn.jtvnw.net/previews-ttv/live_user_shenpai-320x180.jpg", metadata.ThumbnailUrl);
        Assert.True(PlaylistOEmbed.IsAllowedThumbnail(BrowserSourceProviderKind.Twitch, metadata.ThumbnailUrl));

        var entry = Entry("https://www.twitch.tv/Shenpai");
        PlaylistMetadataService.Apply(entry, metadata);
        Assert.True(PlaylistOEmbed.IsLivePreview(entry));
    }

    [Fact]
    public void TwitchVodsHaveNoPublicDescription()
    {
        Assert.False(PlaylistOEmbed.TryDescribeLocally(Entry("https://www.twitch.tv/videos/1234567890"), out _));
        Assert.False(PlaylistOEmbed.TryDescribeLocally(Entry("https://vimeo.com/76979871"), out _));
    }

    [Fact]
    public void TwitchsOfflineRedirectIsRecognized()
    {
        Assert.True(PlaylistOEmbed.IsTwitchOfflinePreview(new Uri("https://static-cdn.jtvnw.net/ttv-static/404_preview-320x180.jpg")));
        Assert.False(PlaylistOEmbed.IsTwitchOfflinePreview(new Uri("https://static-cdn.jtvnw.net/previews-ttv/live_user_shenpai-320x180.jpg")));
        Assert.False(PlaylistOEmbed.IsTwitchOfflinePreview(new Uri("https://example.com/ttv-static/404_preview-320x180.jpg")));
        Assert.False(PlaylistOEmbed.IsTwitchOfflinePreview(null));
    }

    [Fact]
    public void WebPagesAndTwitchAreNotLookedUp()
    {
        Assert.False(PlaylistOEmbed.TryBuildRequestUri(Entry("https://example.com/page"), out _));
        Assert.False(PlaylistOEmbed.TryBuildRequestUri(Entry("https://www.twitch.tv/somechannel"), out _));
    }

    [Fact]
    public void PlaylistLinksAreNotSingleVideos()
    {
        Assert.True(PlaylistOEmbed.IsSingleVideo(Entry("https://www.youtube.com/watch?v=dQw4w9WgXcQ")));
        Assert.False(PlaylistOEmbed.IsSingleVideo(Entry("https://www.youtube.com/playlist?list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI")));
    }

    internal static PlaylistEntry Entry(string url, BrowserSourceProviderKind current = BrowserSourceProviderKind.GenericWeb)
    {
        Assert.True(ScreenPlaylistPolicy.TryCreateEntry(url, current, false, "", null, out var entry, out var error), error);
        return entry;
    }
}

public sealed class PlaylistMetadataServiceTests
{
    private MediaPlaybackTelemetry? telemetry;

    [Fact]
    public async Task FillsInEntriesFromTheSite()
    {
        var handler = new StubHandler(_ => Json("""{"title":"A video","author_name":"A channel","thumbnail_url":"https://i.vimeocdn.com/video/1.jpg","duration":90}"""));
        var (configuration, screen, service) = Setup(handler, "https://vimeo.com/76979871");

        Assert.Empty(service.Update(configuration, force: true));
        var changed = await UpdateUntilChanged(service, configuration);

        Assert.Contains(screen.ScreenId, changed);
        var entry = screen.Playlist[0];
        Assert.True(entry.MetadataLookedUp);
        Assert.Equal("A video", ScreenPlaylistPolicy.DisplayTitle(entry));
        Assert.Equal("A channel", entry.Author);
        Assert.Equal("https://i.vimeocdn.com/video/1.jpg", entry.ThumbnailUrl);
        Assert.Equal(90, entry.DurationSeconds);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RunsWithoutBeingForced()
    {
        // The half-second throttle must let the first call through.
        var handler = new StubHandler(_ => Json("""{"title":"A video"}"""));
        var (configuration, _, service) = Setup(handler, "https://vimeo.com/76979871");

        service.Update(configuration);
        await handler.WaitForCall();
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ATitleFromThePluginWins()
    {
        var handler = new StubHandler(_ => Json("""{"title":"Site title"}"""));
        var (configuration, screen, service) = Setup(handler, "https://vimeo.com/76979871");
        screen.Playlist[0].Title = "Plugin title";

        service.Update(configuration, force: true);
        await UpdateUntilChanged(service, configuration);

        Assert.Equal("Plugin title", ScreenPlaylistPolicy.DisplayTitle(screen.Playlist[0]));
        Assert.Equal("Site title", screen.Playlist[0].MetadataTitle);
    }

    [Fact]
    public async Task VideosTheSiteRefusesAreNotAskedAgain()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var (configuration, screen, service) = Setup(handler, "https://vimeo.com/76979871");

        service.Update(configuration, force: true);
        await UpdateUntilChanged(service, configuration);

        Assert.True(screen.Playlist[0].MetadataLookedUp);
        Assert.Equal(string.Empty, screen.Playlist[0].MetadataTitle);
    }

    [Fact]
    public async Task NetworkTroubleIsRetriedNextSession()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("offline"));
        var (configuration, screen, service) = Setup(handler, "https://vimeo.com/76979871");

        service.Update(configuration, force: true);
        await handler.WaitForCall();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        service.Update(configuration, force: true);

        Assert.False(screen.Playlist[0].MetadataLookedUp);
        service.Update(configuration, force: true);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void TwitchChannelsNeedNoRequest()
    {
        var handler = new StubHandler(_ => Json("""{"title":"x"}"""));
        var (configuration, screen, service) = Setup(handler, "https://www.twitch.tv/shenpai");

        Assert.Contains(screen.ScreenId, service.Update(configuration, force: true));
        Assert.Equal("shenpai", ScreenPlaylistPolicy.DisplayTitle(screen.Playlist[0]));
        Assert.True(screen.Playlist[0].MetadataLookedUp);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AScreensOwnLinkIsLookedUpForItsTile()
    {
        var handler = new StubHandler(_ => Json("""{"title":"A video","thumbnail_url":"https://i.vimeocdn.com/video/1.jpg"}"""));
        var screen = new BrowserScreenProfile { ProviderKind = BrowserSourceProviderKind.Vimeo, VimeoUrl = "https://vimeo.com/76979871" };
        var configuration = new Configuration { Enabled = true };
        configuration.BrowserScreens.Clear();
        configuration.BrowserScreens.Add(screen);
        var service = new PlaylistMetadataService(null, NoOpLog.Create(), _ => telemetry, handler);

        // Not a playlist change, so nothing is reported or saved.
        Assert.Empty(service.Update(configuration, force: true));
        await handler.WaitForCall();
        Assert.Equal(1, handler.Calls);
        for (var i = 0; i < 20; i++)
        {
            Assert.Empty(service.Update(configuration, force: true));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void AScreenShowingAPlaylistVideoReusesItsDetails()
    {
        var handler = new StubHandler(_ => Json("""{"title":"x"}"""));
        var (configuration, screen, service) = Setup(handler, "https://vimeo.com/76979871");
        var entry = screen.Playlist[0];
        PlaylistMetadataService.Apply(entry, new VideoMetadata("A video", "", "https://i.vimeocdn.com/video/1.jpg", 0));
        entry.MetadataLookedUp = true;

        // The entry plays, then leaves the playlist: the screen's link still needs no request.
        ScreenPlaylistPolicy.Load(screen, entry);
        service.Update(configuration, force: true);
        ScreenPlaylistPolicy.Remove(screen, entry.EntryId);
        service.Update(configuration, force: true);

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ChangingTheScreensLinkDescribesTheNewVideo()
    {
        var handler = new StubHandler(request => Json(request.RequestUri!.Query.Contains("111")
            ? """{"title":"First","thumbnail_url":"https://i.vimeocdn.com/video/1.jpg"}"""
            : """{"title":"Second","thumbnail_url":"https://i.vimeocdn.com/video/2.jpg"}"""));
        var screen = new BrowserScreenProfile { ProviderKind = BrowserSourceProviderKind.Vimeo, VimeoUrl = "https://vimeo.com/111" };
        var configuration = new Configuration { Enabled = true };
        configuration.BrowserScreens.Clear();
        configuration.BrowserScreens.Add(screen);
        var service = new PlaylistMetadataService(null, NoOpLog.Create(), _ => telemetry, handler);

        Assert.Equal("https://i.vimeocdn.com/video/1.jpg", await SourceThumbnailUrl(service, configuration, screen));
        screen.VimeoUrl = "https://vimeo.com/222";
        Assert.Equal("https://i.vimeocdn.com/video/2.jpg", await SourceThumbnailUrl(service, configuration, screen));

        // Going back to a link seen earlier this session still finds its picture.
        screen.VimeoUrl = "https://vimeo.com/111";
        Assert.Equal("https://i.vimeocdn.com/video/1.jpg", await SourceThumbnailUrl(service, configuration, screen));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task TheTileFollowsTheVideoThePlayerIsOn()
    {
        var handler = new StubHandler(request => Json(Uri.UnescapeDataString(request.RequestUri!.Query).Contains("v=bbbbbbbbbbb")
            ? """{"title":"Next","thumbnail_url":"https://i.ytimg.com/vi/bbbbbbbbbbb/hqdefault.jpg"}"""
            : """{"title":"First","thumbnail_url":"https://i.ytimg.com/vi/aaaaaaaaaaa/hqdefault.jpg"}"""));
        var screen = new BrowserScreenProfile { YouTubeUrl = "https://www.youtube.com/watch?v=aaaaaaaaaaa&list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI" };
        var configuration = new Configuration { Enabled = true };
        configuration.BrowserScreens.Clear();
        configuration.BrowserScreens.Add(screen);
        var service = new PlaylistMetadataService(null, NoOpLog.Create(), _ => telemetry, handler);

        Assert.Equal("https://i.ytimg.com/vi/aaaaaaaaaaa/hqdefault.jpg", await SourceThumbnailUrl(service, configuration, screen));

        // The YouTube playlist moves on; the screen's own link stays the same.
        telemetry = new MediaPlaybackTelemetry
        {
            State = ScreenPlaybackState.Playing,
            CanonicalUrl = "https://www.youtube.com/watch?v=bbbbbbbbbbb&list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI",
        };
        Assert.Equal("https://i.ytimg.com/vi/bbbbbbbbbbb/hqdefault.jpg", await SourceThumbnailUrlChangedFrom(service, configuration, screen, "https://i.ytimg.com/vi/aaaaaaaaaaa/hqdefault.jpg"));
    }

    private static async Task<string> SourceThumbnailUrlChangedFrom(PlaylistMetadataService service, Configuration configuration, BrowserScreenProfile screen, string previous)
    {
        for (var i = 0; i < 200; i++)
        {
            service.Update(configuration, force: true);
            if (service.GetSourceThumbnailUrl(screen) is { Length: > 0 } url && url != previous)
                return url;

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return previous;
    }

    private static async Task<string> SourceThumbnailUrl(PlaylistMetadataService service, Configuration configuration, BrowserScreenProfile screen)
    {
        for (var i = 0; i < 200; i++)
        {
            service.Update(configuration, force: true);
            if (service.GetSourceThumbnailUrl(screen) is { Length: > 0 } url)
                return url;

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return string.Empty;
    }

    [Fact]
    public void WebPagesAreNeverFetched()
    {
        var handler = new StubHandler(_ => Json("""{"title":"x"}"""));
        var (configuration, _, service) = Setup(handler, "https://example.com/page");

        service.Update(configuration, force: true);
        service.Update(configuration, force: true);

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void WebPagesLearnTheirTitleOncePlaying()
    {
        var handler = new StubHandler(_ => Json("""{"title":"x"}"""));
        var (configuration, screen, service) = Setup(handler, "https://example.com/page");
        ScreenPlaylistPolicy.Load(screen, screen.Playlist[0]);

        // A report from before the entry started belongs to the previous video.
        telemetry = new MediaPlaybackTelemetry { State = ScreenPlaybackState.Playing, Title = "Old page", HostTimestampUnixMs = 1 };
        Assert.Empty(service.Update(configuration, force: true));

        telemetry = new MediaPlaybackTelemetry
        {
            State = ScreenPlaybackState.Playing,
            Title = "Example page",
            DurationMs = 125_000,
            HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000,
        };
        Assert.Contains(screen.ScreenId, service.Update(configuration, force: true));
        Assert.Equal("Example page", ScreenPlaylistPolicy.DisplayTitle(screen.Playlist[0]));
        Assert.Equal(125, screen.Playlist[0].DurationSeconds);
    }

    private (Configuration, BrowserScreenProfile, PlaylistMetadataService) Setup(StubHandler handler, string url)
    {
        var screen = new BrowserScreenProfile();
        Assert.True(ScreenPlaylistPolicy.TryAdd(screen, PlaylistOEmbedTests.Entry(url), out _));
        var configuration = new Configuration { Enabled = true };
        configuration.BrowserScreens.Clear();
        configuration.BrowserScreens.Add(screen);
        var service = new PlaylistMetadataService(null, NoOpLog.Create(), _ => telemetry, handler);
        return (configuration, screen, service);
    }

    private static async Task<IReadOnlyCollection<string>> UpdateUntilChanged(PlaylistMetadataService service, Configuration configuration)
    {
        for (var i = 0; i < 200; i++)
        {
            var changed = service.Update(configuration, force: true);
            if (changed.Count > 0)
                return changed;

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The lookup never finished.");
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly TaskCompletionSource called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public Task WaitForCall() => called.Task.WaitAsync(TimeSpan.FromSeconds(5));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            called.TrySetResult();
            return Task.FromResult(respond(request));
        }
    }
}
