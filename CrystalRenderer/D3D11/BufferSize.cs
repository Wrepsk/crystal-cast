using System.Runtime.CompilerServices;

namespace CrystalRenderer.D3D11;

internal static class BufferSize
{
    internal static int AlignTo16<T>() where T : unmanaged => (Unsafe.SizeOf<T>() + 15) & ~15;
}
