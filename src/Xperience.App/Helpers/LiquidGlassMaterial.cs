using System.Numerics;
using Windows.UI;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Helpers;

/// <summary>
/// 3D optical surface curvature profiles reconstructed from WinUI.Composition.Hlsl (shader_28.asm).
/// </summary>
public enum LiquidGlassSurfaceProfile
{
    ConvexSquircle = 0,
    ConvexCircle = 1,
    Concave = 2,
    Lip = 3
}

/// <summary>
/// Semantic control presets reconstructed from WinUI.LiquidGlass.LiquidGlassPreset.
/// </summary>
public enum LiquidGlassPreset
{
    Panel = 0,
    Button = 1,
    Choice = 2,
    SearchBox = 3,
    Input = 4,
    SliderThumb = 5,
    ToggleSwitchKnob = 6,
    Magnifier = 7,
    FloatingPanel = 8,
    TabBar = 9,
    TabBarItem = 10
}

/// <summary>
/// Complete GPU optical material definition matching WinUI.Composition.Hlsl.ILiquidGlassMaterial
/// and packing the exact constant buffers for LiquidGlassBlur, LiquidGlassPostProcess, and LiquidGlassMain.
/// </summary>
public sealed class LiquidGlassMaterial
{
    // Core geometry & refraction (MaterialParams0..3)
    public float BlurRadius { get; set; } = 3.0f;
    public float RefractionStrength { get; set; } = 32.0f;
    public float DispersionStrength { get; set; } = 0.85f;
    public float CornerRadius { get; set; } = 22.0f;
    public float ShapeRoundness { get; set; } = 4.0f;
    public float RasterizationScale { get; set; } = 1.0f;
    public float BorderThickness { get; set; } = 0.75f;
    public float HighlightStrength { get; set; } = 0.58f;
    public float EdgeSoftness { get; set; } = 0.80f;
    public float MaterialOpacity { get; set; } = 1.0f;
    public float BezelWidth { get; set; } = 14.0f;
    public float GlassThickness { get; set; } = 7.5f;
    public float RefractiveIndex { get; set; } = 1.50f;
    public float TintOpacity { get; set; } = 0.48f;
    public float Saturation { get; set; } = 1.45f;
    public float LightAngle { get; set; } = 0.7853982f; // 45 degrees in radians (top-left / bottom-right Fresnel axis)
    public LiquidGlassSurfaceProfile SurfaceProfile { get; set; } = LiquidGlassSurfaceProfile.ConvexSquircle;
    public float MagnificationStrength { get; set; } = 0.08f;
    public float HighlightSharpness { get; set; } = 2.65f;

    // Tint & inner shadow (MaterialParams4)
    public float TintRed { get; set; } = 0.102f;   // #1A1B20
    public float TintGreen { get; set; } = 0.106f;
    public float TintBlue { get; set; } = 0.125f;
    public float InnerShadowStrength { get; set; } = 0.04f;

    // Specular & grading (MaterialParams5)
    public float SpecularSaturation { get; set; } = 1.45f;
    public float SpecularWidth { get; set; } = 0.62f;
    public float Contrast { get; set; } = 1.04f;
    public float Exposure { get; set; } = 0.04f;

    // Interactive Pointer lighting & displacement (MaterialParams6..8)
    public Vector2 PointerUV { get; set; } = new(0.5f, 0.5f);
    public float PointerRadius { get; set; } = 0.85f;
    public float PointerWeight { get; set; } = 1.0f;
    public Vector2 PointerDelta { get; set; } = Vector2.Zero;
    public float PointerEdgeFalloff { get; set; } = 0.35f;
    public float PointerActive { get; set; } = 0.0f;
    public float PointerRadialDisp { get; set; } = 3.2f;
    public float PointerLightInfluence { get; set; } = 0.55f;
    public float PointerDirDisp { get; set; } = 2.4f;
    public float RefractionMultiplier { get; set; } = 1.0f;

