#define D2D_INPUT_COUNT 0
#include "d2d1effecthelpers.hlsli"

cbuffer constants : register(b0)
{
    float2 uResolution;
    float  uTime;
    float  uIntensity;
    float  uMaxAlpha;
    float  _pad0;
    float4 uColor1;
    float4 uColor2;
    float4 uColor3;
    float4 uColor4;
};

float4 sampleCyclicPalette(float t)
{
    float x = frac(t) * 4.0;
    if (x < 1.0) return lerp(uColor1, uColor2, x);
    if (x < 2.0) return lerp(uColor2, uColor3, x - 1.0);
    if (x < 3.0) return lerp(uColor3, uColor4, x - 2.0);
    return lerp(uColor4, uColor1, x - 3.0);
}

D2D_PS_ENTRY(main)
{
    float2 fragCoord = D2DGetScenePosition().xy;
    float2 res = max(uResolution, float2(1.0, 1.0));
    float2 p = fragCoord - 0.5 * res;
    float2 halfSize = 0.5 * res;
    float minDim = min(res.x, res.y);
    float cornerR = minDim * 0.085;

    float2 q = abs(p) - (halfSize - float2(cornerR, cornerR));
    float distFromEdge = -(length(max(q, float2(0.0, 0.0))) + min(max(q.x, q.y), 0.0) - cornerR);
    distFromEdge = max(distFromEdge, 0.0);

    float ang = atan2(p.y, p.x);
    float wave1 = sin(ang * 3.0 + uTime * 1.45) * cos(ang * 2.0 - uTime * 0.90);
    float wave2 = sin(ang * 5.0 - uTime * 1.85 + 1.3);
    float bandWidth = minDim * 0.24;
    float modulatedDist = max(distFromEdge - (wave1 * 0.16 + wave2 * 0.07) * bandWidth, 0.0);

    float bodyGlow = exp(-modulatedDist / max(bandWidth * 0.42, 1.0));
    float edgeRim  = exp(-distFromEdge / max(bandWidth * 0.08, 1.0)) * 0.65;
    float alpha = saturate((bodyGlow * 0.72 + edgeRim * 0.36) * uMaxAlpha * uIntensity);

    float paletteT = (ang / 6.2831853) + uTime * 0.14 + wave1 * 0.07;
    float4 tint = sampleCyclicPalette(paletteT);
    return float4(tint.rgb * alpha, alpha);
}
