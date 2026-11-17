using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace CrystalCast.Rendering;

/// <summary>
/// Keeps the game camera's zoom where it was while the mouse wheel is used for something else (resizing a
/// screen while picking where it goes). The game still sees the wheel and zooms; this puts the distance back.
/// </summary>
internal sealed class CameraZoomHold
{
    /// <summary>
    /// How long after the last wheel step the zoom stays held. The game can handle a wheel step a frame before or
    /// after the plugin sees it, so the hold outlasts the scrolling a little.
    /// </summary>
    internal const long HoldMs = 300;

    private float? heldDistance;
    private long holdUntilTick;

    /// <summary>
    /// Call every frame while picking. With <paramref name="wheelUsed"/>, the zoom is held at the distance from
    /// before the scrolling; otherwise that distance is updated once the hold has run out.
    /// </summary>
    public unsafe void Update(bool wheelUsed, long nowTick)
    {
        var camera = Control.Instance()->CameraManager.GetActiveCamera();
        if (camera == null)
            return;

        if (wheelUsed)
            holdUntilTick = nowTick + HoldMs;

        if (heldDistance is { } distance && ShouldHold(nowTick, holdUntilTick))
        {
            camera->Distance = distance;
            camera->InterpDistance = distance;
            return;
        }

        heldDistance = camera->Distance;
    }

    public void Reset()
    {
        heldDistance = null;
        holdUntilTick = 0;
    }

    internal static bool ShouldHold(long nowTick, long holdUntilTick) => nowTick < holdUntilTick;
}
