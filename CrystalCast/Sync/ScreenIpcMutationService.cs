using CrystalCast.Rendering;

namespace CrystalCast.Sync;

internal sealed class ScreenIpcMutationService
{
    private readonly Configuration configuration;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateBuilder stateBuilder;
    private readonly ScreenChangePublisher changePublisher;
    private readonly Func<string?, IReadOnlyCollection<ScreenIpcChangeKind>?, string> publishLocalState;

    public ScreenIpcMutationService(
        Configuration configuration,
        WorldScreenManager renderer,
        ScreenStateBuilder stateBuilder,
        ScreenChangePublisher changePublisher,
        Func<string?, IReadOnlyCollection<ScreenIpcChangeKind>?, string> publishLocalState)
    {
        this.configuration = configuration;
        this.renderer = renderer;
        this.stateBuilder = stateBuilder;
        this.changePublisher = changePublisher;
        this.publishLocalState = publishLocalState;
    }

    public bool Remove(string screenId)
    {
        screenId = IpcJsonService.NormalizeText(screenId);
        if (string.IsNullOrWhiteSpace(screenId))
            return false;

        configuration.Normalize();
        var screen = FindBrowserScreen(screenId);
        if (screen is not { CreatedByIpc: true })
            return false;

        configuration.BrowserScreens.Remove(screen);
        configuration.Normalize();
        configuration.Save();
        changePublisher.Remove(screenId);
        return true;
    }

