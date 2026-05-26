using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal sealed class BrowserSourceControlsPanel(WorldScreenManager renderer)
{
    private static readonly IReadOnlyDictionary<BrowserSourceProviderKind, BrowserSourceUiDescriptor> Descriptors =
        new Dictionary<BrowserSourceProviderKind, BrowserSourceUiDescriptor>
        {
            [BrowserSourceProviderKind.YouTube] = CreateYouTubeDescriptor(),
            [BrowserSourceProviderKind.Twitch] = CreateTwitchDescriptor(),
            [BrowserSourceProviderKind.Dailymotion] = CreateDailymotionDescriptor(),
        };

    private readonly Dictionary<string, BrowserSourceUiState> uiStates = new(StringComparer.Ordinal);

    public bool Draw(BrowserScreenProfile screen)
    {
        var descriptor = Descriptors.TryGetValue(screen.ProviderKind, out var knownDescriptor)
            ? knownDescriptor
            : Descriptors[BrowserSourceProviderKind.YouTube];
        var uiState = GetUiState(screen, descriptor);
        var changed = false;

        changed |= DrawUrlControls(screen, descriptor, uiState);
        changed |= DrawPlaybackControls(screen, descriptor, uiState);
        changed |= DrawResolutionPreset(screen, descriptor);
        changed |= DrawCaptureFps(screen, descriptor);
        changed |= DrawSourceOptions(screen, descriptor);
        return changed;
    }

    public void ClearScreen(string screenId)
    {
        foreach (var key in uiStates.Keys.Where(key => key.StartsWith(screenId + "|", StringComparison.Ordinal)).ToArray())
            uiStates.Remove(key);
    }

    private static bool DrawUrlControls(BrowserScreenProfile screen, BrowserSourceUiDescriptor descriptor, BrowserSourceUiState uiState)
    {
        var changed = false;
        var sourceLocked = SourceControlUi.IsSourceControlsLocked(screen);
        var url = descriptor.GetUrl(screen);
        if (!string.Equals(uiState.UrlDraftSource, url, StringComparison.Ordinal))
        {
            uiState.UrlDraft = url;
            uiState.UrlDraftSource = url;
        }

        var committedSourceValid = descriptor.SourceDescriptor.TryParse(url, out var committedSource);
        var draft = uiState.UrlDraft;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var loadButtonWidth = ImGui.CalcTextSize("Load").X + (ImGui.GetStyle().FramePadding.X * 2.0f);
        var rowWidth = ImGui.GetContentRegionAvail().X;
        var keepLoadInline = rowWidth >= loadButtonWidth + spacing + 120.0f;
        ImGui.TextUnformatted(descriptor.InputLabel);
        ImGui.SetNextItemWidth(keepLoadInline
            ? Math.Max(120.0f, rowWidth - loadButtonWidth - spacing)
            : Math.Max(120.0f, rowWidth));

        if (sourceLocked)
            ImGui.BeginDisabled();
        var pressedEnter = ImGui.InputText($"##{descriptor.ProviderKind}Url", ref draft, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
        uiState.UrlDraft = draft;
        var draftSourceValid = descriptor.SourceDescriptor.TryParse(uiState.UrlDraft, out var draftSource);

        if (keepLoadInline)
            ImGui.SameLine();
        if (ImGui.Button($"Load##{descriptor.ProviderKind}Load", new Vector2(Math.Min(loadButtonWidth, ImGui.GetContentRegionAvail().X), 0.0f)) || pressedEnter)
        {
            if (draftSourceValid)
            {
                descriptor.SetUrl(screen, uiState.UrlDraft.Trim());
                uiState.UrlDraftSource = descriptor.GetUrl(screen);
                screen.PlaybackPaused = false;
                changed = true;
            }
        }
        if (sourceLocked)
            ImGui.EndDisabled();

        if (sourceLocked)
            SourceControlUi.DrawLockedControlsMessage(screen, "Source controls");

        if (draftSourceValid)
            ImGui.TextDisabled(draftSource.DisplayName);
        else if (!string.IsNullOrWhiteSpace(uiState.UrlDraft))
            ImGui.TextColored(new Vector4(1.0f, 0.45f, 0.35f, 1.0f), descriptor.InvalidText);
        else if (committedSourceValid)
            ImGui.TextDisabled($"Current {committedSource.DisplayName}");
        else
            ImGui.TextDisabled(descriptor.EmptyText);

        return changed;
    }

    private bool DrawPlaybackControls(BrowserScreenProfile screen, BrowserSourceUiDescriptor descriptor, BrowserSourceUiState uiState)
    {
        var changed = false;
        var telemetry = renderer.PlaybackTelemetry;
        var position = telemetry == null
            ? "0:00"
            : SourceControlUi.FormatPlaybackPosition(telemetry.PositionMs);
        var duration = telemetry is { DurationMs: > 0 }
            ? $" / {SourceControlUi.FormatPlaybackPosition(telemetry.DurationMs)}"
            : string.Empty;
        var state = SourceControlUi.GetPlaybackState(screen, telemetry);
        var isPlaying = state == ScreenPlaybackState.Playing;
        var sourceLocked = SourceControlUi.IsSourceControlsLocked(screen);

        ImGui.TextDisabled($"Playback: {state} @ {position}{duration}");

        var buttonSize = ImGui.GetFrameHeight();
        var toggleLabel = isPlaying
            ? $"Pause##{descriptor.ProviderKind}PlayPause"
            : $"Play##{descriptor.ProviderKind}PlayPause";
        if (sourceLocked)
            ImGui.BeginDisabled();
        if (ImGui.Button(toggleLabel, new Vector2(Math.Max(buttonSize * 2.5f, 52.0f), buttonSize)))
        {
            if (isPlaying)
            {
                screen.PlaybackPaused = true;
                renderer.TryPauseDynamicSource();
            }
            else
            {
                screen.PlaybackPaused = false;
                renderer.TryPlayDynamicSource();
            }

            changed = true;
        }
        if (sourceLocked)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(sourceLocked ? "Locked by IPC" : isPlaying ? "Pause" : "Play");

        ImGui.SameLine();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var restartWidth = Math.Max(buttonSize * 3.0f, 68.0f);
        var availableAfterToggle = ImGui.GetContentRegionAvail().X;
        var keepRestartInline = availableAfterToggle >= restartWidth + spacing + 48.0f;
        var progressWidth = keepRestartInline
            ? Math.Max(48.0f, availableAfterToggle - restartWidth - spacing)
            : Math.Max(24.0f, availableAfterToggle);
        changed |= SourceControlUi.DrawProgressBar(
            $"{descriptor.ProviderKind}{screen.ScreenId}",
            uiState.Progress,
            renderer,
            telemetry,
            progressWidth,
            !sourceLocked && (!descriptor.RequireDurationForSeek || telemetry is { DurationMs: > 0 }));

        if (keepRestartInline)
            ImGui.SameLine();
        if (sourceLocked)
            ImGui.BeginDisabled();
        if (ImGui.Button($"Restart##{descriptor.ProviderKind}Restart", new Vector2(Math.Min(restartWidth, ImGui.GetContentRegionAvail().X), buttonSize)))
        {
            screen.PlaybackPaused = false;
            renderer.TryRestartDynamicSource();
            changed = true;
        }
        if (sourceLocked)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(sourceLocked ? "Locked by IPC" : "Restart");

        return changed;
    }

    private static bool DrawResolutionPreset(BrowserScreenProfile screen, BrowserSourceUiDescriptor descriptor)
    {
        return SourceControlUi.DrawResolutionPreset(
            descriptor.GetBrowserWidth(screen),
            descriptor.GetBrowserHeight(screen),
            (width, height) => descriptor.SetBrowserResolution(screen, width, height));
    }

    private bool DrawCaptureFps(BrowserScreenProfile screen, BrowserSourceUiDescriptor descriptor)
    {
        var changed = false;
        var manualFps = descriptor.GetCaptureFpsManual(screen);
        if (ImGui.Checkbox($"Set capture FPS manually##{descriptor.ProviderKind}ManualFps", ref manualFps))
        {
            descriptor.SetCaptureFpsManual(screen, manualFps);
            changed = true;
        }

        if (descriptor.GetCaptureFpsManual(screen))
        {
            var fps = descriptor.GetCaptureFps(screen);
            if (ImGui.InputFloat($"Capture FPS##{descriptor.ProviderKind}Fps", ref fps, 1.0f, 5.0f))
            {
                descriptor.SetCaptureFps(screen, Math.Clamp(fps, 1.0f, 120.0f));
                changed = true;
            }
        }
        else
        {
            var detectedFps = renderer.GetDetectedVideoFps(screen);
            var autoFps = detectedFps > 0.0f ? detectedFps : 60.0f;
            ImGui.TextDisabled($"Capture FPS: {autoFps:0.#} ({(detectedFps > 0.0f ? "auto-detected" : "default")})");
        }

        return changed;
    }

    private static bool DrawSourceOptions(BrowserScreenProfile screen, BrowserSourceUiDescriptor descriptor)
    {
        var changed = false;
        var sourceLocked = SourceControlUi.IsSourceControlsLocked(screen);
        var autoplay = descriptor.GetAutoplay(screen);
        if (sourceLocked)
            ImGui.BeginDisabled();

        if (ImGui.Checkbox($"Autoplay on load##{descriptor.ProviderKind}Autoplay", ref autoplay))
        {
            descriptor.SetAutoplay(screen, autoplay);
            changed = true;
        }

        if (descriptor.SupportsLoop)
        {
            var loop = descriptor.GetLoop(screen);
            if (ImGui.Checkbox($"{descriptor.LoopLabel}##{descriptor.ProviderKind}Loop", ref loop))
            {
                descriptor.SetLoop(screen, loop);
                changed = true;
            }
        }

        if (descriptor.SupportsPlaylistAutoplayNext
            && descriptor.SourceDescriptor.TryParse(descriptor.GetUrl(screen), out var source)
            && descriptor.ShowPlaylistAutoplayNext(source))
        {
            var playlistAutoplayNext = descriptor.GetPlaylistAutoplayNext(screen);
            if (ImGui.Checkbox($"Autoplay next playlist video##{descriptor.ProviderKind}PlaylistNext", ref playlistAutoplayNext))
            {
                descriptor.SetPlaylistAutoplayNext(screen, playlistAutoplayNext);
                changed = true;
            }
        }

        if (descriptor.SupportsPlaybackRate)
        {
            var rate = descriptor.GetPlaybackRate(screen);
            if (ImGui.SliderFloat($"Playback rate##{descriptor.ProviderKind}Rate", ref rate, 0.25f, 2.0f))
            {
                descriptor.SetPlaybackRate(screen, Math.Clamp(rate, 0.25f, 2.0f));
                changed = true;
            }
        }

        if (sourceLocked)
            ImGui.EndDisabled();

        return changed;
    }

    private BrowserSourceUiState GetUiState(BrowserScreenProfile screen, BrowserSourceUiDescriptor descriptor)
    {
        var key = $"{screen.ScreenId}|{descriptor.ProviderKind}";
        if (uiStates.TryGetValue(key, out var state))
            return state;

        state = new BrowserSourceUiState
        {
            UrlDraft = descriptor.GetUrl(screen),
            UrlDraftSource = descriptor.GetUrl(screen),
        };
        uiStates[key] = state;
        return state;
    }

    private static BrowserSourceUiDescriptor CreateYouTubeDescriptor()
    {
        return new BrowserSourceUiDescriptor
        {
            ProviderKind = BrowserSourceProviderKind.YouTube,
            SourceDescriptor = BrowserSourceDescriptors.YouTube,
            InputLabel = "YouTube URL / ID",
            InvalidText = "YouTube source: invalid",
            EmptyText = "YouTube source: empty",
            LoopLabel = "Loop YouTube video",
            SupportsLoop = true,
            SupportsPlaybackRate = true,
            SupportsPlaylistAutoplayNext = true,
            RequireDurationForSeek = false,
            GetUrl = screen => screen.YouTubeUrl,
            SetUrl = (screen, value) => screen.YouTubeUrl = value,
            GetBrowserWidth = screen => screen.YouTubeBrowserWidth,
            GetBrowserHeight = screen => screen.YouTubeBrowserHeight,
            SetBrowserResolution = (screen, width, height) =>
            {
                screen.YouTubeBrowserWidth = width;
                screen.YouTubeBrowserHeight = height;
            },
            GetCaptureFps = screen => screen.YouTubeCaptureFps,
            SetCaptureFps = (screen, value) => screen.YouTubeCaptureFps = value,
            GetCaptureFpsManual = screen => screen.YouTubeCaptureFpsManual,
            SetCaptureFpsManual = (screen, value) => screen.YouTubeCaptureFpsManual = value,
            GetAutoplay = screen => screen.YouTubeAutoplay,
            SetAutoplay = (screen, value) => screen.YouTubeAutoplay = value,
            GetLoop = screen => screen.LoopYouTube,
            SetLoop = (screen, value) => screen.LoopYouTube = value,
            GetPlaylistAutoplayNext = screen => screen.YouTubePlaylistAutoplayNext,
            SetPlaylistAutoplayNext = (screen, value) => screen.YouTubePlaylistAutoplayNext = value,
            ShowPlaylistAutoplayNext = source => source is YouTubeSourceReference { Kind: YouTubeSourceKind.Playlist },
            GetPlaybackRate = screen => screen.YouTubePlaybackRate,
            SetPlaybackRate = (screen, value) => screen.YouTubePlaybackRate = value,
        };
    }

    private static BrowserSourceUiDescriptor CreateTwitchDescriptor()
    {
        return new BrowserSourceUiDescriptor
        {
            ProviderKind = BrowserSourceProviderKind.Twitch,
            SourceDescriptor = BrowserSourceDescriptors.Twitch,
            InputLabel = "Twitch channel / VOD URL",
            InvalidText = "Twitch source: invalid",
            EmptyText = "Twitch source: empty",
            LoopLabel = string.Empty,
            SupportsLoop = false,
            SupportsPlaybackRate = false,
            SupportsPlaylistAutoplayNext = false,
            RequireDurationForSeek = true,
            GetUrl = screen => screen.TwitchUrl,
            SetUrl = (screen, value) => screen.TwitchUrl = value,
            GetBrowserWidth = screen => screen.TwitchBrowserWidth,
            GetBrowserHeight = screen => screen.TwitchBrowserHeight,
            SetBrowserResolution = (screen, width, height) =>
            {
                screen.TwitchBrowserWidth = width;
                screen.TwitchBrowserHeight = height;
            },
            GetCaptureFps = screen => screen.TwitchCaptureFps,
            SetCaptureFps = (screen, value) => screen.TwitchCaptureFps = value,
            GetCaptureFpsManual = screen => screen.TwitchCaptureFpsManual,
            SetCaptureFpsManual = (screen, value) => screen.TwitchCaptureFpsManual = value,
            GetAutoplay = screen => screen.TwitchAutoplay,
            SetAutoplay = (screen, value) => screen.TwitchAutoplay = value,
        };
    }

    private static BrowserSourceUiDescriptor CreateDailymotionDescriptor()
    {
        return new BrowserSourceUiDescriptor
        {
            ProviderKind = BrowserSourceProviderKind.Dailymotion,
            SourceDescriptor = BrowserSourceDescriptors.Dailymotion,
            InputLabel = "Dailymotion URL / ID",
            InvalidText = "Dailymotion source: invalid",
            EmptyText = "Dailymotion source: empty",
            LoopLabel = "Loop Dailymotion video",
            SupportsLoop = true,
            SupportsPlaybackRate = false,
            SupportsPlaylistAutoplayNext = false,
            RequireDurationForSeek = true,
            GetUrl = screen => screen.DailymotionUrl,
            SetUrl = (screen, value) => screen.DailymotionUrl = value,
            GetBrowserWidth = screen => screen.DailymotionBrowserWidth,
            GetBrowserHeight = screen => screen.DailymotionBrowserHeight,
            SetBrowserResolution = (screen, width, height) =>
            {
                screen.DailymotionBrowserWidth = width;
                screen.DailymotionBrowserHeight = height;
            },
            GetCaptureFps = screen => screen.DailymotionCaptureFps,
            SetCaptureFps = (screen, value) => screen.DailymotionCaptureFps = value,
            GetCaptureFpsManual = screen => screen.DailymotionCaptureFpsManual,
            SetCaptureFpsManual = (screen, value) => screen.DailymotionCaptureFpsManual = value,
            GetAutoplay = screen => screen.DailymotionAutoplay,
            SetAutoplay = (screen, value) => screen.DailymotionAutoplay = value,
            GetLoop = screen => screen.LoopDailymotion,
            SetLoop = (screen, value) => screen.LoopDailymotion = value,
        };
    }

    private sealed class BrowserSourceUiState
    {
        public string UrlDraft { get; set; } = string.Empty;
        public string UrlDraftSource { get; set; } = string.Empty;
        public SourceProgressUiState Progress { get; } = new();
    }

    private sealed class BrowserSourceUiDescriptor
    {
        public required BrowserSourceProviderKind ProviderKind { get; init; }
        public required BrowserSourceDescriptor SourceDescriptor { get; init; }
        public required string InputLabel { get; init; }
        public required string InvalidText { get; init; }
        public required string EmptyText { get; init; }
        public required string LoopLabel { get; init; }
        public required bool SupportsLoop { get; init; }
        public required bool SupportsPlaybackRate { get; init; }
        public required bool SupportsPlaylistAutoplayNext { get; init; }
        public required bool RequireDurationForSeek { get; init; }
        public required Func<BrowserScreenProfile, string> GetUrl { get; init; }
        public required Action<BrowserScreenProfile, string> SetUrl { get; init; }
        public required Func<BrowserScreenProfile, int> GetBrowserWidth { get; init; }
        public required Func<BrowserScreenProfile, int> GetBrowserHeight { get; init; }
        public required Action<BrowserScreenProfile, int, int> SetBrowserResolution { get; init; }
        public required Func<BrowserScreenProfile, float> GetCaptureFps { get; init; }
        public required Action<BrowserScreenProfile, float> SetCaptureFps { get; init; }
        public required Func<BrowserScreenProfile, bool> GetCaptureFpsManual { get; init; }
        public required Action<BrowserScreenProfile, bool> SetCaptureFpsManual { get; init; }
        public required Func<BrowserScreenProfile, bool> GetAutoplay { get; init; }
        public required Action<BrowserScreenProfile, bool> SetAutoplay { get; init; }
        public Func<BrowserScreenProfile, bool> GetLoop { get; init; } = _ => false;
        public Action<BrowserScreenProfile, bool> SetLoop { get; init; } = (_, _) => { };
        public Func<BrowserScreenProfile, bool> GetPlaylistAutoplayNext { get; init; } = _ => true;
        public Action<BrowserScreenProfile, bool> SetPlaylistAutoplayNext { get; init; } = (_, _) => { };
        public Func<IBrowserSourceReference, bool> ShowPlaylistAutoplayNext { get; init; } = _ => false;
        public Func<BrowserScreenProfile, float> GetPlaybackRate { get; init; } = _ => 1.0f;
        public Action<BrowserScreenProfile, float> SetPlaybackRate { get; init; } = (_, _) => { };
    }
}
