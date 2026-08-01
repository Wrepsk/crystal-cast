namespace CrystalCast.Video;

internal sealed class BrowserPlaybackIntent(bool autoplay)
{
    private int playRequested = autoplay ? 1 : 0;

    public bool IsPlayRequested => Volatile.Read(ref playRequested) != 0;

    public void RequestPlay() => Volatile.Write(ref playRequested, 1);

    public void RequestPause() => Volatile.Write(ref playRequested, 0);
}
