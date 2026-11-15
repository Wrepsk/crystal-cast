using CrystalCast.Ipc;
using CrystalCast.Rooms;
using CrystalCast.Windows;

namespace CrystalCast.Tests;

public sealed class RoomUsernameTests
{
    [Theory]
    [InlineData("Sleepy Moogle 42", "Sleepy Moogle 42")]
    [InlineData("  Brave   Chocobo  ", "Brave Chocobo")]
    [InlineData("Line\nbreak\ttab", "Line break tab")]
    [InlineData("Brave\u0000 Chocobo‮", "Brave Chocobo")] // a control character and a text-direction mark
    [InlineData("Zero​width", "Zerowidth")]
    [InlineData("A name that is far too long for anyone", "A name that is far too l")] // 24 characters, like the relay
    [InlineData("Moogle 🌟 friend", "Moogle 🌟 friend")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void CleansNamesLikeTheRelay(string input, string expected)
    {
        Assert.Equal(expected, RoomUsername.Clean(input));
    }

    [Fact]
    public void CountsCharactersNotUtf16Units()
    {
        var name = string.Concat(Enumerable.Repeat("🌟", 30));

        Assert.Equal(RoomUsername.MaxLength, RoomUsername.Clean(name).EnumerateRunes().Count());
    }

    [Fact]
    public void AnEmptyNameShowsAsGuest()
    {
        Assert.Equal("Guest", RoomUsername.Display("  "));
        Assert.Equal("Kupo", RoomUsername.Display(" Kupo "));
    }

    [Fact]
    public void RandomNamesAreValidAndVary()
    {
        var random = new Random(7);
        var names = Enumerable.Range(0, 20).Select(_ => RoomUsername.Random(random)).ToList();

        Assert.All(names, name => Assert.Equal(name, RoomUsername.Clean(name)));
        Assert.All(names, name => Assert.Matches(@"^\w+ [\w ]+ \d{2}$", name));
        Assert.True(names.Distinct().Count() > 10);
    }

    [Fact]
    public void ANewConfigurationGetsARandomUsername()
    {
        var configuration = new Configuration();

        configuration.Normalize();

        Assert.NotEqual(string.Empty, configuration.RoomUsername);
        Assert.Equal(configuration.RoomUsername, RoomUsername.Clean(configuration.RoomUsername));
    }

    [Fact]
    public void ASavedUsernameIsKeptButCleaned()
    {
        var configuration = new Configuration { RoomUsername = "  Merry   Namazu  " };

        configuration.Normalize();

        Assert.Equal("Merry Namazu", configuration.RoomUsername);
    }

    [Fact]
    public void ReadsTheMemberList()
    {
        const string json = """
            {"type":"members","members":[
              {"name":"Sleepy Moogle 42","host":true,"you":false},
              {"name":"Brave Chocobo","host":false,"you":true}]}
            """;

        var message = IpcJsonService.Deserialize<RoomRelayMessage>(json);

        Assert.NotNull(message?.Members);
        Assert.Collection(message.Members,
            host => Assert.Equal(("Sleepy Moogle 42", true, false), (host.Name, host.Host, host.You)),
            you => Assert.Equal(("Brave Chocobo", false, true), (you.Name, you.Host, you.You)));
    }

    [Theory]
    [InlineData(true, false, "(Host)")]
    [InlineData(true, true, "(Host, you)")]
    [InlineData(false, true, "(you)")]
    [InlineData(false, false, "")]
    public void TagsTheHostAndYou(bool host, bool you, string expected)
    {
        Assert.Equal(expected, RoomsWindow.RoomMemberTags(new RoomMember { Name = "x", Host = host, You = you }));
    }
}
