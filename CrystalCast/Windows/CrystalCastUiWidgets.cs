using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal static class CrystalCastUiWidgets
{
    public static bool SliderFloat(
        string label,
        ref float value,
        float minimum,
        float maximum,
        string format = "%.3f")
    {
        var changed = ImGui.SliderFloat(label, ref value, minimum, maximum, format);
        DrawScalarHint("Drag to adjust. Right-click to enter an exact value.");
        changed |= DrawExactValuePopup(label, ref value, minimum, maximum, format, clamp: true);
        return changed;
    }

    public static bool DragFloat(
        string label,
        ref float value,
        float speed,
        float minimum = 0.0f,
        float maximum = 0.0f,
        string format = "%.3f")
    {
        var changed = ImGui.DragFloat(label, ref value, speed, minimum, maximum, format);
        DrawScalarHint("Drag horizontally to adjust. Right-click to enter an exact value.");
        changed |= DrawExactValuePopup(label, ref value, minimum, maximum, format, clamp: minimum < maximum);
        return changed;
    }

    public static bool DragFloat3(string label, ref Vector3 value, float speed, string format = "%.3f")
    {
        var changed = ImGui.DragFloat3(label, ref value, speed, 0.0f, 0.0f, format);
        DrawScalarHint("Drag any component horizontally. Ctrl+click a component to type an exact value.");
        return changed;
    }

    private static bool DrawExactValuePopup(
        string label,
        ref float value,
        float minimum,
        float maximum,
        string format,
        bool clamp)
    {
        var changed = false;
        if (!ImGui.BeginPopupContextItem($"##CrystalCastExactValuePopup{label}"))
            return false;

        ImGui.TextUnformatted("Enter exact value");
        ImGui.SetNextItemWidth(150.0f);
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        var exactValue = value;
        if (ImGui.InputFloat(
                $"Value##CrystalCastExactValue{label}",
                ref exactValue,
                0.0f,
                0.0f,
                format,
                ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue)
            && float.IsFinite(exactValue))
        {
            value = clamp ? Math.Clamp(exactValue, minimum, maximum) : exactValue;
            changed = true;
            ImGui.CloseCurrentPopup();
        }

        ImGui.TextDisabled(clamp
            ? $"Allowed range: {minimum:0.###} to {maximum:0.###}. Press Enter to apply."
            : "Press Enter to apply.");
        ImGui.EndPopup();
        return changed;
    }

    private static void DrawScalarHint(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