    // Dispersion, RefFresnel & Glare (MaterialParams10..12)
    // Cutoff distance d_max = 1500 / (500 / Range)^2 = 0.006 * Range^2 DIPs
    // Range = 15 -> d_max = 1.35 DIPs (strictly 1px-1.35px perimeter rim!)
    public float DispersionRange { get; set; } = 0.32f;
    public float RefFresnelRange { get; set; } = 15.0f;
    public float RefFresnelHardness { get; set; } = 0.0f;
    public float RefFresnelFactor { get; set; } = 14.0f;
    public float GlareRange { get; set; } = 14.5f;
    public float GlareHardness { get; set; } = 0.0f;
    public float GlareFactor { get; set; } = 26.0f;
    public float GlareConvergence { get; set; } = 68.0f;
    public float GlareOppositeFactor { get; set; } = 52.0f;
    public float GlareAngle { get; set; } = -45.0f;
    public float ShapeRadius { get; set; } = -1.0f; // < 0 uses pixel CornerRadius
    public float Magnification { get; set; } = 1.02f;

    // Post-processing pass (shader_43.asm)
    public float BloomAmount { get; set; } = 0.88f;
    public float Brightness { get; set; } = 0.01f;
    public float Temperature { get; set; } = 0.0f;
    public float Vibrance { get; set; } = 0.28f;

    public LiquidGlassMaterial Clone()
    {
        return (LiquidGlassMaterial)MemberwiseClone();
    }

    /// <summary>
    /// Configures optical parameters for a specific control archetype matching WinUI.LiquidGlass.LiquidGlassPresets.
    /// </summary>
    public void ApplyPreset(LiquidGlassPreset preset)
    {
        switch (preset)
        {
            case LiquidGlassPreset.Button:
                SurfaceProfile = LiquidGlassSurfaceProfile.ConvexSquircle;
                ShapeRoundness = 4.0f;
                BezelWidth = 10.5f;
                GlassThickness = 6.0f;
                RefractionStrength = 30.0f;
                DispersionStrength = 0.85f;
                HighlightStrength = 0.62f;
                HighlightSharpness = 2.55f;
                SpecularWidth = 0.58f;
                SpecularSaturation = 1.55f;
                InnerShadowStrength = 0.03f;
                RefFresnelRange = 14.5f;
                RefFresnelHardness = 0.0f;
                RefFresnelFactor = 14.0f;
                GlareRange = 14.0f;
                GlareHardness = 0.0f;
                GlareFactor = 26.0f;
                Magnification = 1.03f;
                break;

            case LiquidGlassPreset.TabBar:
            case LiquidGlassPreset.FloatingPanel:
                SurfaceProfile = LiquidGlassSurfaceProfile.ConvexSquircle;
                ShapeRoundness = 4.0f;
                BezelWidth = 15.0f;
                GlassThickness = 8.5f;
                RefractionStrength = 35.0f;
                DispersionStrength = 0.95f;
                HighlightStrength = 0.60f;
                HighlightSharpness = 2.65f;
                SpecularWidth = 0.64f;
                SpecularSaturation = 1.55f;
                InnerShadowStrength = 0.04f;
                RefFresnelRange = 15.5f;
                RefFresnelHardness = 0.0f;
                RefFresnelFactor = 15.0f;
                GlareRange = 15.0f;
                GlareHardness = 0.0f;
                GlareFactor = 28.0f;
                Magnification = 1.025f;
                break;

            case LiquidGlassPreset.TabBarItem:
                SurfaceProfile = LiquidGlassSurfaceProfile.ConvexSquircle;
                ShapeRoundness = 4.0f;
                BezelWidth = 12.0f;
                GlassThickness = 7.0f;
                RefractionStrength = 34.0f;
                DispersionStrength = 0.90f;
                HighlightStrength = 0.66f;
                HighlightSharpness = 2.45f;
                SpecularWidth = 0.60f;
                SpecularSaturation = 1.60f;
                InnerShadowStrength = 0.02f;
                RefFresnelRange = 14.5f;
                RefFresnelHardness = 0.0f;
                RefFresnelFactor = 16.0f;
                GlareRange = 14.0f;
                GlareHardness = 0.0f;
                GlareFactor = 30.0f;
                Magnification = 1.04f;
                break;

            case LiquidGlassPreset.SearchBox:
            case LiquidGlassPreset.Input:
                SurfaceProfile = LiquidGlassSurfaceProfile.ConvexSquircle;
                ShapeRoundness = 4.0f;
                BezelWidth = 12.5f;
                GlassThickness = 6.5f;
                RefractionStrength = 28.0f;
                DispersionStrength = 0.80f;
                HighlightStrength = 0.56f;
                HighlightSharpness = 2.70f;
                SpecularWidth = 0.58f;
                InnerShadowStrength = 0.04f;
                RefFresnelRange = 15.0f;
                RefFresnelHardness = 0.0f;
                RefFresnelFactor = 13.0f;
                GlareRange = 14.5f;
                GlareHardness = 0.0f;
                GlareFactor = 24.0f;
                Magnification = 1.015f;
                break;

            case LiquidGlassPreset.Magnifier:
            case LiquidGlassPreset.SliderThumb:
            case LiquidGlassPreset.ToggleSwitchKnob:
                SurfaceProfile = LiquidGlassSurfaceProfile.ConvexCircle;
                ShapeRoundness = 2.0f;
                BezelWidth = 13.0f;
                GlassThickness = 9.0f;
                RefractionStrength = 40.0f;
                DispersionStrength = 1.10f;
                HighlightStrength = 0.72f;
                HighlightSharpness = 2.35f;
                SpecularWidth = 0.68f;
                InnerShadowStrength = 0.03f;
                RefFresnelRange = 16.0f;
                RefFresnelHardness = 0.0f;
                RefFresnelFactor = 18.0f;
                GlareRange = 15.0f;
                GlareHardness = 0.0f;
                GlareFactor = 32.0f;
                Magnification = 1.08f;
                break;

            case LiquidGlassPreset.Panel:
            default:
                SurfaceProfile = LiquidGlassSurfaceProfile.ConvexSquircle;
                ShapeRoundness = 4.0f;
                BezelWidth = 13.5f;
                GlassThickness = 7.5f;
                RefractionStrength = 32.0f;
                DispersionStrength = 0.85f;
                HighlightStrength = 0.58f;
                HighlightSharpness = 2.65f;
                SpecularWidth = 0.62f;
                SpecularSaturation = 1.50f;
                InnerShadowStrength = 0.04f;
                RefFresnelRange = 15.0f;
                RefFresnelHardness = 0.0f;
                RefFresnelFactor = 14.0f;
                GlareRange = 14.5f;
                GlareHardness = 0.0f;
                GlareFactor = 26.0f;
                Magnification = 1.02f;
                break;
        }
    }

