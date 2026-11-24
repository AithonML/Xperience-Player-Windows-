using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;
using Xperience.App.Helpers;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Controls;

/// <summary>
/// GPU HLSL-accelerated Liquid Glass surface for WinUI 3 (<see cref="Microsoft.UI.Composition"/>).
/// Executes compiled Direct2D1 ps_4_0 shaders (<c>LiquidGlassBlurHorizontal.cso</c>, <c>LiquidGlassBlurVertical.cso</c>,
/// <c>LiquidGlassPostProcess.cso</c>, <c>LiquidGlassMain.cso</c> reconstructed 1:1 from <c>shader_0.asm</c>,
/// <c>shader_43.asm</c>, and <c>shader_28.asm</c>):
/// - Continuous G2 superellipse SDF (<c>ShapeRoundness</c> p in [2, 8]) with screen-derivative anti-aliasing
/// - 4 3D surface profiles (<c>ConvexSquircle</c>, <c>ConvexCircle</c>, <c>Concave</c>, <c>Lip</c>)
/// - True 3D Snell's Law refraction through <c>BezelWidth</c> + <c>GlassThickness</c>
/// - 7-tap R/G/B spectral chromatic dispersion &amp; <c>SpecularSaturation</c>
/// - 5th-order peripheral <c>RefFresnel</c> + 5th-order anisotropic <c>Glare</c>
/// - Real-time interactive pointer lighting (<c>PointerLightInfluence</c>) &amp; radial displacement
/// </summary>
[ContentProperty(Name = nameof(InnerContent))]
public sealed partial class LiquidGlassPanel : UserControl
{
    private IThemeService? _themeService;
    private bool _isLoaded;
    private bool _isHovered;
    private bool _isPressed;
    private bool _isDraggingLens;
    private Point _dragStartPointerPos;
    private double _dragStartLeft;
    private double _dragStartTop;

    private Vector2 _pointerUV = new(0.5f, 0.5f);
    private Vector2 _pointerDelta = Vector2.Zero;
    private long _lastPointerShaderTick;

    private Compositor? _compositor;
    private ContainerVisual? _rootContainerVisual;
    private SpriteVisual? _glassVisual;
    private SpriteVisual? _specularVisual;
    private CompositionRoundedRectangleGeometry? _clipGeometry;
    private CompositionGeometricClip? _visualClip;

    private CompositionEffectBrush? _unifiedEffectBrush;
    private CompositionEffectBrush? _baseOnlyEffectBrush;
    private CompositionBackdropBrush? _backdropBrush;
    private CompositionVisualSurface? _refractionSurface;
    private CompositionSurfaceBrush? _refractedRimBrush;
    private CompositionSurfaceBrush? _rimMaskBrush;
    private CompositionSurfaceBrush? _shapeClipMaskBrush;
    private CompositionSurfaceBrush? _specularBrush;
    private bool _isRimRefractionActive;

    private CompositionDrawingSurface? _interactiveOpticalSurface;
    private int _interactiveSurfaceW;
    private int _interactiveSurfaceH;

    private XamlRoot? _subscribedXamlRoot;
    private float _rasterizationScale = 1.0f;
    private LiquidGlassBackdropManager.PanelSurfaceSet? _currentSurfaceSet;
    private long _pipelineResourceGeneration = -1;
    private long _visibilityCallbackToken;
    private bool _needsSurfaceRefresh;
    private bool _isUpdatingPipeline;
    private bool _isDispatcherUpdateQueued;
    private bool _queuedForceSurfaceRefresh;

    public static readonly DependencyProperty InnerContentProperty = DependencyProperty.Register(
        nameof(InnerContent),
        typeof(object),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(null));

    public static readonly DependencyProperty GlassCornerRadiusProperty = DependencyProperty.Register(
        nameof(GlassCornerRadius),
        typeof(double),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(24.0, OnVisualDependencyPropertyChanged));

    public static readonly DependencyProperty GlassPaddingProperty = DependencyProperty.Register(
        nameof(GlassPadding),
        typeof(Thickness),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(new Thickness(0)));

    public static readonly DependencyProperty SampleLiveBackdropProperty = DependencyProperty.Register(
        nameof(SampleLiveBackdrop),
        typeof(bool),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(true, OnVisualDependencyPropertyChanged));

    public static readonly DependencyProperty IsDraggableLensProperty = DependencyProperty.Register(
        nameof(IsDraggableLens),
        typeof(bool),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(false, OnVisualDependencyPropertyChanged));

    public static readonly DependencyProperty EnableHoverScaleProperty = DependencyProperty.Register(
        nameof(EnableHoverScale),
        typeof(bool),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(false));

    public static readonly DependencyProperty UseNowPlayingCoverBackdropProperty = DependencyProperty.Register(
        nameof(UseNowPlayingCoverBackdrop),
        typeof(bool),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(false, OnVisualDependencyPropertyChanged));

    public static readonly DependencyProperty HighlightAlphaMultiplierProperty = DependencyProperty.Register(
        nameof(HighlightAlphaMultiplier),
        typeof(double),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(1.0, OnVisualDependencyPropertyChanged));

    public static readonly DependencyProperty TintOpacityOverrideProperty = DependencyProperty.Register(
        nameof(TintOpacityOverride),
        typeof(double),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(-1.0, OnVisualDependencyPropertyChanged));

    public static readonly DependencyProperty PresetProperty = DependencyProperty.Register(
        nameof(Preset),
        typeof(LiquidGlassPreset),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(LiquidGlassPreset.Panel, OnVisualDependencyPropertyChanged));

    public static readonly DependencyProperty SurfaceProfileProperty = DependencyProperty.Register(
        nameof(SurfaceProfile),
        typeof(LiquidGlassSurfaceProfile),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(LiquidGlassSurfaceProfile.ConvexSquircle, OnVisualDependencyPropertyChanged));

