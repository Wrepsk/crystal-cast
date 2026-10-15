using System.Numerics;

namespace CrystalRenderer.Tests;

public sealed class ScreenRenderFrameTests
{
    private static ScreenSurface Surface => new((nint)1, Vector3.Zero, Vector3.UnitX,
        -Vector3.UnitY, new ScreenAppearance());

    [Fact]
    public void CompletingAFrameSubmitsItsSurfacesAndFinishesExactlyOnce()
    {
        var submitted = new List<ScreenSurface>();
        var completions = new List<bool>();
        var frame = new ScreenRenderFrame(2, submitted.Add, completions.Add);
        frame.Submit(Surface);
        frame.Dispose();
        frame.Dispose();
        frame.Cancel();
        Assert.Equal([Surface], submitted);
        Assert.Equal([false], completions);
        Assert.Throws<ObjectDisposedException>(() => frame.Submit(Surface));
    }

    [Fact]
    public void CancelledFramesCannotBeScheduledByDispose()
    {
        var completions = new List<bool>();
        var frame = new ScreenRenderFrame(2, _ => { }, completions.Add);
        frame.Submit(Surface);
        frame.Cancel();
        frame.Dispose();
        Assert.Equal([true], completions);
        Assert.Throws<ObjectDisposedException>(() => frame.Submit(Surface));
    }

    [Fact]
    public void InvalidSurfacesDoNotConsumeCapacityOrReachNativeCode()
    {
        var submitted = 0;
        using var frame = new ScreenRenderFrame(1, _ => submitted++, _ => { });
        Assert.Throws<ArgumentException>(() => frame.Submit(Surface with { TextureHandle = 0 }));
        Assert.Throws<ArgumentException>(() => frame.Submit(Surface with { Center = new Vector3(float.NaN) }));
        Assert.Throws<ArgumentException>(() => frame.Submit(Surface with { Down = Vector3.UnitX }));
        Assert.Throws<ArgumentException>(() => frame.Submit(Surface with { Appearance = new ScreenAppearance { OccludedAlpha = 2 } }));
        frame.Submit(Surface);
        Assert.Equal(1, submitted);
        Assert.Throws<InvalidOperationException>(() => frame.Submit(Surface));
    }

    [Fact]
    public void AFailedCompletionIsNotRepeatedByLaterDisposal()
    {
        var calls = 0;
        var frame = new ScreenRenderFrame(1, _ => { }, _ => { calls++; throw new InvalidOperationException(); });
        Assert.Throws<InvalidOperationException>(frame.Dispose);
        frame.Dispose();
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8193)]
    public void InvalidRendererCapacityIsRejectedBeforeCreatingResources(int capacity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenRendererOptions { MaxSurfaces = capacity }.Validate());

    [Fact]
    public void RendererAssemblyHasNoPluginOrPictomancyDependency()
    {
        var dependencies = typeof(IScreenRenderBackend).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("CrystalCast", dependencies);
        Assert.DoesNotContain("Pictomancy", dependencies);
        Assert.DoesNotContain("Microsoft.Web.WebView2.Core", dependencies);
    }
}