    /// <summary>
    /// Synchronizes user-configurable Xperience settings (sliders/themes) into the GPU material parameters.
    /// </summary>
    public void ApplyUserParameters(
        LiquidGlassParameters p,
        Color resolvedTintColor,
        double? tintOpacityOverride = null,
        double highlightMultiplier = 1.0)
    {
        if (!p.IsShaderEnabled)
        {
            MaterialOpacity = 0.0f;
            return;
        }

        MaterialOpacity = 1.0f;
        BlurRadius = (float)Math.Clamp(p.FrostBlurRadius, 0.0, 64.0);

        // Map user DistortionStrength (0..100, default 60) -> physical Snell RefractionStrength (0..55, default 33)
        float distNorm = (float)Math.Clamp(p.DistortionStrength / 60.0, 0.0, 2.0);
        RefractionStrength *= distNorm;

        // Map user ChromaticAberration (0..6, default 1.25) -> 7-tap DispersionStrength
        float caNorm = (float)Math.Clamp(p.ChromaticAberration / 1.25, 0.0, 3.0);
        DispersionStrength = Math.Clamp(DispersionStrength * caNorm, 0.0f, 4.5f);

        // Map user SpecularRimIntensity (0..1, default 0.55) & per-control multiplier
        float specScale = (float)Math.Clamp((p.SpecularRimIntensity / 0.55) * highlightMultiplier, 0.0, 2.5);
        HighlightStrength = Math.Clamp(HighlightStrength * specScale, 0.0f, 1.5f);
        RefFresnelFactor = Math.Clamp(RefFresnelFactor * specScale, 0.0f, 65.0f);
        GlareFactor = Math.Clamp(GlareFactor * specScale, 0.0f, 85.0f);

        // Map user InnerShadowBlur (0..48, default 8) -> InnerShadowStrength
        float shadowNorm = (float)Math.Clamp(p.InnerShadowBlur / 8.0, 0.0, 2.5);
        InnerShadowStrength = Math.Clamp(InnerShadowStrength * shadowNorm, 0.0f, 0.45f);

        // Map Tint color & opacity
        TintRed = resolvedTintColor.R / 255.0f;
        TintGreen = resolvedTintColor.G / 255.0f;
        TintBlue = resolvedTintColor.B / 255.0f;
        double effectiveTint = tintOpacityOverride.HasValue
            ? Math.Clamp(tintOpacityOverride.Value, 0.0, 0.95)
            : Math.Clamp(p.TintOpacity, 0.0, 0.95);
        TintOpacity = (float)effectiveTint;

        // Map SaturationBoost (0.5..2.0, default 1.60)
        Saturation = (float)Math.Clamp(p.SaturationBoost, 0.5, 2.2);
    }

