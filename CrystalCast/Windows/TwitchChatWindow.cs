using System.Numerics;
using CrystalCast.Sources.Providers;
using CrystalCast.Windows.Panels;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

/// <summary>
/// A screen's Twitch chat in its own window, opened from the Twitch tab. It stays open without the main
/// window and follows whatever channel that screen plays. It shares its chat with the Twitch tab, which shows
/// a note while the chat is out here.
/// </summary>
internal sealed class TwitchChatWindow : CrystalCastWindow
{
    private readonly Configuration configuration;
    private readonly TwitchChatView view;

    public TwitchChatWindow(Configuration configuration, TwitchChatView view)
        : base("Twitch chat###CrystalCastTwitchChat", ImGuiWindowFlags.NoCollapse)
    {
        this.configuration = configuration;
        this.view = view;
        Size = new Vector2(420.0f, 640.0f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(300.0f, 360.0f) };
    }

    /// <summary>The screen whose chat this window shows.</summary>
    public string ScreenId { get; private set; } = string.Empty;

    /// <summary>True while the window shows the chat of <paramref name="screenId"/>.</summary>
    public bool Shows(string screenId) => IsOpen && ScreenId == screenId;

    public void Open(string screenId)
    {
        ScreenId = screenId;
        IsOpen = true;
        BringToFront();
    }

    protected override Vector2 WindowPadding => S(16.0f, 14.0f);

    public override void PreDraw()
    {
        var channel = TryGetChannel(out var name) ? $" · #{name}" : string.Empty;
        WindowName = $"Twitch chat{channel}###CrystalCastTwitchChat";
        base.PreDraw();
    }

    public override void Draw()
    {
        // Turning the Twitch tab off in Settings closes its chat window too.
        if (!configuration.TwitchTabEnabled)
        {
            IsOpen = false;
            return;
        }

        if (!TryGetChannel(out var channel))
        {
            Ui.Meta(configuration.BrowserScreens.Any(screen => screen.ScreenId == ScreenId)
                ? "This screen isn't playing a Twitch channel right now."
                : "The screen this chat belonged to is gone.");
            ImGui.Spacing();
            if (Ui.Button("Close"))
                IsOpen = false;
            return;
        }

        var screen = configuration.BrowserScreens.First(candidate => candidate.ScreenId == ScreenId);
        if (!TwitchChatView.ShouldConnect(configuration, screen))
        {
            view.Release();
            Ui.Meta(TwitchChatView.OffNote(configuration));
            return;
        }

        if (view.Draw(ScreenId, channel, TwitchChatHeaderButton.PopIn))
            IsOpen = false;
    }

    private bool TryGetChannel(out string channel)
    {
        channel = string.Empty;
        var screen = configuration.BrowserScreens.FirstOrDefault(candidate => candidate.ScreenId == ScreenId);
        if (screen == null
            || !TwitchPanel.TryGetSource(screen, out var source)
            || source.Kind != TwitchSourceKind.Channel)
        {
            return false;
        }

        channel = source.ChannelName;
        return true;
    }
}
