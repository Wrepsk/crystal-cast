using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

internal sealed class FirstRunGuideWindow : Window, IDisposable
{
    private const int PageCount = 4;

    private readonly Plugin plugin;
    private int currentPage;

    public FirstRunGuideWindow(Plugin plugin)
        : base("Welcome to CrystalCast###CrystalCastFirstRunGuide")
    {
        this.plugin = plugin;
        Size = new Vector2(680, 520);
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
        var accent = CrystalCastUiTheme.Accent;
        DrawHero(accent);
        DrawProgress(accent);

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + (ImGui.GetStyle().ItemSpacing.Y * 2.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
        if (ImGui.BeginChild("CrystalCastFirstRunGuideContent", new Vector2(0.0f, -footerHeight), false, ImGuiWindowFlags.None))
        {
            switch (currentPage)
            {
                case 0:
                    DrawGettingStartedPage(accent);
                    break;
                case 1:
                    DrawSourceAndAudioPage(accent);
                    break;
                case 2:
                    DrawPlacementPage(accent);
                    break;
                default:
                    DrawTroubleshootingPage(accent);
                    break;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.Spacing();
        DrawNavigation(accent);
    }

    private void DrawHero(Vector4 accent)
    {
        var (eyebrow, title, subtitle) = currentPage switch
        {
            0 => ("WELCOME", "Create your first screen", "Place your screen with a few clicks."),
            1 => ("PLAYBACK", "Make the source yours", "Choose a provider, tune playback, and control how the screen sounds."),
            2 => ("PLACEMENT", "Put it in the world", "Anchor the screen, shape it, and save layouts you want to reuse."),
            _ => ("SUPPORT", "Know where to look", "Quick fixes for invisible output, browser capture, and useful diagnostics."),
        };

        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(accent.X * 0.11f, accent.Y * 0.11f, accent.Z * 0.11f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.Border, CrystalCastUiTheme.WithAlpha(accent, 0.42f));
        if (ImGui.BeginChild("CrystalCastGuideHero", new Vector2(0.0f, 105.0f), true, ImGuiWindowFlags.None))
        {
            var drawList = ImGui.GetWindowDrawList();
            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            drawList.AddRectFilled(
                windowPos,
                new Vector2(windowPos.X + 5.0f, windowPos.Y + windowSize.Y),
                ImGui.GetColorU32(accent),
                10.0f);

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8.0f);
            ImGui.TextColored(CrystalCastUiTheme.AccentText, $"{eyebrow}  •  STEP {currentPage + 1} OF {PageCount}");
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8.0f);
            ImGui.TextUnformatted(title);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8.0f);
            ImGui.TextDisabled(subtitle);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    private void DrawProgress(Vector4 accent)
    {
        ImGui.Spacing();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        const float gap = 7.0f;
        const float height = 5.0f;
        var segmentWidth = (width - (gap * (PageCount - 1))) / PageCount;
        var drawList = ImGui.GetWindowDrawList();
        for (var page = 0; page < PageCount; page++)
        {
            var left = start.X + (page * (segmentWidth + gap));
            var color = page <= currentPage
                ? CrystalCastUiTheme.WithAlpha(accent, page == currentPage ? 1.0f : 0.48f)
                : new Vector4(0.34f, 0.34f, 0.38f, 0.42f);
            drawList.AddRectFilled(
                new Vector2(left, start.Y),
                new Vector2(left + segmentWidth, start.Y + height),
                ImGui.GetColorU32(color),
                height * 0.5f);
        }

        ImGui.Dummy(new Vector2(width, height));
        ImGui.Spacing();
    }

    private static void DrawGettingStartedPage(Vector4 accent)
    {
        DrawCard(
            "GettingStartedFlow",
            "Your three-step setup",
            accent,
            "Open /crystalcast and choose a screen—or use Add screen.",
            "Select a Screen source, paste its URL under Source settings, then press Load.",
            "Open Placement and use the place-in-front button to bring it into view.");

        DrawCard(
            "GettingStartedControls",
            "One screen or every screen?",
            accent,
            "Screen enabled controls only the selected screen.",
            "Plugin enabled controls all screens and releases their browser processes when turned off.");

        DrawCallout("QUICK ACCESS", "The title-bar cog opens Settings. You can also use /ccsettings at any time.", accent);
    }

    private static void DrawSourceAndAudioPage(Vector4 accent)
    {
        DrawCard(
            "SourcePlayback",
            "Source settings",
            accent,
            "Provider-specific controls include the URL, play/pause, restart, autoplay, looping, playback rate, resolution, and capture FPS.",
            "Automatic capture FPS is the best starting point. Higher manual values cost more CPU and GPU time.",
            "Show browser controls exposes player menus, subtitles, quality, sign-in, and other interactive options.");

        DrawCard(
            "SourceAudio",
            "Audio and distance falloff",
            accent,
            "Enable browser audio and set the selected provider's volume from the Audio tab.",
            "Spatial audio makes the screen behave like a sound source in the world: moving farther from the screen lowers its browser volume.",
            "Inside Full volume radius you hear the selected volume. Between the two radii it fades smoothly; at Silent radius it becomes muted.",
            "Turn Spatial audio off when you want the same volume everywhere, regardless of your distance from the screen.");

        DrawCallout("PERFORMANCE TIP", "Keeping browser controls visible can reduce capture performance. CrystalCast hides them when focus returns to the game.", accent);
    }

    private static void DrawPlacementPage(Vector4 accent)
    {
        DrawCard(
            "PlacementModes",
            "Choose an anchor",
            accent,
            "World stays at a fixed position. Follow player travels with your character.",
            "Follow camera remains positioned relative to the camera heading.",
            "Start with the place-in-front button, then refine position and rotation.");

        DrawCard(
            "PlacementShape",
            "Shape, move, and reuse",
            accent,
            "Width controls size; height is calculated automatically from the browser aspect ratio. Curve amount bends the panel.",
            "The placement gizmo lets you move or rotate the screen directly.",
            "Copy placement captures the complete layout and remains available even when an IPC-owned screen is locked.",
            "Switch to an editable screen and use Paste placement to apply it. Undo restores recent edits, while presets save layouts for later.");

        DrawCallout("ZONE CHANGES", "CrystalCast pauses its own fixed world screens when you change zones so playback does not continue somewhere unexpected.", accent);
    }

    private static void DrawTroubleshootingPage(Vector4 accent)
    {
        DrawCard(
            "TroubleshootingOutput",
            "Choose how the screen is layered",
            accent,
            "Scene composite places the screen behind everything, including object and player nameplates.",
            "Native overlay places the screen behind everything except object and player nameplates.",
            "ImGui overlay places the screen in front of all UI elements.",
            "If a screen is invisible or does not render correctly on your PC, try a different output layer in Settings > Rendering.");

        DrawCard(
            "TroubleshootingBrowser",
            "Blank or unstable browser capture?",
            accent,
            "Settings > Browser defaults to Auto. Try JPEG capture or window capture explicitly if needed.",
            "Wine always uses JPEG capture after WebView2 has been installed in the correct prefix.");

        DrawCallout("ASKING FOR HELP", "Settings > Diagnostics > Copy full diagnostics captures renderer, browser, output-layer, and per-screen details that screenshots often miss.", accent);
    }

    private static void DrawCard(string id, string title, Vector4 accent, params string[] bullets)
    {
        var height = 48.0f + (bullets.Length * 35.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.10f, 0.10f, 0.12f, 0.64f));
        ImGui.PushStyleColor(ImGuiCol.Border, CrystalCastUiTheme.WithAlpha(accent, 0.38f));
        if (ImGui.BeginChild($"CrystalCastGuideCard{id}", new Vector2(0.0f, height), true, ImGuiWindowFlags.None))
        {
            ImGui.TextColored(CrystalCastUiTheme.AccentText, title);
            ImGui.Separator();
            foreach (var bullet in bullets)
                DrawBullet(bullet);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
        ImGui.Spacing();
    }

    private static void DrawCallout(string label, string text, Vector4 accent)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 7.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CrystalCastUiTheme.WithAlpha(accent, 0.10f));
        if (ImGui.BeginChild($"CrystalCastGuideCallout{label}", new Vector2(0.0f, 66.0f), false, ImGuiWindowFlags.None))
        {
            var drawList = ImGui.GetWindowDrawList();
            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            drawList.AddRectFilled(
                windowPos,
                new Vector2(windowPos.X + 4.0f, windowPos.Y + windowSize.Y),
                ImGui.GetColorU32(accent),
                7.0f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 7.0f);
            ImGui.TextColored(CrystalCastUiTheme.AccentText, label);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 7.0f);
            ImGui.TextWrapped(text);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private static void DrawBullet(string text)
    {
        ImGui.TextColored(CrystalCastUiTheme.AccentText, "•");
        ImGui.SameLine();
        ImGui.TextWrapped(text);
    }

    private void DrawNavigation(Vector4 accent)
    {
        if (ImGui.Button("Skip guide"))
        {
            Complete();
            return;
        }

        var style = ImGui.GetStyle();
        var arrowWidth = ImGui.GetFrameHeight();
        var pageText = $"{currentPage + 1} / {PageCount}";
        var pageWidth = ImGui.CalcTextSize(pageText).X;
        var finalPage = currentPage == PageCount - 1;
        var nextWidth = finalPage
            ? ImGui.CalcTextSize("Finish").X + (style.FramePadding.X * 2.0f)
            : arrowWidth;
        var navigationWidth = arrowWidth + pageWidth + nextWidth + (style.ItemSpacing.X * 2.0f);

        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - navigationWidth));

        var canGoBack = currentPage > 0;
        if (!canGoBack)
            ImGui.BeginDisabled();
        if (ImGui.ArrowButton("##CrystalCastGuidePrevious", ImGuiDir.Left))
            currentPage--;
        if (!canGoBack)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Previous page");

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(CrystalCastUiTheme.AccentText, pageText);
        ImGui.SameLine();

        if (finalPage)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, CrystalCastUiTheme.WithAlpha(accent, 0.72f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, CrystalCastUiTheme.AccentHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, CrystalCastUiTheme.AccentActive);
            if (ImGui.Button("Finish", new Vector2(nextWidth, 0.0f)))
                Complete();
            ImGui.PopStyleColor(3);
        }
        else
        {
            if (ImGui.ArrowButton("##CrystalCastGuideNext", ImGuiDir.Right))
                currentPage++;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Next page");
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
