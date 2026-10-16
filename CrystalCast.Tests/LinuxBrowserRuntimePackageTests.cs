using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class LinuxBrowserRuntimePackageTests
{
    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("lib\\escape")]
    [InlineData("lib/C:escape")]
    [InlineData("lib//escape")]
    [InlineData("lib/CON.so")]
    [InlineData("lib/name. ")]
    public void UnsafeArchivePathsAreRejected(string path)
        => Assert.Throws<InvalidDataException>(() => LinuxBrowserRuntimePackage.SafeFilePath(Path.GetTempPath(), path));

    [Fact]
    public void ManifestMustMatchTheHashPinnedInThePluginCatalog()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Manifest());
        var release = new LinuxRuntimeRelease("1.0.0", "unused", Hash(bytes), "unused", Hash([]), 1, []);
        Assert.Equal("1.0.0", LinuxBrowserRuntimePackage.ReadManifest(bytes, release).Version);
        bytes[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => LinuxBrowserRuntimePackage.ReadManifest(bytes, release));
    }

    [Fact]
    public async Task ValidArchivePreservesTheExactHashedFiles()
    {
        var root = NewDestination();
        try
        {
            using var archive = Archive();
            await LinuxBrowserRuntimePackage.ExtractVerifiedAsync(archive, Manifest(), root, null, CancellationToken.None);
            Assert.Equal("launcher", await File.ReadAllTextAsync(Path.Combine(root, "bin", "crystalcast-browser"), TestContext.Current.CancellationToken));
            Assert.Equal(3, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
        }
        finally { Delete(root); }
    }

    [Theory]
    [InlineData("unexpected")]
    [InlineData("duplicate")]
    [InlineData("symlink")]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("traversal")]
    public async Task BadArchivesCannotBeAccepted(string problem)
    {
        var root = NewDestination();
        try
        {
            using var archive = Archive(problem);
            await Assert.ThrowsAsync<InvalidDataException>(() => LinuxBrowserRuntimePackage.ExtractVerifiedAsync(
                archive, Manifest(), root, null, CancellationToken.None));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ManifestRejectsCaseCollisionsAndOversizedFiles()
    {
        var manifest = Manifest();
        var collision = manifest.Files.Append(manifest.Files[0] with { Path = "BIN/crystalcast-browser" }).ToArray();
        Assert.Throws<InvalidDataException>(() => LinuxBrowserRuntimePackage.ValidateManifest(manifest with { Files = collision }, "1.0.0"));
        var huge = manifest.Files.ToArray(); huge[0] = huge[0] with { Size = long.MaxValue };
        Assert.Throws<InvalidDataException>(() => LinuxBrowserRuntimePackage.ValidateManifest(manifest with { Files = huge }, "1.0.0"));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static readonly (string Path, string Text)[] Files =
    [ ("bin/crystalcast-browser", "launcher"), ("libexec/crystalcast-browser-helper", "helper"), ("CRYSTALCAST-LICENSE", "license") ];
    private static LinuxRuntimeManifest Manifest() => new(1, "1.0.0", "x86_64", "2.39", 1, "WPE WebKit 2.54.1", Files[0].Path,
        Files.Select(file => new LinuxRuntimeFile(file.Path, Encoding.UTF8.GetByteCount(file.Text), Hash(Encoding.UTF8.GetBytes(file.Text)), true)).ToArray());

    private static MemoryStream Archive(string? problem = null)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var file in Files)
            {
                if (problem == "missing" && file.Path == Files[2].Path) continue;
                var entry = zip.CreateEntry(file.Path);
                if (problem == "symlink" && file.Path == Files[0].Path) entry.ExternalAttributes = 0xa1ff << 16;
                using var writer = new StreamWriter(entry.Open());
                writer.Write(problem == "changed" && file.Path == Files[0].Path ? "modified" : file.Text);
            }
            var extra = problem switch { "unexpected" => "extra", "duplicate" => Files[0].Path, "traversal" => "../escape", _ => null };
            if (extra != null) { using var writer = new StreamWriter(zip.CreateEntry(extra).Open()); writer.Write("bad"); }
        }
        stream.Position = 0;
        return stream;
    }

    private static string NewDestination() => Path.Combine(Path.GetTempPath(), "CrystalCastPackageTest-" + Guid.NewGuid().ToString("N"));
    private static void Delete(string root) { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
