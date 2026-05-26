using CrystalCast.Rendering;
using CrystalCast.Video;

namespace CrystalCast.Sync;

internal interface IBrowserSourceIpcAdapter
{
    BrowserSourceProviderKind ProviderKind { get; }
    bool HasPatch(ScreenIpcMutationRequest request);
    bool HasPatch(ScreenIpcSourceUpdateRequest request);
    bool ApplyPatch(BrowserScreenProfile screen, ScreenIpcMutationRequest request, out string error);
    bool ApplyPatch(BrowserScreenProfile screen, ScreenIpcSourceUpdateRequest request, out string error);
    void ApplyRuntimeControls(WorldScreenManager renderer, BrowserScreenProfile screen, ScreenIpcMutationRequest request);
    void ApplyRuntimeControls(WorldScreenManager renderer, BrowserScreenProfile screen, ScreenIpcSourceUpdateRequest request);
    void AddChangeKinds(List<ScreenIpcChangeKind> changes, ScreenIpcMutationRequest request);
    void AddChangeKinds(List<ScreenIpcChangeKind> changes, ScreenIpcSourceUpdateRequest request);
    ScreenPlaybackStateDto BuildPlaybackState(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry);
    ScreenSourceState BuildSourceState(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry);
    void PopulateSourceStateResponse(
        ScreenIpcSourceStateResponse response,
        BrowserScreenProfile screen,
        ScreenSourceState source,
        ScreenPlaybackStateDto playback);
    void AddFingerprintParts(BrowserScreenProfile? screen, List<object?> parts);
}

internal static class BrowserSourceIpcAdapters
{
    private static readonly IBrowserSourceIpcAdapter[] OrderedAdapters =
    [
        CreateYouTubeAdapter(),
        CreateTwitchAdapter(),
        CreateDailymotionAdapter(),
    ];

    private static readonly IReadOnlyDictionary<BrowserSourceProviderKind, IBrowserSourceIpcAdapter> Adapters =
        OrderedAdapters.ToDictionary(adapter => adapter.ProviderKind);

    public static IEnumerable<IBrowserSourceIpcAdapter> All => OrderedAdapters;

    public static bool IsSupported(BrowserSourceProviderKind provider)
    {
        return Adapters.ContainsKey(provider);
    }

    public static IBrowserSourceIpcAdapter Get(BrowserSourceProviderKind provider)
    {
        return Adapters.TryGetValue(provider, out var adapter)
            ? adapter
            : Adapters[BrowserSourceProviderKind.YouTube];
    }

    public static BrowserSourceProviderKind ResolveRequestedProvider(
        BrowserScreenProfile screen,
        BrowserSourceProviderKind? provider,
        ScreenIpcMutationRequest request)
    {
        return ResolveRequestedProvider(screen.ProviderKind, provider, adapter => adapter.HasPatch(request));
    }

    public static BrowserSourceProviderKind ResolveRequestedProvider(
        BrowserScreenProfile screen,
        BrowserSourceProviderKind? provider,
        ScreenIpcSourceUpdateRequest request)
    {
        return ResolveRequestedProvider(screen.ProviderKind, provider, adapter => adapter.HasPatch(request));
    }

    public static ScreenIpcChangeKind[] GetMutationChangeKinds(ScreenIpcMutationRequest request)
    {
        var changes = new List<ScreenIpcChangeKind>();
        if (request.Placement != null)
            changes.Add(ScreenIpcChangeKind.Placement);
        if (request.SourceControlsLocked.HasValue || !string.IsNullOrWhiteSpace(request.SourceControlsOwnerId))
            changes.Add(ScreenIpcChangeKind.SourceLock);
        AddBrowserChangeKinds(changes, request.Provider.HasValue, request);

        return changes.Count == 0 ? [ScreenIpcChangeKind.Source] : changes.Distinct().ToArray();
    }

