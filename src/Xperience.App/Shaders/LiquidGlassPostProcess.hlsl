//---------------------------------------------------------------------------
// LiquidGlassPostProcess.hlsl
// Reconstructed 1:1 from WinUI.Composition.Hlsl shader_43.asm (PSBody)
// Applies Bloom blend (blurred vs raw), Exposure, Temperature, Brightness,
// Contrast, Vibrance, and Saturation in premultiplied alpha space.
//---------------------------------------------------------------------------
#define D2D_INPUT_COUNT 2
#define D2D_INPUT0_SIMPLE
#define D2D_INPUT1_SIMPLE
#define D2D_ENTRY main
#include "d2d1effecthelpers.hlsli"

cbuffer PostProcessingConstants : register(b0)
{
    // PostParams0 (cb0[0]): (BloomAmount, Brightness, Temperature, Vibrance)
    float4 PostParams0;
    // PostParams1 (cb0[1]): (Saturation, Contrast, Exposure, Reserved)
    float4 PostParams1;
};

D2D_PS_ENTRY(main)
{
    float4 blurred = D2DGetInput(0);
    float4 raw = D2DGetInput(1);

    float bloomAmount = PostParams0.x;
    float brightness  = PostParams0.y;
    float temperature = PostParams0.z;
    float vibrance    = PostParams0.w;

    float saturation  = PostParams1.x;
    float contrast    = PostParams1.y;
    float exposure    = PostParams1.z;

    // Lines 50-56: Lerp raw -> blurred by saturate(BloomAmount) and unpremultiply alpha
    float bloom = saturate(bloomAmount);
    float4 c = lerp(raw, blurred, bloom);
    float a = saturate(c.a);
    float3 rgb = (a > 0.00001f) ? (c.rgb / a) : float3(0.0f, 0.0f, 0.0f);

    // Lines 57-58: Exponential exposure
    rgb *= exp2(exposure);

    // Lines 59-60: Color temperature + brightness
    float3 tempShift = float3(1.0f, 1.0f, 1.0f) + temperature * float3(0.25f, 0.10f, -0.25f);
    rgb = rgb * tempShift + brightness;

    // Lines 61-62: Contrast around 0.5 pivot
    rgb = (rgb - 0.5f) * contrast + 0.5f;

    // Lines 63-75: Vibrance + Saturation (Rec.601 luma weights 0.299, 0.587, 0.114)
    float minC = min(rgb.r, min(rgb.g, rgb.b));
    float maxC = max(rgb.r, max(rgb.g, rgb.b));
    float curSat = saturate((maxC - minC) / max(abs(maxC), 0.00001f));
    float satBoost = (1.0f + vibrance * (1.0f - curSat)) * saturation;
    float luma = dot(rgb, float3(0.299f, 0.587f, 0.114f));
    rgb = saturate(luma + (rgb - luma) * satBoost);

    // Lines 76-78: Re-premultiply alpha
    return float4(rgb * a, a);
}