    public static readonly DependencyProperty ShapeRoundnessProperty = DependencyProperty.Register(
        nameof(ShapeRoundness),
        typeof(double),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(4.0, OnVisualDependencyPropertyChanged));

    public static readonly DependencyProperty EnablePointerLightingProperty = DependencyProperty.Register(
        nameof(EnablePointerLighting),
        typeof(bool),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(true));

    public static readonly DependencyProperty UseAccentTintProperty = DependencyProperty.Register(
        nameof(UseAccentTint),
        typeof(bool),
        typeof(LiquidGlassPanel),
        new PropertyMetadata(false, OnVisualDependencyPropertyChanged));

    public object? InnerContent
    {
        get => GetValue(InnerContentProperty);
        set => SetValue(InnerContentProperty, value);
    }

    public double GlassCornerRadius
    {
        get => (double)GetValue(GlassCornerRadiusProperty);
        set => SetValue(GlassCornerRadiusProperty, value);
    }

    public Thickness GlassPadding
    {
        get => (Thickness)GetValue(GlassPaddingProperty);
        set => SetValue(GlassPaddingProperty, value);
    }

    public bool SampleLiveBackdrop
    {
        get => (bool)GetValue(SampleLiveBackdropProperty);
        set => SetValue(SampleLiveBackdropProperty, value);
    }

    public bool IsDraggableLens
    {
        get => (bool)GetValue(IsDraggableLensProperty);
        set => SetValue(IsDraggableLensProperty, value);
    }

    public bool EnableHoverScale
    {
        get => (bool)GetValue(EnableHoverScaleProperty);
        set => SetValue(EnableHoverScaleProperty, value);
    }

    public bool UseNowPlayingCoverBackdrop
    {
        get => (bool)GetValue(UseNowPlayingCoverBackdropProperty);
        set => SetValue(UseNowPlayingCoverBackdropProperty, value);
    }

    public double HighlightAlphaMultiplier
    {
        get => (double)GetValue(HighlightAlphaMultiplierProperty);
        set => SetValue(HighlightAlphaMultiplierProperty, value);
    }

    public double TintOpacityOverride
    {
        get => (double)GetValue(TintOpacityOverrideProperty);
        set => SetValue(TintOpacityOverrideProperty, value);
    }

    public LiquidGlassPreset Preset
    {
        get => (LiquidGlassPreset)GetValue(PresetProperty);
        set => SetValue(PresetProperty, value);
    }

    public LiquidGlassSurfaceProfile SurfaceProfile
    {
        get => (LiquidGlassSurfaceProfile)GetValue(SurfaceProfileProperty);
        set => SetValue(SurfaceProfileProperty, value);
    }

    public double ShapeRoundness
    {
        get => (double)GetValue(ShapeRoundnessProperty);
        set => SetValue(ShapeRoundnessProperty, value);
    }

    public bool EnablePointerLighting
    {
        get => (bool)GetValue(EnablePointerLightingProperty);
        set => SetValue(EnablePointerLightingProperty, value);
    }

    public bool UseAccentTint
    {
        get => (bool)GetValue(UseAccentTintProperty);
        set => SetValue(UseAccentTintProperty, value);
    }

    public LiquidGlassPanel()
    {
        InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(this, true);

        Loaded += OnPanelLoaded;
        Unloaded += OnPanelUnloaded;
        EffectiveViewportChanged += OnPanelEffectiveViewportChanged;
        PointerEntered += OnPanelPointerEntered;
        PointerExited += OnPanelPointerExited;
        PointerPressed += OnPanelPointerPressed;
        PointerMoved += OnPanelPointerMoved;
        PointerReleased += OnPanelPointerReleased;
        PointerCanceled += OnPanelPointerCanceled;
        PointerCaptureLost += OnPanelPointerCaptureLost;
    }

    public CornerRadius ToCornerRadius(double radius) => new(Math.Max(0.0, radius));

    public void InvalidateGlass()
    {
        if (!_isLoaded)
        {
            return;
        }

        bool mustRefresh = _needsSurfaceRefresh ||
                           _currentSurfaceSet is null ||
                           !LiquidGlassBackdropManager.IsSurfaceSetCurrent(_currentSurfaceSet) ||
                           _pipelineResourceGeneration != LiquidGlassBackdropManager.ResourceGeneration;
        UpdateCompositionPipeline(forceSurfaceRefresh: mustRefresh);
    }

    internal void SyncRefractionViewportOnly()
    {
        if (!_isLoaded || Visibility != Visibility.Visible)
        {
            return;
        }

        double width = GlassVisualHost.ActualWidth;
        double height = GlassVisualHost.ActualHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 2.0 || height < 2.0)
        {
            return;
        }

        long currentResGen = LiquidGlassBackdropManager.ResourceGeneration;
        bool visualDetached = _rootContainerVisual is not null &&
                              !ReferenceEquals(ElementCompositionPreview.GetElementChildVisual(GlassVisualHost), _rootContainerVisual);
        bool sizeMismatch = _rootContainerVisual is not null &&
                            (MathF.Abs(_rootContainerVisual.Size.X - (float)width) > 0.5f ||
                             MathF.Abs(_rootContainerVisual.Size.Y - (float)height) > 0.5f);

        if (_glassVisual is null ||
            _rootContainerVisual is null ||
            _unifiedEffectBrush is null ||
            _baseOnlyEffectBrush is null ||
            _pipelineResourceGeneration != currentResGen ||
            _needsSurfaceRefresh ||
            visualDetached ||
            sizeMismatch ||
            !LiquidGlassBackdropManager.IsSurfaceSetCurrent(_currentSurfaceSet))
        {
            UpdateCompositionPipeline(forceSurfaceRefresh: true);
            return;
        }