    public static ScreenIpcChangeKind[] GetSourceUpdateChangeKinds(ScreenIpcSourceUpdateRequest request)
    {
        var changes = new List<ScreenIpcChangeKind>();
        AddBrowserChangeKinds(changes, request.Provider.HasValue, request);
        return changes.Count == 0 ? [ScreenIpcChangeKind.Source] : changes.Distinct().ToArray();
    }

    public static void AddFingerprintParts(BrowserScreenProfile? screen, List<object?> parts)
    {
        foreach (var adapter in All)
            adapter.AddFingerprintParts(screen, parts);
    }

    private static BrowserSourceProviderKind ResolveRequestedProvider(
        BrowserSourceProviderKind currentProvider,
        BrowserSourceProviderKind? provider,
        Func<IBrowserSourceIpcAdapter, bool> hasPatch)
    {
        if (provider.HasValue)
            return provider.Value;

        var requestedAdapters = All.Where(hasPatch).ToArray();
        return requestedAdapters.Length == 1
            ? requestedAdapters[0].ProviderKind
            : currentProvider;
    }

    private static void AddBrowserChangeKinds(
        List<ScreenIpcChangeKind> changes,
        bool providerChanged,
        ScreenIpcMutationRequest request)
    {
        if (providerChanged)
            changes.Add(ScreenIpcChangeKind.Source);

        foreach (var adapter in All)
            adapter.AddChangeKinds(changes, request);
    }

    private static void AddBrowserChangeKinds(
        List<ScreenIpcChangeKind> changes,
        bool providerChanged,
        ScreenIpcSourceUpdateRequest request)
    {
        if (providerChanged)
            changes.Add(ScreenIpcChangeKind.Source);

        foreach (var adapter in All)
            adapter.AddChangeKinds(changes, request);
    }

