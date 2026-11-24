//---------------------------------------------------------------------------
// LiquidGlassMain.hlsl
// Reconstructed 1:1 from WinUI.Composition.Hlsl shader_28.asm (PSBody, 957 slots)
// Complete GPU optical engine:
// - Screen-space derivative (ddx/ddy) element size & rounded-box SDF
// - Continuous G2 Superellipse corner normals (ShapeRoundness p in [2, 8])
// - 4 3D Surface Profiles (0: ConvexSquircle, 1: ConvexCircle, 2: Concave, 3: Lip)
// - True 3D Snell's Law refraction through BezelWidth + GlassThickness
// - Interactive Pointer radial/directional displacement & center Magnification
// - 8-Tap directional caustic differential filter along curvature gradient
// - 7-Tap R/G/B spectral chromatic dispersion with analytical Ray-Box UV clipping
// - Specular rim with local SpecularSaturation boost + dynamic PointerLightInfluence
// - 5th-order peripheral RefFresnel + 5th-order anisotropic Glare
//---------------------------------------------------------------------------
#define D2D_INPUT_COUNT 1
#define D2D_INPUT0_COMPLEX
#define D2D_ENTRY main
#include "d2d1effecthelpers.hlsli"

cbuffer LiquidGlassConstants : register(b0)
{
    // cb0[0]: (BorderThickness, CornerRadius, RefractionStrength, BezelWidth)
    float4 MaterialParams0;
    // cb0[1]: (HighlightStrength, EdgeSoftness, DispersionStrength, MaterialOpacity)
    float4 MaterialParams1;
    // cb0[2]: (GlassThickness, RefractiveIndex, TintOpacity, Saturation)
    float4 MaterialParams2;
    // cb0[3]: (LightAngleRad, SurfaceProfile, MagnificationStrength, HighlightSharpness)
    float4 MaterialParams3;
    // cb0[4]: (TintRed, TintGreen, TintBlue, InnerShadowStrength)
    float4 MaterialParams4;
    // cb0[5]: (SpecularSaturation, SpecularWidth, Contrast, Exposure)
    float4 MaterialParams5;
    // cb0[6]: (PointerUV.x, PointerUV.y, PointerRadiusNorm, PointerWeight)
    float4 MaterialParams6;
    // cb0[7]: (PointerDelta.x, PointerDelta.y, PointerEdgeFalloffNorm, PointerActive)
    float4 MaterialParams7;
    // cb0[8]: (PointerRadialDisp, PointerLightInfluence, PointerDirDisp, RefractionMultiplier)
    float4 MaterialParams8;
    // cb0[9]: (ShapeRoundness, RasterizationScale, RenderMode, Reserved)
    float4 MaterialParams9;
    // cb0[10]: (DispersionRange, RefFresnelRange, RefFresnelHardness, RefFresnelFactor)
    float4 MaterialParams10;
    // cb0[11]: (GlareRange, GlareHardness, GlareFactor, GlareConvergence)
    float4 MaterialParams11;
    // cb0[12]: (GlareOppositeFactor, GlareAngleDeg, ShapeRadius, Magnification)
    float4 MaterialParams12;
    // cb0[13]: (MinU, MinV, MaxU, MaxV) -> v2 (samplerData UV sub-rect in backdrop texture)
    float4 MaterialParams13;
    // cb0[14]: (ElementWidthPx, ElementHeightPx, InvBackdropWidthPx, InvBackdropHeightPx)
    float4 MaterialParams14;
};

inline float SmoothStep01(float x)
{
    float t = saturate(x);
    return t * t * (3.0f - 2.0f * t);
}

inline float SmootherStep01(float x)
{
    float t = saturate(x);
    return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
}

//---------------------------------------------------------------------------
// Surface height h(t) for t = clamp(insideDist / BezelWidth, 0, 1)
// Reconstructed from shader_28.asm lines 197-225
//---------------------------------------------------------------------------
float EvaluateSurfaceHeight(float t, float profile)
{
    if (profile < 0.5f)
    {
        // 0: ConvexSquircle -> (1 - (1 - t)^4)^(1/4)
        float u = 1.0f - t;
        float u2 = u * u;
        return pow(max(1.0f - u2 * u2, 1e-7f), 0.25f);
    }
    else
    {
        float u = 1.0f - t;
        float circ = sqrt(max(1.0f - u * u, 0.0f));
        if (profile < 1.5f)
        {
            // 1: ConvexCircle -> sqrt(1 - (1 - t)^2)
            return circ;
        }
        else if (profile < 2.5f)
        {
            // 2: Concave -> 1 - sqrt(1 - (1 - t)^2)
            return 1.0f - circ;
        }
        else
        {
            // 3: Lip -> quintic blend between outer squircle ridge and inner concave dip
            float v = 1.0f - 2.0f * t;
            float v2 = v * v;
            float lipOuter = pow(max(1.0f - v2 * v2, 1e-7f), 0.25f);
            float lipInner = 1.10f - circ;
            float s = SmootherStep01(t);
            return lerp(lipOuter, lipInner, s);
        }
    }
}

