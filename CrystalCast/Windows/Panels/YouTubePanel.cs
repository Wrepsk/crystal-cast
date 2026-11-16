using CrystalCast.Browser;
using CrystalCast.Playlist;
using CrystalCast.Rendering;
using CrystalCast.Rooms;
using CrystalCast.YouTube;
using Dalamud.Plugin.Services;
using CrystalCast.Sources;
using CrystalCast.Windows.Style;
using Dalamud.Bindings.ImGui;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows.Panels;

/// <summary>
/// The YouTube tab, shown while the selected screen plays YouTube. Logging in uses Google's own sign-in page,
/// shown in the screen's player browser the way Show controls brings it forward, so the player plays as the
/// user (YouTube Premium applies). The plugin never sees the password or the session; it only checks whether
/// the player's browser holds YouTube's login cookie. Below the login, the user's For You videos.
/// </summary>
internal sealed partial class YouTubePanel(
    WorldScreenManager renderer,
    PlaylistMetadataService metadata,
    RoomService? rooms,
    IPluginLog log) : ISourceTab
{
    private const float LabelWidth = MainLabelWidth;
    private const float LabelGap = MainLabelGap;
    private const long LoginCheckMs = 10_000;

    /// <summary>Google's sign-in for YouTube; afterwards Google sends the browser to YouTube's home page.</summary>
    internal const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true"
        + "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3D%252F";

    /// <summary>YouTube's home page: reaching it means the login is done.</summary>
    internal const string FinishUrl = "https://www.youtube.com/";

    /// <summary>A cookie Google sets for YouTube once the account is logged in.</summary>
    internal const string LoginCookie = YouTubeHomeReader.LoginCookie;

    /// <summary>YouTube's own login cookie, set on the last step of the login.</summary>
    internal const string YouTubeLoginCookie = "LOGIN_INFO";

    private const long SignInCheckMs = 2_000;
    /// <summary>How long to wait for YouTube's own cookie once Google's is there, before finishing anyway.</summary>
    private const long YouTubeCookieGraceMs = 6_000;

    internal static readonly BrowserSignIn SignIn = new(SignInUrl, ["google.*", "youtube.com"], FinishUrl);

    private Task<bool?>? loginCheck;
    private long? lastLoginCheckTick;
    /// <summary>What the screen's player browser says; null until checked, or while it isn't running.</summary>
    private bool? playerLoggedIn;

    /// <summary>What the For You reader's browser found, for when no player is running.</summary>
    private bool? readerLoggedIn;

    /// <summary>Logged in to YouTube: the player's answer when it has one, otherwise the reader's.</summary>
    private bool? LoggedIn => playerLoggedIn ?? readerLoggedIn;
    private string? signInScreenId;
    private string note = string.Empty;

    private Task<(bool? Google, bool? YouTube)>? signInCheck;
    private long lastSignInCheckTick;
    private long? googleCookieSeenTick;

    public string Label => "YouTube";

    public bool AppliesTo(BrowserScreenProfile screen) => screen.ProviderKind == BrowserSourceProviderKind.YouTube;

    public void Update(BrowserScreenProfile? activeScreen)
    {
        if (signInScreenId == null || activeScreen?.ScreenId != signInScreenId)
            return;

        // The login window closed (finished, Done, or dismissed): check the login again right away.
        if (!renderer.IsShowingSignIn(activeScreen))
        {
            signInScreenId = null;
            signInCheck = null;
            googleCookieSeenTick = null;
            lastLoginCheckTick = null;
            return;
        }

        CheckSignInFinished(activeScreen);
    }

    /// <summary>
    /// Ends the login once the browser holds the login cookies, whatever page Google is on. Google can show
    /// passkey offers, 2-step verification or account checks after the password, and some of those pages
    /// never move on to YouTube by themselves.
    /// </summary>
    private void CheckSignInFinished(BrowserScreenProfile screen)
    {
        var now = Environment.TickCount64;
        if (signInCheck is { IsCompleted: true } done)
        {
            signInCheck = null;
            var (google, youTube) = done.IsCompletedSuccessfully ? done.Result : (null, null);
            if (google == true)
            {
                googleCookieSeenTick ??= now;
                if (ShouldFinishSignIn(youTube == true, now - googleCookieSeenTick.Value))
                {
                    renderer.EndSignIns();
                    return;
                }
            }
        }

        if (signInCheck == null && now - lastSignInCheckTick > SignInCheckMs)
        {
            lastSignInCheckTick = now;
            signInCheck = CheckCookiesAsync(screen);
        }
    }

    /// <summary>With Google's cookie there: finish once YouTube's own is too, or after a short wait without it.</summary>
    internal static bool ShouldFinishSignIn(bool youTubeCookie, long msSinceGoogleCookie) =>
        youTubeCookie || msSinceGoogleCookie >= YouTubeCookieGraceMs;

    /// <summary>Both checks start here on the game's thread; only the waiting happens in the background.</summary>
    private async Task<(bool? Google, bool? YouTube)> CheckCookiesAsync(BrowserScreenProfile screen)
    {
        var google = renderer.HasBrowserCookieAsync(screen, FinishUrl, LoginCookie);
        var youTube = renderer.HasBrowserCookieAsync(screen, FinishUrl, YouTubeLoginCookie);
        return (await google.ConfigureAwait(false), await youTube.ConfigureAwait(false));
    }

    public void Dispose()
    {
        if (signInScreenId != null)
            renderer.EndSignIns();
        feedCancel.Cancel();
        feedCancel.Dispose();
    }

    public bool Draw(BrowserScreenProfile screen)
    {
        UpdateLoginCheck(screen);
        DrawAccount(screen);
        Ui.VerticalSpace(24.0f);
        return DrawForYou(screen);
    }

    private void UpdateLoginCheck(BrowserScreenProfile screen)
    {
        if (loginCheck is { IsCompleted: true } done)
        {
            loginCheck = null;
            playerLoggedIn = done.IsCompletedSuccessfully ? done.Result : null;
        }

        var now = Environment.TickCount64;
        if (loginCheck == null && signInScreenId == null && (lastLoginCheckTick is not { } last || now - last > LoginCheckMs))
        {
            lastLoginCheckTick = now;
            loginCheck = renderer.HasBrowserCookieAsync(screen, FinishUrl, LoginCookie);
        }
    }

    private void DrawAccount(BrowserScreenProfile screen)
    {
        Ui.RowLabel("Account", LabelWidth, LabelGap);
        if (signInScreenId == screen.ScreenId)
        {
            Ui.RowText("Finish in the YouTube window", CrystalCastUiTheme.Text);
            ImGui.SameLine(0.0f, S(12.0f));
            // Ends the login either way: finished, stuck on a page, or changed your mind.
            if (Ui.Button("Done", tooltip: "Send the window back to the player. Use it if the page stops after you've signed in."))
                renderer.EndSignIns();

            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.Meta("Sign in with Google there. The player comes back on its own when YouTube opens; if a page gets stuck after signing in, press Done.");
            return;
        }

        switch (LoggedIn)
        {
            case true:
                Ui.RowText("Logged in to YouTube", CrystalCastUiTheme.Text);
                ImGui.SameLine(0.0f, S(12.0f));
                if (Ui.Button("Log out", ButtonKind.Ghost, tooltip: "Logs the YouTube player out of your Google account"))
                    SignOut();
                Ui.RowIndent(LabelWidth, LabelGap);
                Ui.Meta("The player plays as you, so YouTube Premium applies.");
                break;

            case false:
                var canShow = renderer.CanShowSignIn(screen);
                if (Ui.Button("Log in to YouTube", ButtonKind.Primary, disabled: !canShow,
                        tooltip: canShow ? null : "Play a video on this screen first"))
                {
                    if (renderer.TryShowSignIn(screen, SignIn))
                    {
                        signInScreenId = screen.ScreenId;
                        note = string.Empty;
                    }
                    else
                    {
                        note = "The player's browser couldn't open Google's sign-in page.";
                    }
                }

                Ui.RowIndent(LabelWidth, LabelGap);
                Ui.Meta(canShow
                    ? "Opens Google's sign-in in this screen's browser, so the player plays as you."
                    : "Google's sign-in opens in this screen's browser, so play a video on this screen first.");
                break;

            default:
                // Neither the player nor the For You reader has answered yet.
                Ui.RowText(loginCheck != null || feedTask != null ? "Checking…" : "Couldn't tell whether you're logged in", TextSecondary);
                break;
        }

        if (note.Length > 0)
        {
            Ui.RowIndent(LabelWidth, LabelGap);
            Ui.Meta(note);
        }
    }

    private void SignOut()
    {
        playerLoggedIn = false;
        readerLoggedIn = false;
        // The account's picks go with it; the page is read again logged out.
        videos = [];
        lastLoginCheckTick = Environment.TickCount64;
        if (renderer.SignOutBrowsers(BrowserSourceProviderKind.YouTube) > 0)
            return;

        // No YouTube player running: its profile folder is free to delete.
        try
        {
            var folder = BrowserProfileManager.GetWebView2UserDataFolder(BrowserSourceProviderKind.YouTube);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // A browser still closing holds it; the next logout tries again.
        }
    }
}