    private static BrowserSourceIpcAdapter<YouTubeScreenPatchDto, YouTubeSourceReference> CreateYouTubeAdapter()
    {
        return new BrowserSourceIpcAdapter<YouTubeScreenPatchDto, YouTubeSourceReference>
        {
            ProviderKind = BrowserSourceProviderKind.YouTube,
            Descriptor = BrowserSourceDescriptors.YouTube,
            InvalidPatchSourceMessage = "YouTube URL, video ID, playlist, or live channel is invalid.",
            InvalidIdentity = "youtube:invalid",
            InvalidTitle = "Invalid YouTube source",
            DefaultTitle = "YouTube video",
            GetMutationPatch = request => request.YouTube,
            GetSourceUpdatePatch = request => request.YouTube,
            GetUrl = screen => screen.YouTubeUrl,
            SetUrl = (screen, value) => screen.YouTubeUrl = value,
            GetAutoplay = screen => screen.YouTubeAutoplay,
            SetAutoplay = (screen, value) => screen.YouTubeAutoplay = value,
            GetLoop = screen => screen.LoopYouTube,
            SetLoop = (screen, value) => screen.LoopYouTube = value,
            GetPlaylistAutoplayNext = screen => screen.YouTubePlaylistAutoplayNext,
            SetPlaylistAutoplayNext = (screen, value) => screen.YouTubePlaylistAutoplayNext = value,
            GetPlaybackRate = screen => screen.YouTubePlaybackRate,
            SetPlaybackRate = (screen, value) => screen.YouTubePlaybackRate = value,
            GetPatchUrl = patch => patch.Url,
            GetPatchPlaybackPaused = patch => patch.PlaybackPaused,
            GetPatchPositionMs = patch => patch.PositionMs,
            GetPatchRestart = patch => patch.Restart,
            GetPatchAutoplay = patch => patch.Autoplay,
            GetPatchLoop = patch => patch.Loop,
            GetPatchPlaylistAutoplayNext = patch => patch.PlaylistAutoplayNext,
            GetPatchPlaybackRate = patch => patch.PlaybackRate,
            GetPatchBrowserWidth = patch => patch.BrowserWidth,
            SetBrowserWidth = (screen, value) => screen.YouTubeBrowserWidth = value,
            GetPatchBrowserHeight = patch => patch.BrowserHeight,
            SetBrowserHeight = (screen, value) => screen.YouTubeBrowserHeight = value,
            GetPatchCaptureFps = patch => patch.CaptureFps,
            SetCaptureFps = (screen, value) => screen.YouTubeCaptureFps = value,
            GetPatchCaptureFpsManual = patch => patch.CaptureFpsManual,
            SetCaptureFpsManual = (screen, value) => screen.YouTubeCaptureFpsManual = value,
            GetPatchAudioEnabled = patch => patch.AudioEnabled,
            SetAudioEnabled = (screen, value) => screen.YouTubeAudioEnabled = value,
            GetPatchVolume = patch => patch.Volume,
            SetVolume = (screen, value) => screen.YouTubeVolume = value,
            GetPatchSpatialAudioEnabled = patch => patch.SpatialAudioEnabled,
            GetPatchSpatialAudioFullVolumeRadiusMeters = patch => patch.SpatialAudioFullVolumeRadiusMeters,
            GetPatchSpatialAudioSilentRadiusMeters = patch => patch.SpatialAudioSilentRadiusMeters,
            BuildIdentity = source => source.Kind switch
            {
                YouTubeSourceKind.Playlist => $"youtube:playlist:{source.PlaylistId}",
                YouTubeSourceKind.LiveChannel => $"youtube:live-channel:{source.LiveChannelId}",
                _ => $"youtube:{source.VideoId}",
            },
            PopulateSourceStateDto = (response, screen, source, playback) =>
            {
                response.YouTube = new YouTubeSourceStateDto
                {
                    Url = screen.YouTubeUrl,
                    CanonicalUrl = source.Url,
                    VideoId = source.VideoId,
                    Title = source.Title,
                    State = playback.State,
                    PositionMs = playback.PositionMs,
                    DurationMs = playback.DurationMs,
                    Rate = playback.Rate,
                    HostTimestampUnixMs = playback.HostTimestampUnixMs,
                };
            },
            BuildFingerprintParts = screen =>
            [
                screen?.YouTubeAutoplay,
                screen?.LoopYouTube,
                screen?.YouTubePlaylistAutoplayNext,
                screen?.YouTubePlaybackRate,
                screen?.YouTubeBrowserWidth,
                screen?.YouTubeBrowserHeight,
                screen?.YouTubeCaptureFps,
                screen?.YouTubeCaptureFpsManual,
            ],
        };
    }