//---------------------------------------------------------------------------
// 3D Snell's Law Refraction Displacement
// Reconstructed from shader_28.asm lines 226-312 & 514-622
//---------------------------------------------------------------------------
float EvaluateSnellDisplacement(
    float t,
    float bezelPx,
    float glassThicknessPx,
    float invEta,
    float profile)
{
    float h0 = EvaluateSurfaceHeight(t, profile);
    float eps = (t < 0.9999f) ? 0.0001f : -0.0001f;
    float t1 = saturate(t + eps);
    float h1 = EvaluateSurfaceHeight(t1, profile);
    float dhdt = (h1 - h0) / eps;

    float invLen = rsqrt(dhdt * dhdt + 1.0f);
    float nr = -dhdt * invLen;
    float nz = invLen;

    float sin2_i = 1.0f - nz * nz;
    float cos2_t = 1.0f - (invEta * invEta) * sin2_i;
    if (cos2_t <= 0.0f)
    {
        return 0.0f;
    }

    float cos_t = sqrt(cos2_t);
    float k = cos_t - invEta * nz;
    float tr = nr * (-k);
    float tz = invEta - k * nz;
    if (abs(tz) <= 0.00001f)
    {
        return 0.0f;
    }

    float totalDepth = h0 * bezelPx + glassThicknessPx;
    float disp = (totalDepth / tz) * tr;

    if (profile >= 1.5f && profile < 2.5f)
    {
        disp = abs(disp);
    }

    return disp;
}

//---------------------------------------------------------------------------
// Analytical Ray-Box UV Clipping (shader_28.asm lines 632-649)
// Prevents refracted / dispersed rays from sampling outside valid UV bounds.
//---------------------------------------------------------------------------
float2 ClipRayToUvBox(
    float2 anchorUV,
    float2 deltaUV,
    float2 boxMinUV,
    float2 boxMaxUV)
{
    float2 posRoom = boxMaxUV - anchorUV;
    float2 negRoom = boxMinUV - anchorUV;

    float tx = 1.0f;
    if (deltaUV.x > 1e-7f)
        tx = min(1.0f, posRoom.x / deltaUV.x);
    else if (deltaUV.x < -1e-7f)
        tx = min(1.0f, negRoom.x / deltaUV.x);

    float ty = tx;
    if (deltaUV.y > 1e-7f)
        ty = min(tx, posRoom.y / deltaUV.y);
    else if (deltaUV.y < -1e-7f)
        ty = min(tx, negRoom.y / deltaUV.y);

    float tClip = max(ty, 0.0f);
    return clamp(anchorUV + deltaUV * tClip, boxMinUV, boxMaxUV);
}

//---------------------------------------------------------------------------
// Unpremultiplied sample helper (shader_28.asm lines 507-510)
//---------------------------------------------------------------------------
float4 SampleUnpremultiplied(float2 sampleUV, float3 fallbackRgb)
{
    float4 s = D2DSampleInput(0, sampleUV);
    float3 rgb = (s.a > 0.00001f) ? (s.rgb / s.a) : fallbackRgb;
    return float4(rgb, s.a);
}

