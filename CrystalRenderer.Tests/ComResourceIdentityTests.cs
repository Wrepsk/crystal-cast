using System.Runtime.InteropServices;
using CrystalRenderer.D3D11;

namespace CrystalRenderer.Tests;

public sealed class ComResourceIdentityTests
{
    [ComVisible(true), Guid("C958828A-4F11-43E8-ACBA-F691240C6E81"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITestResource { void Touch(); }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class TestResource : ITestResource { public void Touch() { } }

    [Fact]
    public void DifferentInterfacesOfOneResourceMatchByCanonicalIdentity()
    {
        var resource = new TestResource();
        var unknown = Marshal.GetIUnknownForObject(resource);
        var view = Marshal.GetComInterfaceForObject(resource, typeof(ITestResource));
        try
        {
            Assert.NotEqual(unknown, view);
            Assert.True(ComResourceIdentity.AreSame(unknown, view));
            Assert.True(ComResourceIdentity.AreSame(view, unknown));
        }
        finally
        {
            Marshal.Release(view);
            Marshal.Release(unknown);
        }
    }

    [Fact]
    public void DistinctResourcesAndMissingPointersDoNotMatch()
    {
        var left = Marshal.GetIUnknownForObject(new TestResource());
        var right = Marshal.GetIUnknownForObject(new TestResource());
        try
        {
            Assert.False(ComResourceIdentity.AreSame(left, right));
            Assert.True(ComResourceIdentity.AreSame(left, left));
            Assert.False(ComResourceIdentity.AreSame(0, left));
            Assert.False(ComResourceIdentity.AreSame(0, 0));
        }
        finally
        {
            Marshal.Release(right);
            Marshal.Release(left);
        }
    }
}
