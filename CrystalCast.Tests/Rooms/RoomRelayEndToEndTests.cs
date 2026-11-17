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

        using var host = new RoomRelayConnection(relay, created.Code, created.HostKey, "Host");
        Assert.Equal("welcome", (await NextAsync(host, m => m.Type == "welcome")).Type);

        using var viewer = new RoomRelayConnection(relay, created.Code, hostKey: null, "Viewer");
        var welcome = await NextAsync(viewer, m => m.Type == "welcome");
        Assert.Null(welcome.State);
        Assert.Equal(1, (await NextAsync(host, m => m.Type == "viewers")).Count);

        // Everyone gets the member list, the host first, with their own entry marked.
        var members = await NextAsync(viewer, m => m.Type == "members" && m.Members?.Count == 2);
        Assert.Equal(("Host", true, false), (members.Members![0].Name, members.Members[0].Host, members.Members[0].You));
        Assert.Equal(("Viewer", false, true), (members.Members[1].Name, members.Members[1].Host, members.Members[1].You));

        viewer.Rename("Brave Chocobo");
        var renamed = await NextAsync(host, m => m.Type == "members" && m.Members?.Any(member => member.Name == "Brave Chocobo") == true);
        Assert.True(renamed.Members![0].You);

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

        using var refused = new RoomRelayConnection(relay, created.Code, hostKey: null, "Viewer");
        for (var i = 0; i < 50 && !refused.Refused; i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.True(refused.Refused);
    }

    [Fact]
    public async Task PersistentRoomsLetMembersShareAndKeepStrangersOut()
    {
        if (string.IsNullOrWhiteSpace(Relay))
            return;

        Assert.True(RoomRelayApi.TryParseRelayUri(Relay, out var relay));
        var ownerKey = RoomMemberKeys.Create();
        var created = await RoomRelayApi.CreatePersistentRoomAsync(relay, "Club", "hunter2", ownerKey, "Owner", TestContext.Current.CancellationToken);
        Assert.True(created.Error.Length == 0, created.Error);

        using var owner = RoomRelayConnection.ForPersistentRoom(relay, created.Code, ownerKey, "Owner", string.Empty);
        var welcome = await NextAsync(owner, m => m.Type == "welcome");
        Assert.Equal(("owner", "Club", true), (welcome.Role, welcome.Name, welcome.PasswordSet));

        using var stranger = RoomRelayConnection.ForPersistentRoom(relay, created.Code, RoomMemberKeys.Create(), "Stranger", "wrong");
        for (var i = 0; i < 50 && !stranger.Refused; i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(stranger.Refused);
        Assert.Equal("password", stranger.RefusalReason);

        using var member = RoomRelayConnection.ForPersistentRoom(relay, created.Code, RoomMemberKeys.Create(), "Mia", "hunter2");
        Assert.Equal("member", (await NextAsync(member, m => m.Type == "welcome")).Role);

        owner.Send(new { type = "state", screenId = "stage", state = new RoomScreenState { Name = "Stage", Url = "https://youtu.be/x" } });
        var shared = await NextAsync(member, m => m.Type == "state");
        Assert.Equal(("stage", "Stage", welcome.You), (shared.ScreenId, shared.State!.Name, shared.Owner));

        owner.Send(new { type = "delete" });
        await NextAsync(member, m => m.Type == "closed");
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
