// C#/WinRT projects complete runtime classes, including methods CrystalCast intentionally
// keeps behind its existing raw desktop/D3D interop. These internal placeholders satisfy
// those unused signatures while the generated projection owns FrameArrived marshalling.

namespace Windows.Graphics.DirectX.Direct3D11
{
    internal interface IDirect3DDevice
    {
    }
}

namespace Windows.Graphics.Capture
{
    internal sealed class Direct3D11CaptureFrame
    {
    }

    internal sealed class GraphicsCaptureItem
    {
    }

    internal sealed class GraphicsCaptureSession
    {
    }
}

namespace Windows.System
{
    internal sealed class DispatcherQueue
    {
    }
}

namespace ABI.Windows.Graphics.DirectX.Direct3D11
{
    internal static class IDirect3DDeviceMethods
    {
        private static readonly Guid InterfaceId = new("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670");

        internal static ref readonly Guid IID => ref InterfaceId;
    }
}

namespace ABI.Windows.Graphics.Capture
{
    internal static class Direct3D11CaptureFrame
    {
        internal static global::Windows.Graphics.Capture.Direct3D11CaptureFrame FromAbi(IntPtr value) =>
            throw ProjectionMethodNotUsed();

        internal static void DisposeAbi(IntPtr value)
        {
        }

        private static NotSupportedException ProjectionMethodNotUsed() =>
            new("CrystalCast uses native WGC interop for frame access.");
    }

    internal static class GraphicsCaptureItem
    {
        internal static global::WinRT.ObjectReferenceValue CreateMarshaler2(
            global::Windows.Graphics.Capture.GraphicsCaptureItem value) =>
            throw ProjectionMethodNotUsed();

        private static NotSupportedException ProjectionMethodNotUsed() =>
            new("CrystalCast uses native WGC interop for capture items.");
    }

    internal static class GraphicsCaptureSession
    {
        internal static global::Windows.Graphics.Capture.GraphicsCaptureSession FromAbi(IntPtr value) =>
            throw ProjectionMethodNotUsed();

        internal static void DisposeAbi(IntPtr value)
        {
        }

        private static NotSupportedException ProjectionMethodNotUsed() =>
            new("CrystalCast uses native WGC interop for capture sessions.");
    }
}

namespace ABI.Windows.System
{
    internal static class DispatcherQueue
    {
        internal static global::Windows.System.DispatcherQueue FromAbi(IntPtr value) =>
            throw ProjectionMethodNotUsed();

        internal static void DisposeAbi(IntPtr value)
        {
        }

        private static NotSupportedException ProjectionMethodNotUsed() =>
            new("CrystalCast does not use the WGC dispatcher queue projection.");
    }
}
