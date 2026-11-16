using CrystalCast.Twitch;

namespace CrystalCast.Tests;

public sealed class TwitchEmotePickerTests
{
    private static readonly TwitchEmote[] Emotes =
    [
        new("25", "Kappa", "globals", "0"),
        new("e1", "chanHype", "subscriptions", "7"),
        new("e3", "chanFollow", "follower", "7"),
        new("e2", "otherWave", "subscriptions", "9"),
        new("e4", "Kappa", "subscriptions", "9"),
        new("e5", "LUL", "", ""),
    ];

    [Fact]
    public void GroupsThisChannelThenOthersThenGlobal()
    {
        var groups = TwitchEmotePicker.Arrange(Emotes, "7", string.Empty);

        Assert.Equal([TwitchEmoteGroup.ThisChannel, TwitchEmoteGroup.OtherChannels, TwitchEmoteGroup.Global], groups.Select(g => g.Group));
        Assert.Equal(["chanFollow", "chanHype"], groups[0].Emotes.Select(e => e.Name));
        Assert.Equal(["otherWave"], groups[1].Emotes.Select(e => e.Name));
        // Same name twice: the first one listed is kept.
        Assert.Equal(["Kappa", "LUL"], groups[2].Emotes.Select(e => e.Name));
    }

    [Fact]
    public void WithoutAChannelNothingIsThisChannels()
    {
        var groups = TwitchEmotePicker.Arrange(Emotes, string.Empty, string.Empty);
        Assert.DoesNotContain(groups, g => g.Group == TwitchEmoteGroup.ThisChannel);
    }

    [Theory]
    [InlineData("chan", new[] { "chanFollow", "chanHype" })]
    [InlineData("KAPPA", new[] { "Kappa" })]
    [InlineData("  wave ", new[] { "otherWave" })]
    [InlineData("nothing", new string[0])]
    public void SearchesAnyPartOfTheName(string search, string[] expected)
    {
        var names = TwitchEmotePicker.Arrange(Emotes, "7", search).SelectMany(g => g.Emotes).Select(e => e.Name).Order();
        Assert.Equal(expected.Order(), names);
    }

    [Theory]
    [InlineData("", "Kappa ")]
    [InlineData("hi", "hi Kappa ")]
    [InlineData("hi ", "hi Kappa ")]
    public void AddsTheNameAsAWholeWord(string message, string expected)
    {
        Assert.Equal(expected, TwitchEmotePicker.Insert(message, "Kappa"));
    }

    [Fact]
    public void KeepsTheMessageWithinTwitchsLength()
    {
        var full = new string('a', TwitchEmotePicker.MaxMessageLength - 3);
        Assert.Equal(full, TwitchEmotePicker.Insert(full, "Kappa"));
    }
}
