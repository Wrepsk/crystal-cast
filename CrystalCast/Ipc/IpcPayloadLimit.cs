using System.Text;

namespace CrystalCast.Ipc;

/// <summary>Size checks for IPC arguments, applied before any JSON is parsed.</summary>
internal static class IpcPayloadLimit
{
    /// <summary>Screen IDs are short GUID-like strings; anything longer is not a screen ID.</summary>
    internal const int MaxScreenIdLength = 256;

    public static bool TryCheck(string? json, out string error)
    {
        if (json != null
            && (json.Length > ScreenStateIpc.MaxPayloadBytes
                || Encoding.UTF8.GetByteCount(json) > ScreenStateIpc.MaxPayloadBytes))
        {
            error = $"Request must be at most {ScreenStateIpc.MaxPayloadBytes} UTF-8 bytes.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryCheckScreenId(string? screenId, out string error)
    {
        if (screenId != null && screenId.Length > MaxScreenIdLength)
        {
            error = $"Screen ID must be at most {MaxScreenIdLength} characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
