using System.Runtime.InteropServices;

namespace CrystalCast.Windows.Style;

/// <summary>
/// Keeps one COM object (a texture view drawn by ImGui) alive until the next UI frame. ImGui draw lists store raw
/// texture pointers and render after the plugin's draw callback, while the renderer may release and replace its
/// view in between; holding a reference across one frame makes that safe.
/// </summary>
internal sealed class ComReferenceHold : IDisposable
{
    private nint held;

    public void Hold(nint pointer)
    {
        if (pointer == held)
            return;

        if (pointer != 0)
            Marshal.AddRef(pointer);
        if (held != 0)
            Marshal.Release(held);
        held = pointer;
    }

    public void Dispose()
    {
        Hold(0);
    }
}
