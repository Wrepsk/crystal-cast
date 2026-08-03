using CrystalCast.Rendering;

namespace CrystalCast.Tests;

public sealed class KeyedMutexSynchronizationTests
{
    [Fact]
    public void SuccessMeansMutexWasAcquired()
    {
        Assert.True(KeyedMutexSynchronization.InterpretAcquireResult(0));
    }

    [Fact]
    public void WaitTimeoutMeansMutexWasNotAcquired()
    {
        Assert.False(KeyedMutexSynchronization.InterpretAcquireResult(
            KeyedMutexSynchronization.WaitTimeoutResult));
    }

    [Fact]
    public void DxgiFailuresStillThrow()
    {
        Assert.ThrowsAny<Exception>(() =>
            KeyedMutexSynchronization.InterpretAcquireResult(unchecked((int)0x887A0001)));
    }
}
