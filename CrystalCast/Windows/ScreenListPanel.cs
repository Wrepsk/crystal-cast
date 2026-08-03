using System.Numerics;
using CrystalCast.Rendering;
using CrystalCast.Video;
using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal sealed class ScreenListPanel(WorldScreenManager renderer)
{
    private string renamingScreenId = string.Empty;
    private string renameDraft = string.Empty;

    public bool Draw(Configuration config, BrowserScreenProfile activeScreen, Action<string> onScreenDeleted)
    {
        var changed = false;
        var activeIndex = Math.Max(0, config.BrowserScreens.FindIndex(screen => screen.ScreenId == activeScreen.ScreenId));
        var userScreenCount = ScreenLimitPolicy.CountUserScreens(config.BrowserScreens);
        var canAddUserScreen = ScreenLimitPolicy.CanCreateUserScreen(config.BrowserScreens);

        ImGui.TextDisabled("Screen");
        ImGui.SetNextItemWidth(-1.0f);
        if (ImGui.BeginCombo("##CrystalCastScreen", activeScreen.Name))
        {
            for (var i = 0; i < config.BrowserScreens.Count; i++)
            {
                var screen = config.BrowserScreens[i];
                var selected = i == activeIndex;
                if (ImGui.Selectable($"{screen.Name}##Screen{screen.ScreenId}", selected))
                {
                    config.ActiveBrowserScreenId = screen.ScreenId;
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        changed |= DrawScreenSourceCombo(activeScreen);

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var actionWidth = Math.Max(54.0f, (ImGui.GetContentRegionAvail().X - (spacing * 3.0f)) / 4.0f);

        if (!canAddUserScreen)
            ImGui.BeginDisabled();
        CrystalCastUiTheme.PushPrimaryButtonStyle();
        if (ImGui.Button("Add screen", new Vector2(actionWidth, 0.0f)))
        {
            var screen = config.CreateDefaultBrowserScreen(GetNextScreenName(config));
            screen.ProviderKind = activeScreen.ProviderKind;
            config.BrowserScreens.Add(screen);
            config.ActiveBrowserScreenId = screen.ScreenId;
            renderer.PlaceBrowserScreenInFrontOfPlayer(screen);
            changed = true;
        }
        CrystalCastUiTheme.PopPrimaryButtonStyle();
        if (!canAddUserScreen)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (!canAddUserScreen)
            ImGui.BeginDisabled();
        if (ImGui.Button("Duplicate", new Vector2(actionWidth, 0.0f)))
        {
            var copy = activeScreen.CloneAsNew(GetDuplicateScreenName(config, activeScreen.Name));
            OffsetDuplicatePlacement(copy.Placement);
            config.BrowserScreens.Add(copy);
            config.ActiveBrowserScreenId = copy.ScreenId;
            changed = true;
        }
        if (!canAddUserScreen)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Rename", new Vector2(actionWidth, 0.0f)))
        {
            renamingScreenId = activeScreen.ScreenId;
            renameDraft = activeScreen.Name;
        }

        ImGui.SameLine();
        var canDelete = config.BrowserScreens.Count > 1;
        if (!canDelete)
            ImGui.BeginDisabled();
        if (ImGui.Button("Delete", new Vector2(actionWidth, 0.0f)) && canDelete)
        {
            var removedId = activeScreen.ScreenId;
            config.BrowserScreens.RemoveAll(screen => screen.ScreenId == removedId);
            onScreenDeleted(removedId);
            if (renamingScreenId == removedId)
                renamingScreenId = string.Empty;
            config.ActiveBrowserScreenId = config.BrowserScreens[Math.Clamp(activeIndex - 1, 0, config.BrowserScreens.Count - 1)].ScreenId;
            changed = true;
        }
        if (!canDelete)
            ImGui.EndDisabled();

        if (renamingScreenId == activeScreen.ScreenId)
            changed |= DrawRenameControls(activeScreen);

        var enabled = activeScreen.Enabled;
        if (ImGui.Checkbox("Screen enabled", ref enabled))
        {
            activeScreen.Enabled = enabled;
            changed = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{userScreenCount}/{Configuration.MaxBrowserScreens} local screens");

        return changed;
    }

    private static bool DrawScreenSourceCombo(BrowserScreenProfile activeScreen)
    {
        var changed = false;
        var current = FindSourceProviderIndex(activeScreen.ProviderKind);
        var sourceLocked = SourceControlUi.IsSourceControlsLocked(activeScreen);

        if (sourceLocked)
            ImGui.BeginDisabled();
        var providers = BrowserSourceProviderRegistry.Options;
        ImGui.TextDisabled("Screen source");
        ImGui.SetNextItemWidth(-1.0f);
        if (ImGui.BeginCombo("##CrystalCastScreenSource", providers[current].DisplayName))
        {
            for (var i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                var selected = i == current;
                if (ImGui.Selectable(provider.DisplayName, selected))
                {
                    activeScreen.ProviderKind = provider.Kind;
                    activeScreen.PlaybackPaused = false;
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        if (sourceLocked)
            ImGui.EndDisabled();

        return changed;
    }

    private bool DrawRenameControls(BrowserScreenProfile activeScreen)
    {
        var changed = false;
        var draft = renameDraft;
        ImGui.TextDisabled("Screen name");
        ImGui.SetNextItemWidth(-1.0f);
        var pressedEnter = ImGui.InputText("##CrystalCastScreenName", ref draft, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        renameDraft = draft;
        if (ImGui.Button("Save name") || pressedEnter)
        {
            var trimmed = renameDraft.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                activeScreen.Name = trimmed;
                changed = true;
            }

            renamingScreenId = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
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

    private static int FindSourceProviderIndex(BrowserSourceProviderKind providerKind)
    {
        var providers = BrowserSourceProviderRegistry.Options;
        for (var i = 0; i < providers.Count; i++)
        {
            if (providers[i].Kind == providerKind)
                return i;
        }

        return 0;
    }
}
