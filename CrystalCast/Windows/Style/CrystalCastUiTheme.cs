using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace CrystalCast.Windows;

/// <summary>
/// Design tokens from the CrystalCast UI redesign (Foundations sheet). All sizes are at 100% UI scale;
/// use <see cref="S"/> to apply Dalamud's global scale.
/// </summary>
internal static class CrystalCastUiTheme
{
    // Surfaces
    public static readonly Vector4 Base = Hex(0x111214);
    public static readonly Vector4 Raised = Hex(0x17191C);
    public static readonly Vector4 Inset = Hex(0x0B0C0E);
    public static readonly Vector4 Groove = Hex(0x08090A);
    public static readonly Vector4 Control = Hex(0x22252A);
    public static readonly Vector4 ControlHover = Hex(0x2A2E33);
    public static readonly Vector4 ControlPressed = Hex(0x1C1F23);
    public static readonly Vector4 DisabledSurface = Hex(0x141518);
    public static readonly Vector4 DisabledBorder = Hex(0x1D1F22);
    public static readonly Vector4 Hairline = Hex(0x24272B);
    public static readonly Vector4 StrongLine = Hex(0x34383E);
    public static readonly Vector4 SwitchRim = Hex(0x2C3035);
    public static readonly Vector4 DisabledControl = Hex(0x1A1C1F);
    public static readonly Vector4 Divider = Hex(0x1C1E21);

    // Text
    public static readonly Vector4 Text = Hex(0xE8E9EA);
    public static readonly Vector4 TextSecondary = Hex(0xA3A8AE);
    public static readonly Vector4 TextTertiary = Hex(0x7C828A);
    public static readonly Vector4 TextDisabled = Hex(0x555A60);
    public static readonly Vector4 TitleText = Hex(0xF3F4F5);
    public static readonly Vector4 Focus = Hex(0xC9CDD1);

    // Accent (turquoise): only for things that are on, playing, selected or being acted on.
    public static readonly Vector4 Accent = Hex(0x4CCBBF);
    public static readonly Vector4 AccentHover = Hex(0x73D6CD);
    public static readonly Vector4 AccentActive = Hex(0x3EA69D);
    public static readonly Vector4 OnAccent = Hex(0x06201E);
    public static readonly Vector4 SelectedTint = Hex(0x1C3333);
    public static readonly Vector4 SwitchOnRim = Hex(0x3A7673);
    public static readonly Vector4 SwitchOnRimHover = Hex(0x429C96);
    public static readonly Vector4 PrimaryDisabled = Hex(0x223D3D);
    public static readonly Vector4 PrimaryDisabledText = Hex(0x2C605D);
    public static readonly Vector4 SwitchDisabledIndicator = Hex(0x285552);

    /// <summary>Kept for older call sites; text in accent color.</summary>
    public static readonly Vector4 AccentText = Accent;

    // Status
    public static readonly Vector4 Error = Hex(0xE5736A);
    public static readonly Vector4 ErrorSurface = Hex(0x2A1716);
    public static readonly Vector4 ErrorLine = Hex(0x4A2523);
    public static readonly Vector4 ErrorTitle = Hex(0xF2B8B2);
    public static readonly Vector4 ErrorDetail = Hex(0xC9948F);
    public static readonly Vector4 Warning = Hex(0xE0A955);
    public static readonly Vector4 WarningSurface = Hex(0x2A2214);
    public static readonly Vector4 WarningLine = Hex(0x4A3B1F);
    public static readonly Vector4 WarningTitle = Hex(0xF0D3A0);
    public static readonly Vector4 WarningDetail = Hex(0xC9AD7E);

