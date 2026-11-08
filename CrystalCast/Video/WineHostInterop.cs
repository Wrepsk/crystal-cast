using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CrystalCast.Video;

/// <summary>
/// Wine-only bridges to the Linux host: path conversion and starting a native Linux program.
/// Wine runs a non-Windows executable passed to CreateProcess directly on the host. It returns
/// no process handle for it, so the native program's lifetime is tied to its plugin connection.
/// </summary>
internal static class WineHostInterop
{
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private static readonly Lazy<string?> HostSystem = new(QueryHostSystem);

    public static bool IsLinuxHost => HostSystem.Value?.StartsWith("Linux", StringComparison.Ordinal) == true;

    public static string HostSystemName => HostSystem.Value ?? "unknown";

    /// <summary>True when Wine's Z: drive shows the Linux root folder.</summary>
    public static bool HasRootDrive => SafeExists(@"Z:\bin\sh") || SafeExists(@"Z:\usr\bin\sh");

    public static string? ToUnixPath(string windowsPath)
    {
        try
        {
            if (TryGetKernel32Export("wine_get_unix_file_name", out var export))
                return CallToUnix(export, windowsPath);
        }
        catch
        {
            // Fall back to the Z: drive below.
        }

        return RootDrivePathToUnix(windowsPath);
    }

    public static string? ToWindowsPath(string unixPath)
    {
        try
        {
            if (TryGetKernel32Export("wine_get_dos_file_name", out var export))
                return CallToWindows(export, unixPath);
        }
        catch
        {
            // Fall back to the Z: drive below.
        }

        return unixPath.StartsWith('/') && HasRootDrive ? "Z:" + unixPath.Replace('/', '\\') : null;
    }

    /// <summary>"Z:\home\me\x" -> "/home/me/x" when Z: is the Linux root folder.</summary>
    internal static string? RootDrivePathToUnix(string windowsPath)
    {
        if (windowsPath.Length < 3
            || char.ToUpperInvariant(windowsPath[0]) != 'Z'
            || windowsPath[1] != ':'
            || windowsPath[2] != '\\')
        {
            return null;
        }

        return windowsPath[2..].Replace('\\', '/');
    }

    private static string? CallToWindows(nint export, string unixPath)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(unixPath);
        try
        {
            nint result;
            unsafe
            {
                result = ((delegate* unmanaged<nint, nint>)export)(utf8);
            }

            if (result == 0)
                return null;

            try
            {
                return Marshal.PtrToStringUni(result);
            }
            finally
            {
                HeapFree(GetProcessHeap(), 0, result);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>Starts a Linux program. Arguments are passed through Windows command-line quoting.</summary>
    public static void StartUnixProcess(
        string unixExecutable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> extraEnvironment)
    {
        var application = ToWindowsPath(unixExecutable)
            ?? throw new InvalidOperationException($"Wine cannot map {unixExecutable} to a Windows path.");
        var commandLine = BuildCommandLine(Path.GetFileName(unixExecutable), arguments);
        var environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(extraEnvironment));
        try
        {
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessW(
                    application,
                    new StringBuilder(commandLine),
                    0,
                    0,
                    false,
                    CreateUnicodeEnvironment,
                    environment,
                    null,
                    ref startup,
                    out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Wine could not start {unixExecutable}");
            }

            if (info.hProcess != 0)
                CloseHandle(info.hProcess);
            if (info.hThread != 0)
                CloseHandle(info.hThread);
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
    }

    internal static string BuildCommandLine(string program, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        AppendArgument(builder, program);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> extraEnvironment)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value && key.Length > 0 && !key.Contains('='))
                variables[key] = value;
        }

        foreach (var (key, value) in extraEnvironment)
            variables[key] = value;

        var block = new StringBuilder();
        foreach (var (key, value) in variables)
            block.Append(key).Append('=').Append(value).Append('\0');
        block.Append('\0');
        return block.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Contains('"') || argument.Contains('\0'))
            throw new ArgumentException("Native helper arguments cannot contain quotes.", nameof(argument));

        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t']) < 0)
        {
            builder.Append(argument);
            return;
        }

        // A trailing backslash would escape the closing quote.
        builder.Append('"').Append(argument);
        var trailing = argument.Length - argument.TrimEnd('\\').Length;
        builder.Append('\\', trailing).Append('"');
    }

    private static unsafe string? CallToUnix(nint export, string windowsPath)
    {
        nint result;
        fixed (char* path = windowsPath)
        {
            result = ((delegate* unmanaged<char*, nint>)export)(path);
        }

        if (result == 0)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(result);
        }
        finally
        {
            HeapFree(GetProcessHeap(), 0, result);
        }
    }

    private static string? QueryHostSystem()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var ntdll = GetModuleHandleW("ntdll.dll");
            var export = ntdll == 0 || IgnoreWineExports ? 0 : GetProcAddress(ntdll, "wine_get_host_version");
            if (export != 0)
            {
                unsafe
                {
                    nint sysname = 0;
                    nint release = 0;
                    ((delegate* unmanaged<nint*, nint*, void>)export)(&sysname, &release);
                    if (sysname != 0)
                        return Marshal.PtrToStringUTF8(sysname);
                }
            }
        }
        catch
        {
            // Hidden or unusable export: infer below.
        }

        // Proton can hide Wine's exports. Linux has /proc; macOS (CrossOver) does not.
        return SafeExists(@"Z:\proc\self\status") || SafeExists(@"Z:\proc\version")
            ? "Linux (inferred from Wine's Z: drive)"
            : null;
    }

    /// <summary>Troubleshooting and tests: behave as if Wine hid its exports (some Proton builds).</summary>
    internal static bool IgnoreWineExports =>
        Environment.GetEnvironmentVariable("CRYSTALCAST_IGNORE_WINE_EXPORTS") == "1";

    private static bool TryGetKernel32Export(string name, out nint export)
    {
        export = 0;
        if (!OperatingSystem.IsWindows() || IgnoreWineExports)
            return false;

        var kernel32 = GetModuleHandleW("kernel32.dll");
        export = kernel32 == 0 ? 0 : GetProcAddress(kernel32, name);
        return export != 0;
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern nint GetProcAddress(nint module, string procName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string lpApplicationName,
        StringBuilder lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nint GetProcessHeap();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapFree(nint heap, uint flags, nint memory);
}
