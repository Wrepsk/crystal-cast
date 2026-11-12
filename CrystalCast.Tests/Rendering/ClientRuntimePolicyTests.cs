using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class ClientRuntimePolicyTests
{
    [Fact]
    public void MainMenuCannotStartClientRuntime()
    {
        Assert.False(ClientRuntimePolicy.CanStart(isLoggedIn: false));
    }

    [Fact]
    public void LoggedInCharacterCanStartClientRuntime()
    {
        Assert.True(ClientRuntimePolicy.CanStart(isLoggedIn: true));
    }
}
