using System.Numerics;
using CrystalCast.Playlist;
using CrystalCast.Rooms;
using CrystalCast.Sources;
using CrystalCast.Sources.Providers;
using CrystalCast.Windows.Style;
using CrystalCast.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows.Panels;

/// <summary>
/// The YouTube tab's For You grid: the videos on the user's YouTube home page, read by a hidden browser in the
/// player's profile. Clicking one plays it on the screen; + adds it to the screen's playlist.
/// </summary>
internal sealed partial class YouTubePanel
{
    private const float MinimumCardWidth = 190.0f;
    private const float CardGap = 12.0f;
    private const float CardRowGap = 18.0f;

    private readonly CancellationTokenSource feedCancel = new();
    private readonly Dictionary<string, PlaylistEntry> thumbnailEntries = new(StringComparer.Ordinal);
    private Task<YouTubeFeedRead>? feedTask;
    private IReadOnlyList<YouTubeVideo> videos = [];
    private string feedError = string.Empty;
    /// <summary>The login state the shown videos were read with; a change reads the page again.</summary>
    private bool? feedReadLoggedIn;
    private bool feedRead;
    private string feedNote = string.Empty;

    private bool DrawForYou(BrowserScreenProfile screen)
    {
        UpdateFeed();
        DrawFeedHeader();

        if (feedTask != null && videos.Count == 0)
        {
            Ui.Meta("Loading your YouTube home page…");
            return false;
        }

        if (feedError.Length > 0)
            Ui.ErrorText(feedError);
        else if (feedRead && videos.Count == 0)
            Ui.Meta(LoggedIn == true
                ? "YouTube's page had no videos CrystalCast could read. YouTube may have changed its page; Refresh tries again."
                : "Log in to YouTube to see the videos YouTube picks for you.");

        if (feedNote.Length > 0)
            Ui.Meta(feedNote);
        if (videos.Count > 0)
            Ui.VerticalSpace(8.0f);

        return DrawGrid(screen);
    }

    private void DrawFeedHeader()
    {
        Ui.Heading(videos.Count > 0 ? $"For You · {videos.Count}" : "For You");
        const string refresh = "Refresh";
        ImGui.SameLine(ImGui.GetContentRegionMax().X - Ui.ButtonWidth(refresh));
        if (Ui.Button(refresh, ButtonKind.Ghost, disabled: feedTask != null, tooltip: "Read YouTube's home page again for new picks"))
            StartFeedRead();
    }

    /// <summary>Reads the page once the login state is known, again when it changes, and when Refresh is pressed.</summary>
    private void UpdateFeed()
    {
        if (feedTask is { IsCompleted: true } done)
        {
            feedTask = null;
            feedRead = true;
            if (done.IsCompletedSuccessfully)
            {
                videos = done.Result.Videos;
                readerLoggedIn = done.Result.LoggedIn;
                feedReadLoggedIn = done.Result.LoggedIn;
                feedError = string.Empty;
            }
            else if (!done.IsCanceled)
            {
                feedError = $"Couldn't read YouTube's page: {done.Exception?.GetBaseException().Message}";
            }
        }

        // Read once right away (that also tells whether the profile is logged in, with no player running), and
        // again when the player says the login changed.
        if (feedTask == null && (!feedRead || (playerLoggedIn is { } state && feedReadLoggedIn != null && feedReadLoggedIn != state)))
            StartFeedRead();
    }

    private void StartFeedRead()
    {
        feedRead = true;
        feedNote = string.Empty;
        var token = feedCancel.Token;
        feedTask = Task.Run(async () =>
        {
            var page = await YouTubeHomeReader.ReadAsync(log, token).ConfigureAwait(false);
            return new YouTubeFeedRead(page.Data == null ? [] : YouTubeFeed.Parse(page.Data), page.LoggedIn);
        }, token);
    }

    private sealed record YouTubeFeedRead(IReadOnlyList<YouTubeVideo> Videos, bool LoggedIn);

