using CrystalCast.Rendering;
using Dalamud.Plugin.Ipc;
using System.Text;

namespace CrystalCast.Sync;

public sealed class ScreenStateIpc : IDisposable
{
    public const int ApiVersion = 7;
    private const int MaxStatePayloadBytes = 64 * 1024;

    private readonly Configuration configuration;
    private readonly string ownerSessionId;
    private readonly ScreenStateBuilder stateBuilder;
    private readonly ScreenChangePublisher changePublisher;
    private readonly ScreenIpcMutationService mutationService;
    private readonly ICallGateProvider<int> apiVersionProvider;
    private readonly ICallGateProvider<string> snapshotProvider;
    private readonly ICallGateProvider<string, bool> applyStateProvider;
    private readonly ICallGateProvider<string, string> applyStateDetailedProvider;
    private readonly ICallGateProvider<string, bool> removeProvider;
    private readonly ICallGateProvider<string, object> localStateChangedProvider;
    private readonly ICallGateProvider<string, string> createScreenProvider;
    private readonly ICallGateProvider<string, string> updateScreenProvider;
    private readonly ICallGateProvider<string, string> updateSourceProvider;
    private readonly ICallGateProvider<string, string> sourceLockProvider;
    private readonly ICallGateProvider<string, string> sourceStateProvider;
    private readonly RemoteScreenStateStore remoteScreens = new();
    private bool registered;

    public ScreenStateIpc(Configuration configuration, WorldScreenManager renderer)
    {
        this.configuration = configuration;
        ownerSessionId = Guid.NewGuid().ToString("N");
        stateBuilder = new ScreenStateBuilder(configuration, renderer, ownerSessionId);

        apiVersionProvider = Plugin.PluginInterface.GetIpcProvider<int>("CrystalCast.ApiVersion");
        snapshotProvider = Plugin.PluginInterface.GetIpcProvider<string>("CrystalCast.Screen.GetSnapshot");
        applyStateProvider = Plugin.PluginInterface.GetIpcProvider<string, bool>("CrystalCast.Screen.ApplyState");
        applyStateDetailedProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.ApplyStateDetailed");
        removeProvider = Plugin.PluginInterface.GetIpcProvider<string, bool>("CrystalCast.Screen.Remove");
        localStateChangedProvider = Plugin.PluginInterface.GetIpcProvider<string, object>("CrystalCast.Screen.LocalStateChanged");
        var screenChangedProvider = Plugin.PluginInterface.GetIpcProvider<string, object>("CrystalCast.Screen.Changed");
        changePublisher = new ScreenChangePublisher(configuration, screenChangedProvider, ownerSessionId);
        mutationService = new ScreenIpcMutationService(configuration, renderer, stateBuilder, changePublisher, PublishLocalState);
        createScreenProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Create");
        updateScreenProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.Update");
        updateSourceProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.UpdateSource");
        sourceLockProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.SetSourceLock");
        sourceStateProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("CrystalCast.Screen.GetSourceState");

        UpdateRegistration();
    }

    public IReadOnlyCollection<ScreenStateEnvelope> RemoteScreens => remoteScreens.GetSnapshot();

    public bool Enabled => configuration.IpcEnabled;

    public void SetEnabled(bool enabled)
    {
        if (configuration.IpcEnabled == enabled)
            return;

        configuration.IpcEnabled = enabled;
        if (!enabled)
        {
            SendUnavailableForKnownLocalScreens();
            RemoveIpcCreatedScreens();
            remoteScreens.Clear();
            changePublisher.Clear();
        }

        UpdateRegistration();
        configuration.Save();
    }

    public void UpdateRegistration()
    {
        if (configuration.IpcEnabled)
        {
            Register();
            return;
        }

        Unregister();
    }

    private void Register()
    {
        if (registered)
            return;

        apiVersionProvider.RegisterFunc(() => ApiVersion);
        snapshotProvider.RegisterFunc(GetSnapshotJson);
        applyStateProvider.RegisterFunc(ApplyStateJson);
        applyStateDetailedProvider.RegisterFunc(ApplyStateDetailedJson);
        removeProvider.RegisterFunc(Remove);
        createScreenProvider.RegisterFunc(mutationService.CreateScreenJson);
        updateScreenProvider.RegisterFunc(mutationService.UpdateScreenJson);
        updateSourceProvider.RegisterFunc(mutationService.UpdateSourceJson);
        sourceLockProvider.RegisterFunc(mutationService.SetSourceLockJson);
        sourceStateProvider.RegisterFunc(mutationService.GetSourceStateJson);
        registered = true;
    }

    private void Unregister()
    {
        if (!registered)
            return;

        apiVersionProvider.UnregisterFunc();
        snapshotProvider.UnregisterFunc();
        applyStateProvider.UnregisterFunc();
        applyStateDetailedProvider.UnregisterFunc();
        removeProvider.UnregisterFunc();
        createScreenProvider.UnregisterFunc();
        updateScreenProvider.UnregisterFunc();
        updateSourceProvider.UnregisterFunc();
        sourceLockProvider.UnregisterFunc();
        sourceStateProvider.UnregisterFunc();
        registered = false;
    }

    private void SendUnavailableForKnownLocalScreens()
    {
        changePublisher.SendUnavailableEventsForMissingLocalScreens([]);
    }

    private void RemoveIpcCreatedScreens()
    {
        configuration.Normalize();
        var removed = configuration.BrowserScreens.RemoveAll(screen => screen.CreatedByIpc);
        if (removed <= 0)
            return;

        configuration.Normalize();
    }

    public string PublishLocalState()
    {
        return PublishLocalState(null, null);
    }