    /// <summary>
    /// Packs the 15 float4 registers (MaterialParams0..14) for LiquidGlassMain.cso (shader_28.asm).
    /// </summary>
    public void PackMaterialConstants(
        Vector4 uvRect,
        Vector2 elementSizePx,
        Vector2 invBackdropSizePx,
        float renderMode,
        Span<Vector4> dest)
    {
        // cb0[0]: (BorderThickness, CornerRadius, RefractionStrength, BezelWidth)
        dest[0] = new Vector4(BorderThickness, CornerRadius, RefractionStrength, BezelWidth);

        // cb0[1]: (HighlightStrength, EdgeSoftness, DispersionStrength, MaterialOpacity)
        dest[1] = new Vector4(HighlightStrength, EdgeSoftness, DispersionStrength, MaterialOpacity);

        // cb0[2]: (GlassThickness, RefractiveIndex, TintOpacity, Saturation)
        dest[2] = new Vector4(GlassThickness, RefractiveIndex, TintOpacity, Saturation);

        // cb0[3]: (LightAngleRad, SurfaceProfile, MagnificationStrength, HighlightSharpness)
        dest[3] = new Vector4(LightAngle, (float)SurfaceProfile, MagnificationStrength, HighlightSharpness);

        // cb0[4]: (TintRed, TintGreen, TintBlue, InnerShadowStrength)
        dest[4] = new Vector4(TintRed, TintGreen, TintBlue, InnerShadowStrength);

        // cb0[5]: (SpecularSaturation, SpecularWidth, Contrast, Exposure)
        dest[5] = new Vector4(SpecularSaturation, SpecularWidth, Contrast, Exposure);

        // cb0[6]: (PointerUV.x, PointerUV.y, PointerRadiusNorm, PointerWeight)
        dest[6] = new Vector4(PointerUV.X, PointerUV.Y, PointerRadius, PointerWeight);

        // cb0[7]: (PointerDelta.x, PointerDelta.y, PointerEdgeFalloffNorm, PointerActive)
        dest[7] = new Vector4(PointerDelta.X, PointerDelta.Y, PointerEdgeFalloff, PointerActive);

        // cb0[8]: (PointerRadialDisp, PointerLightInfluence, PointerDirDisp, RefractionMultiplier)
        dest[8] = new Vector4(PointerRadialDisp, PointerLightInfluence, PointerDirDisp, RefractionMultiplier);

        // cb0[9]: (ShapeRoundness, RasterizationScale, RenderMode, Reserved)
        dest[9] = new Vector4(ShapeRoundness, RasterizationScale, renderMode, 0.0f);

        // cb0[10]: (DispersionRange, RefFresnelRange, RefFresnelHardness, RefFresnelFactor)
        dest[10] = new Vector4(DispersionRange, RefFresnelRange, RefFresnelHardness, RefFresnelFactor);

        // cb0[11]: (GlareRange, GlareHardness, GlareFactor, GlareConvergence)
        dest[11] = new Vector4(GlareRange, GlareHardness, GlareFactor, GlareConvergence);

        // cb0[12]: (GlareOppositeFactor, GlareAngleDeg, ShapeRadius, Magnification)
        dest[12] = new Vector4(GlareOppositeFactor, GlareAngle, ShapeRadius, Magnification);

        // cb0[13]: (MinU, MinV, MaxU, MaxV) -> v2 samplerData UV sub-rect
        dest[13] = uvRect;

        // cb0[14]: (ElementWidthPx, ElementHeightPx, InvBackdropWidthPx, InvBackdropHeightPx)
        dest[14] = new Vector4(elementSizePx.X, elementSizePx.Y, invBackdropSizePx.X, invBackdropSizePx.Y);
    }

