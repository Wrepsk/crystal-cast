using CrystalCast.Sync;

namespace CrystalCast.Tests;

public sealed class GenericWebIpcApprovalServiceTests
{
    [Fact]
    public void IpcGenericWebRequiresApprovalBeforeFirstLoad()
    {
        using var approvals = new GenericWebIpcApprovalService();
        var screen = CreateIpcScreen("screen-a", "https://example.com/video");

        Assert.Equal(GenericWebIpcAccess.Pending, approvals.EvaluateInitial(screen));
        Assert.True(approvals.TryGetCurrent(out var request));
        Assert.Equal("https://example.com", request.Origin);
        Assert.Equal("https://example.com/video", request.Url);
    }

    [Fact]
    public void OneTimeApprovalIsScopedToOneScreenAndOrigin()
    {
        using var approvals = new GenericWebIpcApprovalService();
        var first = CreateIpcScreen("screen-a", "https://example.com/video");
        var second = CreateIpcScreen("screen-b", "https://example.com/other");

        approvals.EvaluateInitial(first);
        Assert.True(approvals.TryGetCurrent(out var request));
        Assert.True(approvals.Approve(request.RequestId, trustForSession: false));

        Assert.Equal(GenericWebIpcAccess.Allowed, approvals.EvaluateInitial(first));
        Assert.Equal(GenericWebIpcAccess.Pending, approvals.EvaluateInitial(second));
        Assert.True(approvals.IsNavigationAllowed(first, "https://example.com/redirected"));
    }

    [Fact]
    public void SessionTrustAllowsSameExactOriginOnOtherScreens()
    {
        using var approvals = new GenericWebIpcApprovalService();
        var first = CreateIpcScreen("screen-a", "https://example.com/video");
        var second = CreateIpcScreen("screen-b", "https://example.com/other");

        approvals.EvaluateInitial(first);
        Assert.True(approvals.TryGetCurrent(out var request));
        Assert.True(approvals.Approve(request.RequestId, trustForSession: true));

        Assert.Equal(GenericWebIpcAccess.Allowed, approvals.EvaluateInitial(second));
        Assert.False(approvals.TryGetCurrent(out _));
    }

    [Fact]
    public void OriginMatchingNormalizesHostCasingAndDefaultPort()
    {
        using var approvals = new GenericWebIpcApprovalService();
        var first = CreateIpcScreen("screen-a", "https://EXAMPLE.com:443/video");
        approvals.EvaluateInitial(first);
        approvals.TryGetCurrent(out var request);
        approvals.Approve(request.RequestId, trustForSession: true);

        Assert.Equal(
            GenericWebIpcAccess.Allowed,
            approvals.EvaluateInitial(CreateIpcScreen("screen-b", "https://example.com/other")));
    }

    [Theory]
    [InlineData("http://example.com/video")]
    [InlineData("https://example.com:8443/video")]
    [InlineData("https://sub.example.com/video")]
    public void SessionTrustDoesNotCoverDifferentOrigins(string candidate)
    {
        using var approvals = new GenericWebIpcApprovalService();
        var trusted = CreateIpcScreen("screen-a", "https://example.com/video");
        approvals.EvaluateInitial(trusted);
        approvals.TryGetCurrent(out var request);
        approvals.Approve(request.RequestId, trustForSession: true);

        Assert.Equal(
            GenericWebIpcAccess.Pending,
            approvals.EvaluateInitial(CreateIpcScreen("screen-b", candidate)));
    }

    [Fact]
    public void RejectionDoesNotPromptRepeatedlyForSameScreenAndOrigin()
    {
        using var approvals = new GenericWebIpcApprovalService();
        var screen = CreateIpcScreen("screen-a", "https://example.com/video");
        approvals.EvaluateInitial(screen);
        approvals.TryGetCurrent(out var request);

        Assert.True(approvals.Reject(request.RequestId));
        Assert.Equal(GenericWebIpcAccess.Denied, approvals.EvaluateInitial(screen));
        Assert.False(approvals.TryGetCurrent(out _));
    }

    [Fact]
    public void SessionDomainBlockSuppressesLoadsAndPromptsAcrossScreens()
    {
        using var approvals = new GenericWebIpcApprovalService();
        var first = CreateIpcScreen("screen-a", "https://blocked.example/video");
        var second = CreateIpcScreen("screen-b", "https://blocked.example/other");
        approvals.EvaluateInitial(first);
        approvals.EvaluateInitial(second);
        approvals.TryGetCurrent(out var request);

        Assert.True(approvals.Reject(request.RequestId, blockOriginForSession: true));
        Assert.Equal(GenericWebIpcAccess.Denied, approvals.EvaluateInitial(first));
        Assert.Equal(GenericWebIpcAccess.Denied, approvals.EvaluateInitial(second));
        Assert.False(approvals.TryGetCurrent(out _));
    }

