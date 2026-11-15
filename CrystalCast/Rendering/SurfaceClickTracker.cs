using System.Numerics;
using System.Runtime.InteropServices;

namespace CrystalCast.Rendering;

/// <summary>
/// Spots a click on the game world while surface snapping. ImGui doesn't see presses the game takes, so the
/// button is read from Windows. A press counts as a click when it starts over the game (not over a window),
/// is let go quickly and barely moves; the game locks the cursor while the camera turns, so time also matters.
/// </summary>
internal sealed class SurfaceClickTracker
{
    public const long MaxClickMilliseconds = 350;
    public const float MaxClickMovePixels = 5.0f;

    private bool wasDown;
    private Vector2? pressedAt;
    private long pressedAtMs;

    /// <summary>Feeds this frame's button state; true on the frame a click ends.</summary>
    public bool Update(bool down, bool overGame, Vector2 mouse, long nowMs)
    {
        var pressed = down && !wasDown;
        var released = !down && wasDown;
        wasDown = down;

        if (pressed)
        {
            pressedAt = overGame ? mouse : null;
            pressedAtMs = nowMs;
            return false;
        }

        if (!released || pressedAt is not { } start)
            return false;

        pressedAt = null;
        return nowMs - pressedAtMs <= MaxClickMilliseconds
            && Vector2.DistanceSquared(start, mouse) <= MaxClickMovePixels * MaxClickMovePixels;
    }

    public void Reset()
    {
        wasDown = false;
        pressedAt = null;
    }

    /// <summary>Whether the primary mouse button is down while the game window is the one in front.</summary>
    public static bool IsPrimaryButtonDown()
    {
        if (!IsGameInForeground())
            return false;

        var button = GetSystemMetrics(SmSwapButton) != 0 ? VkRButton : VkLButton;
        return (GetAsyncKeyState(button) & 0x8000) != 0;
    }

    private static bool IsGameInForeground()
    {
        var window = GetForegroundWindow();
        if (window == nint.Zero)
            return false;

        GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int SmSwapButton = 23;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
