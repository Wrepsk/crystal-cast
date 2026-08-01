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

        var applied = ScreenPatchApplier.TryApplyScreenMutation(screen, request, out var updated, out var error);

        Assert.True(applied, error);
        Assert.Equal("Living Room", updated.Name);
        Assert.Equal("integration", updated.IpcOwnerId);
        Assert.Equal(2.5f, updated.Placement.PositionX);
        Assert.Equal(4.0f, updated.Placement.WidthMeters);
        Assert.Equal("https://youtu.be/dQw4w9WgXcQ", updated.YouTubeUrl);
        Assert.True(updated.YouTubeAudioEnabled);
        Assert.Equal(0.4f, updated.YouTubeVolume);
        Assert.Equal("Browser screen", screen.Name);
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

        var applied = ScreenPatchApplier.TryApplyScreenMutation(screen, request, out _, out var error);

        Assert.False(applied);
        Assert.Contains("Unsupported", error, StringComparison.Ordinal);
        Assert.Equal("Original", screen.Name);
    }

    [Fact]
    public void InvalidProviderUrlDoesNotPartiallyMutateScreen()
    {
        var screen = new BrowserScreenProfile { Name = "Original" };
        var request = new ScreenIpcMutationRequest
        {
            Name = "Changed",
            Provider = BrowserSourceProviderKind.YouTube,
            YouTube = new YouTubeScreenPatchDto { Url = "not a youtube source" },
        };

        Assert.False(ScreenPatchApplier.TryApplyScreenMutation(screen, request, out _, out _));
        Assert.Equal("Original", screen.Name);
    }
}
