using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using static CrystalCast.Windows.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

internal sealed class BrowserSourceControlsPanel(WorldScreenManager renderer)
{
    private const float LabelWidth = MainLabelWidth;
    private const float LabelGap = MainLabelGap;

    private static readonly IReadOnlyDictionary<BrowserSourceProviderKind, BrowserSourceUiDescriptor> Descriptors =
        new Dictionary<BrowserSourceProviderKind, BrowserSourceUiDescriptor>
        {
            [BrowserSourceProviderKind.YouTube] = CreateYouTubeDescriptor(),
            [BrowserSourceProviderKind.Twitch] = CreateTwitchDescriptor(),
            [BrowserSourceProviderKind.Dailymotion] = CreateDailymotionDescriptor(),
            [BrowserSourceProviderKind.Vimeo] = CreateVimeoDescriptor(),
            [BrowserSourceProviderKind.GenericWeb] = CreateGenericWebDescriptor(),
        };

    private readonly Dictionary<string, BrowserSourceUiState> uiStates = new(StringComparer.Ordinal);

    public bool Draw(BrowserScreenProfile screen)
    {
        var uiState = GetUiState(screen);
        var sourceLocked = SourceControlUi.IsSourceControlsLocked(screen);
        var changed = false;

        if (screen.ProviderKind == BrowserSourceProviderKind.GenericWeb)
        {
            Ui.Banner("GenericWebTrust", BannerKind.Warning, "Generic Web pages are not sandboxed", BrowserNavigationPolicy.GenericWebTrustWarning);
            ImGui.Spacing();
        }

        // Loading a link can switch the provider, so read the descriptor after the link row.
        changed |= DrawLinkControls(screen, uiState, sourceLocked);
        var descriptor = GetDescriptor(screen);
        var capabilities = BrowserSourceProviderRegistry.GetCapabilities(descriptor.ProviderKind);
        DrawBrowserControlsRow(screen, descriptor, capabilities);
        changed |= DrawResolutionPreset(screen, descriptor, sourceLocked);
        changed |= DrawCaptureFps(screen, descriptor);
        changed |= DrawSourceOptions(screen, descriptor, capabilities, sourceLocked);
        return changed;
    }

    public void ClearScreen(string screenId)
    {
        foreach (var key in uiStates.Keys.Where(key => key.StartsWith(screenId + "|", StringComparison.Ordinal)).ToArray())
            uiStates.Remove(key);
    }

    /// <summary>
    /// Provider · resolution · fps, for the status line over the header. While playing, fps is the rate frames
    /// are actually arriving; otherwise it is the target capture rate.
    /// </summary>
    public string DescribeSource(BrowserScreenProfile screen, bool playing)
    {
        var descriptor = GetDescriptor(screen);
        var provider = ProviderName(screen.ProviderKind);
        var resolution = SourceControlUi.ShortResolution(descriptor.GetBrowserWidth(screen), descriptor.GetBrowserHeight(screen));
        var measured = playing ? renderer.GetMeasuredCaptureFps(screen) : 0.0f;
        var fps = measured > 0.0f
            ? MathF.Round(measured)
            : descriptor.GetCaptureFpsManual(screen)
                ? descriptor.GetCaptureFps(screen)
                : renderer.GetDetectedVideoFps(screen) is > 0.0f and var detected ? detected : 60.0f;
        return $"{provider} · {resolution} · {fps:0.#} fps";
    }

    public static bool HasSource(BrowserScreenProfile screen)
    {
        return !string.IsNullOrWhiteSpace(GetDescriptor(screen).GetUrl(screen));
    }

    public static string ProviderName(BrowserSourceProviderKind kind)
    {
        foreach (var option in BrowserSourceProviderRegistry.Options)
        {
            if (option.Kind == kind)
                return option.DisplayName;
        }

        return kind.ToString();
    }

    private static BrowserSourceUiDescriptor GetDescriptor(BrowserScreenProfile screen)
    {
        return Descriptors.TryGetValue(screen.ProviderKind, out var knownDescriptor)
            ? knownDescriptor
            : Descriptors[BrowserSourceProviderKind.YouTube];
    }

