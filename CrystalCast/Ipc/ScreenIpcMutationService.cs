using CrystalCast.Rendering;
using Dalamud.Plugin.Services;

namespace CrystalCast.Ipc;

/// <summary>
/// The calls that create, change and remove screens: <c>Screen.Create</c>, <c>Screen.Update</c>,
/// <c>Screen.UpdateSource</c>, <c>Screen.SetSourceLock</c>, <c>Screen.Remove</c>, and the
/// <c>Screen.GetSourceState</c> read.
/// </summary>
internal sealed class ScreenIpcMutationService
{
    private readonly Configuration configuration;
    private readonly WorldScreenManager renderer;
    private readonly ScreenStateBuilder stateBuilder;
    private readonly ScreenChangePublisher changePublisher;
    private readonly Action<string?, IReadOnlyCollection<ScreenIpcChangeKind>?> publishLocalState;
    private readonly IPluginLog log;
    private readonly HiddenIpcScreens hiddenScreens;

    public ScreenIpcMutationService(
        Configuration configuration,
        WorldScreenManager renderer,
        ScreenStateBuilder stateBuilder,
        ScreenChangePublisher changePublisher,
        Action<string?, IReadOnlyCollection<ScreenIpcChangeKind>?> publishLocalState,
        IPluginLog log,
        HiddenIpcScreens hiddenScreens)
    {
        this.hiddenScreens = hiddenScreens;
        this.configuration = configuration;
        this.renderer = renderer;
        this.stateBuilder = stateBuilder;
        this.changePublisher = changePublisher;
        this.publishLocalState = publishLocalState;
        this.log = log;
    }

    /// <summary>Removes a screen created through IPC. The user's own screens are left alone.</summary>
    public bool Remove(string screenId)
    {
        screenId = IpcJsonService.NormalizeText(screenId);
        if (string.IsNullOrWhiteSpace(screenId))
            return false;

        configuration.Normalize();
        var screen = configuration.FindBrowserScreen(screenId);
        if (screen is not { CreatedByIpc: true })
            return false;

        configuration.BrowserScreens.Remove(screen);
        configuration.Normalize();
        configuration.Save();
        changePublisher.SendUnavailableAndForget(screenId, screen, ScreenIpcRemovalReason.RemovedByIpc);
        return true;
    }

    /// <summary>
    /// The user deleted an IPC screen from the screen list (already removed from the configuration). With
    /// <paramref name="hide"/>, other plugins cannot create it again this session.
    /// </summary>
    public void RemovedByUser(BrowserScreenProfile screen, bool hide)
    {
        if (hide)
            hiddenScreens.Hide(screen.ScreenId);

        changePublisher.SendUnavailableAndForget(
            screen.ScreenId,
            screen,
            hide ? ScreenIpcRemovalReason.HiddenByUser : ScreenIpcRemovalReason.RemovedByUser);
    }

    public string CreateScreenJson(string json) =>
        Handle<ScreenIpcMutationRequest>(json, "create", "screen", CreateScreen);

    public string UpdateScreenJson(string json) => Handle<ScreenIpcMutationRequest>(json, "update", "screen", request =>
    {
        configuration.Normalize();
        var screenId = IpcJsonService.NormalizeText(request.ScreenId);
        var screen = configuration.FindBrowserScreen(screenId);
        if (screen == null)
        {
            return request.CreateIfMissing && !string.IsNullOrWhiteSpace(screenId)
                ? CreateScreen(request)
                : NotFound(screenId);
        }

        if (!ScreenPatchApplier.TryApplyScreenMutation(screen, request, out var updatedScreen, out var error))
            return IpcJsonService.SerializeMutationError(error, screen.ScreenId);

        return Commit(
            screen,
            updatedScreen,
            request.Activate,
            request.Source,
            ScreenChangePublisher.GetMutationChangeKinds(request));
    });

