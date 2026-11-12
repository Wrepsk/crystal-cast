using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures.TextureWraps;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;

namespace CrystalCast.Windows.Style;

internal enum ButtonKind
{
    Secondary,
    Primary,
    Ghost,
}

internal enum BannerKind
{
    Error,
    Warning,
}

/// <summary>
/// ImGui renditions of the controls on the redesign's component sheet. Every control is 28 px tall
/// (at 100% scale) so form rows keep a 36 px pitch.
/// </summary>
internal static class CrystalCastUiWidgets
{
    private const float SwitchTravelSeconds = 0.15f;
    private static readonly Dictionary<uint, float> SwitchAnimation = [];

    public static float FrameHeight => S(ControlHeight);

    // ---------------------------------------------------------------- fonts and text

    public static IDisposable Font(Func<CrystalCastFonts, IFontHandle> pick)
    {
        return Fonts is { } fonts ? pick(fonts).Push() : NullScope.Instance;
    }

    public static IDisposable IconFont() => Fonts is { } fonts ? fonts.Icon.Push() : Plugin.PluginInterface.UiBuilder.IconFontHandle.Push();

    public static void Text(string text, Vector4 color, Func<CrystalCastFonts, IFontHandle>? font = null, bool wrap = false)
    {
        using var _ = font == null ? NullScope.Instance : Font(font);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        if (wrap)
            ImGui.PushTextWrapPos(0.0f);
        ImGui.TextUnformatted(text);
        if (wrap)
            ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    public static void Heading(string text) => Text(text, CrystalCastUiTheme.Text, f => f.SemiBold);

    public static void Label(string text, bool wrap = false) => Text(text, TextSecondary, f => f.Small, wrap);

    public static void Meta(string text, bool wrap = true) => Text(text, TextTertiary, f => f.Small, wrap);

    public static void ErrorText(string text) => Text(text, Error, f => f.Small, wrap: true);

    public static void Readout(string text, Vector4? color = null) => Text(text, color ?? CrystalCastUiTheme.Text, f => f.Mono);

    /// <summary>
    /// Text vertically centered on a control row. It takes up a full row-height item, so anything placed after it
    /// with SameLine lines up with the other controls. With <paramref name="wrap"/>, text that does not fit the
    /// remaining width wraps onto further lines instead.
    /// </summary>
    public static void RowText(string text, Vector4 color, Func<CrystalCastFonts, IFontHandle>? font = null, bool wrap = false)
    {
        using var _ = Font(font ?? (f => f.Small));
        var size = ImGui.CalcTextSize(text);
        var available = ImGui.GetContentRegionAvail().X;
        if (wrap && size.X > available)
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((FrameHeight - ImGui.GetTextLineHeight()) * 0.5f));
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.PushTextWrapPos(0.0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
            return;
        }

        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddText(Snap(new Vector2(pos.X, pos.Y + ((FrameHeight - size.Y) * 0.5f))), U32(color), text);
        ImGui.Dummy(new Vector2(size.X, FrameHeight));
    }

