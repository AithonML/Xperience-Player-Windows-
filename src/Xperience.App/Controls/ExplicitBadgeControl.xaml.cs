using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Xperience.App.Controls;

/// <summary>
/// Adaptive Apple Music / YouTube Music style "E" badge indicating explicit track content.
/// </summary>
public sealed partial class ExplicitBadgeControl : UserControl
{
    private static readonly SolidColorBrush DarkContextFg = new(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush DarkContextBg = new(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush DarkContextBorder = new(Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF));

    private static readonly SolidColorBrush LightContextFg = new(Color.FromArgb(0xCC, 0x0F, 0x17, 0x2A));
    private static readonly SolidColorBrush LightContextBg = new(Color.FromArgb(0x17, 0x0F, 0x17, 0x2A));
    private static readonly SolidColorBrush LightContextBorder = new(Color.FromArgb(0x33, 0x0F, 0x17, 0x2A));

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact),
        typeof(bool),
        typeof(ExplicitBadgeControl),
        new PropertyMetadata(false, OnAppearancePropertyChanged));

    public static readonly DependencyProperty UseLightInkProperty = DependencyProperty.Register(
        nameof(UseLightInk),
        typeof(bool),
        typeof(ExplicitBadgeControl),
        new PropertyMetadata(false, OnAppearancePropertyChanged));

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public bool UseLightInk
    {
        get => (bool)GetValue(UseLightInkProperty);
        set => SetValue(UseLightInkProperty, value);
    }

    public ExplicitBadgeControl()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateAppearance();
        Loaded += (_, _) => UpdateAppearance();
        UpdateAppearance();
    }

    private static void OnAppearancePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ExplicitBadgeControl control)
        {
            control.UpdateAppearance();
        }
    }

    private void UpdateAppearance()
    {
        if (BadgeBorder is null || BadgeGlyphText is null)
        {
            return;
        }

        bool compact = IsCompact;
        double size = compact ? 14.0 : 16.0;
        double radius = compact ? 3.5 : 4.0;
        double fontSize = compact ? 8.5 : 9.5;

        BadgeBorder.Width = size;
        BadgeBorder.Height = size;
        BadgeBorder.CornerRadius = new CornerRadius(radius);
        BadgeGlyphText.FontSize = fontSize;
        BadgeGlyphText.LineHeight = fontSize;

        bool darkSurface = UseLightInk || ActualTheme != ElementTheme.Light;
        BadgeGlyphText.Foreground = darkSurface ? DarkContextFg : LightContextFg;
        BadgeBorder.Background = darkSurface ? DarkContextBg : LightContextBg;
        BadgeBorder.BorderBrush = darkSurface ? DarkContextBorder : LightContextBorder;
    }
}
