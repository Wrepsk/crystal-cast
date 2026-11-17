using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace CrystalCast.Rendering;

/// <summary>
/// Reads the mouse wheel over the game while the plugin needs it, and keeps it from the game. With the mouse
/// over the game, ImGui never sees the wheel, the same way it misses clicks (see SurfaceClickTracker), so a
/// mouse hook on the game window's thread counts the steps before the game gets them. Installed only while
/// picking a spot for a screen, and removed right after.
/// </summary>
internal sealed class GameMouseWheelHook : IDisposable
{
    private const int WhMouse = 7;
    private const int HcAction = 0;
    private const int WmMouseWheel = 0x020A;
    private const int WheelDelta = 120;
    private const int VkControl = 0x11;

    // Kept in a field: the hook calls into it for as long as it's installed.
    private readonly HookProc proc;
    private nint hook;
    private int pendingDelta;
    private volatile bool swallow;
    private long swallowSetTick;

    /// <summary>
    /// The wheel goes back to the game when the plugin stops saying otherwise this long, for example when its
    /// window closes in the middle of picking and nothing calls <see cref="Stop"/>.
    /// </summary>
    internal const long SwallowExpiryMs = 250;

    public GameMouseWheelHook()
    {
        proc = OnMouse;
    }

    public bool Active => hook != nint.Zero;

    /// <summary>Starts counting. False when the game window or the hook isn't available.</summary>
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

        Interlocked.Exchange(ref pendingDelta, 0);
        hook = SetWindowsHookEx(WhMouse, proc, nint.Zero, thread);
        return hook != nint.Zero;
    }

    public void Stop()
    {
        if (hook == nint.Zero)
            return;

        UnhookWindowsHookEx(hook);
        hook = nint.Zero;
        swallow = false;
        Interlocked.Exchange(ref pendingDelta, 0);
    }

    /// <summary>
    /// Whether wheel steps are kept from the game. Set each frame: on while the mouse is over the game, off over
    /// a plugin window so its lists still scroll.
    /// </summary>
    public bool Swallow
    {
        set
        {
            swallow = value;
            Interlocked.Exchange(ref swallowSetTick, Environment.TickCount64);
        }
    }

    internal static bool IsSwallowing(bool swallow, long setTick, long nowTick) =>
        swallow && nowTick - setTick < SwallowExpiryMs;

    /// <summary>Wheel steps since the last call (up is positive), in notches; partial notches carry over.</summary>
    public float TakeNotches()
    {
        var delta = Interlocked.Exchange(ref pendingDelta, 0);
        return delta / (float)WheelDelta;
    }

    /// <summary>Whether Ctrl is held, read from Windows since ImGui may not see keys the game has.</summary>
    public static bool IsControlDown() => (GetAsyncKeyState(VkControl) & 0x8000) != 0;

    public void Dispose() => Stop();

    /// <summary>
    /// Whether a hook call is the message being taken from the queue (counted and blocked) rather than a peek
    /// that leaves it there (HC_NOREMOVE, passed on).
    /// </summary>
    internal static bool ShouldCount(int code) => code == HcAction;

    /// <summary>The wheel step from a hook message's data: the high word, signed.</summary>
    internal static int WheelDeltaOf(uint mouseData) => (short)(mouseData >> 16);

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0 && message == WmMouseWheel && IsSwallowing(swallow, Interlocked.Read(ref swallowSetTick), Environment.TickCount64))
        {
            var info = Marshal.PtrToStructure<MouseHookStructEx>(data);
            Plugin.Log.Debug("CrystalCast wheel hook: code {Code}, delta {Delta}.", code, WheelDeltaOf(info.MouseData));

            // A peek that leaves the message in the queue is let through: blocking it there drops the message,
            // so it would never be taken, and never counted. It's counted and blocked when it's taken.
            if (!ShouldCount(code))
                return CallNextHookEx(hook, code, message, data);

            Interlocked.Add(ref pendingDelta, WheelDeltaOf(info.MouseData));
            return 1;
        }

        return CallNextHookEx(hook, code, message, data);
    }

    private delegate nint HookProc(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookStructEx
    {
        public int X;
        public int Y;
        public nint Window;
        public uint HitTestCode;
        public nint ExtraInfo;
        public uint MouseData;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookType, HookProc proc, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