    public string CreateScreenJson(string json)
    {
        try
        {
            var request = IpcJsonService.Deserialize<ScreenIpcMutationRequest>(json);
            if (request == null)
                return IpcJsonService.SerializeMutationError("Request body is empty.");

            configuration.Normalize();
            if (configuration.BrowserScreens.Count >= Configuration.MaxRenderableBrowserScreens)
                return IpcJsonService.SerializeMutationError($"CrystalCast can render at most {Configuration.MaxRenderableBrowserScreens} browser screens.");

            var requestedScreenId = IpcJsonService.NormalizeText(request.ScreenId);
            if (!string.IsNullOrWhiteSpace(requestedScreenId) && FindBrowserScreen(requestedScreenId) != null)
                return IpcJsonService.SerializeMutationError($"Screen '{requestedScreenId}' already exists.", requestedScreenId);

            if (request.Provider is { } provider && !ScreenPatchApplier.IsSupportedBrowserProvider(provider))
                return IpcJsonService.SerializeMutationError($"Unsupported browser source provider '{provider}'.");

            var name = IpcJsonService.NormalizeText(request.Name);
            if (string.IsNullOrWhiteSpace(name))
                name = GetNextIpcScreenName();

            var screen = configuration.CreateDefaultBrowserScreen(name);
            if (!string.IsNullOrWhiteSpace(requestedScreenId))
                screen.ScreenId = requestedScreenId;

            screen.CreatedByIpc = true;
            screen.IpcOwnerId = IpcJsonService.NormalizeText(request.OwnerId);
            screen.SourceControlsOwnerId = IpcJsonService.NormalizeText(request.SourceControlsOwnerId);
            if (request.SourceControlsLocked == true && string.IsNullOrWhiteSpace(screen.SourceControlsOwnerId))
                screen.SourceControlsOwnerId = screen.IpcOwnerId;

            if (!ScreenPatchApplier.ApplyScreenMutation(screen, request, out var error))
                return IpcJsonService.SerializeMutationError(error, screen.ScreenId);

            configuration.BrowserScreens.Add(screen);
            configuration.SourceKind = ScreenSourceKind.YouTubeBrowser;
            if (request.Activate)
                configuration.ActiveBrowserScreenId = screen.ScreenId;

            configuration.Normalize();
            configuration.Save();
            publishLocalState(screen.ScreenId, ScreenChangePublisher.GetCreateChangeKinds());
            return IpcJsonService.SerializeMutationSuccess(screen, created: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to create CrystalCast screen through IPC.");
            return IpcJsonService.SerializeMutationError($"Failed to create screen: {ex.Message}");
        }
    }

    public string UpdateScreenJson(string json)
    {
        try
        {
            var request = IpcJsonService.Deserialize<ScreenIpcMutationRequest>(json);
            if (request == null)
                return IpcJsonService.SerializeMutationError("Request body is empty.");

            configuration.Normalize();
            var screenId = IpcJsonService.NormalizeText(request.ScreenId);
            var screen = FindBrowserScreen(screenId);
            if (screen == null)
                return IpcJsonService.SerializeMutationError($"Screen '{screenId}' was not found.", screenId);

            if (!ScreenPatchApplier.ApplyScreenMutation(screen, request, out var error))
                return IpcJsonService.SerializeMutationError(error, screen.ScreenId);

            configuration.SourceKind = ScreenSourceKind.YouTubeBrowser;
            if (request.Activate)
                configuration.ActiveBrowserScreenId = screen.ScreenId;

            configuration.Normalize();
            configuration.Save();
            ApplyRuntimeControls(screen, request.YouTube, request.Twitch);
            publishLocalState(screen.ScreenId, ScreenChangePublisher.GetMutationChangeKinds(request));
            return IpcJsonService.SerializeMutationSuccess(screen, created: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to update CrystalCast screen through IPC.");
            return IpcJsonService.SerializeMutationError($"Failed to update screen: {ex.Message}");
        }
    }

    public string SetSourceLockJson(string json)
    {
        try
        {
            var request = IpcJsonService.Deserialize<ScreenIpcSourceLockRequest>(json);
            if (request == null)
                return IpcJsonService.SerializeMutationError("Request body is empty.");

            configuration.Normalize();
            var screenId = IpcJsonService.NormalizeText(request.ScreenId);
            var screen = FindBrowserScreen(screenId);
            if (screen == null)
                return IpcJsonService.SerializeMutationError($"Screen '{screenId}' was not found.", screenId);

            ScreenPatchApplier.ApplySourceLock(screen, request);

            configuration.Save();
            publishLocalState(screen.ScreenId, [ScreenIpcChangeKind.SourceLock]);
            return IpcJsonService.SerializeMutationSuccess(screen, created: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to update CrystalCast source lock through IPC.");
            return IpcJsonService.SerializeMutationError($"Failed to update source lock: {ex.Message}");
        }
    }

    public string UpdateSourceJson(string json)
    {
        try
        {
            var request = IpcJsonService.Deserialize<ScreenIpcSourceUpdateRequest>(json);
            if (request == null)
                return IpcJsonService.SerializeMutationError("Request body is empty.");

            configuration.Normalize();
            var screenId = IpcJsonService.NormalizeText(request.ScreenId);
            var screen = FindBrowserScreen(screenId);
            if (screen == null)
                return IpcJsonService.SerializeMutationError($"Screen '{screenId}' was not found.", screenId);

            if (request.Provider is { } provider && !ScreenPatchApplier.IsSupportedBrowserProvider(provider))
                return IpcJsonService.SerializeMutationError($"Unsupported browser source provider '{provider}'.", screen.ScreenId);

            var providerKind = ScreenPatchApplier.ResolveRequestedProvider(screen, request.Provider, request.YouTube, request.Twitch);
            screen.ProviderKind = providerKind;
            if (!ScreenPatchApplier.ApplyProviderPatch(screen, providerKind, request.YouTube, request.Twitch, out var error))
                return IpcJsonService.SerializeMutationError(error, screen.ScreenId);

            configuration.SourceKind = ScreenSourceKind.YouTubeBrowser;
            if (request.Activate)
                configuration.ActiveBrowserScreenId = screen.ScreenId;

            configuration.Normalize();
            configuration.Save();
            ApplyRuntimeControls(screen, request.YouTube, request.Twitch);
            publishLocalState(screen.ScreenId, ScreenChangePublisher.GetSourceUpdateChangeKinds(request));
            return IpcJsonService.SerializeMutationSuccess(screen, created: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to update CrystalCast source through IPC.");
            return IpcJsonService.SerializeMutationError($"Failed to update source: {ex.Message}");
        }
    }

    public string GetSourceStateJson(string screenId)
    {
        try
        {
            configuration.Normalize();
            var screen = string.IsNullOrWhiteSpace(screenId)
                ? configuration.GetActiveBrowserScreen()
                : FindBrowserScreen(screenId);
            if (screen == null)
            {
                return IpcJsonService.Serialize(new ScreenIpcSourceStateResponse
                {
                    Success = false,
                    Error = $"Screen '{screenId}' was not found.",
                    ScreenId = IpcJsonService.NormalizeText(screenId),
                });
            }

            return IpcJsonService.Serialize(stateBuilder.BuildSourceStateResponse(screen));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to read CrystalCast source state through IPC.");
            return IpcJsonService.Serialize(new ScreenIpcSourceStateResponse
            {
                Success = false,
                Error = $"Failed to get source state: {ex.Message}",
                ScreenId = IpcJsonService.NormalizeText(screenId),
            });
        }
    }

    private void ApplyRuntimeControls(BrowserScreenProfile screen, YouTubeScreenPatchDto? youtube, TwitchScreenPatchDto? twitch)
    {
        if (screen.ProviderKind == BrowserSourceProviderKind.Twitch)
        {
            ApplyTwitchRuntimeControls(screen, twitch);
            return;
        }

        ApplyYouTubeRuntimeControls(screen, youtube);
    }

    private void ApplyYouTubeRuntimeControls(BrowserScreenProfile screen, YouTubeScreenPatchDto? patch)
    {
        if (patch == null)
            return;

        if (patch.Restart)
        {
            renderer.TryRestartDynamicSource(screen);
        }
        else if (patch.PositionMs.HasValue)
        {
            var seconds = Math.Max(0.0, patch.PositionMs.Value / 1000.0);
            renderer.TrySeekDynamicSourceTo(screen, seconds);
        }

        if (!patch.PlaybackPaused.HasValue)
            return;

        if (patch.PlaybackPaused.Value)
            renderer.TryPauseDynamicSource(screen);
        else
            renderer.TryPlayDynamicSource(screen);
    }

    private void ApplyTwitchRuntimeControls(BrowserScreenProfile screen, TwitchScreenPatchDto? patch)
    {
        if (patch == null)
            return;

        if (patch.Restart)
        {
            renderer.TryRestartDynamicSource(screen);
        }
        else if (patch.PositionMs.HasValue)
        {
            var seconds = Math.Max(0.0, patch.PositionMs.Value / 1000.0);
            renderer.TrySeekDynamicSourceTo(screen, seconds);
        }

        if (!patch.PlaybackPaused.HasValue)
            return;

        if (patch.PlaybackPaused.Value)
            renderer.TryPauseDynamicSource(screen);
        else
            renderer.TryPlayDynamicSource(screen);
    }

    private BrowserScreenProfile? FindBrowserScreen(string screenId)
    {
        if (string.IsNullOrWhiteSpace(screenId))
            return null;

        return configuration.BrowserScreens.FirstOrDefault(screen => string.Equals(screen.ScreenId, screenId, StringComparison.Ordinal));
    }

    private string GetNextIpcScreenName()
    {
        for (var i = 1; ; i++)
        {
            var name = $"IPC screen {i}";
            if (configuration.BrowserScreens.All(screen => !string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }
}
