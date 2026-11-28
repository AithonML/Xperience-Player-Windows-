using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.ApplicationModel.Resources;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Services;

/// <summary>
/// Enterprise localization service combining <see cref="ResourceManager"/> with hot-reloadable
/// `.resw` dictionaries for zero-restart runtime language and RTL/LTR switching.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private const string LanguageSettingKey = "UserLanguageCulture";
    private readonly IStorageService _storageService;
    private readonly ILogger<LocalizationService> _logger;
    private readonly Dictionary<string, Dictionary<string, string>> _cultureDictionaries = new(StringComparer.OrdinalIgnoreCase);
    private ResourceManager? _resourceManager;

    /// <inheritdoc />
    public event EventHandler<LanguageOption>? LanguageChanged;

    /// <inheritdoc />
    public IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new LanguageOption("en-US", "English (US)", IsRightToLeft: false),
        new LanguageOption("es-ES", "Español (ES)", IsRightToLeft: false),
        new LanguageOption("ar-SA", "العربية (RTL)", IsRightToLeft: true)
    ];

    /// <inheritdoc />
    public LanguageOption CurrentLanguage { get; private set; }

    /// <inheritdoc />
    public bool IsRightToLeft => CurrentLanguage.IsRightToLeft;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalizationService"/> class.
    /// </summary>
    public LocalizationService(IStorageService storageService, ILogger<LocalizationService> logger)
    {
        _storageService = storageService;
        _logger = logger;
        CurrentLanguage = SupportedLanguages[0];
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var priPath = Path.Combine(AppContext.BaseDirectory, "resources.pri");
            _resourceManager = File.Exists(priPath)
                ? new ResourceManager(priPath)
                : new ResourceManager();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WinAppSDK ResourceManager fallback to direct .resw XML provider.");
        }

        await Task.Run(LoadReswDictionariesFromDisk, cancellationToken).ConfigureAwait(false);

        var savedCulture = await _storageService.LoadAsync<string>(
            LanguageSettingKey,
            defaultValue: null,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(savedCulture))
        {
            await SetLanguageAsync("en-US", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ApplyLanguageInMemory(savedCulture);
        }
    }

    /// <inheritdoc />
    public string GetString(string resourceKey)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            return string.Empty;
        }

        // 1. Check active hot-reload dictionary for instant runtime culture switching
        if (_cultureDictionaries.TryGetValue(CurrentLanguage.CultureCode, out var activeDict))
        {
            if (activeDict.TryGetValue(resourceKey, out var value))
            {
                return value;
            }

            if (activeDict.TryGetValue($"{resourceKey}.Text", out var textVal))
            {
                return textVal;
            }

            if (activeDict.TryGetValue($"{resourceKey}.Content", out var contentVal))
            {
                return contentVal;
            }

            if (activeDict.TryGetValue($"{resourceKey}.PlaceholderText", out var placeholderVal))
            {
                return placeholderVal;
            }
        }

        // 2. Fallback to Windows App SDK ResourceManager PRI map
        if (_resourceManager is not null)
        {
            try
            {
                var candidate = _resourceManager.MainResourceMap.TryGetValue($"Resources/{resourceKey}");
                if (candidate is not null && !string.IsNullOrEmpty(candidate.ValueAsString))
                {
                    return candidate.ValueAsString;
                }
            }
            catch
            {
                // Ignore PRI lookup exceptions in unpackaged mode
            }
        }

        // 3. Fallback to default English dictionary
        if (_cultureDictionaries.TryGetValue("en-US", out var fallbackDict) &&
            fallbackDict.TryGetValue(resourceKey, out var fallbackValue))
        {
            return fallbackValue;
        }

        return resourceKey;
    }

    /// <inheritdoc />
    public async Task SetLanguageAsync(string cultureCode, CancellationToken cancellationToken = default)
    {
        var matchedLanguage = ApplyLanguageInMemory(cultureCode);

        await _storageService.SaveAsync(LanguageSettingKey, matchedLanguage.CultureCode, cancellationToken)
            .ConfigureAwait(false);
    }

    private LanguageOption ApplyLanguageInMemory(string cultureCode)
    {
        var matchedLanguage = SupportedLanguages.FirstOrDefault(
            l => string.Equals(l.CultureCode, cultureCode, StringComparison.OrdinalIgnoreCase))
            ?? SupportedLanguages[0];

        CurrentLanguage = matchedLanguage;

        var cultureInfo = new CultureInfo(matchedLanguage.CultureCode);
        CultureInfo.CurrentCulture = cultureInfo;
        CultureInfo.CurrentUICulture = cultureInfo;
        CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
        CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

        LanguageChanged?.Invoke(this, matchedLanguage);
        return matchedLanguage;
    }

    private void LoadReswDictionariesFromDisk()
    {
        var baseDir = AppContext.BaseDirectory;
        var stringsRoot = Path.Combine(baseDir, "Strings");
        var assembly = typeof(LocalizationService).Assembly;
        var embeddedNames = assembly.GetManifestResourceNames();

        foreach (var lang in SupportedLanguages)
        {
            XDocument? doc = null;
            var reswPath = Path.Combine(stringsRoot, lang.CultureCode, "Resources.resw");

            if (File.Exists(reswPath))
            {
                try
                {
                    doc = XDocument.Load(reswPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse .resw file at {Path}", reswPath);
                }
            }

            if (doc is null)
            {
                // Packaged MSIX fallback: load embedded .resw compiled into Xperience.App.dll
                string cultureTokenDot = $".{lang.CultureCode}.";
                string cultureTokenUnderscore = $".{lang.CultureCode.Replace('-', '_')}.";
                string? resourceName = embeddedNames.FirstOrDefault(n =>
                    n.EndsWith("Resources.resw", StringComparison.OrdinalIgnoreCase) &&
                    (n.Contains(cultureTokenDot, StringComparison.OrdinalIgnoreCase) ||
                     n.Contains(cultureTokenUnderscore, StringComparison.OrdinalIgnoreCase)));

                if (resourceName is not null)
                {
                    try
                    {
                        using var stream = assembly.GetManifestResourceStream(resourceName);
                        if (stream is not null)
                        {
                            doc = XDocument.Load(stream);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse embedded .resw resource {ResourceName}", resourceName);
                    }
                }
            }

            if (doc is null)
            {
                continue;
            }

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dataNode in doc.Descendants("data"))
            {
                var nameAttr = dataNode.Attribute("name")?.Value;
                var valElem = dataNode.Element("value")?.Value;
                if (!string.IsNullOrWhiteSpace(nameAttr) && valElem is not null)
                {
                    dict[nameAttr] = valElem;
                }
            }

            _cultureDictionaries[lang.CultureCode] = dict;
        }
    }
}
