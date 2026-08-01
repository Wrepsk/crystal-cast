using System.Runtime.InteropServices;

namespace CrystalCast.Rendering;

internal static class NativeGraphicsError
{
    private static readonly HashSet<int> DeviceLossHResults =
    [
        unchecked((int)0x887A0005), // DXGI_ERROR_DEVICE_REMOVED
        unchecked((int)0x887A0006), // DXGI_ERROR_DEVICE_HUNG
        unchecked((int)0x887A0007), // DXGI_ERROR_DEVICE_RESET
        unchecked((int)0x887A0020), // DXGI_ERROR_DRIVER_INTERNAL_ERROR
    ];

    private const int WaitTimeoutHResult = unchecked((int)0x887A0027);

    public static bool IsDeviceLost(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (DeviceLossHResults.Contains(current.HResult))
                return true;
        }

        return false;
    }

    public static bool IsWaitTimeout(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current.HResult == WaitTimeoutHResult)
                return true;
        }

        return false;
    }

    internal static Exception FromHResult(int hresult)
        => Marshal.GetExceptionForHR(hresult) ?? new COMException("Native graphics failure.", hresult);
}
