namespace CrystalCast.Video;

/// <summary>Order in which capture tries the rotating shared textures.</summary>
internal static class CaptureSlotPolicy
{
    /// <summary>
    /// Shared textures rotated between window capture and the game. With one texture, capture
    /// had to wait for the game to read each frame and dropped frames that arrived meanwhile.
    /// </summary>
    public const int SharedTextureCount = 3;

    /// <summary>
    /// Returns the slot to try at <paramref name="offset"/> (1 to <paramref name="count"/>):
    /// the slots after the newest published one first, the newest published slot last, so a
    /// frame the renderer may be about to read is only replaced when nothing else is free.
    /// </summary>
    public static int CandidateSlot(int lastPublished, int offset, int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (offset < 1 || offset > count)
            throw new ArgumentOutOfRangeException(nameof(offset));

        return lastPublished < 0 || lastPublished >= count
            ? offset - 1
            : (lastPublished + offset) % count;
    }
}
