using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Xperience.App.Helpers;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Services;

/// <summary>
/// Dynamically manages design tokens, ResourceDictionaries, Window system backdrops (Mica, MicaAlt, Acrylic),
/// and real-time Win2D Liquid Glass shader parameters.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private const string ThemeIdSettingKey = "SelectedThemeId";
    private const string ThemePreferenceSettingKey = "AppThemePreference_v1";
    private const string LastDarkThemeIdSettingKey = "LastDarkThemeId_v1";
    private const string CustomThemeSettingKey = "CustomUserTheme";
    private const string LiquidGlassSettingKey = "LiquidGlassShaderParams_v8";
    private const string DynamicAccentEnabledKey = "IsDynamicCoverAccentEnabled";
    private const string ManualAccentHexKey = "ManualAccentColorHex";

    private readonly IStorageService _storageService;
    private readonly ILogger<ThemeService> _logger;
    private readonly List<ThemePaletteDefinition> _themes;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private string _manualAccentHex = "#FF2DD4BF";
    private string _lastDarkThemeId = "ObsidianLiquidDark";

    /// <inheritdoc />
    public event EventHandler<ThemePaletteDefinition>? ThemeChanged;

    /// <inheritdoc />
    public event EventHandler<LiquidGlassParameters>? LiquidGlassParametersChanged;

    /// <inheritdoc />
    public event EventHandler<string>? AccentColorChanged;

    /// <inheritdoc />
    public ThemePaletteDefinition CurrentTheme { get; private set; }

    /// <inheritdoc />
    public AppThemePreference ThemePreference { get; private set; } = AppThemePreference.Dark;

    /// <inheritdoc />
    public LiquidGlassParameters LiquidGlass { get; private set; }

    /// <inheritdoc />
    public bool IsDynamicCoverAccentEnabled { get; private set; } = true;

    /// <inheritdoc />
    public string ActiveAccentHex { get; private set; } = "#FF2DD4BF";

    /// <inheritdoc />
    public IReadOnlyList<ThemePaletteDefinition> AvailableThemes => _themes;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThemeService"/> class.
    /// </summary>
    public ThemeService(IStorageService storageService, ILogger<ThemeService> logger)
    {
        _storageService = storageService;
        _logger = logger;

        _themes =
        [
            new ThemePaletteDefinition
            {
                Id = "ObsidianLiquidDark",
                DisplayName = "Obsidian Liquid Glass (Default)",
                IsDark = true,
                IsHighContrast = false,
                BackdropKind = BackdropMaterialKind.LiquidGlassMica,
                AppBackgroundHex = "#FF111215",
                SurfaceBackgroundHex = "#6823262C",
                PrimaryAccentHex = "#FF2DD4BF",
                SecondaryAccentHex = "#FF38BDF8",
                PrimaryTextHex = "#FFFFFFFF",
                SecondaryTextHex = "#FFB0B6C2",
                GlassBorderHex = "#4CFFFFFF",
                LiquidGlass = new LiquidGlassParameters
                {
                    TintColorHex = "#FF1A1B20",
                    TintOpacity = 0.50,
                    FrostBlurRadius = 2.0,
                    TurbulenceFrequency = 0.008,
                    DistortionStrength = 60.0,
                    SpecularRimIntensity = 0.55,
                    InnerShadowBlur = 8.0,
                    SaturationBoost = 1.60,
                    IsShaderEnabled = true
                }
            },
            new ThemePaletteDefinition
            {
                Id = "NeonAuroraDark",
                DisplayName = "Aurora Cyber Glass",
                IsDark = true,
                IsHighContrast = false,
                BackdropKind = BackdropMaterialKind.DesktopAcrylic,
                AppBackgroundHex = "#FF0D0B14",
                SurfaceBackgroundHex = "#681E1B2E",
                PrimaryAccentHex = "#FFF43F5E",
                SecondaryAccentHex = "#FFA855F7",
                PrimaryTextHex = "#FFFFFFFF",
                SecondaryTextHex = "#FFC4B5FD",
                GlassBorderHex = "#59F472B6",
                LiquidGlass = new LiquidGlassParameters
                {
                    TintColorHex = "#FF1A1B20",
                    TintOpacity = 0.50,
                    FrostBlurRadius = 2.0,
                    TurbulenceFrequency = 0.008,
                    DistortionStrength = 60.0,
                    SpecularRimIntensity = 0.55,
                    InnerShadowBlur = 8.0,
                    SaturationBoost = 1.60,
                    IsShaderEnabled = true
                }
            },
            new ThemePaletteDefinition
            {
                Id = "CrystalDaylight",
                DisplayName = "Crystal Daylight (Light)",
                IsDark = false,
                IsHighContrast = false,
                BackdropKind = BackdropMaterialKind.LiquidGlassMica,
                AppBackgroundHex = "#FFEEF2F6",
                SurfaceBackgroundHex = "#B8FFFFFF",
                PrimaryAccentHex = "#FF0D9488",
                SecondaryAccentHex = "#FF0284C7",
                PrimaryTextHex = "#FF0F172A",
                SecondaryTextHex = "#FF475569",
                GlassBorderHex = "#99CBD5E1",
                LiquidGlass = new LiquidGlassParameters
                {
                    TintColorHex = "#FFFAFAFA",
                    TintOpacity = 0.50,
                    FrostBlurRadius = 2.0,
                    TurbulenceFrequency = 0.008,
                    DistortionStrength = 60.0,
                    SpecularRimIntensity = 0.55,
                    InnerShadowBlur = 8.0,
                    SaturationBoost = 1.58,
                    IsShaderEnabled = true
                }
            },
            new ThemePaletteDefinition
            {
                Id = "HighContrastAccessibility",
                DisplayName = "High Contrast (WCAG AAA)",
                IsDark = true,
                IsHighContrast = true,
                BackdropKind = BackdropMaterialKind.SolidPerformance,
                AppBackgroundHex = "#FF000000",
                SurfaceBackgroundHex = "#FF121212",
                PrimaryAccentHex = "#FFFFFF00",
                SecondaryAccentHex = "#FF00FFFF",
                PrimaryTextHex = "#FFFFFFFF",
                SecondaryTextHex = "#FFE2E8F0",
                GlassBorderHex = "#FFFFFFFF",
                LiquidGlass = new LiquidGlassParameters
                {
                    TintColorHex = "#FF000000",
                    TintOpacity = 0.85,
                    FrostBlurRadius = 0.0,
                    TurbulenceFrequency = 0.0,
                    DistortionStrength = 0.0,
                    SpecularRimIntensity = 1.0,
                    InnerShadowBlur = 0.0,
                    SaturationBoost = 1.0,
                    IsShaderEnabled = false
                }
            }
        ];

        CurrentTheme = _themes[0];
        LiquidGlass = CurrentTheme.LiquidGlass;
        _uiSettings.ColorValuesChanged += OnSystemColorValuesChanged;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var customTheme = await _storageService.LoadAsync<ThemePaletteDefinition>(
            CustomThemeSettingKey,
            defaultValue: null,
            cancellationToken);

        if (customTheme is not null && !_themes.Any(t => t.Id == customTheme.Id))
        {
            _themes.Add(customTheme);
        }

        var savedThemeId = await _storageService.LoadAsync<string>(
            ThemeIdSettingKey,
            defaultValue: _themes[0].Id,
            cancellationToken);

        var savedLastDarkId = await _storageService.LoadAsync<string>(
            LastDarkThemeIdSettingKey,
            defaultValue: null,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(savedLastDarkId) &&
            _themes.Any(t => string.Equals(t.Id, savedLastDarkId, StringComparison.OrdinalIgnoreCase) && t.IsDark))
        {
            _lastDarkThemeId = savedLastDarkId;
        }

        var matched = _themes.FirstOrDefault(t => string.Equals(t.Id, savedThemeId, StringComparison.OrdinalIgnoreCase)) ?? _themes[0];
        if (matched.IsDark)
        {
            _lastDarkThemeId = matched.Id;
        }

        var savedPreference = await _storageService.LoadAsync<AppThemePreference?>(
            ThemePreferenceSettingKey,
            defaultValue: null,
            cancellationToken);

        ThemePreference = savedPreference ?? (matched.IsDark ? AppThemePreference.Dark : AppThemePreference.Light);
        if (ThemePreference == AppThemePreference.System)
        {
            matched = ResolveSystemMatchedTheme();
        }

        CurrentTheme = matched;

        var savedGlass = await _storageService.LoadAsync<LiquidGlassParameters>(
            LiquidGlassSettingKey,
            defaultValue: matched.LiquidGlass,
            cancellationToken);

        LiquidGlass = savedGlass ?? matched.LiquidGlass;

        IsDynamicCoverAccentEnabled = await _storageService.LoadAsync<bool>(
            DynamicAccentEnabledKey,
            defaultValue: true,
            cancellationToken);

        var savedManualHex = await _storageService.LoadAsync<string>(
            ManualAccentHexKey,
            defaultValue: matched.PrimaryAccentHex,
            cancellationToken);

        _manualAccentHex = string.IsNullOrWhiteSpace(savedManualHex) ? matched.PrimaryAccentHex : savedManualHex;
        ActiveAccentHex = _manualAccentHex;

        ApplyThemeTokensToResources(CurrentTheme);
        if (!IsDynamicCoverAccentEnabled)
        {
            ApplyAccentColorSuite(ColorHelperEx.ParseHex(_manualAccentHex));
        }

        ThemeChanged?.Invoke(this, CurrentTheme);
        LiquidGlassParametersChanged?.Invoke(this, LiquidGlass);
    }

    /// <inheritdoc />
    public async Task ApplyThemeAsync(string themeId, CancellationToken cancellationToken = default)
    {
        var target = _themes.FirstOrDefault(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return;
        }

        if (target.IsDark)
        {
            _lastDarkThemeId = target.Id;
            await _storageService.SaveAsync(LastDarkThemeIdSettingKey, _lastDarkThemeId, cancellationToken);
        }

        ThemePreference = target.IsDark ? AppThemePreference.Dark : AppThemePreference.Light;
        await _storageService.SaveAsync(ThemePreferenceSettingKey, ThemePreference, cancellationToken);
        await ApplyResolvedPaletteCoreAsync(target, persistThemeId: true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetThemePreferenceAsync(AppThemePreference preference, CancellationToken cancellationToken = default)
    {
        ThemePreference = preference;
        await _storageService.SaveAsync(ThemePreferenceSettingKey, preference, cancellationToken);

        ThemePaletteDefinition target = preference switch
        {
            AppThemePreference.System => ResolveSystemMatchedTheme(),
            AppThemePreference.Light => _themes.FirstOrDefault(t => !t.IsDark) ?? _themes[0],
            _ => _themes.FirstOrDefault(t => string.Equals(t.Id, _lastDarkThemeId, StringComparison.OrdinalIgnoreCase) && t.IsDark)
                 ?? _themes.FirstOrDefault(t => t.IsDark)
                 ?? _themes[0]
        };

        await ApplyResolvedPaletteCoreAsync(target, persistThemeId: preference != AppThemePreference.System, cancellationToken);
    }

    private async Task ApplyResolvedPaletteCoreAsync(
        ThemePaletteDefinition target,
        bool persistThemeId,
        CancellationToken cancellationToken)
    {
        CurrentTheme = target;
        LiquidGlass = target.LiquidGlass;

        RunOnUiThread(() =>
        {
            ApplyThemeTokensToResources(CurrentTheme);
            ApplyWindowBackdrop(CurrentTheme.BackdropKind);
        });

        if (persistThemeId)
        {
            await _storageService.SaveAsync(ThemeIdSettingKey, CurrentTheme.Id, cancellationToken);
        }

        await _storageService.SaveAsync(LiquidGlassSettingKey, LiquidGlass, cancellationToken);

        RunOnUiThread(() =>
        {
            ThemeChanged?.Invoke(this, CurrentTheme);
            LiquidGlassParametersChanged?.Invoke(this, LiquidGlass);
        });
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = App.MainWindowInstance?.DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(() => action());
            return;
        }

        action();
    }

    private ThemePaletteDefinition ResolveSystemMatchedTheme()
    {
        bool systemIsDark = IsSystemDarkMode();
        if (systemIsDark)
        {
            return _themes.FirstOrDefault(t => string.Equals(t.Id, _lastDarkThemeId, StringComparison.OrdinalIgnoreCase) && t.IsDark)
                   ?? _themes.FirstOrDefault(t => t.IsDark)
                   ?? _themes[0];
        }

        return _themes.FirstOrDefault(t => !t.IsDark) ?? _themes[0];
    }

    private bool IsSystemDarkMode()
    {
        try
        {
            var bg = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            return ((5 * bg.G) + (2 * bg.R) + bg.B) <= (8 * 128);
        }
        catch
        {
            return true;
        }
    }

    private void OnSystemColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args)
    {
        if (ThemePreference != AppThemePreference.System)
        {
            return;
        }

        var dispatcher = App.MainWindowInstance?.DispatcherQueue;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.TryEnqueue(() =>
        {
            if (ThemePreference != AppThemePreference.System)
            {
                return;
            }

            var resolved = ResolveSystemMatchedTheme();
            if (!string.Equals(resolved.Id, CurrentTheme.Id, StringComparison.OrdinalIgnoreCase))
            {
                _ = ApplyResolvedPaletteCoreAsync(resolved, persistThemeId: false, CancellationToken.None);
            }
        });
    }

    /// <inheritdoc />
    public async Task ApplyCustomThemeAsync(ThemePaletteDefinition customTheme, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customTheme);

        var existingIdx = _themes.FindIndex(t => string.Equals(t.Id, customTheme.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIdx >= 0)
        {
            _themes[existingIdx] = customTheme;
        }
        else
        {
            _themes.Add(customTheme);
        }

        await _storageService.SaveAsync(CustomThemeSettingKey, customTheme, cancellationToken);
        await ApplyThemeAsync(customTheme.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetBackdropMaterialAsync(BackdropMaterialKind backdropKind, CancellationToken cancellationToken = default)
    {
        CurrentTheme = CurrentTheme with { BackdropKind = backdropKind };
        var isShaderActive = backdropKind != BackdropMaterialKind.SolidPerformance;
        LiquidGlass = LiquidGlass with { IsShaderEnabled = isShaderActive };

        RunOnUiThread(() =>
        {
            ApplyWindowBackdrop(backdropKind);
            ThemeChanged?.Invoke(this, CurrentTheme);
            LiquidGlassParametersChanged?.Invoke(this, LiquidGlass);
        });
        await _storageService.SaveAsync(LiquidGlassSettingKey, LiquidGlass, cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpdateLiquidGlassParametersAsync(LiquidGlassParameters parameters, bool persist = false)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        LiquidGlass = parameters;
        RunOnUiThread(() => LiquidGlassParametersChanged?.Invoke(this, LiquidGlass));

        if (persist)
        {
            await _storageService.SaveAsync(LiquidGlassSettingKey, LiquidGlass);
        }
    }

    /// <inheritdoc />
    public async Task SetDynamicCoverAccentEnabledAsync(bool isEnabled, string? currentCoverArtUri = null, CancellationToken cancellationToken = default)
    {
        IsDynamicCoverAccentEnabled = isEnabled;
        await _storageService.SaveAsync(DynamicAccentEnabledKey, isEnabled, cancellationToken);

        if (isEnabled)
        {
            await ApplyAlbumCoverAccentAsync(currentCoverArtUri, cancellationToken);
        }
        else
        {
            var manualColor = ColorHelperEx.ParseHex(_manualAccentHex);
            RunOnUiThread(() =>
            {
                ApplyAccentColorSuite(manualColor);
                AccentColorChanged?.Invoke(this, ActiveAccentHex);
            });
        }
    }

    /// <inheritdoc />
    public async Task ApplyAlbumCoverAccentAsync(string? coverArtUri, CancellationToken cancellationToken = default)
    {
        if (!IsDynamicCoverAccentEnabled)
        {
            return;
        }

        var defaultFallback = ColorHelperEx.ParseHex(
            _manualAccentHex,
            ColorHelperEx.ParseHex(CurrentTheme.PrimaryAccentHex));

        var dominantColor = await ColorHelperEx.ExtractDominantVibrantColorAsync(
            coverArtUri,
            defaultFallback,
            cancellationToken).ConfigureAwait(false);

        if (App.MainWindowInstance?.DispatcherQueue is { } dispatcher)
        {
            dispatcher.TryEnqueue(() =>
            {
                ApplyAccentColorSuite(dominantColor);
                AccentColorChanged?.Invoke(this, ActiveAccentHex);
            });
        }
        else
        {
            ApplyAccentColorSuite(dominantColor);
            AccentColorChanged?.Invoke(this, ActiveAccentHex);
        }
    }

    /// <inheritdoc />
    public async Task SetCustomAccentColorAsync(string accentHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accentHex))
        {
            return;
        }

        _manualAccentHex = accentHex;
        IsDynamicCoverAccentEnabled = false;

        var parsed = ColorHelperEx.ParseHex(accentHex);
        RunOnUiThread(() => ApplyAccentColorSuite(parsed));

        await _storageService.SaveAsync(ManualAccentHexKey, _manualAccentHex, cancellationToken);
        await _storageService.SaveAsync(DynamicAccentEnabledKey, false, cancellationToken);

        RunOnUiThread(() => AccentColorChanged?.Invoke(this, ActiveAccentHex));
    }

    /// <summary>
    /// Applies the active window backdrop material (<see cref="MicaBackdrop"/>, <see cref="DesktopAcrylicBackdrop"/>)
    /// to the main application window on the UI thread.
    /// </summary>
    public void ApplyWindowBackdrop(BackdropMaterialKind kind)
    {
        if (App.MainWindowInstance is not Window window)
        {
            return;
        }

        try
        {
            switch (kind)
            {
                case BackdropMaterialKind.LiquidGlassMica:
                    if (MicaController.IsSupported())
                    {
                        window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    }
                    break;
                case BackdropMaterialKind.MicaAlt:
                    if (MicaController.IsSupported())
                    {
                        window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                    }
                    break;
                case BackdropMaterialKind.DesktopAcrylic:
                    if (DesktopAcrylicController.IsSupported())
                    {
                        window.SystemBackdrop = new DesktopAcrylicBackdrop();
                    }
                    break;
                case BackdropMaterialKind.SolidPerformance:
                    window.SystemBackdrop = null;
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SystemBackdrop '{Kind}' could not be applied; falling back to default.", kind);
        }
    }

    private void ApplyThemeTokensToResources(ThemePaletteDefinition palette)
    {
        if (Application.Current?.Resources is not ResourceDictionary resources)
        {
            return;
        }

        try
        {
            var appBgColor = ColorHelperEx.ParseHex(palette.AppBackgroundHex);
            var surfaceColor = ColorHelperEx.ParseHex(palette.SurfaceBackgroundHex);
            var primaryAccent = ColorHelperEx.ParseHex(palette.PrimaryAccentHex);
            var primaryText = ColorHelperEx.ParseHex(palette.PrimaryTextHex);
            var secondaryText = ColorHelperEx.ParseHex(palette.SecondaryTextHex);
            var glassBorder = ColorHelperEx.ParseHex(palette.GlassBorderHex);

            resources["TokenAppBackgroundColor"] = appBgColor;
            resources["TokenSurfaceBackgroundColor"] = surfaceColor;
            resources["TokenPrimaryTextColor"] = primaryText;
            resources["TokenSecondaryTextColor"] = secondaryText;
            resources["TokenGlassBorderColor"] = glassBorder;

            UpdateSolidBrush(resources, "TokenAppBackgroundBrush", appBgColor);
            UpdateSolidBrush(resources, "TokenSurfaceBackgroundBrush", surfaceColor);
            UpdateSolidBrush(resources, "TokenPrimaryTextBrush", primaryText);
            UpdateSolidBrush(resources, "TokenSecondaryTextBrush", secondaryText);
            UpdateSolidBrush(resources, "TokenGlassBorderBrush", glassBorder);
            UpdateSolidBrush(resources, "TokenSurfaceBorderBrush", glassBorder);

            if (palette.IsDark)
            {
                UpdateSolidBrush(resources, "TokenBrandPillBackgroundBrush", Windows.UI.Color.FromArgb(220, 11, 12, 14));
                UpdateSolidBrush(resources, "TokenActiveSegmentBackgroundBrush", Windows.UI.Color.FromArgb(54, 255, 255, 255));
                UpdateSolidBrush(resources, "TokenActiveSegmentBorderBrush", Windows.UI.Color.FromArgb(85, 255, 255, 255));
            }
            else
            {
                UpdateSolidBrush(resources, "TokenBrandPillBackgroundBrush", Windows.UI.Color.FromArgb(232, 255, 255, 255));
                UpdateSolidBrush(resources, "TokenActiveSegmentBackgroundBrush", Windows.UI.Color.FromArgb(34, 15, 23, 42));
                UpdateSolidBrush(resources, "TokenActiveSegmentBorderBrush", Windows.UI.Color.FromArgb(65, 15, 23, 42));
            }

            if (resources.TryGetValue("TokenVignetteMaskBrush", out var vigObj) &&
                vigObj is LinearGradientBrush vigBrush &&
                vigBrush.GradientStops.Count >= 2)
            {
                vigBrush.GradientStops[0].Color = Windows.UI.Color.FromArgb(0, appBgColor.R, appBgColor.G, appBgColor.B);
                vigBrush.GradientStops[1].Color = Windows.UI.Color.FromArgb(230, appBgColor.R, appBgColor.G, appBgColor.B);
            }

            if (!IsDynamicCoverAccentEnabled)
            {
                ApplyAccentColorSuite(ColorHelperEx.ParseHex(_manualAccentHex, primaryAccent));
            }
            else if (!string.IsNullOrWhiteSpace(ActiveAccentHex))
            {
                ApplyAccentColorSuite(ColorHelperEx.ParseHex(ActiveAccentHex, primaryAccent));
            }
            else
            {
                ApplyAccentColorSuite(primaryAccent);
            }

            if (App.MainWindowInstance?.Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = palette.IsDark ? ElementTheme.Dark : ElementTheme.Light;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update dynamic ResourceDictionary tokens.");
        }
    }

    private void ApplyAccentColorSuite(Windows.UI.Color baseAccentColor)
    {
        if (Application.Current?.Resources is not ResourceDictionary resources)
        {
            return;
        }

        try
        {
            var suite = ColorHelperEx.DeriveHarmoniousAccentSuite(baseAccentColor);
            ActiveAccentHex = ColorHelperEx.ToHex(suite.PrimaryAccent);

            resources["TokenPrimaryAccentColor"] = suite.PrimaryAccent;
            resources["TokenSecondaryAccentColor"] = suite.SecondaryAccent;
            resources["TokenTealButtonColor"] = suite.ActionButton;

            UpdateSolidBrush(resources, "TokenPrimaryAccentBrush", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "TokenSecondaryAccentBrush", suite.SecondaryAccent);
            UpdateSolidBrush(resources, "TokenTealActionBrush", suite.ActionButton);
            UpdateSolidBrush(resources, "TokenNowPlayingOverlayBrush", suite.NowPlayingOverlay);
            UpdateSolidBrush(resources, "TokenNowPlayingPlayButtonBrush", suite.NowPlayingPlayBtn);
            UpdateSolidBrush(resources, "TokenAccentBadgeBackgroundBrush", suite.BadgeBackground);
            UpdateSolidBrush(resources, "TokenAccentBadgeBorderBrush", suite.BadgeBorder);

            if (resources.TryGetValue("TokenProgressBarGradientBrush", out var gradObj) &&
                gradObj is LinearGradientBrush gradBrush &&
                gradBrush.GradientStops.Count >= 2)
            {
                gradBrush.GradientStops[0].Color = suite.PrimaryAccent;
                gradBrush.GradientStops[1].Color = suite.SecondaryAccent;
            }

            // Contrast-safe foreground on top of dynamic accent fills (crisp white by default, dark ink only on very bright pastels)
            double accentLuma = ((0.2126 * suite.PrimaryAccent.R) + (0.7152 * suite.PrimaryAccent.G) + (0.0722 * suite.PrimaryAccent.B)) / 255.0;
            Windows.UI.Color onAccentForeground = accentLuma > 0.74
                ? Windows.UI.Color.FromArgb(255, 17, 18, 21)
                : Colors.White;

            // Native WinUI 3 Controls (ToggleSwitch, AccentButtonStyle, CheckBox, RadioButton, NavigationView, Slider, TextBox)
            UpdateSolidBrush(resources, "AccentFillColorDefaultBrush", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "AccentFillColorSecondaryBrush", suite.SecondaryAccent);
            UpdateSolidBrush(resources, "AccentFillColorTertiaryBrush", suite.ActionButton);

            UpdateSolidBrush(resources, "ToggleSwitchFillOn", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "ToggleSwitchFillOnPointerOver", suite.SecondaryAccent);
            UpdateSolidBrush(resources, "ToggleSwitchFillOnPressed", suite.ActionButton);
            UpdateSolidBrush(resources, "ToggleSwitchStrokeOn", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "ToggleSwitchStrokeOnPointerOver", suite.SecondaryAccent);
            UpdateSolidBrush(resources, "ToggleSwitchStrokeOnPressed", suite.ActionButton);
            UpdateSolidBrush(resources, "ToggleSwitchKnobFillOn", onAccentForeground);
            UpdateSolidBrush(resources, "ToggleSwitchKnobFillOnPointerOver", onAccentForeground);
            UpdateSolidBrush(resources, "ToggleSwitchKnobFillOnPressed", onAccentForeground);

            UpdateSolidBrush(resources, "AccentButtonBackground", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "AccentButtonBackgroundPointerOver", suite.SecondaryAccent);
            UpdateSolidBrush(resources, "AccentButtonBackgroundPressed", suite.ActionButton);
            UpdateSolidBrush(resources, "AccentButtonBorderBrush", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "AccentButtonBorderBrushPointerOver", suite.SecondaryAccent);
            UpdateSolidBrush(resources, "AccentButtonBorderBrushPressed", suite.ActionButton);
            UpdateSolidBrush(resources, "AccentButtonForeground", onAccentForeground);
            UpdateSolidBrush(resources, "AccentButtonForegroundPointerOver", onAccentForeground);
            UpdateSolidBrush(resources, "AccentButtonForegroundPressed", onAccentForeground);

            UpdateSolidBrush(resources, "CheckBoxCheckBackgroundFillChecked", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "CheckBoxCheckBackgroundFillCheckedPointerOver", suite.SecondaryAccent);
            UpdateSolidBrush(resources, "CheckBoxCheckBackgroundFillCheckedPressed", suite.ActionButton);
            UpdateSolidBrush(resources, "CheckBoxCheckBackgroundStrokeChecked", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "CheckBoxCheckBackgroundStrokeCheckedPointerOver", suite.SecondaryAccent);
            UpdateSolidBrush(resources, "CheckBoxCheckBackgroundStrokeCheckedPressed", suite.ActionButton);
            UpdateSolidBrush(resources, "CheckBoxCheckGlyphForegroundChecked", onAccentForeground);
            UpdateSolidBrush(resources, "CheckBoxCheckGlyphForegroundCheckedPointerOver", onAccentForeground);
            UpdateSolidBrush(resources, "CheckBoxCheckGlyphForegroundCheckedPressed", onAccentForeground);

            UpdateSolidBrush(resources, "RadioButtonOuterEllipseCheckedFill", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "RadioButtonOuterEllipseCheckedPointerOverFill", suite.SecondaryAccent);
            UpdateSolidBrush(resources, "RadioButtonOuterEllipseCheckedPressedFill", suite.ActionButton);
            UpdateSolidBrush(resources, "RadioButtonOuterEllipseCheckedStroke", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "RadioButtonOuterEllipseCheckedPointerOverStroke", suite.SecondaryAccent);
            UpdateSolidBrush(resources, "RadioButtonOuterEllipseCheckedPressedStroke", suite.ActionButton);

            UpdateSolidBrush(resources, "NavigationViewSelectionIndicatorForeground", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "TextControlBorderBrushFocused", suite.PrimaryAccent);

            UpdateSolidBrush(resources, "SliderTrackValueFill", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "SliderTrackValueFillPointerOver", suite.SecondaryAccent);
            UpdateSolidBrush(resources, "SliderTrackValueFillPressed", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "SliderThumbBackgroundPointerOver", suite.PrimaryAccent);
            UpdateSolidBrush(resources, "SliderThumbBackgroundPressed", suite.SecondaryAccent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply dynamic accent color suite.");
        }
    }

    private static void UpdateSolidBrush(ResourceDictionary resources, string key, Windows.UI.Color color)
    {
        bool updatedAny = TryMutateBrushRecursive(resources, key, color);
        if (!updatedAny)
        {
            resources[key] = new SolidColorBrush(color);
        }
    }

    private static bool TryMutateBrushRecursive(ResourceDictionary dict, string key, Windows.UI.Color color)
    {
        bool mutated = false;

        if (dict.ContainsKey(key) && dict[key] is SolidColorBrush directBrush)
        {
            directBrush.Color = color;
            mutated = true;
        }

        foreach (var themeEntry in dict.ThemeDictionaries.Values)
        {
            if (themeEntry is ResourceDictionary themeDict &&
                themeDict.ContainsKey(key) &&
                themeDict[key] is SolidColorBrush themeBrush)
            {
                themeBrush.Color = color;
                mutated = true;
            }
        }

        foreach (var merged in dict.MergedDictionaries)
        {
            if (TryMutateBrushRecursive(merged, key, color))
            {
                mutated = true;
            }
        }

        return mutated;
    }
}
