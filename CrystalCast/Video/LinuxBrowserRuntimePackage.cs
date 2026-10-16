using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrystalCast.Video;

internal sealed record LinuxRuntimeFile(string Path, long Size, string Sha256, bool Executable);
internal sealed record LinuxRuntimeManifest(int Schema, string Version, string Architecture, string MinimumGlibc,
    int Protocol, string Engine, string Entrypoint, LinuxRuntimeFile[] Files);
internal sealed record LinuxRuntimeRelease(string Version, string ManifestUrl, string ManifestSha256,
    string ArchiveUrl, string ArchiveSha256, long ArchiveBytes, string[] TestedDistributions);
internal sealed record LinuxRuntimeCatalog(int Schema, LinuxRuntimeRelease[] Releases)
{
    public static LinuxRuntimeCatalog Load()
    {
        using var resource = typeof(LinuxRuntimeCatalog).Assembly.GetManifestResourceStream("CrystalCast.BrowserAssets.LinuxRuntimeCatalog.json")!;
        var catalog = JsonSerializer.Deserialize<LinuxRuntimeCatalog>(resource, LinuxBrowserRuntimePackage.JsonOptions)!;
        if (catalog.Schema != 1) throw new InvalidDataException("Unsupported Linux browser download catalog.");
        return catalog;
    }
}

internal static class LinuxBrowserRuntimePackage
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 12 };
    public const long MaximumExpandedBytes = 3L * 1024 * 1024 * 1024;
    public const long MaximumArchiveBytes = 1536L * 1024 * 1024;

    public static bool IsHash(string value) => Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    public static bool IsVersion(string value) => Regex.IsMatch(value, "\\A[0-9]+\\.[0-9]+\\.[0-9]+\\z", RegexOptions.CultureInvariant);

    public static string SafeFilePath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 240 || relative.Contains('\\') || relative.Contains(':')
            || relative.StartsWith('/') || relative.Split('/').Any(part => string.IsNullOrEmpty(part) || part is "." or ".."
                || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => c < 32 || "<>\"|?*".Contains(c))
                || Regex.IsMatch(part, "\\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|\\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Unsafe Linux browser package path.");
        var prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Browser package path escapes installation.");
        return path;
    }

    public static void RejectLinks(string path)
    {
        for (var current = System.IO.Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The browser installation path contains a symbolic link/reparse point. Choose a normal user data directory.");
        }
    }

    public static LinuxRuntimeManifest ReadManifest(byte[] bytes, LinuxRuntimeRelease release)
    {
        if (bytes.Length is 0 or > 4 * 1024 * 1024 || !IsHash(release.ManifestSha256)
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(release.ManifestSha256)))
            throw new InvalidDataException("Linux browser manifest does not match the trusted catalog hash.");
        var manifest = JsonSerializer.Deserialize<LinuxRuntimeManifest>(bytes, JsonOptions) ?? throw new InvalidDataException("Empty browser manifest.");
        ValidateManifest(manifest, release.Version);
        return manifest;
    }

    public static void ValidateManifest(LinuxRuntimeManifest manifest, string expectedVersion)
    {
        if (manifest.Schema != 1 || manifest.Protocol != 1 || !IsVersion(manifest.Version) || manifest.Version != expectedVersion
            || manifest.Architecture != "x86_64" || manifest.MinimumGlibc != "2.39"
            || manifest.Entrypoint != "bin/crystalcast-browser" || string.IsNullOrEmpty(manifest.Engine)
            || manifest.Files == null || manifest.Files.Length is < 3 or > 12000)
            throw new InvalidDataException("Incompatible Linux browser runtime manifest.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            SafeFilePath(System.IO.Path.GetTempPath(), file.Path);
            if (!paths.Add(file.Path) || !IsHash(file.Sha256) || file.Size < 0 || file.Size > 1024L * 1024 * 1024)
                throw new InvalidDataException("Invalid or duplicate Linux browser package file.");
            total = checked(total + file.Size);
            if (total > MaximumExpandedBytes) throw new InvalidDataException("Linux browser runtime is too large.");
        }
        foreach (var required in new[] { manifest.Entrypoint, "libexec/crystalcast-browser-helper", "CRYSTALCAST-LICENSE" })
            if (!paths.Contains(required)) throw new InvalidDataException("Linux browser runtime is missing a required file.");
        if (!manifest.Files.Any(file => file.Path == manifest.Entrypoint && file.Executable))
            throw new InvalidDataException("Browser launcher is not executable in its manifest.");
    }

    public static async Task ExtractVerifiedAsync(Stream archiveStream, LinuxRuntimeManifest manifest, string destination,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ValidateManifest(manifest, manifest.Version);
        if (Directory.Exists(destination)) throw new IOException("Browser staging directory already exists.");
        RejectLinks(System.IO.Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(destination);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = SafeFilePath(destination, entry.FullName);
            if (!expected.TryGetValue(entry.FullName, out var file) || !found.Add(entry.FullName) || entry.Length != file.Size
                || ((entry.ExternalAttributes >> 16) & 0xf000) is not (0 or 0x8000))
                throw new InvalidDataException("Unexpected, duplicated or non-regular file in Linux browser archive.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            RejectLinks(path);
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long copied = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                copied += count;
                if (copied > file.Size) throw new InvalidDataException("Expanded browser file exceeds its declared length.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            if (copied != file.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(file.Sha256)))
                throw new InvalidDataException("Linux browser file failed SHA-256 verification: " + file.Path);
            progress?.Report((double)found.Count / expected.Count);
        }
        if (found.Count != expected.Count) throw new InvalidDataException("Linux browser archive is incomplete.");
    }
}
