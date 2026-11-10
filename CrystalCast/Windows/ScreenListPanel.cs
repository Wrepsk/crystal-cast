using System.Numerics;
using CrystalCast.Playlist;
using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using static CrystalCast.Windows.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

internal sealed class ScreenListPanel(WorldScreenManager renderer, PlaylistMetadataService metadata)
{
    private string renamingScreenId = string.Empty;
    private string renameDraft = string.Empty;
    private bool focusRename;

    public bool Draw(Configuration config, BrowserScreenProfile? activeScreen, Action<string> onScreenDeleted)
    {
        var changed = false;
        var userScreenCount = ScreenLimitPolicy.CountUserScreens(config.BrowserScreens);
        var canAddUserScreen = ScreenLimitPolicy.CanCreateUserScreen(config.BrowserScreens);

        // Header: "Screens  1 of 8" with duplicate / rename / more.
        Ui.RowText("Screens", CrystalCastUiTheme.Text, f => f.SemiBold);

        ImGui.SameLine(0.0f, S(8.0f));
        Ui.RowText($"{userScreenCount} of {Configuration.MaxBrowserScreens}", TextTertiary, f => f.Small);

        var actionsWidth = (Ui.FrameHeight * 3.0f) + (S(4.0f) * 2.0f);
        ImGui.SameLine(Math.Max(0.0f, ImGui.GetContentRegionMax().X - actionsWidth));
        var canDuplicate = activeScreen != null && canAddUserScreen;
        if (Ui.IconButton("##DuplicateScreen", FontAwesomeIcon.Clone, canAddUserScreen ? "Duplicate screen" : "Screen limit reached", disabled: !canDuplicate))
            changed |= Duplicate(config, activeScreen!);

        ImGui.SameLine(0.0f, S(4.0f));
        if (Ui.IconButton("##RenameScreen", FontAwesomeIcon.Pen, "Rename screen", disabled: activeScreen == null))
            BeginRename(activeScreen!);

        ImGui.SameLine(0.0f, S(4.0f));
        if (Ui.IconButton("##ScreenMore", FontAwesomeIcon.EllipsisH, "More screen actions"))
            Ui.OpenMenu("ScreenActions");
        var menuAnchor = ImGui.GetItemRectMax();
        changed |= DrawScreenMenu(config, activeScreen, canAddUserScreen, onScreenDeleted, menuAnchor);

        if (activeScreen != null && renamingScreenId == activeScreen.ScreenId)
            changed |= DrawRenameControls(activeScreen);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + S(2.0f));
        changed |= DrawThumbnails(config, activeScreen, canAddUserScreen);
        return changed;
    }

    /// <summary>The screen's video thumbnail when it has one; otherwise its crystal artwork.</summary>
    private void DrawScreenArt(ImDrawListPtr dl, Vector2 min, Vector2 max, BrowserScreenProfile screen, float seed)
    {
        if (metadata.GetSourceThumbnail(screen) is { Width: > 0, Height: > 0 } image)
        {
            dl.AddRectFilled(min, max, U32(Inset));
            Ui.ImageCover(dl, image, min, max, Vector4.One, 0.0f);
            return;
        }

        CrystalArtwork.DrawThumbnail(dl, min, max, seed);
    }

    private bool DrawThumbnails(Configuration config, BrowserScreenProfile? activeScreen, bool canAddUserScreen)
    {
        var changed = false;
        var tileWidth = S(128.0f);
        var gap = S(14.0f);
        var available = ImGui.GetContentRegionAvail().X;
        var perRow = Math.Max(1, (int)((available + gap) / (tileWidth + gap)));
        var index = 0;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, S(16.0f)));
        foreach (var screen in config.BrowserScreens)
        {
            if (index % perRow != 0)
                ImGui.SameLine();

            var selected = activeScreen != null && screen.ScreenId == activeScreen.ScreenId;
            var seed = CrystalArtwork.SeedFor(screen.ScreenId);
            var meta = screen.CreatedByIpc
                ? $"{BrowserSourceControlsPanel.ProviderName(screen.ProviderKind)} · IPC"
                : BrowserSourceControlsPanel.ProviderName(screen.ProviderKind);
            if (!screen.Enabled)
                meta = $"{meta} · off";

            if (Ui.Thumbnail(
                    $"##Screen{screen.ScreenId}",
                    screen.Name,
                    meta,
                    selected,
                    dimmed: !screen.Enabled,
                    (dl, min, max) => DrawScreenArt(dl, min, max, screen, seed),
                    Base))
            {
                if (!selected)
                {
                    config.ActiveBrowserScreenId = screen.ScreenId;
                    changed = true;
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(screen.Enabled ? screen.Name : $"{screen.Name} (screen off)");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                config.ActiveBrowserScreenId = screen.ScreenId;
                changed = true;
                Ui.OpenMenu("ScreenActions");
            }

            index++;
        }

        if (index % perRow != 0)
            ImGui.SameLine();
        if (Ui.AddTile(
                "##AddScreen",
                "Add screen",
                !canAddUserScreen,
                canAddUserScreen ? "Add a screen and place it in front of you" : $"All {Configuration.MaxBrowserScreens} local screens are in use"))
        {
            changed |= Add(config, activeScreen);
        }

        ImGui.PopStyleVar();
        return changed;
    }

    private bool DrawScreenMenu(Configuration config, BrowserScreenProfile? activeScreen, bool canAddUserScreen, Action<string> onScreenDeleted, Vector2 anchor)
    {
        if (!Ui.BeginMenu("ScreenActions", anchor))
            return false;

        var changed = false;
        if (Ui.MenuItem("Duplicate", disabled: activeScreen == null || !canAddUserScreen, icon: FontAwesomeIcon.Clone))
            changed |= Duplicate(config, activeScreen!);
        if (Ui.MenuItem("Rename", disabled: activeScreen == null, icon: FontAwesomeIcon.Pen))
            BeginRename(activeScreen!);
        if (Ui.MenuItem("Add screen", disabled: !canAddUserScreen, icon: FontAwesomeIcon.Plus))
            changed |= Add(config, activeScreen);
        if (activeScreen != null && Ui.MenuItem("Place in front of me", icon: FontAwesomeIcon.Crosshairs))
            changed |= renderer.PlaceBrowserScreenInFrontOfPlayer(activeScreen);

        Ui.MenuSeparator();
        if (Ui.MenuItem("Delete", disabled: activeScreen == null, danger: true, icon: FontAwesomeIcon.TrashAlt))
        {
            var activeIndex = config.BrowserScreens.FindIndex(screen => screen.ScreenId == activeScreen!.ScreenId);
            var removedId = activeScreen!.ScreenId;
            config.BrowserScreens.RemoveAll(screen => screen.ScreenId == removedId);
            onScreenDeleted(removedId);
            if (renamingScreenId == removedId)
                renamingScreenId = string.Empty;
            config.ActiveBrowserScreenId = config.BrowserScreens.Count == 0
                ? string.Empty
                : config.BrowserScreens[Math.Clamp(activeIndex - 1, 0, config.BrowserScreens.Count - 1)].ScreenId;
            changed = true;
        }

        Ui.EndMenu();
        return changed;
    }

    private bool Add(Configuration config, BrowserScreenProfile? activeScreen)
    {
        var screen = config.CreateDefaultBrowserScreen(GetNextScreenName(config));
        screen.ProviderKind = activeScreen?.ProviderKind ?? BrowserSourceProviderKind.YouTube;
        config.BrowserScreens.Add(screen);
        config.ActiveBrowserScreenId = screen.ScreenId;
        renderer.PlaceBrowserScreenInFrontOfPlayer(screen);
        return true;
    }

    private static bool Duplicate(Configuration config, BrowserScreenProfile activeScreen)
    {
        var copy = activeScreen.CloneAsNew(GetDuplicateScreenName(config, activeScreen.Name));
        OffsetDuplicatePlacement(copy.Placement);
        config.BrowserScreens.Add(copy);
        config.ActiveBrowserScreenId = copy.ScreenId;
        return true;
    }

    private void BeginRename(BrowserScreenProfile screen)
    {
        renamingScreenId = screen.ScreenId;
        renameDraft = screen.Name;
        focusRename = true;
    }

    private bool DrawRenameControls(BrowserScreenProfile activeScreen)
    {
        var changed = false;
        var draft = renameDraft;
        var saveWidth = Ui.ButtonWidth("Save");
        var cancelWidth = Ui.ButtonWidth("Cancel");
        if (focusRename)
        {
            ImGui.SetKeyboardFocusHere();
            focusRename = false;
        }

        var pressedEnter = Ui.TextField(
            "##CrystalCastScreenName",
            ref draft,
            128,
            "Screen name",
            Math.Max(S(80.0f), ImGui.GetContentRegionAvail().X - saveWidth - cancelWidth - S(16.0f)),
            flags: ImGuiInputTextFlags.EnterReturnsTrue);
        renameDraft = draft;

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Save##ScreenRenameSave", ButtonKind.Primary) || pressedEnter)
        {
            var trimmed = renameDraft.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                activeScreen.Name = trimmed;
                changed = true;
            }

            renamingScreenId = string.Empty;
        }

        ImGui.SameLine(0.0f, S(8.0f));
        if (Ui.Button("Cancel##ScreenRenameCancel", ButtonKind.Ghost) || ImGui.IsKeyPressed(ImGuiKey.Escape))
            renamingScreenId = string.Empty;

        return changed;
    }

    private static string GetNextScreenName(Configuration config)
    {
        for (var i = 1; i <= Configuration.MaxBrowserScreens; i++)
        {
            var name = $"Browser screen {i}";
            if (config.BrowserScreens.All(screen => !string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return $"Browser screen {config.BrowserScreens.Count + 1}";
    }

    private static string GetDuplicateScreenName(Configuration config, string sourceName)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "Browser screen" : $"{sourceName} copy";
        if (config.BrowserScreens.All(screen => !string.Equals(screen.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;

        for (var i = 2; i <= Configuration.MaxBrowserScreens; i++)
        {
            var name = $"{baseName} {i}";
            if (config.BrowserScreens.All(screen => !string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return $"{baseName} {config.BrowserScreens.Count + 1}";
    }

    private static void OffsetDuplicatePlacement(ScreenPlacementSettings placement)
    {
        var right = new Vector3(MathF.Cos(placement.YawRadians), 0.0f, -MathF.Sin(placement.YawRadians));
        placement.PositionX += right.X * 0.35f;
        placement.PositionZ += right.Z * 0.35f;
    }
}
