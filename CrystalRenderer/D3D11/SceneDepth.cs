// Derived from Pictomancy (sourpuh and contributors), AGPL-3.0-or-later.
// See LICENSE.Pictomancy and THIRD_PARTY_NOTICES.md.
using SharpDX.Direct3D;
using SharpDX.Direct3D11;

namespace CrystalRenderer.D3D11;

// Wrap the game's typeless depth stencil texture in a typed SRV
internal class SceneDepth : IDisposable
{
    public RenderContext RenderContext { get; }

    public SceneDepth(RenderContext context) => RenderContext = context;

    private IntPtr _cachedTexPtr;
    private ShaderResourceView? _srv;
    private string? _unavailableReason;

    internal ShaderResourceView? SRV => _srv;
    public SharpDX.Vector2 UvScale { get; private set; } = new(1, 1);

    public void Dispose()
    {
        DropSrv();
    }

    private void DropSrv()
    {
        _srv?.Dispose();
        _srv = null;
        _cachedTexPtr = IntPtr.Zero;
        UvScale = new(1, 1);
    }

    private void SetUnavailable(string reason)
    {
        DropSrv();
        if (_unavailableReason != reason)
            RenderContext.Log?.Warning($"[CrystalRenderer] SceneDepth.Update: {reason}; skipping world screens.");
        _unavailableReason = reason;
    }

    internal void Update(nint texPtr, SharpDX.Vector2 uvScale)
    {
        if (texPtr == nint.Zero)
        {
            SetUnavailable("scene depth source unavailable");
            return;
        }

        if (texPtr != _cachedTexPtr)
        {
            using var tex = new Texture2D(texPtr);
            try
            {
                var description = tex.Description;
                if (!ScreenDepthPolicy.TryGetShaderFormat(description.Format, description.SampleDescription.Count, out var shaderFormat))
                {
                    SetUnavailable($"unsupported depth format {description.Format} / {description.SampleDescription.Count} samples");
                    return;
                }
                var srvDesc = new ShaderResourceViewDescription
                {
                    Format = shaderFormat,
                    Dimension = ShaderResourceViewDimension.Texture2D,
                };
                srvDesc.Texture2D.MostDetailedMip = 0;
                srvDesc.Texture2D.MipLevels = 1;
                var nextSrv = new ShaderResourceView(RenderContext.Device, tex, srvDesc);
                DropSrv();
                _srv = nextSrv;
                _cachedTexPtr = texPtr;
                _unavailableReason = null;
            }
            catch
            {
                DropSrv();
                throw;
            }
            finally
            {
                // Zero the NativePointer before disposal so we don't Release a ref we don't own.
                tex.NativePointer = IntPtr.Zero;
            }
        }

        UvScale = uvScale;
    }
}
