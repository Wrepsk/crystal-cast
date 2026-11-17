using System.Numerics;
using CrystalCast.Browser;
using CrystalCast.Playlist;
using CrystalCast.Rooms;
using CrystalCast.Sources;
using CrystalCast.Sources.Providers;
using CrystalCast.Twitch;
using CrystalCast.Windows.Style;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Utility;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows.Panels;

/// <summary>The Twitch tab's login, followed channels and message box.</summary>
internal sealed partial class TwitchPanel
{
    private const float LabelWidth = MainLabelWidth;
    private const float LabelGap = MainLabelGap;
    private const long FollowingRefreshMs = 60_000;
    private const int FollowingShownCollapsed = 5;

    private readonly CancellationTokenSource accountCancel = new();
    private readonly Dictionary<string, PlaylistEntry> previewEntries = new(StringComparer.Ordinal);

    /// <summary>The screen whose browser shows Twitch's login page, while it does.</summary>
    private string? signInScreenId;
    /// <summary>Set when Log in is pressed; the browser opens once Twitch sends the code.</summary>
    private BrowserScreenProfile? signInRequestedFor;
    private string signInNote = string.Empty;

    /// <summary>A cookie twitch.tv sets only for a logged-in account.</summary>
    internal const string PlayerLoginCookie = "auth-token";
    private const string PlayerLoginCookieUrl = "https://www.twitch.tv/";
    private const long PlayerLoginCheckMs = 10_000;

    /// <summary>
    /// Whether the Twitch player's browser is logged in, from its login cookie (only its presence is checked).
    /// All Twitch screens share one profile, so the last answer stays while the screen plays something else.
    /// Null until a Twitch player has been asked.
    /// </summary>
    private bool? playerLoggedIn;
    private Task<bool?>? playerLoginCheck;
    private long? lastPlayerLoginCheckTick;

    private const string OwnBrowserNote =
        "Your own browser logs in CrystalCast only: chat and Following work, but the Twitch player stays logged out, "
        + "so your subscriptions and Turbo won't apply in it. The Twitch window logs in both.";

    private Task<IReadOnlyList<TwitchStream>>? followingTask;
    private IReadOnlyList<TwitchStream> following = [];
    private string followingError = string.Empty;
    private long? followingLoadedTick;
    private string followingUser = string.Empty;
    private bool showAllFollowing;
    private string playlistNote = string.Empty;

    private void DisposeAccountTasks()
    {
        accountCancel.Cancel();
        if (signInScreenId != null)
            renderer.EndSignIns();
        accountCancel.Dispose();
    }

    // ---------------------------------------------------------------- login

    /// <summary>
    /// Opens Twitch's activate page in the player once the code arrives, and returns the player to its video
    /// when the login is approved, cancelled or fails.
    /// </summary>
    private void UpdateSignIn()
    {
        var state = account.State;
        if (signInRequestedFor is { } screen && account.PendingCode is { } code)
        {
            signInRequestedFor = null;
            if (renderer.TryShowSignIn(screen, TwitchApp.SignIn(code.VerificationUri)))
            {
                signInScreenId = screen.ScreenId;
                signInNote = string.Empty;
            }
            else
            {
                signInNote = "The player's browser can't show Twitch's page here, so open it in your own browser.";
            }
        }

        if (state is not (TwitchAccountState.Starting or TwitchAccountState.WaitingForApproval))
        {
            signInRequestedFor = null;
            if (signInScreenId != null)
            {
                renderer.EndSignIns();
                signInScreenId = null;
                // The player may have just logged in: ask again right away.
                lastPlayerLoginCheckTick = null;
            }
        }
    }

    private void UpdatePlayerLoginCheck(BrowserScreenProfile screen)
    {
        if (playerLoginCheck is { IsCompleted: true } done)
        {
            playerLoginCheck = null;
            if (done.IsCompletedSuccessfully && done.Result is { } loggedIn)
                playerLoggedIn = loggedIn;
        }

        // Another site's player has another profile; only a Twitch player can say.
        if (screen.ProviderKind != BrowserSourceProviderKind.Twitch || signInScreenId != null || playerLoginCheck != null)
            return;

        var now = Environment.TickCount64;
        if (lastPlayerLoginCheckTick is not { } last || now - last > PlayerLoginCheckMs)
        {
            lastPlayerLoginCheckTick = now;
            playerLoginCheck = renderer.HasBrowserCookieAsync(screen, PlayerLoginCookieUrl, PlayerLoginCookie);
        }
    }

