using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Plugin.Services;

namespace CrystalCast.Video;

internal static class CefRuntimeManager
{
    private static readonly object Gate = new();
    private static bool initialized;
    private static bool failed;
    private static string status = "CEF not initialized";
    private static Dictionary<string, Assembly>? cefAssemblies;
    // Avast's hook library crashed in CEF's Windows capability access path in crash dumps.
    private static readonly string[] UnsafeHookModuleNames =
    [
        "aswhook.dll",
    ];

    public static string Status
    {
        get
        {
            lock (Gate)
            {
                return status;
            }
        }
    }

    public static bool TryInitialize(IPluginLog log, string? pluginDirectory, out string currentStatus)
    {
        lock (Gate)
        {
            if (initialized)
            {
                status = "CEF initialized";
                currentStatus = status;
                return true;
            }

            if (failed)
            {
                currentStatus = status;
                return false;
            }

            try
            {
                if (TryDescribeUnsafeHostHook(log, out var unsafeHookStatus))
                {
                    failed = true;
                    status = unsafeHookStatus;
                    currentStatus = status;
                    log.Warning("Skipping CEF initialization for CrystalCast: {Status}", status);
                    return false;
                }

                var paths = ResolveCefRuntimePaths(pluginDirectory);
                ValidateCefFiles(paths);
                var assemblies = PreloadCefSharpAssemblies(paths.ManagedDir, paths.CefDir);
                cefAssemblies = assemblies;

                var cefType = GetRequiredType(assemblies, "CefSharp.Core", "CefSharp.Cef");
                if (GetNullableBoolProperty(cefType, "IsInitialized") == true)
                {
                    initialized = true;
                    status = "CEF initialized";
                    currentStatus = status;
                    return true;
                }

                var cacheRoot = BrowserProfileManager.GetCefRoot();
                Directory.CreateDirectory(cacheRoot);

                var cefSharpSettingsType = GetRequiredType(assemblies, "CefSharp", "CefSharp.CefSharpSettings");
                SetStaticProperty(cefSharpSettingsType, "ShutdownOnExit", false);
                SetStaticProperty(cefSharpSettingsType, "SubprocessExitIfParentProcessClosed", true);

                var cefSettingsType = GetRequiredType(assemblies, "CefSharp.OffScreen", "CefSharp.OffScreen.CefSettings");
                var settings = Activator.CreateInstance(cefSettingsType)
                    ?? throw new InvalidOperationException("Failed to create CEF settings.");
                SetInstanceProperty(settings, "BrowserSubprocessPath", paths.SubprocessPath);
                SetInstanceProperty(settings, "ResourcesDirPath", paths.CefDir);
                SetInstanceProperty(settings, "LocalesDirPath", paths.LocalesPath);
                SetInstanceProperty(settings, "RootCachePath", cacheRoot);
                SetInstanceProperty(settings, "CachePath", Path.Combine(cacheRoot, "Cache"));
                SetInstanceProperty(settings, "LogFile", Path.Combine(cacheRoot, "debug.log"));
                SetInstanceProperty(settings, "WindowlessRenderingEnabled", true);
                SetInstanceProperty(settings, "MultiThreadedMessageLoop", true);
                SetInstanceProperty(settings, "BackgroundColor", 0xFF000000u);
                AddCefCommandLineArg(settings, "autoplay-policy", "no-user-gesture-required");
                cefSettingsType.GetMethod("EnableAudio", BindingFlags.Instance | BindingFlags.Public)?.Invoke(settings, null);

                initialized = InvokeCefInitialize(cefType, settings);
                status = initialized ? $"CEF initialized from {paths.CefDir}" : "CEF initialization returned false";
                cefAssemblies = assemblies;
                failed = !initialized;
                currentStatus = status;
                return initialized;
            }
            catch (Exception ex)
            {
                var cause = ex.GetBaseException();
                failed = true;
                status = $"CEF init failed: {cause.Message}";
                currentStatus = status;
                log.Warning(ex, "Failed to initialize CEF for CrystalCast.");
                return false;
            }
        }
    }

    private static bool TryDescribeUnsafeHostHook(IPluginLog log, out string unsafeHookStatus)
    {
        unsafeHookStatus = string.Empty;

        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                var moduleName = module.ModuleName;
                if (!UnsafeHookModuleNames.Contains(moduleName, StringComparer.OrdinalIgnoreCase))
                    continue;

                unsafeHookStatus = $"CEF disabled: {moduleName} is loaded in the game process and has crashed CEF offscreen; use WebView2 capture";
                return true;
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not inspect process modules before CEF initialization.");
        }

