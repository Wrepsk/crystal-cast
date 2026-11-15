using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics;

namespace CrystalCast.Rendering;

/// <summary>Reads the housing items placed where the player is (indoors, or the yards of the ward outdoors).</summary>
internal static class HousingFurnitureReader
{
    /// <summary>Items farther than this from the mouse ray aren't tested against it.</summary>
    private const float PickPrefilterMeters = 8.0f;

    /// <summary>Names cost a string each, so per-frame lookups leave them out.</summary>
    public static unsafe void Read(List<HousingFurnitureItem> output, bool withNames)
    {
        output.Clear();
        foreach (var item in PlacedItems())
        {
            var position = (Vector3)item->Position;
            output.Add(withNames
                ? new HousingFurnitureItem(item->BaseId, item->HousingFurnitureIndex, item->NameString, position, item->Rotation, ReadCenter(item, position))
                : new HousingFurnitureItem(item->BaseId, item->HousingFurnitureIndex, string.Empty, position, item->Rotation));
        }
    }

    /// <summary>
    /// The item whose model the ray hits first, using the game's own model test. False when no model reports a hit
    /// (some items may not support it); the caller can then fall back to the collision hit.
    /// </summary>
    public static unsafe bool TryPickByModel(Vector3 origin, Vector3 direction, out HousingFurnitureItem picked)
    {
        picked = default;
        var bestDistance = float.MaxValue;
        var ray = new Ray(origin, direction);
        foreach (var item in PlacedItems())
        {
            var position = (Vector3)item->Position;
            var along = Vector3.Dot(position - origin, direction);
            if (along < -PickPrefilterMeters
                || Vector3.DistanceSquared(position, origin + (direction * along)) > PickPrefilterMeters * PickPrefilterMeters)
            {
                continue;
            }

            var hitNative = default(FFXIVClientStructs.FFXIV.Common.Math.Vector3);
            var modelChecked = false;
            if (!((GameObject*)item)->IntersectsRay(&ray, &hitNative, &modelChecked))
                continue;

            var hit = (Vector3)hitNative;
            var distance = PlacementMath.IsFinite(hit) ? Vector3.Dot(hit - origin, direction) : along;
            if (distance < 0.0f || distance >= bestDistance)
                continue;

            bestDistance = distance;
            picked = new HousingFurnitureItem(item->BaseId, item->HousingFurnitureIndex, item->NameString, position, item->Rotation, ReadCenter(item, position));
        }

        return bestDistance < float.MaxValue;
    }

    private static unsafe List<nint> PlacedItemAddresses()
    {
        var addresses = new List<nint>();
        var housing = HousingManager.Instance();
        if (housing == null)
            return addresses;

        HousingFurnitureManager* furniture = null;
        if (housing->IsInside() && housing->IndoorTerritory != null)
            furniture = &housing->IndoorTerritory->FurnitureManager;
        else if (housing->IsOutside() && housing->OutdoorTerritory != null)
            furniture = &housing->OutdoorTerritory->FurnitureManager;

        if (furniture == null)
            return addresses;

        foreach (var pointer in furniture->ObjectManager.ObjectArray.Objects)
        {
            var item = (HousingObject*)pointer.Value;
            if (item == null)
                continue;

            if (item->BaseId == 0 || item->HousingFurnitureIndex < 0 || !PlacementMath.IsFinite((Vector3)item->Position) || !float.IsFinite(item->Rotation))
                continue;

            addresses.Add((nint)item);
        }

        return addresses;
    }

    private static unsafe HousingObjectEnumerable PlacedItems() => new(PlacedItemAddresses());

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

    /// <summary>Iterates housing objects as pointers (iterators can't yield pointers).</summary>
    private readonly unsafe struct HousingObjectEnumerable(List<nint> addresses)
    {
        public Enumerator GetEnumerator() => new(addresses);

        public struct Enumerator(List<nint> addresses)
        {
            private int index = -1;

            public HousingObject* Current => (HousingObject*)addresses[index];

            public bool MoveNext() => ++index < addresses.Count;
        }
    }
}
