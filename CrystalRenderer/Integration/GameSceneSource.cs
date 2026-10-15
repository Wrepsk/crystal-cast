using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace CrystalRenderer.Integration;

internal readonly record struct GameSceneFrame(
    SharpDX.Matrix ViewProjection,
    SharpDX.Vector2 ViewportSize,
    Vector3 CameraPosition,
    nint BackBuffer,
    nint DepthTexture,
    SharpDX.Vector2 DepthUvScale,
    bool ResolutionScaled);

/// <summary>All per-frame game pointers are captured at the pass that consumes them.</summary>
internal static class GameSceneSource
{
    internal static unsafe bool TryCapture(out GameSceneFrame frame)
    {
        frame = default;
        var device = Device.Instance();
        var control = Control.Instance();
        if (device == null || control == null || device->Width == 0 || device->Height == 0
            || device->SwapChain == null || device->SwapChain->BackBuffer == null
            || device->SwapChain->BackBuffer->D3D11Texture2D == null)
            return false;
        var camera = control->CameraManager.GetActiveCamera();
        var renderCamera = camera != null ? camera->SceneCamera.RenderCamera : null;
        if (renderCamera == null) return false;
        var viewProjection = control->ViewProjectionMatrix;
        var values = (float*)&viewProjection;
        for (var i = 0; i < 16; i++)
            if (!float.IsFinite(values[i])) return false;

        var targets = RenderTargetManager.Instance();
        var depth = targets != null ? targets->DepthStencil : null;
        var uvScale = depth == null ? new SharpDX.Vector2(1, 1) : new SharpDX.Vector2(
            depth->AllocatedWidth > 0 ? (float)depth->ActualWidth / depth->AllocatedWidth : 1,
            depth->AllocatedHeight > 0 ? (float)depth->ActualHeight / depth->AllocatedHeight : 1);
        frame = new GameSceneFrame(
            *(SharpDX.Matrix*)&viewProjection,
            new SharpDX.Vector2(device->Width, device->Height),
            renderCamera->Origin,
            (nint)device->SwapChain->BackBuffer->D3D11Texture2D,
            depth == null ? 0 : (nint)depth->D3D11Texture2D,
            uvScale,
            depth != null && (depth->ActualWidth != device->Width || depth->ActualHeight != device->Height));
        return true;
    }
}
