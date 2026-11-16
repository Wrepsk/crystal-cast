using System.Text.Json;
using CrystalCast.Browser;
using CrystalCast.Browser.WebView2;
using CrystalCast.Sources;
using Dalamud.Plugin.Services;
using Microsoft.Web.WebView2.Core;

namespace CrystalCast.YouTube;

/// <summary>What the hidden browser found: the page's data (null when it had none) and whether it was logged in.</summary>
internal sealed record YouTubeHomePage(string? Data, bool LoggedIn);

/// <summary>
/// Opens youtube.com once in a hidden browser that uses the YouTube player's profile, so YouTube sees the
/// logged-in user and serves their own home page, and hands back the page's video data (YouTube's
/// <c>ytInitialData</c>). The plugin never touches the cookies; the browser visits YouTube like a normal tab
/// would. The browser is muted, can't leave YouTube, and closes as soon as the data is read.
/// </summary>
internal static class YouTubeHomeReader
{
    internal const string HomeUrl = "https://www.youtube.com/";

    /// <summary>A cookie Google sets for YouTube once the account is logged in.</summary>
    internal const string LoginCookie = "SAPISID";
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan DataWait = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The page's data as JSON (null when YouTube's page had none, for example a consent page), and whether the
    /// profile is logged in to YouTube, so the tab knows even while no screen's player is running.
    /// </summary>
    public static Task<YouTubeHomePage> ReadAsync(IPluginLog log, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<YouTubeHomePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => ThreadMain(result, log, cancellationToken))
        {
            IsBackground = true,
            Name = "CrystalCast YouTube home reader",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    /// <summary>WebView2 needs a thread with a message loop; this one lives only as long as the read.</summary>
    private static void ThreadMain(TaskCompletionSource<YouTubeHomePage> result, IPluginLog log, CancellationToken cancellationToken)
    {
        using var context = new BrowserSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var work = ReadOnThreadAsync(cancellationToken);
            var quit = false;
            while (!work.IsCompleted && !quit)
            {
                BrowserNativeMessagePump.PumpMessages(ref quit);
                context.ExecutePending();
                BrowserNativeMessagePump.WaitForWork(context.WorkAvailable, 50);
            }

            // Let the cleanup in ReadOnThreadAsync finish closing the browser.
            var drainStart = Environment.TickCount64;
            while (!work.IsCompleted && Environment.TickCount64 - drainStart < 3000)
            {
                BrowserNativeMessagePump.PumpMessages(ref quit);
                context.ExecutePending();
                BrowserNativeMessagePump.WaitForWork(context.WorkAvailable, 25);
            }

            if (work.IsCompletedSuccessfully)
                result.TrySetResult(work.Result);
            else if (work.IsCanceled)
                result.TrySetCanceled();
            else
                result.TrySetException(work.Exception?.GetBaseException() ?? new TimeoutException("YouTube's page didn't load."));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CrystalCast couldn't read YouTube's home page.");
            result.TrySetException(ex);
        }
    }

    private static async Task<YouTubeHomePage> ReadOnThreadAsync(CancellationToken cancellationToken)
    {
        if (!WebView2BrowserFrameSource.TryGetWebView2Runtime(out _, out var runtimeError, out var browserFolder))
            throw new InvalidOperationException($"WebView2 isn't available: {runtimeError}");

        var userDataFolder = BrowserProfileManager.GetWebView2UserDataFolder(BrowserSourceProviderKind.YouTube);
        Directory.CreateDirectory(userDataFolder);

        WebView2HostWindow? hostWindow = null;
        CoreWebView2Controller? controller = null;
        try
        {
            hostWindow = WebView2HostWindow.Create(1280, 800);
            hostWindow.ShowForCapture();

            // The same options as the YouTube player, so both can share the profile at once.
            var options = new CoreWebView2EnvironmentOptions(
                additionalBrowserArguments: WebView2BrowserArguments.Combine(BrowserSourceDescriptors.YouTube.WebView2AdditionalBrowserArguments));
            var environment = await CoreWebView2Environment.CreateAsync(browserFolder, userDataFolder, options);
            WebView2BrowserGuards.TrackBrowserProcesses(environment);
            cancellationToken.ThrowIfCancellationRequested();

            controller = await environment.CreateCoreWebView2ControllerAsync(hostWindow.Hwnd);
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            controller.IsVisible = true;
            var webView = controller.CoreWebView2;
            WebView2ProcessTracker.Track((int)webView.BrowserProcessId);
            WebView2BrowserGuards.ConfigureWebView(webView);
            webView.IsMuted = true;
            webView.NewWindowRequested += WebView2BrowserGuards.OnNewWindowRequested;
            webView.PermissionRequested += WebView2BrowserGuards.OnPermissionRequested;
            webView.DownloadStarting += WebView2BrowserGuards.OnDownloadStarting;
            webView.NavigationStarting += (_, args) =>
            {
                if (!IsAllowedPage(args.Uri))
                    args.Cancel = true;
            };

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            webView.NavigationCompleted += (_, args) => loaded.TrySetResult(args.IsSuccess);
            webView.Navigate(HomeUrl);

            var finished = await Task.WhenAny(loaded.Task, Task.Delay(LoadTimeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (finished != loaded.Task)
                throw new TimeoutException("YouTube's page took too long to load.");

            // Only whether the login cookie is there; its value is never read.
            var cookies = await webView.CookieManager.GetCookiesAsync(HomeUrl);
            var loggedIn = cookies.Any(cookie => cookie.Name == LoginCookie);

            // The data is part of the page; give a slow page a moment to fill it in.
            var waitUntil = DateTime.UtcNow + DataWait;
            while (true)
            {
                var json = await webView.ExecuteScriptAsync(
                    "(() => { const d = window.ytInitialData; return d ? JSON.stringify(d) : null; })()");
                if (JsonSerializer.Deserialize<string?>(json) is { Length: > 0 } data)
                    return new YouTubeHomePage(data, loggedIn);
                if (DateTime.UtcNow > waitUntil)
                    return new YouTubeHomePage(null, loggedIn);

                await Task.Delay(500, cancellationToken);
            }
        }
        finally
        {
            WebView2BrowserGuards.BestEffort(() => controller?.Close());
            WebView2BrowserGuards.BestEffort(() => hostWindow?.Dispose());
        }
    }

    /// <summary>YouTube itself, and Google's sites for a consent or account check it may send the page to.</summary>
    internal static bool IsAllowedPage(string? url)
    {
        return BrowserSignInPolicy.IsAllowed(url, new BrowserSignIn(HomeUrl, ["youtube.com", "google.*"]));
    }
}
