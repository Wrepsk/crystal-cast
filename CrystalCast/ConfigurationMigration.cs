namespace CrystalCast;

internal static class ConfigurationMigration
{
    public static bool Normalize(Configuration configuration)
    {
        var changed = false;
        var migratingFromLocalVideo = (int)configuration.SourceKind == 2;

        if (string.IsNullOrWhiteSpace(configuration.ScreenId))
        {
            configuration.ScreenId = Guid.NewGuid().ToString("N");
            changed = true;
        }

        if (configuration.SourceKind != ScreenSourceKind.Browser)
        {
            configuration.SourceKind = ScreenSourceKind.Browser;
            changed = true;
        }

        if (configuration.LocalVideoPlacementMode is not (ScreenPlacementMode.World or ScreenPlacementMode.FollowPlayer or ScreenPlacementMode.FollowCamera))
        {
            configuration.LocalVideoPlacementMode = ScreenPlacementMode.World;
            changed = true;
        }

        if (configuration.PlacementGizmoOperation is not (ScreenPlacementGizmoOperation.Translate or ScreenPlacementGizmoOperation.Rotate))
        {
            configuration.PlacementGizmoOperation = ScreenPlacementGizmoOperation.Translate;
            changed = true;
        }

        if (configuration.BrowserScreens == null)
        {
            configuration.BrowserScreens = [];
            changed = true;
        }
        if (configuration.BrowserScreens.Count == 0)
        {
            configuration.BrowserScreens.Add(configuration.CreateBrowserScreenFromLegacySettings("Browser screen 1"));
            changed = true;
        }

        var normalizedOutputMode = ScreenOutputModePolicy.Normalize(configuration.OutputMode);
        if (configuration.OutputMode != normalizedOutputMode)
        {
            configuration.OutputMode = normalizedOutputMode;
            changed = true;
        }

        var usedScreenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < configuration.BrowserScreens.Count; i++)
            changed |= configuration.BrowserScreens[i].Normalize($"Browser screen {i + 1}", usedScreenIds);

        changed |= ScreenLimitPolicy.DisableScreensOutsideLimits(configuration.BrowserScreens);

        if (migratingFromLocalVideo)
        {
            foreach (var screen in configuration.BrowserScreens.Where(screen => screen.Enabled))
            {
                screen.Enabled = false;
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(configuration.ActiveBrowserScreenId)
            || configuration.BrowserScreens.All(screen => screen.ScreenId != configuration.ActiveBrowserScreenId))
        {
            configuration.ActiveBrowserScreenId = configuration.BrowserScreens[0].ScreenId;
            changed = true;
        }

        if (configuration.PlacementPresets == null)
        {
            configuration.PlacementPresets = [];
            changed = true;
        }

        var usedPresetIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < configuration.PlacementPresets.Count; i++)
            changed |= configuration.PlacementPresets[i].Normalize($"Placement {i + 1}", usedPresetIds);

        if (configuration.PlacementPresets.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(configuration.ActivePlacementPresetId))
            {
                configuration.ActivePlacementPresetId = string.Empty;
                changed = true;
            }
        }
        else if (string.IsNullOrWhiteSpace(configuration.ActivePlacementPresetId)
            || configuration.PlacementPresets.All(preset => preset.PresetId != configuration.ActivePlacementPresetId))
        {
            configuration.ActivePlacementPresetId = configuration.PlacementPresets[0].PresetId;
            changed = true;
        }

        if (configuration.Version < 2)
        {
            configuration.Version = 2;
            changed = true;
        }

        return changed;
    }
}
