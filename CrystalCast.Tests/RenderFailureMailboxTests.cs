using CrystalCast.Rendering;
using Pictomancy.DXDraw;

namespace CrystalCast.Tests;

public sealed class RenderFailureMailboxTests
{
    [Fact]
    public void CallbackDeviceLossReachesManagedRecoveryOnceWithItsOriginalHResult()
    {
        var mailbox = new RenderFailureMailbox();
        var failure = NativeGraphicsError.FromHResult(unchecked((int)0x887A0005));
        mailbox.Report(failure);
        var received = Record.Exception(mailbox.ThrowIfFailed);
        Assert.Same(failure, received);
        Assert.True(NativeGraphicsError.IsDeviceLost(received!));
        mailbox.ThrowIfFailed();
    }

    [Fact]
    public void LaterCallbacksCannotOverwriteTheFirstFailure()
    {
        var mailbox = new RenderFailureMailbox();
        var first = new InvalidOperationException("first callback");
        mailbox.Report(first);
        Parallel.For(0, 32, i => mailbox.Report(new Exception($"callback {i}")));
        Assert.Same(first, Record.Exception(mailbox.ThrowIfFailed));
        mailbox.ThrowIfFailed();
        var next = new InvalidOperationException("after recovery");
        mailbox.Report(next);
        Assert.Same(next, Record.Exception(mailbox.ThrowIfFailed));
    }
}