    public string UpdateSourceJson(string json) => Handle<ScreenIpcSourceUpdateRequest>(json, "update", "source", request =>
    {
        configuration.Normalize();
        var screenId = IpcJsonService.NormalizeText(request.ScreenId);
        var screen = configuration.FindBrowserScreen(screenId);
        if (screen == null)
            return NotFound(screenId);

        if (!ScreenPatchApplier.TryApplySourceMutation(screen, request, out var updatedScreen, out var error))
            return IpcJsonService.SerializeMutationError(error, screen.ScreenId);

        return Commit(
            screen,
            updatedScreen,
            request.Activate,
            request.Source,
            ScreenChangePublisher.GetSourceUpdateChangeKinds(request));
    });

    public string SetSourceLockJson(string json) => Handle<ScreenIpcSourceLockRequest>(json, "update", "source lock", request =>
    {
        configuration.Normalize();
        var screenId = IpcJsonService.NormalizeText(request.ScreenId);
        var screen = configuration.FindBrowserScreen(screenId);
        if (screen == null)
            return NotFound(screenId);

        ScreenPatchApplier.ApplySourceLock(screen, request);

        configuration.Save();
        publishLocalState(screen.ScreenId, [ScreenIpcChangeKind.SourceLock]);
        return IpcJsonService.SerializeMutationSuccess(screen, created: false);
    });

    public string GetSourceStateJson(string screenId)
    {
        try
        {
            configuration.Normalize();
            var screen = string.IsNullOrWhiteSpace(screenId)
                ? configuration.GetActiveBrowserScreen()
                : configuration.FindBrowserScreen(screenId);
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

    private string CreateScreen(ScreenIpcMutationRequest request)
    {
        try
        {
            configuration.Normalize();
            if (hiddenScreens.IsHidden(IpcJsonService.NormalizeText(request.ScreenId)))
                return IpcJsonService.SerializeHiddenByUserError(request.ScreenId);

            if (!ScreenLimitPolicy.CanCreateIpcScreen(configuration.BrowserScreens))
                return IpcJsonService.SerializeMutationError($"CrystalCast can create at most {Configuration.MaxIpcBrowserScreens} IPC browser screens and {Configuration.MaxRenderableBrowserScreens} browser screens in total.");

            var requestedScreenId = IpcJsonService.NormalizeText(request.ScreenId);
            if (configuration.FindBrowserScreen(requestedScreenId) != null)
                return IpcJsonService.SerializeMutationError($"Screen '{requestedScreenId}' already exists.", requestedScreenId);

            if (request.Provider is { } provider && !BrowserSourceIpcAdapters.IsSupported(provider))
                return IpcJsonService.SerializeMutationError(ScreenPatchApplier.UnsupportedProviderMessage(provider));

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

    /// <summary>Stores a changed screen, sends the patch's player commands, and announces the change.</summary>
    private string Commit(
        BrowserScreenProfile current,
        BrowserScreenProfile updated,
        bool activate,
        ScreenSourcePatchDto? sourcePatch,
        ScreenIpcChangeKind[] changes)
    {
        var index = configuration.BrowserScreens.IndexOf(current);
        if (index < 0)
            throw new InvalidOperationException($"Screen '{current.ScreenId}' is no longer part of the configuration.");

        configuration.BrowserScreens[index] = updated;
        if (activate)
            configuration.ActiveBrowserScreenId = updated.ScreenId;

        configuration.Normalize();
        configuration.Save();
        BrowserSourceIpcAdapters.ApplyRuntimeControls(renderer, updated, sourcePatch);
        publishLocalState(updated.ScreenId, changes);
        return IpcJsonService.SerializeMutationSuccess(updated, created: false);
    }

    /// <summary>Parses the request and runs the handler, turning a missing body or an exception into an error answer.</summary>
    private string Handle<TRequest>(string json, string verb, string subject, Func<TRequest, string> handler)
        where TRequest : class
    {
        try
        {
            var request = IpcJsonService.Deserialize<TRequest>(json);
            return request == null
                ? IpcJsonService.SerializeMutationError("Request body is empty.")
                : handler(request);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to {Verb:l} CrystalCast {Subject:l} through IPC.", verb, subject);
            return IpcJsonService.SerializeMutationError($"Failed to {verb} {subject}: {ex.Message}");
        }
    }

    private static string NotFound(string screenId)
    {
        return IpcJsonService.SerializeMutationError($"Screen '{screenId}' was not found.", screenId);
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
