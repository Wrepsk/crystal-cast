using CrystalCast.Sources;
using CrystalCast.Windows.Panels;

namespace CrystalCast.Tests;

public sealed class YouTubeSignInTests
{
    [Theory]
    [InlineData("https://accounts.google.com/v3/signin/identifier?continue=x", true)]
    [InlineData("https://accounts.youtube.com/accounts/SetSID", true)]
    [InlineData("https://www.youtube.com/signin?action_handle_signin=true", true)]
    [InlineData("https://consent.youtube.com/m", true)]
    [InlineData("http://accounts.google.com/", false)]
    [InlineData("https://google.com.evil.example/", false)]
    [InlineData("https://www.twitch.tv/", false)]
    [InlineData("https://accounts.google.com.tr/accounts/SetSID", true)]
    [InlineData("https://accounts.google.co.uk/accounts/SetSID", true)]
    [InlineData("https://www.google.de/", true)]
    [InlineData("https://google.example.com/", false)]
    [InlineData("https://accounts.google.co.evil/", false)]
    [InlineData("https://notgoogle.com/", false)]
    public void VisitsOnlyGoogleAndYouTubeWhileLoggingIn(string url, bool allowed)
    {
        Assert.Equal(allowed, BrowserSignInPolicy.IsAllowed(url, YouTubePanel.SignIn));
    }

    [Theory]
    [InlineData("https://www.youtube.com/", true)]
    [InlineData("https://www.youtube.com/?app=desktop", true)]
    [InlineData("https://www.youtube.com/signin?next=%2F", false)]
    [InlineData("https://accounts.google.com/", false)]
    public void FinishesOnYouTubesHomePage(string url, bool finished)
    {
        Assert.Equal(finished, BrowserSignInPolicy.IsFinish(url, YouTubePanel.SignIn));
    }

    [Fact]
    public void StartsOnAPageItMayVisit()
    {
        Assert.True(BrowserSignInPolicy.IsAllowed(YouTubePanel.SignInUrl, YouTubePanel.SignIn));
        Assert.False(BrowserSignInPolicy.IsFinish(YouTubePanel.SignInUrl, YouTubePanel.SignIn));
    }

    [Fact]
    public void NoFinishPageMeansItNeverFinishesOnAPage()
    {
        var twitch = CrystalCast.Twitch.TwitchApp.SignIn("https://www.twitch.tv/activate?device-code=X");
        Assert.False(BrowserSignInPolicy.IsFinish("https://www.twitch.tv/", twitch));
    }

    [Theory]
    [InlineData("https://accounts.google.com/signin/challenge?TL=secret#frag", "https://accounts.google.com/signin/challenge")]
    [InlineData("about:blank", "about:")]
    [InlineData("intent://x#Intent;end", "intent:")]
    public void LogsPagesWithoutTheirQuery(string url, string logged)
    {
        Assert.Equal(logged, BrowserSignInPolicy.Describe(url));
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(false, 1000, false)]
    [InlineData(false, 6000, true)]
    public void FinishesOnceTheLoginCookiesAreThere(bool youTubeCookie, long msSinceGoogleCookie, bool finish)
    {
        Assert.Equal(finish, YouTubePanel.ShouldFinishSignIn(youTubeCookie, msSinceGoogleCookie));
    }
}
