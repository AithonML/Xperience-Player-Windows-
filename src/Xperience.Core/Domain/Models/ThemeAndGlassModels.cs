using Xperience.Core.Domain.Enums;

namespace Xperience.Core.Domain.Models;

/// <summary>
/// Encapsulates the real-time GPU shader parameters for the Win2D Liquid Glass effect.
/// Inspired by iOS 26 Liquid Glass and Win2D displacement/turbulence pipelines.
/// </summary>
public sealed record LiquidGlassParameters
{
    /// <summary>
    /// Gets the hexadecimal ARGB tint color applied over the refracted glass surface.
    /// </summary>
    public string TintColorHex { get; init; } = "#FF1A1B20";

    /// <summary>
    /// Gets the tint overlay opacity (0.0 to 1.0). Default is 0.50 (50%), aligned with Android GlassEffectConfig.surfaceOpacity.
    /// </summary>
    public double TintOpacity { get; init; } = 0.50;

    /// <summary>
    /// Gets the Gaussian blur radius for the frosted glass layer (0.0 to 30.0 px). Default is 2.0 px (Android blurRadius = 2dp).
    /// </summary>
    public double FrostBlurRadius { get; init; } = 2.0;

    /// <summary>
    /// Gets the organic surface turbulence frequency (0.001 to 0.025). Default is 0.008.
    /// </summary>
    public double TurbulenceFrequency { get; init; } = 0.008;

    /// <summary>
    /// Gets the meniscus edge refraction strength (0.0 to 200.0). Default is 60.0 (60%, Android lensAmount = 0.6f).
    /// </summary>
    public double DistortionStrength { get; init; } = 60.0;

    /// <summary>
    /// Gets the intensity of the specular top-left / bottom-right glass rim reflection (0.0 to 1.0). Default is 0.55 (55%, Android EdgeHighlightAlpha = 0.55f).
    /// </summary>
    public double SpecularRimIntensity { get; init; } = 0.55;

    /// <summary>
    /// Gets the inner shadow blur radius in pixels (0.0 to 30.0).
    /// </summary>
    public double InnerShadowBlur { get; init; } = 8.0;

    /// <summary>
    /// Gets the saturation factor for colors refracted through the glass (0.5 to 2.0). Default is 1.60 (Android vibrancy = 1.2 -> 1.0 + 0.5 * 1.2 = 1.60).
    /// </summary>
    public double SaturationBoost { get; init; } = 1.60;

    /// <summary>
    /// Gets the 7-tap spectral chromatic dispersion intensity (0.0 to 4.0). Default is 1.25.
    /// </summary>
    public double ChromaticAberration { get; init; } = 1.25;

    /// <summary>
    /// Gets a value indicating whether the real-time Win2D displacement shader is active.
    /// </summary>
    public bool IsShaderEnabled { get; init; } = true;
}

/// <summary>
/// Represents a complete visual theme and design token palette that can be serialized to/from JSON
/// and injected dynamically at runtime.
/// </summary>
public sealed record ThemePaletteDefinition
{
    /// <summary>
    /// Gets the unique theme identifier (e.g., "ObsidianLiquidDark", "CrystalLight", "HighContrast", or custom ID).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the human-readable display name of the theme.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets a value indicating whether the base theme uses dark luminances.
    /// </summary>
    public bool IsDark { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether high-contrast accessibility tokens are active.
    /// </summary>
    public bool IsHighContrast { get; init; }

    /// <summary>
    /// Gets the preferred backdrop material for this theme.
    /// </summary>
    public BackdropMaterialKind BackdropKind { get; init; } = BackdropMaterialKind.LiquidGlassMica;

    /// <summary>
    /// Gets the primary background color token (#AARRGGBB).
    /// </summary>
    public string AppBackgroundHex { get; init; } = "#FF111215";

    /// <summary>
    /// Gets the secondary elevated surface color token (#AARRGGBB).
    /// </summary>
    public string SurfaceBackgroundHex { get; init; } = "#6624272D";

    /// <summary>
    /// Gets the primary accent color token (#AARRGGBB) used for active states and progress bars.
    /// </summary>
    public string PrimaryAccentHex { get; init; } = "#FF2DD4BF";

    /// <summary>
    /// Gets the secondary accent color token (#AARRGGBB) used for gradients and highlights.
    /// </summary>
    public string SecondaryAccentHex { get; init; } = "#FF38BDF8";

    /// <summary>
    /// Gets the primary foreground text color token (#AARRGGBB).
    /// </summary>
    public string PrimaryTextHex { get; init; } = "#FFFFFFFF";

    /// <summary>
    /// Gets the secondary/muted foreground text color token (#AARRGGBB).
    /// </summary>
    public string SecondaryTextHex { get; init; } = "#FFB0B6C2";

    /// <summary>
    /// Gets the glass specular border color token (#AARRGGBB).
    /// </summary>
    public string GlassBorderHex { get; init; } = "#48FFFFFF";

    /// <summary>
    /// Gets the associated Liquid Glass shader parameters for this theme.
    /// </summary>
    public LiquidGlassParameters LiquidGlass { get; init; } = new();
}

/// <summary>
/// Represents a supported application locale for dynamic runtime localization.
/// </summary>
/// <param name="CultureCode">BCP-47 language tag (e.g., "en-US", "es-ES", "ar-SA").</param>
/// <param name="NativeName">Display name in the target language.</param>
/// <param name="IsRightToLeft">True if the script uses Right-To-Left (RTL) flow direction.</param>
public sealed record LanguageOption(string CultureCode, string NativeName, bool IsRightToLeft = false);
