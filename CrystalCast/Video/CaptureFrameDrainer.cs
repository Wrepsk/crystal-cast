namespace CrystalCast.Video;

internal static class CaptureFrameDrainer
{
    public static T DrainNewest<T>(
        Func<T> tryGetNext,
        Func<T, bool> isEmpty,
        Action<T> close,
        out int discarded)
    {
        ArgumentNullException.ThrowIfNull(tryGetNext);
        ArgumentNullException.ThrowIfNull(isEmpty);
        ArgumentNullException.ThrowIfNull(close);

        discarded = 0;
        var latest = tryGetNext();
        if (isEmpty(latest))
            return latest;

        while (true)
        {
            var next = tryGetNext();
            if (isEmpty(next))
                return latest;

            close(latest);
            discarded++;
            latest = next;
        }
    }
}