        return false;
    }

    private static CefRuntimePaths ResolveCefRuntimePaths(string? pluginDirectory)
    {
        var searched = new List<string>();
        foreach (var root in GetRuntimeSearchRoots(pluginDirectory))
        {
            foreach (var cefDir in GetCefCandidateDirectories(root))
            {
                if (string.IsNullOrWhiteSpace(cefDir) || searched.Contains(cefDir, StringComparer.OrdinalIgnoreCase))
                    continue;

                searched.Add(cefDir);
                if (!File.Exists(Path.Combine(cefDir, "libcef.dll")))
                    continue;

                var managedDir = ResolveManagedDirectory(root, cefDir);
                var subprocessPath = ResolveExistingFile(
                    "CefSharp.BrowserSubprocess.exe",
                    cefDir,
                    Path.Combine(root, "runtimes", "win-x64", "native"),
                    root,
                    managedDir);
                var localesPath = ResolveLocalesDirectory(cefDir, root, managedDir);
                return new CefRuntimePaths(root, managedDir, cefDir, subprocessPath, localesPath, searched);
            }
        }

        throw new FileNotFoundException($"Missing CEF runtime file: libcef.dll. Searched: {string.Join("; ", searched)}");
    }

    private static IEnumerable<string> GetRuntimeSearchRoots(string? pluginDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in GetRuntimeSearchRootCandidates(pluginDirectory))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(directory);
            }
            catch
            {
                continue;
            }

            if (Directory.Exists(fullPath) && seen.Add(fullPath))
                yield return fullPath;
        }
    }

    public static bool TryGetLoadedType(
        string assemblyName,
        string typeName,
        IPluginLog log,
        string? pluginDirectory,
        out Type? type,
        out string currentStatus)
    {
        lock (Gate)
        {
            type = null;
            if (!TryInitialize(log, pluginDirectory, out currentStatus))
                return false;

            try
            {
                var assemblies = cefAssemblies ?? GetLoadedCefAssemblies();
                type = GetRequiredType(assemblies, assemblyName, typeName);
                currentStatus = status;
                return true;
            }
            catch (Exception ex)
            {
                var cause = ex.GetBaseException();
                currentStatus = $"CEF type lookup failed: {cause.Message}";
                return false;
            }
        }
    }

    private static IEnumerable<string?> GetRuntimeSearchRootCandidates(string? pluginDirectory)
    {
        yield return pluginDirectory;

        var assemblyLocation = typeof(CefRuntimeManager).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
            yield return Path.GetDirectoryName(assemblyLocation);

        yield return AppContext.BaseDirectory;
        yield return AppDomain.CurrentDomain.BaseDirectory;
        yield return Environment.CurrentDirectory;
    }

    private static IEnumerable<string> GetCefCandidateDirectories(string root)
    {
        yield return root;
        yield return Path.Combine(root, "runtimes", "win-x64", "native");
    }

    private static string ResolveManagedDirectory(string root, string cefDir)
    {
        foreach (var directory in new[]
        {
            root,
            cefDir,
            Path.Combine(root, "runtimes", "win-x64", "lib", "net6.0"),
        })
        {
            if (File.Exists(Path.Combine(directory, "CefSharp.OffScreen.dll")))
                return directory;
        }

        return root;
    }

    private static string ResolveExistingFile(string fileName, params string[] directories)
    {
        foreach (var directory in directories)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                return path;
        }

        return Path.Combine(directories.FirstOrDefault() ?? string.Empty, fileName);
    }

    private static string ResolveLocalesDirectory(string cefDir, string root, string managedDir)
    {
        foreach (var directory in new[]
        {
            Path.Combine(cefDir, "locales"),
            Path.Combine(root, "locales"),
            Path.Combine(managedDir, "locales"),
        })
        {
            if (Directory.Exists(directory))
                return directory;
        }

        return Path.Combine(cefDir, "locales");
    }

    private static Dictionary<string, Assembly> PreloadCefSharpAssemblies(string managedDir, string cefDir)
    {
        var loadContext = AssemblyLoadContext.Default;
        var assemblyPaths = new[]
        {
            Path.Combine(managedDir, "CefSharp.Core.Runtime.dll"),
            Path.Combine(managedDir, "CefSharp.Core.dll"),
            Path.Combine(managedDir, "CefSharp.dll"),
            Path.Combine(managedDir, "CefSharp.OffScreen.dll"),
            Path.Combine(cefDir, "CefSharp.Core.Runtime.dll"),
            Path.Combine(cefDir, "CefSharp.Core.dll"),
            Path.Combine(cefDir, "CefSharp.dll"),
        };
        var assemblies = GetLoadedCefAssemblies();

        foreach (var assemblyPath in assemblyPaths)
        {
            if (!File.Exists(assemblyPath))
                continue;

            var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
            if (assemblyName.Name == null)
                continue;

            if (assemblies.TryGetValue(assemblyName.Name, out var existingAssembly))
            {
                if (string.IsNullOrEmpty(existingAssembly.Location))
                {
                    throw new InvalidOperationException(
                        $"{assemblyName.Name} was already loaded without a disk path. Restart the game so CrystalCast can load CEF from {assemblyPath}.");
                }

                if (AssemblyLoadContext.GetLoadContext(existingAssembly)?.IsCollectible == true)
                {
                    throw new InvalidOperationException(
                        $"{assemblyName.Name} was already loaded in Dalamud's collectible plugin context. Restart the game so CrystalCast can load CEF into the default context from {assemblyPath}.");
                }

                continue;
            }

            assemblies[assemblyName.Name] = loadContext.LoadFromAssemblyPath(assemblyPath);
        }

        return assemblies;
    }

    private static Dictionary<string, Assembly> GetLoadedCefAssemblies()
    {
        var assemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (name?.StartsWith("CefSharp", StringComparison.OrdinalIgnoreCase) != true)
                continue;

            if (!assemblies.TryGetValue(name, out var existingAssembly))
            {
                assemblies[name] = assembly;
                continue;
            }

            if (IsPreferredAssembly(assembly, existingAssembly))
                assemblies[name] = assembly;
        }

        return assemblies;
    }

    private static bool IsPreferredAssembly(Assembly candidate, Assembly existing)
    {
        var candidateContext = AssemblyLoadContext.GetLoadContext(candidate);
        var existingContext = AssemblyLoadContext.GetLoadContext(existing);
        var candidateIsDefault = candidateContext == AssemblyLoadContext.Default;
        var existingIsDefault = existingContext == AssemblyLoadContext.Default;
        if (candidateIsDefault != existingIsDefault)
            return candidateIsDefault;

        var candidateIsCollectible = candidateContext?.IsCollectible == true;
        var existingIsCollectible = existingContext?.IsCollectible == true;
        if (candidateIsCollectible != existingIsCollectible)
            return !candidateIsCollectible;

        var candidateHasLocation = !string.IsNullOrEmpty(candidate.Location);
        var existingHasLocation = !string.IsNullOrEmpty(existing.Location);
        return candidateHasLocation && !existingHasLocation;
    }

    private static Type GetRequiredType(IReadOnlyDictionary<string, Assembly> assemblies, string assemblyName, string typeName)
    {
        if (!assemblies.TryGetValue(assemblyName, out var assembly))
            throw new FileNotFoundException($"Missing CEF assembly: {assemblyName}");

        return assembly.GetType(typeName, throwOnError: true)
            ?? throw new TypeLoadException($"Missing CEF type: {typeName}");
    }

    private static bool? GetNullableBoolProperty(Type type, string propertyName)
    {
        return type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) switch
        {
            bool value => value,
            null => null,
            var value => (bool?)value,
        };
    }

    private static void SetStaticProperty(Type type, string propertyName, object value)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
        property?.SetValue(null, ConvertValue(value, property.PropertyType));
    }

    private static void SetInstanceProperty(object instance, string propertyName, object value)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        property?.SetValue(instance, ConvertValue(value, property.PropertyType));
    }

    private static void AddCefCommandLineArg(object settings, string key, string value)
    {
        var property = settings.GetType().GetProperty("CefCommandLineArgs", BindingFlags.Public | BindingFlags.Instance);
        if (property?.GetValue(settings) is not System.Collections.IDictionary args)
            return;

        args[key] = value;
    }

    private static object ConvertValue(object value, Type targetType)
    {
        var realTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return realTargetType.IsEnum
            ? Enum.ToObject(realTargetType, value)
            : Convert.ChangeType(value, realTargetType);
    }

    private static bool InvokeCefInitialize(Type cefType, object settings)
    {
        var initializeMethod = cefType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Initialize" || method.ReturnType != typeof(bool))
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 3
                    && parameters[1].ParameterType == typeof(bool)
                    && parameters[2].ParameterType.FullName == "CefSharp.IBrowserProcessHandler";
            })
            ?? throw new MissingMethodException("CefSharp.Cef", "Initialize");

        return (bool)initializeMethod.Invoke(null, new[] { settings, false, null })!;
    }

    private static void ValidateCefFiles(CefRuntimePaths paths)
    {
        var requiredFiles = new[]
        {
            "libcef.dll",
            "chrome_elf.dll",
            "icudtl.dat",
            "v8_context_snapshot.bin",
            "resources.pak",
            "chrome_100_percent.pak",
            "chrome_200_percent.pak",
        };

        foreach (var file in requiredFiles)
        {
            var path = Path.Combine(paths.CefDir, file);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Missing CEF runtime file: {path}", path);
        }

        if (!File.Exists(paths.SubprocessPath))
            throw new FileNotFoundException($"Missing CEF subprocess: {paths.SubprocessPath}", paths.SubprocessPath);

        if (!Directory.Exists(paths.LocalesPath) || !File.Exists(Path.Combine(paths.LocalesPath, "en-US.pak")))
            throw new DirectoryNotFoundException($"Missing CEF locales directory: {paths.LocalesPath}");
    }

    private sealed record CefRuntimePaths(
        string RootDir,
        string ManagedDir,
        string CefDir,
        string SubprocessPath,
        string LocalesPath,
        IReadOnlyList<string> SearchedDirectories);
}
