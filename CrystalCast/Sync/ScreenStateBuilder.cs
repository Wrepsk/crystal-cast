using System.Globalization;
using System.Security.Cryptography;
using CrystalCast.Rendering;
using CrystalCast.Video;

namespace CrystalCast.Sync;

internal sealed class ScreenStateBuilder
{
    private readonly Configuration configuration;
    private readonly WorldScreenManager renderer;

    public ScreenStateBuilder(Configuration configuration, WorldScreenManager renderer)
    {
        this.configuration = configuration;
        this.renderer = renderer;
    }

    public ScreenStateEnvelope BuildLocalState()
    {
        return BuildLocalStates().FirstOrDefault() ?? new ScreenStateEnvelope
        {
            OwnerSessionId = configuration.OwnerSessionId,
            TerritoryId = (ushort)Plugin.ClientState.TerritoryType,
        };
    }

    public IEnumerable<ScreenStateEnvelope> BuildLocalStates()
    {
        configuration.Normalize();
        if (!configuration.Enabled)
            return [];

        if (configuration.SourceKind == ScreenSourceKind.YouTubeBrowser)
        {
            var enabledScreens = configuration.BrowserScreens
                .Take(Configuration.MaxRenderableBrowserScreens)
                .Where(screen => screen.Enabled)
                .ToArray();

            var states = new List<ScreenStateEnvelope>();
            foreach (var screen in enabledScreens)
            {
                if (TryBuildBrowserScreenState(screen, out var state))
                    states.Add(state);
            }

            return states;
        }

        return TryBuildLocalVideoState(out var localState) ? [localState] : [];
    }

    public bool TryBuildLocalVideoState(out ScreenStateEnvelope state)
    {
        var placement = configuration.GetLocalVideoPlacement();
        if (!TryResolveForIpc(placement, out var resolved))
        {
            state = null!;
            return false;
        }

        state = BuildLocalVideoState(placement, resolved);
        return true;
    }

    public ScreenStateEnvelope BuildLocalVideoState(ScreenPlacementSettings placement, ResolvedScreenPlacement resolved)
    {
        var source = BuildLocalVideoSourceState();
        var rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(
            resolved.YawRadians,
            resolved.PitchRadians,
            resolved.RollRadians);

        return new ScreenStateEnvelope
        {
            SchemaVersion = 1,
            ScreenId = configuration.ScreenId,
            OwnerSessionId = configuration.OwnerSessionId,
            TerritoryId = (ushort)Plugin.ClientState.TerritoryType,
            Position = Vector3Dto.FromVector3(resolved.Position),
            Rotation = QuaternionDto.FromQuaternion(System.Numerics.Quaternion.Normalize(rotation)),
            SizeMeters = new Vector2Dto(placement.WidthMeters, placement.HeightMeters),
            Source = source,
            Playback = BuildLocalPlaybackState(),
            Visual = new ScreenVisualState
            {
                OccludedAlpha = placement.OccludedAlpha,
                OcclusionTolerance = placement.OcclusionTolerance,
                ScreenCurveAmountMeters = placement.ScreenCurveAmountMeters,
                DistanceFadeEnabled = placement.EnableDistanceFade,
                FadeStartMeters = placement.FadeStartMeters,
                FadeStopMeters = placement.FadeStopMeters,
            },
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sequence = configuration.LocalSequence,
        };
    }

    public bool TryBuildBrowserScreenState(BrowserScreenProfile screen, out ScreenStateEnvelope state)
    {
        if (!TryResolveForIpc(screen.Placement, out var resolved))
        {
            state = null!;
            return false;
        }

        state = BuildBrowserScreenState(screen, resolved);
        return true;
    }

