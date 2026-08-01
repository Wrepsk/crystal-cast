using CrystalCast.Rendering;
using Dalamud.Plugin.Services;

namespace CrystalCast.Sync;

internal sealed class ScreenIpcMutationService
{
    private readonly Configuration configuration;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateBuilder stateBuilder;
    private readonly ScreenChangePublisher changePublisher;
    private readonly Func<string?, IReadOnlyCollection<ScreenIpcChangeKind>?, string> publishLocalState;
    private readonly IPluginLog log;

    public ScreenIpcMutationService(
        Configuration configuration,
        WorldScreenManager renderer,
        ScreenStateBuilder stateBuilder,
        ScreenChangePublisher changePublisher,
        Func<string?, IReadOnlyCollection<ScreenIpcChangeKind>?, string> publishLocalState,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.renderer = renderer;
        this.stateBuilder = stateBuilder;
        this.changePublisher = changePublisher;
        this.publishLocalState = publishLocalState;
        this.log = log;
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
        changePublisher.SendUnavailableAndForget(screenId, screen);
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
            if (!ScreenLimitPolicy.CanCreateIpcScreen(configuration.BrowserScreens))
                return IpcJsonService.SerializeMutationError($"CrystalCast can create at most {Configuration.MaxIpcBrowserScreens} IPC browser screens and {Configuration.MaxRenderableBrowserScreens} browser screens in total.");

            var requestedScreenId = IpcJsonService.NormalizeText(request.ScreenId);
            if (!string.IsNullOrWhiteSpace(requestedScreenId) && FindBrowserScreen(requestedScreenId) != null)
                return IpcJsonService.SerializeMutationError($"Screen '{requestedScreenId}' already exists.", requestedScreenId);

            if (request.Provider is { } provider && !ScreenPatchApplier.IsSupportedBrowserProvider(provider))
                return IpcJsonService.SerializeMutationError($"Unsupported browser source provider '{provider}'.");

            var name = IpcJsonService.NormalizeText(request.Name);
            if (string.IsNullOrWhiteSpace(name))
                name = GetNextIpcScreenName();

            var screen = configuration.CreateDefaultBrowserScreen(name, createdByIpc: true);
            if (!string.IsNullOrWhiteSpace(requestedScreenId))
                screen.ScreenId = requestedScreenId;

            screen.IpcOwnerId = IpcJsonService.NormalizeText(request.OwnerId);
            screen.SourceControlsOwnerId = IpcJsonService.NormalizeText(request.SourceControlsOwnerId);
            if (request.SourceControlsLocked == true && string.IsNullOrWhiteSpace(screen.SourceControlsOwnerId))
                screen.SourceControlsOwnerId = screen.IpcOwnerId;

            if (!ScreenPatchApplier.TryApplyScreenMutation(screen, request, out var createdScreen, out var error))
                return IpcJsonService.SerializeMutationError(error, screen.ScreenId);

            configuration.BrowserScreens.Add(createdScreen);
            if (request.Activate)
                configuration.ActiveBrowserScreenId = createdScreen.ScreenId;

            configuration.Normalize();
            configuration.Save();
            publishLocalState(createdScreen.ScreenId, ScreenChangePublisher.GetCreateChangeKinds());
            return IpcJsonService.SerializeMutationSuccess(createdScreen, created: true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to create CrystalCast screen through IPC.");
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

            if (!ScreenPatchApplier.TryApplyScreenMutation(screen, request, out var updatedScreen, out var error))
                return IpcJsonService.SerializeMutationError(error, screen.ScreenId);

            ReplaceBrowserScreen(screen, updatedScreen);
            if (request.Activate)
                configuration.ActiveBrowserScreenId = updatedScreen.ScreenId;

            configuration.Normalize();
            configuration.Save();
            ApplyRuntimeControls(updatedScreen, request);
            publishLocalState(updatedScreen.ScreenId, ScreenChangePublisher.GetMutationChangeKinds(request));
            return IpcJsonService.SerializeMutationSuccess(updatedScreen, created: false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to update CrystalCast screen through IPC.");
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
            log.Warning(ex, "Failed to update CrystalCast source lock through IPC.");
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

            if (!ScreenPatchApplier.TryApplySourceMutation(screen, request, out var updatedScreen, out var error))
                return IpcJsonService.SerializeMutationError(error, screen.ScreenId);

            ReplaceBrowserScreen(screen, updatedScreen);
            if (request.Activate)
                configuration.ActiveBrowserScreenId = updatedScreen.ScreenId;

            configuration.Normalize();
            configuration.Save();
            ApplyRuntimeControls(updatedScreen, request);
            publishLocalState(updatedScreen.ScreenId, ScreenChangePublisher.GetSourceUpdateChangeKinds(request));
            return IpcJsonService.SerializeMutationSuccess(updatedScreen, created: false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to update CrystalCast source through IPC.");
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
            log.Warning(ex, "Failed to read CrystalCast source state through IPC.");
            return IpcJsonService.Serialize(new ScreenIpcSourceStateResponse
            {
                Success = false,
                Error = $"Failed to get source state: {ex.Message}",
                ScreenId = IpcJsonService.NormalizeText(screenId),
            });
        }
    }

    private void ApplyRuntimeControls(BrowserScreenProfile screen, ScreenIpcMutationRequest request)
    {
        BrowserSourceIpcAdapters.Get(screen.ProviderKind).ApplyRuntimeControls(renderer, screen, request);
    }

    private void ApplyRuntimeControls(BrowserScreenProfile screen, ScreenIpcSourceUpdateRequest request)
    {
        BrowserSourceIpcAdapters.Get(screen.ProviderKind).ApplyRuntimeControls(renderer, screen, request);
    }

    private BrowserScreenProfile? FindBrowserScreen(string screenId)
    {
        if (string.IsNullOrWhiteSpace(screenId))
            return null;

        return configuration.BrowserScreens.FirstOrDefault(screen => string.Equals(screen.ScreenId, screenId, StringComparison.Ordinal));
    }

    private void ReplaceBrowserScreen(BrowserScreenProfile current, BrowserScreenProfile updated)
    {
        var index = configuration.BrowserScreens.IndexOf(current);
        if (index < 0)
            throw new InvalidOperationException($"Screen '{current.ScreenId}' is no longer part of the configuration.");

        configuration.BrowserScreens[index] = updated;
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
