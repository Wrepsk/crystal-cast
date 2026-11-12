using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using CrystalCast.Ipc;
using CrystalCast.Sources;
using CrystalCast.Video;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace CrystalCast.Playlist;

/// <summary>
/// Fills in playlist entries' titles, channels, lengths and thumbnails. YouTube, Vimeo and Dailymotion are
/// asked through their oEmbed endpoints when an entry is added; other entries learn their title and length
/// once they play. Web pages are never fetched outside the browser, so a page another plugin added isn't
/// contacted before the user approves it. Lookups run in the background; results are applied on the game's
/// main thread in <see cref="Update"/>.
/// </summary>
internal sealed class PlaylistMetadataService : IDisposable
{
    private const int MaxConcurrentRequests = 2;
    private const int MaxThumbnailBytes = 2 * 1024 * 1024;
    private const long UpdateIntervalMs = 500;
    private const long ThumbnailIdleMs = 120_000;

    /// <summary>Twitch updates its live previews about every five minutes.</summary>
    private const long LivePreviewRefreshMs = 300_000;

    private readonly ITextureProvider? textures;
    private readonly IPluginLog log;
    private readonly Func<BrowserScreenProfile, MediaPlaybackTelemetry?> getTelemetry;
    private readonly HttpClient http;
    private readonly SemaphoreSlim requestGate = new(MaxConcurrentRequests);
    private readonly CancellationTokenSource disposal = new();
    private readonly ConcurrentQueue<LookupResult> results = new();
    private readonly HashSet<string> requested = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string EntryId, long SinceUnixMs)> playing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ThumbnailSlot> thumbnails = new(StringComparer.Ordinal);

    /// <summary>
    /// What each screen is showing, described like a playlist entry so the screen list can show its
    /// thumbnail. Kept in memory only, keyed by provider and link.
    /// </summary>
    private readonly Dictionary<string, PlaylistEntry> sources = new(StringComparer.Ordinal);

    /// <summary>The link each screen's player is on, by screen ID; refreshed in <see cref="Update"/>.</summary>
    private readonly Dictionary<string, (BrowserSourceProviderKind Provider, string Url)> shown = new(StringComparer.Ordinal);

    /// <summary>Links seen this session keep their details, up to this many, so switching back needs no lookup.</summary>
    private const int MaxSources = 256;
    private long? lastUpdateTick;
    private bool disposed;

    public PlaylistMetadataService(
        ITextureProvider? textures,
        IPluginLog log,
        Func<BrowserScreenProfile, MediaPlaybackTelemetry?> getTelemetry,
        HttpMessageHandler? handler = null)
    {
        this.textures = textures;
        this.log = log;
        this.getTelemetry = getTelemetry;
        http = new HttpClient(handler ?? new SocketsHttpHandler { MaxAutomaticRedirections = 3 })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CrystalCast", typeof(PlaylistMetadataService).Assembly.GetName().Version?.ToString(3) ?? "1"));
    }

    /// <summary>Applies finished lookups and starts new ones. Returns the screens whose entries changed.</summary>
    public IReadOnlyCollection<string> Update(Configuration configuration, bool force = false)
    {
        var now = Environment.TickCount64;
        if (disposed || (!force && lastUpdateTick is { } last && now - last < UpdateIntervalMs))
            return [];

        lastUpdateTick = now;
        var changed = new HashSet<string>(StringComparer.Ordinal);
        SyncSources(configuration);
        ApplyResults(configuration, changed);
        StartLookups(configuration, changed);
        LearnFromPlayback(configuration, changed);
        TrimThumbnails(now);
        return changed;
    }

    /// <summary>
    /// The entry's thumbnail once it has loaded; starts loading it the first time it is asked for. A Twitch
    /// live preview is fetched again every few minutes, showing the old picture until the new one is in.
    /// </summary>
    public IDalamudTextureWrap? GetThumbnail(PlaylistEntry entry)
    {
        var slot = GetSlot(entry);
        if (slot == null)
            return null;

        if (slot.Task.IsCompletedSuccessfully)
        {
            if (slot.Previous is { } previous)
            {
                slot.Previous = null;
                DisposeWhenDone(previous);
            }

            return slot.Task.Result.Image;
        }

        return slot.Previous is { IsCompletedSuccessfully: true } shown ? shown.Result.Image : null;
    }

    /// <summary>For a Twitch channel: true when live, false when offline, null until its preview has been checked.</summary>
    public bool? IsLive(PlaylistEntry entry)
    {
        if (!PlaylistOEmbed.IsLivePreview(entry) || GetSlot(entry) is not { } slot)
            return null;

        var task = slot.Task.IsCompletedSuccessfully ? slot.Task : slot.Previous;
        return task is { IsCompletedSuccessfully: true } done && done.Result.Checked ? !done.Result.Offline : null;
    }

    private ThumbnailSlot? GetSlot(PlaylistEntry entry)
    {
        if (disposed || textures == null || !PlaylistOEmbed.IsAllowedThumbnail(entry.ProviderKind, entry.ThumbnailUrl))
            return null;

        var now = Environment.TickCount64;
        if (!thumbnails.TryGetValue(entry.ThumbnailUrl, out var slot))
        {
            slot = new ThumbnailSlot(LoadThumbnailAsync(entry.ThumbnailUrl, disposal.Token), now);
            thumbnails[entry.ThumbnailUrl] = slot;
        }
        else if (PlaylistOEmbed.IsLivePreview(entry)
            && slot.Task.IsCompleted
            && slot.Previous == null
            && now - slot.CreatedTick > LivePreviewRefreshMs)
        {
            var refreshed = new ThumbnailSlot(LoadThumbnailAsync(entry.ThumbnailUrl, disposal.Token), now) { Previous = slot.Task };
            thumbnails[entry.ThumbnailUrl] = refreshed;
            slot = refreshed;
        }

        slot.LastUsedTick = now;
        return slot;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        disposal.Cancel();
        foreach (var slot in thumbnails.Values)
        {
            DisposeWhenDone(slot.Task);
            if (slot.Previous != null)
                DisposeWhenDone(slot.Previous);
        }

        thumbnails.Clear();
        http.Dispose();
        disposal.Dispose();
    }

    private void ApplyResults(Configuration configuration, HashSet<string> changed)
    {
        while (results.TryDequeue(out var result))
        {
            if (!result.Final)
                continue; // Network trouble: try again next session.

            foreach (var (screenId, entry) in DescribedEntries(configuration))
            {
                if (entry.MetadataLookedUp
                    || entry.ProviderKind != result.Provider
                    || !string.Equals(entry.Url, result.Url, StringComparison.Ordinal))
                    continue;

                if (result.Metadata is { } metadata)
                    Apply(entry, metadata);

                entry.MetadataLookedUp = true;
                if (screenId != null)
                    changed.Add(screenId);
            }
        }
    }

    /// <summary>
    /// The thumbnail of what the screen is showing, or null when there's no link, the site has no picture,
    /// or it hasn't loaded yet. A matching playlist entry's details are used when it has them.
    /// </summary>
    public IDalamudTextureWrap? GetSourceThumbnail(BrowserScreenProfile screen)
    {
        return FindSourceEntry(screen) is { } entry ? GetThumbnail(entry) : null;
    }

    /// <summary>The thumbnail address <see cref="GetSourceThumbnail"/> uses, or empty.</summary>
    internal string GetSourceThumbnailUrl(BrowserScreenProfile screen) => FindSourceEntry(screen)?.ThumbnailUrl ?? string.Empty;

    private PlaylistEntry? FindSourceEntry(BrowserScreenProfile screen)
    {
        if (!shown.TryGetValue(screen.ScreenId, out var link))
            return null;

        foreach (var entry in screen.Playlist)
        {
            if (entry.ProviderKind == link.Provider
                && entry.ThumbnailUrl.Length > 0
                && string.Equals(entry.Url, link.Url, StringComparison.Ordinal))
                return entry;
        }

        return sources.GetValueOrDefault(SourceKey(link.Provider, link.Url));
    }

    /// <summary>
    /// Notes which link each screen's player is on and keeps an in-memory entry for it. Links from earlier
    /// in the session are kept (up to <see cref="MaxSources"/>), since their lookup has already run.
    /// </summary>
    private void SyncSources(Configuration configuration)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        shown.Clear();
        foreach (var screen in configuration.BrowserScreens)
        {
            if (GetShownLink(screen) is not { } url)
                continue;

            shown[screen.ScreenId] = (screen.ProviderKind, url);
            var key = SourceKey(screen.ProviderKind, url);
            current.Add(key);
            if (!sources.ContainsKey(key))
                sources[key] = CreateSource(configuration, screen.ProviderKind, url);
        }

        if (sources.Count <= MaxSources)
            return;

        // Forgotten links may be asked about again.
        foreach (var key in sources.Keys.Where(key => !current.Contains(key)).ToArray())
        {
            sources.Remove(key);
            requested.Remove(key);
        }
    }

    /// <summary>
    /// The link of the video the screen's player is on. That can differ from the screen's link when a
    /// YouTube or Dailymotion playlist moves on, or YouTube autoplays the next video. A playlist link
    /// resolves to its current video, so the tile shows that video.
    /// </summary>
    private string? GetShownLink(BrowserScreenProfile screen)
    {
        var url = BrowserSourceProviderRegistry.GetUrl(screen);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var playing = getTelemetry(screen)?.CanonicalUrl;
        if (!string.IsNullOrWhiteSpace(playing)
            && PlaylistOEmbed.IsSupported(screen.ProviderKind)
            && BrowserSourceDetector.TryDetect(playing, screen.ProviderKind, out var detected)
            && detected.ProviderKind == screen.ProviderKind)
            url = detected.Url;

        return PlaylistOEmbed.CurrentVideoLink(screen.ProviderKind, url) ?? url;
    }

    /// <summary>
    /// A new screen link, starting from a playlist entry's details when one already described it: its
    /// lookup has run, so it wouldn't be asked again.
    /// </summary>
    private static PlaylistEntry CreateSource(Configuration configuration, BrowserSourceProviderKind provider, string url)
    {
        var source = new PlaylistEntry { ProviderKind = provider, Url = url };
        foreach (var screen in configuration.BrowserScreens)
        {
            foreach (var entry in screen.Playlist)
            {
                if (!entry.MetadataLookedUp || entry.ProviderKind != provider || !string.Equals(entry.Url, url, StringComparison.Ordinal))
                    continue;

                source.MetadataTitle = entry.MetadataTitle;
                source.Author = entry.Author;
                source.ThumbnailUrl = entry.ThumbnailUrl;
                source.DurationSeconds = entry.DurationSeconds;
                source.MetadataLookedUp = true;
                return source;
            }
        }

        return source;
    }

    /// <summary>Every entry to describe: playlist entries with their screen, then the screens' links.</summary>
    private IEnumerable<(string? ScreenId, PlaylistEntry Entry)> DescribedEntries(Configuration configuration)
    {
        foreach (var screen in configuration.BrowserScreens)
        {
            foreach (var entry in screen.Playlist)
                yield return (screen.ScreenId, entry);
        }

        foreach (var source in sources.Values)
            yield return (null, source);
    }

    private static string SourceKey(BrowserSourceProviderKind provider, string url) => $"{provider}|{url}";

    internal static void Apply(PlaylistEntry entry, VideoMetadata metadata)
    {
        entry.MetadataTitle = metadata.Title;
        if (metadata.Author.Length > 0)
            entry.Author = metadata.Author;
        if (metadata.ThumbnailUrl.Length > 0)
            entry.ThumbnailUrl = metadata.ThumbnailUrl;
        if (metadata.DurationSeconds > 0)
            entry.DurationSeconds = metadata.DurationSeconds;
    }

    private void StartLookups(Configuration configuration, HashSet<string> changed)
    {
        foreach (var (screenId, entry) in DescribedEntries(configuration))
        {
            if (entry.MetadataLookedUp)
                continue;

            // Twitch channels are described from the link alone.
            if (PlaylistOEmbed.TryDescribeLocally(entry, out var local))
            {
                Apply(entry, local);
                entry.MetadataLookedUp = true;
                if (screenId != null)
                    changed.Add(screenId);
                continue;
            }

            if (!PlaylistOEmbed.IsSupported(entry.ProviderKind))
                continue;

            var key = SourceKey(entry.ProviderKind, entry.Url);
            if (requested.Contains(key))
                continue;

            requested.Add(key);
            if (!PlaylistOEmbed.TryBuildRequestUri(entry, out var requestUri))
            {
                results.Enqueue(new LookupResult(entry.ProviderKind, entry.Url, null, Final: true));
                continue;
            }

            _ = LookUpAsync(entry.ProviderKind, entry.Url, requestUri, disposal.Token);
        }
    }

    private async Task LookUpAsync(BrowserSourceProviderKind provider, string url, Uri requestUri, CancellationToken cancellationToken)
    {
        try
        {
            await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var response = await http.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // 4xx means the site won't describe it (private, removed, not embeddable); don't ask again.
                    var final = (int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.TooManyRequests;
                    results.Enqueue(new LookupResult(provider, url, null, final));
                    return;
                }

                var bytes = await ReadBoundedAsync(response, PlaylistOEmbed.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                var metadata = bytes == null ? null : PlaylistOEmbed.Parse(provider, System.Text.Encoding.UTF8.GetString(bytes));
                results.Enqueue(new LookupResult(provider, url, metadata, Final: true));
            }
            finally
            {
                requestGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Looking up a playlist video's details failed.");
            results.Enqueue(new LookupResult(provider, url, null, Final: false));
        }
    }

    /// <summary>Takes the title and length from the player for entries no site described.</summary>
    private void LearnFromPlayback(Configuration configuration, HashSet<string> changed)
    {
        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var screen in configuration.BrowserScreens)
        {
            if (ScreenPlaylistPolicy.FindEntry(screen, screen.PlaylistCurrentEntryId) is not { } entry)
            {
                playing.Remove(screen.ScreenId);
                continue;
            }

            if (!playing.TryGetValue(screen.ScreenId, out var since) || since.EntryId != entry.EntryId)
            {
                since = (entry.EntryId, nowUnixMs);
                playing[screen.ScreenId] = since;
            }

            // Only reports from after this entry started, so the previous video's title isn't taken.
            var telemetry = getTelemetry(screen);
            if (telemetry is not { State: ScreenPlaybackState.Playing } || telemetry.HostTimestampUnixMs < since.SinceUnixMs)
                continue;

            if (entry.MetadataTitle.Length == 0 && ScreenPlaylistPolicy.CleanTitle(telemetry.Title) is { Length: > 0 } title)
            {
                entry.MetadataTitle = title;
                changed.Add(screen.ScreenId);
            }

            if (entry.DurationSeconds == 0 && telemetry.DurationMs > 0 && PlaylistOEmbed.IsSingleVideo(entry))
            {
                entry.DurationSeconds = (int)Math.Min(int.MaxValue, telemetry.DurationMs / 1000);
                if (entry.DurationSeconds > 0)
                    changed.Add(screen.ScreenId);
            }
        }
    }

    private async Task<ThumbnailResult> LoadThumbnailAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            byte[]? bytes;
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                // Twitch answers an offline channel's preview with a redirect to its "offline" picture.
                if (PlaylistOEmbed.IsTwitchOfflinePreview(response.RequestMessage?.RequestUri))
                    return new ThumbnailResult(null, Checked: true, Offline: true);

                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                    return default;

                bytes = await ReadBoundedAsync(response, MaxThumbnailBytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                requestGate.Release();
            }

            var image = bytes == null
                ? null
                : await textures!.CreateFromImageAsync(bytes, "CrystalCast playlist thumbnail", cancellationToken).ConfigureAwait(false);
            return new ThumbnailResult(image, Checked: true, Offline: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Loading a playlist thumbnail failed.");
            return default;
        }
    }

    /// <summary>Frees thumbnails no list row has drawn for a while.</summary>
    private void TrimThumbnails(long now)
    {
        foreach (var (url, slot) in thumbnails.Where(pair => now - pair.Value.LastUsedTick > ThumbnailIdleMs).ToArray())
        {
            thumbnails.Remove(url);
            DisposeWhenDone(slot.Task);
            if (slot.Previous != null)
                DisposeWhenDone(slot.Previous);
        }
    }

    private static void DisposeWhenDone(Task<ThumbnailResult> task)
    {
        task.ContinueWith(
            finished =>
            {
                if (finished.IsCompletedSuccessfully)
                    finished.Result.Image?.Dispose();
            },
            TaskScheduler.Default);
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private sealed class ThumbnailSlot(Task<ThumbnailResult> task, long createdTick)
    {
        public Task<ThumbnailResult> Task { get; } = task;
        public long CreatedTick { get; } = createdTick;
        public long LastUsedTick { get; set; }

        /// <summary>The last picture, shown while a refreshed one loads.</summary>
        public Task<ThumbnailResult>? Previous { get; set; }
    }

    /// <summary><paramref name="Checked"/> is false when the image couldn't be fetched at all.</summary>
    private readonly record struct ThumbnailResult(IDalamudTextureWrap? Image, bool Checked, bool Offline);

    private readonly record struct LookupResult(BrowserSourceProviderKind Provider, string Url, VideoMetadata? Metadata, bool Final);
}
