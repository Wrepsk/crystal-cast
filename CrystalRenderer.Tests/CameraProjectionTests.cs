using System.Numerics;
using CrystalRenderer.Integration;

namespace CrystalRenderer.Tests;

public sealed class CameraProjectionTests
{
    private static Matrix4x4 Projection
    {
        get
        {
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.1f, 1000f);
            projection.M33 = 0f;
            projection.M43 = 0.1f;
            return projection;
        }
    }

    [Fact]
    public void StationaryWorldPointsStayFixedAcrossTemporalJitterSamples()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(4, 2, 8), Vector3.Zero, Vector3.UnitY);
        Vector4? previous = null;
        foreach (var jitter in new[] { new Vector2(-0.45f, 0.3f), new Vector2(0.2f, -0.4f), Vector2.Zero })
        {
            var depthProjection = Projection;
            depthProjection.M31 = 2f * jitter.X / 1280;
            depthProjection.M32 = 2f * jitter.Y / 720;
            Assert.True(CameraProjection.TryCreate(view, Projection, depthProjection, out var matrices));
            var point = Vector4.Transform(new Vector4(0.2f, 1f, 0f, 1f), matrices.ViewProjection);
            if (previous.HasValue) Assert.Equal(previous.Value, point);
            previous = point;
            Assert.Equal(view * Projection, matrices.ViewProjection);
            Assert.Equal(view * depthProjection, matrices.DepthViewProjection);
        }
    }

    [Fact]
    public void RealCameraMotionAndLensChangesAreAppliedWithoutSmoothing()
    {
        Assert.True(CameraProjection.TryCreate(Matrix4x4.Identity, Projection, Projection, out var original));
        var view = Matrix4x4.CreateRotationZ(0.2f) * Matrix4x4.CreateTranslation(-2f, 0, 0);
        Assert.True(CameraProjection.TryCreate(view, Projection, Projection, out var moved));
        Assert.Equal(view * Projection, moved.ViewProjection);
        Assert.Equal(moved.ViewProjection, moved.DepthViewProjection);
        Assert.NotEqual(original, moved);
        var lens = Projection;
        lens.M11 *= 1.25f;
        lens.M22 *= 1.25f;
        Assert.True(CameraProjection.TryCreate(view, lens, lens, out var zoomed));
        Assert.NotEqual(moved, zoomed);
    }

    [Fact]
    public void OffCentreCameraAndFiniteDepthMappingArePreserved()
    {
        var projection = Projection;
        projection.M31 = 0.0001f;
        projection.M32 = -0.15f;
        projection.M33 = 0.0001f;
        projection.M43 = 0.10001f;
        Assert.True(CameraProjection.TryCreate(Matrix4x4.Identity, projection, projection, out var matrices));
        Assert.Equal(projection, matrices.ViewProjection);
        Assert.Equal(new Vector2(projection.M33, projection.M43), matrices.DepthCoefficients);
    }

    [Fact]
    public void OrthographicCamerasKeepTheirProjection()
    {
        var projection = Matrix4x4.CreateOrthographic(10, 5, 0.1f, 100f);
        Assert.True(CameraProjection.TryCreate(Matrix4x4.Identity, projection, projection, out var matrices));
        Assert.Equal(projection, matrices.ViewProjection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InvalidCameraSamplesAreRejected(int invalidMatrix)
    {
        var view = Matrix4x4.Identity;
        var projection = Projection;
        var depth = Projection;
        if (invalidMatrix == 0) view.M11 = float.NaN;
        if (invalidMatrix == 1) projection.M11 = float.NaN;
        if (invalidMatrix == 2) depth.M11 = float.NaN;
        Assert.False(CameraProjection.TryCreate(view, projection, depth, out _));
    }

    [Fact]
    public void SingularProjectionIsRejected()
        => Assert.False(CameraProjection.TryCreate(Matrix4x4.Identity, Projection, default, out _));

    [Fact]
    public void AffineRenderViewGetsItsHomogeneousElement()
    {
        var view = Matrix4x4.Identity;
        view.M44 = 0f;
        Assert.True(CameraProjection.TryCreate(view, Projection, Projection, out var matrices));
        Assert.Equal(Projection, matrices.ViewProjection);
        Assert.Equal(Projection, matrices.DepthViewProjection);
    }
}