    /// <summary>
    /// Packs the 2 float4 registers (PostParams0..1) for LiquidGlassPostProcess.cso (shader_43.asm).
    /// </summary>
    public void PackPostProcessConstants(out Vector4 post0, out Vector4 post1)
    {
        post0 = new Vector4(BloomAmount, Brightness, Temperature, Vibrance);
        post1 = new Vector4(Saturation, Contrast, Exposure, 0.0f);
    }

    /// <summary>
    /// Computes the exact 21-tap bilinear Gaussian blur kernel for LiquidGlassBlurHorizontal/Vertical.cso (shader_0.asm).
    /// Pairs 20 discrete offsets per side into 10 hardware bilinear sample offsets + weights.
    /// </summary>
    public static void ComputeGaussianBlurConstants(
        float blurRadius,
        float invTexWidth,
        float invTexHeight,
        out Vector4 blurParams0,
        out Vector4 pair0,
        out Vector4 pair1,
        out Vector4 pair2,
        out Vector4 pair3,
        out Vector4 pair4)
    {
        float clampedRadius = Math.Clamp(blurRadius, 0.0f, 96.0f);
        if (clampedRadius <= 0.05f)
        {
            blurParams0 = new Vector4(0.0f, 1.0f, invTexWidth, invTexHeight);
            pair0 = Vector4.Zero;
            pair1 = Vector4.Zero;
            pair2 = Vector4.Zero;
            pair3 = Vector4.Zero;
            pair4 = Vector4.Zero;
            return;
        }

        // Standard Gaussian sigma for radius R: sigma = max(R * 0.5, 0.35).
        // When sigma exceeds the 20-pixel half-window (sigma > 6.67), scale step distance via BlurAmount.
        float rawSigma = Math.Max(clampedRadius * 0.55f, 0.40f);
        float maxKernelSigma = 6.666667f;
        float blurAmountScale = rawSigma > maxKernelSigma ? (rawSigma / maxKernelSigma) : 1.0f;
        float kernelSigma = rawSigma / blurAmountScale;
        float invTwoSigmaSq = 1.0f / (2.0f * kernelSigma * kernelSigma);

        Span<float> discreteWeights = stackalloc float[21];
        float sum = 1.0f;
        discreteWeights[0] = 1.0f;
        for (int k = 1; k <= 20; k++)
        {
            float w = MathF.Exp(-(k * k) * invTwoSigmaSq);
            discreteWeights[k] = w;
            sum += 2.0f * w;
        }

        float invSum = 1.0f / sum;
        float centerWeight = discreteWeights[0] * invSum;

        Span<float> pairOffsets = stackalloc float[10];
        Span<float> pairWeights = stackalloc float[10];
        for (int i = 0; i < 10; i++)
        {
            int kA = 2 * i + 1;
            int kB = 2 * i + 2;
            float wA = discreteWeights[kA] * invSum;
            float wB = discreteWeights[kB] * invSum;
            float combinedW = wA + wB;
            float bilinearOffset = combinedW > 1e-8f
                ? (kA + (wB / combinedW))
                : kA;

            pairOffsets[i] = bilinearOffset;
            pairWeights[i] = combinedW;
        }

        blurParams0 = new Vector4(blurAmountScale, centerWeight, invTexWidth, invTexHeight);
        pair0 = new Vector4(pairOffsets[0], pairWeights[0], pairOffsets[1], pairWeights[1]);
        pair1 = new Vector4(pairOffsets[2], pairWeights[2], pairOffsets[3], pairWeights[3]);
        pair2 = new Vector4(pairOffsets[4], pairWeights[4], pairOffsets[5], pairWeights[5]);
        pair3 = new Vector4(pairOffsets[6], pairWeights[6], pairOffsets[7], pairWeights[7]);
        pair4 = new Vector4(pairOffsets[8], pairWeights[8], pairOffsets[9], pairWeights[9]);
    }
}