D2D_PS_ENTRY(main)
{
    // 1. Extract scale & base geometric parameters (lines 92-97)
    float scale = clamp(MaterialParams9.y, 0.01f, 16.0f);
    float borderThickness = scale * MaterialParams0.x;
    float cornerRadiusParam = scale * MaterialParams0.y;
    float edgeSoftness = scale * MaterialParams1.y;
    float surfaceProfile = clamp(MaterialParams3.y, 0.0f, 3.0f);
    float renderMode = MaterialParams9.z;

    // 2. Resolve UV coordinates, texel delta (v1.zw), and samplerData UvRect (v2)
    float4 d2dUV = D2DGetInputCoordinate(0);
    float4 scenePos = D2DGetScenePosition();

    float2 elemSizeHint = MaterialParams14.xy;
    float4 samplerData = MaterialParams13;
    float2 minUV = min(samplerData.xy, samplerData.zw);
    float2 maxUV = max(samplerData.xy, samplerData.zw);
    float2 spanUV = maxUV - minUV;
    bool hasValidRect = (spanUV.x > 0.000001f) && (spanUV.y > 0.000001f);

    float2 localUV;
    float2 uv;
    if (elemSizeHint.x > 1.0f && elemSizeHint.y > 1.0f)
    {
        localUV = saturate(scenePos.xy / elemSizeHint);
        uv = hasValidRect ? lerp(minUV, maxUV, localUV) : localUV;
    }
    else
    {
        uv = d2dUV.xy;
        localUV = hasValidRect ? ((uv - minUV) / spanUV) : uv;
    }

    // Screen-space derivative size calculation (lines 107-118)
    float2 dUVdx = ddx(localUV);
    float2 dUVdy = ddy(localUV);
    float2 gradLen = float2(
        sqrt(dot(float2(dUVdx.x, dUVdy.x), float2(dUVdx.x, dUVdy.x))),
        sqrt(dot(float2(dUVdx.y, dUVdy.y), float2(dUVdx.y, dUVdy.y)))
    );
    float2 sizePx = max(1.0f / max(gradLen, float2(0.000001f, 0.000001f)), float2(1.0f, 1.0f));
    if (elemSizeHint.x > 1.0f && elemSizeHint.y > 1.0f)
    {
        sizePx = elemSizeHint;
    }

    float2 pixelPos = localUV * sizePx;
    float2 halfSize = sizePx * 0.5f;
    float2 p = pixelPos - halfSize;

    // 3. Corner radius & Rounded Box SDF (lines 119-162)
    bool useShapeRadius = (MaterialParams12.z >= 0.0f);
    float minHalf = min(halfSize.x, halfSize.y);
    float shapeRadiusNorm = saturate(MaterialParams12.z);
    float prelimCornerR = useShapeRadius ? (shapeRadiusNorm * minHalf) : cornerRadiusParam;

    bool isSquircleNoBorder = (surfaceProfile < 0.5f) && (borderThickness <= 0.0001f);
    bool isFullCapsule = isSquircleNoBorder && (prelimCornerR >= (max(minHalf, 1.0f) - 0.5f));
    float insetBias = isFullCapsule ? 0.0f : -1.0f;

    float2 effHalfSize = max(sizePx * 0.5f + insetBias, float2(1.0f, 1.0f));
    float maxCornerR = min(effHalfSize.x, effHalfSize.y);
    float cornerR = useShapeRadius
        ? (shapeRadiusNorm * maxCornerR)
        : clamp(cornerRadiusParam, 0.0f, maxCornerR);

    float2 boxInner = effHalfSize - cornerR;
    float2 q = abs(p) - boxInner;
    float sd = length(max(q, 0.0f)) + min(max(q.x, q.y), 0.0f) - cornerR;

    float2 dSd = float2(ddx(sd), ddy(sd));
    float sdGrad = max(length(dSd), 0.5f);
    float aaWidth = max(edgeSoftness, sdGrad * 0.5f);
    float shapeAlpha = saturate(MaterialParams1.w) * (1.0f - SmoothStep01(sd / aaWidth));

    if (shapeAlpha <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    // 4. Unpack optical material parameters (lines 164-196)
    float bezelPx = clamp(scale * MaterialParams0.w, 1.0f, max(maxCornerR - 0.5f, 1.0f));
    float glassThicknessPx = max(scale * MaterialParams2.x, 0.0f);
    float tintOpacity = saturate(MaterialParams2.z);
    float refractiveIndex = max(MaterialParams2.y, 1.0f);
    float invEta = 1.0f / refractiveIndex;
    float saturation = max(MaterialParams2.w, 0.0f);
    float magnificationStrength = max(MaterialParams3.z, 0.0f);
    float highlightSharpness = max(MaterialParams3.w, 0.25f);
    float4 tintAndShadow = saturate(MaterialParams4);
    float3 tintColor = tintAndShadow.rgb;
    float innerShadowStrength = tintAndShadow.w;

    float specularSaturation = max(MaterialParams5.x, 0.0f);
    float specularWidthPx = scale * max(MaterialParams5.y, 0.25f);
    float contrast = max(MaterialParams5.z, 0.0f);
    float exposure = clamp(MaterialParams5.w, -4.0f, 4.0f);

    float2 pointerRadiusAndWeight = max(MaterialParams6.zw, float2(0.0f, 0.0f));
    float pointerEdgeFalloffNorm = max(MaterialParams7.z, 0.0f);
    float pointerActive = saturate(MaterialParams7.w);
    float3 pointerDispAndRefMul = max(MaterialParams8.xzw, float3(0.0f, 0.0f, 0.0f));
    float2 dispAndFresnelRange = max(MaterialParams10.xy, float2(0.001f, 0.0001f));
    float magnification = max(MaterialParams12.w, 1.0f);
    float maxDimPx = max(sizePx.x, sizePx.y);

    float2 texelDelta = (MaterialParams14.z > 1e-7f && MaterialParams14.w > 1e-7f)
        ? MaterialParams14.zw
        : max(abs(d2dUV.zw), float2(0.000001f, 0.000001f));

    float2 pointerPosPx = sizePx * MaterialParams6.xy;
    float2 pointerDeltaPx = sizePx * MaterialParams7.xy;
    float pointerRadiusPx = pointerRadiusAndWeight.x * maxDimPx;
    float pointerEdgeFalloffPx = pointerEdgeFalloffNorm * maxDimPx;

    float2 centerUV = hasValidRect ? ((minUV + maxUV) * 0.5f) : float2(0.5f, 0.5f);
    float2 magUV = centerUV + (uv - centerUV) / magnification;

    float insideDist = max(-sd, 0.0f);
    float tBezel = min(insideDist / bezelPx, 1.0f);

    // 5. Evaluate 3D Snell's Law refraction at current depth tBezel (lines 197-312)
    float snellDisp0 = EvaluateSnellDisplacement(tBezel, bezelPx, glassThicknessPx, invEta, surfaceProfile);
    float refScale = max(MaterialParams0.z, 0.0f) * (1.0f / 24.0f);

    // Soft interior anti-aliasing factor (lines 313-319)
    float innerFadeWidth = max(max(sdGrad, edgeSoftness), 0.75f);
    float innerMask = 1.0f - SmoothStep01(sd / innerFadeWidth);

    // 6. Superellipse 2D Normal & Corner Pinch Attenuation (lines 320-401)
    bool hasRoundedCorner = (cornerR > 0.5f) && (cornerR < (maxCornerR - 0.5f)) && (!isFullCapsule);
    float rBlend = hasRoundedCorner ? min(maxCornerR, max(cornerR, bezelPx * 1.25f)) : cornerR;
    float2 signP = (p < 0.0f) ? float2(-1.0f, -1.0f) : float2(1.0f, 1.0f);
    float2 qBlend = abs(p) - (effHalfSize - rBlend);

    float2 dirAbs;
    if (max(qBlend.x, qBlend.y) > 0.0f)
    {
        float roundness = clamp(MaterialParams9.x, 2.0f, 8.0f);
        float2 qPos = max(qBlend, 0.0f);
        float2 u = qPos / max(rBlend, 1.0f);
        float2 u2 = u * u;
        float2 u3 = u * u2;
        float2 u4 = u2 * u2;

        float w24 = min((roundness - 2.0f) * 0.5f, 1.0f);
        float w48 = max((roundness - 4.0f) * 0.25f, 0.0f);
        float2 n24 = u + w24 * (u * u2 - u);
        float2 n48 = u3 + w48 * (u3 * u4 - u3);
        float2 nSuper = (roundness <= 4.0f) ? n24 : n48;

        float lenSuper = length(nSuper);
        float2 axisFallback = (qBlend.y < qBlend.x) ? float2(1.0f, 0.0f) : float2(0.0f, 1.0f);
        float2 unitSuper = (lenSuper > 0.000001f) ? (nSuper / lenSuper) : axisFallback;

        float lenCirc = length(qPos);
        float2 unitCirc = (lenCirc > 0.00001f) ? (qPos / lenCirc) : axisFallback;
        dirAbs = hasRoundedCorner ? unitSuper : unitCirc;
    }
    else
    {
        float seamWidth = hasRoundedCorner ? clamp(bezelPx * 0.12f, 1.0f, 4.0f) : 0.5f;
        float seamT = SmoothStep01((qBlend.x - qBlend.y + seamWidth) / (2.0f * seamWidth));
        float2 unitSeam = normalize(float2(seamT, 1.0f - seamT));
        float2 axisEdge = (qBlend.y < qBlend.x) ? float2(1.0f, 0.0f) : float2(0.0f, 1.0f);
        dirAbs = hasRoundedCorner ? unitSeam : axisEdge;
    }

    float2 normal2D = signP * dirAbs;

    // Corner pinch attenuation factors (lines 391-401)
    float2 qCornerPos = max(q, 0.0f);
    float minCornerAxis = min(qCornerPos.x, qCornerPos.y);
    float cornerRatio = min((2.0f * minCornerAxis) / max(cornerR, 1.0f), 1.0f);
    float cornerWeight = (cornerR > 0.0001f && hasRoundedCorner) ? SmoothStep01(cornerRatio) : 0.0f;
    float cornerRefMul  = 1.0f + cornerWeight * 0.28f;
    float cornerDispMul = 1.0f + cornerWeight * 0.85f;
    float cornerSpecMul = 1.0f + cornerWeight * 0.18f;

    float maxRefDispPx = max(maxDimPx * 0.48f, 1.0f);
    float refDispPx = clamp(snellDisp0 * refScale * pointerDispAndRefMul.z * cornerRefMul, -maxRefDispPx, maxRefDispPx) * innerMask;

    // RenderMode == 3.0: Output Superellipse SDF Alpha Mask (ShapeRoundness + EdgeSoftness)
    if (renderMode >= 2.5f)
    {
        return float4(shapeAlpha, shapeAlpha, shapeAlpha, shapeAlpha);
    }

    // RenderMode == 2.0: Output Snell 3D Meniscus Profile & Superellipse SDF Alpha Mask for Live Composition
    if (renderMode >= 1.5f)
    {
        float rimProfile = saturate(abs(snellDisp0 * refScale) / max(bezelPx * 0.65f, 1.0f));
        float maskVal = rimProfile * shapeAlpha;
        return float4(maskVal, maskVal, maskVal, maskVal);
    }

    // 7. Interactive Pointer Displacement & Center Magnification (lines 410-470)
    float2 ptrLocalP = pointerPosPx - halfSize;
    float2 ptrQ = abs(ptrLocalP) - boxInner;
    float ptrSd = length(max(ptrQ, 0.0f)) + min(max(ptrQ.x, ptrQ.y), 0.0f) - cornerR;

    float ptrInsideWeight = (ptrSd <= 0.0f) ? 1.0f : 0.0f;
    if (ptrSd > 0.0f && pointerEdgeFalloffPx > 0.0001f)
    {
        ptrInsideWeight = 1.0f - SmoothStep01(ptrSd / pointerEdgeFalloffPx);
    }

    float2 toPixelFromPtr = pixelPos - pointerPosPx;
    float distToPtr = length(toPixelFromPtr);
    float ptrRadialFalloff = (pointerRadiusPx > 0.0001f)
        ? (1.0f - SmoothStep01(distToPtr / pointerRadiusPx))
        : 0.0f;

    float ptrInfluence = saturate(ptrInsideWeight * ptrRadialFalloff) * pointerRadiusAndWeight.y * pointerActive;
    float2 ptrRadialDir = (distToPtr > 0.0001f) ? (toPixelFromPtr / distToPtr) : float2(0.0f, 0.0f);

    float ptrDeltaLen = length(pointerDeltaPx);
    float2 ptrMoveDir = (ptrDeltaLen > 0.0001f) ? (-pointerDeltaPx / ptrDeltaLen) : float2(0.0f, 0.0f);
    float ptrMoveNorm = min(ptrDeltaLen / max(maxDimPx * 7.0f, 1e-4f), 1.0f);

    float2 totalDispPx = -normal2D * refDispPx
        + ptrRadialDir * (ptrInfluence * pointerDispAndRefMul.x * innerMask)
        + ptrMoveDir * (ptrMoveNorm * ptrInfluence * pointerDispAndRefMul.y * innerMask);

    float2 refractedUV = magUV + totalDispPx * texelDelta;

    // Additional center magnification pull (lines 462-470)
    float maxHalf = max(max(halfSize.x, halfSize.y), 1.0f);
    float2 magPullUV = -((totalDispPx + p) / maxHalf) * texelDelta * magnificationStrength * innerMask * 0.498039f;
    refractedUV += magPullUV;

    // 8. Chromatic Dispersion Magnitude & UV Clip Box (lines 470-506)
    float dispFalloff = 1.0f - SmoothStep01((tBezel - 0.18f * dispAndFresnelRange.x) / (0.82f * dispAndFresnelRange.x));
    float dispBasePx = min(abs(refDispPx) * 0.04f, scale * 1.50f) + scale * 0.35f;
    float dispTotalPx = min(dispBasePx * dispFalloff * MaterialParams1.z * innerMask * cornerDispMul, scale * 8.0f);
    float2 dispVecUV = dispTotalPx * (normal2D * texelDelta);

    float2 halfTexel = max(texelDelta * 0.5f, float2(0.000001f, 0.000001f));
    float2 defaultMin = min(halfTexel, 1.0f - halfTexel);
    float2 defaultMax = max(halfTexel, 1.0f - halfTexel);
    float2 boxMinUV = hasValidRect ? max(minUV + halfTexel, defaultMin) : defaultMin;
    float2 boxMaxUV = hasValidRect ? min(maxUV - halfTexel, defaultMax) : defaultMax;

    float2 anchorUV = clamp(uv, boxMinUV, boxMaxUV);
    float2 deltaCenterUV = refractedUV - uv;
    float2 centerSampleUV = clamp(uv + deltaCenterUV, boxMinUV, boxMaxUV);

    float4 baseSample = SampleUnpremultiplied(centerSampleUV, tintColor);
    float3 baseRgb = baseSample.rgb;

    // 9. 8-Tap Directional Caustic Differential Filter & 7-Tap Spectral Chromatic Dispersion (lines 511-863)
    float tBezelNext = min((insideDist + 1.0f) / bezelPx, 1.0f);
    float snellDisp1 = EvaluateSnellDisplacement(tBezelNext, bezelPx, glassThicknessPx, invEta, surfaceProfile);
    float refDispNextPx = clamp(snellDisp1 * refScale * pointerDispAndRefMul.z * cornerRefMul, -maxRefDispPx, maxRefDispPx) * innerMask;
    float diffDispPx = min(abs(refDispNextPx - refDispPx), scale * 8.0f);
    float2 diffVecUV = diffDispPx * (normal2D * texelDelta);

    // 8-tap caustic differential integration for Green/Blue channels (lines 631-759)
    float2 tapG0 = ClipRayToUvBox(anchorUV, deltaCenterUV - diffVecUV * 0.500000f, boxMinUV, boxMaxUV);
    float2 tapG1 = ClipRayToUvBox(anchorUV, deltaCenterUV - diffVecUV * 0.357143f, boxMinUV, boxMaxUV);
    float2 tapG2 = ClipRayToUvBox(anchorUV, deltaCenterUV - diffVecUV * 0.214286f, boxMinUV, boxMaxUV);
    float2 tapG3 = ClipRayToUvBox(anchorUV, deltaCenterUV - diffVecUV * 0.071429f, boxMinUV, boxMaxUV);
    float2 tapG4 = ClipRayToUvBox(anchorUV, deltaCenterUV + diffVecUV * 0.071429f, boxMinUV, boxMaxUV);
    float2 tapG5 = ClipRayToUvBox(anchorUV, deltaCenterUV + diffVecUV * 0.214286f, boxMinUV, boxMaxUV);
    float2 tapG6 = ClipRayToUvBox(anchorUV, deltaCenterUV + diffVecUV * 0.357143f, boxMinUV, boxMaxUV);
    float2 tapG7 = ClipRayToUvBox(anchorUV, deltaCenterUV + diffVecUV * 0.500000f, boxMinUV, boxMaxUV);

    float4 sG0 = SampleUnpremultiplied(tapG0, baseRgb);
    float4 sG1 = SampleUnpremultiplied(tapG1, baseRgb);
    float4 sG2 = SampleUnpremultiplied(tapG2, baseRgb);
    float4 sG3 = SampleUnpremultiplied(tapG3, baseRgb);
    float4 sG4 = SampleUnpremultiplied(tapG4, baseRgb);
    float4 sG5 = SampleUnpremultiplied(tapG5, baseRgb);
    float4 sG6 = SampleUnpremultiplied(tapG6, baseRgb);
    float4 sG7 = SampleUnpremultiplied(tapG7, baseRgb);

    float2 gbMid = (sG0.gb + sG1.gb + sG2.gb + sG3.gb + sG4.gb + sG5.gb + sG6.gb + sG7.gb) * 0.125f;

    // 6 additional spectral dispersion taps along normal (-1, -2/3, -1/3, +1/3, +2/3, +1) (lines 760-863)
    float2 tapD_neg3 = ClipRayToUvBox(anchorUV, deltaCenterUV - dispVecUV * 1.000000f, boxMinUV, boxMaxUV);
    float2 tapD_neg2 = ClipRayToUvBox(anchorUV, deltaCenterUV - dispVecUV * 0.666667f, boxMinUV, boxMaxUV);
    float2 tapD_neg1 = ClipRayToUvBox(anchorUV, deltaCenterUV - dispVecUV * 0.333333f, boxMinUV, boxMaxUV);
    float2 tapD_pos1 = ClipRayToUvBox(anchorUV, deltaCenterUV + dispVecUV * 0.333333f, boxMinUV, boxMaxUV);
    float2 tapD_pos2 = ClipRayToUvBox(anchorUV, deltaCenterUV + dispVecUV * 0.666667f, boxMinUV, boxMaxUV);
    float2 tapD_pos3 = ClipRayToUvBox(anchorUV, deltaCenterUV + dispVecUV * 1.000000f, boxMinUV, boxMaxUV);

    float3 sD_neg3 = SampleUnpremultiplied(tapD_neg3, baseRgb).rgb;
    float3 sD_neg2 = SampleUnpremultiplied(tapD_neg2, baseRgb).rgb;
    float3 sD_neg1 = SampleUnpremultiplied(tapD_neg1, baseRgb).rgb;
    float3 sD_pos1 = SampleUnpremultiplied(tapD_pos1, baseRgb).rgb;
    float3 sD_pos2 = SampleUnpremultiplied(tapD_pos2, baseRgb).rgb;
    float3 sD_pos3 = SampleUnpremultiplied(tapD_pos3, baseRgb).rgb;

    float3 refractedRgb;
    refractedRgb.r = (sD_neg3.r + sD_neg2.r + sD_neg1.r) * 0.28571429f + sD_pos3.r * 0.14285714f;
    refractedRgb.g = (gbMid.x + sD_neg1.g + sD_pos1.g) * 0.28571429f + sD_neg2.g * 0.14285714f;
    refractedRgb.b = (gbMid.y + sD_pos1.b + sD_pos2.b + sD_pos3.b) * 0.25f;

    // 10. Color Grading (Rec.709 Saturation, Exposure, Contrast) & Tint (lines 864-871)
    float luma709 = dot(refractedRgb, float3(0.2126f, 0.7152f, 0.0722f));
    float3 gradedRgb = luma709 + (refractedRgb - luma709) * saturation;
    gradedRgb = (gradedRgb * exp2(exposure) - 0.5f) * contrast + 0.5f;
    float3 tintedRgb = lerp(gradedRgb, tintColor, tintOpacity);

    // 11. Inner Shadow & Physical Border Rim (lines 872-891)
    float innerShadowRange = max(bezelPx * 0.65f, 1.0f);
    float innerShadowBand = 1.0f - SmoothStep01(insideDist / innerShadowRange);
    float innerShadowMul = 1.0f - innerShadowBand * innerShadowStrength;
    float3 shadowedRgb = tintedRgb * innerShadowMul;

    float borderBand = (borderThickness > 0.0001f)
        ? (1.0f - SmoothStep01((insideDist - borderThickness) / aaWidth))
        : 0.0f;
    float borderWeight = borderBand * MaterialParams1.x * 0.20f;
    float3 borderedRgb = lerp(shadowedRgb, tintColor, borderWeight);

    // 12. Directional Specular Rim & Dynamic Pointer Specular Lighting (lines 892-971)
    float2 lightDir = normalize(float2(cos(MaterialParams3.x), sin(MaterialParams3.x)));
    float specWidth = max(specularWidthPx, 0.25f);
    float specDistNorm = insideDist / specWidth;
    float specRadialCirc = sqrt(max(1.0f - (1.0f - specDistNorm) * (1.0f - specDistNorm), 0.0f));
    float specOuterFade = 1.0f - SmoothStep01((specDistNorm - 2.0f) / max(aaWidth / specWidth, 0.25f));
    float specRadialMask = max(specRadialCirc * specOuterFade, 0.0f);

    float ndotl = min(abs(dot(normal2D, lightDir)), 1.0f);
    float dirSpec = saturate(pow(max(ndotl, 1e-6f), highlightSharpness) * specRadialMask * innerMask * cornerSpecMul);

    // Pointer dynamic specular lighting (lines 921-959)
    float2 toPtr = pointerPosPx - pixelPos;
    float toPtrLen = length(toPtr);
    float ptrSpec = 0.0f;
    if (toPtrLen > 0.0001f && ptrInfluence > 0.0f)
    {
        float pointerLightInf = max(MaterialParams8.y, 0.0f);
        float2 toPtrDir = toPtr / toPtrLen;
        float ndotp = dot(normal2D, toPtrDir);
        float ptrFacing = (ptrSd <= 0.0f) ? saturate(-ndotp) : saturate(ndotp);
        float ptrAngular = pow(max(ptrFacing * 0.85f + 0.15f, 1e-6f), max(highlightSharpness * 0.85f, 0.50f));

        float pClamped = min(ptrInfluence, 1.0f);
        float pQuintic = SmootherStep01(pClamped);
        float pShaped = pQuintic + (sqrt(max(pQuintic, 0.0f)) - pQuintic) * 0.28f;

        ptrSpec = saturate(specRadialMask * ptrAngular * pShaped)
            * pointerLightInf
            * (1.0f + ptrInsideWeight * 0.18f)
            * (1.0f + cornerWeight * 0.12f)
            * innerMask;
    }

    // SpecularSaturation boost under specular highlight + specular additive blend (lines 960-971)
    float dirSpecSq = dirSpec * dirSpec;
    float curLuma = dot(borderedRgb, float3(0.2126f, 0.7152f, 0.0722f));
    float3 specSaturatedRgb = curLuma + (borderedRgb - curLuma) * specularSaturation;
    float3 postSpecRgb = lerp(borderedRgb, specSaturatedRgb, dirSpecSq);

    float specBlend = saturate(dirSpecSq * MaterialParams1.x);
    postSpecRgb = lerp(postSpecRgb, float3(dirSpec, dirSpec, dirSpec), specBlend);
    postSpecRgb = lerp(postSpecRgb, float3(1.0f, 1.0f, 1.0f), saturate(ptrSpec));

    // 13. 5th-Order Peripheral RefFresnel (lines 972-1001)
    float fresnelMetric = 500.0f / dispAndFresnelRange.y;
    float fresnelScale = fresnelMetric * fresnelMetric;
    float fresnelBase = 1.0f - (fresnelScale * insideDist) / (scale * 1500.0f) + MaterialParams10.z * 0.01f;
    float fresnelClamped = clamp(fresnelBase, 0.0f, 1.0f);
    float fresnelPow2 = fresnelClamped * fresnelClamped;
    float fresnelPow5 = min(fresnelPow2 * fresnelPow2 * fresnelClamped, 1.0f);
    float fresnelSmooth = SmoothStep01(scale * MaterialParams10.y * 0.5f);
    float fresnelIntensity = fresnelPow5 * fresnelSmooth * saturate(MaterialParams10.w * 0.01f);

    float3 fresnelTint = lerp(float3(1.0f, 1.0f, 1.0f), tintColor, tintOpacity * 0.5f);
    float3 fresnelTarget = lerp(fresnelTint, float3(1.0f, 1.0f, 1.0f), fresnelIntensity * 0.20f);
    postSpecRgb = lerp(postSpecRgb, fresnelTarget, fresnelIntensity * 0.70f);

    // 14. 5th-Order Anisotropic Glare (lines 1002-1044)
    float glareOppFactor = saturate(MaterialParams12.x * 0.01f);
    float glareAngleRad = MaterialParams12.y * 0.0174532925f;
    float2 glareDir = float2(cos(glareAngleRad), sin(glareAngleRad));
    float ndotg = dot(normal2D, glareDir);
    float glareSideMul = (ndotg < 0.0f) ? glareOppFactor : 1.0f;
    float glareRawAngular = max(glareSideMul * (ndotg * ndotg) * MaterialParams11.z * 0.012f, 0.0f);
    float glareExp = saturate(MaterialParams11.w * 0.01f) * 2.0f + 0.10f;
    float glareAngular = min(pow(max(glareRawAngular, 1e-7f), glareExp), 1.0f);

    float glareRangeMetric = 500.0f / max(MaterialParams11.x, 0.0001f);
    float glareRangeScale = glareRangeMetric * glareRangeMetric;
    float glareRadialBase = 1.0f - (glareRangeScale * insideDist) / (scale * 1500.0f) + MaterialParams11.y * 0.01f;
    float glareRadialClamped = clamp(glareRadialBase, 0.0f, 1.0f);
    float glareRadialPow2 = glareRadialClamped * glareRadialClamped;
    float glareRadialPow5 = min(glareRadialPow2 * glareRadialPow2 * glareRadialClamped, 1.0f);
    float glareSmooth = SmoothStep01(scale * MaterialParams11.x * 0.5f);
    float glareTotal = min(glareSmooth * glareRadialPow5 * glareAngular, 1.0f);

    float3 finalRgb = saturate(lerp(postSpecRgb, float3(1.0f, 1.0f, 1.0f), glareTotal));

    // RenderMode == 1.0: Exact Closed-Form Premultiplied Optical Overlay Mode
    // Satisfies SourceOver(E6, 1 - T6, tintedRgb) == finalRgb (lines 872-1044 of shader_28.asm)
    if (renderMode >= 0.5f && renderMode < 1.5f)
    {
        float s1 = saturate(innerShadowBand * innerShadowStrength);
        float b2 = saturate(borderWeight);
        float k3 = specBlend;
        float k4 = saturate(ptrSpec);
        float k5 = saturate(fresnelIntensity * 0.70f);
        float k6 = saturate(glareTotal);

        // Step 1: Inner shadow (lerp toward black by s1)
        float T = 1.0f - s1;
        float3 E = float3(0.0f, 0.0f, 0.0f);

        // Step 2: Physical border rim (lerp toward tintColor by b2)
        T *= (1.0f - b2);
        E = E * (1.0f - b2) + tintColor * b2;

        // Step 3: Directional specular (lerp toward dirSpec by k3, plus SpecularSaturation boost)
        float3 specColor = saturate(float3(dirSpec, dirSpec, dirSpec) + (specSaturatedRgb - borderedRgb));
        T *= (1.0f - k3);
        E = E * (1.0f - k3) + specColor * k3;

        // Step 4: Dynamic pointer specular (lerp toward white by k4)
        T *= (1.0f - k4);
        E = E * (1.0f - k4) + float3(k4, k4, k4);

        // Step 5: 5th-order peripheral RefFresnel (lerp toward fresnelTarget by k5)
        T *= (1.0f - k5);
        E = E * (1.0f - k5) + fresnelTarget * k5;

        // Step 6: 5th-order anisotropic Glare (lerp toward white by k6)
        T *= (1.0f - k6);
        E = E * (1.0f - k6) + float3(k6, k6, k6);

        // Add subtle 7-tap spectral chromatic dispersion contribution from t0
        float3 dispDelta = refractedRgb - baseRgb;
        float dispMag = saturate(length(dispDelta) * 0.85f);
        T *= (1.0f - dispMag * 0.35f);
        E = saturate(E + dispDelta * 0.65f);

        float overlayAlpha = saturate(1.0f - T) * shapeAlpha;
        float3 overlayPremul = saturate(E) * shapeAlpha;
        return float4(overlayPremul, overlayAlpha);
    }

    // Default RenderMode == 0.0 (1:1 shader_28.asm full output)
    return float4(finalRgb * shapeAlpha, shapeAlpha);
}
