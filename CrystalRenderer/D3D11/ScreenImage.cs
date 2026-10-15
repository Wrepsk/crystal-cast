// Derived from Pictomancy (sourpuh and contributors), AGPL-3.0-or-later.
// See LICENSE.Pictomancy and THIRD_PARTY_NOTICES.md.
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.InteropServices;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace CrystalRenderer.D3D11;

internal class ScreenImage : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix ViewProj;
        public Matrix InvViewProj;
        public Matrix DepthViewProj;
        public Vector2 RenderTargetSize;
        public Vector2 PixelToUv;
        public Vector2 DepthCoefficients;
        public Vector2 Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector3 Center;
        public Vector3 Right;
        public Vector3 Down;
        public Vector2 UvMin;
        public Vector2 UvMax;
        public float OccludedAlpha;
        public float OcclusionTolerance;
        public float FadeStart;
        public float FadeStop;
        public float ProjectionHeight;
    }

    private struct Submission
    {
        public Instance Inst;
        public IntPtr NativePtr;
    }

    private class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private RenderBuffer<Instance>.Builder _inner;
            internal Builder(RenderContext ctx, Data data) { _inner = data._buffer.Map(ctx); }
            public void Dispose() => _inner.Dispose();
            public void Add(Instance inst) => _inner.Add(ref inst);
        }

        private readonly RenderBuffer<Instance> _buffer;

        public Data(RenderContext ctx, string name, int maxCount)
        {
            _buffer = new(name, ctx, maxCount, BindFlags.VertexBuffer, true);
        }

        public void Dispose() => _buffer.Dispose();
        public Builder Map(RenderContext ctx) => new(ctx, this);

        public void BindVertexBuffer(RenderContext ctx)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
        }
    }

    private readonly List<Submission> _flatImages = new();
    private readonly RenderContext _ctx;
    private readonly Data _flatData;
    private readonly SharpDX.Direct3D11.Buffer _constantBuffer;
    private readonly InputLayout _il;
    private readonly VertexShader _vsFlat;
    private readonly PixelShader _psFlat;
    private readonly PixelShader _psFlatOpaque;
    private readonly DepthStencilState _opaqueDepthState;
    private readonly DepthStencilState _transparentDepthState;
    private readonly RasterizerState _rsFlat;

    public bool HasPending => _flatImages.Count > 0;

    private const string ShaderHeader = """
            cbuffer Constants : register(b0)
            {
                float4x4 viewProj;
                float4x4 invViewProj;
                float4x4 depthViewProj;
                float2 renderTargetSize;
                float2 pixelToUv;
                float2 depthCoefficients;
                float2 padding;
            };

            Texture2D<float4> _userTex : register(t3);
            SamplerState _userSampler
            {
                Filter = MIN_MAG_MIP_LINEAR;
                AddressU = CLAMP;
                AddressV = CLAMP;
            };

            struct ImageInstance
            {
                float3 center : WORLD0;
                float3 right : WORLD1;
                float3 down : WORLD2;
                float2 uvMin : TEXCOORD0;
                float2 uvMax : TEXCOORD1;
                float2 occlusionParams : OCCLUSIONPARAMS;
                float2 fadeParams : FADEPARAMS;
                float projectionHeight : HEIGHT;
            };
            """;

    internal static string FlatShaderSource => ShapeSharedShader.Preamble + ShaderHeader + ShapeSharedShader.Mixin + """
            struct VSOutput
            {
                float4 projPos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 depthPos : TEXCOORD1;
                nointerpolation float2 occlusionParams : OCCLUSIONPARAMS;
                nointerpolation float2 fadeParams : FADEPARAMS;
            };

            VSOutput vs(in ImageInstance inst, uint vertexId : SV_VERTEXID)
            {
                float2 corner = float2(vertexId & 1u, (vertexId >> 1u) & 1u);
                float2 offset = corner - 0.5;
                float3 worldPos = inst.center + offset.x * inst.right + offset.y * inst.down;

                VSOutput o;
                o.projPos = mul(float4(worldPos, 1.0), viewProj);
                o.depthPos = mul(float4(worldPos, 1.0), depthViewProj);
                o.uv = lerp(inst.uvMin, inst.uvMax, corner);
                o.occlusionParams = inst.occlusionParams;
                o.fadeParams = inst.fadeParams;
                return o;
            }

            float4 samplePixel(VSOutput input)
            {
                float4 color = _userTex.Sample(_userSampler, input.uv);
                return applyShared(color, input.depthPos, input.occlusionParams, input.fadeParams);
            }

            float4 psOpaque(VSOutput input) : SV_TARGET
            {
                float4 color = samplePixel(input);
                if (color.a < 0.99999) discard;
                return color;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                float4 color = samplePixel(input);
                if (color.a <= 0.0 || color.a >= 0.99999) discard;
                return color;
            }
            """;

    public ScreenImage(RenderContext ctx, int maxImages, Action<string>? log = null)
    {
        _ctx = ctx;
        log ??= message => ctx.Log?.Debug(message);
        try
        {
            _flatData = new(ctx, "ImageFlat", maxImages);

            var flatShader = FlatShaderSource;

            using var vsFlat = ShaderBytecode.Compile(flatShader, "vs", "vs_5_0");
            if (vsFlat.HasErrors || vsFlat.Bytecode == null)
                throw new InvalidOperationException($"Image flat VS compile failed:\n{vsFlat.Message}");
            log($"Image flat VS compile: {vsFlat.Message}");
            _vsFlat = new(ctx.Device, vsFlat.Bytecode);

            using var psFlat = ShaderBytecode.Compile(flatShader, "ps", "ps_5_0");
            if (psFlat.HasErrors || psFlat.Bytecode == null)
                throw new InvalidOperationException($"Image flat PS compile failed:\n{psFlat.Message}");
            log($"Image flat PS compile: {psFlat.Message}");
            _psFlat = new(ctx.Device, psFlat.Bytecode);
            using var psOpaque = ShaderBytecode.Compile(flatShader, "psOpaque", "ps_5_0");
            if (psOpaque.HasErrors || psOpaque.Bytecode == null)
                throw new InvalidOperationException($"Image opaque PS compile failed:\n{psOpaque.Message}");
            _psFlatOpaque = new(ctx.Device, psOpaque.Bytecode);
            _opaqueDepthState = new(ctx.Device, ScreenDepthPolicy.CreateImageState(writeDepth: true));
            _transparentDepthState = new(ctx.Device, ScreenDepthPolicy.CreateImageState(writeDepth: false));

            _constantBuffer = new(ctx.Device, BufferSize.AlignTo16<Constants>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

            _il = new(ctx.Device, vsFlat.Bytecode,
            [
                new InputElement("WORLD", 0, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
                new InputElement("WORLD", 1, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
                new InputElement("WORLD", 2, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
                new InputElement("TEXCOORD", 1, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
                new InputElement("OCCLUSIONPARAMS", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
                new InputElement("FADEPARAMS", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
                new InputElement("HEIGHT", 0, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            ]);

            var rsDescFlat = RasterizerStateDescription.Default();
            rsDescFlat.CullMode = CullMode.None;
            _rsFlat = new(ctx.Device, rsDescFlat);


        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _flatData?.Dispose();
        _constantBuffer?.Dispose();
        _il?.Dispose();
        _vsFlat?.Dispose();
        _psFlat?.Dispose();
        _psFlatOpaque?.Dispose();
        _opaqueDepthState?.Dispose();
        _transparentDepthState?.Dispose();
        _rsFlat?.Dispose();
    }

    public void UpdateConstants(Constants consts)
    {
        consts.ViewProj.Transpose();
        consts.InvViewProj.Transpose();
        consts.DepthViewProj.Transpose();
        _ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
    }

    public void Add(IntPtr nativePtr, Vector3 center, Vector3 right, Vector3 down, Vector2 uvMin, Vector2 uvMax, ScreenAppearance p)
    {
        if (nativePtr == IntPtr.Zero) return;
        // Skip degenerate plane (right parallel to down).
        if (Vector3.Cross(right, down).LengthSquared() < 1e-8f) return;

        // A deferred composite may outlive the source texture wrapper (screen removal,
        // source changes, or resizing). Retain its SRV before handing the frame to the hook.
        _ctx.GetUserSrv(nativePtr);


        var sub = new Submission
        {
            Inst = new Instance
            {
                Center = center,
                Right = right,
                Down = down,
                UvMin = uvMin,
                UvMax = uvMax,
                OccludedAlpha = p.OccludedAlpha,
                OcclusionTolerance = p.OcclusionTolerance,
                FadeStart = p.FadeStart,
                FadeStop = p.FadeStop,
                ProjectionHeight = 0f,
            },
            NativePtr = nativePtr,
        };
        _flatImages.Add(sub);
    }

    public void Discard()
    {
        _flatImages.Clear();
    }

    public void FlushFlat(Vector3 cameraPos, DepthStencilState shapeState)
    {
        if (_flatImages.Count > 0)
        {
            _flatImages.Sort((a, b) =>
            {
                float da = Vector3.DistanceSquared(a.Inst.Center, cameraPos);
                float db = Vector3.DistanceSquared(b.Inst.Center, cameraPos);
                return db.CompareTo(da);
            });

            using (var b = _flatData.Map(_ctx))
            {
                for (int i = 0; i < _flatImages.Count; i++)
                    b.Add(_flatImages[i].Inst);
            }

            BindFlat();
            _flatData.BindVertexBuffer(_ctx);
            try
            {
                // First resolve solid screen intersections per pixel. Transparent pixels must
                // not write depth, or invisible/occluded portions would hide other screens.
                _ctx.Context.OutputMerger.SetDepthStencilState(_opaqueDepthState, 0);
                _ctx.Context.PixelShader.Set(_psFlatOpaque);
                DrawFlatInstances();
                _ctx.Context.OutputMerger.SetDepthStencilState(_transparentDepthState, 0);
                _ctx.Context.PixelShader.Set(_psFlat);
                DrawFlatInstances();
            }
            finally
            {
                _ctx.Context.OutputMerger.SetDepthStencilState(shapeState, 0);
                _ctx.Context.PixelShader.SetShaderResource(3, null);
            }
        }

        Discard();
    }

    private void DrawFlatInstances()
    {
        for (int i = 0; i < _flatImages.Count; i++)
        {
            _ctx.Context.PixelShader.SetShaderResource(3, _ctx.GetUserSrv(_flatImages[i].NativePtr));
            _ctx.Context.DrawInstanced(4, 1, 0, i);
        }
    }

    private void BindFlat()
    {
        _ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        _ctx.Context.InputAssembler.InputLayout = _il;
        _ctx.Context.VertexShader.Set(_vsFlat);
        _ctx.Context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.PixelShader.Set(_psFlat);
        _ctx.Context.PixelShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.GeometryShader.Set(null);
        _ctx.Context.Rasterizer.State = _rsFlat;
    }

}
