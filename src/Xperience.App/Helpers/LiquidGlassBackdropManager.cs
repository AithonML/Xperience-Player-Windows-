using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Xperience.App.Controls;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Helpers;

/// <summary>
/// Manages the GPU-accelerated HLSL Liquid Glass optical pipeline for WinUI 3 (<see cref="Microsoft.UI.Composition"/>).
/// Replaces all legacy CPU SDF/pixel loops with compiled Direct2D1 ps_4_0 shaders:
/// - Pass 1 (<c>LiquidGlassBlurHorizontal.cso</c> -> <c>LiquidGlassBlurVertical.cso</c> -> <c>LiquidGlassPostProcess.cso</c>):
///   21-tap separable Gaussian blur with MirrorUV boundary reflection + Bloom, Exposure, Temperature, Vibrance, Saturation.
/// - Pass 2 (<c>LiquidGlassMain.cso</c> / <c>shader_28.asm</c>):
///   Continuous G2 superellipse SDF (<c>ShapeRoundness</c>), 4 3D surface profiles (<c>ConvexSquircle</c>, <c>ConvexCircle</c>,
///   <c>Concave</c>, <c>Lip</c>), 3D Snell's Law refraction, 8-tap caustic differential filter, 7-tap R/G/B spectral chromatic
///   dispersion, <c>InnerShadow</c>, <c>BorderThickness</c>, <c>Specular</c> with <c>SpecularSaturation</c>, dynamic
///   <c>PointerLightInfluence</c>, 5th-order peripheral <c>RefFresnel</c>, and 5th-order anisotropic <c>Glare</c>.
/// </summary>
public static class LiquidGlassBackdropManager
{
    private const int MaxIdleCachedSurfaceSets = 16;

    private static readonly object SyncLock = new();
    private static readonly Dictionary<MaskCacheKey, CachedSurfaceEntry> SurfaceCache = new();
    private static readonly LinkedList<MaskCacheKey> CacheLru = new();
    private static readonly List<PanelSurfaceSet> DetachedOwnedSets = new();
    private static readonly List<WeakReference<LiquidGlassPanel>> ActivePanels = new();
    private static readonly LiquidGlassGpuShaderEngine GpuEngine = new();

    private static WeakReference<UIElement>? _mainWorkspaceRef;
    private static WeakReference<UIElement>? _nowPlayingArtworkRef;
    private static WeakReference<UIElement>? _fullscreenStageRef;
    private static WeakReference<UIElement>? _homeAuroraRef;
    private static WeakReference<UIElement>? _artistBackdropRef;
    private static WeakReference<UIElement>? _fullscreenOverlayRef;

    private static Compositor? _cachedCompositor;
    private static CompositionEffectFactory? _cachedBaseEffectFactory;
    private static CompositionEffectFactory? _cachedUnifiedGlassEffectFactory;
    private static CanvasDevice? _trackedDevice;
    private static long _resourceGeneration = 1;
    private static long _paletteGeneration = 1;

    static LiquidGlassBackdropManager()
    {
        GpuEngine.RenderingDeviceReplaced += OnGpuRenderingDeviceReplaced;
    }

    public sealed class ManagerInstanceFacade
    {
        public Compositor? Compositor => _cachedCompositor;
        public LiquidGlassGpuShaderEngine GpuShaderEngine => GpuEngine;
    }

    public static ManagerInstanceFacade Instance { get; } = new();

    internal static long ResourceGeneration => Volatile.Read(ref _resourceGeneration);
    internal static long PaletteGeneration => Volatile.Read(ref _paletteGeneration);

    /// <summary>
    /// Raised if the underlying Direct2D / Composition device resets or the ambient palette changes
    /// and cached GPU surfaces need to be rebound on active <see cref="LiquidGlassPanel"/> instances.
    /// </summary>
    public static event EventHandler? GpuResourcesInvalidated;

    internal sealed class PanelSurfaceSet
    {
        public required CompositionDrawingSurface RimLensingMask { get; init; }
        public required CompositionDrawingSurface ShapeClipMask { get; init; }
        public required CompositionDrawingSurface SpecularMap { get; init; }
        public float BezelDip { get; init; }
        public int PixelWidth { get; init; }
        public int PixelHeight { get; init; }
        public long ResourceGeneration { get; init; }
        public long PaletteGeneration { get; init; }

        internal readonly List<WeakReference<LiquidGlassPanel>> Owners = new();
        internal bool InCache;
        internal bool IsDisposed;

        internal void AttachOwnerLocked(LiquidGlassPanel panel)
        {
            for (int i = Owners.Count - 1; i >= 0; i--)
            {
                if (!Owners[i].TryGetTarget(out var existing))
                {
                    Owners.RemoveAt(i);
                }
                else if (ReferenceEquals(existing, panel))
                {
                    return;
                }
            }

            Owners.Add(new WeakReference<LiquidGlassPanel>(panel));
        }

        internal void DetachOwnerLocked(LiquidGlassPanel panel)
        {
            for (int i = Owners.Count - 1; i >= 0; i--)
            {
                if (!Owners[i].TryGetTarget(out var existing) || ReferenceEquals(existing, panel))
                {
                    Owners.RemoveAt(i);
                }
            }
        }

        internal bool HasLiveOwnersLocked()
        {
            for (int i = Owners.Count - 1; i >= 0; i--)
            {
                if (!Owners[i].TryGetTarget(out _))
                {
                    Owners.RemoveAt(i);
                }
            }

            return Owners.Count > 0;
        }
    }

    /// <summary>
    /// Optical bezel metrics and isotropic inward displacement in DIPs for a Liquid Glass surface.
    /// </summary>
    public readonly record struct LensOpticalMetrics(
        float RimRefractionOpacity,
        float RimSaturation,
        float BezelDip,
        float InwardShiftDip);

    private sealed class CachedSurfaceEntry
    {
        public required PanelSurfaceSet Surfaces { get; init; }
        public required LinkedListNode<MaskCacheKey> LruNode { get; init; }
    }

