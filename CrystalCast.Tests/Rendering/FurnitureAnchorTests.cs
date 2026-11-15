using System.Numerics;
using CrystalCast.Ipc;
using CrystalCast.Rooms;

namespace CrystalCast.Tests;

public sealed class FurnitureAnchorTests
{
    private static readonly RoomLocation House = new()
    {
        WorldId = 40,
        TerritoryId = 608,
        Ward = 4,
        Plot = 12,
        Room = 0,
        Indoors = true,
    };

    private static HousingFurnitureItem Item(uint baseId, int index, float x, float yaw = 0.0f) =>
        new(baseId, index, $"Item {index}", new Vector3(x, 0, 0), yaw);

    private static FurnitureAnchor AnchorTo(HousingFurnitureItem item) => FurnitureAnchor.For(House, item);

    [Fact]
    public void FindsTheSameSlot()
    {
        var tv = Item(baseId: 900, index: 7, x: 5);
        var anchor = AnchorTo(tv);
        var moved = tv with { Position = new Vector3(20, 1, 3), YawRadians = 1.5f };

        var found = FurnitureAnchorMatcher.Find(anchor, House, [Item(900, 2, 1), moved]);

        Assert.Equal(moved, found);
    }

    [Fact]
    public void FallsBackToTheNearestItemOfTheSameKind()
    {
        // Put away and placed again: the slot changed, so the closest item of that kind is used.
        var anchor = AnchorTo(Item(baseId: 900, index: 7, x: 5));
        var near = Item(900, 11, 6);

        var found = FurnitureAnchorMatcher.Find(anchor, House, [Item(900, 3, 30), near, Item(901, 7, 5)]);

        Assert.Equal(near, found);
    }

    [Fact]
    public void AnotherKindInTheSameSlotIsNotAMatch()
    {
        var anchor = AnchorTo(Item(baseId: 900, index: 7, x: 5));

        Assert.Null(FurnitureAnchorMatcher.Find(anchor, House, [Item(901, 7, 5)]));
    }

    [Fact]
    public void OnlyMatchesInTheSameHouse()
    {
        var tv = Item(baseId: 900, index: 7, x: 5);
        var anchor = AnchorTo(tv);

        Assert.Null(FurnitureAnchorMatcher.Find(anchor, House with { Plot = 13 }, [tv]));
        Assert.Null(FurnitureAnchorMatcher.Find(anchor, House with { WorldId = 41 }, [tv]));
        Assert.Null(FurnitureAnchorMatcher.Find(anchor, new RoomLocation(), [tv]));
    }

    [Fact]
    public void AFurnitureModeWithoutAnAnchorFallsBackToFixed()
    {
        var placement = new ScreenPlacementSettings { Mode = ScreenPlacementMode.Furniture };

        placement.Normalize();

        Assert.Equal(ScreenPlacementMode.World, placement.Mode);
    }

    [Fact]
    public void LeavingFurnitureModeForgetsTheAnchor()
    {
        var placement = new ScreenPlacementSettings
        {
            Mode = ScreenPlacementMode.Furniture,
            Furniture = AnchorTo(Item(900, 7, 5)),
        };
        Assert.False(placement.Normalize());

        placement.Mode = ScreenPlacementMode.FollowPlayer;
        placement.Normalize();

        Assert.Null(placement.Furniture);
    }

    [Fact]
    public void CopiesKeepTheirOwnAnchor()
    {
        var placement = new ScreenPlacementSettings
        {
            Mode = ScreenPlacementMode.Furniture,
            Furniture = AnchorTo(Item(900, 7, 5)),
        };

        var copy = placement.Clone();
        copy.Furniture!.Name = "Renamed";

        Assert.Equal(ScreenPlacementMode.Furniture, copy.Mode);
        Assert.Equal("Item 7", placement.Furniture!.Name);
    }

    [Fact]
    public void IpcCannotSwitchAScreenToFurniture()
    {
        var screen = new BrowserScreenProfile();
        var request = new ScreenIpcMutationRequest
        {
            Placement = new ScreenPlacementPatchDto { Mode = ScreenPlacementMode.Furniture, PositionX = 1.0f },
        };

        Assert.True(ScreenPatchApplier.TryApplyScreenMutation(screen, request, out var updated, out var error), error);
        Assert.Equal(ScreenPlacementMode.World, updated.Placement.Mode);
        Assert.Equal(1.0f, updated.Placement.PositionX);
    }

    [Theory]
    [InlineData(0L, 5_000L, true)] // never read
    [InlineData(4_990L, 5_000L, false)] // read this frame
    [InlineData(4_000L, 5_000L, true)] // read a while ago
    [InlineData(6_000L, 5_000L, true)] // the clock went backwards
    public void TheFurnitureCacheIsReadWhenNeeded(long readAtMs, long nowMs, bool expired)
    {
        Assert.Equal(expired, Rendering.ScreenPlacementResolver.FurnitureCacheExpired(readAtMs, nowMs));
    }

    [Fact]
    public void TheAnchorSurvivesSavingAndLoading()
    {
        // Dalamud saves plugin configuration with Newtonsoft.Json and type names.
        var settings = new Newtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Objects };
        var placement = new ScreenPlacementSettings
        {
            Mode = ScreenPlacementMode.Furniture,
            PositionX = 0.5f,
            Furniture = AnchorTo(Item(900, 7, 5)),
        };

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(placement, settings);
        var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<ScreenPlacementSettings>(json, settings)!;
        loaded.Normalize();

        Assert.Equal(ScreenPlacementMode.Furniture, loaded.Mode);
        Assert.Equal(placement.Furniture, loaded.Furniture);
    }
}
