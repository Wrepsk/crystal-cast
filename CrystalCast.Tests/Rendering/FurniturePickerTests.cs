using System.Numerics;
using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class FurniturePickerTests
{
    private static HousingFurnitureItem At(int index, float x, float z, float y = 0.0f) =>
        new(BaseId: 100, Index: index, Name: $"Item {index}", Position: new Vector3(x, y, z), YawRadians: 0.0f);

    private static readonly HousingFurnitureItem[] Room = [At(1, 0, 0), At(2, 3, 0), At(3, 0, 5)];

    [Fact]
    public void PicksTheItemStandingNearestTheHit()
    {
        // A hit on the front of item 2, a meter up.
        var picked = FurniturePicker.NearestToHit(Room, new Vector3(2.8f, 1.0f, 0.4f), Vector3.UnitZ);

        Assert.Equal(2, picked?.Index);
    }

    [Fact]
    public void IgnoresTheFloorAroundAnItem()
    {
        Assert.Null(FurniturePicker.NearestToHit(Room, new Vector3(0.5f, 0.0f, 0.5f), Vector3.UnitY));
    }

    [Fact]
    public void ATableTopCountsEvenThoughItFacesUp()
    {
        var picked = FurniturePicker.NearestToHit(Room, new Vector3(0.2f, 0.8f, 5.1f), Vector3.UnitY);

        Assert.Equal(3, picked?.Index);
    }

    [Fact]
    public void AHitFarFromEveryItemPicksNothing()
    {
        Assert.Null(FurniturePicker.NearestToHit(Room, new Vector3(10, 1, 10), Vector3.UnitZ));
    }

    [Fact]
    public void AHitBelowAnItemsBaseIsNotThatItem()
    {
        // An item on an upper floor, and a hit on the floor below it.
        var upstairs = new[] { At(4, 0, 0, y: 5.0f) };

        Assert.Null(FurniturePicker.NearestToHit(upstairs, new Vector3(0.1f, 1.0f, 0.1f), Vector3.UnitZ));
    }
}
