namespace CrystalCast.Rendering;

internal sealed class PlacementPredictionContext
{
    private bool initialized;
    private ushort territoryId;
    private nint playerAddress;

    public bool Update(ushort currentTerritoryId, nint currentPlayerAddress)
    {
        if (initialized
            && territoryId == currentTerritoryId
            && playerAddress == currentPlayerAddress)
        {
            return false;
        }

        initialized = true;
        territoryId = currentTerritoryId;
        playerAddress = currentPlayerAddress;
        return true;
    }
}