    public ScreenStateEnvelope BuildBrowserScreenState(BrowserScreenProfile screen, ResolvedScreenPlacement resolved)
    {
        var placement = screen.Placement;
        var rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(
            resolved.YawRadians,
            resolved.PitchRadians,
            resolved.RollRadians);

        return new ScreenStateEnvelope
        {
            SchemaVersion = 1,
            ScreenId = screen.ScreenId,
            OwnerSessionId = configuration.OwnerSessionId,
            TerritoryId = (ushort)Plugin.ClientState.TerritoryType,
            Position = Vector3Dto.FromVector3(resolved.Position),
            Rotation = QuaternionDto.FromQuaternion(System.Numerics.Quaternion.Normalize(rotation)),
            SizeMeters = new Vector2Dto(placement.WidthMeters, placement.HeightMeters),
            Source = BuildBrowserSourceState(screen),
            Playback = BuildBrowserPlaybackState(screen),
            Visual = new ScreenVisualState
            {
                OccludedAlpha = placement.OccludedAlpha,
                OcclusionTolerance = placement.OcclusionTolerance,
                ScreenCurveAmountMeters = placement.ScreenCurveAmountMeters,
                DistanceFadeEnabled = placement.EnableDistanceFade,
                FadeStartMeters = placement.FadeStartMeters,
                FadeStopMeters = placement.FadeStopMeters,
            },
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sequence = screen.LocalSequence,
        };
    }

    public ScreenIpcSourceStateResponse BuildSourceStateResponse(BrowserScreenProfile screen)
    {
        var source = BuildBrowserSourceState(screen);
        var playback = BuildBrowserPlaybackState(screen);
        var youtubeState = screen.ProviderKind == BrowserSourceProviderKind.YouTube
            ? new YouTubeSourceStateDto
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
            }
            : null;
        var twitchState = screen.ProviderKind == BrowserSourceProviderKind.Twitch
            ? new TwitchSourceStateDto
            {
                Url = screen.TwitchUrl,
                CanonicalUrl = source.Url,
                VideoId = source.VideoId,
                ChannelName = GetTwitchChannelName(screen.TwitchUrl),
                Title = source.Title,
                State = playback.State,
                PositionMs = playback.PositionMs,
                DurationMs = playback.DurationMs,
                Rate = playback.Rate,
                HostTimestampUnixMs = playback.HostTimestampUnixMs,
            }
            : null;
        var dailymotionState = screen.ProviderKind == BrowserSourceProviderKind.Dailymotion
            ? new DailymotionSourceStateDto
            {
                Url = screen.DailymotionUrl,
                CanonicalUrl = source.Url,
                VideoId = source.VideoId,
                PlaylistId = GetDailymotionPlaylistId(screen.DailymotionUrl),
                Title = source.Title,
                State = playback.State,
                PositionMs = playback.PositionMs,
                DurationMs = playback.DurationMs,
                Rate = playback.Rate,
                HostTimestampUnixMs = playback.HostTimestampUnixMs,
            }
            : null;

