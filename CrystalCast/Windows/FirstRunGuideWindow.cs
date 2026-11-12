using CrystalCast.Windows.Style;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

internal sealed class FirstRunGuideWindow : CrystalCastWindow, IDisposable
{
    private const int PageCount = 4;

    private readonly Plugin plugin;
    private int currentPage;

    public FirstRunGuideWindow(Plugin plugin)
        : base("Welcome to CrystalCast###CrystalCastFirstRunGuide")
    {
        this.plugin = plugin;
        Size = new Vector2(680, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
    }

    public bool WasShown { get; private set; }

    public void Show()
    {
        currentPage = 0;
        WasShown = true;
        IsOpen = true;
    }

    public void Suspend()
    {
        IsOpen = false;
        WasShown = false;
    }

    public void Complete()
    {
        if (!plugin.Configuration.FirstRunGuideCompleted)
        {
            plugin.Configuration.FirstRunGuideCompleted = true;
            plugin.Configuration.Save();
        }

        IsOpen = false;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        DrawHero();
        DrawProgress();

        var footerHeight = Ui.FrameHeight + (ImGui.GetStyle().ItemSpacing.Y * 2.0f);
        if (ImGui.BeginChild("CrystalCastFirstRunGuideContent", new Vector2(0.0f, -footerHeight), false, ImGuiWindowFlags.None))
        {
            switch (currentPage)
            {
                case 0:
                    DrawGettingStartedPage();
                    break;
                case 1:
                    DrawSourceAndAudioPage();
                    break;
                case 2:
                    DrawPlacementPage();
                    break;
                default:
                    DrawTroubleshootingPage();
                    break;
            }
        }

        ImGui.EndChild();

        ImGui.Spacing();
        DrawNavigation();
    }

    private void DrawHero()
    {
        var (eyebrow, title, subtitle) = currentPage switch
        {
            0 => ("Welcome", "Create your first screen", "Place your screen with a few clicks."),
            1 => ("Playback", "Make the source yours", "Choose a provider, tune playback, and control how the screen sounds."),
            2 => ("Placement", "Put it in the world", "Anchor the screen, shape it, and save layouts you want to reuse."),
            _ => ("Support", "Know where to look", "Quick fixes for invisible output, browser capture, and useful diagnostics."),
        };

        Ui.Hero($"{eyebrow} · step {currentPage + 1} of {PageCount}", title, subtitle);
    }

    private void DrawProgress()
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var gap = S(6.0f);
        var height = S(3.0f);
        var segmentWidth = (width - (gap * (PageCount - 1))) / PageCount;
        var drawList = ImGui.GetWindowDrawList();
        for (var page = 0; page < PageCount; page++)
        {
            var left = start.X + (page * (segmentWidth + gap));
            var color = page == currentPage
                ? Accent
                : page < currentPage
                    ? NeutralFill
                    : Hairline;
            drawList.AddRectFilled(
                new Vector2(left, start.Y),
                new Vector2(left + segmentWidth, start.Y + height),
                U32(color),
                height * 0.5f);
        }

        ImGui.Dummy(new Vector2(width, height));
        Ui.VerticalSpace(20.0f);
    }

    private static void DrawGettingStartedPage()
    {
        Ui.Card(
            "Your three-step setup",
            "Open /crystalcast and pick a screen, or use Add screen.",
            "Paste a YouTube, Twitch, Dailymotion, Vimeo or web link on the Source tab, then press Load. CrystalCast picks the right player from the link.",
            "On the Placement tab, use Place in front of player to bring it into view.");

        Ui.Card(
            "One screen or every screen?",
            "The Screen switch over the artwork turns only the selected screen on or off.",
            "The Plugin switch in the title bar controls all screens and releases their browser processes when turned off.");

        Ui.Card(
            "Quick access",
            "The sliders button in the title bar opens Settings. You can also use /ccsettings at any time.");
    }