    /// <summary>The plugin is logged in but the player isn't: the player plays logged out, without the account's perks.</summary>
    internal static bool ShowsPlayerLoggedOutNote(TwitchAccountState state, bool? playerLoggedIn) =>
        state == TwitchAccountState.SignedIn && playerLoggedIn == false;

    private bool DrawAccount(BrowserScreenProfile screen)
    {
        UpdatePlayerLoginCheck(screen);
        Ui.RowLabel("Account", LabelWidth, LabelGap);
        if (!account.Configured)
        {
            Ui.RowText("Not available in this build", TextSecondary);
            return false;
        }

        switch (account.State)
        {
            case TwitchAccountState.SignedIn:
                Ui.RowText($"Logged in as {account.DisplayName}", CrystalCastUiTheme.Text);
                ImGui.SameLine(0.0f, S(12.0f));
                if (Ui.Button("Log out", ButtonKind.Ghost, tooltip: "Logs CrystalCast and the Twitch player out of your account"))
                    SignOut();

                if (ShowsPlayerLoggedOutNote(account.State, playerLoggedIn))
                {
                    Ui.RowIndent(LabelWidth, LabelGap);
                    Ui.Meta("The Twitch player isn't logged in (as after logging in from your own browser), so your subscriptions and Turbo don't apply in it. To change that, log out and log in again with the Twitch window.");
                }

                if (!account.StaysLoggedIn)
                {
                    Ui.RowIndent(LabelWidth, LabelGap);
                    Ui.Meta("This login can't be stored safely on this system, so it ends when the game closes.");
                }

                break;

            case TwitchAccountState.Starting:
                Ui.RowText("Getting a login code from Twitch…", TextSecondary);
                break;

            case TwitchAccountState.WaitingForApproval:
                DrawWaitingForApproval(screen);
                break;

            default:
                if (Ui.Button("Log in to Twitch", ButtonKind.Primary))
                {
                    signInRequestedFor = screen;
                    signInNote = string.Empty;
                    account.BeginSignIn();
                }

                Ui.RowIndent(LabelWidth, LabelGap);
                Ui.Meta("Chat, see who you follow, and watch as yourself, so subscriptions and Turbo apply in the player.");
                if (account.LastError.Length > 0)
                {
                    Ui.RowIndent(LabelWidth, LabelGap);
                    Ui.ErrorText(account.LastError);
                }

                break;
        }

        return false;
    }

    private void DrawWaitingForApproval(BrowserScreenProfile screen)
    {
        var code = account.PendingCode;
        var inBrowser = signInScreenId != null
            && renderer.IsShowingSignIn(screen)
            && renderer.AreBrowserControlsVisible(screen);
        Ui.RowText(inBrowser ? "Finish in the Twitch window" : "Waiting for you to approve", CrystalCastUiTheme.Text);
        ImGui.SameLine(0.0f, S(12.0f));
        // The login window stays open when you click away, so it can end up behind the game.
        if (inBrowser && Ui.Button("Show window", tooltip: "Bring the Twitch window to the front"))
            renderer.TryShowBrowserControls(screen);
        if (inBrowser)
            ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Cancel", ButtonKind.Ghost))
        {
            account.CancelSignIn();
            UpdateSignIn();
            return;
        }

        if (code == null)
            return;

        Ui.RowIndent(LabelWidth, LabelGap);
        Ui.Meta(inBrowser
            ? "Log in if Twitch asks, then press Authorize. The player comes back on its own."
            : $"Open twitch.tv/activate and enter {code.UserCode}, or:");

        Ui.RowIndent(LabelWidth, LabelGap);
        if (!inBrowser && renderer.CanShowSignIn(screen) && Ui.Button("Show the Twitch window"))
        {
            if (renderer.TryShowSignIn(screen, TwitchApp.SignIn(code.VerificationUri)))
            {
                signInScreenId = screen.ScreenId;
            }
        }

        if (!inBrowser && renderer.CanShowSignIn(screen))
            ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Open in my browser", tooltip: "Logs in CrystalCast only; the player stays logged out"))
        {
            Util.OpenLink(code.VerificationUri);
        }

        if (!inBrowser)
        {
            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.Meta(OwnBrowserNote);
        }

        if (signInNote.Length > 0)
        {
            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.Meta(signInNote);
        }
    }