    // Materials (instrument parts)
    public static readonly Vector4 Cap = Hex(0xD4D7DA);
    public static readonly Vector4 CapLit = Hex(0xF2F3F4);
    public static readonly Vector4 IndexLine = Hex(0x6E747B);
    public static readonly Vector4 NeutralFill = Hex(0x8A9097);
    public static readonly Vector4 Detent = Hex(0x4A4F55);
    public static readonly Vector4 DisabledPart = Hex(0x33373C);
    public static readonly Vector4 DisabledFill = Hex(0x2A2D31);
    public static readonly Vector4 DisabledPlay = Hex(0x26292D);

    // Layout constants (unscaled px). The type scale is about 1.2× the mockup so text reads well in game.
    public const float ControlHeight = 32.0f;
    public const float TitleBarHeight = 44.0f;
    public const float MainGutter = 20.0f;
    public const float MainLabelWidth = 150.0f;
    public const float MainLabelGap = 12.0f;
    public const float SettingsLabelWidth = 184.0f;
    public const float SettingsLabelGap = 16.0f;

    private const int WindowStyleColorCount = 33;
    private const int WindowStyleVarCount = 19;

    public static CrystalCastFonts? Fonts { get; set; }

    public static float S(float value) => value * ImGuiHelpers.GlobalScale;

    public static Vector2 S(float x, float y) => new(x * ImGuiHelpers.GlobalScale, y * ImGuiHelpers.GlobalScale);

    public static uint U32(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);

    public static Vector4 WithAlpha(Vector4 color, float alpha) => new(color.X, color.Y, color.Z, alpha);

    public static Vector4 Mix(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, Math.Clamp(t, 0.0f, 1.0f));

    public static Vector4 Hex(uint rgb, float alpha = 1.0f) => new(
        ((rgb >> 16) & 0xFF) / 255.0f,
        ((rgb >> 8) & 0xFF) / 255.0f,
        (rgb & 0xFF) / 255.0f,
        alpha);

    /// <summary>
    /// Pushes the CrystalCast ImGuiStyle for a whole window. Call from <c>PreDraw</c> and pair with
    /// <see cref="PopWindowStyle"/> in <c>PostDraw</c>.
    /// </summary>
    public static void PushWindowStyle(Vector2 windowPadding)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, TextTertiary);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Base);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.0f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Raised);
        ImGui.PushStyleColor(ImGuiCol.Border, Hairline);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Inset);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Inset);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Inset);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Base);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Base);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, Base);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, new Vector4(0.0f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, ControlHover);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, StrongLine);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, StrongLine);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Cap);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, CapLit);
        ImGui.PushStyleColor(ImGuiCol.Button, Control);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ControlHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ControlPressed);
        ImGui.PushStyleColor(ImGuiCol.Header, SelectedTint);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Control);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ControlPressed);
        ImGui.PushStyleColor(ImGuiCol.Separator, Hairline);
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, StrongLine);
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, new Vector4(0.0f));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, StrongLine);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, WithAlpha(Accent, 0.35f));
        ImGui.PushStyleColor(ImGuiCol.ModalWindowDimBg, new Vector4(0.0f, 0.0f, 0.0f, 0.5f));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, windowPadding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, S(8.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, S(6.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, S(4.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, S(10.0f, 8.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, S(4.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, S(8.0f, 8.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, S(8.0f, 6.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, S(8.0f, 6.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.IndentSpacing, S(16.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, S(10.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, S(4.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, S(10.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, S(2.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.0f, 0.5f));

        Fonts?.PushBody();
    }

    public static void PopWindowStyle()
    {
        Fonts?.PopBody();
        ImGui.PopStyleVar(WindowStyleVarCount);
        ImGui.PopStyleColor(WindowStyleColorCount);
    }

    /// <summary>
    /// Pushes the popup look used by dropdowns and menus. Pop with <see cref="PopPopupStyle"/>.
    /// </summary>
    public static void PushPopupStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Raised);
        ImGui.PushStyleColor(ImGuiCol.Border, StrongLine);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, S(4.0f, 4.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, S(4.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, S(0.0f, 2.0f));
    }

    public static void PopPopupStyle()
    {
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(2);
    }
}
