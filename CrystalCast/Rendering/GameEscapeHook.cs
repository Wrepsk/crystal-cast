using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace CrystalCast.Rendering;

/// <summary>
/// Catches Escape on the game window while the plugin wants it (to cancel picking a spot for a screen) and keeps
/// it from the game, which would otherwise close a window, drop the target or open the system menu. Like
/// <see cref="GameMouseWheelHook"/>, a keyboard hook on the game window's thread sees the key before the game.
/// </summary>
internal sealed class GameEscapeHook : IDisposable
{
    private const int WhKeyboard = 2;
    private const int HcAction = 0;
    private const int VkEscape = 0x1B;

    // Kept in a field: the hook calls into it for as long as it's installed.
    private readonly HookProc proc;
    private nint hook;
    private int pressed;
    private volatile bool capture;
    private long captureSetTick;

    public GameEscapeHook()
    {
        proc = OnKey;
    }

    public bool Active => hook != nint.Zero;

    public unsafe bool Start()
    {
        if (hook != nint.Zero)
            return true;

        var framework = Framework.Instance();
        var window = framework != null && framework->GameWindow != null ? framework->GameWindow->WindowHandle : nint.Zero;
        if (window == nint.Zero)
            return false;

        var thread = GetWindowThreadProcessId(window, out _);
        if (thread == 0)
            return false;

        Interlocked.Exchange(ref pressed, 0);
        hook = SetWindowsHookEx(WhKeyboard, proc, nint.Zero, thread);
        return hook != nint.Zero;
    }

    public void Stop()
    {
        if (hook == nint.Zero)
            return;

        UnhookWindowsHookEx(hook);
        hook = nint.Zero;
        capture = false;
        Interlocked.Exchange(ref pressed, 0);
    }

    /// <summary>
    /// Whether Escape is taken from the game. Set each frame: off while a plugin text field has the keyboard, so
    /// Escape still works there. It lapses by itself when the plugin stops setting it.
    /// </summary>
    public bool Capture
    {
        set
        {
            capture = value;
            Interlocked.Exchange(ref captureSetTick, Environment.TickCount64);
        }
    }

    /// <summary>True once per Escape press caught since the last call.</summary>
    public bool TakePress() => Interlocked.Exchange(ref pressed, 0) != 0;

    public void Dispose() => Stop();

    /// <summary>A key going down, from the keyboard hook's message flags: bit 31 is set while it's released.</summary>
    internal static bool IsKeyDown(nint flags) => ((long)flags & 0x80000000L) == 0;

    private nint OnKey(int code, nint key, nint flags)
    {
        var capturing = capture && Environment.TickCount64 - Interlocked.Read(ref captureSetTick) < GameMouseWheelHook.SwallowExpiryMs;
        // As with the wheel: a peek that leaves the key in the queue passes; the key is caught when it's taken.
        if (code == HcAction && key == VkEscape && capturing)
        {
            if (IsKeyDown(flags))
                Interlocked.Exchange(ref pressed, 1);
            return 1;
        }

        return CallNextHookEx(hook, code, key, flags);
    }

    private delegate nint HookProc(int code, nint key, nint flags);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookType, HookProc proc, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint key, nint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