    /// <summary>One logout for both: CrystalCast's token and the player's Twitch cookies.</summary>
    private void SignOut()
    {
        account.SignOut();
        // The player's cookies are cleared too; ask it again once it's back.
        playerLoggedIn = null;
        lastPlayerLoginCheckTick = null;
        following = [];
        followingLoadedTick = null;
        if (renderer.SignOutBrowsers(BrowserSourceProviderKind.Twitch) > 0)
            return;

        // No Twitch player running: its profile folder is free to delete.
        try
        {
            var folder = BrowserProfileManager.GetWebView2UserDataFolder(BrowserSourceProviderKind.Twitch);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // A browser still closing holds it; the cookies stay until the next logout.
        }
    }

    // ---------------------------------------------------------------- following

    private bool DrawFollowing(BrowserScreenProfile screen, TwitchSourceReference? source)
    {
        if (account.State != TwitchAccountState.SignedIn)
            return false;

        RefreshFollowing();
        var count = following.Count;
        Ui.Heading(count > 0 ? $"Following · {count} live" : "Following");
        const string refresh = "Refresh";
        ImGui.SameLine(ImGui.GetContentRegionMax().X - Ui.ButtonWidth(refresh));
        if (Ui.Button(refresh, ButtonKind.Ghost, disabled: followingTask != null, tooltip: "Check again who you follow is live"))
            followingTask = account.GetFollowedLiveAsync(accountCancel.Token);

        if (followingTask != null && followingLoadedTick == null)
            Ui.Meta("Loading…");

        if (followingError.Length > 0)
            Ui.ErrorText(followingError);
        else if (followingLoadedTick != null && count == 0)
            Ui.Meta("Nobody you follow is live right now.");

        var changed = false;
        var shown = showAllFollowing ? following : following.Take(FollowingShownCollapsed);
        foreach (var stream in shown)
            changed |= DrawStream(screen, source, stream);

        if (count > FollowingShownCollapsed)
        {
            if (Ui.Button(showAllFollowing ? "Show fewer" : $"Show all {count}", ButtonKind.Ghost))
                showAllFollowing = !showAllFollowing;
        }

        if (playlistNote.Length > 0)
            Ui.Meta(playlistNote);
        return changed;
    }

    private void RefreshFollowing()
    {
        if (followingUser != account.UserId)
        {
            followingUser = account.UserId;
            following = [];
            followingLoadedTick = null;
        }

        if (followingTask is { IsCompleted: true } done)
        {
            followingTask = null;
            followingLoadedTick = Environment.TickCount64;
            if (done.IsCompletedSuccessfully)
            {
                following = done.Result;
                followingError = string.Empty;
            }
            else
            {
                followingError = $"Couldn't load who you follow: {done.Exception?.GetBaseException().Message}";
            }
        }

        if (followingTask == null
            && (followingLoadedTick is not { } loaded || Environment.TickCount64 - loaded > FollowingRefreshMs))
        {
            followingTask = account.GetFollowedLiveAsync(accountCancel.Token);
        }
    }

