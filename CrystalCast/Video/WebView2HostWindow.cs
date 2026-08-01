using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CrystalCast.Video;

internal sealed class WebView2HostWindow : IDisposable
{
    private const string ClassName = "CrystalCastWebView2HostWindow";
    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int SwShow = 5;
    private const int SwShowNoActivate = 4;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint WmClose = 0x0010;
    private const uint GaRoot = 2;
    private const long InteractionActivationGraceMilliseconds = 500;
    private const long InteractionReopenCooldownMilliseconds = 500;
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndBottom = new(1);
    private const uint CaptureWindowStyle = WsPopup | WsClipChildren | WsClipSiblings;
    private const uint InteractionWindowStyle = WsPopup | WsCaption | WsClipChildren | WsClipSiblings;

    private static readonly object RegisterLock = new();
    private static readonly ConcurrentDictionary<IntPtr, WeakReference<WebView2HostWindow>> Windows = new();
    private static readonly WndProcDelegate WindowProcDelegate = WindowProc;
    private static bool registered;

    private IntPtr hwnd;
    private readonly int width;
    private readonly int height;
    private bool interactionVisible;
    private bool interactionWasForeground;
    private long interactionShownAtTick;

    private WebView2HostWindow(IntPtr hwnd, int width, int height)
    {
        this.hwnd = hwnd;
        this.width = width;
        this.height = height;
    }

    public IntPtr Hwnd => hwnd;
    public event Action? InteractionDismissed;

