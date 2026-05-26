using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CrystalCast.Video;

internal sealed class WebView2HostWindow : IDisposable
{
    private const string ClassName = "CrystalCastWebView2HostWindow";
    private const uint WsPopup = 0x80000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const int SwShowNoActivate = 4;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndBottom = new(1);

    private static readonly object RegisterLock = new();
    private static readonly WndProcDelegate WindowProcDelegate = WindowProc;
    private static bool registered;

    private IntPtr hwnd;

    private WebView2HostWindow(IntPtr hwnd)
    {
        this.hwnd = hwnd;
    }

    public IntPtr Hwnd => hwnd;

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
            WsPopup | WsClipChildren | WsClipSiblings,
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

        return new WebView2HostWindow(hwnd);
    }

    public void Show()
    {
        if (hwnd == IntPtr.Zero)
            return;

        ShowWindow(hwnd, SwShowNoActivate);
        SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow);
        UpdateWindow(hwnd);
    }

    public void Dispose()
    {
        if (hwnd == IntPtr.Zero)
            return;

        var current = hwnd;
        hwnd = IntPtr.Zero;
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
        return DefWindowProc(hWnd, message, wParam, lParam);
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
    private static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

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
