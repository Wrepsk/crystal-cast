using CrystalCast.Rendering;
using CrystalCast.Video;

namespace CrystalCast.Sync;

/// <summary>
/// Translates between one provider's settings on a screen and the IPC shapes: applies a
/// <see cref="ScreenSourcePatchDto"/>, and describes the screen's source and playback for state envelopes and
/// <c>Screen.GetSourceState</c>.
/// </summary>
internal sealed class BrowserSourceIpcAdapter
{
    public required BrowserSourceProviderKind ProviderKind { get; init; }
    public required BrowserSourceDescriptor Descriptor { get; init; }
    public required string InvalidPatchSourceMessage { get; init; }
    public required string InvalidIdentity { get; init; }
    public required string InvalidTitle { get; init; }
    public required string DefaultTitle { get; init; }
    public required Func<IBrowserSourceReference, string> BuildIdentity { get; init; }

    /// <summary>Fills the provider's own fields of a source-state answer from the configured link.</summary>
    public Action<ScreenSourceDetailsDto, string> AddProviderDetails { get; init; } = (_, _) => { };

    /// <summary>False when the configured link is left out of the change fingerprint; the source identity then stands in for it.</summary>
    public bool LinkInFingerprint { get; init; } = true;

    private BrowserProviderSettings Settings => BrowserProviderSettings.For(ProviderKind);

    public bool ApplyPatch(BrowserScreenProfile screen, ScreenSourcePatchDto? patch, out string error)
    {
        error = string.Empty;
        if (patch == null)
            return true;

        var settings = Settings;
        if (patch.Url != null)
        {
            var url = patch.Url.Trim();
            if (!string.IsNullOrWhiteSpace(url) && !Descriptor.TryParse(url, out _))
            {
                error = InvalidPatchSourceMessage;
                return false;
            }

            settings.SetUrl(screen, url);

            // A page address set by a plugin needs the user's approval before it loads.
            if (ProviderKind == BrowserSourceProviderKind.GenericWeb)
                screen.GenericWebUrlFromIpc = true;
        }

        Apply(patch.PlaybackPaused, value => screen.PlaybackPaused = value);
        Apply(patch.Autoplay, value => settings.SetAutoplay(screen, value));
        Apply(patch.Loop, value => settings.SetLoop(screen, value));
        Apply(patch.PlaylistAutoplayNext, value => settings.SetPlaylistAutoplayNext(screen, value));
        Apply(patch.PlaybackRate, value => settings.SetPlaybackRate(screen, value));
        Apply(patch.BrowserWidth, value => settings.SetBrowserWidth(screen, value));
        Apply(patch.BrowserHeight, value => settings.SetBrowserHeight(screen, value));
        Apply(patch.CaptureFps, value => settings.SetCaptureFps(screen, value));
        Apply(patch.CaptureFpsManual, value => settings.SetCaptureFpsManual(screen, value));
        Apply(patch.AudioEnabled, value => settings.SetAudioEnabled(screen, value));
        Apply(patch.Volume, value => settings.SetVolume(screen, value));
        Apply(patch.SpatialAudioEnabled, value => screen.SpatialAudioEnabled = value);
        Apply(patch.SpatialAudioFullVolumeRadiusMeters, value => screen.SpatialAudioFullVolumeRadiusMeters = value);
        Apply(patch.SpatialAudioSilentRadiusMeters, value => screen.SpatialAudioSilentRadiusMeters = value);
        return true;
    }

