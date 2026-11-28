using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Manages dynamic ResourceDictionaries, design tokens, window backdrop materials (Mica, Acrylic),
/// and real-time Win2D Liquid Glass shader parameters.
/// </summary>
public interface IThemeService
{
    /// <summary>
    /// Raised when the active theme palette changes.
    /// </summary>
    event EventHandler<ThemePaletteDefinition>? ThemeChanged;

    /// <summary>
    /// Raised when the Win2D Liquid Glass shader parameters are updated in real-time.
    /// </summary>
    event EventHandler<LiquidGlassParameters>? LiquidGlassParametersChanged;

    /// <summary>
    /// Raised when the active accent color changes (either dynamically from album art or manually selected).
    /// </summary>
    event EventHandler<string>? AccentColorChanged;

    /// <summary>
    /// Gets the currently active theme palette definition.
    /// </summary>
    ThemePaletteDefinition CurrentTheme { get; }

    /// <summary>
    /// Gets the active appearance mode preference (System, Dark, or Light).
    /// </summary>
    AppThemePreference ThemePreference { get; }

    /// <summary>
    /// Gets the current Win2D Liquid Glass effect parameters.
    /// </summary>
    LiquidGlassParameters LiquidGlass { get; }

    /// <summary>
    /// Gets a value indicating whether the primary accent color dynamically adapts to the current track's album cover art.
    /// </summary>
    bool IsDynamicCoverAccentEnabled { get; }

    /// <summary>
    /// Gets the currently active primary accent hexadecimal color (#AARRGGBB).
    /// </summary>
    string ActiveAccentHex { get; }

    /// <summary>
    /// Gets the built-in and custom registered theme palettes.
    /// </summary>
    IReadOnlyList<ThemePaletteDefinition> AvailableThemes { get; }

    /// <summary>
    /// Initializes the theme engine and applies persisted visual tokens and backdrop materials.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a theme palette by its unique identifier at runtime without restarting.
    /// </summary>
    /// <param name="themeId">The target theme identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyThemeAsync(string themeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the appearance preference (<see cref="AppThemePreference.System"/>, <see cref="AppThemePreference.Dark"/>,
    /// or <see cref="AppThemePreference.Light"/>) and immediately synchronizes the active theme.
    /// </summary>
    /// <param name="preference">The desired theme synchronization mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetThemePreferenceAsync(AppThemePreference preference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a custom theme definition loaded from JSON or created dynamically by the user.
    /// </summary>
    /// <param name="customTheme">The custom theme palette definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyCustomThemeAsync(ThemePaletteDefinition customTheme, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the window backdrop material (LiquidGlassMica, MicaAlt, DesktopAcrylic, SolidPerformance).
    /// </summary>
    /// <param name="backdropKind">The desired backdrop material kind.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetBackdropMaterialAsync(BackdropMaterialKind backdropKind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the real-time Win2D Liquid Glass parameters and notifies all active glass surfaces to redraw.
    /// </summary>
    /// <param name="parameters">New Liquid Glass parameters.</param>
    /// <param name="persist">Whether to save the parameters to persistent storage.</param>
    Task UpdateLiquidGlassParametersAsync(LiquidGlassParameters parameters, bool persist = false);

    /// <summary>
    /// Enables or disables automatic dynamic accent color extraction from the current song's album cover art.
    /// </summary>
    /// <param name="isEnabled">True to adapt dynamically to album art; false to use the manual/custom accent color.</param>
    /// <param name="currentCoverArtUri">Optional current track cover art URI to immediately extract if enabled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetDynamicCoverAccentEnabledAsync(bool isEnabled, string? currentCoverArtUri = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the dominant vibrant color from the specified album cover image URI and updates all accent tokens dynamically.
    /// </summary>
    /// <param name="coverArtUri">Local file path or ms-appx URI of the album cover image.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyAlbumCoverAccentAsync(string? coverArtUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a manual custom accent color (#RRGGBB or #AARRGGBB), disabling automatic album cover extraction.
    /// </summary>
    /// <param name="accentHex">The desired accent color in hexadecimal format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetCustomAccentColorAsync(string accentHex, CancellationToken cancellationToken = default);
}
