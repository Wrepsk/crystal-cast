// Derived from Pictomancy (sourpuh and contributors), AGPL-3.0-or-later.
// See LICENSE.Pictomancy and THIRD_PARTY_NOTICES.md.
using System.Runtime.InteropServices;

namespace CrystalRenderer.D3D11;

internal static class ComResourceIdentity
{
    internal static bool AreSame(nint left, nint right)
    {
        if (left == 0 || right == 0) return false;
        if (left == right) return true;
        var unknown = new Guid("00000000-0000-0000-C000-000000000046");
        nint leftIdentity = 0;
        nint rightIdentity = 0;
        try
        {
            return Marshal.QueryInterface(left, in unknown, out leftIdentity) == 0
                && Marshal.QueryInterface(right, in unknown, out rightIdentity) == 0
                && leftIdentity == rightIdentity;
        }
        finally
        {
            if (rightIdentity != 0) Marshal.Release(rightIdentity);
            if (leftIdentity != 0) Marshal.Release(leftIdentity);
        }
    }
}
