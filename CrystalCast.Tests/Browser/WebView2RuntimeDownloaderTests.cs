using CrystalCast.Browser.WebView2;
using Xunit;

namespace CrystalCast.Tests;

public sealed class WebView2RuntimeDownloaderTests
{
    private const string Host = "https:\\u002F\\u002Fmsedge.sf.dl.delivery.mp.microsoft.com\\u002Ffilestreamingservice\\u002Ffiles\\u002F";

    [Fact]
    public void PicksTheNewestX64LinkFromTheDownloadPage()
    {
        var html = string.Concat(
            "{\"x64\",\"", Host, "11111111-2222-3333-4444-555555555555\\u002FMicrosoft.WebView2.FixedVersionRuntime.156.0.4300.10.x64.cab\"}",
            "{\"x64\",\"", Host, "b7b22a57-b321-483e-9855-d260b076f1a7\\u002FMicrosoft.WebView2.FixedVersionRuntime.157.0.4341.42.x64.cab\"}",
            "{\"arm64\",\"", Host, "aaaaaaaa-b321-483e-9855-d260b076f1a7\\u002FMicrosoft.WebView2.FixedVersionRuntime.158.0.1.1.arm64.cab\"}");

        var link = WebView2RuntimeDownloader.FindNewestLink(html);

        Assert.NotNull(link);
        Assert.Equal(new Version(157, 0, 4341, 42), link.Version);
        Assert.EndsWith("Microsoft.WebView2.FixedVersionRuntime.157.0.4341.42.x64.cab", link.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void PageWithoutLinksGivesNothing()
    {
        Assert.Null(WebView2RuntimeDownloader.FindNewestLink("<html>maintenance</html>"));
    }

    [Theory]
    [InlineData("https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/x/Microsoft.WebView2.FixedVersionRuntime.1.2.3.4.x64.cab", true)]
    [InlineData("http://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/x/Microsoft.WebView2.FixedVersionRuntime.1.2.3.4.x64.cab", false)]
    [InlineData("https://msedge.sf.dl.delivery.mp.microsoft.com.evil.example/Microsoft.WebView2.FixedVersionRuntime.1.2.3.4.x64.cab", false)]
    [InlineData("https://msedge.sf.dl.delivery.mp.microsoft.com/files/setup.exe", false)]
    public void OnlyHttpsCabinetsOnMicrosoftsHostAreAllowed(string url, bool allowed)
    {
        Assert.Equal(allowed, WebView2RuntimeDownloader.IsAllowedLink(url));
    }

    [Fact]
    public void FallbackLinkIsAllowed()
    {
        Assert.True(WebView2RuntimeDownloader.IsAllowedLink(WebView2RuntimeDownloader.FallbackLink.Url.AbsoluteUri));
    }

    [Fact]
    public void CabinetPathsDropTheTopFolder()
    {
        var target = Path.Combine(Path.GetTempPath(), "cc-runtime");

        var mapped = WebView2RuntimeDownloader.MapCabinetPath(
            "Microsoft.WebView2.FixedVersionRuntime.157.0.4341.42.x64\\Locales\\en-US.pak",
            target);

        Assert.Equal(Path.Combine(target, "Locales", "en-US.pak"), mapped);
    }

    [Theory]
    [InlineData("top\\..\\..\\outside.dll")]
    [InlineData("top\\sub\\..\\..\\..\\outside.dll")]
    [InlineData("loose-file.dll")]
    public void CabinetPathsCannotLeaveTheTargetFolder(string name)
    {
        Assert.Null(WebView2RuntimeDownloader.MapCabinetPath(name, Path.Combine(Path.GetTempPath(), "cc-runtime")));
    }

    [Theory]
    [InlineData("157.0.4341.42", true)]
    [InlineData("unpacking-157.0.4341.42", false)]
    [InlineData("157.0", false)]
    public void OnlyFullVersionFoldersCount(string name, bool valid)
    {
        Assert.Equal(valid, WebView2FixedRuntime.ParseVersion(name) != null);
    }

    [Fact]
    public void FolderWithoutTheMarkerIsUnfinished()
    {
        var folder = Directory.CreateTempSubdirectory("cc-wv2-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, WebView2FixedRuntime.BrowserExecutableName), string.Empty);
            Assert.False(WebView2FixedRuntime.IsComplete(folder));

            File.WriteAllText(Path.Combine(folder, WebView2FixedRuntime.CompleteMarkerName), string.Empty);
            Assert.True(WebView2FixedRuntime.IsComplete(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\x\WebView2Runtime\.trash", true)]
    [InlineData(@"C:\x\WebView2Runtime\.trash\", true)]
    [InlineData(@"C:\x\WebView2Runtime\157.0.4341.42", false)]
    public void TrashFolderIsRecognized(string folder, bool isTrash)
    {
        Assert.Equal(isTrash, WebView2FixedRuntime.IsTrashFolder(folder));
    }
}
