using System.Net.Http.Headers;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace CrystalCast.Twitch;

/// <summary>
/// Emote and badge pictures for the chat, loaded from Twitch's image host on first use and kept while they're
/// shown. Only https pictures on static-cdn.jtvnw.net are loaded. Pictures not drawn for a few minutes are
/// released.
/// </summary>
internal sealed class TwitchImageCache : IDisposable
{
    /// <summary>Twitch's image host, and the 7TV, BTTV and FFZ emote hosts.</summary>
    private static readonly string[] ImageHosts = ["static-cdn.jtvnw.net", "cdn.7tv.app", "cdn.betterttv.net", "cdn.frankerfacez.com"];
    private const int MaxImageBytes = 512 * 1024;
    private const int MaxConcurrentLoads = 4;
    private const long IdleReleaseMs = 300_000;
    private const long TrimIntervalMs = 30_000;

    private readonly ITextureProvider? textures;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(MaxConcurrentLoads);
    private readonly CancellationTokenSource disposal = new();
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private long lastTrimTick;

    public TwitchImageCache(ITextureProvider? textures, HttpMessageHandler? handler = null)
    {
        this.textures = textures;
        http = new HttpClient(handler ?? new SocketsHttpHandler { MaxAutomaticRedirections = 2 })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CrystalCast", typeof(TwitchImageCache).Assembly.GetName().Version?.ToString(3) ?? "1"));
    }

    /// <summary>A Twitch emote's picture: the still version, in Twitch's dark-theme style, at 2x for sharpness.</summary>
    public static string EmoteUrl(string emoteId) =>
        $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(emoteId)}/static/dark/2.0";

    internal static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && ImageHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>The picture once loaded; starts loading it the first time. Null while loading or when it failed.</summary>
    public IDalamudTextureWrap? Get(string url)
    {
        if (textures == null || !IsAllowed(url))
            return null;

        var now = Environment.TickCount64;
        if (!slots.TryGetValue(url, out var slot))
        {
            slot = new Slot(LoadAsync(url, disposal.Token));
            slots[url] = slot;
        }

        slot.LastUsedTick = now;
        Trim(now);
        return slot.Task.IsCompletedSuccessfully ? slot.Task.Result : null;
    }

    public void Dispose()
    {
        disposal.Cancel();
        foreach (var slot in slots.Values)
            Release(slot);
        slots.Clear();
        http.Dispose();
    }

    private async Task<IDalamudTextureWrap?> LoadAsync(string url, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
                return null;

            return await textures!.CreateFromImageAsync(bytes, "CrystalCast Twitch chat image", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private void Trim(long now)
    {
        if (now - lastTrimTick < TrimIntervalMs)
            return;

        lastTrimTick = now;
        foreach (var (url, slot) in slots.Where(pair => now - pair.Value.LastUsedTick > IdleReleaseMs && pair.Value.Task.IsCompleted).ToArray())
        {
            slots.Remove(url);
            Release(slot);
        }
    }

    private static void Release(Slot slot)
    {
        if (slot.Task.IsCompletedSuccessfully)
            slot.Task.Result?.Dispose();
        else
            _ = slot.Task.ContinueWith(task => task.Result?.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private sealed class Slot(Task<IDalamudTextureWrap?> task)
    {
        public Task<IDalamudTextureWrap?> Task { get; } = task;
        public long LastUsedTick { get; set; }
    }
}