    /// <summary>Section heading with an optional one-line description, 14 px above the content.</summary>
    public static void SectionHeader(string title, string? description = null)
    {
        Heading(title);
        if (!string.IsNullOrWhiteSpace(description))
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - S(6.0f));
            Label(description, wrap: true);
        }

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + S(6.0f));
    }

    public static void VerticalSpace(float px)
    {
        ImGui.Dummy(new Vector2(0.0f, Math.Max(0.0f, S(px) - ImGui.GetStyle().ItemSpacing.Y)));
    }

    // ---------------------------------------------------------------- form rows

    /// <summary>
    /// Draws a form label vertically centered on a 28 px control row and moves the cursor to the value column.
    /// </summary>
    public static void RowLabel(string label, float labelWidth, float gap)
    {
        var start = ImGui.GetCursorPos();
        using (Font(f => f.Small))
        {
            var textHeight = ImGui.GetTextLineHeight();
            ImGui.SetCursorPos(new Vector2(start.X, start.Y + ((FrameHeight - textHeight) * 0.5f)));
            ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
            ImGui.PushTextWrapPos(start.X + S(labelWidth));
            ImGui.TextUnformatted(label);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        ImGui.SetCursorPos(new Vector2(start.X + S(labelWidth + gap), start.Y));
    }

    /// <summary>Moves the cursor to the value column of a row that has no label (hints under a field).</summary>
    public static void RowIndent(float labelWidth, float gap)
    {
        ImGui.SetCursorPos(new Vector2(ImGui.GetCursorPosX() + S(labelWidth + gap), ImGui.GetCursorPosY() - S(4.0f)));
    }

    // ---------------------------------------------------------------- buttons

    public static bool Button(
        string label,
        ButtonKind kind = ButtonKind.Secondary,
        float width = 0.0f,
        bool disabled = false,
        bool on = false,
        Vector4? textColor = null,
        string? tooltip = null)
    {
        Vector4 bg, hover, active, text;
        switch (kind)
        {
            case ButtonKind.Primary:
                (bg, hover, active, text) = disabled
                    ? (PrimaryDisabled, PrimaryDisabled, PrimaryDisabled, PrimaryDisabledText)
                    : (Accent, AccentHover, AccentActive, OnAccent);
                break;
            case ButtonKind.Ghost:
                var clear = new Vector4(0.0f);
                (bg, hover, active, text) = disabled
                    ? (clear, clear, clear, CrystalCastUiTheme.TextDisabled)
                    : (clear, Control, ControlPressed, on ? Accent : textColor ?? TextSecondary);
                break;
            default:
                (bg, hover, active, text) = disabled
                    ? (DisabledControl, DisabledControl, DisabledControl, CrystalCastUiTheme.TextDisabled)
                    : (Control, ControlHover, ControlPressed, textColor ?? CrystalCastUiTheme.Text);
                break;
        }

        ImGui.PushStyleColor(ImGuiCol.Button, bg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(S(12.0f), 0.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.DisabledAlpha, 1.0f);
        if (disabled)
            ImGui.BeginDisabled();

        bool pressed;
        using (Font(f => f.SemiBold))
            pressed = ImGui.Button(label, new Vector2(width, FrameHeight));

        if (disabled)
            ImGui.EndDisabled();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);

        if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);

        // Ghost "hover" turns the label primary.
        if (kind == ButtonKind.Ghost && !disabled && !on && textColor == null && ImGui.IsItemHovered())
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            using (Font(f => f.SemiBold))
            {
                var visible = VisibleLabel(label);
                var size = ImGui.CalcTextSize(visible);
                ImGui.GetWindowDrawList().AddText(
                    new Vector2(min.X + ((max.X - min.X - size.X) * 0.5f), min.Y + ((max.Y - min.Y - size.Y) * 0.5f)),
                    U32(CrystalCastUiTheme.Text),
                    visible);
            }
        }

        return pressed && !disabled;
    }

    public static float ButtonWidth(string label)
    {
        using (Font(f => f.SemiBold))
            return ImGui.CalcTextSize(VisibleLabel(label)).X + S(24.0f);
    }

    public static bool IconButton(
        string id,
        FontAwesomeIcon icon,
        string tooltip,
        bool disabled = false,
        bool on = false,
        bool ghost = true,
        Vector4? color = null)
    {
        var size = new Vector2(FrameHeight, FrameHeight);
        var pos = ImGui.GetCursorScreenPos();
        if (disabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton(id, size);
        if (disabled)
            ImGui.EndDisabled();

        var hovered = !disabled && ImGui.IsItemHovered();
        var held = !disabled && ImGui.IsItemActive();
        var dl = ImGui.GetWindowDrawList();
        Vector4? bg = ghost
            ? held ? ControlPressed : hovered ? Control : null
            : disabled ? DisabledControl : held ? ControlPressed : hovered ? ControlHover : Control;
        if (bg is { } background)
            dl.AddRectFilled(pos, pos + size, U32(background), S(4.0f));

        var iconColor = disabled
            ? CrystalCastUiTheme.TextDisabled
            : on
                ? Accent
                : hovered
                    ? CrystalCastUiTheme.Text
                    : color ?? (ghost ? TextSecondary : CrystalCastUiTheme.Text);
        DrawIcon(dl, pos + (size * 0.5f), icon, iconColor);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrEmpty(tooltip))
            ImGui.SetTooltip(tooltip);

        return clicked && !disabled;
    }

    /// <summary>Draws a FontAwesome glyph centered on <paramref name="center"/>, at the icon font's native size.</summary>
    public static void DrawIcon(ImDrawListPtr dl, Vector2 center, FontAwesomeIcon icon, Vector4 color)
    {
        using (IconFont())
        {
            var glyph = icon.ToIconString();
            var textSize = ImGui.CalcTextSize(glyph);
            dl.AddText(Snap(center - (textSize * 0.5f)), U32(color), glyph);
        }
    }

    /// <summary>Shortens <paramref name="text"/> with an ellipsis so it fits <paramref name="maxWidth"/> in the current font.</summary>
    public static string Ellipsize(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        const string ellipsis = "…";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid].TrimEnd() + ellipsis).X <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }

        return text[..low].TrimEnd() + ellipsis;
    }

    /// <summary>Rounds a position down to whole pixels so text and icons are not resampled.</summary>
    public static Vector2 Snap(Vector2 position) => new(MathF.Floor(position.X), MathF.Floor(position.Y));

    // ---------------------------------------------------------------- text and number fields

    public static bool TextField(
        string id,
        ref string value,
        int maxLength,
        string hint = "",
        float width = 0.0f,
        bool error = false,
        bool disabled = false,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None,
        bool mono = false)
    {
        var result = false;
        using (Font(f => mono ? f.Mono : f.Body))
        {
            BeginFieldStyle(disabled);
            ImGui.SetNextItemWidth(ResolveWidth(width));
            result = ImGui.InputTextWithHint(id, hint, ref value, maxLength, flags);
            EndFieldStyle(disabled, error);
        }

        return result && !disabled;
    }

    /// <summary>Styled drag field. Right-click enters an exact value.</summary>
    public static bool DragFloat(
        string id,
        ref float value,
        float speed,
        float minimum = 0.0f,
        float maximum = 0.0f,
        string format = "%.3f",
        float width = 0.0f,
        bool disabled = false,
        string? tooltip = null)
    {
        bool changed;
        using (Font(f => f.Mono))
        {
            BeginFieldStyle(disabled);
            ImGui.SetNextItemWidth(ResolveWidth(width));
            changed = ImGui.DragFloat(id, ref value, speed, minimum, maximum, format);
            EndFieldStyle(disabled, error: false);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip ?? "Drag horizontally to adjust. Double-click or right-click to enter an exact value.");

        if (!disabled)
            changed |= DrawExactValuePopup(id, ref value, minimum, maximum, format, clamp: minimum < maximum);
        return changed && !disabled;
    }

    /// <summary>Three drag fields side by side, each prefixed with its axis name.</summary>
    public static bool DragFloat3(string id, ref Vector3 value, float speed, string[] axes, string format = "%.3f", bool disabled = false)
    {
        var changed = false;
        var spacing = S(8.0f);
        var total = ImGui.GetContentRegionAvail().X;
        var each = (total - (spacing * 2.0f)) / 3.0f;
        var components = new[] { value.X, value.Y, value.Z };
        for (var i = 0; i < 3; i++)
        {
            if (i > 0)
                ImGui.SameLine(0.0f, spacing);
            var component = components[i];
            if (DragFloat($"##{id}{i}", ref component, speed, 0.0f, 0.0f, $"{axes[i]}  {format}", each, disabled,
                    "Drag horizontally to adjust. Double-click or right-click to enter an exact value."))
            {
                components[i] = component;
                changed = true;
            }
        }

        if (changed)
            value = new Vector3(components[0], components[1], components[2]);
        return changed;
    }

    private static void BeginFieldStyle(bool disabled)
    {
        var padY = Math.Max(0.0f, (FrameHeight - ImGui.GetFontSize()) * 0.5f);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, disabled ? DisabledSurface : Inset);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, disabled ? DisabledSurface : Inset);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Inset);
        ImGui.PushStyleColor(ImGuiCol.Text, disabled ? CrystalCastUiTheme.TextDisabled : CrystalCastUiTheme.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, TextTertiary);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(S(10.0f), padY));
        ImGui.PushStyleVar(ImGuiStyleVar.DisabledAlpha, 1.0f);
        if (disabled)
            ImGui.BeginDisabled();
    }

    private static void EndFieldStyle(bool disabled, bool error)
    {
        if (disabled)
            ImGui.EndDisabled();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);

        var border = disabled
            ? DisabledBorder
            : ImGui.IsItemActive()
                ? Accent
                : error
                    ? Error
                    : ImGui.IsItemHovered()
                        ? StrongLine
                        : Hairline;
        ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), U32(border), S(4.0f), ImDrawFlags.None, 1.0f);
    }

    private static float ResolveWidth(float width)
    {
        var available = ImGui.GetContentRegionAvail().X;
        return width > 0.0f ? width : Math.Max(S(40.0f), available + width);
    }

    // ---------------------------------------------------------------- dropdowns and menus

    /// <summary>
    /// A field-styled dropdown. When it returns true, draw items with <see cref="MenuItem"/> and call <see cref="EndDropdown"/>.
    /// </summary>
    public static bool BeginDropdown(string id, string preview, float width = 0.0f, bool disabled = false, Action<ImDrawListPtr, Vector2, Vector2>? drawPrefix = null)
    {
        var popupId = $"##CrystalCastDropdown{id}";
        var size = new Vector2(ResolveWidth(width), FrameHeight);
        var pos = ImGui.GetCursorScreenPos();
        var open = ImGui.IsPopupOpen(popupId);

        if (disabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton(id, size);
        if (disabled)
            ImGui.EndDisabled();
        var hovered = !disabled && ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + size, U32(disabled ? DisabledSurface : Inset), S(4.0f));
        dl.AddRect(pos, pos + size, U32(disabled ? DisabledBorder : open ? Accent : hovered ? StrongLine : Hairline), S(4.0f), ImDrawFlags.None, 1.0f);

        var textX = pos.X + S(10.0f);
        if (drawPrefix != null)
        {
            var prefixMin = new Vector2(pos.X + S(4.0f), pos.Y + S(5.0f));
            var prefixMax = new Vector2(prefixMin.X + S(32.0f), prefixMin.Y + S(18.0f));
            drawPrefix(dl, prefixMin, prefixMax);
            textX = prefixMax.X + S(8.0f);
        }

        using (Font(f => f.Body))
        {
            var textHeight = ImGui.GetTextLineHeight();
            dl.PushClipRect(pos, new Vector2(pos.X + size.X - S(28.0f), pos.Y + size.Y), true);
            dl.AddText(new Vector2(textX, pos.Y + ((size.Y - textHeight) * 0.5f)), U32(disabled ? CrystalCastUiTheme.TextDisabled : CrystalCastUiTheme.Text), preview);
            dl.PopClipRect();
        }

        DrawChevron(dl, new Vector2(pos.X + size.X - S(16.0f), pos.Y + (size.Y * 0.5f)), disabled ? CrystalCastUiTheme.TextDisabled : TextSecondary, open);

        if (clicked && !disabled)
            ImGui.OpenPopup(popupId);

        ImGui.SetNextWindowPos(new Vector2(pos.X, pos.Y + size.Y + S(4.0f)));
        ImGui.SetNextWindowSizeConstraints(new Vector2(size.X, 0.0f), new Vector2(Math.Max(size.X, S(420.0f)), S(360.0f)));
        PushPopupStyle();
        if (ImGui.BeginPopup(popupId, ImGuiWindowFlags.NoMove))
            return true;

        PopPopupStyle();
        return false;
    }

    public static void EndDropdown()
    {
        ImGui.EndPopup();
        PopPopupStyle();
    }

    /// <summary>Opens a menu anchored below-right of the last item. Pair with <see cref="BeginMenu"/>.</summary>
    public static void OpenMenu(string id)
    {
        ImGui.OpenPopup($"##CrystalCastMenu{id}");
    }

    public static bool BeginMenu(string id, Vector2 anchorMax)
    {
        var popupId = $"##CrystalCastMenu{id}";
        if (!ImGui.IsPopupOpen(popupId))
            return false;

        ImGui.SetNextWindowPos(new Vector2(anchorMax.X, anchorMax.Y + S(4.0f)), ImGuiCond.Appearing, new Vector2(1.0f, 0.0f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(S(180.0f), 0.0f), new Vector2(S(320.0f), S(400.0f)));
        PushPopupStyle();
        if (ImGui.BeginPopup(popupId, ImGuiWindowFlags.NoMove))
            return true;

        PopPopupStyle();
        return false;
    }

    public static void EndMenu() => EndDropdown();

    public static bool MenuItem(string label, bool selected = false, bool disabled = false, bool danger = false, FontAwesomeIcon? icon = null)
    {
        var width = Math.Max(ImGui.GetContentRegionAvail().X, S(172.0f));
        var size = new Vector2(width, FrameHeight);
        var pos = ImGui.GetCursorScreenPos();
        if (disabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton(label, size);
        if (disabled)
            ImGui.EndDisabled();
        var hovered = !disabled && ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        if (hovered)
            dl.AddRectFilled(pos, pos + size, U32(danger ? ErrorSurface : Control), S(3.0f));

        var textColor = disabled ? CrystalCastUiTheme.TextDisabled : danger ? Error : CrystalCastUiTheme.Text;
        var textX = pos.X + S(8.0f);
        if (icon is { } glyph)
        {
            DrawIcon(dl, new Vector2(textX + S(8.0f), pos.Y + (size.Y * 0.5f)), glyph, disabled ? CrystalCastUiTheme.TextDisabled : danger ? Error : TextSecondary);
            textX += S(24.0f);
        }

        using (Font(f => f.Body))
        {
            var visible = VisibleLabel(label);
            dl.AddText(new Vector2(textX, pos.Y + ((size.Y - ImGui.GetTextLineHeight()) * 0.5f)), U32(textColor), visible);
        }

        if (selected)
            DrawIcon(dl, new Vector2(pos.X + size.X - S(16.0f), pos.Y + (size.Y * 0.5f)), FontAwesomeIcon.Check, Accent);

        if (clicked && !disabled)
        {
            ImGui.CloseCurrentPopup();
            return true;
        }

        return false;
    }

    public static void MenuSeparator()
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, S(172.0f));
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(pos.X, pos.Y + S(2.0f)),
            new Vector2(pos.X + width, pos.Y + S(3.0f)),
            U32(Hairline));
        ImGui.Dummy(new Vector2(width, S(4.0f)));
    }

    public static void MenuHint(string text)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + S(8.0f));
        Text(text, TextTertiary, f => f.Small);
    }

    private static void DrawChevron(ImDrawListPtr dl, Vector2 center, Vector4 color, bool up)
    {
        var w = S(4.5f);
        var h = S(2.5f) * (up ? -1.0f : 1.0f);
        dl.AddLine(new Vector2(center.X - w, center.Y - h), center + new Vector2(0.0f, h), U32(color), S(1.6f));
        dl.AddLine(center + new Vector2(0.0f, h), new Vector2(center.X + w, center.Y - h), U32(color), S(1.6f));
    }

    // ---------------------------------------------------------------- switch

    public static bool Switch(string id, ref bool value, bool disabled = false, string? tooltip = null)
    {
        var track = new Vector2(S(34.0f), S(18.0f));
        var pos = ImGui.GetCursorScreenPos();
        var itemId = ImGui.GetID(id);
        if (disabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton(id, new Vector2(track.X, FrameHeight));
        if (disabled)
            ImGui.EndDisabled();
        var hovered = !disabled && ImGui.IsItemHovered();

        var changed = false;
        if (clicked && !disabled)
        {
            value = !value;
            changed = true;
        }

        if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);

        DrawSwitch(ImGui.GetWindowDrawList(), new Vector2(pos.X, pos.Y + ((FrameHeight - track.Y) * 0.5f)), itemId, value, hovered, disabled);
        return changed;
    }

    /// <summary>Switch with a text label. The label is clickable too.</summary>
    /// <summary>
    /// Switch with a clickable text label. <paramref name="labelFirst"/> draws the small secondary label used in
    /// the title bar and over artwork; otherwise the label is body text on the right.
    /// </summary>
    public static bool SwitchLabeled(string id, string label, ref bool value, bool labelFirst = false, bool disabled = false, string? tooltip = null)
    {
        var changed = false;
        var color = disabled ? CrystalCastUiTheme.TextDisabled : labelFirst ? TextSecondary : CrystalCastUiTheme.Text;
        var gap = S(labelFirst ? 8.0f : 10.0f);

        if (labelFirst)
        {
            changed |= SwitchLabelText(id, label, ref value, disabled, color, small: true);
            ImGui.SameLine(0.0f, gap);
            changed |= Switch(id, ref value, disabled, tooltip);
        }
        else
        {
            changed |= Switch(id, ref value, disabled, tooltip);
            ImGui.SameLine(0.0f, gap);
            changed |= SwitchLabelText(id, label, ref value, disabled, color, small: false);
        }

        return changed;
    }

    private static bool SwitchLabelText(string id, string label, ref bool value, bool disabled, Vector4 color, bool small)
    {
        using (Font(f => small ? f.Small : f.Body))
        {
            var size = ImGui.CalcTextSize(label);
            var pos = ImGui.GetCursorScreenPos();
            var clicked = ImGui.InvisibleButton($"{id}Label", new Vector2(size.X, FrameHeight));
            ImGui.GetWindowDrawList().AddText(new Vector2(pos.X, pos.Y + ((FrameHeight - size.Y) * 0.5f)), U32(color), label);
            if (clicked && !disabled)
            {
                value = !value;
                return true;
            }
        }

        return false;
    }

    private static void DrawSwitch(ImDrawListPtr dl, Vector2 min, uint itemId, bool on, bool hovered, bool disabled)
    {
        var dt = ImGui.GetIO().DeltaTime;
        var target = on ? 1.0f : 0.0f;
        if (!SwitchAnimation.TryGetValue(itemId, out var t))
            t = target;
        t = t < target
            ? Math.Min(target, t + (dt / SwitchTravelSeconds))
            : Math.Max(target, t - (dt / SwitchTravelSeconds));
        SwitchAnimation[itemId] = t;

        var max = min + new Vector2(S(34.0f), S(18.0f));
        Vector4 trackColor, rim, knob;
        if (disabled)
        {
            trackColor = DisabledSurface;
            rim = DisabledBorder;
            knob = DisabledPart;
        }
        else if (on)
        {
            trackColor = SelectedTint;
            rim = hovered ? SwitchOnRimHover : SwitchOnRim;
            knob = hovered ? new Vector4(1.0f) : CrystalCastUiTheme.Text;
        }
        else
        {
            trackColor = Inset;
            rim = hovered ? StrongLine : SwitchRim;
            knob = hovered ? NeutralFill : IndexLine;
        }

        dl.AddRectFilled(min, max, U32(trackColor), S(4.0f));
        dl.AddRect(min, max, U32(rim), S(4.0f), ImDrawFlags.None, 1.0f);
        if (on)
        {
            var indicator = new Vector2(min.X + S(6.0f), min.Y + S(8.0f));
            dl.AddRectFilled(indicator, indicator + new Vector2(S(8.0f), S(2.0f)), U32(disabled ? SwitchDisabledIndicator : Accent), S(1.0f));
        }

        var knobX = min.X + S(3.0f) + (S(16.0f) * t);
        var knobMin = new Vector2(knobX, min.Y + S(3.0f));
        dl.AddRectFilled(knobMin, knobMin + new Vector2(S(12.0f), S(12.0f)), U32(knob), S(3.0f));
    }

    // ---------------------------------------------------------------- slider

    /// <summary>
    /// Instrument-style slider: 4 px groove, neutral fill, 10 × 18 cap with an index line, an optional detent
    /// at the default value, and a 48 px Geist Mono readout on the right. Right-click enters an exact value.
    /// </summary>
    public static bool Slider(
        string id,
        ref float value,
        float minimum,
        float maximum,
        Func<float, string> readout,
        float? detent = null,
        bool disabled = false,
        float readoutWidth = 60.0f,
        float trackWidth = 0.0f,
        string exactFormat = "%.3f")
    {
        var showReadout = readoutWidth > 0.0f;
        var gap = showReadout ? S(12.0f) : 0.0f;
        var readoutW = showReadout ? S(readoutWidth) : 0.0f;
        var width = trackWidth > 0.0f
            ? trackWidth
            : Math.Max(S(40.0f), ImGui.GetContentRegionAvail().X - readoutW - gap);
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, FrameHeight);

        if (disabled)
            ImGui.BeginDisabled();
        ImGui.InvisibleButton(id, size);
        if (disabled)
            ImGui.EndDisabled();
        var hovered = !disabled && ImGui.IsItemHovered();
        var active = !disabled && ImGui.IsItemActive();
        var changed = false;
        var range = Math.Max(1e-6f, maximum - minimum);
        if (active)
        {
            var capHalf = S(5.0f);
            var fraction = Math.Clamp((ImGui.GetIO().MousePos.X - pos.X - capHalf) / Math.Max(1.0f, width - (capHalf * 2.0f)), 0.0f, 1.0f);
            var next = minimum + (fraction * range);
            if (Math.Abs(next - value) > float.Epsilon)
            {
                value = next;
                changed = true;
            }
        }

        if (hovered || active)
            ImGui.SetTooltip(showReadout ? "Drag to adjust. Right-click to enter an exact value." : readout(value));

        var current = Math.Clamp((value - minimum) / range, 0.0f, 1.0f);
        float? detentFraction = detent is { } d ? Math.Clamp((d - minimum) / range, 0.0f, 1.0f) : null;
        DrawSliderTrack(
            ImGui.GetWindowDrawList(),
            pos,
            size,
            current,
            disabled ? DisabledFill : active ? TextSecondary : NeutralFill,
            hovered || active,
            active,
            disabled,
            detentFraction);

        if (!disabled)
            changed |= DrawExactValuePopup(id, ref value, minimum, maximum, exactFormat, clamp: true);

        if (!showReadout)
            return changed && !disabled;

        ImGui.SameLine(0.0f, gap);
        using (Font(f => f.Mono))
        {
            var text = readout(value);
            var textWidth = ImGui.CalcTextSize(text).X;
            var start = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(start.X + Math.Max(0.0f, readoutW - textWidth), start.Y + ((FrameHeight - ImGui.GetTextLineHeight()) * 0.5f)));
            ImGui.PushStyleColor(ImGuiCol.Text, disabled ? CrystalCastUiTheme.TextDisabled : CrystalCastUiTheme.Text);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }

        return changed && !disabled;
    }

    /// <summary>Draws the slider groove, fill, cap and optional detent inside a row-height rectangle.</summary>
    public static void DrawSliderTrack(
        ImDrawListPtr dl,
        Vector2 pos,
        Vector2 size,
        float fraction,
        Vector4 fill,
        bool lit,
        bool dragging,
        bool disabled,
        float? detentFraction = null)
    {
        var capW = S(10.0f);
        var capH = S(18.0f);
        var centerY = pos.Y + (size.Y * 0.5f);
        var grooveMin = new Vector2(pos.X, centerY - S(2.0f));
        var grooveMax = new Vector2(pos.X + size.X, centerY + S(2.0f));
        var capX = pos.X + (capW * 0.5f) + ((size.X - capW) * fraction);

        dl.AddRectFilled(grooveMin, grooveMax, U32(Groove), S(2.0f));
        if (capX > grooveMin.X)
            dl.AddRectFilled(grooveMin, new Vector2(capX, grooveMax.Y), U32(fill), S(2.0f));

        if (detentFraction is { } detent)
        {
            var x = pos.X + (capW * 0.5f) + ((size.X - capW) * detent);
            dl.AddRectFilled(new Vector2(x, grooveMax.Y + S(3.0f)), new Vector2(x + 1.0f, grooveMax.Y + S(8.0f)), U32(Detent));
        }

        var capMin = new Vector2(capX - (capW * 0.5f), centerY - (capH * 0.5f));
        var capMax = capMin + new Vector2(capW, capH);
        if (disabled)
        {
            dl.AddRectFilled(capMin, capMax, U32(DisabledPart), S(2.0f));
            return;
        }

        dl.AddRectFilled(capMin + new Vector2(0.0f, 1.0f), capMax + new Vector2(0.0f, 1.0f), U32(new Vector4(0.0f, 0.0f, 0.0f, 0.5f)), S(2.0f));
        dl.AddRectFilled(capMin, capMax, U32(lit ? CapLit : Cap), S(2.0f));
        dl.AddRectFilled(
            new Vector2(capX - S(1.0f), capMin.Y),
            new Vector2(capX + S(1.0f), capMax.Y),
            U32(dragging ? Accent : IndexLine));
    }

    private static bool DrawExactValuePopup(string id, ref float value, float minimum, float maximum, string format, bool clamp)
    {
        var changed = false;
        if (!ImGui.BeginPopupContextItem($"##CrystalCastExactValuePopup{id}"))
            return false;

        Label("Enter exact value");
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        var exactValue = value;
        using (Font(f => f.Mono))
        {
            BeginFieldStyle(disabled: false);
            ImGui.SetNextItemWidth(S(150.0f));
            if (ImGui.InputFloat(
                    $"##CrystalCastExactValue{id}",
                    ref exactValue,
                    0.0f,
                    0.0f,
                    format.Contains('%') ? format[format.IndexOf('%')..].Split(' ')[0] : "%.3f",
                    ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue)
                && float.IsFinite(exactValue))
            {
                value = clamp ? Math.Clamp(exactValue, minimum, maximum) : exactValue;
                changed = true;
                ImGui.CloseCurrentPopup();
            }

            EndFieldStyle(disabled: false, error: false);
        }

        Meta(clamp
            ? $"Allowed range: {minimum:0.###} to {maximum:0.###}. Press Enter to apply."
            : "Press Enter to apply.");
        ImGui.EndPopup();
        return changed;
    }

    // ---------------------------------------------------------------- tabs and navigation

    /// <summary>Equal-width underline tabs that fill the row.</summary>
    public static int Tabs(string id, IReadOnlyList<string> labels, int selected)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = S(38.0f);
        var cell = width / Math.Max(1, labels.Count);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(new Vector2(start.X, start.Y + height - 1.0f), new Vector2(start.X + width, start.Y + height), U32(Hairline));

        for (var i = 0; i < labels.Count; i++)
        {
            var cellMin = new Vector2(start.X + (cell * i), start.Y);
            ImGui.SetCursorScreenPos(cellMin);
            if (ImGui.InvisibleButton($"##{id}Tab{i}", new Vector2(cell, height)))
                selected = i;
            var hovered = ImGui.IsItemHovered();
            var isSelected = i == selected;

            using (Font(f => f.Medium))
            {
                var textSize = ImGui.CalcTextSize(labels[i]);
                dl.AddText(
                    new Vector2(cellMin.X + ((cell - textSize.X) * 0.5f), cellMin.Y + ((height - textSize.Y) * 0.5f)),
                    U32(isSelected || hovered ? CrystalCastUiTheme.Text : TextSecondary),
                    labels[i]);
            }

            if (isSelected)
                dl.AddRectFilled(new Vector2(cellMin.X, start.Y + height - S(2.0f)), new Vector2(cellMin.X + cell, start.Y + height), U32(Accent));
        }

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, height));
        return selected;
    }

    public static bool NavItem(string label, FontAwesomeIcon icon, bool active)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(width, S(36.0f));
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##Nav{label}", size);
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        if (active)
            dl.AddRectFilled(pos, pos + size, U32(SelectedTint), S(4.0f));
        else if (hovered)
            dl.AddRectFilled(pos, pos + size, U32(ControlPressed), S(4.0f));

        var textColor = active || hovered ? CrystalCastUiTheme.Text : TextSecondary;
        DrawIcon(dl, new Vector2(pos.X + S(18.0f), pos.Y + (size.Y * 0.5f)), icon, active ? Accent : textColor);
        using (Font(f => f.Medium))
            dl.AddText(new Vector2(pos.X + S(40.0f), pos.Y + ((size.Y - ImGui.GetTextLineHeight()) * 0.5f)), U32(textColor), label);

        return clicked;
    }

    // ---------------------------------------------------------------- status and banners

    public static void StatusLine(Vector4 dotColor, string state, string details, bool disabled = false)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        using (Font(f => f.Small))
        {
            var lineHeight = ImGui.GetTextLineHeight();
            dl.AddCircleFilled(new Vector2(pos.X + S(3.0f), pos.Y + (lineHeight * 0.5f)), S(3.0f), U32(dotColor));
            var x = pos.X + S(14.0f);
            using (Font(f => f.SmallMedium))
            {
                dl.AddText(new Vector2(x, pos.Y), U32(disabled ? CrystalCastUiTheme.TextDisabled : CrystalCastUiTheme.Text), state);
                x += ImGui.CalcTextSize(state).X;
            }

            if (!string.IsNullOrEmpty(details))
                dl.AddText(new Vector2(x + S(4.0f), pos.Y), U32(disabled ? CrystalCastUiTheme.TextDisabled : TextSecondary), $"· {details}");
            ImGui.Dummy(new Vector2(1.0f, lineHeight));
        }
    }

    /// <summary>Error or warning banner. Returns true when the trailing ghost button is pressed.</summary>
    public static bool Banner(string id, BannerKind kind, string title, string detail, string? actionLabel = null)
    {
        var (surface, line, iconColor, titleColor, detailColor, icon) = kind == BannerKind.Error
            ? (ErrorSurface, ErrorLine, Error, ErrorTitle, ErrorDetail, FontAwesomeIcon.ExclamationTriangle)
            : (WarningSurface, WarningLine, Warning, WarningTitle, WarningDetail, FontAwesomeIcon.InfoCircle);

        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var padLeft = S(12.0f);
        var padRight = S(8.0f);
        var padY = S(10.0f);
        var iconSize = S(16.0f);
        var gap = S(10.0f);
        var buttonWidth = actionLabel == null ? 0.0f : ButtonWidth(actionLabel);
        var textWidth = Math.Max(S(80.0f), width - padLeft - iconSize - gap - padRight - (actionLabel == null ? 0.0f : buttonWidth + gap));

        float titleHeight;
        float detailHeight = 0.0f;
        using (Font(f => f.SemiBold))
            titleHeight = ImGui.CalcTextSize(title, false, textWidth).Y;
        if (!string.IsNullOrEmpty(detail))
        {
            using (Font(f => f.MonoDetail))
                detailHeight = ImGui.CalcTextSize(detail, false, textWidth).Y + S(2.0f);
        }

        var height = Math.Max(padY + titleHeight + detailHeight + padY, actionLabel == null ? 0.0f : FrameHeight + (padY * 1.4f));
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(width, height), U32(surface), S(4.0f));
        dl.AddRect(pos, pos + new Vector2(width, height), U32(line), S(4.0f), ImDrawFlags.None, 1.0f);
        DrawIcon(dl, new Vector2(pos.X + padLeft + (iconSize * 0.5f), pos.Y + padY + S(9.0f)), icon, iconColor);

        var textX = pos.X + padLeft + iconSize + gap;
        ImGui.SetCursorScreenPos(new Vector2(textX, pos.Y + padY));
        var wrapAt = ImGui.GetCursorPosX() + textWidth;
        ImGui.PushTextWrapPos(wrapAt);
        using (Font(f => f.SemiBold))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, titleColor);
            ImGui.TextUnformatted(title);
            ImGui.PopStyleColor();
        }

        if (!string.IsNullOrEmpty(detail))
        {
            ImGui.SetCursorScreenPos(new Vector2(textX, ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y + S(2.0f)));
            using (Font(f => f.MonoDetail))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, detailColor);
                ImGui.TextUnformatted(detail);
                ImGui.PopStyleColor();
            }
        }

        ImGui.PopTextWrapPos();

        var pressed = false;
        if (actionLabel != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(pos.X + width - padRight - buttonWidth, pos.Y + ((height - FrameHeight) * 0.5f)));
            pressed = Button($"{actionLabel}##{id}", ButtonKind.Ghost, textColor: detailColor);
        }

        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(new Vector2(width, height));
        return pressed;
    }

    // ---------------------------------------------------------------- playback

    public static bool PlayButton(string id, bool playing, bool disabled, string tooltip)
    {
        var diameter = S(36.0f);
        var pos = ImGui.GetCursorScreenPos();
        if (disabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton(id, new Vector2(diameter, diameter));
        if (disabled)
            ImGui.EndDisabled();
        var hovered = !disabled && ImGui.IsItemHovered();
        var held = !disabled && ImGui.IsItemActive();

        Vector4 bg, glyph;
        if (disabled)
            (bg, glyph) = (DisabledPlay, CrystalCastUiTheme.TextDisabled);
        else if (playing)
            (bg, glyph) = (hovered ? AccentHover : Accent, OnAccent);
        else
            (bg, glyph) = (held ? Focus : hovered ? new Vector4(1.0f) : CrystalCastUiTheme.Text, Base);

        var dl = ImGui.GetWindowDrawList();
        var center = pos + new Vector2(diameter * 0.5f, diameter * 0.5f);
        dl.AddCircleFilled(center, diameter * 0.5f, U32(bg), 32);

        var unit = S(14.0f) / 24.0f;
        var origin = center - new Vector2(S(7.0f), S(7.0f));
        Vector2 P(float x, float y) => origin + new Vector2(x * unit, y * unit);
        if (playing)
        {
            dl.AddRectFilled(P(6.0f, 4.5f), P(10.0f, 19.5f), U32(glyph), unit);
            dl.AddRectFilled(P(14.0f, 4.5f), P(18.0f, 19.5f), U32(glyph), unit);
        }
        else
        {
            var nudge = new Vector2(S(1.0f), 0.0f);
            dl.AddTriangleFilled(P(7.0f, 4.5f) + nudge, P(19.5f, 12.0f) + nudge, P(7.0f, 19.5f) + nudge, U32(glyph));
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return clicked && !disabled;
    }

    // ---------------------------------------------------------------- thumbnails and tiles

    public static bool Thumbnail(string id, string name, string meta, bool selected, bool dimmed, Action<ImDrawListPtr, Vector2, Vector2> drawArt, Vector4 surroundColor)
    {
        var artSize = S(128.0f, 72.0f);
        float nameHeight;
        using (Font(f => f.Medium))
            nameHeight = ImGui.GetTextLineHeight();
        float metaHeight;
        using (Font(f => f.Small))
            metaHeight = ImGui.GetTextLineHeight();
        var size = new Vector2(artSize.X, artSize.Y + S(6.0f) + nameHeight + metaHeight);
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();

        var artMax = pos + artSize;
        dl.PushClipRect(pos, artMax, true);
        drawArt(dl, pos, artMax);
        if (hovered)
            dl.AddRectFilled(pos, artMax, U32(new Vector4(1.0f, 1.0f, 1.0f, 0.06f)));
        if (dimmed)
            dl.AddRectFilled(pos, artMax, U32(WithAlpha(Base, 0.55f)));
        dl.PopClipRect();
        RoundCorners(dl, pos, artMax, S(6.0f), surroundColor);

        if (selected)
            dl.AddRect(pos - S(3.0f, 3.0f), artMax + S(3.0f, 3.0f), U32(Accent), S(8.0f), ImDrawFlags.None, S(2.0f));

        var textWidth = artSize.X;
        dl.PushClipRect(new Vector2(pos.X, artMax.Y), new Vector2(pos.X + textWidth, pos.Y + size.Y), true);
        using (Font(f => f.Medium))
            dl.AddText(new Vector2(pos.X, artMax.Y + S(6.0f)), U32(dimmed ? TextSecondary : CrystalCastUiTheme.Text), name);
        using (Font(f => f.Small))
            dl.AddText(new Vector2(pos.X, artMax.Y + S(6.0f) + nameHeight), U32(TextTertiary), meta);
        dl.PopClipRect();
        return clicked;
    }

    public static bool AddTile(string id, string label, bool disabled, string tooltip)
    {
        var artSize = S(128.0f, 72.0f);
        float nameHeight;
        using (Font(f => f.Medium))
            nameHeight = ImGui.GetTextLineHeight();
        float metaHeight;
        using (Font(f => f.Small))
            metaHeight = ImGui.GetTextLineHeight();
        var size = new Vector2(artSize.X, artSize.Y + S(6.0f) + nameHeight + metaHeight);
        var pos = ImGui.GetCursorScreenPos();
        if (disabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton(id, size);
        if (disabled)
            ImGui.EndDisabled();
        var hovered = !disabled && ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        var dash = disabled ? Hairline : hovered ? Detent : StrongLine;
        DashedRect(dl, pos, pos + artSize, U32(dash), S(6.0f), S(4.0f), S(3.0f));
        var iconColor = disabled ? CrystalCastUiTheme.TextDisabled : hovered ? CrystalCastUiTheme.Text : TextSecondary;
        DrawIcon(dl, pos + (artSize * 0.5f), FontAwesomeIcon.Plus, iconColor);
        using (Font(f => f.Medium))
            dl.AddText(new Vector2(pos.X, pos.Y + artSize.Y + S(6.0f)), U32(disabled ? CrystalCastUiTheme.TextDisabled : TextSecondary), label);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return clicked && !disabled;
    }

    public static bool ModeTile(string id, string label, ScreenOutputMode mode, bool selected, bool disabled = false)
    {
        var width = S(200.0f);
        float textHeight;
        using (Font(f => f.Medium))
            textHeight = ImGui.GetTextLineHeight();
        var size = new Vector2(width, S(8.0f) + S(72.0f) + S(8.0f) + textHeight + S(10.0f));
        var pos = ImGui.GetCursorScreenPos();
        if (disabled)
            ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton(id, size);
        if (disabled)
            ImGui.EndDisabled();
        var hovered = !disabled && ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + size, U32(hovered ? ControlPressed : Raised), S(6.0f));
        if (selected)
            dl.AddRect(pos - S(3.0f, 3.0f), pos + size + S(3.0f, 3.0f), U32(Accent), S(8.0f), ImDrawFlags.None, S(2.0f));

        var diagramMin = pos + S(8.0f, 8.0f);
        var diagramMax = new Vector2(pos.X + size.X - S(8.0f), diagramMin.Y + S(72.0f));
        dl.AddRectFilled(diagramMin, diagramMax, U32(Inset), S(4.0f));
        DrawLayerDiagram(dl, diagramMin, diagramMax, mode, selected, disabled);

        var textColor = disabled ? CrystalCastUiTheme.TextDisabled : selected || hovered ? CrystalCastUiTheme.Text : TextSecondary;
        using (Font(f => f.Medium))
            dl.AddText(new Vector2(pos.X + S(8.0f), diagramMax.Y + S(8.0f)), U32(textColor), label);

        return clicked && !disabled;
    }

    private static void DrawLayerDiagram(ImDrawListPtr dl, Vector2 min, Vector2 max, ScreenOutputMode mode, bool selected, bool disabled)
    {
        var scale = (max.X - min.X) / 184.0f;
        var yScale = (max.Y - min.Y) / 72.0f;
        Vector2 P(float x, float y) => min + new Vector2(x * scale, y * yScale);
        Vector2[] Layer(float top) => [P(92, top), P(152, top + 10), P(92, top + 20), P(32, top + 10)];
        var alpha = disabled ? 0.45f : 1.0f;

        var ground = Layer(46);
        Polyline(dl, ground, U32(WithAlpha(CrystalCastUiTheme.TextDisabled, alpha)), 1.0f);

        var (crystalTop, uiTop) = mode switch
        {
            ScreenOutputMode.SceneComposite => (28.0f, 8.0f),
            _ => (18.0f, 14.0f),
        };

        var ui = Layer(uiTop);
        var crystal = Layer(crystalTop);
        var crystalStroke = selected ? Accent : TextSecondary;
        var crystalFill = selected ? WithAlpha(Accent, 0.22f) : WithAlpha(TextTertiary, 0.18f);

        void DrawCrystal()
        {
            dl.AddQuadFilled(crystal[0], crystal[1], crystal[2], crystal[3], U32(WithAlpha(crystalFill, crystalFill.W * alpha)));
            Polyline(dl, crystal, U32(WithAlpha(crystalStroke, alpha)), S(1.2f));
        }

        void DrawUi()
        {
            for (var i = 0; i < 4; i++)
                DashedLine(dl, ui[i], ui[(i + 1) % 4], U32(WithAlpha(TextTertiary, alpha)), S(3.0f), S(3.0f));
        }

        DrawCrystal();
        DrawUi();
    }

    // ---------------------------------------------------------------- images

    /// <summary>
    /// Draws <paramref name="image"/> filling the box, cropping the overflow evenly (which also trims
    /// YouTube's 4:3 letterbox bars from a 16:9 box).
    /// </summary>
    public static void ImageCover(ImDrawListPtr dl, IDalamudTextureWrap image, Vector2 min, Vector2 max, Vector4 tint, float rounding)
    {
        var (uv0, uv1) = CoverUv((float)image.Width / image.Height, (max.X - min.X) / (max.Y - min.Y));
        dl.AddImageRounded(image.Handle, min, max, uv0, uv1, U32(tint), rounding);
    }

    /// <summary>The part of an image of <paramref name="sourceAspect"/> that fills a box of <paramref name="targetAspect"/>.</summary>
    internal static (Vector2 Uv0, Vector2 Uv1) CoverUv(float sourceAspect, float targetAspect)
    {
        if (!(sourceAspect > 0.0f) || !(targetAspect > 0.0f))
            return (Vector2.Zero, Vector2.One);

        if (sourceAspect > targetAspect)
        {
            var inset = (1.0f - (targetAspect / sourceAspect)) * 0.5f;
            return (new Vector2(inset, 0.0f), new Vector2(1.0f - inset, 1.0f));
        }

        var vertical = (1.0f - (sourceAspect / targetAspect)) * 0.5f;
        return (new Vector2(0.0f, vertical), new Vector2(1.0f, 1.0f - vertical));
    }

    // ---------------------------------------------------------------- helper-window layout

    private static readonly Stack<(Vector2 Min, float Width)> CardStack = new();

    /// <summary>Eyebrow, serif title and subtitle at the top of a helper window.</summary>
    public static void Hero(string eyebrow, string title, string subtitle)
    {
        Text(eyebrow, TextTertiary, f => f.Small);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - S(4.0f));
        Text(title, TitleText, f => f.PageTitle);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - S(4.0f));
        Text(subtitle, TextSecondary, f => f.Body, wrap: true);
        VerticalSpace(20.0f);
    }

    /// <summary>Starts a raised card that grows to fit its content. Pair with <see cref="EndCard"/>.</summary>
    public static void BeginCard()
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        CardStack.Push((min, width));
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(min + S(16.0f, 14.0f));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - S(32.0f));
    }

    public static void EndCard()
    {
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        var (min, width) = CardStack.Pop();
        var max = new Vector2(min.X + width, ImGui.GetItemRectMax().Y + S(14.0f));
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(min, max, U32(Raised), S(6.0f));
        dl.ChannelsMerge();
        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(max - min);
        VerticalSpace(12.0f);
    }

    /// <summary>A card with a heading and a list of short points.</summary>
    public static void Card(string title, params string[] points)
    {
        BeginCard();
        Heading(title);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + S(2.0f));
        foreach (var point in points)
            Bullet(point);
        EndCard();
    }

    public static void Bullet(string text)
    {
        var pos = ImGui.GetCursorScreenPos();
        using (Font(f => f.Body))
        {
            var lineHeight = ImGui.GetTextLineHeight();
            ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(pos.X + S(3.0f), pos.Y + (lineHeight * 0.5f)), S(2.0f), U32(TextTertiary));
            ImGui.SetCursorScreenPos(new Vector2(pos.X + S(14.0f), pos.Y));
            ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
            ImGui.TextWrapped(text);
            ImGui.PopStyleColor();
        }
    }

    // ---------------------------------------------------------------- drawing helpers

    public static void Polyline(ImDrawListPtr dl, Vector2[] points, uint color, float thickness)
    {
        for (var i = 0; i < points.Length; i++)
            dl.AddLine(points[i], points[(i + 1) % points.Length], color, thickness);
    }

    public static void DashedLine(ImDrawListPtr dl, Vector2 a, Vector2 b, uint color, float dash, float gap)
    {
        var delta = b - a;
        var length = delta.Length();
        if (length <= 0.0f)
            return;
        var dir = delta / length;
        for (var d = 0.0f; d < length; d += dash + gap)
            dl.AddLine(a + (dir * d), a + (dir * Math.Min(length, d + dash)), color, 1.0f);
    }

    public static void DashedRect(ImDrawListPtr dl, Vector2 min, Vector2 max, uint color, float radius, float dash, float gap)
    {
        DashedLine(dl, new Vector2(min.X + radius, min.Y + 0.5f), new Vector2(max.X - radius, min.Y + 0.5f), color, dash, gap);
        DashedLine(dl, new Vector2(min.X + radius, max.Y - 0.5f), new Vector2(max.X - radius, max.Y - 0.5f), color, dash, gap);
        DashedLine(dl, new Vector2(min.X + 0.5f, min.Y + radius), new Vector2(min.X + 0.5f, max.Y - radius), color, dash, gap);
        DashedLine(dl, new Vector2(max.X - 0.5f, min.Y + radius), new Vector2(max.X - 0.5f, max.Y - radius), color, dash, gap);
        dl.PathArcTo(new Vector2(min.X + radius, min.Y + radius), radius - 0.5f, MathF.PI, MathF.PI * 1.5f);
        dl.PathStroke(color, ImDrawFlags.None, 1.0f);
        dl.PathArcTo(new Vector2(max.X - radius, min.Y + radius), radius - 0.5f, MathF.PI * 1.5f, MathF.PI * 2.0f);
        dl.PathStroke(color, ImDrawFlags.None, 1.0f);
        dl.PathArcTo(new Vector2(max.X - radius, max.Y - radius), radius - 0.5f, 0.0f, MathF.PI * 0.5f);
        dl.PathStroke(color, ImDrawFlags.None, 1.0f);
        dl.PathArcTo(new Vector2(min.X + radius, max.Y - radius), radius - 0.5f, MathF.PI * 0.5f, MathF.PI);
        dl.PathStroke(color, ImDrawFlags.None, 1.0f);
    }

    /// <summary>Masks the square corners of a drawn image so it reads as a rounded rectangle on <paramref name="surround"/>.</summary>
    public static void RoundCorners(ImDrawListPtr dl, Vector2 min, Vector2 max, float radius, Vector4 surround)
    {
        var half = radius * 0.5f;
        dl.AddRect(min - new Vector2(half, half), max + new Vector2(half, half), U32(surround), radius + half, ImDrawFlags.None, radius);
    }

    private static string VisibleLabel(string label)
    {
        var index = label.IndexOf("##", StringComparison.Ordinal);
        return index >= 0 ? label[..index] : label;
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