    /// <summary>
    /// The link row. The provider comes from the link itself: video sites are matched by their address,
    /// and any other web address opens as a Generic Web page after the user confirms it.
    /// </summary>
    private static bool DrawLinkControls(BrowserScreenProfile screen, BrowserSourceUiState uiState, bool sourceLocked)
    {
        var changed = false;
        var descriptor = GetDescriptor(screen);
        var url = descriptor.GetUrl(screen);
        if (!string.Equals(uiState.UrlDraftSource, url, StringComparison.Ordinal))
        {
            uiState.UrlDraft = url;
            uiState.UrlDraftSource = url;
        }

        var committedSourceValid = descriptor.SourceDescriptor.TryParse(url, out var committedSource);
        var draft = uiState.UrlDraft;

        Ui.RowLabel("Link", LabelWidth, LabelGap);
        var loadWidth = Ui.ButtonWidth("Load");
        var spacing = S(8.0f);
        var fieldWidth = ImGui.GetContentRegionAvail().X - loadWidth - Ui.FrameHeight - (spacing * 2.0f);
        var draftValid = BrowserSourceDetector.TryDetect(draft, screen.ProviderKind, out _);
        var pressedEnter = Ui.TextField(
            "##SourceLink",
            ref draft,
            1024,
            "Paste a YouTube, Twitch, Dailymotion, Vimeo or web link",
            Math.Max(S(80.0f), fieldWidth),
            error: !draftValid && !string.IsNullOrWhiteSpace(draft),
            disabled: sourceLocked,
            flags: ImGuiInputTextFlags.EnterReturnsTrue);
        uiState.UrlDraft = draft;
        draftValid = BrowserSourceDetector.TryDetect(draft, screen.ProviderKind, out var detected);
        var draftInvalid = !draftValid && !string.IsNullOrWhiteSpace(draft);
        var draftIsNew = draftValid
            && (detected.ProviderKind != screen.ProviderKind || !string.Equals(detected.Url, url, StringComparison.Ordinal));

        // A pending Generic Web confirmation only applies to the link it was raised for.
        if (uiState.ConfirmGenericWebUrl != null
            && (!draftValid || !detected.IsGenericWebFallback || detected.Url != uiState.ConfirmGenericWebUrl))
        {
            uiState.ConfirmGenericWebUrl = null;
        }

        ImGui.SameLine(0.0f, spacing);
        if ((Ui.Button("Load##SourceLinkLoad", ButtonKind.Primary, disabled: sourceLocked || !draftValid) || pressedEnter)
            && draftValid
            && !sourceLocked)
        {
            if (detected.IsGenericWebFallback && screen.ProviderKind != BrowserSourceProviderKind.GenericWeb)
                uiState.ConfirmGenericWebUrl = detected.Url;
            else
                changed |= ApplyDetectedSource(screen, uiState, detected);
        }

        ImGui.SameLine(0.0f, spacing);
        var canCopyLink = !string.IsNullOrWhiteSpace(url);
        if (Ui.IconButton("##SourceLinkCopy", FontAwesomeIcon.Copy, "Copy link", disabled: !canCopyLink, ghost: false))
        {
            ImGui.SetClipboardText(url);
            uiState.SourceCopiedAtTick = Environment.TickCount64;
        }

        Ui.RowIndent(LabelWidth, LabelGap);
        if (sourceLocked)
            Ui.Meta($"Locked by {SourceControlUi.LockedOwner(screen)}.");
        else if (uiState.SourceCopiedAtTick > 0 && Environment.TickCount64 - uiState.SourceCopiedAtTick < 2000)
            Ui.Meta("Link copied.");
        else if (draftInvalid)
            Ui.ErrorText(DescribeInvalidDraft(draft, screen.ProviderKind));
        else if (draftIsNew && detected.IsGenericWebFallback)
            Ui.Text($"Not a supported video site · opens as a web page ({detected.Source.DisplayName})", Warning, f => f.Small, wrap: true);
        else if (draftIsNew)
            Ui.Meta($"{detected.Source.DisplayName} · press Load to play");
        else if (committedSourceValid)
            Ui.Meta($"Loaded · {committedSource.DisplayName}");
        else
            Ui.Meta("Nothing loaded");

        if (uiState.ConfirmGenericWebUrl != null && !sourceLocked)
            changed |= DrawGenericWebConfirmation(screen, uiState, detected);

        return changed;
    }