    private static BrowserSourceIpcAdapter<TwitchScreenPatchDto, TwitchSourceReference> CreateTwitchAdapter()
    {
        return new BrowserSourceIpcAdapter<TwitchScreenPatchDto, TwitchSourceReference>
        {
            ProviderKind = BrowserSourceProviderKind.Twitch,
            Descriptor = BrowserSourceDescriptors.Twitch,
            InvalidPatchSourceMessage = "Twitch channel or VOD URL is invalid.",
            InvalidIdentity = "twitch:invalid",
            InvalidTitle = "Invalid Twitch source",
            DefaultTitle = "Twitch",
            GetMutationPatch = request => request.Twitch,
            GetSourceUpdatePatch = request => request.Twitch,
            GetUrl = screen => screen.TwitchUrl,
            SetUrl = (screen, value) => screen.TwitchUrl = value,
            GetAutoplay = screen => screen.TwitchAutoplay,
            SetAutoplay = (screen, value) => screen.TwitchAutoplay = value,
            GetPatchUrl = patch => patch.Url,
            GetPatchPlaybackPaused = patch => patch.PlaybackPaused,
            GetPatchPositionMs = patch => patch.PositionMs,
            GetPatchRestart = patch => patch.Restart,
            GetPatchAutoplay = patch => patch.Autoplay,
            GetPatchBrowserWidth = patch => patch.BrowserWidth,
            SetBrowserWidth = (screen, value) => screen.TwitchBrowserWidth = value,
            GetPatchBrowserHeight = patch => patch.BrowserHeight,
            SetBrowserHeight = (screen, value) => screen.TwitchBrowserHeight = value,
            GetPatchCaptureFps = patch => patch.CaptureFps,
            SetCaptureFps = (screen, value) => screen.TwitchCaptureFps = value,
            GetPatchCaptureFpsManual = patch => patch.CaptureFpsManual,
            SetCaptureFpsManual = (screen, value) => screen.TwitchCaptureFpsManual = value,
            GetPatchAudioEnabled = patch => patch.AudioEnabled,
            SetAudioEnabled = (screen, value) => screen.TwitchAudioEnabled = value,
            GetPatchVolume = patch => patch.Volume,
            SetVolume = (screen, value) => screen.TwitchVolume = value,
            GetPatchSpatialAudioEnabled = patch => patch.SpatialAudioEnabled,
            GetPatchSpatialAudioFullVolumeRadiusMeters = patch => patch.SpatialAudioFullVolumeRadiusMeters,
            GetPatchSpatialAudioSilentRadiusMeters = patch => patch.SpatialAudioSilentRadiusMeters,
            BuildIdentity = source => source.Kind switch
            {
                TwitchSourceKind.Video => $"twitch:video:{source.VideoId}",
                _ => $"twitch:channel:{source.ChannelName}",
            },
            PopulateSourceStateDto = (response, screen, source, playback) =>
            {
                response.Twitch = new TwitchSourceStateDto
                {
                    Url = screen.TwitchUrl,
                    CanonicalUrl = source.Url,
                    VideoId = source.VideoId,
                    ChannelName = TwitchVideoId.TryParseSource(screen.TwitchUrl, out var parsed) && parsed.Kind == TwitchSourceKind.Channel
                        ? parsed.ChannelName
                        : string.Empty,
                    Title = source.Title,
                    State = playback.State,
                    PositionMs = playback.PositionMs,
                    DurationMs = playback.DurationMs,
                    Rate = playback.Rate,
                    HostTimestampUnixMs = playback.HostTimestampUnixMs,
                };
            },
            BuildFingerprintParts = screen =>
            [
                screen?.TwitchUrl,
                screen?.TwitchAutoplay,
                screen?.TwitchBrowserWidth,
                screen?.TwitchBrowserHeight,
                screen?.TwitchCaptureFps,
                screen?.TwitchCaptureFpsManual,
            ],
        };
    }

