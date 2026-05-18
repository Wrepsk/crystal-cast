using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CrystalCast.Video;

public sealed class CefYouTubeBrowserFrameSource : IVideoFrameSource, IMediaPlaybackTelemetrySource, IMediaPlaybackController
{
    private static long globalPlayerPageSequence;
    private const long PlayerReadyReloadDelayMs = 8000;
    private const long LoadingFrameIntervalMs = 66;
    private const int MaxPlayerReadyReloads = 2;

    private readonly string input;
    private readonly string videoId;
    private readonly string canonicalUrl;
    private readonly bool isValidVideoId;
    private readonly bool autoplay;
    private readonly object telemetryLock = new();
    private readonly Dictionary<string, Delegate> browserEventHandlers = new(StringComparer.Ordinal);
    private object? browser;
    private VideoFrame? latestFrame;
    private VideoFrame? latestLoadingFrame;
    private MediaPlaybackTelemetry telemetry = new();
    private bool loop;
    private bool audioEnabled;
    private float volume;
    private float playbackRate;
    private volatile bool captureEnabled;
    private volatile string browserStatus = "stopped";
    private volatile string playerStatus = "player not ready";
    private long sequence;
    private long lastFrameUnixMs;
    private long captureWindowStartUnixMs;
    private long lastPlayerLoadUnixMs;
    private long playerReadyUnixMs;
    private long lastLoadingFrameUnixMs;
    private int captureWindowFrames;
    private int playerLoadAttempt;
    private double measuredCaptureFps;
    private double lastPaintMilliseconds;
    private bool playerReady;
    private bool playerFailed;

    public CefYouTubeBrowserFrameSource(
        string input,
        int width,
        int height,
        float captureFps,
        bool autoplay,
        bool loop,
        bool audioEnabled,
        float volume,
        float playbackRate)
    {
        this.input = input;
        isValidVideoId = YouTubeVideoId.TryParse(input, out videoId);
        canonicalUrl = YouTubeVideoId.BuildCanonicalWatchUrl(videoId);
        Width = Math.Clamp(width, 320, 3840);
        Height = Math.Clamp(height, 180, 2160);
        FramesPerSecond = Math.Clamp(captureFps, 1.0f, 60.0f);
        this.autoplay = autoplay;
        this.loop = loop;
        this.audioEnabled = audioEnabled;
        this.volume = QuantizeVolume(volume);
        this.playbackRate = ClampPlaybackRate(playbackRate);

        if (!isValidVideoId)
            browserStatus = "invalid YouTube URL or video ID";

        UpdateTelemetry(ScreenPlaybackState.Stopped, 0, this.playbackRate, string.Empty);
    }

    public string Name => "YouTube browser (CEF offscreen)";
    public int Width { get; }
    public int Height { get; }
    public float FramesPerSecond { get; }
    public bool IsRunning => captureEnabled;