        return new ScreenIpcSourceStateResponse
        {
            Success = true,
            ScreenId = screen.ScreenId,
            Name = screen.Name,
            Enabled = screen.Enabled,
            CreatedByIpc = screen.CreatedByIpc,
            OwnerId = screen.IpcOwnerId,
            SourceControlsLocked = screen.SourceControlsLocked,
            SourceControlsOwnerId = screen.SourceControlsOwnerId,
            SourceKind = ScreenSourceKind.YouTubeBrowser,
            Provider = screen.ProviderKind.ToString(),
            SourceName = renderer.GetSourceName(screen),
            SourceStatus = renderer.GetSourceStatus(screen),
            YouTube = youtubeState,
            Twitch = twitchState,
            Dailymotion = dailymotionState,
        };
    }

    public static bool TryResolveForIpc(ScreenPlacementSettings placement, out ResolvedScreenPlacement resolved)
    {
        if (ScreenPlacementResolver.TryResolve(placement, out resolved))
            return true;

        resolved = default;
        return false;
    }

    private ScreenPlaybackStateDto BuildLocalPlaybackState()
    {
        return new ScreenPlaybackStateDto
        {
            State = configuration.PlaybackPaused ? ScreenPlaybackState.Paused : ScreenPlaybackState.Playing,
            PositionMs = 0,
            Rate = 1.0f,
            Loop = configuration.LoopLocalVideo,
            HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private ScreenPlaybackStateDto BuildBrowserPlaybackState(BrowserScreenProfile screen)
    {
        var telemetry = renderer.GetPlaybackTelemetry(screen);
        var rate = screen.ProviderKind == BrowserSourceProviderKind.YouTube ? screen.YouTubePlaybackRate : 1.0f;
        var loop = screen.ProviderKind switch
        {
            BrowserSourceProviderKind.YouTube => screen.LoopYouTube,
            BrowserSourceProviderKind.Dailymotion => screen.LoopDailymotion,
            _ => false,
        };
        var playlistAutoplayNext = screen.ProviderKind != BrowserSourceProviderKind.YouTube || screen.YouTubePlaylistAutoplayNext;
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
            Rate = rate,
            Loop = loop,
            PlaylistAutoplayNext = playlistAutoplayNext,
            HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static ScreenSourceState BuildFileSourceState(ScreenSourceKind kind, string path, string fallbackIdentity)
    {
        var title = string.IsNullOrWhiteSpace(path) ? fallbackIdentity : Path.GetFileName(path);
        var hash = TryHashFile(path);
        return new ScreenSourceState
        {
            Kind = kind,
            Identity = string.IsNullOrEmpty(hash) ? fallbackIdentity : $"sha256:{hash}",
            Title = title,
            Hash = hash,
        };
    }

    private ScreenSourceState BuildLocalVideoSourceState()
    {
        var source = BuildFileSourceState(ScreenSourceKind.LocalVideo, configuration.LocalVideoPath, "local-video");
        source.Identity = $"{source.Identity}|scale={configuration.LocalVideoScalePercent.ToString("0.#", CultureInfo.InvariantCulture)}";
        return source;
    }

    private ScreenSourceState BuildBrowserSourceState(BrowserScreenProfile screen)
    {
        return screen.ProviderKind switch
        {
            BrowserSourceProviderKind.YouTube => BuildYouTubeSourceState(screen),
            BrowserSourceProviderKind.Twitch => BuildTwitchSourceState(screen),
            BrowserSourceProviderKind.Dailymotion => BuildDailymotionSourceState(screen),
            _ => new ScreenSourceState
            {
                Kind = ScreenSourceKind.YouTubeBrowser,
                Provider = screen.ProviderKind.ToString(),
                Identity = $"browser:{screen.ProviderKind}",
                Title = $"{screen.ProviderKind} source",
            },
        };
    }

    private ScreenSourceState BuildYouTubeSourceState(BrowserScreenProfile screen)
    {
        var telemetry = renderer.GetPlaybackTelemetry(screen);
        if (!YouTubeVideoId.TryParseSource(screen.YouTubeUrl, out var source))
        {
            return new ScreenSourceState
            {
                Kind = ScreenSourceKind.YouTubeBrowser,
                Provider = BrowserSourceProviderKind.YouTube.ToString(),
                Identity = "youtube:invalid",
                Title = "Invalid YouTube source",
            };
        }

        var currentVideoId = YouTubeVideoId.IsValidVideoId(telemetry?.VideoId ?? string.Empty)
            ? telemetry!.VideoId
            : source.VideoId;
        var canonicalUrl = YouTubeVideoId.BuildCanonicalSourceUrl(source, currentVideoId);
        return new ScreenSourceState
        {
            Kind = ScreenSourceKind.YouTubeBrowser,
            Provider = BrowserSourceProviderKind.YouTube.ToString(),
            Identity = BuildYouTubeSourceIdentity(source),
            Title = string.IsNullOrWhiteSpace(telemetry?.Title) ? "YouTube video" : telemetry.Title,
            Hash = string.Empty,
            Url = canonicalUrl,
            VideoId = currentVideoId,
        };
    }

    private static string BuildYouTubeSourceIdentity(YouTubeSourceReference source)
    {
        return source.Kind switch
        {
            YouTubeSourceKind.Playlist => $"youtube:playlist:{source.PlaylistId}",
            YouTubeSourceKind.LiveChannel => $"youtube:live-channel:{source.LiveChannelId}",
            _ => $"youtube:{source.VideoId}",
        };
    }

    private ScreenSourceState BuildTwitchSourceState(BrowserScreenProfile screen)
    {
        var telemetry = renderer.GetPlaybackTelemetry(screen);
        if (!TwitchVideoId.TryParseSource(screen.TwitchUrl, out var source))
        {
            return new ScreenSourceState
            {
                Kind = ScreenSourceKind.YouTubeBrowser,
                Provider = BrowserSourceProviderKind.Twitch.ToString(),
                Identity = "twitch:invalid",
                Title = "Invalid Twitch source",
            };
        }

        var currentVideoId = TwitchVideoId.IsValidVideoId(telemetry?.VideoId ?? string.Empty)
            ? telemetry!.VideoId
            : source.VideoId;
        var canonicalUrl = TwitchVideoId.BuildCanonicalSourceUrl(source, currentVideoId);
        return new ScreenSourceState
        {
            Kind = ScreenSourceKind.YouTubeBrowser,
            Provider = BrowserSourceProviderKind.Twitch.ToString(),
            Identity = BuildTwitchSourceIdentity(source),
            Title = string.IsNullOrWhiteSpace(telemetry?.Title) ? "Twitch" : telemetry.Title,
            Hash = string.Empty,
            Url = canonicalUrl,
            VideoId = currentVideoId,
        };
    }

    private static string BuildTwitchSourceIdentity(TwitchSourceReference source)
    {
        return source.Kind switch
        {
            TwitchSourceKind.Video => $"twitch:video:{source.VideoId}",
            _ => $"twitch:channel:{source.ChannelName}",
        };
    }

    private ScreenSourceState BuildDailymotionSourceState(BrowserScreenProfile screen)
    {
        var telemetry = renderer.GetPlaybackTelemetry(screen);
        if (!DailymotionVideoId.TryParseSource(screen.DailymotionUrl, out var source))
        {
            return new ScreenSourceState
            {
                Kind = ScreenSourceKind.YouTubeBrowser,
                Provider = BrowserSourceProviderKind.Dailymotion.ToString(),
                Identity = "dailymotion:invalid",
                Title = "Invalid Dailymotion source",
            };
        }

        var currentVideoId = DailymotionVideoId.IsValidVideoId(telemetry?.VideoId ?? string.Empty)
            ? telemetry!.VideoId
            : source.VideoId;
        var canonicalUrl = DailymotionVideoId.BuildCanonicalSourceUrl(source, currentVideoId);
        return new ScreenSourceState
        {
            Kind = ScreenSourceKind.YouTubeBrowser,
            Provider = BrowserSourceProviderKind.Dailymotion.ToString(),
            Identity = BuildDailymotionSourceIdentity(source),
            Title = string.IsNullOrWhiteSpace(telemetry?.Title) ? "Dailymotion" : telemetry.Title,
            Hash = string.Empty,
            Url = canonicalUrl,
            VideoId = currentVideoId,
        };
    }

    private static string BuildDailymotionSourceIdentity(DailymotionSourceReference source)
    {
        return source.Kind switch
        {
            DailymotionSourceKind.Playlist => $"dailymotion:playlist:{source.PlaylistId}",
            _ => $"dailymotion:video:{source.VideoId}",
        };
    }

    private static string GetDailymotionPlaylistId(string url)
    {
        return DailymotionVideoId.TryParseSource(url, out var source) && source.Kind == DailymotionSourceKind.Playlist
            ? source.PlaylistId
            : string.Empty;
    }

    private static string GetTwitchChannelName(string url)
    {
        return TwitchVideoId.TryParseSource(url, out var source) && source.Kind == TwitchSourceKind.Channel
            ? source.ChannelName
            : string.Empty;
    }

    private static string TryHashFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;

            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }
}