    private static bool DrawGenericWebConfirmation(BrowserScreenProfile screen, BrowserSourceUiState uiState, DetectedBrowserSource detected)
    {
        var changed = false;
        ImGui.Spacing();
        if (Ui.Banner(
                "GenericWebConfirm",
                BannerKind.Warning,
                "Open this link as a web page?",
                "It isn't a supported video site. " + BrowserNavigationPolicy.GenericWebTrustWarning,
                "Open as web page"))
        {
            changed = ApplyDetectedSource(screen, uiState, detected);
        }

        if (Ui.Button("Cancel##GenericWebConfirmCancel", ButtonKind.Ghost))
            uiState.ConfirmGenericWebUrl = null;

        ImGui.Spacing();
        return changed;
    }

    private static bool ApplyDetectedSource(BrowserScreenProfile screen, BrowserSourceUiState uiState, DetectedBrowserSource detected)
    {
        screen.ProviderKind = detected.ProviderKind;
        GetDescriptor(screen).SetUrl(screen, detected.Url);
        screen.PlaybackPaused = false;
        uiState.UrlDraft = detected.Url;
        uiState.UrlDraftSource = detected.Url;
        uiState.ConfirmGenericWebUrl = null;
        return true;
    }

    private static string DescribeInvalidDraft(string draft, BrowserSourceProviderKind currentProvider)
    {
        if (BrowserSourceDetector.NormalizeLink(draft.Trim()) != null)
            return "Not a link CrystalCast can play";

        return currentProvider == BrowserSourceProviderKind.GenericWeb
            ? "Paste a full link"
            : $"Not a link or {ProviderName(currentProvider)} ID";
    }

    private void DrawBrowserControlsRow(
        BrowserScreenProfile screen,
        BrowserSourceUiDescriptor descriptor,
        BrowserProviderCapabilities capabilities)
    {
        if (!capabilities.SupportsBrowserControls || !renderer.AreBrowserControlsAvailable(screen))
            return;

        var visible = renderer.AreBrowserControlsVisible(screen);
        Ui.RowLabel("Browser controls", LabelWidth, LabelGap);
        var tooltip = visible
            ? "Return the browser page to capture mode"
            : descriptor.ProviderKind == BrowserSourceProviderKind.YouTube
                ? "Open YouTube's player controls for captions, quality, playback speed, and other settings"
                : "Bring the browser page forward for mouse and keyboard input";
        if (Ui.Button(visible ? $"Hide controls##{descriptor.ProviderKind}BrowserControls" : $"Show controls##{descriptor.ProviderKind}BrowserControls", tooltip: tooltip))
        {
            if (visible)
                renderer.TryHideBrowserControls(screen);
            else
                renderer.TryShowBrowserControls(screen);
        }
    }

    private static bool DrawResolutionPreset(BrowserScreenProfile screen, BrowserSourceUiDescriptor descriptor, bool sourceLocked)
    {
        Ui.RowLabel("Resolution", LabelWidth, LabelGap);
        return SourceControlUi.DrawResolutionPreset(
            $"##{descriptor.ProviderKind}Resolution",
            descriptor.GetBrowserWidth(screen),
            descriptor.GetBrowserHeight(screen),
            (width, height) => descriptor.SetBrowserResolution(screen, width, height),
            sourceLocked);
    }