    public string Status
    {
        get
        {
            var frameAge = lastFrameUnixMs == 0
                ? "no painted frame"
                : $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastFrameUnixMs} ms frame age";
            return $"{browserStatus}; {playerStatus}; paint {measuredCaptureFps:0.#}/{FramesPerSecond:0.#} fps; {lastPaintMilliseconds:0.#} ms; {frameAge}; {CefRuntimeManager.Status}";
        }
    }

    public void Start()
    {
        var wasCaptureEnabled = captureEnabled;
        if (!isValidVideoId)
        {
            browserStatus = $"invalid YouTube URL or video ID: {input}";
            return;
        }

        if (browser == null)
            CreateBrowser();

        if (browser == null)
            return;

        captureEnabled = true;
        MaybeReloadPlayerIfNotReady();
        if (!wasCaptureEnabled)
            Play();
    }

    public void Stop()
    {
        var wasCaptureEnabled = captureEnabled;
        captureEnabled = false;
        if (wasCaptureEnabled)
            Pause();

        UpdateTelemetry(ScreenPlaybackState.Paused, GetTelemetryPositionMs(), playbackRate, GetTelemetryTitle());
    }

    public void ApplyPlaybackSettings(bool audioEnabled, float volume, float playbackRate, bool loop)
    {
        volume = QuantizeVolume(volume);
        playbackRate = ClampPlaybackRate(playbackRate);

        if (this.audioEnabled == audioEnabled
            && Math.Abs(this.volume - volume) < 0.001f
            && Math.Abs(this.playbackRate - playbackRate) < 0.001f
            && this.loop == loop)
        {
            return;
        }

        this.audioEnabled = audioEnabled;
        this.volume = volume;
        this.playbackRate = playbackRate;
        this.loop = loop;
        ExecutePlayerScript("crystalCastApplySettings", new
        {
            audioEnabled,
            volume,
            playbackRate,
            loop,
        });
    }

    public void Play()
    {
        captureEnabled = true;
        if (GetTelemetryState() != ScreenPlaybackState.Playing)
            SendPlayerActivationClick();

        ExecutePlayerScript("crystalCastPlay");
    }

    public void Pause()
    {
        captureEnabled = false;
        ExecutePlayerScript("crystalCastPause");
        UpdateTelemetry(ScreenPlaybackState.Paused, GetTelemetryPositionMs(), playbackRate, GetTelemetryTitle());
    }

    public void SeekBy(double seconds)
    {
        ExecutePlayerScript("crystalCastSeekBy", seconds);
    }

    public void Restart()
    {
        captureEnabled = true;
        ExecutePlayerScript("crystalCastRestart");
    }

    public bool TryGetLatestFrame(out VideoFrame frame)
    {
        if (ShouldShowLoadingFrame())
        {
            frame = GetLoadingFrame();
            return true;
        }

        frame = latestFrame!;
        return frame != null;
    }

    public bool TryGetPlaybackTelemetry(out MediaPlaybackTelemetry currentTelemetry)
    {
        lock (telemetryLock)
        {
            currentTelemetry = telemetry;
        }

        return isValidVideoId;
    }

    public void Dispose()
    {
        captureEnabled = false;
        if (browser != null)
        {
            CloseBrowserHost(browser);
            RemoveBrowserEventHandlers(browser);
            if (browser is IDisposable disposable)
                disposable.Dispose();

            browser = null;
        }

        browserStatus = "disposed";
        UpdateTelemetry(ScreenPlaybackState.Stopped, GetTelemetryPositionMs(), playbackRate, GetTelemetryTitle());
    }

    private void CreateBrowser()
    {
        if (!CefRuntimeManager.TryInitialize(out var cefStatus))
        {
            browserStatus = cefStatus;
            return;
        }

        try
        {
            var browserSettingsType = GetCefType("CefSharp.Core", "CefSharp.BrowserSettings");
            var chromiumBrowserType = GetCefType("CefSharp.OffScreen", "CefSharp.OffScreen.ChromiumWebBrowser");
            var browserSettings = CreateBrowserSettings(browserSettingsType);
            SetInstanceProperty(browserSettings, "WindowlessFrameRate", (int)Math.Clamp(MathF.Round(FramesPerSecond), 1.0f, 60.0f));
            SetInstanceProperty(browserSettings, "BackgroundColor", 0xFF000000u);

            browser = CreateChromiumBrowser(chromiumBrowserType, browserSettings);
            SetInstanceProperty(browser, "Size", new System.Drawing.Size(Width, Height));
            AddBrowserEventHandler(browser, "Paint", nameof(OnPaint));
            AddBrowserEventHandler(browser, "JavascriptMessageReceived", nameof(OnJavascriptMessageReceived));
            AddBrowserEventHandler(browser, "LoadError", nameof(OnLoadError));
            AddBrowserEventHandler(browser, "FrameLoadEnd", nameof(OnFrameLoadEnd));
            AddBrowserEventHandler(browser, "LoadingStateChanged", nameof(OnLoadingStateChanged));
            AddBrowserEventHandler(browser, "ConsoleMessage", nameof(OnConsoleMessage));
            AddBrowserEventHandler(browser, "StatusMessage", nameof(OnStatusMessage));
            AddBrowserEventHandler(browser, "TitleChanged", nameof(OnTitleChanged));

            LoadPlayerHtml("new video");
        }
        catch (Exception ex)
        {
            browserStatus = $"CEF browser failed: {ex.Message}";
            Plugin.Log.Warning(ex, "Failed to create CEF YouTube browser.");
        }
    }

    private void LoadPlayerHtml(string reason)
    {
        var currentBrowser = browser;
        if (currentBrowser == null || GetInstanceProperty<bool>(currentBrowser, "IsDisposed"))
            return;

        playerReady = false;
        playerFailed = false;
        playerReadyUnixMs = 0;
        latestLoadingFrame = null;
        lastLoadingFrameUnixMs = 0;
        playerStatus = "player not ready";
        var sequence = Interlocked.Increment(ref globalPlayerPageSequence);
        var pageUrl = $"{YouTubePlayerPage.PlayerOrigin}/player.html?video={Uri.EscapeDataString(videoId)}&attempt={playerLoadAttempt}&seq={sequence}";
        var html = YouTubePlayerPage.BuildHtml(videoId, autoplay, loop, audioEnabled, volume, playbackRate);
        InvokeWebBrowserExtension("LoadHtml", currentBrowser, html, pageUrl);
        lastPlayerLoadUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        browserStatus = $"CEF loading YouTube player ({reason})";
    }

    private void MaybeReloadPlayerIfNotReady()
    {
        if (browser == null || playerReady || playerFailed || playerLoadAttempt >= MaxPlayerReadyReloads || lastPlayerLoadUnixMs == 0)
            return;

        var elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastPlayerLoadUnixMs;
        if (elapsedMs < PlayerReadyReloadDelayMs)
            return;

        try
        {
            playerLoadAttempt++;
            LoadPlayerHtml($"retry {playerLoadAttempt}");
        }
        catch (Exception ex)
        {
            playerFailed = true;
            browserStatus = $"CEF player reload failed: {ex.Message}";
            Plugin.Log.Debug(ex, "Failed to reload CEF YouTube player after ready timeout.");
        }
    }

    private bool ShouldShowLoadingFrame()
    {
        if (!captureEnabled || browser == null || playerFailed)
            return false;

        return !playerReady || lastFrameUnixMs < playerReadyUnixMs;
    }

    private VideoFrame GetLoadingFrame()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (latestLoadingFrame != null && now - lastLoadingFrameUnixMs < LoadingFrameIntervalMs)
            return latestLoadingFrame;

        var elapsedMs = Math.Max(0, now - (lastPlayerLoadUnixMs == 0 ? now : lastPlayerLoadUnixMs));
        var pixels = RenderLoadingFrame(Width, Height, elapsedMs);
        latestLoadingFrame = new VideoFrame(pixels, Width, Height, Interlocked.Increment(ref sequence), now);
        lastLoadingFrameUnixMs = now;
        return latestLoadingFrame;
    }

    private static byte[] RenderLoadingFrame(int width, int height, long elapsedMs)
    {
        var pixels = new byte[width * height * 4];
        FillBackground(pixels);
        DrawLoadingRing(pixels, width, height, elapsedMs);
        return pixels;
    }

    private static void FillBackground(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x06;
            pixels[i + 1] = 0x05;
            pixels[i + 2] = 0x05;
            pixels[i + 3] = 0xFF;
        }
    }

    private static void DrawLoadingRing(byte[] pixels, int width, int height, long elapsedMs)
    {
        var shortest = Math.Max(1, Math.Min(width, height));
        var centerX = width * 0.5f;
        var centerY = height * 0.5f;
        var radius = Math.Clamp(shortest * 0.06f, 22.0f, 76.0f);
        var thickness = Math.Clamp(radius * 0.16f, 4.0f, 10.0f);
        var coreRadius = Math.Clamp(radius * (0.14f + (0.04f * MathF.Sin(elapsedMs / 190.0f))), 4.0f, 13.0f);
        var spin = (elapsedMs % 900) / 900.0f * MathF.Tau;
        var minX = Math.Max(0, (int)MathF.Floor(centerX - radius - thickness - 2.0f));
        var maxX = Math.Min(width - 1, (int)MathF.Ceiling(centerX + radius + thickness + 2.0f));
        var minY = Math.Max(0, (int)MathF.Floor(centerY - radius - thickness - 2.0f));
        var maxY = Math.Min(height - 1, (int)MathF.Ceiling(centerY + radius + thickness + 2.0f));

        for (var y = minY; y <= maxY; y++)
        {
            var dy = y + 0.5f - centerY;
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x + 0.5f - centerX;
                var distance = MathF.Sqrt((dx * dx) + (dy * dy));
                var ringDistance = MathF.Abs(distance - radius);
                var index = ((y * width) + x) * 4;

                if (distance <= coreRadius)
                {
                    BlendPixel(pixels, index, 255, 255, 255, 0.86f);
                    continue;
                }

                if (ringDistance > thickness)
                    continue;

                var angle = MathF.Atan2(dy, dx) - spin;
                while (angle < 0.0f)
                    angle += MathF.Tau;
                while (angle >= MathF.Tau)
                    angle -= MathF.Tau;

                var arc = angle / MathF.Tau;
                var sweep = 1.0f - MathF.Min(1.0f, arc * 1.55f);
                var baseAlpha = 0.12f + (0.8f * sweep);
                var edge = 1.0f - Math.Clamp(ringDistance / thickness, 0.0f, 1.0f);
                var alpha = Math.Clamp(baseAlpha * edge, 0.0f, 0.96f);
                BlendPixel(pixels, index, 132, 213, 255, alpha);
            }
        }
    }

    private static void BlendPixel(byte[] pixels, int index, byte red, byte green, byte blue, float alpha)
    {
        alpha = Math.Clamp(alpha, 0.0f, 1.0f);
        pixels[index] = BlendChannel(pixels[index], blue, alpha);
        pixels[index + 1] = BlendChannel(pixels[index + 1], green, alpha);
        pixels[index + 2] = BlendChannel(pixels[index + 2], red, alpha);
        pixels[index + 3] = 0xFF;
    }

    private static byte BlendChannel(byte background, byte foreground, float alpha)
    {
        return (byte)Math.Clamp(MathF.Round(background + ((foreground - background) * alpha)), 0, 255);
    }

    private void ExecutePlayerScript(string functionName, object? argument = null)
    {
        var currentBrowser = browser;
        if (currentBrowser == null || GetInstanceProperty<bool>(currentBrowser, "IsDisposed"))
            return;

        try
        {
            var script = argument == null
                ? $"window.{functionName} && window.{functionName}();"
                : $"window.{functionName} && window.{functionName}({JsonSerializer.Serialize(argument, YouTubePlayerPage.JsonOptions)});";
            InvokeWebBrowserExtension("ExecuteScriptAsync", currentBrowser, script);
        }
        catch (Exception ex)
        {
            browserStatus = $"CEF command failed: {ex.Message}";
        }
    }

    private void SendPlayerActivationClick()
    {
        var currentBrowser = browser;
        if (currentBrowser == null || GetInstanceProperty<bool>(currentBrowser, "IsDisposed"))
            return;

        try
        {
            var host = InvokeWebBrowserExtension("GetBrowserHost", currentBrowser);
            if (host == null)
                return;

            var flags = Enum.ToObject(GetCefType("CefSharp", "CefSharp.CefEventFlags"), 0);
            var leftButton = Enum.Parse(GetCefType("CefSharp", "CefSharp.MouseButtonType"), "Left");
            var x = Math.Clamp(Width / 2, 1, Math.Max(1, Width - 1));
            var y = Math.Clamp(Height / 2, 1, Math.Max(1, Height - 1));

            TryInvokeInstanceMethod(host, "SendFocusEvent", true);
            TryInvokeInstanceMethod(host, "SetFocus", true);
            InvokeWebBrowserExtension("SendMouseMoveEvent", host, x, y, false, flags);
            InvokeWebBrowserExtension("SendMouseClickEvent", host, x, y, leftButton, false, 1, flags);
            InvokeWebBrowserExtension("SendMouseClickEvent", host, x, y, leftButton, true, 1, flags);
        }
        catch (Exception ex)
        {
            browserStatus = $"CEF activation click failed: {ex.Message}";
            Plugin.Log.Debug(ex, "Failed to send CEF activation click.");
        }
    }

    private static void CloseBrowserHost(object currentBrowser)
    {
        try
        {
            var host = InvokeWebBrowserExtension("GetBrowserHost", currentBrowser);
            if (host != null)
                TryInvokeInstanceMethod(host, "CloseBrowser", true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to close CEF browser host before disposal.");
        }
    }

    private static Type GetCefType(string assemblyName, string typeName)
    {
        if (CefRuntimeManager.TryGetLoadedType(assemblyName, typeName, out var type, out var cefStatus) && type != null)
            return type;

        throw new InvalidOperationException(cefStatus);
    }

    private static object CreateChromiumBrowser(Type chromiumBrowserType, object browserSettings)
    {
        foreach (var constructor in chromiumBrowserType.GetConstructors())
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length == 6 && parameters[0].ParameterType == typeof(string))
            {
                return constructor.Invoke(
                [
                    "about:blank",
                    browserSettings,
                    null,
                    true,
                    null,
                    true,
                ]);
            }
        }

        throw new MissingMethodException(chromiumBrowserType.FullName, ".ctor(string, IBrowserSettings, IRequestContext, bool, Action<IBrowser>, bool)");
    }

    private static object CreateBrowserSettings(Type browserSettingsType)
    {
        var createMethod = browserSettingsType.GetMethod("Create", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, [typeof(bool)]);
        if (createMethod != null)
            return createMethod.Invoke(null, [true]) ?? throw new InvalidOperationException("Failed to create CEF browser settings.");

        var boolConstructor = browserSettingsType.GetConstructor([typeof(bool)]);
        if (boolConstructor != null)
            return boolConstructor.Invoke([true]);

        return Activator.CreateInstance(browserSettingsType)
            ?? throw new InvalidOperationException("Failed to create CEF browser settings.");
    }

    private void AddBrowserEventHandler(object target, string eventName, string methodName)
    {
        var eventInfo = target.GetType().GetEvent(eventName)
            ?? throw new MissingMemberException(target.GetType().FullName, eventName);
        var handler = CreateEventDelegate(eventInfo.EventHandlerType!, methodName);
        eventInfo.AddEventHandler(target, handler);
        browserEventHandlers[eventName] = handler;
    }

    private void RemoveBrowserEventHandlers(object target)
    {
        foreach (var (eventName, handler) in browserEventHandlers)
        {
            target.GetType().GetEvent(eventName)?.RemoveEventHandler(target, handler);
        }

        browserEventHandlers.Clear();
    }

    private Delegate CreateEventDelegate(Type eventHandlerType, string methodName)
    {
        var method = GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(GetType().FullName, methodName);
        var direct = Delegate.CreateDelegate(eventHandlerType, this, method, throwOnBindFailure: false);
        if (direct != null)
            return direct;

        var invoke = eventHandlerType.GetMethod("Invoke")
            ?? throw new MissingMethodException(eventHandlerType.FullName, "Invoke");
        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        Expression sender = parameters.Length > 0
            ? Expression.Convert(parameters[0], typeof(object))
            : Expression.Constant(null, typeof(object));
        Expression args = parameters.Length > 1
            ? Expression.Convert(parameters[1], typeof(object))
            : Expression.Constant(null, typeof(object));
        var call = Expression.Call(Expression.Constant(this), method, sender, args);
        return Expression.Lambda(eventHandlerType, call, parameters).Compile();
    }

    private static object? InvokeWebBrowserExtension(string methodName, object browser, params object?[] arguments)
    {
        var extensionsType = GetCefType("CefSharp", "CefSharp.WebBrowserExtensions");
        var callArguments = new object?[arguments.Length + 1];
        callArguments[0] = browser;
        Array.Copy(arguments, 0, callArguments, 1, arguments.Length);

        foreach (var method in extensionsType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (method.Name != methodName)
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length != callArguments.Length)
                continue;

            if (!ParametersAccept(parameters, callArguments))
                continue;

            return method.Invoke(null, callArguments);
        }

        throw new MissingMethodException(extensionsType.FullName, methodName);
    }

    private static bool TryInvokeInstanceMethod(object instance, string methodName, params object?[] arguments)
    {
        foreach (var method in instance.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (method.Name != methodName)
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length != arguments.Length)
                continue;

            if (!ParametersAccept(parameters, arguments))
                continue;

            method.Invoke(instance, arguments);
            return true;
        }

        return false;
    }

    private static bool ParametersAccept(System.Reflection.ParameterInfo[] parameters, object?[] arguments)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            var argument = arguments[i];
            if (argument == null)
            {
                if (parameters[i].ParameterType.IsValueType && Nullable.GetUnderlyingType(parameters[i].ParameterType) == null)
                    return false;

                continue;
            }

            if (!parameters[i].ParameterType.IsInstanceOfType(argument))
                return false;
        }

        return true;
    }

    private static void SetInstanceProperty(object instance, string propertyName, object? value)
    {
        var property = instance.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(instance.GetType().FullName, propertyName);
        property.SetValue(instance, ConvertValue(value, property.PropertyType));
    }

    private static T GetInstanceProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(instance.GetType().FullName, propertyName);
        var value = property.GetValue(instance);
        if (value is T typed)
            return typed;

        if (value == null)
            return default!;

        return (T)ConvertValue(value, typeof(T))!;
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value == null)
            return null;

        if (targetType.IsInstanceOfType(value))
            return value;

        var realTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (realTargetType.IsEnum)
            return Enum.ToObject(realTargetType, value);

        return Convert.ChangeType(value, realTargetType);
    }

    private void OnPaint(object? sender, object args)
    {
        var isPopup = GetInstanceProperty<bool>(args, "IsPopup");
        var width = GetInstanceProperty<int>(args, "Width");
        var height = GetInstanceProperty<int>(args, "Height");
        var bufferHandle = GetInstanceProperty<IntPtr>(args, "BufferHandle");
        if (isPopup || !captureEnabled || width <= 0 || height <= 0 || bufferHandle == IntPtr.Zero)
            return;

        var sw = Stopwatch.StartNew();
        var length = width * height * 4;
        var pixels = new byte[length];
        Marshal.Copy(bufferHandle, pixels, 0, length);
        sw.Stop();
        lastPaintMilliseconds = sw.Elapsed.TotalMilliseconds;
        browserStatus = "CEF BGRA paint running";
        PublishFrame(pixels, width, height);
    }

    private void PublishFrame(byte[] pixels, int width, int height)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var frame = new VideoFrame(pixels, width, height, Interlocked.Increment(ref sequence), now);
        Interlocked.Exchange(ref latestFrame, frame);
        lastFrameUnixMs = now;

        if (captureWindowStartUnixMs == 0)
            captureWindowStartUnixMs = now;

        captureWindowFrames++;
        var elapsedMs = now - captureWindowStartUnixMs;
        if (elapsedMs >= 1000)
        {
            measuredCaptureFps = captureWindowFrames * 1000.0 / elapsedMs;
            captureWindowFrames = 0;
            captureWindowStartUnixMs = now;
        }
    }

    private void OnJavascriptMessageReceived(object? sender, object args)
    {
        try
        {
            var message = GetInstanceProperty<object?>(args, "Message");
            var json = message as string ?? JsonSerializer.Serialize(message, YouTubePlayerPage.JsonOptions);
            using var document = JsonDocument.Parse(json);
            UpdateFromWebMessage(document.RootElement);
        }
        catch (Exception ex)
        {
            playerStatus = $"invalid CEF browser message: {ex.Message}";
        }
    }

    private void OnLoadError(object? sender, object args)
    {
        var frame = GetInstanceProperty<object?>(args, "Frame");
        if (frame != null && GetInstanceProperty<bool>(frame, "IsMain"))
            browserStatus = $"CEF load failed: {GetInstanceProperty<object?>(args, "ErrorCode")} {GetInstanceProperty<string?>(args, "ErrorText")}";
    }

    private void OnFrameLoadEnd(object? sender, object args)
    {
        var frame = GetInstanceProperty<object?>(args, "Frame");
        if (frame != null && GetInstanceProperty<bool>(frame, "IsMain"))
            browserStatus = $"CEF page loaded: HTTP {GetInstanceProperty<int>(args, "HttpStatusCode")}";
    }

    private void OnLoadingStateChanged(object? sender, object args)
    {
        if (!GetInstanceProperty<bool>(args, "IsLoading") && browserStatus.StartsWith("CEF loading", StringComparison.Ordinal))
            browserStatus = "CEF page loaded";
    }

    private void OnConsoleMessage(object? sender, object args)
    {
        var message = GetInstanceProperty<string?>(args, "Message");
        var line = GetInstanceProperty<int>(args, "Line");
        if (!string.IsNullOrWhiteSpace(message))
            playerStatus = $"CEF console: {message} (line {line})";
    }

    private void OnStatusMessage(object? sender, object args)
    {
        var value = GetInstanceProperty<string?>(args, "Value");
        if (!string.IsNullOrWhiteSpace(value))
            browserStatus = $"CEF status: {value}";
    }

    private void OnTitleChanged(object? sender, object args)
    {
        var title = GetInstanceProperty<string?>(args, "Title");
        if (!string.IsNullOrWhiteSpace(title) && title.StartsWith("CrystalCast:", StringComparison.Ordinal))
            playerStatus = title["CrystalCast:".Length..];
    }

    private void UpdateFromWebMessage(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeProperty))
            return;

        var type = typeProperty.GetString();
        switch (type)
        {
            case "ready":
                playerReady = true;
                playerFailed = false;
                playerReadyUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                playerStatus = $"player ready: {videoId}";
                break;
            case "status":
                UpdateFromStatusMessage(root);
                break;
            case "error":
                playerFailed = true;
                playerStatus = DescribeYouTubeError(root);
                break;
            case "script-error":
                playerStatus = TryGetString(root, "message", "script error");
                break;
            case "debug":
                playerStatus = TryGetString(root, "message", "browser debug");
                break;
        }
    }

    private void UpdateFromStatusMessage(JsonElement root)
    {
        var title = TryGetString(root, "title", string.Empty);
        var positionSeconds = TryGetDouble(root, "positionSeconds", 0.0);
        var rate = (float)TryGetDouble(root, "rate", playbackRate);
        var stateCode = TryGetInt(root, "state", -1);
        var playbackState = stateCode switch
        {
            1 or 3 => ScreenPlaybackState.Playing,
            2 or 5 => ScreenPlaybackState.Paused,
            _ => ScreenPlaybackState.Stopped,
        };

        var positionMs = (long)Math.Max(0.0, positionSeconds * 1000.0);
        UpdateTelemetry(playbackState, positionMs, rate, title);
        playerStatus = string.IsNullOrWhiteSpace(title)
            ? $"player state {stateCode}; {positionSeconds:0.0}s"
            : $"{title}; state {stateCode}; {positionSeconds:0.0}s";
    }

    private void UpdateTelemetry(ScreenPlaybackState state, long positionMs, float rate, string title)
    {
        lock (telemetryLock)
        {
            telemetry = new MediaPlaybackTelemetry
            {
                State = state,
                PositionMs = positionMs,
                Rate = ClampPlaybackRate(rate),
                Title = title,
                VideoId = videoId,
                CanonicalUrl = canonicalUrl,
                HostTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }
    }

    private string GetTelemetryTitle()
    {
        lock (telemetryLock)
        {
            return telemetry.Title;
        }
    }

    private ScreenPlaybackState GetTelemetryState()
    {
        lock (telemetryLock)
        {
            return telemetry.State;
        }
    }

    private long GetTelemetryPositionMs()
    {
        lock (telemetryLock)
        {
            return telemetry.PositionMs;
        }
    }

    private static string DescribeYouTubeError(JsonElement root)
    {
        if (root.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind == JsonValueKind.String)
            return messageProperty.GetString() ?? "YouTube player error";

        var code = TryGetInt(root, "code", 0);
        return code switch
        {
            2 => "YouTube player error: invalid video ID or parameter",
            5 => "YouTube player error: HTML5 playback failed",
            100 => "YouTube player error: video unavailable or private",
            101 or 150 => "YouTube player error: embedding is disallowed by the owner",
            153 => "YouTube player error: missing or blocked HTTP Referer",
            -1 => "YouTube player error: failed to load the IFrame API",
            _ => $"YouTube player error: {code}",
        };
    }

    private static float ClampPlaybackRate(float rate)
    {
        if (!float.IsFinite(rate))
            return 1.0f;

        return Math.Clamp(rate, 0.25f, 2.0f);
    }

    private static float ClampVolume(float volume)
    {
        if (!float.IsFinite(volume))
            return 0.0f;

        return Math.Clamp(volume, 0.0f, 1.0f);
    }

    private static float QuantizeVolume(float volume)
    {
        var clamped = ClampVolume(volume);
        return clamped <= 0.0f
            ? 0.0f
            : MathF.Ceiling(clamped * 100.0f) / 100.0f;
    }

    private static string TryGetString(JsonElement root, string propertyName, string fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static double TryGetDouble(JsonElement root, string propertyName, double fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : fallback;
    }

    private static int TryGetInt(JsonElement root, string propertyName, int fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : fallback;
    }
}
