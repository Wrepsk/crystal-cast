using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
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
        ApplyResults(configuration, changed);
        StartLookups(configuration);
        LearnFromPlayback(configuration, changed);
        TrimThumbnails(now);
        return changed;
    }

    /// <summary>The entry's thumbnail once it has loaded; starts loading it the first time it is asked for.</summary>
    public IDalamudTextureWrap? GetThumbnail(PlaylistEntry entry)
    {
        if (disposed || textures == null || !PlaylistOEmbed.IsAllowedThumbnail(entry.ProviderKind, entry.ThumbnailUrl))
            return null;

        if (!thumbnails.TryGetValue(entry.ThumbnailUrl, out var slot))
        {
            slot = new ThumbnailSlot(LoadThumbnailAsync(entry.ThumbnailUrl, disposal.Token));
            thumbnails[entry.ThumbnailUrl] = slot;
        }

        slot.LastUsedTick = Environment.TickCount64;
        return slot.Task.IsCompletedSuccessfully ? slot.Task.Result : null;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        disposal.Cancel();
        foreach (var slot in thumbnails.Values)
            DisposeWhenDone(slot.Task);

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

            foreach (var screen in configuration.BrowserScreens)
            {
                foreach (var entry in screen.Playlist)
                {
                    if (entry.MetadataLookedUp
                        || entry.ProviderKind != result.Provider
                        || !string.Equals(entry.Url, result.Url, StringComparison.Ordinal))
                        continue;

                    if (result.Metadata is { } metadata)
                        Apply(entry, metadata);

                    entry.MetadataLookedUp = true;
                    changed.Add(screen.ScreenId);
                }
            }
        }
    }

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

    private void StartLookups(Configuration configuration)
    {
        foreach (var screen in configuration.BrowserScreens)
        {
            foreach (var entry in screen.Playlist)
            {
                if (entry.MetadataLookedUp || !PlaylistOEmbed.IsSupported(entry.ProviderKind))
                    continue;

                var key = $"{entry.ProviderKind}|{entry.Url}";
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

    private async Task<IDalamudTextureWrap?> LoadThumbnailAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            byte[]? bytes;
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                    return null;

                bytes = await ReadBoundedAsync(response, MaxThumbnailBytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                requestGate.Release();
            }

            return bytes == null
                ? null
                : await textures!.CreateFromImageAsync(bytes, "CrystalCast playlist thumbnail", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Loading a playlist thumbnail failed.");
            return null;
        }
    }

    /// <summary>Frees thumbnails no list row has drawn for a while.</summary>
    private void TrimThumbnails(long now)
    {
        foreach (var (url, slot) in thumbnails.Where(pair => now - pair.Value.LastUsedTick > ThumbnailIdleMs).ToArray())
        {
            thumbnails.Remove(url);
            DisposeWhenDone(slot.Task);
        }
    }

    private static void DisposeWhenDone(Task<IDalamudTextureWrap?> task)
    {
        task.ContinueWith(
            finished =>
            {
                if (finished.IsCompletedSuccessfully)
                    finished.Result?.Dispose();
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

    private sealed class ThumbnailSlot(Task<IDalamudTextureWrap?> task)
    {
        public Task<IDalamudTextureWrap?> Task { get; } = task;
        public long LastUsedTick { get; set; }
    }

    private readonly record struct LookupResult(BrowserSourceProviderKind Provider, string Url, VideoMetadata? Metadata, bool Final);
}
