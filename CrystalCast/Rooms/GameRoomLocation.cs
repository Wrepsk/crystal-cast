using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CrystalCast.Rooms;

/// <summary>Reads where the local player is: world, zone, and for housing the ward, plot, room and division.</summary>
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
        };

        var housing = HousingManager.Instance();
        if (housing == null)
            return location;

        return location with
        {
            Ward = housing->GetCurrentWard(),
            Plot = housing->GetCurrentPlot(),
            Room = housing->GetCurrentRoom(),
            Division = housing->GetCurrentDivision(),
            Indoors = housing->IsInside(),
        };
    }
}
