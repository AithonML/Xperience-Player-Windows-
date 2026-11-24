using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Xperience.Core.Abstractions;

namespace Xperience.App.Helpers;

/// <summary>
/// Provides GPU-accelerated Windows Composition spring micro-interactions (hover elevation,
/// tactile press compression, and magnetic cursor tilt) for XAML controls.
/// </summary>
public static class MicroInteractions
{
    /// <summary>
    /// Identifies the <c>MicroInteractions.HoverScale</c> attached dependency property.
    /// Setting this to a value &gt; 0 (e.g., 1.04) enables Composition spring hover and press animations.
    /// </summary>
    public static readonly DependencyProperty HoverScaleProperty =
        DependencyProperty.RegisterAttached(
            "HoverScale",
            typeof(double),
            typeof(MicroInteractions),
            new PropertyMetadata(1.0, OnHoverScaleChanged));

    /// <summary>
    /// Identifies the <c>MicroInteractions.PressScale</c> attached dependency property.
    /// </summary>
    public static readonly DependencyProperty PressScaleProperty =
        DependencyProperty.RegisterAttached(
            "PressScale",
            typeof(double),
            typeof(MicroInteractions),
            new PropertyMetadata(0.95));

    /// <summary>
    /// Identifies the <c>MicroInteractions.HoverElevationY</c> attached dependency property.
    /// </summary>
    public static readonly DependencyProperty HoverElevationYProperty =
        DependencyProperty.RegisterAttached(
            "HoverElevationY",
            typeof(double),
            typeof(MicroInteractions),
            new PropertyMetadata(0.0));

    /// <summary>
    /// Gets the target scale factor when the pointer hovers over the element.
    /// </summary>
    public static double GetHoverScale(DependencyObject obj) => (double)obj.GetValue(HoverScaleProperty);

    /// <summary>
    /// Sets the target scale factor when the pointer hovers over the element.
    /// </summary>
    public static void SetHoverScale(DependencyObject obj, double value) => obj.SetValue(HoverScaleProperty, value);

    /// <summary>
    /// Gets the target scale factor when the element is pressed.
    /// </summary>
    public static double GetPressScale(DependencyObject obj) => (double)obj.GetValue(PressScaleProperty);

    /// <summary>
    /// Sets the target scale factor when the element is pressed.
    /// </summary>
    public static void SetPressScale(DependencyObject obj, double value) => obj.SetValue(PressScaleProperty, value);

    /// <summary>
    /// Gets the vertical Y translation offset applied on hover.
    /// </summary>
    public static double GetHoverElevationY(DependencyObject obj) => (double)obj.GetValue(HoverElevationYProperty);

    /// <summary>
    /// Sets the vertical Y translation offset applied on hover.
    /// </summary>
    public static void SetHoverElevationY(DependencyObject obj, double value) => obj.SetValue(HoverElevationYProperty, value);

    /// <summary>
    /// Identifies the <c>MicroInteractions.LyricGlowOpacity</c> attached dependency property.
    /// Applies a true 0-offset GPU Gaussian glow (<see cref="DropShadow"/> with <see cref="TextBlock.GetAlphaMask"/>)
    /// behind the active lyric line without any duplicate text shadow offset.
    /// </summary>
    public static readonly DependencyProperty LyricGlowOpacityProperty =
        DependencyProperty.RegisterAttached(
            "LyricGlowOpacity",
            typeof(double),
            typeof(MicroInteractions),
            new PropertyMetadata(0.0, OnLyricOpticalPropertyChanged));

    /// <summary>
    /// Identifies the <c>MicroInteractions.LyricBlurStrength</c> attached dependency property.
    /// Applies a true 0-offset GPU Gaussian defocus blur (<see cref="DropShadow"/> with <see cref="TextBlock.GetAlphaMask"/>)
    /// to distant inactive Fullscreen lyric lines.
    /// </summary>
    public static readonly DependencyProperty LyricBlurStrengthProperty =
        DependencyProperty.RegisterAttached(
            "LyricBlurStrength",
            typeof(double),
            typeof(MicroInteractions),
            new PropertyMetadata(0.0, OnLyricOpticalPropertyChanged));

    public static double GetLyricGlowOpacity(DependencyObject obj) => (double)obj.GetValue(LyricGlowOpacityProperty);
    public static void SetLyricGlowOpacity(DependencyObject obj, double value) => obj.SetValue(LyricGlowOpacityProperty, value);

    public static double GetLyricBlurStrength(DependencyObject obj) => (double)obj.GetValue(LyricBlurStrengthProperty);
    public static void SetLyricBlurStrength(DependencyObject obj, double value) => obj.SetValue(LyricBlurStrengthProperty, value);

    private const int CompositionStateUnprepared = 0;
    private const int CompositionStateLegacyVisual = 1;
    private const int CompositionStateFacadeOnly = 2;

    private static readonly DependencyProperty CompositionPrepStateProperty =
        DependencyProperty.RegisterAttached(
            "CompositionPrepState",
            typeof(int),
            typeof(MicroInteractions),
            new PropertyMetadata(CompositionStateUnprepared));

