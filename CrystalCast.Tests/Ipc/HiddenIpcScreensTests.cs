using CrystalCast.Ipc;

namespace CrystalCast.Tests;

public sealed class HiddenIpcScreensTests
{
    [Fact]
    public void HiddenScreensStayHiddenUntilCleared()
    {
        var hidden = new HiddenIpcScreens();
        hidden.Hide("sync-screen");

        Assert.True(hidden.IsHidden("sync-screen"));
        Assert.False(hidden.IsHidden("other-screen"));
        Assert.False(hidden.IsHidden(null));
        Assert.Equal(1, hidden.Count);

        hidden.Clear();

        Assert.False(hidden.IsHidden("sync-screen"));
        Assert.Equal(0, hidden.Count);
    }

    [Fact]
    public void BlankIdsAreIgnored()
    {
        var hidden = new HiddenIpcScreens();
        hidden.Hide("");
        hidden.Hide("   ");

        Assert.Equal(0, hidden.Count);
    }

    [Fact]
    public void TheListHasALimit()
    {
        var hidden = new HiddenIpcScreens();
        for (var i = 0; i < HiddenIpcScreens.MaxEntries + 10; i++)
            hidden.Hide($"screen-{i}");

        Assert.Equal(HiddenIpcScreens.MaxEntries, hidden.Count);
    }

    [Fact]
    public void RefusalTellsTheCallerTheUserHidIt()
    {
        var json = IpcJsonService.SerializeHiddenByUserError("sync-screen");

        Assert.Contains("\"success\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"hiddenByUser\":true", json, StringComparison.Ordinal);
        Assert.Contains("sync-screen", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ScreenIpcRemovalReason.RemovedByUser, "\"removalReason\":\"RemovedByUser\"")]
    [InlineData(ScreenIpcRemovalReason.HiddenByUser, "\"removalReason\":\"HiddenByUser\"")]
    [InlineData(ScreenIpcRemovalReason.RemovedByIpc, "\"removalReason\":\"RemovedByIpc\"")]
    public void UnavailableEventsSayWhyTheScreenWentAway(ScreenIpcRemovalReason reason, string expected)
    {
        var configuration = new Configuration();
        var screen = Assert.IsType<BrowserScreenProfile>(configuration.GetActiveBrowserScreen());
        var messages = new List<string>();
        var publisher = new ScreenChangePublisher(configuration, "runtime-session", messages.Add);

        publisher.SendUnavailableAndForget(screen.ScreenId, screen, reason);

        var message = Assert.Single(messages);
        Assert.Contains("\"changes\":[\"Unavailable\"]", message, StringComparison.Ordinal);
        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateIfMissingIsReadFromTheRequest()
    {
        var request = IpcJsonService.Deserialize<ScreenIpcMutationRequest>("{\"screenId\":\"sync-screen\",\"createIfMissing\":true}");

        Assert.NotNull(request);
        Assert.True(request.CreateIfMissing);
    }
}
