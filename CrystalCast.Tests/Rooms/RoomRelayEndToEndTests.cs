using CrystalCast.Rooms;

namespace CrystalCast.Tests.Rooms;

/// <summary>
/// Drives the plugin's relay client against a real relay. Runs only when CRYSTALCAST_TEST_RELAY is set, for
/// example to http://127.0.0.1:8787 with <c>npm run dev</c> running in relay/.
/// </summary>
public sealed class RoomRelayEndToEndTests
{
    private static readonly string? Relay = Environment.GetEnvironmentVariable("CRYSTALCAST_TEST_RELAY");

    [Fact]
    public async Task HostStatesReachTheViewer()
    {
        if (string.IsNullOrWhiteSpace(Relay))
            return;

        Assert.True(RoomRelayApi.TryParseRelayUri(Relay, out var relay));
        var created = await RoomRelayApi.CreateRoomAsync(relay, TestContext.Current.CancellationToken);
        Assert.True(created.Error.Length == 0, created.Error);

        using var host = new RoomRelayConnection(relay, created.Code, created.HostKey);
        Assert.Equal("welcome", (await NextAsync(host, m => m.Type == "welcome")).Type);

        using var viewer = new RoomRelayConnection(relay, created.Code, hostKey: null);
        var welcome = await NextAsync(viewer, m => m.Type == "welcome");
        Assert.Null(welcome.State);
        Assert.Equal(1, (await NextAsync(host, m => m.Type == "viewers")).Count);

        var state = new RoomScreenState
        {
            Name = "Stage",
            Provider = BrowserSourceProviderKind.Vimeo,
            Url = "https://vimeo.com/76979871",
            PositionMs = 12_345,
            Location = new RoomLocation { WorldId = 73, TerritoryId = 341, Ward = 4, Plot = 12 },
        };
        host.Send(new { type = "state", change = true, state });

        var received = await NextAsync(viewer, m => m.Type == "state");
        Assert.Equal(12_345, received.State!.PositionMs);
        Assert.Equal(BrowserSourceProviderKind.Vimeo, received.State.Provider);
        Assert.Equal(12, received.State.Location.Plot);
        Assert.True(received.ReceivedAt > 0);

        viewer.Send(new { type = "add", url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ" });
        var add = await NextAsync(host, m => m.Type == "add");
        Assert.Equal("https://www.youtube.com/watch?v=aqz-KE-bpKQ", add.Url);

        viewer.Send(new { type = "ping", t = 7 });
        var pong = await NextAsync(viewer, m => m.Type == "pong");
        Assert.Equal(7, pong.T);

        host.Send(new { type = "close" });
        await NextAsync(viewer, m => m.Type == "closed");

        using var refused = new RoomRelayConnection(relay, created.Code, hostKey: null);
        for (var i = 0; i < 50 && !refused.Refused; i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.True(refused.Refused);
    }

    private static async Task<RoomRelayMessage> NextAsync(RoomRelayConnection connection, Func<RoomRelayMessage, bool> match)
    {
        for (var i = 0; i < 100; i++)
        {
            while (connection.TryDequeue(out var message))
            {
                if (match(message))
                    return message;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The relay did not send the expected message.");
    }
}