    private bool DrawGrid(BrowserScreenProfile screen)
    {
        if (videos.Count == 0)
            return false;

        var changed = false;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var gap = S(CardGap);
        var columns = Math.Max(1, (int)((width + gap) / (S(MinimumCardWidth) + gap)));
        var cardWidth = (width - (gap * (columns - 1))) / columns;
        var thumbHeight = MathF.Round(cardWidth * 9.0f / 16.0f);

        float titleLine, smallLine;
        using (Ui.Font(f => f.SemiBold))
            titleLine = ImGui.GetTextLineHeight();
        using (Ui.Font(f => f.Small))
            smallLine = ImGui.GetTextLineHeight();
        var cardHeight = thumbHeight + S(8.0f) + (titleLine * 2.0f) + S(4.0f) + (smallLine * 2.0f);
        var rowStep = cardHeight + S(CardRowGap);

        var playing = YouTubeVideoId.TryParseSource(screen.YouTubeUrl, out var current) ? current.VideoId : string.Empty;
        var locked = SourceControlUi.IsSourceControlsLocked(screen);
        var addBlocked = PlaylistPanel.DescribeBlocks(screen).Add;

        for (var i = 0; i < videos.Count; i++)
        {
            var video = videos[i];
            var pos = start + new Vector2((i % columns) * (cardWidth + gap), (i / columns) * rowStep);
            changed |= DrawCard(screen, video, pos, cardWidth, thumbHeight, titleLine, smallLine, video.VideoId == playing, locked, addBlocked);
        }

        var rows = (videos.Count + columns - 1) / columns;
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, (rows * rowStep) - S(CardRowGap)));
        return changed;
    }

    private bool DrawCard(
        BrowserScreenProfile screen,
        YouTubeVideo video,
        Vector2 pos,
        float width,
        float thumbHeight,
        float titleLine,
        float smallLine,
        bool playing,
        bool locked,
        string? addBlocked)
    {
        var changed = false;
        var dl = ImGui.GetWindowDrawList();
        var thumbMax = pos + new Vector2(width, thumbHeight);
        var titleTop = thumbMax.Y + S(8.0f);
        var metaTop = titleTop + (titleLine * 2.0f) + S(4.0f);

        // The picture and title play the video; the + below adds it to the playlist.
        ImGui.SetCursorScreenPos(pos);
        var clicked = ImGui.InvisibleButton($"##YouTubeVideo{video.VideoId}", new Vector2(width, metaTop - pos.Y));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetTooltip(locked
                ? $"{video.Title}\nLocked by {SourceControlUi.LockedOwner(screen)}"
                : playing ? $"{video.Title}\nPlaying on this screen" : $"{video.Title}\nClick to play on this screen");
        }

        if (clicked && !locked && !playing)
        {
            screen.ProviderKind = BrowserSourceProviderKind.YouTube;
            BrowserProviderSettings.For(screen).SetUrl(screen, video.Url);
            screen.PlaybackPaused = false;
            changed = true;
        }

        var rounding = S(6.0f);
        dl.AddRectFilled(pos, thumbMax, U32(Inset), rounding);
        if (metadata.GetThumbnail(ThumbnailEntry(video)) is { } image)
            Ui.ImageCover(dl, image, pos, thumbMax, hovered && !locked ? Vector4.One : new Vector4(0.92f, 0.92f, 0.92f, 1.0f), rounding);
        if (playing)
            dl.AddRect(pos, thumbMax, U32(Accent), rounding, ImDrawFlags.None, S(2.0f));

        using (Ui.Font(f => f.Small))
        {
            if (video.IsLive)
                DrawBadge(dl, thumbMax, "LIVE", Error);
            else if (video.Duration.Length > 0)
                DrawBadge(dl, thumbMax, video.Duration, new Vector4(0.0f, 0.0f, 0.0f, 0.8f));
            if (playing)
                DrawBadge(dl, new Vector2(pos.X + ImGui.CalcTextSize("Playing").X + S(22.0f), pos.Y + smallLine + S(16.0f)), "Playing", Accent, OnAccent);
        }

        using (Ui.Font(f => f.SemiBold))
        {
            var lines = WrapTwoLines(video.Title, width);
            for (var line = 0; line < lines.Count; line++)
                dl.AddText(new Vector2(pos.X, titleTop + (line * titleLine)), U32(hovered && !locked ? TitleText : CrystalCastUiTheme.Text), lines[line]);
        }

        var addSize = Ui.FrameHeight;
        var metaWidth = width - addSize - S(6.0f);
        using (Ui.Font(f => f.Small))
        {
            dl.AddText(new Vector2(pos.X, metaTop), U32(TextSecondary), Ui.Ellipsize(video.Channel, metaWidth));
            var details = string.Join(" · ", new[] { video.Views, video.Age }.Where(part => part.Length > 0));
            dl.AddText(new Vector2(pos.X, metaTop + smallLine), U32(TextTertiary), Ui.Ellipsize(details, metaWidth));
        }

        ImGui.SetCursorScreenPos(new Vector2(pos.X + width - addSize, metaTop + ((smallLine * 2.0f) - addSize) * 0.5f));
        if (Ui.IconButton($"##YouTubeQueue{video.VideoId}", FontAwesomeIcon.Plus, addBlocked ?? "Add to this screen's playlist", disabled: addBlocked != null))
            changed |= AddToPlaylist(screen, video);

        return changed;
    }

    /// <summary>A small label in the thumbnail's bottom-right corner (or wherever <paramref name="bottomRight"/> is).</summary>
    private static void DrawBadge(ImDrawListPtr dl, Vector2 bottomRight, string text, Vector4 background, Vector4? foreground = null)
    {
        var size = ImGui.CalcTextSize(text);
        var padding = S(5.0f, 2.0f);
        var max = bottomRight - S(6.0f, 6.0f);
        var min = max - size - (padding * 2.0f);
        dl.AddRectFilled(min, max, U32(background), S(3.0f));
        dl.AddText(min + padding, U32(foreground ?? new Vector4(1.0f, 1.0f, 1.0f, 1.0f)), text);
    }

    /// <summary>The title on up to two lines, the second shortened with an ellipsis when it's still too long.</summary>
    internal static List<string> WrapTwoLines(string text, float width)
    {
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        var index = 0;
        for (; index < words.Length; index++)
        {
            var candidate = current.Length == 0 ? words[index] : $"{current} {words[index]}";
            if (current.Length > 0 && ImGui.CalcTextSize(candidate).X > width)
                break;
            current = candidate;
        }

        if (index >= words.Length)
        {
            lines.Add(Ui.Ellipsize(current, width));
            return lines;
        }

        lines.Add(Ui.Ellipsize(current, width));
        lines.Add(Ui.Ellipsize(string.Join(' ', words[index..]), width));
        return lines;
    }

    private bool AddToPlaylist(BrowserScreenProfile screen, YouTubeVideo video)
    {
        if (RoomService.IsRoomScreen(screen))
        {
            var roomError = "Rooms aren't available.";
            feedNote = rooms != null && rooms.RequestAdd(screen, video.Url, out roomError)
                ? $"Sent \"{video.Title}\" to the host's playlist."
                : roomError;
            return false;
        }

        if (ScreenPlaylistPolicy.TryCreateEntry(video.Url, BrowserSourceProviderKind.YouTube, addedByIpc: false, string.Empty, null, out var entry, out var error)
            && ScreenPlaylistPolicy.TryAdd(screen, entry, out error))
        {
            feedNote = $"Added \"{video.Title}\" to the playlist.";
            return true;
        }

        feedNote = error;
        return false;
    }

    private PlaylistEntry ThumbnailEntry(YouTubeVideo video)
    {
        if (!thumbnailEntries.TryGetValue(video.VideoId, out var entry))
        {
            if (thumbnailEntries.Count > 300)
                thumbnailEntries.Clear();

            entry = new PlaylistEntry
            {
                ProviderKind = BrowserSourceProviderKind.YouTube,
                Url = video.Url,
                ThumbnailUrl = video.ThumbnailUrl,
            };
            thumbnailEntries[video.VideoId] = entry;
        }

        return entry;
    }
}