    private static BrowserSourceIpcAdapter<DailymotionScreenPatchDto, DailymotionSourceReference> CreateDailymotionAdapter()
    {
        return new BrowserSourceIpcAdapter<DailymotionScreenPatchDto, DailymotionSourceReference>
        {
            ProviderKind = BrowserSourceProviderKind.Dailymotion,
            Descriptor = BrowserSourceDescriptors.Dailymotion,
            InvalidPatchSourceMessage = "Dailymotion URL, video ID, or playlist is invalid.",
            InvalidIdentity = "dailymotion:invalid",
            InvalidTitle = "Invalid Dailymotion source",
            DefaultTitle = "Dailymotion",
            GetMutationPatch = request => request.Dailymotion,
            GetSourceUpdatePatch = request => request.Dailymotion,
            GetUrl = screen => screen.DailymotionUrl,
            SetUrl = (screen, value) => screen.DailymotionUrl = value,
            GetAutoplay = screen => screen.DailymotionAutoplay,
            SetAutoplay = (screen, value) => screen.DailymotionAutoplay = value,
            GetLoop = screen => screen.LoopDailymotion,
            SetLoop = (screen, value) => screen.LoopDailymotion = value,
            GetPatchUrl = patch => patch.Url,
            GetPatchPlaybackPaused = patch => patch.PlaybackPaused,
            GetPatchPositionMs = patch => patch.PositionMs,
            GetPatchRestart = patch => patch.Restart,
            GetPatchAutoplay = patch => patch.Autoplay,
            GetPatchLoop = patch => patch.Loop,
            GetPatchBrowserWidth = patch => patch.BrowserWidth,
            SetBrowserWidth = (screen, value) => screen.DailymotionBrowserWidth = value,
            GetPatchBrowserHeight = patch => patch.BrowserHeight,
            SetBrowserHeight = (screen, value) => screen.DailymotionBrowserHeight = value,
            GetPatchCaptureFps = patch => patch.CaptureFps,
            SetCaptureFps = (screen, value) => screen.DailymotionCaptureFps = value,
            GetPatchCaptureFpsManual = patch => patch.CaptureFpsManual,
            SetCaptureFpsManual = (screen, value) => screen.DailymotionCaptureFpsManual = value,
            GetPatchAudioEnabled = patch => patch.AudioEnabled,
            SetAudioEnabled = (screen, value) => screen.DailymotionAudioEnabled = value,
            GetPatchVolume = patch => patch.Volume,
            SetVolume = (screen, value) => screen.DailymotionVolume = value,
            GetPatchSpatialAudioEnabled = patch => patch.SpatialAudioEnabled,
            GetPatchSpatialAudioFullVolumeRadiusMeters = patch => patch.SpatialAudioFullVolumeRadiusMeters,
            GetPatchSpatialAudioSilentRadiusMeters = patch => patch.SpatialAudioSilentRadiusMeters,
            BuildIdentity = source => source.Kind switch
            {
                DailymotionSourceKind.Playlist => $"dailymotion:playlist:{source.PlaylistId}",
                _ => $"dailymotion:video:{source.VideoId}",
            },
            PopulateSourceStateDto = (response, screen, source, playback) =>
            {
                response.Dailymotion = new DailymotionSourceStateDto
                {
                    Url = screen.DailymotionUrl,
                    CanonicalUrl = source.Url,
                    VideoId = source.VideoId,
                    PlaylistId = DailymotionVideoId.TryParseSource(screen.DailymotionUrl, out var parsed) && parsed.Kind == DailymotionSourceKind.Playlist
                        ? parsed.PlaylistId
                        : string.Empty,
                    Title = source.Title,
                    State = playback.State,
                    PositionMs = playback.PositionMs,
                    DurationMs = playback.DurationMs,
                    Rate = playback.Rate,
                    HostTimestampUnixMs = playback.HostTimestampUnixMs,
                };
            },
            BuildFingerprintParts = screen =>
            [
                screen?.DailymotionUrl,
                screen?.DailymotionAutoplay,
                screen?.LoopDailymotion,
                screen?.DailymotionBrowserWidth,
                screen?.DailymotionBrowserHeight,
                screen?.DailymotionCaptureFps,
                screen?.DailymotionCaptureFpsManual,
            ],
        };
    }

