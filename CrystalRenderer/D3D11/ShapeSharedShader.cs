// Derived from Pictomancy, AGPL-3.0-or-later. See LICENSE.Pictomancy.
namespace CrystalRenderer.D3D11;
internal static class ShapeSharedShader
{
    public const string Preamble = """
        static const float PI = 3.14159265359;

        // Approximation for infinity in reverse Z.
        static const float SKY_DEPTH_EPS = 1e-6;
        // Threshold for when character pixels are slightly non-black in sceneInfo.
        static const float CHARACTER_MASK_THRESHOLD = 0.05;
        // Disable fade when fadeStop is at or beyond this.
        static const float NO_FADE_SENTINEL = 1e10;
        // For projected shapes, fraction of the half-extent that fades to zero at the edges.
        static const float HEIGHT_FADE_BAND = 0.2;
        // Divide-by-zero guard for fade / height / half-extent denominators.
        static const float DIVIDE_EPS = 1e-4;

        Texture2D<float4> _sceneDepth : register(t0);
        Texture2D<float4> _sceneInfo : register(t1);
        Texture2D<float4> _sceneNormal : register(t2);
        SamplerState _sceneSampler : register(s0);
    """;

    // Requires cbuffer fields: `depthCoefficients`, `renderTargetSize`, `pixelToUv`.
    public const string Mixin = """
        float occlusionVisibility(float sceneNdcZ, float pixelNdcZ, float2 occlusionParams)
        {
            float pixelWorldZ = depthCoefficients.y / max(pixelNdcZ + depthCoefficients.x, SKY_DEPTH_EPS);
            float sceneWorldZ = depthCoefficients.y / max(sceneNdcZ + depthCoefficients.x, SKY_DEPTH_EPS);
            float occludedAlpha = occlusionParams.x;
            float occlusionTolerance = occlusionParams.y;

            float behindMeters = max(pixelWorldZ - sceneWorldZ, 0.0);
            return behindMeters <= occlusionTolerance ? 1.0 : occludedAlpha;
        }

        float4 applyDistanceFade(float4 color, float pixelNdcZ, float2 fadeParams)
        {
            float fadeStart = fadeParams.x;
            float fadeStop = fadeParams.y;
            if (fadeStop < NO_FADE_SENTINEL)
            {
                float pixelWorldZ = depthCoefficients.y / max(pixelNdcZ + depthCoefficients.x, SKY_DEPTH_EPS);
                float range = max(fadeStop - fadeStart, DIVIDE_EPS);
                color.a *= saturate((fadeStop - pixelWorldZ) / range);
            }
            return color;
        }

        float4 applyShared(float4 color, float4 depthPos, float2 occlusionParams, float2 fadeParams)
        {
            float3 depthNdc = depthPos.xyz / depthPos.w;
            float2 uv = (depthNdc.xy * float2(0.5, -0.5) + 0.5) * renderTargetSize * pixelToUv;
            float sceneNdcZ = _sceneDepth.Sample(_sceneSampler, uv).r;
            float pixelNdcZ = depthNdc.z;

            color.a *= occlusionVisibility(sceneNdcZ, pixelNdcZ, occlusionParams);
            color = applyDistanceFade(color, pixelNdcZ, fadeParams);

            return color;
        }

        float4 applyFresnelRim(float4 color, float3 N, float3 V, float3 fresnelParams, float extraRim)
        {
            float spread = fresnelParams.x;
            float angular = (spread > 0.0) ? pow(1.0 - saturate(abs(dot(N, V))), 1.0 / spread) : 0.0;
            float rim = max(angular, extraRim);
            float rimAlpha = rim * fresnelParams.z;
            color.rgb += rimAlpha * fresnelParams.y;
            color.a = saturate(color.a + rimAlpha);
            return color;
        }
        """;

}