    private readonly record struct MaskCacheKey(
        int PixelWidth,
        int PixelHeight,
        int CornerRadiusQ,
        int BezelQ,
        int PresetId,
        int ProfileId,
        int RoundnessQ,
        int HighlightQ,
        int DispersionQ,
        int RefractionQ,
        int BlurQ,
        int InnerShadowQ,
        int TintAlphaQ,
        int TintRgb24,
        int SaturationQ,
        bool IsDark);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="set"/> is still backed by live, undisposed
    /// GPU surfaces matching the active device and ambient palette generations.
    /// </summary>
    internal static bool IsSurfaceSetCurrent(PanelSurfaceSet? set)
    {
        if (set is null)
        {
            return false;
        }

        lock (SyncLock)
        {
            return !set.IsDisposed &&
                   set.ResourceGeneration == _resourceGeneration &&
                   set.PaletteGeneration == _paletteGeneration;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="set"/> has not been disposed and belongs to
    /// the current Direct2D / Composition device generation.
    /// </summary>
    internal static bool IsSurfaceSetAlive(PanelSurfaceSet? set)
    {
        if (set is null)
        {
            return false;
        }

        lock (SyncLock)
        {
            return !set.IsDisposed && set.ResourceGeneration == _resourceGeneration;
        }
    }

    /// <summary>
    /// Updates the live ambient song palette feeding Pass 1 (<c>shader_0</c> -> <c>shader_43</c>) on the GPU.
    /// </summary>
    public static void UpdateAmbientPalette(Color top, Color middle, Color bottom, Color accent)
    {
        if (!GpuEngine.UpdateAmbientPalette(top, middle, bottom, accent))
        {
            return;
        }

        lock (SyncLock)
        {
            _paletteGeneration++;
            InvalidateSurfaceCacheLocked();
        }

        GpuResourcesInvalidated?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Registers the primary UI stage visual layers used as cycle-free GPU backdrop sources for
    /// <see cref="CompositionVisualSurface"/> rim refraction across overlays and scrolled sections.
    /// </summary>
    public static void RegisterStageBackdrops(
        UIElement? mainWorkspace,
        UIElement? nowPlayingArtwork,
        UIElement? fullscreenStage,
        UIElement? homeAurora,
        UIElement? artistBackdrop,
        UIElement? fullscreenOverlay = null)
    {
        lock (SyncLock)
        {
            _mainWorkspaceRef = mainWorkspace is not null ? new WeakReference<UIElement>(mainWorkspace) : null;
            _nowPlayingArtworkRef = nowPlayingArtwork is not null ? new WeakReference<UIElement>(nowPlayingArtwork) : null;
            _fullscreenStageRef = fullscreenStage is not null ? new WeakReference<UIElement>(fullscreenStage) : null;
            _homeAuroraRef = homeAurora is not null ? new WeakReference<UIElement>(homeAurora) : null;
            _artistBackdropRef = artistBackdrop is not null ? new WeakReference<UIElement>(artistBackdrop) : null;
            _fullscreenOverlayRef = fullscreenOverlay is not null ? new WeakReference<UIElement>(fullscreenOverlay) : null;
        }

        NotifyViewportScrolled();
    }

    internal static void RegisterPanel(LiquidGlassPanel panel)
    {
        lock (SyncLock)
        {
            for (int i = ActivePanels.Count - 1; i >= 0; i--)
            {
                if (!ActivePanels[i].TryGetTarget(out var existing))
                {
                    ActivePanels.RemoveAt(i);
                }
                else if (ReferenceEquals(existing, panel))
                {
                    return;
                }
            }

            ActivePanels.Add(new WeakReference<LiquidGlassPanel>(panel));
        }
    }

    internal static void UnregisterPanel(LiquidGlassPanel panel)
    {
        lock (SyncLock)
        {
            for (int i = ActivePanels.Count - 1; i >= 0; i--)
            {
                if (!ActivePanels[i].TryGetTarget(out var existing) || ReferenceEquals(existing, panel))
                {
                    ActivePanels.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Synchronizes viewport offsets on active loaded <see cref="LiquidGlassPanel"/> instances when
    /// scroll viewers or overlays shift relative to stage backdrops.
    /// </summary>
    public static void NotifyViewportScrolled()
    {
        List<LiquidGlassPanel> snapshot;
        lock (SyncLock)
        {
            snapshot = new List<LiquidGlassPanel>(ActivePanels.Count);
            for (int i = ActivePanels.Count - 1; i >= 0; i--)
            {
                if (ActivePanels[i].TryGetTarget(out var panel))
                {
                    snapshot.Add(panel);
                }
                else
                {
                    ActivePanels.RemoveAt(i);
                }
            }
        }

        foreach (var panel in snapshot)
        {
            panel.SyncRefractionViewportOnly();
        }
    }

    /// <summary>
    /// Resolves the highest-fidelity non-ancestor backdrop <see cref="UIElement"/> behind a specific
    /// <see cref="LiquidGlassPanel"/> instance so <see cref="CompositionVisualSurface"/> never forms
    /// a visual-tree cycle.
    /// </summary>
    public static UIElement? ResolveBackdropElement(UIElement glassPanel, bool useNowPlayingCover)
    {
        if (glassPanel is null)
        {
            return null;
        }

        if (useNowPlayingCover &&
            TryGetUsableBackdrop(_nowPlayingArtworkRef, glassPanel, out var nowPlayingBackdrop))
        {
            return nowPlayingBackdrop;
        }

        if (TryResolveSlicerButtonsBackdrop(glassPanel, out var slicerButtons))
        {
            return slicerButtons;
        }

        if (TryResolveVolumeTrackBackdrop(glassPanel, out var volumeTrack))
        {
            return volumeTrack;
        }

        if (TryResolveCardArtworkBackdrop(glassPanel, out var cardArtwork))
        {
            return cardArtwork;
        }

        WeakReference<UIElement>? fsOverlayRef;
        WeakReference<UIElement>? fsStageRef;
        WeakReference<UIElement>? mainWorkspaceRef;
        WeakReference<UIElement>? artistBackdropRef;
        WeakReference<UIElement>? homeAuroraRef;

        lock (SyncLock)
        {
            fsOverlayRef = _fullscreenOverlayRef;
            fsStageRef = _fullscreenStageRef;
            mainWorkspaceRef = _mainWorkspaceRef;
            artistBackdropRef = _artistBackdropRef;
            homeAuroraRef = _homeAuroraRef;
        }

        if (fsOverlayRef is not null &&
            fsOverlayRef.TryGetTarget(out var fsOverlay) &&
            IsVisualAncestor(fsOverlay, glassPanel) &&
            TryGetUsableBackdrop(fsStageRef, glassPanel, out var fsStage))
        {
            return fsStage;
        }

        if (TryGetUsableBackdrop(mainWorkspaceRef, glassPanel, out var mainWorkspace))
        {
            return mainWorkspace;
        }

        if (artistBackdropRef is not null &&
            TryGetUsableBackdrop(artistBackdropRef, glassPanel, out var artistBackdrop) &&
            VisualTreeHelper.GetParent(artistBackdrop) is UIElement artistHeaderGrid &&
            IsVisualAncestor(artistHeaderGrid, glassPanel))
        {
            return artistBackdrop;
        }

        if (TryGetUsableBackdrop(homeAuroraRef, glassPanel, out var homeAurora))
        {
            return homeAurora;
        }

        return null;
    }

    private static bool TryGetUsableBackdrop(
        WeakReference<UIElement>? targetRef,
        UIElement glassPanel,
        out UIElement backdrop)
    {
        backdrop = null!;
        if (targetRef is null || !targetRef.TryGetTarget(out var candidate))
        {
            return false;
        }

        if (!IsElementVisible(candidate, glassPanel.XamlRoot))
        {
            return false;
        }

        if (IsVisualAncestor(candidate, glassPanel))
        {
            return false;
        }

        backdrop = candidate;
        return true;
    }

    private static bool TryResolveSlicerButtonsBackdrop(UIElement glassPanel, out UIElement buttonsPanel)
    {
        buttonsPanel = null!;
        DependencyObject? parent = VisualTreeHelper.GetParent(glassPanel);
        if (parent is FrameworkElement hostFe &&
            hostFe.Name.EndsWith("IndicatorHost", StringComparison.Ordinal) &&
            VisualTreeHelper.GetParent(hostFe) is Panel slicerContainer &&
            slicerContainer is FrameworkElement containerFe &&
            containerFe.Name.EndsWith("SlicerContainer", StringComparison.Ordinal) &&
            slicerContainer.Children.Count >= 2)
        {
            UIElement candidate = slicerContainer.Children[1];
            if (IsElementVisible(candidate, glassPanel.XamlRoot) && !IsVisualAncestor(candidate, glassPanel))
            {
                buttonsPanel = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveVolumeTrackBackdrop(UIElement glassPanel, out UIElement trackCapsule)
    {
        trackCapsule = null!;
        DependencyObject? cur = VisualTreeHelper.GetParent(glassPanel);
        bool sawHeldThumb = false;
        for (int depth = 0; cur is not null && depth < 5; depth++)
        {
            if (cur is FrameworkElement fe)
            {
                if (string.Equals(fe.Name, "HeldLiquidGlassThumb", StringComparison.Ordinal))
                {
                    sawHeldThumb = true;
                }
                else if (sawHeldThumb &&
                         string.Equals(fe.Name, "RootSliderGrid", StringComparison.Ordinal) &&
                         fe is Panel sliderGrid &&
                         sliderGrid.Children.Count >= 1)
                {
                    UIElement candidate = sliderGrid.Children[0];
                    if (IsElementVisible(candidate, glassPanel.XamlRoot) && !IsVisualAncestor(candidate, glassPanel))
                    {
                        trackCapsule = candidate;
                        return true;
                    }

                    return false;
                }
            }

            cur = VisualTreeHelper.GetParent(cur);
        }

        return false;
    }

    private static bool TryResolveCardArtworkBackdrop(UIElement glassPanel, out UIElement artworkElement)
    {
        artworkElement = null!;
        DependencyObject? parent = VisualTreeHelper.GetParent(glassPanel);
        if (parent is StackPanel && VisualTreeHelper.GetParent(parent) is Grid parentGrid)
        {
            parent = parentGrid;
        }

        if (parent is Grid cardGrid &&
            cardGrid.Children.Count >= 2 &&
            cardGrid.ActualWidth is >= 100.0 and <= 360.0 &&
            cardGrid.ActualHeight is >= 100.0 and <= 420.0)
        {
            for (int i = 0; i < cardGrid.Children.Count; i++)
            {
                UIElement child = cardGrid.Children[i];
                if (ReferenceEquals(child, glassPanel) || IsVisualAncestor(child, glassPanel))
                {
                    break;
                }

                if (child is FrameworkElement fe &&
                    fe.Visibility == Visibility.Visible &&
                    fe.Opacity > 0.25 &&
                    fe.ActualWidth >= cardGrid.ActualWidth * 0.85 &&
                    fe.ActualHeight >= cardGrid.ActualHeight * 0.85)
                {
                    artworkElement = child;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsElementVisible(UIElement? element, XamlRoot? panelXamlRoot)
    {
        if (element is null || element.Visibility != Visibility.Visible || element.Opacity <= 0.001)
        {
            return false;
        }

        if (panelXamlRoot is not null && element.XamlRoot is not null && !ReferenceEquals(panelXamlRoot, element.XamlRoot))
        {
            return false;
        }

        if (element is FrameworkElement fe && (fe.ActualWidth < 2.0 || fe.ActualHeight < 2.0))
        {
            return false;
        }

        return true;
    }

    internal static bool IsVisualAncestor(DependencyObject potentialAncestor, DependencyObject target)
    {
        DependencyObject? cur = target;
        while (cur is not null)
        {
            if (ReferenceEquals(cur, potentialAncestor))
            {
                return true;
            }

            cur = VisualTreeHelper.GetParent(cur);
        }

        return false;
    }

    /// <summary>
    /// Gets or creates the shared single-source frosted-backdrop <see cref="CompositionEffectFactory"/>
    /// masked by the GPU-computed superellipse <c>ShapeClipMask</c> (<c>RenderMode = 3.0</c>).
    /// </summary>
    public static CompositionEffectFactory GetOrCreateBaseEffectFactory(Compositor compositor)
    {
        ArgumentNullException.ThrowIfNull(compositor);

        lock (SyncLock)
        {
            EnsureGpuEngineLocked(compositor);

            if (_cachedBaseEffectFactory is not null && ReferenceEquals(_cachedCompositor, compositor))
            {
                return _cachedBaseEffectFactory;
            }

            using var vibrancyEffect = new SaturationEffect
            {
                Name = "BaseVibrancy",
                Saturation = 1.60f,
                Source = new CompositionEffectSourceParameter("BackdropBase")
            };

            using var frostBlur = new GaussianBlurEffect
            {
                Name = "BaseFrostBlur",
                BlurAmount = 2.0f,
                Optimization = EffectOptimization.Balanced,
                BorderMode = EffectBorderMode.Hard,
                Source = vibrancyEffect
            };

            using var glassTint = new ColorSourceEffect
            {
                Name = "GlassTintSource",
                Color = Color.FromArgb(128, 0x1A, 0x1B, 0x20)
            };

            using var baseComposite = new CompositeEffect
            {
                Mode = CanvasComposite.SourceOver
            };
            baseComposite.Sources.Add(frostBlur);
            baseComposite.Sources.Add(glassTint);

            using var shapeMasked = new AlphaMaskEffect
            {
                Source = baseComposite,
                AlphaMask = new CompositionEffectSourceParameter("ShapeClipMask")
            };

            string[] animatable =
            [
                "BaseVibrancy.Saturation",
                "BaseFrostBlur.BlurAmount",
                "GlassTintSource.Color"
            ];

            _cachedBaseEffectFactory = compositor.CreateEffectFactory(shapeMasked, animatable);
            _cachedCompositor = compositor;
            return _cachedBaseEffectFactory;
        }
    }

    /// <summary>
    /// Gets or creates the unified optical Liquid Glass <see cref="CompositionEffectFactory"/>
    /// that composites <c>BackdropBase</c> + <c>RefractedRim</c> (masked by the GPU 3D Snell <c>RimLensingMask</c>,
    /// <c>RenderMode = 2.0</c>), applies <c>GlassTintSource</c> uniformly over both, and clips to the GPU
    /// superellipse <c>ShapeClipMask</c> (<c>RenderMode = 3.0</c>).
    /// </summary>
    public static CompositionEffectFactory GetOrCreateUnifiedGlassEffectFactory(Compositor compositor)
    {
        ArgumentNullException.ThrowIfNull(compositor);

        lock (SyncLock)
        {
            EnsureGpuEngineLocked(compositor);

            if (_cachedUnifiedGlassEffectFactory is not null && ReferenceEquals(_cachedCompositor, compositor))
            {
                return _cachedUnifiedGlassEffectFactory;
            }

            using var baseVibrancy = new SaturationEffect
            {
                Name = "BaseVibrancy",
                Saturation = 1.60f,
                Source = new CompositionEffectSourceParameter("BackdropBase")
            };

            using var baseBlur = new GaussianBlurEffect
            {
                Name = "BaseFrostBlur",
                BlurAmount = 2.0f,
                Optimization = EffectOptimization.Balanced,
                BorderMode = EffectBorderMode.Hard,
                Source = baseVibrancy
            };

            using var rimVibrancy = new SaturationEffect
            {
                Name = "RimVibrancy",
                Saturation = 1.68f,
                Source = new CompositionEffectSourceParameter("RefractedRim")
            };

            using var rimBlur = new GaussianBlurEffect
            {
                Name = "RimFrostBlur",
                BlurAmount = 1.4f,
                Optimization = EffectOptimization.Balanced,
                BorderMode = EffectBorderMode.Hard,
                Source = rimVibrancy
            };

            using var rimMasked = new AlphaMaskEffect
            {
                Source = rimBlur,
                AlphaMask = new CompositionEffectSourceParameter("RimLensingMask")
            };

            using var rimOpacity = new OpacityEffect
            {
                Name = "RimRefractionOpacity",
                Opacity = 0.88f,
                Source = rimMasked
            };

            using var glassTint = new ColorSourceEffect
            {
                Name = "GlassTintSource",
                Color = Color.FromArgb(128, 0x1A, 0x1B, 0x20)
            };

            using var unifiedComposite = new CompositeEffect
            {
                Mode = CanvasComposite.SourceOver
            };
            unifiedComposite.Sources.Add(baseBlur);
            unifiedComposite.Sources.Add(rimOpacity);
            unifiedComposite.Sources.Add(glassTint);

            using var shapeMasked = new AlphaMaskEffect
            {
                Source = unifiedComposite,
                AlphaMask = new CompositionEffectSourceParameter("ShapeClipMask")
            };

            string[] animatable =
            [
                "BaseVibrancy.Saturation",
                "BaseFrostBlur.BlurAmount",
                "RimVibrancy.Saturation",
                "RimFrostBlur.BlurAmount",
                "RimRefractionOpacity.Opacity",
                "GlassTintSource.Color"
            ];

            _cachedUnifiedGlassEffectFactory = compositor.CreateEffectFactory(shapeMasked, animatable);
            _cachedCompositor = compositor;
            return _cachedUnifiedGlassEffectFactory;
        }
    }

    /// <summary>
    /// Computes 3D Snell optical bezel metrics and inward displacement in DIPs.
    /// </summary>
    public static LensOpticalMetrics ComputeLensMetrics(
        float widthDip,
        float heightDip,
        double distortionStrength,
        double specularRimIntensity)
    {
        float w = Math.Max(8.0f, widthDip);
        float h = Math.Max(8.0f, heightDip);
        float minDim = Math.Min(w, h);

        float refractionNorm = (float)Math.Clamp(distortionStrength / 60.0, 0.0, 2.0);
        float specularNorm = (float)Math.Clamp(specularRimIntensity, 0.0, 1.0);

        float bezelDip = Math.Clamp(
            minDim * (0.24f + (0.14f * Math.Min(refractionNorm, 1.5f)) + (0.04f * specularNorm)),
            6.5f,
            Math.Min(17.5f, Math.Max(7.0f, minDim * 0.42f)));

        if (refractionNorm <= 0.01f)
        {
            return new LensOpticalMetrics(
                RimRefractionOpacity: 0.0f,
                RimSaturation: 1.60f,
                BezelDip: bezelDip,
                InwardShiftDip: 0.0f);
        }

        float maxSafeShift = Math.Min(minDim * 0.38f, 14.5f);
        float inwardShiftDip = Math.Clamp(refractionNorm * Math.Min(minDim * 0.30f, 12.0f), 2.0f, maxSafeShift);

        float rimOpacity = Math.Clamp(0.78f + (refractionNorm * 0.20f), 0.0f, 0.98f);
        float rimSat = Math.Clamp(1.68f + (refractionNorm * 0.24f), 1.0f, 2.0f);

        return new LensOpticalMetrics(
            RimRefractionOpacity: rimOpacity,
            RimSaturation: rimSat,
            BezelDip: bezelDip,
            InwardShiftDip: inwardShiftDip);
    }

    public static Color ResolveTintColor(
        LiquidGlassParameters glass,
        bool isDarkTheme,
        bool isHovered,
        bool isPressed,
        double tintOpacityOverride = -1.0)
    {
        Color rgb = ResolveBaseRgb(glass, isDarkTheme, isHovered, isPressed);
        double baseOpacity = tintOpacityOverride >= 0.0
            ? Math.Clamp(tintOpacityOverride, 0.0, 1.0)
            : Math.Clamp(glass.TintOpacity, 0.0, 1.0);
        double interactiveBoost = isPressed ? 0.05 : (isHovered ? 0.025 : 0.0);
        double alphaTotal = Math.Clamp(baseOpacity + interactiveBoost, 0.0, 1.0);
        byte finalAlpha = (byte)Math.Clamp(Math.Round(alphaTotal * 255.0), 0, 255);
        return Color.FromArgb(finalAlpha, rgb.R, rgb.G, rgb.B);
    }

    internal static Color ResolveBaseRgb(
        LiquidGlassParameters glass,
        bool isDarkTheme,
        bool isHovered,
        bool isPressed)
    {
        Color baseRgb = TryParseHexColor(glass.TintColorHex, out var parsed)
            ? parsed
            : (isDarkTheme ? Color.FromArgb(255, 0x1A, 0x1B, 0x20) : Color.FromArgb(255, 0xFA, 0xFA, 0xFA));

        if (!isDarkTheme && baseRgb.R < 120 && baseRgb.G < 120 && baseRgb.B < 120)
        {
            baseRgb = Color.FromArgb(255, 0xFA, 0xFA, 0xFA);
        }

        int rgbLift = isDarkTheme ? (isPressed ? 16 : (isHovered ? 8 : 0)) : (isPressed ? -8 : (isHovered ? 3 : 0));
        byte r = (byte)Math.Clamp(baseRgb.R + rgbLift, 0, 255);
        byte g = (byte)Math.Clamp(baseRgb.G + rgbLift, 0, 255);
        byte b = (byte)Math.Clamp(baseRgb.B + rgbLift, 0, 255);

        return Color.FromArgb(255, r, g, b);
    }

    /// <summary>
    /// Builds a configured <see cref="LiquidGlassMaterial"/> for a panel instance.
    /// </summary>
    internal static LiquidGlassMaterial CreateConfiguredMaterial(
        LiquidGlassPreset preset,
        LiquidGlassSurfaceProfile profileOverride,
        float cornerRadiusDip,
        float bezelDip,
        float shapeRoundness,
        float rasterizationScale,
        LiquidGlassParameters userGlass,
        bool isDarkTheme,
        double? tintOpacityOverride,
        double highlightMultiplier)
    {
        var material = new LiquidGlassMaterial();
        material.ApplyPreset(preset);
        material.SurfaceProfile = profileOverride;
        material.CornerRadius = cornerRadiusDip;
        material.BezelWidth = bezelDip;
        material.ShapeRoundness = Math.Clamp(shapeRoundness, 2.0f, 8.0f);
        material.RasterizationScale = Math.Clamp(rasterizationScale, 1.0f, 3.0f);

        Color tintRgb = ResolveBaseRgb(userGlass, isDarkTheme, isHovered: false, isPressed: false);
        material.ApplyUserParameters(userGlass, tintRgb, tintOpacityOverride, highlightMultiplier);

        if (!isDarkTheme)
        {
            material.InnerShadowStrength *= 0.65f;
        }

        return material;
    }

    /// <summary>
    /// Returns a cached or newly GPU-rendered set of DirectComposition surfaces (<c>RimLensingMask</c>,
    /// <c>ShapeClipMask</c>, and <c>SpecularMap</c>) evaluated 100% on the GPU via <c>LiquidGlassMain.cso</c>,
    /// and pins <paramref name="owner"/> as a live owner so the surfaces can NEVER be evicted or disposed
    /// while the panel is bound to them.
    /// </summary>
    internal static PanelSurfaceSet? AcquirePanelTextures(
        LiquidGlassPanel owner,
        Compositor compositor,
        float widthDip,
        float heightDip,
        float cornerRadiusDip,
        float bezelDip,
        float rasterizationScale,
        bool isDarkTheme,
        LiquidGlassPreset preset,
        LiquidGlassSurfaceProfile surfaceProfile,
        float shapeRoundness,
        LiquidGlassParameters userGlass,
        double? tintOpacityOverride,
        double highlightMultiplier)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(compositor);

        float dpiScale = Math.Clamp(rasterizationScale, 1.0f, 3.0f);
        float safeW = Math.Max(8.0f, widthDip);
        float safeH = Math.Max(8.0f, heightDip);
        float maxRadius = Math.Min(safeW, safeH) * 0.5f;
        float clampedRadius = Math.Clamp(cornerRadiusDip, 0.0f, maxRadius);
        float clampedBezel = Math.Clamp(bezelDip, 3.0f, Math.Max(4.0f, maxRadius));

        float maxSidePx = Math.Max(safeW, safeH) * dpiScale;
        if (maxSidePx > 512.0f)
        {
            dpiScale *= 512.0f / maxSidePx;
        }

        int rawPxW = (int)Math.Clamp(MathF.Round(safeW * dpiScale), 16, 512);
        int rawPxH = (int)Math.Clamp(MathF.Round(safeH * dpiScale), 16, 512);
        int stepW = rawPxW < 256 ? 8 : 16;
        int stepH = rawPxH < 256 ? 8 : 16;
        int qPxW = Math.Max(16, ((rawPxW + (stepW - 1)) / stepW) * stepW);
        int qPxH = Math.Max(16, ((rawPxH + (stepH - 1)) / stepH) * stepH);
        int qRadius = Math.Max(0, (int)MathF.Round(clampedRadius));
        int qBezel = Math.Max(3, (int)MathF.Round(clampedBezel));
        int qRoundness = (int)MathF.Round(Math.Clamp(shapeRoundness, 2.0f, 8.0f) * 10.0f);
        int qHighlight = (int)MathF.Round((float)(userGlass.SpecularRimIntensity * highlightMultiplier * 100.0));
        int qDispersion = (int)MathF.Round((float)(userGlass.ChromaticAberration * 20.0));
        int qRefraction = (int)MathF.Round((float)(Math.Clamp(userGlass.DistortionStrength, 0.0, 200.0) * 2.0));
        int qBlur = (int)MathF.Round((float)(Math.Clamp(userGlass.FrostBlurRadius, 0.0, 64.0) * 4.0));
        int qInnerShadow = (int)MathF.Round((float)(Math.Clamp(userGlass.InnerShadowBlur, 0.0, 48.0) * 4.0));
        Color resolvedTintRgb = ResolveBaseRgb(userGlass, isDarkTheme, isHovered: false, isPressed: false);
        int tintRgb24 = (resolvedTintRgb.R << 16) | (resolvedTintRgb.G << 8) | resolvedTintRgb.B;
        double effectiveTintAlpha = tintOpacityOverride.HasValue
            ? Math.Clamp(tintOpacityOverride.Value, 0.0, 0.95)
            : Math.Clamp(userGlass.TintOpacity, 0.0, 0.95);
        int qTintAlpha = (int)MathF.Round((float)(effectiveTintAlpha * 100.0));
        int qSaturation = (int)MathF.Round((float)(Math.Clamp(userGlass.SaturationBoost, 0.5, 2.2) * 50.0));

        var key = new MaskCacheKey(
            qPxW,
            qPxH,
            qRadius,
            qBezel,
            (int)preset,
            (int)surfaceProfile,
            qRoundness,
            qHighlight,
            qDispersion,
            qRefraction,
            qBlur,
            qInnerShadow,
            qTintAlpha,
            tintRgb24,
            qSaturation,
            isDarkTheme);

        PanelSurfaceSet? result = null;
        bool broadcastDeviceReset = false;

        lock (SyncLock)
        {
            PruneDetachedOwnedSetsLocked();

            if (!EnsureGpuEngineLocked(compositor))
            {
                return null;
            }

            if (SurfaceCache.TryGetValue(key, out var existing) &&
                !existing.Surfaces.IsDisposed &&
                existing.Surfaces.ResourceGeneration == _resourceGeneration &&
                existing.Surfaces.PaletteGeneration == _paletteGeneration)
            {
                CacheLru.Remove(existing.LruNode);
                CacheLru.AddFirst(existing.LruNode);
                existing.Surfaces.AttachOwnerLocked(owner);
                return existing.Surfaces;
            }

            float effectiveScale = qPxW / safeW;
            var material = CreateConfiguredMaterial(
                preset,
                surfaceProfile,
                clampedRadius,
                clampedBezel,
                shapeRoundness,
                effectiveScale,
                userGlass,
                isDarkTheme,
                tintOpacityOverride,
                highlightMultiplier);

            var created = BuildGpuPanelSurfaceSetLocked(qPxW, qPxH, clampedBezel, material, out bool deviceLost);
            if (created is null && deviceLost)
            {
                _resourceGeneration++;
                GpuEngine.ResetDeviceResources(disposeGraphicsDevice: false);
                InvalidateSurfaceCacheLocked();
                _cachedBaseEffectFactory = null;
                _cachedUnifiedGlassEffectFactory = null;
                broadcastDeviceReset = true;

                if (EnsureGpuEngineLocked(compositor))
                {
                    created = BuildGpuPanelSurfaceSetLocked(qPxW, qPxH, clampedBezel, material, out _);
                }
            }

            if (created is null)
            {
                result = null;
            }
            else
            {
                created.AttachOwnerLocked(owner);
                if (SurfaceCache.TryGetValue(key, out var staleExisting))
                {
                    CacheLru.Remove(staleExisting.LruNode);
                    SurfaceCache.Remove(key);
                    staleExisting.Surfaces.InCache = false;
                    if (!staleExisting.Surfaces.HasLiveOwnersLocked())
                    {
                        DisposeSurfaceSetLocked(staleExisting.Surfaces);
                    }
                    else
                    {
                        DetachedOwnedSets.Add(staleExisting.Surfaces);
                    }
                }

                var lruNode = CacheLru.AddFirst(key);
                SurfaceCache[key] = new CachedSurfaceEntry
                {
                    Surfaces = created,
                    LruNode = lruNode
                };

                TrimIdleCacheLocked();
                result = created;
            }
        }

        if (broadcastDeviceReset)
        {
            GpuResourcesInvalidated?.Invoke(null, EventArgs.Empty);
        }

        return result;
    }

    /// <summary>
    /// Releases <paramref name="owner"/>'s hold on <paramref name="surfaceSet"/> AFTER the panel's
    /// Composition brushes have been detached or swapped to a replacement surface set.
    /// If the set is detached from the active cache and has no remaining live owners, it is disposed immediately.
    /// </summary>
    internal static void ReleasePanelTextures(LiquidGlassPanel owner, PanelSurfaceSet? surfaceSet)
    {
        if (owner is null || surfaceSet is null)
        {
            return;
        }

        lock (SyncLock)
        {
            surfaceSet.DetachOwnerLocked(owner);
            if (!surfaceSet.InCache && !surfaceSet.HasLiveOwnersLocked())
            {
                DetachedOwnedSets.Remove(surfaceSet);
                DisposeSurfaceSetLocked(surfaceSet);
            }
            else if (surfaceSet.InCache && !surfaceSet.HasLiveOwnersLocked())
            {
                TrimIdleCacheLocked();
            }

            PruneDetachedOwnedSetsLocked();
        }
    }

    /// <summary>
    /// Renders or updates an interactive per-panel GPU optical overlay surface (<c>RenderMode = 1.0</c>)
    /// with real-time cursor position (<c>PointerUV</c>), movement vector (<c>PointerDelta</c>),
    /// and <c>PointerLightInfluence</c> executed directly on the GPU via <c>LiquidGlassMain.cso</c>.
    /// Uses swap-then-dispose so the caller's live brush never points to a disposed surface.
    /// </summary>
    internal static bool RenderInteractivePanelOverlay(
        Compositor compositor,
        ref CompositionDrawingSurface? interactiveSurface,
        ref int currentSurfaceW,
        ref int currentSurfaceH,
        int targetPixelW,
        int targetPixelH,
        LiquidGlassMaterial material,
        Vector4 uvRect,
        out CompositionDrawingSurface? replacedSurfaceToDispose)
    {
        replacedSurfaceToDispose = null;
        bool deviceLost = false;
        bool rendered = false;

        lock (SyncLock)
        {
            if (!EnsureGpuEngineLocked(compositor))
            {
                return false;
            }

            int pxW = Math.Clamp(targetPixelW, 16, 512);
            int pxH = Math.Clamp(targetPixelH, 16, 512);

            if (interactiveSurface is null || currentSurfaceW != pxW || currentSurfaceH != pxH)
            {
                var nextSurface = GpuEngine.CreateSurface(pxW, pxH);
                if (nextSurface is null)
                {
                    return false;
                }

                if (!GpuEngine.RenderLiquidGlassSurface(
                    nextSurface,
                    pxW,
                    pxH,
                    material,
                    uvRect,
                    renderMode: 1.0f,
                    out deviceLost))
                {
                    try { nextSurface.Dispose(); } catch { }
                    rendered = false;
                }
                else
                {
                    replacedSurfaceToDispose = interactiveSurface;
                    interactiveSurface = nextSurface;
                    currentSurfaceW = pxW;
                    currentSurfaceH = pxH;
                    rendered = true;
                }
            }
            else
            {
                rendered = GpuEngine.RenderLiquidGlassSurface(
                    interactiveSurface,
                    pxW,
                    pxH,
                    material,
                    uvRect,
                    renderMode: 1.0f,
                    out deviceLost);
            }
        }

        if (deviceLost)
        {
            HandleGpuDeviceReset(disposeGraphicsDevice: false);
        }

        return rendered;
    }

    private static PanelSurfaceSet? BuildGpuPanelSurfaceSetLocked(
        int pixelW,
        int pixelH,
        float bezelDip,
        LiquidGlassMaterial material,
        out bool encounteredDeviceLost)
    {
        encounteredDeviceLost = false;

        var rimSurface = GpuEngine.CreateSurface(pixelW, pixelH);
        var clipSurface = GpuEngine.CreateSurface(pixelW, pixelH);
        var specularSurface = GpuEngine.CreateSurface(pixelW, pixelH);

        if (rimSurface is null || clipSurface is null || specularSurface is null)
        {
            try { rimSurface?.Dispose(); } catch { }
            try { clipSurface?.Dispose(); } catch { }
            try { specularSurface?.Dispose(); } catch { }
            return null;
        }

        var fullUvRect = new Vector4(0.0f, 0.0f, 1.0f, 1.0f);

        bool lostClip = false;
        bool lostSpec = false;

        // 1. RenderMode = 2.0f -> 3D Snell's Law Meniscus Lensing Profile Mask (shader_28.asm)
        bool okRim = GpuEngine.RenderLiquidGlassSurface(
            rimSurface,
            pixelW,
            pixelH,
            material,
            fullUvRect,
            renderMode: 2.0f,
            out bool lostRim);

        // 2. RenderMode = 3.0f -> Continuous G2 Superellipse SDF Alpha Clip Mask (ShapeRoundness + EdgeSoftness)
        bool okClip = okRim && GpuEngine.RenderLiquidGlassSurface(
            clipSurface,
            pixelW,
            pixelH,
            material,
            fullUvRect,
            renderMode: 3.0f,
            out lostClip);

        // 3. RenderMode = 1.0f -> Exact Closed-Form Premultiplied Optical Overlay
        //    (InnerShadow + BorderThickness + Specular with SpecularSaturation + 5th-order RefFresnel +
        //     5th-order Anisotropic Glare + 7-tap Spectral Chromatic Dispersion from Pass 1 t0)
        bool okSpec = okClip && GpuEngine.RenderLiquidGlassSurface(
            specularSurface,
            pixelW,
            pixelH,
            material,
            fullUvRect,
            renderMode: 1.0f,
            out lostSpec);

        encounteredDeviceLost = lostRim || lostClip || lostSpec;
        if (!okRim || !okClip || !okSpec)
        {
            try { rimSurface.Dispose(); } catch { }
            try { clipSurface.Dispose(); } catch { }
            try { specularSurface.Dispose(); } catch { }
            return null;
        }

        return new PanelSurfaceSet
        {
            RimLensingMask = rimSurface,
            ShapeClipMask = clipSurface,
            SpecularMap = specularSurface,
            BezelDip = bezelDip,
            PixelWidth = pixelW,
            PixelHeight = pixelH,
            ResourceGeneration = _resourceGeneration,
            PaletteGeneration = _paletteGeneration,
            InCache = true
        };
    }

    private static bool EnsureGpuEngineLocked(Compositor compositor)
    {
        CanvasDevice sharedDevice;
        try
        {
            sharedDevice = CanvasDevice.GetSharedDevice();
        }
        catch
        {
            return false;
        }

        if (!ReferenceEquals(_trackedDevice, sharedDevice))
        {
            bool hadTrackedDevice = _trackedDevice is not null;
            if (_trackedDevice is not null)
            {
                _trackedDevice.DeviceLost -= OnCanvasDeviceLost;
            }

            _trackedDevice = sharedDevice;
            _trackedDevice.DeviceLost += OnCanvasDeviceLost;

            if (hadTrackedDevice)
            {
                _resourceGeneration++;
                GpuEngine.ResetDeviceResources(disposeGraphicsDevice: false);
                InvalidateSurfaceCacheLocked();
                _cachedBaseEffectFactory = null;
                _cachedUnifiedGlassEffectFactory = null;
            }
        }

        if (!ReferenceEquals(_cachedCompositor, compositor))
        {
            bool hadCompositor = _cachedCompositor is not null;
            _cachedCompositor = compositor;
            _cachedBaseEffectFactory = null;
            _cachedUnifiedGlassEffectFactory = null;

            if (hadCompositor)
            {
                _resourceGeneration++;
                GpuEngine.ResetDeviceResources(disposeGraphicsDevice: true);
                InvalidateSurfaceCacheLocked();
            }
        }

        return GpuEngine.EnsureInitialized(compositor);
    }

    private static void OnCanvasDeviceLost(CanvasDevice sender, object args)
    {
        HandleGpuDeviceReset(disposeGraphicsDevice: false);
    }

    private static void OnGpuRenderingDeviceReplaced(object? sender, EventArgs args)
    {
        HandleGpuDeviceReset(disposeGraphicsDevice: false);
    }

    private static void HandleGpuDeviceReset(bool disposeGraphicsDevice)
    {
        lock (SyncLock)
        {
            _resourceGeneration++;
            GpuEngine.ResetDeviceResources(disposeGraphicsDevice);
            InvalidateSurfaceCacheLocked();
            _cachedBaseEffectFactory = null;
            _cachedUnifiedGlassEffectFactory = null;
        }

        GpuResourcesInvalidated?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Detaches all cached surface sets without disposing any set still referenced by a live
    /// <see cref="LiquidGlassPanel"/>. Owned sets remain valid in <see cref="DetachedOwnedSets"/>
    /// until their owning panels swap to fresh surfaces and call <see cref="ReleasePanelTextures"/>.
    /// </summary>
    private static void InvalidateSurfaceCacheLocked()
    {
        foreach (var entry in SurfaceCache.Values)
        {
            entry.Surfaces.InCache = false;
            if (!entry.Surfaces.HasLiveOwnersLocked())
            {
                DisposeSurfaceSetLocked(entry.Surfaces);
            }
            else
            {
                DetachedOwnedSets.Add(entry.Surfaces);
            }
        }

        SurfaceCache.Clear();
        CacheLru.Clear();
        PruneDetachedOwnedSetsLocked();
    }

    /// <summary>
    /// Enforces <see cref="MaxIdleCachedSurfaceSets"/> strictly over unowned (idle) cache entries,
    /// guaranteeing that active surfaces bound to live panels are NEVER evicted or disposed.
    /// </summary>
    private static void TrimIdleCacheLocked()
    {
        int idleCount = 0;
        for (var node = CacheLru.First; node is not null; node = node.Next)
        {
            if (SurfaceCache.TryGetValue(node.Value, out var entry) && !entry.Surfaces.HasLiveOwnersLocked())
            {
                idleCount++;
            }
        }

        if (idleCount <= MaxIdleCachedSurfaceSets)
        {
            return;
        }

        var cursor = CacheLru.Last;
        while (cursor is not null && idleCount > MaxIdleCachedSurfaceSets)
        {
            var prev = cursor.Previous;
            var key = cursor.Value;
            if (SurfaceCache.TryGetValue(key, out var entry) && !entry.Surfaces.HasLiveOwnersLocked())
            {
                CacheLru.Remove(cursor);
                SurfaceCache.Remove(key);
                entry.Surfaces.InCache = false;
                DisposeSurfaceSetLocked(entry.Surfaces);
                idleCount--;
            }

            cursor = prev;
        }
    }

    private static void PruneDetachedOwnedSetsLocked()
    {
        for (int i = DetachedOwnedSets.Count - 1; i >= 0; i--)
        {
            var set = DetachedOwnedSets[i];
            if (set.IsDisposed || !set.HasLiveOwnersLocked())
            {
                DetachedOwnedSets.RemoveAt(i);
                DisposeSurfaceSetLocked(set);
            }
        }
    }

    private static void DisposeSurfaceSetLocked(PanelSurfaceSet set)
    {
        if (set.IsDisposed)
        {
            return;
        }

        set.IsDisposed = true;
        try
        {
            set.RimLensingMask.Dispose();
            set.ShapeClipMask.Dispose();
            set.SpecularMap.Dispose();
        }
        catch
        {
            // Ignore disposal races on device reset
        }
    }

    private static bool TryParseHexColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        string trimmed = hex.Trim();
        if (trimmed.StartsWith('#'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.Length == 6 &&
            uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            color = Color.FromArgb(
                255,
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF));
            return true;
        }

        if (trimmed.Length == 8 &&
            uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
        {
            color = Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));
            return true;
        }

        return false;
    }
}
