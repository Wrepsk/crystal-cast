using CrystalCast.Windows.Style;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

/// <summary>
/// Base of every CrystalCast window: applies the CrystalCast window style around each draw, so the windows
/// look alike whatever Dalamud theme the user picked.
/// </summary>
public abstract class CrystalCastWindow : Window
{
    /// <summary>Label column of a dialog's detail rows.</summary>
    protected const float DetailLabelWidth = 140.0f;
    protected const float DetailLabelGap = 16.0f;

    protected CrystalCastWindow(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        : base(name, flags)
    {
    }

    /// <summary>The padding inside the window. Dialogs use the default.</summary>
    protected virtual Vector2 WindowPadding => S(28.0f, 24.0f);

    public override void PreDraw()
    {
        PushWindowStyle(WindowPadding);
    }

    public override void PostDraw()
    {
        PopWindowStyle();
    }

    /// <summary>A labelled, wrapping line of text, as dialogs use to describe what they ask about.</summary>
    protected static void DrawDetail(string label, string value)
    {
        Ui.RowLabel(label, DetailLabelWidth, DetailLabelGap);
        Ui.RowText(value, Text, f => f.Body, wrap: true);
    }
}
