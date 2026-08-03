using System.Runtime.InteropServices;
using SharpDX.DXGI;

namespace CrystalCast.Rendering;

internal static class KeyedMutexSynchronization
{
    internal const int WaitTimeoutResult = 0x00000102;

    public static unsafe bool TryAcquire(KeyedMutex mutex, long key, uint timeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(mutex);
        var nativePointer = mutex.NativePointer;
        if (nativePointer == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(KeyedMutex));

        var vtable = *(IntPtr**)nativePointer;
        var acquireSync = (delegate* unmanaged[Stdcall]<IntPtr, long, uint, int>)vtable[8];
        return InterpretAcquireResult(acquireSync(nativePointer, key, timeoutMilliseconds));
    }

    internal static bool InterpretAcquireResult(int result)
    {
        if (result == 0)
            return true;
        if (result == WaitTimeoutResult)
            return false;
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);

        throw new InvalidOperationException($"Unexpected keyed-mutex acquire result 0x{result:X8}.");
    }
}
