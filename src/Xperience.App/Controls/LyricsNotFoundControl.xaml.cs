using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Xperience.App.Controls;

/// <summary>
/// Expressive animated empty/not-found lyrics state featuring a custom vector Sad Musical Note SVG
/// with gentle floating/swaying motion, drooping flag secondary animation, periodic eye blink,
/// and animated falling teardrop. Automatically starts/stops its WinUI 3 Storyboards based on
/// visibility and loaded state so it consumes zero GPU cycles when hidden.
/// </summary>
public sealed partial class LyricsNotFoundControl : UserControl
{
    private long _visibilityCallbackToken;
    private bool _isStoryboardRunning;
    private bool _hasAppliedPalette;
    private bool _lastAppliedIsLight;
    private Color _lastAppliedAccent;

    /// <summary>
    /// Identifies the <see cref="Title"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(LyricsNotFoundControl),
            new PropertyMetadata("No lyrics found", OnCopyPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="Subtitle"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(
            nameof(Subtitle),
            typeof(string),
            typeof(LyricsNotFoundControl),
            new PropertyMetadata("We couldn't find synced lyrics for this track.", OnCopyPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="HelperText"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty HelperTextProperty =
        DependencyProperty.Register(
            nameof(HelperText),
            typeof(string),
            typeof(LyricsNotFoundControl),
            new PropertyMetadata("You can import a local .LRC file above anytime.", OnCopyPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="IsLightMode"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsLightModeProperty =
        DependencyProperty.Register(
            nameof(IsLightMode),
            typeof(bool),
            typeof(LyricsNotFoundControl),
            new PropertyMetadata(false, OnThemeOrVisualPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="IsFullscreen"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsFullscreenProperty =
        DependencyProperty.Register(
            nameof(IsFullscreen),
            typeof(bool),
            typeof(LyricsNotFoundControl),
            new PropertyMetadata(false, OnLayoutPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="AccentColor"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(
            nameof(AccentColor),
            typeof(Color),
            typeof(LyricsNotFoundControl),
            new PropertyMetadata(Color.FromArgb(255, 45, 212, 191), OnThemeOrVisualPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="IsActive"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(LyricsNotFoundControl),
            new PropertyMetadata(true, OnAnimationStateChanged));

    /// <summary>
    /// Initializes a new instance of <see cref="LyricsNotFoundControl"/>.
    /// </summary>
    public LyricsNotFoundControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;

        ApplyCopy();
        ApplyLayoutScale();
        ApplyThemePalette();
    }

    /// <summary>
    /// Gets or sets the primary heading text (100% English).
    /// </summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Gets or sets the descriptive subtitle text (100% English).
    /// </summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>
    /// Gets or sets the subtle helper hint below the subtitle (100% English).
    /// </summary>
    public string HelperText
    {
        get => (string)GetValue(HelperTextProperty);
        set => SetValue(HelperTextProperty, value);
    }

    /// <summary>
    /// Gets or sets whether light-mode adaptive colors should be used.
    /// </summary>
    public bool IsLightMode
    {
        get => (bool)GetValue(IsLightModeProperty);
        set => SetValue(IsLightModeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether spacious Fullscreen icon &amp; typography sizing should be applied.
    /// </summary>
    public bool IsFullscreen
    {
        get => (bool)GetValue(IsFullscreenProperty);
        set => SetValue(IsFullscreenProperty, value);
    }

    /// <summary>
    /// Gets or sets the dynamic album cover accent color used to harmonize the ambient halo and note gradient.
    /// </summary>
    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    /// <summary>
    /// Gets or sets whether animations are allowed to run while visible.
    /// </summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static void OnCopyPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsNotFoundControl control)
        {
            control.ApplyCopy();
        }
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsNotFoundControl control)
        {
            control.ApplyLayoutScale();
        }
    }

    private static void OnThemeOrVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsNotFoundControl control)
        {
            control.ApplyThemePalette();
        }
    }

    private static void OnAnimationStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsNotFoundControl control)
        {
            control.UpdateStoryboardState();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_visibilityCallbackToken == 0)
        {
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityPropertyChanged);
        }

        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        SizeChanged -= OnControlSizeChanged;
        SizeChanged += OnControlSizeChanged;

        ApplyCopy();
        ApplyLayoutScale();
        ApplyThemePalette();
        UpdateStoryboardState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        SizeChanged -= OnControlSizeChanged;

        if (_visibilityCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }

        StopAnimations();
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        UpdateStoryboardState();
    }

    private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateStoryboardState();
    }

    private void OnVisibilityPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        UpdateStoryboardState();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyThemePalette();
    }

    private void ApplyCopy()
    {
        if (TitleTextBlock is not null)
        {
            TitleTextBlock.Text = string.IsNullOrWhiteSpace(Title) ? "No lyrics found" : Title;
        }

        if (SubtitleTextBlock is not null)
        {
            SubtitleTextBlock.Text = string.IsNullOrWhiteSpace(Subtitle)
                ? "We couldn't find synced lyrics for this track."
                : Subtitle;
        }

        if (HelperTextBlock is not null)
        {
            bool hasHelper = !string.IsNullOrWhiteSpace(HelperText);
            HelperTextBlock.Text = HelperText ?? string.Empty;
            HelperTextBlock.Visibility = hasHelper ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ApplyLayoutScale()
    {
        bool full = IsFullscreen;
        if (IconViewbox is not null)
        {
            double size = full ? 128.0 : 92.0;
            IconViewbox.Width = size;
            IconViewbox.Height = size;
        }

        if (ContentStack is not null)
        {
            ContentStack.Spacing = full ? 14.0 : 10.0;
        }

        if (TitleTextBlock is not null)
        {
            TitleTextBlock.FontSize = full ? 23.0 : 16.5;
        }

        if (SubtitleTextBlock is not null)
        {
            SubtitleTextBlock.FontSize = full ? 14.5 : 12.5;
            SubtitleTextBlock.MaxWidth = full ? 350.0 : 250.0;
        }

        if (HelperTextBlock is not null)
        {
            HelperTextBlock.FontSize = full ? 12.5 : 11.0;
            HelperTextBlock.MaxWidth = full ? 330.0 : 240.0;
        }
    }

    private void ApplyThemePalette()
    {
        if (NoteBodyPath is null || NoteFlagPath is null)
        {
            return;
        }

        bool isLight = IsLightMode;
        Color accent = AccentColor;
        if (_hasAppliedPalette && _lastAppliedIsLight == isLight && _lastAppliedAccent == accent)
        {
            return;
        }

        _hasAppliedPalette = true;
        _lastAppliedIsLight = isLight;
        _lastAppliedAccent = accent;

        // Adaptive high-contrast note gradient harmonized subtly with the active album accent
        Color noteTop = isLight
            ? BlendColor(Color.FromArgb(255, 71, 85, 105), accent, 0.24f)
            : BlendColor(Color.FromArgb(245, 241, 245, 249), accent, 0.16f);

        Color noteBottom = isLight
            ? BlendColor(Color.FromArgb(255, 30, 41, 59), accent, 0.20f)
            : BlendColor(Color.FromArgb(232, 203, 213, 225), accent, 0.22f);

        var noteGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0.15, 0.05),
            EndPoint = new Point(0.85, 0.95)
        };
        noteGradient.GradientStops.Add(new GradientStop { Offset = 0.0, Color = noteTop });
        noteGradient.GradientStops.Add(new GradientStop { Offset = 1.0, Color = noteBottom });

        var noteStroke = new SolidColorBrush(isLight
            ? Color.FromArgb(88, 15, 23, 42)
            : Color.FromArgb(128, 255, 255, 255));

        NoteBodyPath.Fill = noteGradient;
        NoteBodyPath.Stroke = noteStroke;
        NoteFlagPath.Fill = noteGradient;
        NoteFlagPath.Stroke = noteStroke;

        if (NoteSpecularPath is not null)
        {
            NoteSpecularPath.Stroke = new SolidColorBrush(isLight
                ? Color.FromArgb(72, 255, 255, 255)
                : Color.FromArgb(128, 255, 255, 255));
        }

        // High-contrast expressive facial features
        var faceBrush = new SolidColorBrush(isLight
            ? Color.FromArgb(245, 248, 250, 252)
            : Color.FromArgb(245, 22, 28, 42));

        if (LeftBrowPath is not null) LeftBrowPath.Stroke = faceBrush;
        if (RightBrowPath is not null) RightBrowPath.Stroke = faceBrush;
        if (FrownPath is not null) FrownPath.Stroke = faceBrush;
        if (LeftEyeEllipse is not null) LeftEyeEllipse.Fill = faceBrush;
        if (RightEyeEllipse is not null) RightEyeEllipse.Fill = faceBrush;

        var glintBrush = new SolidColorBrush(isLight
            ? Color.FromArgb(230, 56, 189, 248)
            : Color.FromArgb(242, 255, 255, 255));
        if (LeftEyeGlint is not null) LeftEyeGlint.Fill = glintBrush;
        if (RightEyeGlint is not null) RightEyeGlint.Fill = glintBrush;

        if (MiniNotePath is not null)
        {
            MiniNotePath.Fill = new SolidColorBrush(isLight
                ? Color.FromArgb(140, 51, 65, 85)
                : Color.FromArgb(156, 255, 255, 255));
        }

        if (AmbientHaloInnerStop is not null && AmbientHaloOuterStop is not null)
        {
            AmbientHaloInnerStop.Color = Color.FromArgb(isLight ? (byte)64 : (byte)96, accent.R, accent.G, accent.B);
            AmbientHaloOuterStop.Color = Color.FromArgb(0, accent.R, accent.G, accent.B);
        }

        if (TitleTextBlock is not null)
        {
            TitleTextBlock.Foreground = new SolidColorBrush(isLight
                ? Color.FromArgb(255, 15, 23, 42)
                : Color.FromArgb(248, 255, 255, 255));
        }

        if (SubtitleTextBlock is not null)
        {
            SubtitleTextBlock.Foreground = new SolidColorBrush(isLight
                ? Color.FromArgb(192, 30, 41, 59)
                : Color.FromArgb(196, 241, 245, 249));
        }

        if (HelperTextBlock is not null)
        {
            HelperTextBlock.Foreground = new SolidColorBrush(isLight
                ? Color.FromArgb(142, 51, 65, 85)
                : Color.FromArgb(136, 226, 232, 240));
        }
    }

    private bool IsAncestorChainVisible()
    {
        DependencyObject? current = this;
        int depth = 0;
        while (current is not null && depth < 16)
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
            depth++;
        }

        return true;
    }

    private void UpdateStoryboardState()
    {
        bool shouldRun = IsLoaded && Visibility == Visibility.Visible && IsActive && IsAncestorChainVisible();
        if (shouldRun)
        {
            StartAnimations();
        }
        else
        {
            StopAnimations();
        }
    }

    private void StartAnimations()
    {
        if (_isStoryboardRunning)
        {
            return;
        }

        try
        {
            SadNoteFloatStoryboard?.Begin();
            SadNoteTeardropStoryboard?.Begin();
            SadNoteBlinkStoryboard?.Begin();
            _isStoryboardRunning = true;
        }
        catch
        {
            // Ignore transient visual-tree state errors during teardown
        }
    }

    private void StopAnimations()
    {
        if (!_isStoryboardRunning)
        {
            return;
        }

        try
        {
            SadNoteFloatStoryboard?.Stop();
            SadNoteTeardropStoryboard?.Stop();
            SadNoteBlinkStoryboard?.Stop();
        }
        catch
        {
            // Ignore transient teardown errors
        }
        finally
        {
            _isStoryboardRunning = false;
        }
    }

    private static Color BlendColor(Color a, Color b, float t)
    {
        float clamped = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            a.A,
            (byte)Math.Clamp((int)MathF.Round(a.R + ((b.R - a.R) * clamped)), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(a.G + ((b.G - a.G) * clamped)), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(a.B + ((b.B - a.B) * clamped)), 0, 255));
    }
}
