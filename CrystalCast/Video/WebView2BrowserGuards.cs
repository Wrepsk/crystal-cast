using Microsoft.Web.WebView2.Core;

namespace CrystalCast.Video;

/// <summary>
/// The lock-down settings and event handlers every CrystalCast WebView2 browser uses, whether it shows a
/// provider's player page or a Generic Web page.
/// </summary>
internal static class WebView2BrowserGuards
{
    /// <summary>Runs a cleanup step and ignores its failure; used while a browser is being torn down.</summary>
    public static void BestEffort(Action step)
    {
        try
        {
            step();
        }
        catch
        {
            // Best-effort browser cleanup during plugin unload/source changes.
        }
    }

    /// <summary>Records the environment's browser processes so leftovers can be ended at unload.</summary>
    public static void TrackBrowserProcesses(CoreWebView2Environment environment)
    {
        try
        {
            foreach (var info in environment.GetProcessInfos())
            {
                if (info.Kind == CoreWebView2ProcessKind.Browser)
                    WebView2ProcessTracker.Track(info.ProcessId);
            }
        }
        catch
        {
            // Older runtimes lack process info; the browser id is recorded again once the view exists.
        }
    }

    /// <summary>Turns off the browser features a captured page must not offer: menus, dev tools, shortcuts, autofill, host objects.</summary>
    public static void ConfigureWebView(CoreWebView2 webView)
    {
        webView.Settings.AreDefaultContextMenusEnabled = false;
        webView.Settings.AreDevToolsEnabled = false;
        webView.Settings.AreBrowserAcceleratorKeysEnabled = false;
        webView.Settings.IsStatusBarEnabled = false;
        webView.Settings.IsZoomControlEnabled = false;
        webView.Settings.AreHostObjectsAllowed = false;
        webView.Settings.IsGeneralAutofillEnabled = false;
        webView.Settings.IsPasswordAutosaveEnabled = false;
    }

    /// <summary>Popups are never opened.</summary>
    public static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    /// <summary>Grants only what <see cref="BrowserPermissionPolicy"/> allows.</summary>
    public static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = BrowserPermissionPolicy.IsAllowed(args.PermissionKind.ToString())
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;
    }

    /// <summary>Downloads are always cancelled.</summary>
    public static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
    }
}
