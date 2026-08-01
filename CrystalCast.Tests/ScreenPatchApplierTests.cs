using CrystalCast.Sync;

namespace CrystalCast.Tests;

public sealed class ScreenPatchApplierTests
{
    [Fact]
    public void AppliesValidBrowserAndPlacementPatch()
    {
        var screen = new BrowserScreenProfile();
        var request = new ScreenIpcMutationRequest
        {
            Name = "  Living Room  ",
            OwnerId = " integration ",
            Provider = BrowserSourceProviderKind.YouTube,
            Placement = new ScreenPlacementPatchDto
            {
                PositionX = 2.5f,
                WidthMeters = 4.0f,
            },
            YouTube = new YouTubeScreenPatchDto
            {
                Url = "https://youtu.be/dQw4w9WgXcQ",
                AudioEnabled = true,
                Volume = 0.4f,
            },
        };

        var applied = ScreenPatchApplier.ApplyScreenMutation(screen, request, out var error);

        Assert.True(applied, error);
        Assert.Equal("Living Room", screen.Name);
        Assert.Equal("integration", screen.IpcOwnerId);
        Assert.Equal(2.5f, screen.Placement.PositionX);
        Assert.Equal(4.0f, screen.Placement.WidthMeters);
        Assert.Equal("https://youtu.be/dQw4w9WgXcQ", screen.YouTubeUrl);
        Assert.True(screen.YouTubeAudioEnabled);
        Assert.Equal(0.4f, screen.YouTubeVolume);
    }

    [Fact]
    public void UnsupportedProviderIsRejectedBeforeMutation()
    {
        var screen = new BrowserScreenProfile { Name = "Original" };
        var request = new ScreenIpcMutationRequest
        {
            Name = "Changed",
            Provider = (BrowserSourceProviderKind)999,
        };

        var applied = ScreenPatchApplier.ApplyScreenMutation(screen, request, out var error);

        Assert.False(applied);
        Assert.Contains("Unsupported", error, StringComparison.Ordinal);
        Assert.Equal("Original", screen.Name);
    }

    [Fact(Skip = "Phase 3 will make screen mutations transactional before enabling this regression assertion.")]
    public void InvalidProviderUrlDoesNotPartiallyMutateScreen()
    {
        var screen = new BrowserScreenProfile { Name = "Original" };
        var request = new ScreenIpcMutationRequest
        {
            Name = "Changed",
            Provider = BrowserSourceProviderKind.YouTube,
            YouTube = new YouTubeScreenPatchDto { Url = "not a youtube source" },
        };

        Assert.False(ScreenPatchApplier.ApplyScreenMutation(screen, request, out _));
        Assert.Equal("Original", screen.Name);
    }
}
