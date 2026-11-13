using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CrystalCast.Rooms;

/// <summary>
/// Reads where the local player is: world, zone and instance, and in housing the ward, plus the house or
/// apartment when indoors.
/// </summary>
internal static class GameRoomLocation
{
    /// <summary>Call on the game's main thread.</summary>
    public static unsafe RoomLocation Read(IClientState clientState, IObjectTable objectTable)
    {
        var player = objectTable.LocalPlayer;
        if (player == null || clientState.TerritoryType == 0)
            return new RoomLocation();

        var location = new RoomLocation
        {
            WorldId = player.CurrentWorld.RowId,
            TerritoryId = (ushort)clientState.TerritoryType,
            Instance = clientState.Instance,
        };

        var housing = HousingManager.Instance();
        if (housing == null)
            return location;

        var ward = housing->GetCurrentWard();
        if (ward < 0)
            return location;

        var indoors = housing->IsInside();
        if (!indoors)
        {
            // Outdoors the plot is just where someone stands; leaving it out also keeps the host from sending
            // an update every time they cross onto another plot.
            return location with { Ward = ward, Division = housing->GetCurrentDivision() };
        }

        return location with
        {
            Ward = ward,
            Plot = housing->GetCurrentPlot(),
            Room = housing->GetCurrentRoom(),
            Division = housing->GetCurrentDivision(),
            Indoors = true,
        };
    }
}
