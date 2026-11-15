using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CrystalCast.Rendering;

/// <summary>Reads the housing items placed where the player is (indoors, or the yards of the ward outdoors).</summary>
internal static class HousingFurnitureReader
{
    /// <summary>Names cost a string each, so per-frame lookups leave them out.</summary>
    public static unsafe void Read(List<HousingFurnitureItem> output, bool withNames)
    {
        output.Clear();
        var housing = HousingManager.Instance();
        if (housing == null)
            return;

        HousingFurnitureManager* furniture = null;
        if (housing->IsInside() && housing->IndoorTerritory != null)
            furniture = &housing->IndoorTerritory->FurnitureManager;
        else if (housing->IsOutside() && housing->OutdoorTerritory != null)
            furniture = &housing->OutdoorTerritory->FurnitureManager;

        if (furniture == null)
            return;

        foreach (var pointer in furniture->ObjectManager.ObjectArray.Objects)
        {
            var gameObject = pointer.Value;
            if (gameObject == null)
                continue;

            var item = (HousingObject*)gameObject;
            var position = (Vector3)item->Position;
            if (item->BaseId == 0 || item->HousingFurnitureIndex < 0 || !PlacementMath.IsFinite(position) || !float.IsFinite(item->Rotation))
                continue;

            output.Add(withNames
                ? new HousingFurnitureItem(item->BaseId, item->HousingFurnitureIndex, item->NameString, position, item->Rotation, ReadCenter(item, position))
                : new HousingFurnitureItem(item->BaseId, item->HousingFurnitureIndex, string.Empty, position, item->Rotation));
        }
    }

    /// <summary>The middle of the item's model, or a meter above its base when the game gives nothing sensible.</summary>
    private static unsafe Vector3 ReadCenter(HousingObject* item, Vector3 position)
    {
        var native = default(FFXIVClientStructs.FFXIV.Common.Math.Vector3);
        item->GetCenterPosition(&native);
        var center = (Vector3)native;
        return PlacementMath.IsFinite(center) && Vector3.DistanceSquared(center, position) is > 0.0001f and < 400.0f
            ? center
            : position + Vector3.UnitY;
    }
}