    public ScreenPlaybackStateDto BuildPlaybackState(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry)
    {
        var settings = Settings;
        var loop = settings.GetLoop(screen);
        var playlistAutoplayNext = settings.GetPlaylistAutoplayNext(screen);
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
            Rate = settings.GetPlaybackRate(screen),
            Loop = loop,
            PlaylistAutoplayNext = playlistAutoplayNext,
            HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    public ScreenSourceState BuildSourceState(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry)
    {
        if (!Descriptor.TryParse(Settings.GetUrl(screen), out var source))
        {
            return new ScreenSourceState
            {
                Kind = ScreenSourceKind.Browser,
                Provider = ProviderKind.ToString(),
                Identity = InvalidIdentity,
                Title = InvalidTitle,
            };
        }

        var telemetryVideoId = telemetry?.VideoId ?? string.Empty;
        var currentVideoId = telemetry != null && Descriptor.IsValidVideoId(telemetryVideoId)
            ? telemetryVideoId
            : source.VideoId;
        return new ScreenSourceState
        {
            Kind = ScreenSourceKind.Browser,
            Provider = ProviderKind.ToString(),
            Identity = BuildIdentity(source),
            Title = string.IsNullOrWhiteSpace(telemetry?.Title) ? DefaultTitle : telemetry.Title,
            Hash = string.Empty,
            Url = Descriptor.BuildCanonicalSourceUrl(source, currentVideoId),
            VideoId = currentVideoId,
        };
    }

    /// <summary>The <c>source</c> object of a <c>Screen.GetSourceState</c> answer.</summary>
    public ScreenSourceDetailsDto BuildSourceDetails(BrowserScreenProfile screen, MediaPlaybackTelemetry? telemetry)
    {
        var source = BuildSourceState(screen, telemetry);
        var playback = BuildPlaybackState(screen, telemetry);
        var configuredUrl = Settings.GetUrl(screen);
        var details = new ScreenSourceDetailsDto
        {
            Url = configuredUrl,
            CanonicalUrl = source.Url,
            VideoId = source.VideoId,
            Title = source.Title,
            State = playback.State,
            PositionMs = playback.PositionMs,
            DurationMs = playback.DurationMs,
            Rate = playback.Rate,
            HostTimestampUnixMs = playback.HostTimestampUnixMs,
        };
        AddProviderDetails(details, configuredUrl);
        return details;
    }

    /// <summary>
    /// The settings that count as a <see cref="ScreenIpcChangeKind.Source"/> change when they differ.
    /// Every provider's are included, so a change to a provider the screen is not showing is noticed too.
    /// </summary>
    public void AddFingerprintParts(BrowserScreenProfile? screen, List<object?> parts)
    {
        if (screen == null)
        {
            parts.Add(null);
            return;
        }

        var settings = Settings;
        if (LinkInFingerprint)
            parts.Add(settings.GetUrl(screen));

        parts.Add(settings.GetAutoplay(screen));
        parts.Add(settings.GetLoop(screen));
        parts.Add(settings.GetPlaylistAutoplayNext(screen));
        parts.Add(settings.GetPlaybackRate(screen));
        parts.Add(settings.GetBrowserWidth(screen));
        parts.Add(settings.GetBrowserHeight(screen));
        parts.Add(settings.GetCaptureFps(screen));
        parts.Add(settings.GetCaptureFpsManual(screen));
    }

    private static void Apply<TValue>(TValue? value, Action<TValue> setter)
        where TValue : struct
    {
        if (value.HasValue)
            setter(value.Value);
    }
}

internal static class BrowserSourceIpcAdapters
{
    private static readonly BrowserSourceIpcAdapter[] OrderedAdapters =
    [
        new()
        {
            ProviderKind = BrowserSourceProviderKind.YouTube,
            Descriptor = BrowserSourceDescriptors.YouTube,
            InvalidPatchSourceMessage = "YouTube URL, video ID, playlist, or live channel is invalid.",
            InvalidIdentity = "youtube:invalid",
            InvalidTitle = "Invalid YouTube source",
            DefaultTitle = "YouTube video",
            LinkInFingerprint = false,
            BuildIdentity = reference =>
            {
                var source = (YouTubeSourceReference)reference;
                return source.Kind switch
                {
                    YouTubeSourceKind.Playlist => $"youtube:playlist:{source.PlaylistId}",
                    YouTubeSourceKind.LiveChannel => $"youtube:live-channel:{source.LiveChannelId}",
                    _ => $"youtube:{source.VideoId}",
                };
            },
        },
        new()
        {
            ProviderKind = BrowserSourceProviderKind.Twitch,
            Descriptor = BrowserSourceDescriptors.Twitch,
            InvalidPatchSourceMessage = "Twitch channel or VOD URL is invalid.",
            InvalidIdentity = "twitch:invalid",
            InvalidTitle = "Invalid Twitch source",
            DefaultTitle = "Twitch",
            BuildIdentity = reference =>
            {
                var source = (TwitchSourceReference)reference;
                return source.Kind switch
                {
                    TwitchSourceKind.Video => $"twitch:video:{source.VideoId}",
                    _ => $"twitch:channel:{source.ChannelName}",
                };
            },
            AddProviderDetails = (details, url) =>
            {
                if (TwitchVideoId.TryParseSource(url, out var parsed) && parsed.Kind == TwitchSourceKind.Channel)
                    details.ChannelName = parsed.ChannelName;
            },
        },
        new()
        {
            ProviderKind = BrowserSourceProviderKind.Dailymotion,
            Descriptor = BrowserSourceDescriptors.Dailymotion,
            InvalidPatchSourceMessage = "Dailymotion URL, video ID, or playlist is invalid.",
            InvalidIdentity = "dailymotion:invalid",
            InvalidTitle = "Invalid Dailymotion source",
            DefaultTitle = "Dailymotion",
            BuildIdentity = reference =>
            {
                var source = (DailymotionSourceReference)reference;
                return source.Kind switch
                {
                    DailymotionSourceKind.Playlist => $"dailymotion:playlist:{source.PlaylistId}",
                    _ => $"dailymotion:video:{source.VideoId}",
                };
            },
            AddProviderDetails = (details, url) =>
            {
                if (DailymotionVideoId.TryParseSource(url, out var parsed) && parsed.Kind == DailymotionSourceKind.Playlist)
                    details.PlaylistId = parsed.PlaylistId;
            },
        },
        new()
        {
            ProviderKind = BrowserSourceProviderKind.Vimeo,
            Descriptor = BrowserSourceDescriptors.Vimeo,
            InvalidPatchSourceMessage = "Vimeo URL or video ID is invalid.",
            InvalidIdentity = "vimeo:invalid",
            InvalidTitle = "Invalid Vimeo source",
            DefaultTitle = "Vimeo",
            BuildIdentity = reference =>
            {
                var source = (VimeoSourceReference)reference;
                return string.IsNullOrWhiteSpace(source.Hash)
                    ? $"vimeo:video:{source.VideoId}"
                    : $"vimeo:video:{source.VideoId}:h:{source.Hash}";
            },
            AddProviderDetails = (details, url) =>
            {
                if (VimeoVideoId.TryParseSource(url, out var parsed))
                    details.Hash = parsed.Hash;
            },
        },
        new()
        {
            ProviderKind = BrowserSourceProviderKind.GenericWeb,
            Descriptor = BrowserSourceDescriptors.GenericWeb,
            InvalidPatchSourceMessage = "Generic Web URL is invalid.",
            InvalidIdentity = "generic-web:invalid",
            InvalidTitle = "Invalid Generic Web source",
            DefaultTitle = "Generic Web",
            BuildIdentity = reference => $"generic-web:{((GenericWebSourceReference)reference).Url}",
        },
    ];

