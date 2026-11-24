using Xperience.Core.Domain.Models;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Manages internationalization (i18n) and runtime localization (L10n) with hot-reload and RTL support.
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    /// Raised whenever the active language or flow direction changes at runtime.
    /// </summary>
    event EventHandler<LanguageOption>? LanguageChanged;

    /// <summary>
    /// Gets the currently active language option.
    /// </summary>
    LanguageOption CurrentLanguage { get; }

    /// <summary>
    /// Gets a value indicating whether the active culture requires Right-To-Left (RTL) layout flow.
    /// </summary>
    bool IsRightToLeft { get; }

    /// <summary>
    /// Gets the list of all supported languages registered in the application.
    /// </summary>
    IReadOnlyList<LanguageOption> SupportedLanguages { get; }

    /// <summary>
    /// Initializes the localization engine and restores the user's saved language preference.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a localized string resource by its identifier key.
    /// </summary>
    /// <param name="resourceKey">The resource key (e.g., "Nav_Library" or "NowPlaying_Header").</param>
    /// <returns>The localized string for the current culture, or the key itself if not found.</returns>
    string GetString(string resourceKey);

    /// <summary>
    /// Switches the application language in real-time without restarting the process.
    /// </summary>
    /// <param name="cultureCode">BCP-47 culture code (e.g., "en-US", "es-ES", "ar-SA").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetLanguageAsync(string cultureCode, CancellationToken cancellationToken = default);
}
