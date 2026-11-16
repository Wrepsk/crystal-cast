namespace CrystalCast.Twitch;

/// <summary>CrystalCast's registered Twitch app.</summary>
internal static class TwitchApp
{
    /// <summary>
    /// The Client ID of CrystalCast's app on dev.twitch.tv, registered as a Public client so device code login
    /// needs no secret. Client IDs are not secret; Twitch shows them to anyone using the app. Empty turns Twitch
    /// login off; reading chat still works.
    /// </summary>
    internal const string ClientId = "4epik280sq2x7e8a76ezsybdiez38d";

    /// <summary>The site Twitch's login and activate pages live on; the player may visit it while logging in.</summary>
    internal const string SignInDomain = "twitch.tv";
}