    private static void DrawSourceAndAudioPage()
    {
        Ui.Card(
            "Source tab",
            "Provider-specific controls include the link, autoplay, looping, playback rate, resolution, and capture FPS. Play, restart and seek sit under the artwork.",
            "Automatic capture FPS is the best starting point. Higher manual values cost more CPU and GPU time.",
            "Show controls exposes player menus, subtitles, quality, sign-in, and other interactive options.");

        Ui.Card(
            "Audio and distance falloff",
            "Turn on browser audio and set the volume from the Audio tab.",
            "Distance falloff makes the screen behave like a sound source in the world: moving farther away lowers its volume.",
            "Inside the full volume radius you hear the selected volume. Between the two radii it fades smoothly; at the silent radius it is muted.",
            "Turn distance falloff off when you want the same volume everywhere.");

        Ui.Card(
            "Performance tip",
            "Keeping browser controls visible can reduce capture performance. CrystalCast hides them when focus returns to the game.");
    }

    private static void DrawPlacementPage()
    {
        Ui.Card(
            "Choose an anchor",
            "Fixed in place stays where you put it. Follows you travels with your character.",
            "Follows your view also turns with your camera, so it stays in sight.",
            "Start with Place in front of player, then drag the handles on the screen while the Placement tab is open.");

        Ui.Card(
            "Shape, move, and reuse",
            "Width controls size; height follows the source's aspect ratio. Curve bends the panel.",
            "The gizmo lets you move or rotate the screen directly in the world.",
            "Copy captures the complete layout and works even when an IPC-owned screen is locked.",
            "Switch to an editable screen and use Paste to apply it. Undo restores recent edits, and presets save layouts for later.");

        Ui.Card(
            "Zone changes",
            "CrystalCast pauses its own fixed world screens when you change zones so playback does not continue somewhere unexpected.");
    }

    private static void DrawTroubleshootingPage()
    {
        Ui.Card(
            "Choose how the screen is layered",
            "Scene composite places the screen behind everything, including object and player nameplates.",
            "Native overlay places the screen behind everything except nameplates.",
            "If a screen is invisible or does not render correctly, try a different output layer in Settings > Rendering.");

        Ui.Card(
            "Blank or unstable browser capture?",
            "Settings > Browser defaults to Auto. Try JPEG capture or window capture explicitly if needed.",
            "Wine always uses JPEG capture after WebView2 has been installed in the correct prefix.");

        Ui.Card(
            "Asking for help",
            "Settings > Diagnostics > Copy full diagnostics captures renderer, browser, output-layer, and per-screen details that screenshots often miss.");
    }

    private void DrawNavigation()
    {
        if (Ui.Button("Skip guide", ButtonKind.Ghost))
        {
            Complete();
            return;
        }

        var finalPage = currentPage == PageCount - 1;
        var pageText = $"{currentPage + 1} / {PageCount}";
        float pageWidth;
        using (Ui.Font(f => f.Mono))
            pageWidth = ImGui.CalcTextSize(pageText).X;
        var nextWidth = finalPage ? Ui.ButtonWidth("Finish") : Ui.ButtonWidth("Next");
        var spacing = S(8.0f);
        var navigationWidth = Ui.FrameHeight + pageWidth + nextWidth + (spacing * 2.0f);

        ImGui.SameLine(Math.Max(0.0f, ImGui.GetContentRegionMax().X - navigationWidth));
        if (Ui.IconButton("##CrystalCastGuidePrevious", FontAwesomeIcon.ChevronLeft, "Previous page", disabled: currentPage == 0))
            currentPage--;

        ImGui.SameLine(0.0f, spacing);
        using (Ui.Font(f => f.Mono))
        {
            var pos = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddText(new Vector2(pos.X, pos.Y + ((Ui.FrameHeight - ImGui.GetTextLineHeight()) * 0.5f)), U32(TextSecondary), pageText);
            ImGui.Dummy(new Vector2(pageWidth, Ui.FrameHeight));
        }

        ImGui.SameLine(0.0f, spacing);
        if (finalPage)
        {
            if (Ui.Button("Finish", ButtonKind.Primary))
                Complete();
        }
        else if (Ui.Button("Next", ButtonKind.Primary))
        {
            currentPage++;
        }
    }
}

internal static class FirstRunGuidePolicy
{
    public static bool ShouldShow(bool completed, bool pluginEnabled, bool prerequisiteWindowOpen)
    {
        return !completed && pluginEnabled && !prerequisiteWindowOpen;
    }
}