    private bool DrawCaptureFps(BrowserScreenProfile screen, BrowserSourceUiDescriptor descriptor)
    {
        var changed = false;
        Ui.RowLabel("Capture FPS", LabelWidth, LabelGap);
        var manualFps = descriptor.GetCaptureFpsManual(screen);
        if (Ui.SwitchLabeled($"##{descriptor.ProviderKind}ManualFps", "Manual", ref manualFps))
        {
            descriptor.SetCaptureFpsManual(screen, manualFps);
            changed = true;
        }

        ImGui.SameLine(0.0f, S(12.0f));
        if (descriptor.GetCaptureFpsManual(screen))
        {
            var fps = descriptor.GetCaptureFps(screen);
            if (Ui.DragFloat($"##{descriptor.ProviderKind}Fps", ref fps, 1.0f, 1.0f, 120.0f, "%.0f", S(76.0f)))
            {
                descriptor.SetCaptureFps(screen, Math.Clamp(fps, 1.0f, 120.0f));
                changed = true;
            }

            ImGui.SameLine(0.0f, S(12.0f));
            AlignedMeta("Manual · 1 to 120");
        }
        else
        {
            var detectedFps = renderer.GetDetectedVideoFps(screen);
            var autoFps = detectedFps > 0.0f ? detectedFps : 60.0f;
            var display = $"{autoFps:0.#}";
            Ui.TextField($"##{descriptor.ProviderKind}AutoFps", ref display, 16, width: S(76.0f), disabled: true, mono: true);
            ImGui.SameLine(0.0f, S(12.0f));
            AlignedMeta(detectedFps > 0.0f ? "Auto-detected" : "Default");
        }

        return changed;
    }

    private static bool DrawSourceOptions(
        BrowserScreenProfile screen,
        BrowserSourceUiDescriptor descriptor,
        BrowserProviderCapabilities capabilities,
        bool sourceLocked)
    {
        var changed = false;
        var autoplay = descriptor.GetAutoplay(screen);
        Ui.RowLabel("Autoplay on load", LabelWidth, LabelGap);
        if (Ui.Switch($"##{descriptor.ProviderKind}Autoplay", ref autoplay, sourceLocked))
        {
            descriptor.SetAutoplay(screen, autoplay);
            changed = true;
        }

        if (capabilities.SupportsLoop)
        {
            var loop = descriptor.GetLoop(screen);
            Ui.RowLabel(descriptor.LoopLabel, LabelWidth, LabelGap);
            if (Ui.Switch($"##{descriptor.ProviderKind}Loop", ref loop, sourceLocked))
            {
                descriptor.SetLoop(screen, loop);
                changed = true;
            }
        }

        if (capabilities.SupportsPlaylist
            && descriptor.SourceDescriptor.TryParse(descriptor.GetUrl(screen), out var source)
            && descriptor.ShowPlaylistAutoplayNext(source))
        {
            var playlistAutoplayNext = descriptor.GetPlaylistAutoplayNext(screen);
            Ui.RowLabel("Play next in playlist", LabelWidth, LabelGap);
            if (Ui.Switch($"##{descriptor.ProviderKind}PlaylistNext", ref playlistAutoplayNext, sourceLocked))
            {
                descriptor.SetPlaylistAutoplayNext(screen, playlistAutoplayNext);
                changed = true;
            }
        }

        if (capabilities.SupportsPlaybackRate)
        {
            var rate = descriptor.GetPlaybackRate(screen);
            Ui.RowLabel("Playback rate", LabelWidth, LabelGap);
            if (Ui.Slider(
                    $"##{descriptor.ProviderKind}Rate",
                    ref rate,
                    0.25f,
                    2.0f,
                    value => $"{value:0.00}×",
                    detent: 1.0f,
                    disabled: sourceLocked,
                    exactFormat: "%.2f"))
            {
                descriptor.SetPlaybackRate(screen, MathF.Round(Math.Clamp(rate, 0.25f, 2.0f) * 20.0f) / 20.0f);
                changed = true;
            }
        }

        return changed;
    }

    private static void AlignedMeta(string text)
    {
        Ui.RowText(text, TextTertiary, f => f.Small);
    }