    private static readonly IReadOnlyDictionary<BrowserSourceProviderKind, BrowserSourceIpcAdapter> Adapters =
        OrderedAdapters.ToDictionary(adapter => adapter.ProviderKind);

    public static IEnumerable<BrowserSourceIpcAdapter> All => OrderedAdapters;

    public static bool IsSupported(BrowserSourceProviderKind provider)
    {
        return Adapters.ContainsKey(provider);
    }

    /// <summary>The adapter of <paramref name="provider"/>; an unknown provider reads as YouTube.</summary>
    public static BrowserSourceIpcAdapter Get(BrowserSourceProviderKind provider)
    {
        return Adapters.TryGetValue(provider, out var adapter)
            ? adapter
            : Adapters[BrowserSourceProviderKind.YouTube];
    }

    /// <summary>
    /// Carries out the parts of a patch that are commands to the running player and not settings: restart,
    /// seek, and play or pause.
    /// </summary>
    public static void ApplyRuntimeControls(WorldScreenManager renderer, BrowserScreenProfile screen, ScreenSourcePatchDto? patch)
    {
        if (patch == null)
            return;

        if (patch.Restart)
        {
            renderer.TryRestartDynamicSource(screen);
        }
        else if (patch.PositionMs.HasValue)
        {
            var seconds = Math.Max(0.0, patch.PositionMs.Value / 1000.0);
            renderer.TrySeekDynamicSourceTo(screen, seconds);
        }

        if (!patch.PlaybackPaused.HasValue)
            return;

        if (patch.PlaybackPaused.Value)
            renderer.TryPauseDynamicSource(screen);
        else
            renderer.TryPlayDynamicSource(screen);
    }

    public static void AddFingerprintParts(BrowserScreenProfile? screen, List<object?> parts)
    {
        foreach (var adapter in All)
            adapter.AddFingerprintParts(screen, parts);
    }
}