    private static void OnLyricOpticalPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Neutralized: WinUI 3 Composition DropShadow.Mask(GetAlphaMask) with scale transitions causes
        // sub-pixel stereoscopic offset text ghosting. Avoid attaching unused SizeChanged handlers.
    }

    private static void OnHoverScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        // Defer ElementCompositionPreview.SetIsTranslationEnabled and GetElementVisual until
        // actual pointer interaction so non-hovered rows/cards never eagerly allocate Composition visuals.
        element.PointerEntered -= OnElementPointerEntered;
        element.PointerEntered += OnElementPointerEntered;

        element.PointerExited -= OnElementPointerExited;
        element.PointerExited += OnElementPointerExited;

        element.PointerPressed -= OnElementPointerPressed;
        element.PointerPressed += OnElementPointerPressed;

        element.PointerReleased -= OnElementPointerReleased;
        element.PointerReleased += OnElementPointerReleased;
    }

    private static int EnsureCompositionPrepared(FrameworkElement element)
    {
        int state = (int)element.GetValue(CompositionPrepStateProperty);
        if (state != CompositionStateUnprepared)
        {
            return state;
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            state = CompositionStateLegacyVisual;
        }
        catch
        {
            // Elements using WinUI 3 UIElement facade properties (e.g. CenterPoint, OpacityTransition,
            // ScaleTransition) forbid SetIsTranslationEnabled with UnauthorizedAccessException.
            // Fall back to direct UIElement facade animations without calling SetIsTranslationEnabled.
            state = CompositionStateFacadeOnly;
        }

        element.SetValue(CompositionPrepStateProperty, state);
        return state;
    }

    private static void OnElementPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            int state = EnsureCompositionPrepared(element);
            float hoverScale = (float)GetHoverScale(element);
            float elevationY = (float)GetHoverElevationY(element);
            AnimateSpringScaleAndTranslation(element, state, hoverScale, new Vector3(0f, elevationY, 0f), dampingRatio: 0.58f, periodMs: 34);
        }
    }

    private static void OnElementPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            int state = (int)element.GetValue(CompositionPrepStateProperty);
            if (state != CompositionStateUnprepared)
            {
                AnimateSpringScaleAndTranslation(element, state, 1.0f, Vector3.Zero, dampingRatio: 0.65f, periodMs: 38);
            }
        }
    }

    private static void OnElementPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            int state = EnsureCompositionPrepared(element);
            float pressScale = (float)GetPressScale(element);
            AnimateSpringScaleAndTranslation(element, state, pressScale, new Vector3(0f, 1.0f, 0f), dampingRatio: 0.72f, periodMs: 22);
        }
    }

    private static void OnElementPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            int state = (int)element.GetValue(CompositionPrepStateProperty);
            if (state != CompositionStateUnprepared)
            {
                float hoverScale = (float)GetHoverScale(element);
                float elevationY = (float)GetHoverElevationY(element);
                AnimateSpringScaleAndTranslation(element, state, hoverScale, new Vector3(0f, elevationY, 0f), dampingRatio: 0.52f, periodMs: 36);
            }
        }
    }

    private static void AnimateSpringScaleAndTranslation(
        FrameworkElement element,
        int prepState,
        float targetScale,
        Vector3 targetTranslation,
        float dampingRatio,
        int periodMs)
    {
        try
        {
            var centerPoint = new Vector3((float)element.ActualWidth * 0.5f, (float)element.ActualHeight * 0.5f, 0f);

            if (prepState == CompositionStateFacadeOnly)
            {
                var compositor = CompositionTarget.GetCompositorForCurrentThread();
                if (compositor is null)
                {
                    return;
                }

                element.CenterPoint = centerPoint;

                using var scaleSpring = compositor.CreateSpringVector3Animation();
                scaleSpring.Target = "Scale";
                scaleSpring.FinalValue = new Vector3(targetScale, targetScale, 1.0f);
                scaleSpring.DampingRatio = dampingRatio;
                scaleSpring.Period = TimeSpan.FromMilliseconds(periodMs);

                using var translationSpring = compositor.CreateSpringVector3Animation();
                translationSpring.Target = "Translation";
                translationSpring.FinalValue = targetTranslation;
                translationSpring.DampingRatio = dampingRatio;
                translationSpring.Period = TimeSpan.FromMilliseconds(periodMs);

                element.StartAnimation(scaleSpring);
                element.StartAnimation(translationSpring);
                return;
            }

            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.CenterPoint = centerPoint;
            var visualCompositor = visual.Compositor;

            using var legacyScaleSpring = visualCompositor.CreateSpringVector3Animation();
            legacyScaleSpring.Target = "Scale";
            legacyScaleSpring.FinalValue = new Vector3(targetScale, targetScale, 1.0f);
            legacyScaleSpring.DampingRatio = dampingRatio;
            legacyScaleSpring.Period = TimeSpan.FromMilliseconds(periodMs);

            using var legacyTranslationSpring = visualCompositor.CreateSpringVector3Animation();
            legacyTranslationSpring.Target = "Translation";
            legacyTranslationSpring.FinalValue = targetTranslation;
            legacyTranslationSpring.DampingRatio = dampingRatio;
            legacyTranslationSpring.Period = TimeSpan.FromMilliseconds(periodMs);

            visual.StartAnimation("Scale", legacyScaleSpring);
            visual.StartAnimation("Translation", legacyTranslationSpring);
        }
        catch
        {
            // Ignore if visual tree is detaching or element composition mode rejects animation
        }
    }
}
