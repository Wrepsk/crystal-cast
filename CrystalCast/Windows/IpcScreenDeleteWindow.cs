using System.Numerics;
using CrystalCast.Sources;
using CrystalCast.Windows.Style;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using static CrystalCast.Windows.Style.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.Style.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

/// <summary>
/// Asks before deleting a screen another plugin created: delete it so it may come back on that plugin's next
/// update, or delete it and keep it hidden for the session.
/// </summary>
internal sealed class IpcScreenDeleteWindow : CrystalCastWindow, IDisposable
{

    private readonly Configuration configuration;
    private string screenId = string.Empty;
    private string screenName = string.Empty;
    private string ownerId = string.Empty;
    private string sourceDescription = string.Empty;
    private Action<bool>? onDelete;
    private bool keepHidden = true;

    public IpcScreenDeleteWindow(Configuration configuration)
        : base(
            "Delete this screen?###CrystalCastIpcScreenDelete",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize)
    {
        this.configuration = configuration;
        Size = new Vector2(580.0f, 390.0f);
        SizeCondition = ImGuiCond.Always;
        IsOpen = false;
    }

    /// <summary>Opens the question for <paramref name="screen"/>; <paramref name="delete"/> gets whether to hide it.</summary>
    public void Ask(BrowserScreenProfile screen, Action<bool> delete)
    {
        screenId = screen.ScreenId;
        screenName = string.IsNullOrWhiteSpace(screen.Name) ? "Unnamed IPC screen" : screen.Name;
        ownerId = string.IsNullOrWhiteSpace(screen.IpcOwnerId) ? "Unknown plugin" : screen.IpcOwnerId;
        sourceDescription = DescribeSource(screen);
        onDelete = delete;
        keepHidden = true;
        IsOpen = true;
        BringToFront();
    }

    public void Suspend()
    {
        IsOpen = false;
        onDelete = null;
    }

    public override void OnClose()
    {
        onDelete = null;
    }

    public override void Draw()
    {
        // The plugin may have removed the screen itself while this was open.
        if (onDelete == null || !configuration.BrowserScreens.Any(screen => screen.ScreenId == screenId))
        {
            IsOpen = false;
            onDelete = null;
            return;
        }

        Ui.Hero(
            "Screen from another plugin",
            "Delete this screen?",
            "Another plugin added this screen and may add it again on its next update, for example a sync plugin sharing someone else's screen.");

        DrawDetail("Screen", screenName);
        DrawDetail("Added by", ownerId);
        if (!string.IsNullOrWhiteSpace(sourceDescription))
            DrawDetail("Showing", sourceDescription);

        Ui.VerticalSpace(16.0f);
        Ui.SwitchLabeled("##KeepIpcScreenHidden", "Keep it hidden this session", ref keepHidden);
        Ui.Meta(keepHidden
            ? "The plugin can't add it again until the game restarts. Settings > IPC > Hidden screens can show it again sooner."
            : "It comes back the next time the plugin sends it.");

        var footerY = ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y - Ui.FrameHeight;
        ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), footerY));
        if (Ui.Button("Cancel"))
        {
            IsOpen = false;
            onDelete = null;
            return;
        }

        var deleteLabel = keepHidden ? "Delete and hide" : "Delete";
        ImGui.SameLine(Math.Max(0.0f, ImGui.GetContentRegionMax().X - Ui.ButtonWidth(deleteLabel)));
        if (Ui.Button(deleteLabel, ButtonKind.Primary))
        {
            var delete = onDelete;
            IsOpen = false;
            onDelete = null;
            delete(keepHidden);
        }
    }

    public void Dispose()
    {
    }

    private static string DescribeSource(BrowserScreenProfile screen)
    {
        var url = BrowserProviderSettings.For(screen).GetUrl(screen);
        return $"{screen.ProviderKind}{(string.IsNullOrWhiteSpace(url) ? string.Empty : $" · {url}")}";
    }
}
