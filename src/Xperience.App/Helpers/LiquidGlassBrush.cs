using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Xperience.App.Helpers;

/// <summary>
/// XAML Composition brush reconstruction of WinUI.Composition.Hlsl.LiquidGlassBrush.
/// Renders the GPU HLSL Liquid Glass pipeline (shader_0 -> shader_43 -> shader_28) into a CompositionBrush.
/// </summary>
public sealed class LiquidGlassBrush : XamlCompositionBrushBase
{
    public LiquidGlassMaterial Material { get; } = new();

    private CompositionDrawingSurface? _gpuSurface;
    private CompositionSurfaceBrush? _surfaceBrush;
    private long _resourceGeneration = -1;
    private bool _isConnected;

    public static readonly DependencyProperty PresetProperty =
        DependencyProperty.Register(nameof(Preset), typeof(LiquidGlassPreset), typeof(LiquidGlassBrush),
            new PropertyMetadata(LiquidGlassPreset.Panel, OnMaterialPropertyChanged));

    public static readonly DependencyProperty BlurRadiusProperty =
        DependencyProperty.Register(nameof(BlurRadius), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(3.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty RefractionStrengthProperty =
        DependencyProperty.Register(nameof(RefractionStrength), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(32.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty DispersionStrengthProperty =
        DependencyProperty.Register(nameof(DispersionStrength), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(1.25, OnMaterialPropertyChanged));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(22.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty ShapeRoundnessProperty =
        DependencyProperty.Register(nameof(ShapeRoundness), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(4.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty SurfaceProfileProperty =
        DependencyProperty.Register(nameof(SurfaceProfile), typeof(LiquidGlassSurfaceProfile), typeof(LiquidGlassBrush),
            new PropertyMetadata(LiquidGlassSurfaceProfile.ConvexSquircle, OnMaterialPropertyChanged));

    public static readonly DependencyProperty BezelWidthProperty =
        DependencyProperty.Register(nameof(BezelWidth), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(15.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty GlassThicknessProperty =
        DependencyProperty.Register(nameof(GlassThickness), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(8.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty RefractiveIndexProperty =
        DependencyProperty.Register(nameof(RefractiveIndex), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(1.50, OnMaterialPropertyChanged));

    public static readonly DependencyProperty HighlightStrengthProperty =
        DependencyProperty.Register(nameof(HighlightStrength), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(0.65, OnMaterialPropertyChanged));

    public static readonly DependencyProperty HighlightSharpnessProperty =
        DependencyProperty.Register(nameof(HighlightSharpness), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(2.35, OnMaterialPropertyChanged));

    public static readonly DependencyProperty SpecularWidthProperty =
        DependencyProperty.Register(nameof(SpecularWidth), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(1.45, OnMaterialPropertyChanged));

    public static readonly DependencyProperty SpecularSaturationProperty =
        DependencyProperty.Register(nameof(SpecularSaturation), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(1.55, OnMaterialPropertyChanged));

    public static readonly DependencyProperty InnerShadowStrengthProperty =
        DependencyProperty.Register(nameof(InnerShadowStrength), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(0.16, OnMaterialPropertyChanged));

    public static readonly DependencyProperty TintOpacityProperty =
        DependencyProperty.Register(nameof(TintOpacity), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(0.48, OnMaterialPropertyChanged));

    public static readonly DependencyProperty SaturationProperty =
        DependencyProperty.Register(nameof(Saturation), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(1.45, OnMaterialPropertyChanged));

    public static readonly DependencyProperty RefFresnelRangeProperty =
        DependencyProperty.Register(nameof(RefFresnelRange), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(28.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty RefFresnelHardnessProperty =
        DependencyProperty.Register(nameof(RefFresnelHardness), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(4.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty RefFresnelFactorProperty =
        DependencyProperty.Register(nameof(RefFresnelFactor), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(22.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty GlareRangeProperty =
        DependencyProperty.Register(nameof(GlareRange), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(26.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty GlareHardnessProperty =
        DependencyProperty.Register(nameof(GlareHardness), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(5.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty GlareFactorProperty =
        DependencyProperty.Register(nameof(GlareFactor), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(42.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty GlareConvergenceProperty =
        DependencyProperty.Register(nameof(GlareConvergence), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(58.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty GlareOppositeFactorProperty =
        DependencyProperty.Register(nameof(GlareOppositeFactor), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(55.0, OnMaterialPropertyChanged));

    public static readonly DependencyProperty GlareAngleProperty =
        DependencyProperty.Register(nameof(GlareAngle), typeof(double), typeof(LiquidGlassBrush),
            new PropertyMetadata(-45.0, OnMaterialPropertyChanged));

    public LiquidGlassPreset Preset
    {
        get => (LiquidGlassPreset)GetValue(PresetProperty);
        set => SetValue(PresetProperty, value);
    }

    public double BlurRadius
    {
        get => (double)GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    public double RefractionStrength
    {
        get => (double)GetValue(RefractionStrengthProperty);
        set => SetValue(RefractionStrengthProperty, value);
    }

    public double DispersionStrength
    {
        get => (double)GetValue(DispersionStrengthProperty);
        set => SetValue(DispersionStrengthProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double ShapeRoundness
    {
        get => (double)GetValue(ShapeRoundnessProperty);
        set => SetValue(ShapeRoundnessProperty, value);
    }

    public LiquidGlassSurfaceProfile SurfaceProfile
    {
        get => (LiquidGlassSurfaceProfile)GetValue(SurfaceProfileProperty);
        set => SetValue(SurfaceProfileProperty, value);
    }

    public double BezelWidth
    {
        get => (double)GetValue(BezelWidthProperty);
        set => SetValue(BezelWidthProperty, value);
    }

    public double GlassThickness
    {
        get => (double)GetValue(GlassThicknessProperty);
        set => SetValue(GlassThicknessProperty, value);
    }

    public double RefractiveIndex
    {
        get => (double)GetValue(RefractiveIndexProperty);
        set => SetValue(RefractiveIndexProperty, value);
    }

    public double HighlightStrength
    {
        get => (double)GetValue(HighlightStrengthProperty);
        set => SetValue(HighlightStrengthProperty, value);
    }

    public double HighlightSharpness
    {
        get => (double)GetValue(HighlightSharpnessProperty);
        set => SetValue(HighlightSharpnessProperty, value);
    }

    public double SpecularWidth
    {
        get => (double)GetValue(SpecularWidthProperty);
        set => SetValue(SpecularWidthProperty, value);
    }

    public double SpecularSaturation
    {
        get => (double)GetValue(SpecularSaturationProperty);
        set => SetValue(SpecularSaturationProperty, value);
    }

    public double InnerShadowStrength
    {
        get => (double)GetValue(InnerShadowStrengthProperty);
        set => SetValue(InnerShadowStrengthProperty, value);
    }

    public double TintOpacity
    {
        get => (double)GetValue(TintOpacityProperty);
        set => SetValue(TintOpacityProperty, value);
    }

    public double Saturation
    {
        get => (double)GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    public double RefFresnelRange
    {
        get => (double)GetValue(RefFresnelRangeProperty);
        set => SetValue(RefFresnelRangeProperty, value);
    }

    public double RefFresnelHardness
    {
        get => (double)GetValue(RefFresnelHardnessProperty);
        set => SetValue(RefFresnelHardnessProperty, value);
    }

    public double RefFresnelFactor
    {
        get => (double)GetValue(RefFresnelFactorProperty);
        set => SetValue(RefFresnelFactorProperty, value);
    }

    public double GlareRange
    {
        get => (double)GetValue(GlareRangeProperty);
        set => SetValue(GlareRangeProperty, value);
    }

    public double GlareHardness
    {
        get => (double)GetValue(GlareHardnessProperty);
        set => SetValue(GlareHardnessProperty, value);
    }

    public double GlareFactor
    {
        get => (double)GetValue(GlareFactorProperty);
        set => SetValue(GlareFactorProperty, value);
    }

    public double GlareConvergence
    {
        get => (double)GetValue(GlareConvergenceProperty);
        set => SetValue(GlareConvergenceProperty, value);
    }

    public double GlareOppositeFactor
    {
        get => (double)GetValue(GlareOppositeFactorProperty);
        set => SetValue(GlareOppositeFactorProperty, value);
    }

    public double GlareAngle
    {
        get => (double)GetValue(GlareAngleProperty);
        set => SetValue(GlareAngleProperty, value);
    }

    private static void OnMaterialPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidGlassBrush brush)
        {
            brush.SyncAndRenderMaterial();
        }
    }

    protected override void OnConnected()
    {
        base.OnConnected();
        _isConnected = true;
        LiquidGlassBackdropManager.GpuResourcesInvalidated -= OnGpuResourcesInvalidated;
        LiquidGlassBackdropManager.GpuResourcesInvalidated += OnGpuResourcesInvalidated;
        SyncAndRenderMaterial();
    }

    protected override void OnDisconnected()
    {
        base.OnDisconnected();
        _isConnected = false;
        LiquidGlassBackdropManager.GpuResourcesInvalidated -= OnGpuResourcesInvalidated;
        CompositionBrush = null;
        if (_surfaceBrush is not null)
        {
            _surfaceBrush.Surface = null;
            _surfaceBrush.Dispose();
            _surfaceBrush = null;
        }

        _gpuSurface?.Dispose();
        _gpuSurface = null;
        _resourceGeneration = -1;
    }

    private void OnGpuResourcesInvalidated(object? sender, EventArgs e)
    {
        if (!_isConnected)
        {
            return;
        }

        if (DispatcherQueue is { } dq)
        {
            if (dq.HasThreadAccess)
            {
                SyncAndRenderMaterial();
            }
            else
            {
                dq.TryEnqueue(() =>
                {
                    if (_isConnected)
                    {
                        SyncAndRenderMaterial();
                    }
                });
            }
        }
    }

    private void SyncAndRenderMaterial()
    {
        if (!_isConnected)
        {
            return;
        }

        var compositor = LiquidGlassBackdropManager.Instance.Compositor;
        var engine = LiquidGlassBackdropManager.Instance.GpuShaderEngine;
        if (compositor is null || !engine.EnsureInitialized(compositor))
        {
            return;
        }

        Material.ApplyPreset(Preset);
        Material.BlurRadius = (float)BlurRadius;
        Material.RefractionStrength = (float)RefractionStrength;
        Material.DispersionStrength = (float)DispersionStrength;
        Material.CornerRadius = (float)CornerRadius;
        Material.ShapeRoundness = (float)ShapeRoundness;
        Material.SurfaceProfile = SurfaceProfile;
        Material.BezelWidth = (float)BezelWidth;
        Material.GlassThickness = (float)GlassThickness;
        Material.RefractiveIndex = (float)RefractiveIndex;
        Material.HighlightStrength = (float)HighlightStrength;
        Material.HighlightSharpness = (float)HighlightSharpness;
        Material.SpecularWidth = (float)SpecularWidth;
        Material.SpecularSaturation = (float)SpecularSaturation;
        Material.InnerShadowStrength = (float)InnerShadowStrength;
        Material.TintOpacity = (float)TintOpacity;
        Material.Saturation = (float)Saturation;
        Material.RefFresnelRange = (float)RefFresnelRange;
        Material.RefFresnelHardness = (float)RefFresnelHardness;
        Material.RefFresnelFactor = (float)RefFresnelFactor;
        Material.GlareRange = (float)GlareRange;
        Material.GlareHardness = (float)GlareHardness;
        Material.GlareFactor = (float)GlareFactor;
        Material.GlareConvergence = (float)GlareConvergence;
        Material.GlareOppositeFactor = (float)GlareOppositeFactor;
        Material.GlareAngle = (float)GlareAngle;

        long currentGen = LiquidGlassBackdropManager.ResourceGeneration;
        bool needsNewSurface = _gpuSurface is null || _resourceGeneration != currentGen;
        CompositionDrawingSurface? targetSurface = needsNewSurface
            ? engine.CreateSurface(256, 96)
            : _gpuSurface;

        if (targetSurface is null)
        {
            return;
        }

        if (!engine.RenderLiquidGlassSurface(
            targetSurface,
            256,
            96,
            Material,
            new Vector4(0f, 0f, 1f, 1f),
            0.0f))
        {
            if (needsNewSurface)
            {
                try { targetSurface.Dispose(); } catch { }
            }

            return;
        }

        var oldSurface = needsNewSurface ? _gpuSurface : null;
        _gpuSurface = targetSurface;
        _resourceGeneration = currentGen;

        if (_surfaceBrush is null)
        {
            _surfaceBrush = compositor.CreateSurfaceBrush(_gpuSurface);
            _surfaceBrush.Stretch = CompositionStretch.Fill;
        }
        else if (!ReferenceEquals(_surfaceBrush.Surface, _gpuSurface))
        {
            _surfaceBrush.Surface = _gpuSurface;
        }

        CompositionBrush = _surfaceBrush;

        if (oldSurface is not null && !ReferenceEquals(oldSurface, _gpuSurface))
        {
            try { oldSurface.Dispose(); } catch { }
        }
    }
}
