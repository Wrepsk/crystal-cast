using CrystalCast.Sync;

namespace CrystalCast.Tests;

public sealed class ScreenChangePublisherTests
{
    [Fact]
    public void UnchangedScreenIsNotPublishedTwice()
    {
        var configuration = new Configuration();
        var screen = Assert.IsType<BrowserScreenProfile>(configuration.GetActiveBrowserScreen());
        var messages = new List<string>();
        var publisher = new ScreenChangePublisher(configuration, "runtime-session", messages.Add);
        var state = new ScreenStateEnvelope
        {
            ScreenId = screen.ScreenId,
            Source = new ScreenSourceState { Kind = ScreenSourceKind.Browser },
        };

        Assert.True(publisher.MaybeSendScreenChanged(state, null, null));
        Assert.False(publisher.MaybeSendScreenChanged(state, null, null));
        Assert.Single(messages);
    }

    [Fact]
    public void ForcedChangePublishesAnUnchangedScreen()
    {
        var configuration = new Configuration();
        var screen = Assert.IsType<BrowserScreenProfile>(configuration.GetActiveBrowserScreen());
        var messages = new List<string>();
        var publisher = new ScreenChangePublisher(configuration, "runtime-session", messages.Add);
        var state = new ScreenStateEnvelope
        {
            ScreenId = screen.ScreenId,
            Source = new ScreenSourceState { Kind = ScreenSourceKind.Browser },
        };
        publisher.MaybeSendScreenChanged(state, null, null);

        Assert.True(publisher.MaybeSendScreenChanged(state, screen.ScreenId, [ScreenIpcChangeKind.Playback]));
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void UnavailableEventPreservesOwnershipAndHasNoState()
    {
        var configuration = new Configuration();
        var messages = new List<string>();
        var publisher = new ScreenChangePublisher(configuration, "runtime-session", messages.Add);
        var screen = new BrowserScreenProfile
        {
            ScreenId = "screen",
            CreatedByIpc = true,
            IpcOwnerId = "ipc-owner",
            SourceControlsLocked = true,
            SourceControlsOwnerId = "lock-owner",
        };

        publisher.SendUnavailableAndForget(screen.ScreenId, screen);

        var evt = Assert.IsType<ScreenIpcChangeEvent>(IpcJsonService.Deserialize<ScreenIpcChangeEvent>(Assert.Single(messages)));
        Assert.Equal("runtime-session", evt.OwnerSessionId);
        Assert.True(evt.CreatedByIpc);
        Assert.Equal("ipc-owner", evt.OwnerId);
        Assert.True(evt.SourceControlsLocked);
        Assert.Equal("lock-owner", evt.SourceControlsOwnerId);
        Assert.Equal([ScreenIpcChangeKind.Unavailable], evt.Changes);
        Assert.Null(evt.State);
    }
}
