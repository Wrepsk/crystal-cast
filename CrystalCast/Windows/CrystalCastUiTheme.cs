using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal static class CrystalCastUiTheme
{
    private const int TabStyleColorCount = 5;

    public static readonly Vector4 Accent = new(0.392f, 0.149f, 0.145f, 1.0f);
    public static readonly Vector4 AccentText = new(0.847f, 0.549f, 0.537f, 1.0f);
    public static readonly Vector4 AccentHover = new(0.490f, 0.212f, 0.204f, 1.0f);
    public static readonly Vector4 AccentActive = new(0.565f, 0.251f, 0.239f, 1.0f);

    public static Vector4 WithAlpha(Vector4 color, float alpha)
    {
        return new Vector4(color.X, color.Y, color.Z, alpha);
    }

    public static void DrawWindowHeader(string id, string eyebrow, string title, string subtitle)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 9.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(Accent.X * 0.11f, Accent.Y * 0.11f, Accent.Z * 0.11f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.Border, WithAlpha(Accent, 0.42f));
        if (ImGui.BeginChild($"CrystalCastWindowHeader{id}", new Vector2(0.0f, 82.0f), true, ImGuiWindowFlags.None))
        {
            var drawList = ImGui.GetWindowDrawList();
            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            drawList.AddRectFilled(
                windowPos,
                new Vector2(windowPos.X + 5.0f, windowPos.Y + windowSize.Y),
                ImGui.GetColorU32(Accent),
                9.0f);

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8.0f);
            ImGui.TextColored(AccentText, eyebrow);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8.0f);
            ImGui.TextUnformatted(title);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8.0f);
            ImGui.PushTextWrapPos();
            ImGui.TextDisabled(subtitle);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    public static void DrawSectionHeader(string title, string subtitle)
    {
        var start = ImGui.GetCursorScreenPos();
        var height = string.IsNullOrWhiteSpace(subtitle)
            ? ImGui.GetTextLineHeight()
            : (ImGui.GetTextLineHeight() * 2.0f) + 3.0f;
        ImGui.GetWindowDrawList().AddRectFilled(
            start,
            new Vector2(start.X + 3.0f, start.Y + height),
            ImGui.GetColorU32(Accent),
            1.5f);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 10.0f);
        ImGui.TextUnformatted(title);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 10.0f);
            ImGui.PushTextWrapPos();
            ImGui.TextDisabled(subtitle);
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
    }

    public static void PushTabStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.Tab, WithAlpha(Accent, 0.20f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, AccentHover);
        ImGui.PushStyleColor(ImGuiCol.TabActive, WithAlpha(AccentActive, 0.88f));
        ImGui.PushStyleColor(ImGuiCol.TabUnfocused, WithAlpha(Accent, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.TabUnfocusedActive, WithAlpha(Accent, 0.48f));
    }

    public static void PopTabStyle()
    {
        ImGui.PopStyleColor(TabStyleColorCount);
    }

    public static void PushPrimaryButtonStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, WithAlpha(Accent, 0.72f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
    }

    public static void PopPrimaryButtonStyle()
    {
        ImGui.PopStyleColor(3);
    }
}
