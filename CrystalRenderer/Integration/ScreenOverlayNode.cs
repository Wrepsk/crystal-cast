// Derived from Pictomancy (sourpuh and contributors), AGPL-3.0-or-later.
// See LICENSE.Pictomancy and THIRD_PARTY_NOTICES.md.
﻿using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using KamiToolKit.Enums;
using KamiToolKit.Overlay.UiOverlay;
using SharpDX.Direct3D11;

namespace CrystalRenderer.Integration;

internal unsafe class ScreenOverlayNode : OverlayNode
{
    public override OverlayLayer OverlayLayer => OverlayLayer.Background;
    private readonly TextureImageNode screenTextureNode;

    public Texture* current;

    public ScreenOverlayNode()
    {
        screenTextureNode = new();
        screenTextureNode.AttachNode(this);
    }

    public void UpdateTexture(Texture2D? texture2D, ShaderResourceView? shaderResourceView)
    {
        if (texture2D == null || shaderResourceView == null)
        {
            IsVisible = false;
            return;
        }

        if (current != null)
        {
            if (current->D3D11Texture2D == (void*)texture2D.NativePointer)
            {
                return;
            }
            current->DecRef();
        }
        var desc = texture2D.Description;
        var width = desc.Width;
        var height = desc.Height;
        var mipLevel = (byte)desc.MipLevels;
        var format = desc.Format;
        var flags = TextureFlags.TextureType2D;
        if (desc.Usage == ResourceUsage.Immutable)
            flags |= TextureFlags.Immutable;
        if (desc.Usage == ResourceUsage.Dynamic)
            flags |= TextureFlags.ReadWrite;
        if ((desc.CpuAccessFlags & CpuAccessFlags.Read) != 0)
            flags |= TextureFlags.CpuRead;
        if ((desc.BindFlags & BindFlags.RenderTarget) != 0)
            flags |= TextureFlags.TextureRenderTarget;
        if ((desc.BindFlags & BindFlags.DepthStencil) != 0)
            flags |= TextureFlags.TextureDepthStencil;

        var texture = Texture.CreateTexture2D(width, height, mipLevel, TextureFormat.B8G8R8A8_UNORM, flags, 0);
        if (texture == null)
        {
            return;
        }

        texture->D3D11Texture2D = (void*)texture2D.NativePointer;
        texture->D3D11ShaderResourceView = (void*)shaderResourceView.NativePointer;
        texture->IncRef();
        screenTextureNode.TextureSize = new(width, height);
        screenTextureNode.Size = new(width, height);
        screenTextureNode.SetTexture(texture);
        Size = new(width, height);

        current = texture;
    }

    protected override void OnUpdate()
    {
    }
}