    [Fact]
    public void SessionDomainBlockDoesNotCoverSubdomains()
    {
        using var approvals = new GenericWebIpcApprovalService();
        var blocked = CreateIpcScreen("screen-a", "https://example.com/video");
        approvals.EvaluateInitial(blocked);
        approvals.TryGetCurrent(out var request);
        approvals.Reject(request.RequestId, blockOriginForSession: true);

        Assert.Equal(
            GenericWebIpcAccess.Pending,
            approvals.EvaluateInitial(CreateIpcScreen("screen-b", "https://sub.example.com/video")));
    }

    [Fact]
    public void ResetSessionDecisionsRequiresTrustedAndBlockedDomainsToAskAgain()
    {
        using var approvals = new GenericWebIpcApprovalService();
        var trusted = CreateIpcScreen("screen-a", "https://trusted.example/video");
        var blocked = CreateIpcScreen("screen-b", "https://blocked.example/video");
        approvals.EvaluateInitial(trusted);
        approvals.TryGetCurrent(out var trustRequest);
        approvals.Approve(trustRequest.RequestId, trustForSession: true);
        approvals.EvaluateInitial(blocked);
        approvals.TryGetCurrent(out var blockRequest);
        approvals.Reject(blockRequest.RequestId, blockOriginForSession: true);

        approvals.ResetSessionDecisions();

        Assert.Equal(GenericWebIpcAccess.Pending, approvals.EvaluateInitial(trusted));
        Assert.Equal(GenericWebIpcAccess.Pending, approvals.EvaluateInitial(blocked));
    }

    [Fact]
    public void CrossOriginNavigationRequiresAnotherApproval()
    {
        using var approvals = new GenericWebIpcApprovalService();
        var screen = CreateIpcScreen("screen-a", "https://example.com/video");
        approvals.EvaluateInitial(screen);
        approvals.TryGetCurrent(out var initial);
        approvals.Approve(initial.RequestId, trustForSession: false);

        Assert.False(approvals.IsNavigationAllowed(screen, "https://redirect.example.net/watch"));
        Assert.True(approvals.TryGetCurrent(out var redirect));
        Assert.True(redirect.IsRedirect);
        Assert.Equal("https://redirect.example.net", redirect.Origin);

        approvals.Approve(redirect.RequestId, trustForSession: false);
        Assert.True(approvals.IsNavigationAllowed(screen, "https://redirect.example.net/watch"));
        Assert.Equal("https://redirect.example.net/watch", approvals.GetApprovedStartUrl(screen));
    }

    [Fact]
    public void ChangingConfiguredUrlRequiresFreshOneTimeApproval()
    {
        using var approvals = new GenericWebIpcApprovalService();
        var screen = CreateIpcScreen("screen-a", "https://example.com/first");
        approvals.EvaluateInitial(screen);
        approvals.TryGetCurrent(out var initial);
        approvals.Approve(initial.RequestId, trustForSession: false);

        screen.GenericWebUrl = "https://example.com/second";

        Assert.Equal(GenericWebIpcAccess.Pending, approvals.EvaluateInitial(screen));
        Assert.True(approvals.TryGetCurrent(out var next));
        Assert.Equal("https://example.com/second", next.Url);
    }

    [Fact]
    public void LocalGenericWebScreensAreUnaffected()
    {
        using var approvals = new GenericWebIpcApprovalService();
        var screen = CreateIpcScreen("screen-a", "https://example.com/video");
        screen.CreatedByIpc = false;

        Assert.Equal(GenericWebIpcAccess.Allowed, approvals.EvaluateInitial(screen));
        Assert.True(approvals.IsNavigationAllowed(screen, "https://another.example/video"));
        Assert.False(approvals.TryGetCurrent(out _));
    }

    private static BrowserScreenProfile CreateIpcScreen(string screenId, string url)
    {
        return new BrowserScreenProfile
        {
            ScreenId = screenId,
            Name = "IPC movie night",
            CreatedByIpc = true,
            IpcOwnerId = "ExamplePlugin",
            ProviderKind = BrowserSourceProviderKind.GenericWeb,
            GenericWebUrl = url,
        };
    }
}
