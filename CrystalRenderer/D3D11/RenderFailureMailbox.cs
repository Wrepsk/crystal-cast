// Derived from Pictomancy (sourpuh and contributors), AGPL-3.0-or-later.
// See LICENSE.Pictomancy and THIRD_PARTY_NOTICES.md.
using System.Runtime.ExceptionServices;

namespace CrystalRenderer.D3D11;

// Native callbacks must not unwind into the game. Hand failures to the next managed draw instead.
internal sealed class RenderFailureMailbox
{
    private Exception? failure;

    public void Report(Exception exception) => Interlocked.CompareExchange(ref failure, exception, null);

    public void ThrowIfFailed()
    {
        if (Interlocked.Exchange(ref failure, null) is { } exception)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
