using CrystalCast.Playlist;
using CrystalCast.Rendering;
using CrystalCast.Rooms;
using CrystalCast.Sources;
using CrystalCast.Sources.Providers;
using CrystalCast.Twitch;
using CrystalCast.Windows.Style;
using Dalamud.Bindings.ImGui;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows.Panels;

/// <summary>
/// The Twitch tab, shown while the selected screen plays Twitch. For a live channel its chat comes first,
/// then the user's Twitch login and the channels they follow that are live. The chat can move to its own
/// window; the tab then says where it went.
/// </summary>
internal sealed partial class TwitchPanel(
    WorldScreenManager renderer,
    TwitchAccount account,
    Configuration configuration,
    PlaylistMetadataService metadata,
    RoomService? rooms,
    TwitchChatView chat,
    TwitchChatWindow chatWindow) : ISourceTab
{
    /// <summary>Space left under the chat so the login row below it peeks into view.</summary>
    private const float BelowChatPeek = 120.0f;
    private const float MaximumChatHeight = 560.0f;

    public string Label => "Twitch";

    public bool AppliesTo(BrowserScreenProfile screen) => IsShown(configuration, TryGetSource(screen, out _));

    public bool IsPlaying(BrowserScreenProfile screen) => TryGetSource(screen, out _);

    /// <summary>
    /// Whether the tab shows: only while the screen plays Twitch, or on every screen when the user turned that
    /// on, and never when the tab is off.
    /// </summary>
    internal static bool IsShown(Configuration configuration, bool playsTwitch)
    {
        return configuration.TwitchTabEnabled && (playsTwitch || configuration.TwitchTabAlwaysVisible);
    }

    public void Dispose() => DisposeAccountTasks();

    public void Update(BrowserScreenProfile? activeScreen)
    {
        UpdateSignIn();
        if (!configuration.TwitchTabEnabled && chatWindow.IsOpen)
            chatWindow.IsOpen = false;
    }

    public bool Draw(BrowserScreenProfile screen)
    {
        TwitchSourceReference? source = TryGetSource(screen, out var playing) ? playing : null;
        if (source is { Kind: TwitchSourceKind.Channel } channel)
        {
            DrawChat(screen, channel.ChannelName);
            Ui.VerticalSpace(24.0f);
        }

        var changed = DrawAccount(screen);
        Ui.VerticalSpace(20.0f);
        changed |= DrawFollowing(screen, source);

        if (source is { Kind: TwitchSourceKind.Video })
        {
            Ui.VerticalSpace(20.0f);
            Ui.SectionHeader("Chat", "This screen plays a Twitch video. Chat is shown for live channels.");
        }
        else if (source == null && account.State != TwitchAccountState.SignedIn)
        {
            Ui.VerticalSpace(20.0f);
            Ui.Meta("Chat shows here while this screen plays a Twitch channel.");
        }

        return changed;
    }

    private void DrawChat(BrowserScreenProfile screen, string channel)
    {
        // One chat at a time: while it's in its own window, the tab points there.
        if (chatWindow.IsOpen)
        {
            var here = chatWindow.Shows(screen.ScreenId);
            Ui.SectionHeader("Chat", here
                ? "The chat is open in its own window."
                : "The chat window is showing another screen's chat.");
            if (!here && Ui.Button("Show this channel there"))
                chatWindow.Open(screen.ScreenId);
            if (!here)
                ImGui.SameLine(0.0f, S(8.0f));
            if (Ui.Button("Show it here"))
                chatWindow.IsOpen = false;
            return;
        }

        // A screen that's off doesn't keep a connection to Twitch.
        if (!TwitchChatView.ShouldConnect(configuration, screen))
        {
            chat.Release();
            Ui.SectionHeader("Chat", TwitchChatView.OffNote(configuration));
            return;
        }

        var height = Math.Clamp(
            ImGui.GetContentRegionAvail().Y - S(BelowChatPeek),
            S(TwitchChatView.MinimumChatHeight),
            S(MaximumChatHeight));
        if (chat.Draw(channel, TwitchChatHeaderButton.PopOut, height))
            chatWindow.Open(screen.ScreenId);
    }

    internal static bool TryGetSource(BrowserScreenProfile screen, out TwitchSourceReference source)
    {
        source = default;
        if (screen.ProviderKind != BrowserSourceProviderKind.Twitch
            || !BrowserSourceDescriptors.Twitch.TryParse(screen.TwitchUrl, out var parsed)
            || parsed is not TwitchSourceReference twitch)
        {
            return false;
        }

        source = twitch;
        return true;
    }
}