    private BrowserSourceUiState GetUiState(BrowserScreenProfile screen)
    {
        var key = $"{screen.ScreenId}|link";
        if (uiStates.TryGetValue(key, out var state))
            return state;

        var url = GetDescriptor(screen).GetUrl(screen);
        state = new BrowserSourceUiState
        {
            UrlDraft = url,
            UrlDraftSource = url,
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
            LoopLabel = "Loop video",
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
            LoopLabel = string.Empty,
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
            LoopLabel = "Loop video",
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

    private static BrowserSourceUiDescriptor CreateVimeoDescriptor()
    {
        return new BrowserSourceUiDescriptor
        {
            ProviderKind = BrowserSourceProviderKind.Vimeo,
            SourceDescriptor = BrowserSourceDescriptors.Vimeo,
            LoopLabel = "Loop video",
            RequireDurationForSeek = true,
            GetUrl = screen => screen.VimeoUrl,
            SetUrl = (screen, value) => screen.VimeoUrl = value,
            GetBrowserWidth = screen => screen.VimeoBrowserWidth,
            GetBrowserHeight = screen => screen.VimeoBrowserHeight,
            SetBrowserResolution = (screen, width, height) =>
            {
                screen.VimeoBrowserWidth = width;
                screen.VimeoBrowserHeight = height;
            },
            GetCaptureFps = screen => screen.VimeoCaptureFps,
            SetCaptureFps = (screen, value) => screen.VimeoCaptureFps = value,
            GetCaptureFpsManual = screen => screen.VimeoCaptureFpsManual,
            SetCaptureFpsManual = (screen, value) => screen.VimeoCaptureFpsManual = value,
            GetAutoplay = screen => screen.VimeoAutoplay,
            SetAutoplay = (screen, value) => screen.VimeoAutoplay = value,
            GetLoop = screen => screen.LoopVimeo,
            SetLoop = (screen, value) => screen.LoopVimeo = value,
            GetPlaybackRate = screen => screen.VimeoPlaybackRate,
            SetPlaybackRate = (screen, value) => screen.VimeoPlaybackRate = value,
        };
    }

    private static BrowserSourceUiDescriptor CreateGenericWebDescriptor()
    {
        return new BrowserSourceUiDescriptor
        {
            ProviderKind = BrowserSourceProviderKind.GenericWeb,
            SourceDescriptor = BrowserSourceDescriptors.GenericWeb,
            LoopLabel = "Loop media",
            RequireDurationForSeek = true,
            GetUrl = screen => screen.GenericWebUrl,
            SetUrl = (screen, value) => screen.GenericWebUrl = value,
            GetBrowserWidth = screen => screen.GenericWebBrowserWidth,
            GetBrowserHeight = screen => screen.GenericWebBrowserHeight,
            SetBrowserResolution = (screen, width, height) =>
            {
                screen.GenericWebBrowserWidth = width;
                screen.GenericWebBrowserHeight = height;
            },
            GetCaptureFps = screen => screen.GenericWebCaptureFps,
            SetCaptureFps = (screen, value) => screen.GenericWebCaptureFps = value,
            GetCaptureFpsManual = screen => screen.GenericWebCaptureFpsManual,
            SetCaptureFpsManual = (screen, value) => screen.GenericWebCaptureFpsManual = value,
            GetAutoplay = screen => screen.GenericWebAutoplay,
            SetAutoplay = (screen, value) => screen.GenericWebAutoplay = value,
            GetLoop = screen => screen.LoopGenericWeb,
            SetLoop = (screen, value) => screen.LoopGenericWeb = value,
            GetPlaybackRate = screen => screen.GenericWebPlaybackRate,
            SetPlaybackRate = (screen, value) => screen.GenericWebPlaybackRate = value,
        };
    }

    private sealed class BrowserSourceUiState
    {
        public string UrlDraft { get; set; } = string.Empty;
        public string UrlDraftSource { get; set; } = string.Empty;
        public long SourceCopiedAtTick { get; set; }
        public string? ConfirmGenericWebUrl { get; set; }
    }

    private sealed class BrowserSourceUiDescriptor
    {
        public required BrowserSourceProviderKind ProviderKind { get; init; }
        public required BrowserSourceDescriptor SourceDescriptor { get; init; }
        public required string LoopLabel { get; init; }
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
