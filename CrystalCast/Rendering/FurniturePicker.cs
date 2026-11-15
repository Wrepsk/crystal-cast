using System.Numerics;

namespace CrystalCast.Rendering;

/// <summary>
/// Picks the housing item under a collision hit when the game's model test finds nothing: the item standing
/// closest to the hit, sideways, as long as the hit is within its reach and isn't the floor beside it.
/// </summary>
internal static class FurniturePicker
{
    /// <summary>How far sideways from an item's base a hit may be and still count as that item.</summary>
    public const float MaxSidewaysMeters = 1.5f;

    /// <summary>How far above its base a hit may be and still count as that item.</summary>
    public const float MaxHeightMeters = 6.0f;

    public static HousingFurnitureItem? NearestToHit(IReadOnlyList<HousingFurnitureItem> items, Vector3 hit, Vector3 hitNormal)
    {
        HousingFurnitureItem? best = null;
        var bestDistance = MaxSidewaysMeters * MaxSidewaysMeters;
        foreach (var item in items)
        {
            var height = hit.Y - item.Position.Y;
            if (height < -0.1f || height > MaxHeightMeters)
                continue;

            // An upward face at the item's base height is the floor around it, not the item.
            if (hitNormal.Y > 0.7f && height < 0.05f)
                continue;

            var sideways = new Vector2(hit.X - item.Position.X, hit.Z - item.Position.Z).LengthSquared();
            if (sideways <= bestDistance)
            {
                best = item;
                bestDistance = sideways;
            }
        }

        return best;
    }
}
