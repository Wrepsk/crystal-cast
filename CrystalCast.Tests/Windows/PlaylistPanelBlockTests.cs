using CrystalCast.Windows.Panels;

namespace CrystalCast.Tests;

public sealed class PlaylistPanelBlockTests
{
    [Fact]
    public void TheUsersOwnScreensAreOpen()
    {
        var blocks = PlaylistPanel.DescribeBlocks(new BrowserScreenProfile());
        Assert.Null(blocks.Add);
        Assert.Null(blocks.Controls);
        Assert.Null(blocks.Remove);
    }

    [Fact]
    public void ALockOnTheUsersScreenStillLetsThemRemove()
    {
        var blocks = PlaylistPanel.DescribeBlocks(new BrowserScreenProfile { SourceControlsLocked = true, SourceControlsOwnerId = "Sync" });
        Assert.NotNull(blocks.Add);
        Assert.NotNull(blocks.Controls);
        Assert.Null(blocks.Remove);
    }

    [Fact]
    public void APluginsScreenIsReadOnly()
    {
        var blocks = PlaylistPanel.DescribeBlocks(new BrowserScreenProfile { CreatedByIpc = true, IpcOwnerId = "Sync" });
        Assert.Equal("Sync manages this screen's playlist", blocks.Add);
        Assert.Equal(blocks.Add, blocks.Controls);
        Assert.Equal(blocks.Add, blocks.Remove);
    }

    [Fact]
    public void AllowingUserAddsUnblocksOnlyAdding()
    {
        var blocks = PlaylistPanel.DescribeBlocks(new BrowserScreenProfile { CreatedByIpc = true, IpcOwnerId = "Sync", PlaylistAllowUserAdds = true });
        Assert.Null(blocks.Add);
        Assert.NotNull(blocks.Controls);
        Assert.NotNull(blocks.Remove);
    }

    [Fact]
    public void ALockStillBlocksAddingOnAPluginsScreen()
    {
        var screen = new BrowserScreenProfile
        {
            CreatedByIpc = true,
            IpcOwnerId = "Sync",
            PlaylistAllowUserAdds = true,
            SourceControlsLocked = true,
            SourceControlsOwnerId = "Sync",
        };
        Assert.NotNull(PlaylistPanel.DescribeBlocks(screen).Add);
    }
}