    private sealed class BrowserSourceIpcAdapter<TPatch, TSource> : IBrowserSourceIpcAdapter
        where TPatch : class
        where TSource : struct, IBrowserSourceReference
    {
        public required BrowserSourceProviderKind ProviderKind { get; init; }
        public required BrowserSourceDescriptor Descriptor { get; init; }
        public required string InvalidPatchSourceMessage { get; init; }
        public required string InvalidIdentity { get; init; }
        public required string InvalidTitle { get; init; }
        public required string DefaultTitle { get; init; }
        public required Func<ScreenIpcMutationRequest, TPatch?> GetMutationPatch { get; init; }
        public required Func<ScreenIpcSourceUpdateRequest, TPatch?> GetSourceUpdatePatch { get; init; }
        public required Func<BrowserScreenProfile, string> GetUrl { get; init; }
        public required Action<BrowserScreenProfile, string> SetUrl { get; init; }
        public required Func<BrowserScreenProfile, bool> GetAutoplay { get; init; }
        public required Action<BrowserScreenProfile, bool> SetAutoplay { get; init; }
        public Func<BrowserScreenProfile, bool> GetLoop { get; init; } = _ => false;
        public Action<BrowserScreenProfile, bool> SetLoop { get; init; } = (_, _) => { };
        public Func<BrowserScreenProfile, bool> GetPlaylistAutoplayNext { get; init; } = _ => true;
        public Action<BrowserScreenProfile, bool> SetPlaylistAutoplayNext { get; init; } = (_, _) => { };
        public Func<BrowserScreenProfile, float> GetPlaybackRate { get; init; } = _ => 1.0f;
        public Action<BrowserScreenProfile, float> SetPlaybackRate { get; init; } = (_, _) => { };
        public required Func<TPatch, string?> GetPatchUrl { get; init; }
        public required Func<TPatch, bool?> GetPatchPlaybackPaused { get; init; }
        public required Func<TPatch, long?> GetPatchPositionMs { get; init; }
        public required Func<TPatch, bool> GetPatchRestart { get; init; }
        public required Func<TPatch, bool?> GetPatchAutoplay { get; init; }
        public Func<TPatch, bool?> GetPatchLoop { get; init; } = _ => null;
        public Func<TPatch, bool?> GetPatchPlaylistAutoplayNext { get; init; } = _ => null;
        public Func<TPatch, float?> GetPatchPlaybackRate { get; init; } = _ => null;
        public required Func<TPatch, int?> GetPatchBrowserWidth { get; init; }
        public required Action<BrowserScreenProfile, int> SetBrowserWidth { get; init; }
        public required Func<TPatch, int?> GetPatchBrowserHeight { get; init; }
        public required Action<BrowserScreenProfile, int> SetBrowserHeight { get; init; }
        public required Func<TPatch, float?> GetPatchCaptureFps { get; init; }
        public required Action<BrowserScreenProfile, float> SetCaptureFps { get; init; }
        public required Func<TPatch, bool?> GetPatchCaptureFpsManual { get; init; }
        public required Action<BrowserScreenProfile, bool> SetCaptureFpsManual { get; init; }
        public required Func<TPatch, bool?> GetPatchAudioEnabled { get; init; }
        public required Action<BrowserScreenProfile, bool> SetAudioEnabled { get; init; }
        public required Func<TPatch, float?> GetPatchVolume { get; init; }
        public required Action<BrowserScreenProfile, float> SetVolume { get; init; }
        public required Func<TPatch, bool?> GetPatchSpatialAudioEnabled { get; init; }
        public required Func<TPatch, float?> GetPatchSpatialAudioFullVolumeRadiusMeters { get; init; }
        public required Func<TPatch, float?> GetPatchSpatialAudioSilentRadiusMeters { get; init; }
        public required Func<TSource, string> BuildIdentity { get; init; }
        public required Action<ScreenIpcSourceStateResponse, BrowserScreenProfile, ScreenSourceState, ScreenPlaybackStateDto> PopulateSourceStateDto { get; init; }
        public required Func<BrowserScreenProfile?, object?[]> BuildFingerprintParts { get; init; }

        public bool HasPatch(ScreenIpcMutationRequest request)
        {
            return GetMutationPatch(request) != null;
        }

        public bool HasPatch(ScreenIpcSourceUpdateRequest request)
        {
            return GetSourceUpdatePatch(request) != null;
        }

        public bool ApplyPatch(BrowserScreenProfile screen, ScreenIpcMutationRequest request, out string error)
        {
            return ApplyPatch(screen, GetMutationPatch(request), out error);
        }

        public bool ApplyPatch(BrowserScreenProfile screen, ScreenIpcSourceUpdateRequest request, out string error)
        {
            return ApplyPatch(screen, GetSourceUpdatePatch(request), out error);
        }

        public void ApplyRuntimeControls(WorldScreenManager renderer, BrowserScreenProfile screen, ScreenIpcMutationRequest request)
        {
            ApplyRuntimeControls(renderer, screen, GetMutationPatch(request));
        }

        public void ApplyRuntimeControls(WorldScreenManager renderer, BrowserScreenProfile screen, ScreenIpcSourceUpdateRequest request)
        {
            ApplyRuntimeControls(renderer, screen, GetSourceUpdatePatch(request));
        }

        public void AddChangeKinds(List<ScreenIpcChangeKind> changes, ScreenIpcMutationRequest request)
        {
            AddChangeKinds(changes, GetMutationPatch(request));
        }

        public void AddChangeKinds(List<ScreenIpcChangeKind> changes, ScreenIpcSourceUpdateRequest request)
        {
            AddChangeKinds(changes, GetSourceUpdatePatch(request));
        }

        public ScreenPlaybackStateDto BuildPlaybackState(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry)
        {
            var loop = GetLoop(screen);
            var playlistAutoplayNext = GetPlaylistAutoplayNext(screen);
            if (telemetry != null)
            {
                return new ScreenPlaybackStateDto
                {
                    State = screen.PlaybackPaused ? ScreenPlaybackState.Paused : telemetry.State,
                    PositionMs = telemetry.PositionMs,
                    DurationMs = telemetry.DurationMs,
                    Rate = telemetry.Rate,
                    Loop = loop,
                    PlaylistAutoplayNext = playlistAutoplayNext,
                    HostTimestampUnixMs = telemetry.HostTimestampUnixMs,
                };
            }

            return new ScreenPlaybackStateDto
            {
                State = screen.PlaybackPaused ? ScreenPlaybackState.Paused : ScreenPlaybackState.Playing,
                PositionMs = 0,
                Rate = GetPlaybackRate(screen),
                Loop = loop,
                PlaylistAutoplayNext = playlistAutoplayNext,
                HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }

        public ScreenSourceState BuildSourceState(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry)
        {
            if (!TryParse(GetUrl(screen), out var source))
            {
                return new ScreenSourceState
                {
                    Kind = ScreenSourceKind.YouTubeBrowser,
                    Provider = ProviderKind.ToString(),
                    Identity = InvalidIdentity,
                    Title = InvalidTitle,
                };
            }

            var currentVideoId = Descriptor.IsValidVideoId(telemetry?.VideoId ?? string.Empty)
                ? telemetry!.VideoId
                : source.VideoId;
            var canonicalUrl = Descriptor.BuildCanonicalSourceUrl(source, currentVideoId);
            return new ScreenSourceState
            {
                Kind = ScreenSourceKind.YouTubeBrowser,
                Provider = ProviderKind.ToString(),
                Identity = BuildIdentity(source),
                Title = string.IsNullOrWhiteSpace(telemetry?.Title) ? DefaultTitle : telemetry.Title,
                Hash = string.Empty,
                Url = canonicalUrl,
                VideoId = currentVideoId,
            };
        }

        public void PopulateSourceStateResponse(
            ScreenIpcSourceStateResponse response,
            BrowserScreenProfile screen,
            ScreenSourceState source,
            ScreenPlaybackStateDto playback)
        {
            PopulateSourceStateDto(response, screen, source, playback);
        }

        public void AddFingerprintParts(BrowserScreenProfile? screen, List<object?> parts)
        {
            parts.AddRange(BuildFingerprintParts(screen));
        }

        private bool ApplyPatch(BrowserScreenProfile screen, TPatch? patch, out string error)
        {
            error = string.Empty;
            if (patch == null)
                return true;

            if (GetPatchUrl(patch) != null)
            {
                var url = GetPatchUrl(patch)!.Trim();
                if (!string.IsNullOrWhiteSpace(url) && !TryParse(url, out _))
                {
                    error = InvalidPatchSourceMessage;
                    return false;
                }

                SetUrl(screen, url);
            }

            Apply(GetPatchPlaybackPaused(patch), value => screen.PlaybackPaused = value);
            Apply(GetPatchAutoplay(patch), value => SetAutoplay(screen, value));
            Apply(GetPatchLoop(patch), value => SetLoop(screen, value));
            Apply(GetPatchPlaylistAutoplayNext(patch), value => SetPlaylistAutoplayNext(screen, value));
            Apply(GetPatchPlaybackRate(patch), value => SetPlaybackRate(screen, value));
            Apply(GetPatchBrowserWidth(patch), value => SetBrowserWidth(screen, value));
            Apply(GetPatchBrowserHeight(patch), value => SetBrowserHeight(screen, value));
            Apply(GetPatchCaptureFps(patch), value => SetCaptureFps(screen, value));
            Apply(GetPatchCaptureFpsManual(patch), value => SetCaptureFpsManual(screen, value));
            Apply(GetPatchAudioEnabled(patch), value => SetAudioEnabled(screen, value));
            Apply(GetPatchVolume(patch), value => SetVolume(screen, value));
            Apply(GetPatchSpatialAudioEnabled(patch), value => screen.SpatialAudioEnabled = value);
            Apply(GetPatchSpatialAudioFullVolumeRadiusMeters(patch), value => screen.SpatialAudioFullVolumeRadiusMeters = value);
            Apply(GetPatchSpatialAudioSilentRadiusMeters(patch), value => screen.SpatialAudioSilentRadiusMeters = value);
            return true;
        }

        private void ApplyRuntimeControls(WorldScreenManager renderer, BrowserScreenProfile screen, TPatch? patch)
        {
            if (patch == null)
                return;

            if (GetPatchRestart(patch))
            {
                renderer.TryRestartDynamicSource(screen);
            }
            else if (GetPatchPositionMs(patch).HasValue)
            {
                var seconds = Math.Max(0.0, GetPatchPositionMs(patch)!.Value / 1000.0);
                renderer.TrySeekDynamicSourceTo(screen, seconds);
            }

            if (!GetPatchPlaybackPaused(patch).HasValue)
                return;

            if (GetPatchPlaybackPaused(patch)!.Value)
                renderer.TryPauseDynamicSource(screen);
            else
                renderer.TryPlayDynamicSource(screen);
        }

        private void AddChangeKinds(List<ScreenIpcChangeKind> changes, TPatch? patch)
        {
            if (patch == null)
                return;

            if (GetPatchUrl(patch) != null
                || GetPatchAutoplay(patch).HasValue
                || GetPatchLoop(patch).HasValue
                || GetPatchBrowserWidth(patch).HasValue
                || GetPatchBrowserHeight(patch).HasValue
                || GetPatchCaptureFps(patch).HasValue
                || GetPatchCaptureFpsManual(patch).HasValue)
            {
                changes.Add(ScreenIpcChangeKind.Source);
            }

            if (GetPatchPlaybackPaused(patch).HasValue
                || GetPatchPositionMs(patch).HasValue
                || GetPatchRestart(patch)
                || GetPatchPlaybackRate(patch).HasValue
                || GetPatchPlaylistAutoplayNext(patch).HasValue)
            {
                changes.Add(ScreenIpcChangeKind.Playback);
            }
        }

        private bool TryParse(string url, out TSource source)
        {
            if (Descriptor.TryParse(url, out var parsed) && parsed is TSource typed)
            {
                source = typed;
                return true;
            }

            source = default;
            return false;
        }

        private static void Apply<TValue>(TValue? value, Action<TValue> setter)
            where TValue : struct
        {
            if (value.HasValue)
                setter(value.Value);
        }
    }
}
