using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class BrowserCaptureFailureDiagnosticsTests
{
    [Fact]
    public void FailureIncludesRootTypeHResultAndMessage()
    {
        var root = new InvalidOperationException("native capture failed");
        var wrapped = new AggregateException("callback", root);

        var status = BrowserCaptureFailureDiagnostics.Format(wrapped);

        Assert.Contains(nameof(InvalidOperationException), status, StringComparison.Ordinal);
        Assert.Contains($"0x{root.HResult:X8}", status, StringComparison.Ordinal);
        Assert.Contains(root.Message, status, StringComparison.Ordinal);
    }
}