    private string PublishLocalState(string? changedScreenId, IReadOnlyCollection<ScreenIpcChangeKind>? forcedChanges)
    {
        configuration.Normalize();
        UpdateRegistration();
        if (!configuration.IpcEnabled)
        {
            configuration.Save();
            return string.Empty;
        }

        var states = new List<ScreenStateEnvelope>();
        if (configuration.Enabled)
        {
            var screensToPublish = ScreenLimitPolicy.GetAllowedScreens(configuration.BrowserScreens)
                .Where(screen => screen.Enabled)
                .ToArray();

            foreach (var screen in screensToPublish)
            {
                if (!ScreenStateBuilder.TryResolveForIpc(screen.Placement, out var resolved))
                    continue;

                screen.LocalSequence++;
                states.Add(stateBuilder.BuildBrowserScreenState(screen, resolved));
            }
        }

        configuration.Save();

        var publishedScreenIds = states.Select(state => state.ScreenId).ToHashSet(StringComparer.Ordinal);
        changePublisher.RememberKnownLocalScreens(publishedScreenIds);

        string? firstJson = null;
        foreach (var state in states)
        {
            var json = IpcJsonService.Serialize(state);
            firstJson ??= json;
            localStateChangedProvider.SendMessage(json);
            changePublisher.MaybeSendScreenChanged(state, changedScreenId, forcedChanges);
        }

        changePublisher.SendUnavailableEventsForMissingLocalScreens(publishedScreenIds);

        if (forcedChanges is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(changedScreenId)
            && states.All(state => !string.Equals(state.ScreenId, changedScreenId, StringComparison.Ordinal))
            && FindBrowserScreen(changedScreenId) is { } forcedScreen
            && forcedScreen.Enabled
            && stateBuilder.TryBuildBrowserScreenState(forcedScreen, out var forcedState))
        {
            changePublisher.MaybeSendScreenChanged(forcedState, changedScreenId, forcedChanges);
        }

        return firstJson ?? string.Empty;
    }

    public ScreenStateEnvelope BuildLocalState()
    {
        return stateBuilder.BuildLocalState();
    }

    public IEnumerable<ScreenStateEnvelope> BuildLocalStates()
    {
        return stateBuilder.BuildLocalStates();
    }

    public void Dispose()
    {
        Unregister();
        remoteScreens.Clear();
        changePublisher.Clear();
    }

    private string GetSnapshotJson()
    {
        var localStates = BuildLocalStates().ToArray();
        changePublisher.RememberKnownLocalScreens(localStates.Select(state => state.ScreenId));
        var snapshot = new
        {
            schemaVersion = 1,
            apiVersion = ApiVersion,
            local = localStates.FirstOrDefault(),
            localScreens = localStates,
            remote = remoteScreens.GetSnapshot()
                .OrderBy(screen => screen.OwnerSessionId)
                .ThenBy(screen => screen.ScreenId)
                .ToArray(),
        };
        return IpcJsonService.Serialize(snapshot);
    }

    private bool ApplyStateJson(string json)
    {
        return ApplyState(json).Success;
    }

    private string ApplyStateDetailedJson(string json)
    {
        return IpcJsonService.Serialize(ApplyState(json));
    }

    private ScreenIpcApplyStateResponse ApplyState(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)
                || json.Length > MaxStatePayloadBytes
                || Encoding.UTF8.GetByteCount(json) > MaxStatePayloadBytes)
            {
                return new ScreenIpcApplyStateResponse
                {
                    Success = false,
                    Result = ScreenIpcApplyStateResult.RejectedInvalid,
                    Error = $"State payload must be at most {MaxStatePayloadBytes} UTF-8 bytes.",
                };
            }

            var state = IpcJsonService.Deserialize<ScreenStateEnvelope>(json);
            var result = remoteScreens.Apply(state, ownerSessionId, out var error);
            return new ScreenIpcApplyStateResponse
            {
                Success = result is RemoteScreenApplyResult.Applied
                    or RemoteScreenApplyResult.IgnoredSelf
                    or RemoteScreenApplyResult.IgnoredDuplicate
                    or RemoteScreenApplyResult.IgnoredStale,
                Result = result switch
                {
                    RemoteScreenApplyResult.Applied => ScreenIpcApplyStateResult.Applied,
                    RemoteScreenApplyResult.IgnoredSelf => ScreenIpcApplyStateResult.IgnoredSelf,
                    RemoteScreenApplyResult.IgnoredDuplicate => ScreenIpcApplyStateResult.IgnoredDuplicate,
                    RemoteScreenApplyResult.IgnoredStale => ScreenIpcApplyStateResult.IgnoredStale,
                    RemoteScreenApplyResult.RejectedCapacity => ScreenIpcApplyStateResult.RejectedCapacity,
                    _ => ScreenIpcApplyStateResult.RejectedInvalid,
                },
                Error = error,
                ScreenId = state?.ScreenId ?? string.Empty,
                OwnerSessionId = state?.OwnerSessionId ?? string.Empty,
            };
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to apply CrystalCast screen state IPC payload.");
            return new ScreenIpcApplyStateResponse
            {
                Success = false,
                Result = ScreenIpcApplyStateResult.RejectedInvalid,
                Error = "State payload is not valid JSON.",
            };
        }
    }

    private bool Remove(string screenId)
    {
        screenId = IpcJsonService.NormalizeText(screenId);
        if (string.IsNullOrWhiteSpace(screenId))
            return false;

        if (mutationService.Remove(screenId))
            return true;

        return remoteScreens.RemoveByScreenId(screenId);
    }

    private BrowserScreenProfile? FindBrowserScreen(string screenId)
    {
        if (string.IsNullOrWhiteSpace(screenId))
            return null;

        return configuration.BrowserScreens.FirstOrDefault(screen => string.Equals(screen.ScreenId, screenId, StringComparison.Ordinal));
    }
}