    private bool DrawStream(BrowserScreenProfile screen, TwitchSourceReference? source, TwitchStream stream)
    {
        var changed = false;
        var width = ImGui.GetContentRegionAvail().X;
        var thumbSize = S(96.0f, 54.0f);
        var rowHeight = thumbSize.Y + S(8.0f);
        var min = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var watching = source is { Kind: TwitchSourceKind.Channel } playing && playing.ChannelName == stream.Login;

        var thumbMin = min + new Vector2(0.0f, S(4.0f));
        dl.AddRectFilled(thumbMin, thumbMin + thumbSize, U32(Inset), S(4.0f));
        if (metadata.GetThumbnail(PreviewEntry(stream)) is { } image)
            Ui.ImageCover(dl, image, thumbMin, thumbMin + thumbSize, Vector4.One, S(4.0f));

        // Buttons on the right: Watch, then add to the playlist.
        var addWidth = Ui.FrameHeight;
        var watchLabel = watching ? "Watching" : "Watch";
        var watchWidth = Ui.ButtonWidth(watchLabel);
        var buttonsWidth = watchWidth + S(6.0f) + addWidth;
        var textX = thumbMin.X + thumbSize.X + S(12.0f);
        var textWidth = Math.Max(S(40.0f), min.X + width - buttonsWidth - S(12.0f) - textX);

        using (Ui.Font(f => f.SemiBold))
        {
            var lineHeight = ImGui.GetTextLineHeight();
            dl.AddText(new Vector2(textX, thumbMin.Y), U32(CrystalCastUiTheme.Text), Ui.Ellipsize(stream.DisplayName, textWidth));
            using (Ui.Font(f => f.Small))
            {
                var y = thumbMin.Y + lineHeight + S(2.0f);
                var details = stream.GameName.Length > 0
                    ? $"{stream.GameName} · {FormatViewers(stream.ViewerCount)}"
                    : FormatViewers(stream.ViewerCount);
                dl.AddText(new Vector2(textX, y), U32(TextSecondary), Ui.Ellipsize(details, textWidth));
                dl.AddText(new Vector2(textX, y + ImGui.GetTextLineHeight() + S(1.0f)), U32(TextTertiary), Ui.Ellipsize(stream.Title, textWidth));
            }
        }

        var buttonY = min.Y + ((rowHeight - Ui.FrameHeight) * 0.5f);
        var locked = SourceControlUi.IsSourceControlsLocked(screen);
        ImGui.SetCursorScreenPos(new Vector2(min.X + width - buttonsWidth, buttonY));
        if (Ui.Button(
                $"{watchLabel}##TwitchWatch{stream.Login}",
                watching ? ButtonKind.Secondary : ButtonKind.Primary,
                disabled: watching || locked,
                tooltip: locked ? $"Locked by {SourceControlUi.LockedOwner(screen)}" : $"Play {stream.DisplayName} on this screen"))
        {
            // From any screen: a screen playing something else switches to Twitch, like loading a link.
            screen.ProviderKind = BrowserSourceProviderKind.Twitch;
            BrowserProviderSettings.For(screen).SetUrl(screen, ChannelUrl(stream.Login));
            screen.PlaybackPaused = false;
            changed = true;
        }

        ImGui.SameLine(0.0f, S(6.0f));
        var addBlocked = PlaylistPanel.DescribeBlocks(screen).Add;
        if (Ui.IconButton(
                $"##TwitchQueue{stream.Login}",
                FontAwesomeIcon.Plus,
                addBlocked ?? "Add to this screen's playlist",
                disabled: addBlocked != null,
                ghost: false))
        {
            changed |= AddToPlaylist(screen, stream);
        }

        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(new Vector2(width, rowHeight));
        return changed;
    }

    private bool AddToPlaylist(BrowserScreenProfile screen, TwitchStream stream)
    {
        var url = ChannelUrl(stream.Login);
        if (RoomService.IsRoomScreen(screen))
        {
            var roomError = "Rooms aren't available.";
            playlistNote = rooms != null && rooms.RequestAdd(screen, url, out roomError)
                ? $"Sent {stream.DisplayName} to the host's playlist."
                : roomError;
            return false;
        }

        if (ScreenPlaylistPolicy.TryCreateEntry(url, BrowserSourceProviderKind.Twitch, addedByIpc: false, string.Empty, null, out var entry, out var error)
            && ScreenPlaylistPolicy.TryAdd(screen, entry, out error))
        {
            playlistNote = $"Added {stream.DisplayName} to the playlist.";
            return true;
        }

        playlistNote = error;
        return false;
    }

    private PlaylistEntry PreviewEntry(TwitchStream stream)
    {
        if (!previewEntries.TryGetValue(stream.ThumbnailUrl, out var entry))
        {
            if (previewEntries.Count > 200)
                previewEntries.Clear();

            entry = new PlaylistEntry
            {
                ProviderKind = BrowserSourceProviderKind.Twitch,
                Url = ChannelUrl(stream.Login),
                ThumbnailUrl = stream.ThumbnailUrl,
            };
            previewEntries[stream.ThumbnailUrl] = entry;
        }

        return entry;
    }

    private static string ChannelUrl(string login) => $"https://www.twitch.tv/{login}";

    internal static string FormatViewers(int viewers) => viewers switch
    {
        1 => "1 viewer",
        < 1000 => $"{viewers} viewers",
        < 1_000_000 => FormattableString.Invariant($"{viewers / 1000.0:0.#}K viewers"),
        _ => FormattableString.Invariant($"{viewers / 1_000_000.0:0.#}M viewers"),
    };
}