    public (int Width, int Height) GetInteractionClientSize()
    {
        var screenWidth = Math.Max(1, GetSystemMetrics(SmCxScreen));
        var screenHeight = Math.Max(1, GetSystemMetrics(SmCyScreen));
        var maxWidth = Math.Min(width, Math.Max(320, (int)(screenWidth * 0.75)));
        var maxHeight = Math.Min(height, Math.Max(180, (int)(screenHeight * 0.75)));
        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)width, maxHeight / (double)height));
        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    public static WebView2HostWindow Create(int width, int height)
    {
        EnsureRegistered();

        var instance = GetModuleHandle(null);
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var hwnd = CreateWindowEx(
            WsExToolWindow | WsExNoActivate,
            ClassName,
            "CrystalCast WebView2 capture",
            CaptureWindowStyle,
            x,
            y,
            Math.Max(1, width),
            Math.Max(1, height),
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create WebView2 capture host window.");

        var window = new WebView2HostWindow(hwnd, Math.Max(1, width), Math.Max(1, height));
        Windows[hwnd] = new WeakReference<WebView2HostWindow>(window);
        return window;
    }

    public void ShowForCapture()
    {
        if (hwnd == IntPtr.Zero)
            return;

        interactionVisible = false;
        var exStyle = (uint)GetWindowLongPtr(hwnd, GwlExStyle);
        if ((exStyle & WsExNoActivate) == 0)
            SetWindowLongPtr(hwnd, GwlExStyle, (IntPtr)(exStyle | WsExNoActivate));

        SetWindowLongPtr(hwnd, GwlStyle, WindowStyleToIntPtr(CaptureWindowStyle));
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        ShowWindow(hwnd, SwShowNoActivate);
        SetWindowPos(hwnd, HwndBottom, x, y, width, height, SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow | SwpFrameChanged);
        UpdateWindow(hwnd);
    }

    public void ShowForInteraction(int clientWidth, int clientHeight)
    {
        if (hwnd == IntPtr.Zero)
            return;

        interactionVisible = true;
        interactionWasForeground = false;
        interactionShownAtTick = Environment.TickCount64;
        var exStyle = (uint)GetWindowLongPtr(hwnd, GwlExStyle);
        if ((exStyle & WsExNoActivate) != 0)
            SetWindowLongPtr(hwnd, GwlExStyle, (IntPtr)(exStyle & ~WsExNoActivate));

        SetWindowLongPtr(hwnd, GwlStyle, WindowStyleToIntPtr(InteractionWindowStyle));
        clientWidth = Math.Clamp(clientWidth, 1, width);
        clientHeight = Math.Clamp(clientHeight, 1, height);
        var windowRect = GetWindowRectForClient(clientWidth, clientHeight, InteractionWindowStyle, (uint)GetWindowLongPtr(hwnd, GwlExStyle));
        var screenWidth = Math.Max(1, GetSystemMetrics(SmCxScreen));
        var screenHeight = Math.Max(1, GetSystemMetrics(SmCyScreen));
        var windowWidth = Math.Max(1, windowRect.Right - windowRect.Left);
        var windowHeight = Math.Max(1, windowRect.Bottom - windowRect.Top);
        var x = Math.Max(0, (screenWidth - windowWidth) / 2);
        var y = Math.Max(0, (screenHeight - windowHeight) / 2);

        ShowWindow(hwnd, SwShow);
        SetWindowPos(hwnd, HwndTop, x, y, windowWidth, windowHeight, SwpShowWindow | SwpFrameChanged);
        SetForegroundWindow(hwnd);
        SetFocus(hwnd);
        UpdateWindow(hwnd);
        interactionWasForeground = IsInteractionForeground();
    }

    public void ReturnToCapture()
    {
        if (hwnd == IntPtr.Zero)
            return;

        interactionVisible = false;
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var exStyle = (uint)GetWindowLongPtr(hwnd, GwlExStyle);
        if ((exStyle & WsExNoActivate) == 0)
            SetWindowLongPtr(hwnd, GwlExStyle, (IntPtr)(exStyle | WsExNoActivate));

        SetWindowLongPtr(hwnd, GwlStyle, WindowStyleToIntPtr(CaptureWindowStyle));
        SetWindowPos(hwnd, HwndTop, x, y, width, height, SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow | SwpFrameChanged);
        SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        UpdateWindow(hwnd);
    }

    public void PollInteractionFocus()
    {
        if (!interactionVisible || hwnd == IntPtr.Zero)
            return;

        var isForeground = IsInteractionForeground();
        var elapsedMilliseconds = Math.Max(0, Environment.TickCount64 - interactionShownAtTick);
        if (!ShouldDismissInteraction(isForeground, interactionWasForeground, elapsedMilliseconds))
        {
            interactionWasForeground |= isForeground;
            return;
        }

        interactionVisible = false;
        NotifyInteractionDismissed();
    }

    public void Dispose()
    {
        if (hwnd == IntPtr.Zero)
            return;

        var current = hwnd;
        hwnd = IntPtr.Zero;
        Windows.TryRemove(current, out _);
        DestroyWindow(current);
    }

    private static void EnsureRegistered()
    {
        lock (RegisterLock)
        {
            if (registered)
                return;

            var instance = GetModuleHandle(null);
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                Style = 0,
                WindowProc = WindowProcDelegate,
                ClassExtraBytes = 0,
                WindowExtraBytes = 0,
                Instance = instance,
                Icon = IntPtr.Zero,
                Cursor = IntPtr.Zero,
                Background = IntPtr.Zero,
                MenuName = null,
                ClassName = ClassName,
                SmallIcon = IntPtr.Zero,
            };

            var atom = RegisterClassEx(ref windowClass);
            if (atom == 0)
            {
                const int errorClassAlreadyExists = 1410;
                var error = Marshal.GetLastWin32Error();
                if (error != errorClassAlreadyExists)
                    throw new Win32Exception(error, "Failed to register WebView2 capture host window class.");
            }

            registered = true;
        }
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (IsInteractionDismissMessage(message)
            && Windows.TryGetValue(hWnd, out var weakWindow)
            && weakWindow.TryGetTarget(out var window))
        {
            window.NotifyInteractionDismissed();
            if (message == WmClose)
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, message, wParam, lParam);
    }

    internal static bool IsInteractionDismissMessage(uint message)
    {
        return message == WmClose;
    }

    internal static bool ShouldDismissInteraction(bool isForeground, bool wasForeground, long elapsedMilliseconds)
    {
        return !isForeground
            && (wasForeground || elapsedMilliseconds >= InteractionActivationGraceMilliseconds);
    }

    internal static bool CanReopenInteraction(long dismissedAtTick, long currentTick)
    {
        return dismissedAtTick <= 0
            || Math.Max(0, currentTick - dismissedAtTick) >= InteractionReopenCooldownMilliseconds;
    }

    private bool IsInteractionForeground()
    {
        var foregroundWindow = GetForegroundWindow();
        return foregroundWindow != IntPtr.Zero
            && (foregroundWindow == hwnd || GetAncestor(foregroundWindow, GaRoot) == hwnd);
    }

    private void NotifyInteractionDismissed()
    {
        try
        {
            InteractionDismissed?.Invoke();
        }
        catch
        {
            // Never allow managed event handlers to unwind through the native window procedure.
        }
    }

    private static Rect GetWindowRectForClient(int clientWidth, int clientHeight, uint style, uint exStyle)
    {
        var rect = new Rect
        {
            Left = 0,
            Top = 0,
            Right = Math.Max(1, clientWidth),
            Bottom = Math.Max(1, clientHeight),
        };

        AdjustWindowRectEx(ref rect, style, false, exStyle);
        return rect;
    }

    private static IntPtr WindowStyleToIntPtr(uint style)
    {
        return new IntPtr(unchecked((int)style));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool AdjustWindowRectEx(ref Rect rect, uint style, bool menu, uint exStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public WndProcDelegate WindowProc;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ClassName;
        public IntPtr SmallIcon;
    }
}
