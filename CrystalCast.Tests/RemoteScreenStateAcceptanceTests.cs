using CrystalCast.Sync;

namespace CrystalCast.Tests;

public sealed class RemoteScreenStateAcceptanceTests
{
    [Fact]
    public void RejectsNullWrongSchemaAndMissingId()
    {
        Assert.Equal(RemoteScreenStateDecision.Reject, RemoteScreenStateAcceptance.Evaluate(null, "local", null));
        Assert.Equal(RemoteScreenStateDecision.Reject, RemoteScreenStateAcceptance.Evaluate(new ScreenStateEnvelope { SchemaVersion = 2, ScreenId = "screen" }, "local", null));
        Assert.Equal(RemoteScreenStateDecision.Reject, RemoteScreenStateAcceptance.Evaluate(new ScreenStateEnvelope { ScreenId = " " }, "local", null));
    }

    [Fact]
    public void IgnoresStatesFromLocalSession()
    {
        var candidate = new ScreenStateEnvelope { ScreenId = "screen", OwnerSessionId = "local", Sequence = 10 };

        Assert.Equal(RemoteScreenStateDecision.IgnoreSelf, RemoteScreenStateAcceptance.Evaluate(candidate, "local", null));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void IgnoresLowerOrEqualSequence(long candidateSequence)
    {
        var existing = new ScreenStateEnvelope { ScreenId = "screen", OwnerSessionId = "remote", Sequence = 10 };
        var candidate = new ScreenStateEnvelope { ScreenId = "screen", OwnerSessionId = "remote", Sequence = candidateSequence };

        Assert.Equal(RemoteScreenStateDecision.IgnoreStale, RemoteScreenStateAcceptance.Evaluate(candidate, "local", existing));
    }

    [Fact]
    public void AcceptsNewerRemoteSequence()
    {
        var existing = new ScreenStateEnvelope { ScreenId = "screen", OwnerSessionId = "remote", Sequence = 10 };
        var candidate = new ScreenStateEnvelope { ScreenId = "screen", OwnerSessionId = "remote", Sequence = 11 };

        Assert.Equal(RemoteScreenStateDecision.Accept, RemoteScreenStateAcceptance.Evaluate(candidate, "local", existing));
    }
}
