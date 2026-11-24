//---------------------------------------------------------------------------
// LiquidGlassBlurVertical.hlsl
// Reconstructed 1:1 from WinUI.Composition.Hlsl shader_0.asm (BlurVertical)
// 21-tap bilinear separable Gaussian blur with MirrorUV boundary reflection
//---------------------------------------------------------------------------
#define D2D_INPUT_COUNT 1
#define D2D_INPUT0_COMPLEX
#define D2D_ENTRY main
#include "d2d1effecthelpers.hlsli"

cbuffer BlurConstants : register(b0)
{
    // BlurParams0: (BlurAmount, CenterWeight, InvTexWidth, InvTexHeight)
    float4 BlurParams0;
    float4 PairData0;       // (offset0, weight0, offset1, weight1)
    float4 PairData1;       // (offset2, weight2, offset3, weight3)
    float4 PairData2;       // (offset4, weight4, offset5, weight5)
    float4 PairData3;       // (offset6, weight6, offset7, weight7)
    float4 PairData4;       // (offset8, weight8, offset9, weight9)
};

inline float2 MirrorUV2(float2 v)
{
    float2 a = abs(v);
    float2 k = floor(a * 0.5f);
    float2 p = a - k * 2.0f;
    return (p > 1.0f) ? (2.0f - p) : p;
}

inline void AccumulateVerticalPair(
    float2 uv,
    float stepScale,
    float4 pair,
    inout float4 acc)
{
    float2 posV = MirrorUV2(uv.yy + pair.xz * stepScale);
    float2 negV = MirrorUV2(uv.yy - pair.xz * stepScale);

    float4 sPos0 = D2DSampleInput(0, float2(uv.x, posV.x));
    float4 sPos1 = D2DSampleInput(0, float2(uv.x, posV.y));
    float4 sNeg0 = D2DSampleInput(0, float2(uv.x, negV.x));
    float4 sNeg1 = D2DSampleInput(0, float2(uv.x, negV.y));

    acc += (sPos0 + sNeg0) * pair.y;
    acc += (sPos1 + sNeg1) * pair.w;
}

D2D_PS_ENTRY(main)
{
    float4 uvCoord = D2DGetInputCoordinate(0);
    float2 uv = uvCoord.xy;
    float dv = (BlurParams0.w > 1e-7f) ? BlurParams0.w : max(abs(uvCoord.w), 1.0f / 512.0f);

    float stepScale = max(BlurParams0.x, 0.0f) * dv;
    float4 acc = D2DSampleInput(0, uv) * BlurParams0.y;

    AccumulateVerticalPair(uv, stepScale, PairData0, acc);
    AccumulateVerticalPair(uv, stepScale, PairData1, acc);
    AccumulateVerticalPair(uv, stepScale, PairData2, acc);
    AccumulateVerticalPair(uv, stepScale, PairData3, acc);
    AccumulateVerticalPair(uv, stepScale, PairData4, acc);

    return acc;
}
