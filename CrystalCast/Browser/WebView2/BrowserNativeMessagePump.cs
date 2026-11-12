using System.Runtime.InteropServices;

namespace CrystalCast.Video;

internal static class BrowserNativeMessagePump
{
    private const uint PmRemove = 0x0001;
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x0004;
    private const uint WmQuit = 0x0012;

    public static void PumpMessages(ref bool quit)
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            if (message.Message == WmQuit)
            {
                quit = true;
                return;
            }

            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    public static void WaitForWork(WaitHandle handle, uint timeoutMilliseconds)
    {
        var handles = new[] { handle.SafeWaitHandle.DangerousGetHandle() };
        _ = MsgWaitForMultipleObjectsEx(1, handles, timeoutMilliseconds, QsAllInput, MwmoInputAvailable);
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint filterMinimum, uint filterMaximum, uint removeMessage);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public System.Drawing.Point Point;
    }
}
