namespace CrystalCast.Sync;

/// <summary>
/// The exact names of every IPC channel CrystalCast provides. See <c>docs/ipc-api.md</c> for what each one
/// takes and returns.
/// </summary>
public static class IpcChannels
{
    /// <summary>Function, <c>() =&gt; int</c>.</summary>
    public const string ApiVersion = "CrystalCast.ApiVersion";

    /// <summary>Function, <c>() =&gt; string</c> (JSON).</summary>
    public const string GetCapabilities = "CrystalCast.GetCapabilities";

    /// <summary>Event, no arguments: the functions below were just registered.</summary>
    public const string Ready = "CrystalCast.Ready";

    /// <summary>Event, no arguments: the functions below are about to be unregistered.</summary>
    public const string Disposed = "CrystalCast.Disposed";

    /// <summary>Function, <c>() =&gt; string</c> (JSON).</summary>
    public const string GetSnapshot = "CrystalCast.Screen.GetSnapshot";

    /// <summary>Function, <c>string stateJson =&gt; string</c> (JSON).</summary>
    public const string ApplyState = "CrystalCast.Screen.ApplyState";

    /// <summary>Function, <c>string requestJson =&gt; string</c> (JSON).</summary>
    public const string CreateScreen = "CrystalCast.Screen.Create";

    /// <summary>Function, <c>string requestJson =&gt; string</c> (JSON).</summary>
    public const string UpdateScreen = "CrystalCast.Screen.Update";

    /// <summary>Function, <c>string requestJson =&gt; string</c> (JSON).</summary>
    public const string UpdateSource = "CrystalCast.Screen.UpdateSource";

    /// <summary>Function, <c>string requestJson =&gt; string</c> (JSON).</summary>
    public const string SetSourceLock = "CrystalCast.Screen.SetSourceLock";

    /// <summary>Function, <c>string screenId =&gt; string</c> (JSON).</summary>
    public const string GetSourceState = "CrystalCast.Screen.GetSourceState";

    /// <summary>Function, <c>string screenId =&gt; string</c> (JSON).</summary>
    public const string RemoveScreen = "CrystalCast.Screen.Remove";

    /// <summary>Event, <c>string eventJson</c>.</summary>
    public const string ScreenChanged = "CrystalCast.Screen.Changed";

    /// <summary>Function, <c>string screenId =&gt; string</c> (JSON).</summary>
    public const string PlaylistGet = "CrystalCast.Screen.Playlist.Get";

    /// <summary>Function, <c>string requestJson =&gt; string</c> (JSON).</summary>
    public const string PlaylistAdd = "CrystalCast.Screen.Playlist.Add";

    /// <summary>Function, <c>string requestJson =&gt; string</c> (JSON).</summary>
    public const string PlaylistRemove = "CrystalCast.Screen.Playlist.Remove";

    /// <summary>Function, <c>string requestJson =&gt; string</c> (JSON).</summary>
    public const string PlaylistClear = "CrystalCast.Screen.Playlist.Clear";

    /// <summary>Function, <c>string requestJson =&gt; string</c> (JSON).</summary>
    public const string PlaylistSkip = "CrystalCast.Screen.Playlist.Skip";

    /// <summary>Function, <c>string requestJson =&gt; string</c> (JSON).</summary>
    public const string PlaylistMove = "CrystalCast.Screen.Playlist.Move";

    /// <summary>Function, <c>string requestJson =&gt; string</c> (JSON).</summary>
    public const string PlaylistSetOptions = "CrystalCast.Screen.Playlist.SetOptions";
}