        var glass = _themeService?.LiquidGlass ?? new LiquidGlassParameters();
        bool isDarkTheme = _themeService?.CurrentTheme.IsDark ?? true;
        bool useLiveComposition = glass.IsShaderEnabled && (SampleLiveBackdrop || IsDraggableLens);
        if (!useLiveComposition)
        {
            UpdateCompositionPipeline(forceSurfaceRefresh: false);
            return;
        }

        var metrics = LiquidGlassBackdropManager.ComputeLensMetrics(
            (float)width,
            (float)height,
            glass.DistortionStrength,
            glass.SpecularRimIntensity);

        bool rimActive = SyncRefractionSurfaceAndTransforms(
            (float)width,
            (float)height,
            metrics.InwardShiftDip,
            glass.DistortionStrength);

        if (rimActive != _isRimRefractionActive)
        {
            _isRimRefractionActive = rimActive;
            _glassVisual.Brush = rimActive ? _unifiedEffectBrush : _baseOnlyEffectBrush;
        }

        ApplyTintColors(glass, isDarkTheme);
    }

    private static void OnVisualDependencyPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidGlassPanel panel && panel._isLoaded)
        {
            panel.UpdateCompositionPipeline(forceSurfaceRefresh: true);
        }
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _needsSurfaceRefresh = true;
        GlassVisualHost.SizeChanged -= OnHostSizeChanged;
        GlassVisualHost.SizeChanged += OnHostSizeChanged;

        if (_visibilityCallbackToken == 0)
        {
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnPanelVisibilityChanged);
        }

        AttachThemeService();
        AttachXamlRootTracking();

        LiquidGlassBackdropManager.RegisterPanel(this);
        LiquidGlassBackdropManager.GpuResourcesInvalidated -= OnGpuResourcesInvalidated;
        LiquidGlassBackdropManager.GpuResourcesInvalidated += OnGpuResourcesInvalidated;

        UpdateCompositionPipeline(forceSurfaceRefresh: true);
    }

    private void OnPanelUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _isHovered = false;
        _isPressed = false;
        _isDraggingLens = false;
        _needsSurfaceRefresh = false;
        _isDispatcherUpdateQueued = false;
        _queuedForceSurfaceRefresh = false;

        if (_visibilityCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }

        GlassVisualHost.SizeChanged -= OnHostSizeChanged;
        LiquidGlassBackdropManager.UnregisterPanel(this);
        LiquidGlassBackdropManager.GpuResourcesInvalidated -= OnGpuResourcesInvalidated;
        DetachThemeService();
        DetachXamlRootTracking();

        if (EnableHoverScale)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(this);
                visual.StopAnimation(nameof(visual.Scale));
                visual.Scale = Vector3.One;
            }
            catch
            {
                // Ignore visual teardown race during window close
            }
        }

        DisposeCompositionPipeline();
    }

    private void OnPanelVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (!_isLoaded)
        {
            return;
        }

        if (Visibility != Visibility.Visible)
        {
            DisposeCompositionPipeline();
            _needsSurfaceRefresh = true;
            return;
        }

        if (_needsSurfaceRefresh ||
            _currentSurfaceSet is null ||
            !LiquidGlassBackdropManager.IsSurfaceSetCurrent(_currentSurfaceSet) ||
            _pipelineResourceGeneration != LiquidGlassBackdropManager.ResourceGeneration)
        {
            UpdateCompositionPipeline(forceSurfaceRefresh: true);
        }
        else
        {
            SyncRefractionViewportOnly();
        }
    }

    private void OnPanelEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (_isLoaded && GlassVisualHost.ActualWidth >= 2.0 && GlassVisualHost.ActualHeight >= 2.0)
        {
            SyncRefractionViewportOnly();
        }
    }

    private void AttachThemeService()
    {
        if (_themeService is null && Application.Current is App app)
        {
            _themeService = app.Services.GetService<IThemeService>();
        }

        if (_themeService is not null)
        {
            _themeService.LiquidGlassParametersChanged -= OnLiquidGlassParametersChanged;
            _themeService.LiquidGlassParametersChanged += OnLiquidGlassParametersChanged;
            _themeService.ThemeChanged -= OnThemeChanged;
            _themeService.ThemeChanged += OnThemeChanged;
            _themeService.AccentColorChanged -= OnAccentColorChanged;
            _themeService.AccentColorChanged += OnAccentColorChanged;
        }
    }

    private void DetachThemeService()
    {
        if (_themeService is not null)
        {
            _themeService.LiquidGlassParametersChanged -= OnLiquidGlassParametersChanged;
            _themeService.ThemeChanged -= OnThemeChanged;
            _themeService.AccentColorChanged -= OnAccentColorChanged;
        }
    }

    private void OnAccentColorChanged(object? sender, string accentHex)
    {
        if (UseAccentTint && _isLoaded)
        {
            EnqueueUpdate(forceSurfaceRefresh: false);
        }
    }

    private void AttachXamlRootTracking()
    {
        var root = XamlRoot;
        if (ReferenceEquals(_subscribedXamlRoot, root))
        {
            return;
        }

        DetachXamlRootTracking();
        if (root is not null)
        {
            _subscribedXamlRoot = root;
            _rasterizationScale = (float)Math.Clamp(root.RasterizationScale, 1.0, 4.0);
            _subscribedXamlRoot.Changed += OnXamlRootChanged;
        }
    }

    private void DetachXamlRootTracking()
    {
        if (_subscribedXamlRoot is not null)
        {
            _subscribedXamlRoot.Changed -= OnXamlRootChanged;
            _subscribedXamlRoot = null;
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        float nextScale = (float)Math.Clamp(sender.RasterizationScale, 1.0, 4.0);
        if (MathF.Abs(nextScale - _rasterizationScale) > 0.01f)
        {
            _rasterizationScale = nextScale;
            UpdateCompositionPipeline(forceSurfaceRefresh: true);
        }
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isLoaded)
        {
            UpdateCompositionPipeline(forceSurfaceRefresh: true);
        }
    }

    private void OnLiquidGlassParametersChanged(object? sender, LiquidGlassParameters e)
    {
        EnqueueUpdate(forceSurfaceRefresh: true);
    }

    private void OnThemeChanged(object? sender, ThemePaletteDefinition e)
    {
        EnqueueUpdate(forceSurfaceRefresh: true);
    }

    private void OnGpuResourcesInvalidated(object? sender, EventArgs e)
    {
        EnqueueUpdate(forceSurfaceRefresh: true);
    }

    private void EnqueueUpdate(bool forceSurfaceRefresh)
    {
        if (!_isLoaded)
        {
            return;
        }

        if (forceSurfaceRefresh)
        {
            _needsSurfaceRefresh = true;
        }

        if (DispatcherQueue is { } dq)
        {
            if (dq.HasThreadAccess)
            {
                bool shouldForce = forceSurfaceRefresh || _needsSurfaceRefresh;
                UpdateCompositionPipeline(shouldForce);
            }
            else
            {
                _queuedForceSurfaceRefresh |= forceSurfaceRefresh;
                if (!_isDispatcherUpdateQueued)
                {
                    _isDispatcherUpdateQueued = true;
                    if (!dq.TryEnqueue(() =>
                    {
                        _isDispatcherUpdateQueued = false;
                        bool force = _queuedForceSurfaceRefresh || _needsSurfaceRefresh;
                        _queuedForceSurfaceRefresh = false;
                        if (_isLoaded)
                        {
                            UpdateCompositionPipeline(force);
                        }
                    }))
                    {
                        _isDispatcherUpdateQueued = false;
                    }
                }
            }
        }
    }

    private LiquidGlassPreset ResolveEffectivePreset(float w, float h, float cornerRadius)
    {
        if (ReadLocalValue(PresetProperty) != DependencyProperty.UnsetValue)
        {
            return Preset;
        }

        float minDim = Math.Min(w, h);
        if (MathF.Abs(w - h) <= 6.0f && cornerRadius >= minDim * 0.44f)
        {
            return LiquidGlassPreset.Button;
        }

        if (TintOpacityOverride is >= 0.0 and <= 0.30)
        {
            return LiquidGlassPreset.TabBarItem;
        }

        if (w >= 260.0f && h <= 64.0f && cornerRadius >= minDim * 0.42f)
        {
            return LiquidGlassPreset.TabBar;
        }

        return LiquidGlassPreset.Panel;
    }

    private void UpdateCompositionPipeline(bool forceSurfaceRefresh)
    {
        if (!_isLoaded)
        {
            return;
        }

        if (_isUpdatingPipeline)
        {
            if (forceSurfaceRefresh)
            {
                _needsSurfaceRefresh = true;
            }

            return;
        }

        bool needsPostResetPass = false;
        _isUpdatingPipeline = true;
        try
        {
            UpdateCompositionPipelineCore(forceSurfaceRefresh);
            needsPostResetPass = _isLoaded &&
                                 _rootContainerVisual is not null &&
                                 _pipelineResourceGeneration != LiquidGlassBackdropManager.ResourceGeneration;
        }
        finally
        {
            _isUpdatingPipeline = false;
        }

        if (needsPostResetPass)
        {
            _isUpdatingPipeline = true;
            try
            {
                UpdateCompositionPipelineCore(forceSurfaceRefresh: true);
            }
            finally
            {
                _isUpdatingPipeline = false;
            }
        }
    }

    private void UpdateCompositionPipelineCore(bool forceSurfaceRefresh)
    {
        if (!_isLoaded)
        {
            return;
        }

        if (Visibility != Visibility.Visible)
        {
            if (_rootContainerVisual is not null || _currentSurfaceSet is not null)
            {
                DisposeCompositionPipeline();
            }

            _needsSurfaceRefresh = true;
            return;
        }

        var glass = _themeService?.LiquidGlass ?? new LiquidGlassParameters();
        bool isDarkTheme = _themeService?.CurrentTheme.IsDark ?? true;
        bool useLiveComposition = glass.IsShaderEnabled && (SampleLiveBackdrop || IsDraggableLens);

        if (!useLiveComposition)
        {
            BackdropHost.Visibility = Visibility.Visible;
            if (_rootContainerVisual is not null)
            {
                _rootContainerVisual.IsVisible = false;
            }
            return;
        }

        double width = GlassVisualHost.ActualWidth;
        double height = GlassVisualHost.ActualHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 2.0 || height < 2.0)
        {
            if (_rootContainerVisual is not null || _currentSurfaceSet is not null)
            {
                DisposeCompositionPipeline();
            }

            _needsSurfaceRefresh = true;
            BackdropHost.Visibility = Visibility.Collapsed;
            return;
        }

        long activeResourceGeneration = LiquidGlassBackdropManager.ResourceGeneration;
        if (_rootContainerVisual is not null && _pipelineResourceGeneration != activeResourceGeneration)
        {
            DisposeCompositionPipeline();
            forceSurfaceRefresh = true;
        }

        EnsureCompositionPipeline();
        if (_compositor is null ||
            _rootContainerVisual is null ||
            _glassVisual is null ||
            _specularVisual is null ||
            _unifiedEffectBrush is null ||
            _baseOnlyEffectBrush is null ||
            _clipGeometry is null ||
            _rimMaskBrush is null ||
            _shapeClipMaskBrush is null ||
            _specularBrush is null)
        {
            BackdropHost.Visibility = Visibility.Visible;
            _needsSurfaceRefresh = true;
            return;
        }

        if (!ReferenceEquals(ElementCompositionPreview.GetElementChildVisual(GlassVisualHost), _rootContainerVisual))
        {
            ElementCompositionPreview.SetElementChildVisual(GlassVisualHost, _rootContainerVisual);
        }

        float w = (float)width;
        float h = (float)height;
        var sizeVec = new Vector2(w, h);
        float cornerRadius = Math.Clamp((float)GlassCornerRadius, 0.0f, Math.Min(w, h) * 0.5f);

        _rootContainerVisual.Size = sizeVec;
        _glassVisual.Size = sizeVec;
        _specularVisual.Size = sizeVec;
        _clipGeometry.Size = sizeVec;
        _clipGeometry.CornerRadius = new Vector2(cornerRadius, cornerRadius);

        var metrics = LiquidGlassBackdropManager.ComputeLensMetrics(
            w,
            h,
            glass.DistortionStrength,
            glass.SpecularRimIntensity);

        float blurAmount = (float)Math.Clamp(glass.FrostBlurRadius, 0.0, 30.0);
        float baseSat = (float)Math.Clamp(glass.SaturationBoost > 0 ? glass.SaturationBoost : (isDarkTheme ? 1.60 : 1.58), 0.5, 2.0);
        float rimBlurAmount = Math.Max(0.35f, blurAmount * 0.50f);

        _unifiedEffectBrush.Properties.InsertScalar("BaseFrostBlur.BlurAmount", blurAmount);
        _unifiedEffectBrush.Properties.InsertScalar("BaseVibrancy.Saturation", baseSat);
        _unifiedEffectBrush.Properties.InsertScalar("RimFrostBlur.BlurAmount", rimBlurAmount);
        _unifiedEffectBrush.Properties.InsertScalar("RimVibrancy.Saturation", metrics.RimSaturation);
        _unifiedEffectBrush.Properties.InsertScalar("RimRefractionOpacity.Opacity", metrics.RimRefractionOpacity);

        _baseOnlyEffectBrush.Properties.InsertScalar("BaseFrostBlur.BlurAmount", blurAmount);
        _baseOnlyEffectBrush.Properties.InsertScalar("BaseVibrancy.Saturation", baseSat);

        // Specular & Fresnel intensity is encoded directly inside LiquidGlassMain.cso's closed-form output (RenderMode = 1.0)
        _specularVisual.Opacity = glass.IsShaderEnabled ? 1.0f : 0.0f;

        float scaleNow = XamlRoot is { } xr
            ? (float)Math.Clamp(xr.RasterizationScale, 1.0, 4.0)
            : _rasterizationScale;
        _rasterizationScale = scaleNow;

        var effectivePreset = ResolveEffectivePreset(w, h, cornerRadius);
        double? tintOverride = TintOpacityOverride >= 0.0 ? TintOpacityOverride : null;

        bool surfaceNotCurrent = !LiquidGlassBackdropManager.IsSurfaceSetCurrent(_currentSurfaceSet);
        bool bezelChanged = _currentSurfaceSet is null || MathF.Abs(_currentSurfaceSet.BezelDip - metrics.BezelDip) >= 1.0f;
        if (forceSurfaceRefresh ||
            _needsSurfaceRefresh ||
            surfaceNotCurrent ||
            bezelChanged ||
            _rimMaskBrush.Surface is null ||
            _shapeClipMaskBrush.Surface is null ||
            _specularBrush.Surface is null)
        {
            var oldSurfaceSet = _currentSurfaceSet;
            var nextSurfaces = LiquidGlassBackdropManager.AcquirePanelTextures(
                this,
                _compositor,
                w,
                h,
                cornerRadius,
                metrics.BezelDip,
                _rasterizationScale,
                isDarkTheme,
                effectivePreset,
                SurfaceProfile,
                (float)ShapeRoundness,
                glass,
                tintOverride,
                HighlightAlphaMultiplier);

            if (nextSurfaces is not null)
            {
                _currentSurfaceSet = nextSurfaces;
                _rimMaskBrush.Surface = nextSurfaces.RimLensingMask;
                _shapeClipMaskBrush.Surface = nextSurfaces.ShapeClipMask;
                _specularBrush.Surface = nextSurfaces.SpecularMap;
                _needsSurfaceRefresh = false;

                if (oldSurfaceSet is not null && !ReferenceEquals(oldSurfaceSet, nextSurfaces))
                {
                    LiquidGlassBackdropManager.ReleasePanelTextures(this, oldSurfaceSet);
                }
            }
            else
            {
                _needsSurfaceRefresh = true;
            }
        }

        if (_currentSurfaceSet is null || !LiquidGlassBackdropManager.IsSurfaceSetAlive(_currentSurfaceSet))
        {
            BackdropHost.Visibility = Visibility.Visible;
            _rootContainerVisual.IsVisible = false;
            _needsSurfaceRefresh = true;
            return;
        }

        BackdropHost.Visibility = Visibility.Collapsed;
        _rootContainerVisual.IsVisible = true;

        if ((_isHovered || _isPressed) && EnablePointerLighting)
        {
            UpdateGpuPointerLightingSurface(w, h, cornerRadius, metrics.BezelDip, effectivePreset, glass, isDarkTheme);
        }
        else if (_interactiveOpticalSurface is not null)
        {
            if (_specularBrush is not null && _currentSurfaceSet is not null)
            {
                _specularBrush.Surface = _currentSurfaceSet.SpecularMap;
            }

            try { _interactiveOpticalSurface.Dispose(); } catch { }
            _interactiveOpticalSurface = null;
            _interactiveSurfaceW = 0;
            _interactiveSurfaceH = 0;
        }

        bool rimActive = SyncRefractionSurfaceAndTransforms(
            w,
            h,
            metrics.InwardShiftDip,
            glass.DistortionStrength);

        _isRimRefractionActive = rimActive;
        _glassVisual.Brush = rimActive ? _unifiedEffectBrush : _baseOnlyEffectBrush;
        ApplyTintColors(glass, isDarkTheme);
    }

    private void UpdateGpuPointerLightingSurface(
        float w,
        float h,
        float cornerRadius,
        float bezelDip,
        LiquidGlassPreset effectivePreset,
        LiquidGlassParameters glass,
        bool isDarkTheme)
    {
        if (_compositor is null ||
            _specularBrush is null ||
            _currentSurfaceSet is null ||
            !LiquidGlassBackdropManager.IsSurfaceSetAlive(_currentSurfaceSet))
        {
            return;
        }

        int pxW = _currentSurfaceSet.PixelWidth;
        int pxH = _currentSurfaceSet.PixelHeight;
        float effectiveScale = pxW / Math.Max(8.0f, w);
        double? tintOverride = TintOpacityOverride >= 0.0 ? TintOpacityOverride : null;

        var material = LiquidGlassBackdropManager.CreateConfiguredMaterial(
            effectivePreset,
            SurfaceProfile,
            cornerRadius,
            bezelDip,
            (float)ShapeRoundness,
            effectiveScale,
            glass,
            isDarkTheme,
            tintOverride,
            HighlightAlphaMultiplier);

        material.PointerUV = _pointerUV;
        material.PointerDelta = _pointerDelta;
        material.PointerActive = _isPressed ? 1.0f : (_isHovered ? 0.85f : 0.0f);
        material.PointerLightInfluence = _isPressed ? 0.92f : 0.72f;
        material.RefractionMultiplier = _isPressed ? 1.18f : 1.08f;
        material.HighlightStrength = Math.Min(1.35f, material.HighlightStrength * (_isPressed ? 1.22f : 1.10f));

        if (LiquidGlassBackdropManager.RenderInteractivePanelOverlay(
            _compositor,
            ref _interactiveOpticalSurface,
            ref _interactiveSurfaceW,
            ref _interactiveSurfaceH,
            pxW,
            pxH,
            material,
            new Vector4(0.0f, 0.0f, 1.0f, 1.0f),
            out var replacedSurface) &&
            _interactiveOpticalSurface is not null)
        {
            _specularBrush.Surface = _interactiveOpticalSurface;
            if (replacedSurface is not null)
            {
                try { replacedSurface.Dispose(); } catch { }
            }
        }
    }

    private void ApplyTintColors(LiquidGlassParameters glass, bool isDarkTheme)
    {
        var tintColor = LiquidGlassBackdropManager.ResolveTintColor(
            glass,
            isDarkTheme,
            _isHovered,
            _isPressed,
            TintOpacityOverride);

        if (UseAccentTint && TryResolveAccentColor(out var accentColor))
        {
            double alphaRatio = TintOpacityOverride >= 0.0
                ? Math.Clamp(TintOpacityOverride, 0.08, 0.85)
                : (isDarkTheme ? 0.34 : 0.28);
            double interactiveBoost = _isPressed ? 0.06 : (_isHovered ? 0.03 : 0.0);
            byte finalAlpha = (byte)Math.Clamp(Math.Round((alphaRatio + interactiveBoost) * 255.0), 0, 255);
            tintColor = Windows.UI.Color.FromArgb(finalAlpha, accentColor.R, accentColor.G, accentColor.B);
        }

        _unifiedEffectBrush?.Properties.InsertColor("GlassTintSource.Color", tintColor);
        _baseOnlyEffectBrush?.Properties.InsertColor("GlassTintSource.Color", tintColor);
    }

    private static bool TryResolveAccentColor(out Windows.UI.Color color)
    {
        if (Application.Current?.Resources.TryGetValue("TokenPrimaryAccentBrush", out var brushObj) == true &&
            brushObj is Microsoft.UI.Xaml.Media.SolidColorBrush solid)
        {
            color = solid.Color;
            return true;
        }

        color = default;
        return false;
    }

    private bool SyncRefractionSurfaceAndTransforms(
        float w,
        float h,
        float inwardShiftDip,
        double distortionStrength)
    {
        if (_refractionSurface is null ||
            inwardShiftDip <= 0.1f ||
            distortionStrength <= 0.5)
        {
            return false;
        }

        var backdropElement = LiquidGlassBackdropManager.ResolveBackdropElement(this, UseNowPlayingCoverBackdrop);
        if (backdropElement is null)
        {
            return false;
        }

        try
        {
            var transform = GlassVisualHost.TransformToVisual(backdropElement);
            var topLeft = transform.TransformPoint(new Point(0, 0));
            var bottomRight = transform.TransformPoint(new Point(w, h));

            float minX = (float)Math.Min(topLeft.X, bottomRight.X);
            float minY = (float)Math.Min(topLeft.Y, bottomRight.Y);
            float srcW = MathF.Max(2.0f, MathF.Abs((float)(bottomRight.X - topLeft.X)));
            float srcH = MathF.Max(2.0f, MathF.Abs((float)(bottomRight.Y - topLeft.Y)));

            if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(srcW) || !float.IsFinite(srcH))
            {
                return false;
            }

            if (backdropElement is FrameworkElement fe)
            {
                if (minX + srcW <= 0.0f || minY + srcH <= 0.0f ||
                    minX >= fe.ActualWidth || minY >= fe.ActualHeight)
                {
                    return false;
                }
            }

            float aspectRatio = Math.Clamp(srcH / Math.Max(1.0f, srcW), 0.28f, 1.0f);
            float shiftX = Math.Min(inwardShiftDip * aspectRatio, srcW * 0.32f);
            float shiftY = Math.Min(inwardShiftDip, srcH * 0.34f);
            float subW = MathF.Max(2.0f, srcW - (2.0f * shiftX));
            float subH = MathF.Max(2.0f, srcH - (2.0f * shiftY));
            float offX = minX + shiftX;
            float offY = minY + shiftY;

            var sourceVisual = ElementCompositionPreview.GetElementVisual(backdropElement);
            _refractionSurface.SourceVisual = sourceVisual;
            _refractionSurface.SourceOffset = new Vector2(offX, offY);
            _refractionSurface.SourceSize = new Vector2(subW, subH);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureCompositionPipeline()
    {
        if (_rootContainerVisual is not null && _unifiedEffectBrush is not null && _baseOnlyEffectBrush is not null)
        {
            return;
        }

        var hostVisual = ElementCompositionPreview.GetElementVisual(GlassVisualHost);
        var compositor = hostVisual.Compositor;
        _compositor = compositor;

        var baseFactory = LiquidGlassBackdropManager.GetOrCreateBaseEffectFactory(compositor);
        var unifiedFactory = LiquidGlassBackdropManager.GetOrCreateUnifiedGlassEffectFactory(compositor);

        _backdropBrush = compositor.CreateBackdropBrush();

        _refractionSurface = compositor.CreateVisualSurface();
        _refractedRimBrush = compositor.CreateSurfaceBrush(_refractionSurface);
        _refractedRimBrush.Stretch = CompositionStretch.Fill;

        _rimMaskBrush = compositor.CreateSurfaceBrush();
        _rimMaskBrush.Stretch = CompositionStretch.Fill;

        _shapeClipMaskBrush = compositor.CreateSurfaceBrush();
        _shapeClipMaskBrush.Stretch = CompositionStretch.Fill;

        _specularBrush = compositor.CreateSurfaceBrush();
        _specularBrush.Stretch = CompositionStretch.Fill;

        _baseOnlyEffectBrush = baseFactory.CreateBrush();
        _baseOnlyEffectBrush.SetSourceParameter("BackdropBase", _backdropBrush);
        _baseOnlyEffectBrush.SetSourceParameter("ShapeClipMask", _shapeClipMaskBrush);

        _unifiedEffectBrush = unifiedFactory.CreateBrush();
        _unifiedEffectBrush.SetSourceParameter("BackdropBase", _backdropBrush);
        _unifiedEffectBrush.SetSourceParameter("RefractedRim", _refractedRimBrush);
        _unifiedEffectBrush.SetSourceParameter("RimLensingMask", _rimMaskBrush);
        _unifiedEffectBrush.SetSourceParameter("ShapeClipMask", _shapeClipMaskBrush);

        _clipGeometry = compositor.CreateRoundedRectangleGeometry();
        _visualClip = compositor.CreateGeometricClip(_clipGeometry);

        _glassVisual = compositor.CreateSpriteVisual();
        _glassVisual.Brush = _baseOnlyEffectBrush;

        _specularVisual = compositor.CreateSpriteVisual();
        _specularVisual.Brush = _specularBrush;
        _specularVisual.Opacity = 1.0f;

        _rootContainerVisual = compositor.CreateContainerVisual();
        _rootContainerVisual.Clip = _visualClip;
        _rootContainerVisual.Children.InsertAtTop(_glassVisual);
        _rootContainerVisual.Children.InsertAtTop(_specularVisual);

        _pipelineResourceGeneration = LiquidGlassBackdropManager.ResourceGeneration;
        ElementCompositionPreview.SetElementChildVisual(GlassVisualHost, _rootContainerVisual);
    }

    private void DisposeCompositionPipeline()
    {
        if (GlassVisualHost is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(GlassVisualHost, null);
        }

        if (_rimMaskBrush is not null) _rimMaskBrush.Surface = null;
        if (_shapeClipMaskBrush is not null) _shapeClipMaskBrush.Surface = null;
        if (_specularBrush is not null) _specularBrush.Surface = null;
        if (_refractedRimBrush is not null) _refractedRimBrush.Surface = null;
        if (_refractionSurface is not null) _refractionSurface.SourceVisual = null;

        var surfaceSetToRelease = _currentSurfaceSet;
        _currentSurfaceSet = null;

        _specularVisual?.Dispose();
        _glassVisual?.Dispose();
        _rootContainerVisual?.Dispose();
        _visualClip?.Dispose();
        _clipGeometry?.Dispose();
        _unifiedEffectBrush?.Dispose();
        _baseOnlyEffectBrush?.Dispose();
        _backdropBrush?.Dispose();
        _refractedRimBrush?.Dispose();
        _refractionSurface?.Dispose();
        _rimMaskBrush?.Dispose();
        _shapeClipMaskBrush?.Dispose();
        _specularBrush?.Dispose();
        _interactiveOpticalSurface?.Dispose();

        _specularVisual = null;
        _glassVisual = null;
        _rootContainerVisual = null;
        _visualClip = null;
        _clipGeometry = null;
        _unifiedEffectBrush = null;
        _baseOnlyEffectBrush = null;
        _backdropBrush = null;
        _refractedRimBrush = null;
        _refractionSurface = null;
        _rimMaskBrush = null;
        _shapeClipMaskBrush = null;
        _specularBrush = null;
        _interactiveOpticalSurface = null;
        _interactiveSurfaceW = 0;
        _interactiveSurfaceH = 0;
        _compositor = null;
        _pipelineResourceGeneration = -1;
        _isRimRefractionActive = false;

        if (surfaceSetToRelease is not null)
        {
            LiquidGlassBackdropManager.ReleasePanelTextures(this, surfaceSetToRelease);
        }
    }

    private void UpdatePointerPosition(PointerRoutedEventArgs e)
    {
        double w = GlassVisualHost.ActualWidth;
        double h = GlassVisualHost.ActualHeight;
        if (w < 2.0 || h < 2.0)
        {
            return;
        }

        var pt = e.GetCurrentPoint(GlassVisualHost).Position;
        var nextUV = new Vector2(
            Math.Clamp((float)(pt.X / w), 0.0f, 1.0f),
            Math.Clamp((float)(pt.Y / h), 0.0f, 1.0f));

        _pointerDelta = nextUV - _pointerUV;
        _pointerUV = nextUV;
    }

    private void OnPanelPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;
        UpdatePointerPosition(e);
        UpdateInteractiveState(refreshPointerShader: true);
    }

    private void OnPanelPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        _isPressed = false;
        _pointerDelta = Vector2.Zero;
        UpdateInteractiveState(refreshPointerShader: false);
    }

    private void OnPanelPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = true;
        UpdatePointerPosition(e);

        if (IsDraggableLens && Parent is UIElement parentElement)
        {
            _isDraggingLens = true;
            _dragStartPointerPos = e.GetCurrentPoint(parentElement).Position;
            _dragStartLeft = double.IsNaN(Canvas.GetLeft(this)) ? 0.0 : Canvas.GetLeft(this);
            _dragStartTop = double.IsNaN(Canvas.GetTop(this)) ? 0.0 : Canvas.GetTop(this);
            CapturePointer(e.Pointer);
            e.Handled = true;
        }

        UpdateInteractiveState(refreshPointerShader: true);
    }

    private void OnPanelPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isHovered || _isPressed)
        {
            UpdatePointerPosition(e);
            long nowTick = Environment.TickCount64;
            if (EnablePointerLighting && nowTick - _lastPointerShaderTick >= 16)
            {
                _lastPointerShaderTick = nowTick;
                UpdateInteractiveState(refreshPointerShader: true);
            }
        }

        if (!_isDraggingLens || !IsDraggableLens || Parent is not FrameworkElement parentElement)
        {
            return;
        }

        var current = e.GetCurrentPoint(parentElement).Position;
        double deltaX = current.X - _dragStartPointerPos.X;
        double deltaY = current.Y - _dragStartPointerPos.Y;

        double maxX = Math.Max(0.0, parentElement.ActualWidth - ActualWidth);
        double maxY = Math.Max(0.0, parentElement.ActualHeight - ActualHeight);

        double targetX = Math.Clamp(_dragStartLeft + deltaX, 0.0, maxX);
        double targetY = Math.Clamp(_dragStartTop + deltaY, 0.0, maxY);

        Canvas.SetLeft(this, targetX);
        Canvas.SetTop(this, targetY);
        SyncRefractionViewportOnly();
        e.Handled = true;
    }

    private void OnPanelPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingLens)
        {
            _isDraggingLens = false;
            ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        _isPressed = false;
        UpdatePointerPosition(e);
        UpdateInteractiveState(refreshPointerShader: true);
    }

    private void OnPanelPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingLens = false;
        _isPressed = false;
        UpdateInteractiveState(refreshPointerShader: false);
    }

    private void OnPanelPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingLens = false;
        _isPressed = false;
        UpdateInteractiveState(refreshPointerShader: false);
    }

    private void UpdateInteractiveState(bool refreshPointerShader)
    {
        if (_isLoaded && Visibility == Visibility.Visible)
        {
            if (_unifiedEffectBrush is null ||
                _baseOnlyEffectBrush is null ||
                _specularVisual is null ||
                _pipelineResourceGeneration != LiquidGlassBackdropManager.ResourceGeneration ||
                !LiquidGlassBackdropManager.IsSurfaceSetCurrent(_currentSurfaceSet))
            {
                UpdateCompositionPipeline(forceSurfaceRefresh: true);
            }
            else
            {
                var glass = _themeService?.LiquidGlass ?? new LiquidGlassParameters();
                bool isDarkTheme = _themeService?.CurrentTheme.IsDark ?? true;

                ApplyTintColors(glass, isDarkTheme);
                _specularVisual.Opacity = glass.IsShaderEnabled ? 1.0f : 0.0f;

                if ((_isHovered || _isPressed) && refreshPointerShader && EnablePointerLighting)
                {
                    float w = (float)GlassVisualHost.ActualWidth;
                    float h = (float)GlassVisualHost.ActualHeight;
                    if (w >= 2.0f && h >= 2.0f)
                    {
                        float cornerRadius = Math.Clamp((float)GlassCornerRadius, 0.0f, Math.Min(w, h) * 0.5f);
                        var metrics = LiquidGlassBackdropManager.ComputeLensMetrics(
                            w,
                            h,
                            glass.DistortionStrength,
                            glass.SpecularRimIntensity);
                        var effectivePreset = ResolveEffectivePreset(w, h, cornerRadius);
                        UpdateGpuPointerLightingSurface(w, h, cornerRadius, metrics.BezelDip, effectivePreset, glass, isDarkTheme);
                    }
                }
                else if (!_isHovered && !_isPressed &&
                         _specularBrush is not null &&
                         _currentSurfaceSet is not null &&
                         LiquidGlassBackdropManager.IsSurfaceSetAlive(_currentSurfaceSet))
                {
                    _specularBrush.Surface = _currentSurfaceSet.SpecularMap;
                    if (_interactiveOpticalSurface is not null)
                    {
                        try { _interactiveOpticalSurface.Dispose(); } catch { }
                        _interactiveOpticalSurface = null;
                        _interactiveSurfaceW = 0;
                        _interactiveSurfaceH = 0;
                    }
                }
            }
        }

        if (EnableHoverScale)
        {
            AnimateHoverSpringScale();
        }
    }

    private void AnimateHoverSpringScale()
    {
        if (ActualWidth < 2.0 || ActualHeight < 2.0)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(this);
        var compositor = visual.Compositor;

        visual.CenterPoint = new Vector3((float)(ActualWidth * 0.5), (float)(ActualHeight * 0.5), 0.0f);

        float target = _isPressed ? 0.955f : (_isHovered ? 1.022f : 1.0f);

        using var spring = compositor.CreateSpringVector3Animation();
        spring.FinalValue = new Vector3(target, target, 1.0f);
        spring.DampingRatio = _isPressed ? 0.62f : 0.48f;
        spring.Period = TimeSpan.FromMilliseconds(48);
        visual.StartAnimation(nameof(visual.Scale), spring);
    }
}
