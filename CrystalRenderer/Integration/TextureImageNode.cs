// Derived from Pictomancy (sourpuh and contributors), AGPL-3.0-or-later.
// See LICENSE.Pictomancy and THIRD_PARTY_NOTICES.md.
﻿using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Premade.Node.Simple;

namespace CrystalRenderer.Integration;

/// <summary>
/// WARNING: This is a non-owning texture image node.
/// This node is meant to reference a texture that is owned elsewhere.
/// </summary>
internal unsafe class TextureImageNode : SimpleImageNode
{
    public void SetTexture(Texture* texture)
    {
        var asset = PartsList[0]->UldAsset;
        asset->AtkTexture.KernelTexture = texture;
        asset->AtkTexture.TextureType = TextureType.KernelTexture;
    }

    protected override void Dispose(bool disposing, bool isNativeDestructor)
    {
        if (disposing)
        {
            base.Dispose(disposing, isNativeDestructor);
        }
    }
}
