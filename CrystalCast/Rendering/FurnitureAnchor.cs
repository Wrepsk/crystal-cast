using System.Numerics;
using CrystalCast.Rooms;

namespace CrystalCast;

/// <summary>
/// A placed housing item a screen is attached to. The item is found again by its slot in the house's furniture
/// list and its kind; if the slot now holds something else, the nearest item of the same kind to where it was is
/// used, so moving it or putting it away and placing it again keeps the screen with it.
/// </summary>
[Serializable]
public sealed record FurnitureAnchor
{
    /// <summary>The house (indoors) or ward half (outdoors) the item is in.</summary>
    public RoomLocation House { get; set; } = new();

    /// <summary>The kind of item.</summary>
    public uint BaseId { get; set; }

    /// <summary>The item's slot in the house's furniture list.</summary>
    public int Index { get; set; } = -1;

    /// <summary>The item's name when it was attached, for the UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Where the item was when attached, to tell apart several items of the same kind.</summary>
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }

    internal bool IsValid => House.IsKnown && House.InHousing && BaseId != 0 && Index >= 0;

    internal Vector3 Position => new(PositionX, PositionY, PositionZ);

    internal FurnitureAnchor Copy() => this with { };

    internal static FurnitureAnchor For(RoomLocation house, HousingFurnitureItem item) => new()
    {
        House = house,
        BaseId = item.BaseId,
        Index = item.Index,
        Name = item.Name,
        PositionX = item.Position.X,
        PositionY = item.Position.Y,
        PositionZ = item.Position.Z,
    };
}

/// <summary>
/// A placed housing item as the game has it now. <see cref="Position"/> is its base; <see cref="Center"/> the
/// middle of its model, read only with names (default when not read).
/// </summary>
internal readonly record struct HousingFurnitureItem(uint BaseId, int Index, string Name, Vector3 Position, float YawRadians, Vector3 Center = default);

internal static class FurnitureAnchorMatcher
{
    /// <summary>The item the anchor points at here, or null when it isn't in this house or no longer placed.</summary>
    public static HousingFurnitureItem? Find(FurnitureAnchor anchor, RoomLocation here, IReadOnlyList<HousingFurnitureItem> items)
    {
        if (!anchor.IsValid || !anchor.House.SamePlaceAs(here))
            return null;

        HousingFurnitureItem? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var item in items)
        {
            if (item.BaseId != anchor.BaseId)
                continue;

            if (item.Index == anchor.Index)
                return item;

            var distance = Vector3.DistanceSquared(item.Position, anchor.Position);
            if (distance < nearestDistance)
            {
                nearest = item;
                nearestDistance = distance;
            }
        }

        return nearest;
    }
}
