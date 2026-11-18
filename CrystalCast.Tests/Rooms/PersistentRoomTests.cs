using CrystalCast.Rooms;
using CrystalCast.Windows;

namespace CrystalCast.Tests.Rooms;

public sealed class PersistentRoomTests
{
    [Fact]
    public void MemberKeysAreLongRandomHex()
    {
        var first = RoomMemberKeys.Create();
        var second = RoomMemberKeys.Create();

        Assert.Equal(64, first.Length);
        Assert.True(RoomMemberKeys.IsValid(first));
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void BadMemberKeysAreRejected(string? key)
    {
        Assert.False(RoomMemberKeys.IsValid(key));
    }

    [Fact]
    public void PasswordsAreHashedAndEmptyMeansNone()
    {
        Assert.Equal(string.Empty, RoomMemberKeys.HashPassword(""));
        Assert.Equal(string.Empty, RoomMemberKeys.HashPassword(null));

        var hash = RoomMemberKeys.HashPassword("hunter2");
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("hunter2", hash);
        Assert.Equal(hash, RoomMemberKeys.HashPassword("hunter2"));
        Assert.NotEqual(hash, RoomMemberKeys.HashPassword("Hunter2"));
    }

    [Fact]
    public void TheRoomListDropsBadCodesAndRepeats()
    {
        var cleaned = RoomMemberKeys.CleanRoomList(
        [
            new PersistentRoomEntry { Code = "abcd-2345", Name = "Club" },
            new PersistentRoomEntry { Code = "ABCD2345", Name = "Again" },
            new PersistentRoomEntry { Code = "nope" },
            new PersistentRoomEntry { Code = "WXYZ-6789", AutoJoin = false },
        ]);

        Assert.Equal(["ABCD2345", "WXYZ6789"], cleaned.Select(entry => entry.Code).ToArray());
        Assert.Equal("Club", cleaned[0].Name);
        Assert.False(cleaned[1].AutoJoin);
    }

    [Fact]
    public void MirrorIdsAreStableAndDistinct()
    {
        var a = RoomMemberKeys.MirrorScreenId("ABCD2345", "owner1", "screen1");
        Assert.Equal(a, RoomMemberKeys.MirrorScreenId("ABCD2345", "owner1", "screen1"));
        Assert.NotEqual(a, RoomMemberKeys.MirrorScreenId("ABCD2345", "owner2", "screen1"));
        Assert.NotEqual(a, RoomMemberKeys.MirrorScreenId("WXYZ6789", "owner1", "screen1"));
        Assert.StartsWith("proom-ABCD2345-", a);
    }

    [Fact]
    public void PersistentSocketAddressCarriesKeyNameAndPassword()
    {
        Assert.True(RoomRelayApi.TryParseRelayUri("https://relay.example/", out var relay));

        var withPassword = RoomRelayConnection.BuildPersistentSocketUri(relay, "ABCD2345", "00ff", "A&B", "abc123");
        Assert.Equal("wss://relay.example/persistent/ABCD2345/socket?key=00ff&name=A%26B&password=abc123", withPassword.AbsoluteUri);

        var without = RoomRelayConnection.BuildPersistentSocketUri(relay, "ABCD2345", "00ff", "A", "");
        Assert.DoesNotContain("password", without.AbsoluteUri);
    }

    [Theory]
    [InlineData("owner", "mod", true)]
    [InlineData("owner", "member", true)]
    [InlineData("mod", "member", true)]
    [InlineData("mod", "mod", false)]
    [InlineData("mod", "owner", false)]
    [InlineData("member", "member", false)]
    public void WhoCanModerateWhom(string actor, string target, bool allowed)
    {
        Assert.Equal(allowed, RoomsWindow.CanModerate(actor, target));
    }

    [Fact]
    public void MemberTagsShowRoleYouAndOffline()
    {
        Assert.Equal("(Owner, you)", RoomsWindow.PersistentMemberTags(new RoomMember { Role = "owner", You = true, Online = true }));
        Assert.Equal("(Moderator, offline)", RoomsWindow.PersistentMemberTags(new RoomMember { Role = "mod" }));
        Assert.Equal(string.Empty, RoomsWindow.PersistentMemberTags(new RoomMember { Role = "member", Online = true }));
    }

    [Fact]
    public void AMirrorTargetNamesTheSharer()
    {
        var target = new RoomMirrorTarget("proom-x", "Mia's screen", "Mia in Club", NameSuffix: "(Mia)");
        var request = RoomViewerPlan.Build(
            target,
            new RoomScreenState { Name = "Stage", Url = "https://youtu.be/x", Location = new RoomLocation { WorldId = 1, TerritoryId = 2 } },
            0, 0, new RoomLocation { WorldId = 1, TerritoryId = 2 }, null, 0, new RoomViewerApplied());

        Assert.Equal("proom-x", request.ScreenId);
        Assert.Equal("Stage (Mia)", request.Name);
        Assert.Equal("Mia in Club", request.SourceControlsOwnerId);
        Assert.True(request.Enabled);

        var hidden = RoomViewerPlan.Build(
            target with { Visible = false },
            new RoomScreenState { Location = new RoomLocation { WorldId = 1, TerritoryId = 2 } },
            0, 0, new RoomLocation { WorldId = 1, TerritoryId = 2 }, null, 0, new RoomViewerApplied());
        Assert.False(hidden.Enabled);
        Assert.Equal("Mia's screen", hidden.Name);
    }
}
