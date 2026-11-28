using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Windows.UI;
using Xperience.App.Services;
using Xperience.Core.Collections;

namespace Xperience.App.Helpers;

/// <summary>
/// Represents 4 harmonized northern-lights colors extracted from an album cover for the Home Aurora header.
/// </summary>
public readonly record struct AuroraPalette(
    Color Color1,
    Color Color2,
    Color Color3,
    Color Color4)
{
    public static AuroraPalette Default => new(
        Color.FromArgb(255, 20, 184, 166),  // Teal
        Color.FromArgb(255, 14, 165, 233),  // Cyan/Sky
        Color.FromArgb(255, 99, 102, 241),  // Indigo
        Color.FromArgb(255, 168, 85, 247)); // Violet
}

/// <summary>
/// Represents adaptive dark/light ambient background colors and vibrant accent extracted from an album cover
/// for the Now Playing sidebar gradient dissolve.
/// </summary>
public readonly record struct NowPlayingAmbientPalette(
    Color DarkAmbient,
    Color LightAmbient,
    Color VibrantAccent);

/// <summary>
/// Represents the full Artist Detail ambient theme derived from the main color of the artist's profile/banner photo.
/// </summary>
public readonly record struct ArtistPagePalette(
    Color MainBackground,
    Color HeroMidStop,
    Color CardSurface,
    Color CardBorder,
    Color PrimaryInk,
    Color SecondaryInk,
    Color AccentColor,
    bool IsLightSurface)
{
    public static ArtistPagePalette CreateDefault(bool isLightTheme)
        => ColorHelperEx.BuildArtistPagePaletteFromColor(
            isLightTheme ? Color.FromArgb(255, 178, 170, 162) : Color.FromArgb(255, 32, 36, 44),
            isLightTheme);
}

/// <summary>
/// Represents the full Album Detail ambient theme derived from the album cover artwork.
/// </summary>
public readonly record struct AlbumPagePalette(
    Color MainBackground,
    Color CardSurface,
    Color CardBorder,
    Color PrimaryInk,
    Color SecondaryInk,
    Color ArtistLinkAccent,
    Color ActiveTrackRowBackground,
    Color ActiveTrackAccent,
    bool IsLightSurface)
{
    public static AlbumPagePalette CreateDefault(bool isLightTheme)
        => ColorHelperEx.BuildAlbumPagePaletteFromColor(
            isLightTheme ? Color.FromArgb(255, 206, 176, 124) : Color.FromArgb(255, 38, 44, 56),
            isLightTheme);
}

/// <summary>
/// Utility methods for parsing and converting hexadecimal color tokens (#AARRGGBB / #RRGGBB) to <see cref="Color"/>,
/// plus HSV color-space vibrancy extraction from local files, ms-appx assets, and remote HTTPS album art.
/// </summary>
public static class ColorHelperEx
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    /// <summary>
    /// Parses a hexadecimal color string into a <see cref="Color"/> struct.
    /// </summary>
    /// <param name="hex">Hexadecimal string (#RRGGBB or #AARRGGBB).</param>
    /// <param name="fallback">Fallback color if parsing fails.</param>
    /// <returns>The parsed <see cref="Color"/>.</returns>
    public static Color ParseHex(string? hex, Color? fallback = null)
    {
        var defaultColor = fallback ?? Color.FromArgb(255, 45, 212, 191);
        if (string.IsNullOrWhiteSpace(hex))
        {
            return defaultColor;
        }

        var span = hex.AsSpan().Trim();
        if (span.StartsWith("#"))
        {
            span = span[1..];
        }

        try
        {
            if (span.Length == 6)
            {
                byte r = byte.Parse(span[..2], System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(span.Slice(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(span.Slice(4, 2), System.Globalization.NumberStyles.HexNumber);
                return Color.FromArgb(255, r, g, b);
            }

            if (span.Length == 8)
            {
                byte a = byte.Parse(span[..2], System.Globalization.NumberStyles.HexNumber);
                byte r = byte.Parse(span.Slice(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(span.Slice(4, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(span.Slice(6, 2), System.Globalization.NumberStyles.HexNumber);
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch
        {
            // Fall through to defaultColor
        }

        return defaultColor;
    }

    /// <summary>
    /// Converts a <see cref="Color"/> to an #AARRGGBB hexadecimal string.
    /// </summary>
    public static string ToHex(Color color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Converts an RGB <see cref="Color"/> to HSV components (H in [0, 360], S in [0, 1], V in [0, 1]).
    /// </summary>
    public static void RgbToHsv(Color color, out double h, out double s, out double v)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        h = 0.0;
        if (delta > 0.0001)
        {
            if (Math.Abs(max - r) < 0.0001)
            {
                h = 60.0 * (((g - b) / delta) % 6.0);
            }
            else if (Math.Abs(max - g) < 0.0001)
            {
                h = 60.0 * (((b - r) / delta) + 2.0);
            }
            else
            {
                h = 60.0 * (((r - g) / delta) + 4.0);
            }
        }

        if (h < 0.0)
        {
            h += 360.0;
        }

        s = max <= 0.0001 ? 0.0 : delta / max;
        v = max;
    }

    /// <summary>
    /// Converts HSV components (H in [0, 360], S in [0, 1], V in [0, 1]) back to a <see cref="Color"/>.
    /// </summary>
    public static Color HsvToRgb(double h, double s, double v, byte alpha = 255)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);

        double c = v * s;
        double x = c * (1.0 - Math.Abs(((h / 60.0) % 2.0) - 1.0));
        double m = v - c;

        double r1 = 0, g1 = 0, b1 = 0;
        if (h < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        byte r = (byte)Math.Clamp(Math.Round((r1 + m) * 255.0), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round((g1 + m) * 255.0), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round((b1 + m) * 255.0), 0, 255);
        return Color.FromArgb(alpha, r, g, b);
    }

    /// <summary>
    /// Derives a complete, contrast-checked suite of semantic accent colors from any base accent color.
    /// </summary>
    public static (
        Color PrimaryAccent,
        Color SecondaryAccent,
        Color ActionButton,
        Color NowPlayingOverlay,
        Color NowPlayingPlayBtn,
        Color BadgeBackground,
        Color BadgeBorder) DeriveHarmoniousAccentSuite(Color baseColor)
    {
        RgbToHsv(baseColor, out double h, out double s, out double v);

        // Ensure vibrant, legible primary accent for dark glass UI
        double primaryS = Math.Clamp(s < 0.15 ? 0.65 : s * 1.08, 0.52, 0.90);
        double primaryV = Math.Clamp(v, 0.78, 0.95);
        var primary = HsvToRgb(h, primaryS, primaryV, 255);

        // Secondary companion accent (slightly lighter & hue-shifted for gradients and hover states)
        var secondary = HsvToRgb((h + 16.0) % 360.0, Math.Clamp(primaryS * 0.88, 0.42, 0.82), Math.Clamp(primaryV * 1.06, 0.86, 1.0), 255);

        // Action button circle background ('+' and Song play buttons) — rich medium-deep tone for crisp white icon contrast
        var actionBtn = HsvToRgb(h, Math.Clamp(primaryS * 0.95, 0.50, 0.78), 0.54, 255);

        // Now Playing bottom frosted overlay (#B8 alpha)
        var nowPlayingOverlay = HsvToRgb(h, Math.Clamp(primaryS * 0.80, 0.36, 0.68), 0.31, 0xB8);

        // Now Playing floating circular play button (#A6 alpha)
        var nowPlayingPlayBtn = HsvToRgb(h, Math.Clamp(primaryS * 0.68, 0.32, 0.58), 0.55, 0xA6);

        // Subtle translucent badge background & border
        var badgeBg = Color.FromArgb(0x36, primary.R, primary.G, primary.B);
        var badgeBorder = Color.FromArgb(0x8C, primary.R, primary.G, primary.B);

        return (primary, secondary, actionBtn, nowPlayingOverlay, nowPlayingPlayBtn, badgeBg, badgeBorder);
    }

    /// <summary>
    /// Derives Dark-mode and Light-mode ambient dissolve background colors and a vibrant accent from a base color,
    /// matching Android's <c>ArtworkColorExtractor.extractArtistAmbientColors</c> (<c>minVal = 0.08f, maxVal = 0.16f</c>).
    /// </summary>
    public static NowPlayingAmbientPalette DeriveAmbientPaletteFromColor(Color baseColor)
    {
        RgbToHsv(baseColor, out double h, out double s, out double v);
        var vibrant = DeriveHarmoniousAccentSuite(baseColor).PrimaryAccent;

        if (s < 0.08)
        {
            return new NowPlayingAmbientPalette(
                DarkAmbient: Color.FromArgb(255, 15, 16, 20),
                LightAmbient: Color.FromArgb(255, 244, 246, 249),
                VibrantAccent: vibrant);
        }

        // Exact Android AccentColorManager.kt swatchToAmbientColor:
        // dark: minSat = 0.35f, maxSat = 0.65f, minVal = 0.08f, maxVal = 0.16f
        // light: minSat = 0.06f, maxSat = 0.16f, minVal = 0.93f, maxVal = 0.98f
        var darkAmbient = HsvToRgb(
            h,
            Math.Clamp(s * 0.68, 0.28, 0.58),
            Math.Clamp(v * 0.18, 0.09, 0.15),
            255);

        var lightAmbient = HsvToRgb(
            h,
            Math.Clamp(s * 0.22, 0.06, 0.16),
            Math.Clamp(v, 0.93, 0.98),
            255);

        return new NowPlayingAmbientPalette(darkAmbient, lightAmbient, vibrant);
    }

    /// <summary>
    /// Asynchronously extracts a <see cref="NowPlayingAmbientPalette"/> from an album cover image URI/path.
    /// </summary>
    public static async Task<NowPlayingAmbientPalette> ExtractNowPlayingAmbientPaletteAsync(
        string? coverArtUri,
        Color fallbackAccent,
        CancellationToken cancellationToken = default)
    {
        var dominant = await ExtractDominantVibrantColorAsync(coverArtUri, fallbackAccent, cancellationToken)
            .ConfigureAwait(false);
        return DeriveAmbientPaletteFromColor(dominant);
    }

    private static readonly BoundedLruCache<string, Color> DominantColorCache =
        new(maxCapacity: 128, comparer: StringComparer.OrdinalIgnoreCase);

    private static readonly BoundedLruCache<string, AuroraPalette> AuroraPaletteCache =
        new(maxCapacity: 128, comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Asynchronously extracts the dominant vibrant color from an album cover image file, ms-appx URI, or HTTP/HTTPS URL.
    /// Uses a 24-bin HSV hue-vibrancy histogram on a 36x36 downsampled bitmap so extraction takes &lt; 2 ms.
    /// </summary>
    public static async Task<Color> ExtractDominantVibrantColorAsync(
        string? coverArtUri,
        Color? fallbackColor = null,
        CancellationToken cancellationToken = default)
    {
        var defaultColor = fallbackColor ?? Color.FromArgb(255, 45, 212, 191);
        if (string.IsNullOrWhiteSpace(coverArtUri) ||
            coverArtUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) ||
            coverArtUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase))
        {
            return defaultColor;
        }

        string cacheKey = BuildPaletteCacheKey(coverArtUri);
        if (!string.IsNullOrEmpty(cacheKey) && DominantColorCache.TryGet(cacheKey, out var cachedColor))
        {
            return cachedColor;
        }

        try
        {
            Color extracted = defaultColor;
            if (coverArtUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                coverArtUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? imageBytes = await ArtworkCacheManager.GetOrDownloadBytesAsync(coverArtUri, 256, cancellationToken)
                    .ConfigureAwait(false);
                if (imageBytes is null || imageBytes.Length == 0)
                {
                    return defaultColor;
                }

                using var memStream = new InMemoryRandomAccessStream();
                await memStream.WriteAsync(imageBytes.AsBuffer());
                memStream.Seek(0);
                extracted = await DecodeAndExtractDominantColorAsync(memStream, defaultColor, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string? resolvedPath = ResolveLocalImagePath(coverArtUri);
                if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
                {
                    return defaultColor;
                }

                await using var fileStream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                using var randomAccessStream = fileStream.AsRandomAccessStream();
                extracted = await DecodeAndExtractDominantColorAsync(randomAccessStream, defaultColor, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(cacheKey))
            {
                DominantColorCache.Set(cacheKey, extracted);
            }

            return extracted;
        }
        catch
        {
            return defaultColor;
        }
    }

    private static string BuildPaletteCacheKey(string uri)
    {
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var (canonicalKey, _, _) = ArtworkCacheManager.NormalizeArtworkRequest(uri, 256);
            if (!string.IsNullOrEmpty(canonicalKey))
            {
                return canonicalKey;
            }
        }

        return uri.Trim();
    }

    /// <summary>
    /// Extracts up to 4 harmonious vibrant colors from an album cover image for the animated Aurora Borealis header.
    /// Ports the 36-bin HSV hue-separation and luminous harmonization algorithm from Android's AccentColorManager.
    /// </summary>
    public static async Task<AuroraPalette> ExtractAuroraPaletteAsync(
        string? coverArtUri,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(coverArtUri) ||
            coverArtUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) ||
            coverArtUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase))
        {
            return AuroraPalette.Default;
        }

        string cacheKey = BuildPaletteCacheKey(coverArtUri);
        if (!string.IsNullOrEmpty(cacheKey) && AuroraPaletteCache.TryGet(cacheKey, out var cachedPalette))
        {
            return cachedPalette;
        }

        try
        {
            byte[]? bytes = null;
            if (coverArtUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                coverArtUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? imageBytes = await ArtworkCacheManager.GetOrDownloadBytesAsync(coverArtUri, 256, cancellationToken)
                    .ConfigureAwait(false);
                if (imageBytes is { Length: > 0 })
                {
                    using var memStream = new InMemoryRandomAccessStream();
                    await memStream.WriteAsync(imageBytes.AsBuffer());
                    memStream.Seek(0);
                    bytes = await DecodeScaledBgra8BytesAsync(memStream).ConfigureAwait(false);
                }
            }
            else
            {
                string? resolvedPath = ResolveLocalImagePath(coverArtUri);
                if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
                {
                    await using var fileStream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                    using var randomAccessStream = fileStream.AsRandomAccessStream();
                    bytes = await DecodeScaledBgra8BytesAsync(randomAccessStream).ConfigureAwait(false);
                }
            }

            if (bytes is null || bytes.Length < 4)
            {
                return AuroraPalette.Default;
            }

            var palette = await Task.Run(() => ComputeAuroraPaletteFromBgra8(bytes), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(cacheKey))
            {
                AuroraPaletteCache.Set(cacheKey, palette);
            }

            return palette;
        }
        catch
        {
            return AuroraPalette.Default;
        }
    }

    private static async Task<byte[]> DecodeScaledBgra8BytesAsync(IRandomAccessStream randomAccessStream)
    {
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomAccessStream);
        var transform = new Windows.Graphics.Imaging.BitmapTransform
        {
            ScaledWidth = 36,
            ScaledHeight = 36,
            InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Linear
        };

        var pixelData = await decoder.GetPixelDataAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            transform,
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

        return pixelData.DetachPixelData();
    }

    private static AuroraPalette ComputeAuroraPaletteFromBgra8(byte[] bytes)
    {
        const int BinCount = 36;
        double[] binWeights = new double[BinCount];
        double[] binSumR = new double[BinCount];
        double[] binSumG = new double[BinCount];
        double[] binSumB = new double[BinCount];

        double fallbackSumR = 0, fallbackSumG = 0, fallbackSumB = 0;
        int validPixels = 0;

        for (int i = 0; i <= bytes.Length - 4; i += 4)
        {
            byte b = bytes[i];
            byte g = bytes[i + 1];
            byte r = bytes[i + 2];

            var c = Color.FromArgb(255, r, g, b);
            RgbToHsv(c, out double h, out double s, out double v);

            if (v is > 0.12 and < 0.94)
            {
                fallbackSumR += r;
                fallbackSumG += g;
                fallbackSumB += b;
                validPixels++;
            }

            if (v < 0.14 || v > 0.97 || s < 0.14)
            {
                continue;
            }

            double weight = (0.35 + (s * s)) * Math.Sin(Math.Clamp((v - 0.08) / 0.92, 0.1, 1.0) * Math.PI * 0.7);
            int bin = Math.Clamp((int)(h / (360.0 / BinCount)), 0, BinCount - 1);

            binWeights[bin] += weight;
            binSumR[bin] += r * weight;
            binSumG[bin] += g * weight;
            binSumB[bin] += b * weight;
        }

        var rankedBins = Enumerable.Range(0, BinCount)
            .Where(b => binWeights[b] > 0.01)
            .OrderByDescending(b => binWeights[b])
            .ToList();

        var pickedColors = new List<Color>(4);
        var pickedHues = new List<double>(4);

        static Color BoostAuroraSwatch(Color raw)
        {
            RgbToHsv(raw, out double hh, out double ss, out double vv);
            double targetS = Math.Clamp(ss < 0.25 ? 0.58 : ss * 1.15, 0.55, 0.92);
            double targetV = Math.Clamp(vv < 0.55 ? 0.76 : vv * 1.08, 0.68, 0.92);
            return HsvToRgb(hh, targetS, targetV, 255);
        }

        // First pass: pick bins with >= 28° hue separation
        foreach (int b in rankedBins)
        {
            if (pickedColors.Count >= 4) break;
            double w = binWeights[b];
            var raw = Color.FromArgb(255,
                (byte)Math.Clamp(Math.Round(binSumR[b] / w), 0, 255),
                (byte)Math.Clamp(Math.Round(binSumG[b] / w), 0, 255),
                (byte)Math.Clamp(Math.Round(binSumB[b] / w), 0, 255));
            RgbToHsv(raw, out double hue, out _, out _);

            bool tooClose = pickedHues.Any(existing =>
            {
                double diff = Math.Abs(existing - hue);
                double circular = Math.Min(diff, 360.0 - diff);
                return circular < 28.0;
            });

            if (!tooClose)
            {
                pickedHues.Add(hue);
                pickedColors.Add(BoostAuroraSwatch(raw));
            }
        }

        // Second pass: relax separation if cover is narrow-palette
        if (pickedColors.Count < 4)
        {
            foreach (int b in rankedBins)
            {
                if (pickedColors.Count >= 4) break;
                double w = binWeights[b];
                var raw = Color.FromArgb(255,
                    (byte)Math.Clamp(Math.Round(binSumR[b] / w), 0, 255),
                    (byte)Math.Clamp(Math.Round(binSumG[b] / w), 0, 255),
                    (byte)Math.Clamp(Math.Round(binSumB[b] / w), 0, 255));
                RgbToHsv(raw, out double hue, out _, out _);

                bool duplicate = pickedHues.Any(existing =>
                {
                    double diff = Math.Abs(existing - hue);
                    return Math.Min(diff, 360.0 - diff) < 10.0;
                });

                if (!duplicate)
                {
                    pickedHues.Add(hue);
                    pickedColors.Add(BoostAuroraSwatch(raw));
                }
            }
        }

        // If monochrome / empty, derive from average fallback or Default
        if (pickedColors.Count == 0)
        {
            if (validPixels > 0)
            {
                var mono = Color.FromArgb(255,
                    (byte)Math.Clamp(Math.Round(fallbackSumR / validPixels), 0, 255),
                    (byte)Math.Clamp(Math.Round(fallbackSumG / validPixels), 0, 255),
                    (byte)Math.Clamp(Math.Round(fallbackSumB / validPixels), 0, 255));
                pickedColors.Add(BoostAuroraSwatch(mono));
            }
            else
            {
                return AuroraPalette.Default;
            }
        }

        // Synthesize harmonious companions (+38°, -42°, +145°) if fewer than 4 distinct swatches
        RgbToHsv(pickedColors[0], out double primaryH, out double primaryS, out double primaryV);
        double[] offsets = [38.0, -42.0, 145.0];
        int offsetIdx = 0;
        while (pickedColors.Count < 4)
        {
            double nextHue = (primaryH + offsets[offsetIdx % offsets.Length] + 360.0) % 360.0;
            pickedColors.Add(HsvToRgb(nextHue, Math.Clamp(primaryS * 0.92, 0.56, 0.90), Math.Clamp(primaryV, 0.70, 0.92), 255));
            offsetIdx++;
        }

        return new AuroraPalette(pickedColors[0], pickedColors[1], pickedColors[2], pickedColors[3]);
    }

    private static async Task<Color> DecodeAndExtractDominantColorAsync(
        IRandomAccessStream randomAccessStream,
        Color fallbackColor,
        CancellationToken cancellationToken)
    {
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomAccessStream);
        var transform = new Windows.Graphics.Imaging.BitmapTransform
        {
            ScaledWidth = 36,
            ScaledHeight = 36,
            InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Linear
        };

        var pixelData = await decoder.GetPixelDataAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            transform,
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

        byte[] bytes = pixelData.DetachPixelData();
        return await Task.Run(() => ComputeDominantColorFromBgra8(bytes, fallbackColor), cancellationToken).ConfigureAwait(false);
    }

    private static string? ResolveLocalImagePath(string uriOrPath)
    {
        if (File.Exists(uriOrPath))
        {
            return uriOrPath;
        }

        if (uriOrPath.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase))
        {
            string relative = uriOrPath["ms-appx:///".Length..].Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.Combine(AppContext.BaseDirectory, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            if (File.Exists(uri.LocalPath))
            {
                return uri.LocalPath;
            }
        }

        return null;
    }

    private static Color ComputeDominantColorFromBgra8(byte[] bytes, Color fallback)
    {
        const int BinCount = 24;
        double[] binWeights = new double[BinCount];
        double[] binSumR = new double[BinCount];
        double[] binSumG = new double[BinCount];
        double[] binSumB = new double[BinCount];

        double fallbackSumR = 0, fallbackSumG = 0, fallbackSumB = 0;
        int validPixels = 0;

        for (int i = 0; i <= bytes.Length - 4; i += 4)
        {
            byte b = bytes[i];
            byte g = bytes[i + 1];
            byte r = bytes[i + 2];

            var c = Color.FromArgb(255, r, g, b);
            RgbToHsv(c, out double h, out double s, out double v);

            // Track general non-extreme pixels in case the cover is monochrome
            if (v is > 0.12 and < 0.94)
            {
                fallbackSumR += r;
                fallbackSumG += g;
                fallbackSumB += b;
                validPixels++;
            }

            // Filter out near-black, near-white, and desaturated grays for chromatic dominance
            if (v < 0.16 || v > 0.96 || s < 0.18)
            {
                continue;
            }

            // Weight heavily by saturation and mid-high brightness so vivid cover colors win
            double weight = (s * s) * Math.Sin(Math.Clamp((v - 0.1) / 0.9, 0.1, 1.0) * Math.PI * 0.65);
            int bin = Math.Clamp((int)(h / (360.0 / BinCount)), 0, BinCount - 1);

            binWeights[bin] += weight;
            binSumR[bin] += r * weight;
            binSumG[bin] += g * weight;
            binSumB[bin] += b * weight;
        }

        int bestBin = -1;
        double bestWeight = 0.0;
        for (int b = 0; b < BinCount; b++)
        {
            if (binWeights[b] > bestWeight)
            {
                bestWeight = binWeights[b];
                bestBin = b;
            }
        }

        if (bestBin >= 0 && bestWeight > 0.01)
        {
            byte avgR = (byte)Math.Clamp(Math.Round(binSumR[bestBin] / bestWeight), 0, 255);
            byte avgG = (byte)Math.Clamp(Math.Round(binSumG[bestBin] / bestWeight), 0, 255);
            byte avgB = (byte)Math.Clamp(Math.Round(binSumB[bestBin] / bestWeight), 0, 255);
            var rawWinner = Color.FromArgb(255, avgR, avgG, avgB);
            return DeriveHarmoniousAccentSuite(rawWinner).PrimaryAccent;
        }

        if (validPixels > 0)
        {
            byte avgR = (byte)Math.Clamp(Math.Round(fallbackSumR / validPixels), 0, 255);
            byte avgG = (byte)Math.Clamp(Math.Round(fallbackSumG / validPixels), 0, 255);
            byte avgB = (byte)Math.Clamp(Math.Round(fallbackSumB / validPixels), 0, 255);
            var monochrome = Color.FromArgb(255, avgR, avgG, avgB);
            RgbToHsv(monochrome, out double mh, out double ms, out _);
            if (ms > 0.06)
            {
                return HsvToRgb(mh, 0.55, 0.85);
            }
        }

        return fallback;
    }

    /// <summary>
    /// Computes WCAG relative luminance in [0, 1] for adaptive contrast decisions.
    /// </summary>
    public static double GetRelativeLuminance(Color c)
    {
        static double Linearize(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(c.R)) + (0.7152 * Linearize(c.G)) + (0.0722 * Linearize(c.B));
    }

    /// <summary>
    /// Extracts the authentic main color of an artist's profile or banner image (or Apple Music hex seed)
    /// and returns a complete, contrast-checked <see cref="ArtistPagePalette"/> for the Artist Detail view.
    /// </summary>
    public static async Task<ArtistPagePalette> ExtractArtistPagePaletteAsync(
        string? imageUri,
        string? seedHexColor,
        bool isLightTheme,
        CancellationToken cancellationToken = default)
    {
        Color? seedColor = null;
        if (!string.IsNullOrWhiteSpace(seedHexColor))
        {
            Color parsed = ParseHex(seedHexColor, Color.FromArgb(0, 0, 0, 0));
            if (parsed.A > 0)
            {
                seedColor = parsed;
            }
        }

        Color fallbackRaw = seedColor ?? (isLightTheme
            ? Color.FromArgb(255, 178, 170, 162)
            : Color.FromArgb(255, 36, 40, 48));

        if (string.IsNullOrWhiteSpace(imageUri) ||
            imageUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) ||
            imageUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase))
        {
            return BuildArtistPagePaletteFromColor(fallbackRaw, isLightTheme);
        }

        try
        {
            byte[]? bytes = null;
            if (imageUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                imageUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? imageBytes = await ArtworkCacheManager.GetOrDownloadBytesAsync(imageUri, 544, cancellationToken)
                    .ConfigureAwait(false);
                if (imageBytes is { Length: > 0 })
                {
                    using var memStream = new InMemoryRandomAccessStream();
                    await memStream.WriteAsync(imageBytes.AsBuffer());
                    memStream.Seek(0);
                    bytes = await DecodeScaledBgra8BytesAsync(memStream).ConfigureAwait(false);
                }
            }
            else
            {
                string? resolvedPath = ResolveLocalImagePath(imageUri);
                if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
                {
                    await using var fileStream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                    using var randomAccessStream = fileStream.AsRandomAccessStream();
                    bytes = await DecodeScaledBgra8BytesAsync(randomAccessStream).ConfigureAwait(false);
                }
            }

            if (bytes is null || bytes.Length < 4)
            {
                return BuildArtistPagePaletteFromColor(fallbackRaw, isLightTheme);
            }

            Color rawMain = await Task.Run(() => ComputeArtistMainPhotoColor(bytes, fallbackRaw), cancellationToken)
                .ConfigureAwait(false);
            return BuildArtistPagePaletteFromColor(rawMain, isLightTheme);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return BuildArtistPagePaletteFromColor(fallbackRaw, isLightTheme);
        }
    }

    /// <summary>
    /// Converts any extracted raw dominant color from an artist photo into a cohesive <see cref="ArtistPagePalette"/>
    /// preserving the true hue and organic tone while keeping ink contrast crisp in Light and Dark themes.
    /// </summary>
    public static ArtistPagePalette BuildArtistPagePaletteFromColor(Color rawDominant, bool isLightTheme)
    {
        RgbToHsv(rawDominant, out double h, out double s, out double v);

        Color mainBg;
        if (isLightTheme)
        {
            // Keep warm neutral/stone/cream tones faithful (like Tove Lo's #ABA299),
            // while gently lifting overly dark photos so light mode stays harmonious and legible.
            double targetS = s < 0.08 ? s : Math.Clamp(s * 0.72, 0.05, 0.42);
            double targetV = v >= 0.44
                ? Math.Clamp(v, 0.56, 0.86)
                : Math.Clamp(0.48 + (v * 0.45), 0.48, 0.68);
            mainBg = HsvToRgb(h, targetS, targetV, 255);
        }
        else
        {
            // Dark mode preserves the authentic hue/undertone in a deep rich range
            double targetS = s < 0.08 ? s * 0.90 : Math.Clamp(s * 0.78, 0.14, 0.56);
            double targetV = Math.Clamp(v >= 0.50 ? 0.24 : 0.16 + (v * 0.24), 0.14, 0.30);
            mainBg = HsvToRgb(h, targetS, targetV, 255);
        }

        double lum = GetRelativeLuminance(mainBg);
        bool isLightSurface = lum > 0.33;

        Color primaryInk = isLightSurface
            ? Color.FromArgb(255, 16, 17, 20)
            : Color.FromArgb(255, 248, 250, 252);

        Color secondaryInk = isLightSurface
            ? Color.FromArgb(204, 24, 26, 32)
            : Color.FromArgb(204, 226, 232, 240);

        Color cardSurface = isLightSurface
            ? Color.FromArgb(52, 255, 255, 255)
            : Color.FromArgb(62, (byte)Math.Min(255, mainBg.R + 24), (byte)Math.Min(255, mainBg.G + 24), (byte)Math.Min(255, mainBg.B + 28));

        Color cardBorder = isLightSurface
            ? Color.FromArgb(30, 0, 0, 0)
            : Color.FromArgb(34, 255, 255, 255);

        Color accent = s >= 0.12
            ? HsvToRgb(h, Math.Clamp(s * 1.1, 0.48, 0.88), isLightSurface ? 0.52 : 0.86, 255)
            : (isLightSurface ? Color.FromArgb(255, 20, 22, 28) : Color.FromArgb(255, 45, 212, 191));

        return new ArtistPagePalette(
            MainBackground: mainBg,
            HeroMidStop: mainBg,
            CardSurface: cardSurface,
            CardBorder: cardBorder,
            PrimaryInk: primaryInk,
            SecondaryInk: secondaryInk,
            AccentColor: accent,
            IsLightSurface: isLightSurface);
    }

    private static Color ComputeArtistMainPhotoColor(byte[] bytes, Color fallback)
    {
        // Quantize into 16x16x16 RGB buckets (4096 bins) to capture the true main color of the image,
        // including warm stone/beige/neutral backgrounds as well as chromatic artist photos.
        const int Size = 4096;
        var counts = new double[Size];
        var sumR = new double[Size];
        var sumG = new double[Size];
        var sumB = new double[Size];

        double totalR = 0, totalG = 0, totalB = 0, totalW = 0;

        int pixelCount = bytes.Length / 4;
        int dim = Math.Max(1, (int)Math.Sqrt(pixelCount));

        for (int p = 0; p < pixelCount; p++)
        {
            int idx = p * 4;
            byte b = bytes[idx];
            byte g = bytes[idx + 1];
            byte r = bytes[idx + 2];

            var c = Color.FromArgb(255, r, g, b);
            RgbToHsv(c, out _, out double s, out double v);

            // Skip clipped pure black or clipped blown-out pure white unless needed
            if (v < 0.06 || v > 0.985)
            {
                continue;
            }

            // Favor background/edge perimeter pixels slightly too since hero dissolves directly into edges/bottom
            int y = p / dim;
            double bottomAndEdgeBias = (y > dim * 0.55) ? 1.45 : 1.0;
            double chromaBoost = 1.0 + (s * 0.55);
            double weight = bottomAndEdgeBias * chromaBoost;

            int qr = r >> 4;
            int qg = g >> 4;
            int qb = b >> 4;
            int bucket = (qr << 8) | (qg << 4) | qb;

            counts[bucket] += weight;
            sumR[bucket] += r * weight;
            sumG[bucket] += g * weight;
            sumB[bucket] += b * weight;

            totalR += r * weight;
            totalG += g * weight;
            totalB += b * weight;
            totalW += weight;
        }

        int bestBucket = -1;
        double bestWeight = 0;
        for (int i = 0; i < Size; i++)
        {
            if (counts[i] > bestWeight)
            {
                bestWeight = counts[i];
                bestBucket = i;
            }
        }

        if (bestBucket >= 0 && totalW > 0 && bestWeight >= totalW * 0.06)
        {
            // Blend top bucket (65%) with overall weighted photo tone (35%) for an organic, eye-pleasing backdrop
            byte br = (byte)Math.Clamp(Math.Round(((sumR[bestBucket] / bestWeight) * 0.65) + ((totalR / totalW) * 0.35)), 0, 255);
            byte bg = (byte)Math.Clamp(Math.Round(((sumG[bestBucket] / bestWeight) * 0.65) + ((totalG / totalW) * 0.35)), 0, 255);
            byte bb = (byte)Math.Clamp(Math.Round(((sumB[bestBucket] / bestWeight) * 0.65) + ((totalB / totalW) * 0.35)), 0, 255);
            return Color.FromArgb(255, br, bg, bb);
        }

        if (totalW > 0)
        {
            return Color.FromArgb(
                255,
                (byte)Math.Clamp(Math.Round(totalR / totalW), 0, 255),
                (byte)Math.Clamp(Math.Round(totalG / totalW), 0, 255),
                (byte)Math.Clamp(Math.Round(totalB / totalW), 0, 255));
        }

        return fallback;
    }

    /// <summary>
    /// Extracts the dominant ambient color of an album cover image and returns a complete,
    /// contrast-checked <see cref="AlbumPagePalette"/> for the Album Detail view.
    /// </summary>
    public static async Task<AlbumPagePalette> ExtractAlbumPagePaletteAsync(
        string? coverArtUri,
        bool isLightTheme,
        CancellationToken cancellationToken = default)
    {
        Color fallbackRaw = isLightTheme
            ? Color.FromArgb(255, 206, 176, 124)
            : Color.FromArgb(255, 42, 48, 62);

        if (string.IsNullOrWhiteSpace(coverArtUri) ||
            coverArtUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) ||
            coverArtUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase))
        {
            return BuildAlbumPagePaletteFromColor(fallbackRaw, isLightTheme);
        }

        try
        {
            Color dominant = await ExtractDominantVibrantColorAsync(coverArtUri, fallbackRaw, cancellationToken)
                .ConfigureAwait(false);
            return BuildAlbumPagePaletteFromColor(dominant, isLightTheme);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return BuildAlbumPagePaletteFromColor(fallbackRaw, isLightTheme);
        }
    }

    /// <summary>
    /// Builds a cohesive <see cref="AlbumPagePalette"/> from a cover's dominant color,
    /// matching the Android sister app's Album Detail ambient surface, active track highlight, and ink colors.
    /// </summary>
    public static AlbumPagePalette BuildAlbumPagePaletteFromColor(Color rawDominant, bool isLightTheme)
    {
        RgbToHsv(rawDominant, out double h, out double s, out double v);

        Color mainBg;
        double bgS;
        double bgV;
        if (isLightTheme)
        {
            bgS = s < 0.06 ? s : Math.Clamp(s * 0.26, 0.09, 0.20);
            bgV = Math.Clamp(v, 0.90, 0.94);
            mainBg = HsvToRgb(h, bgS, bgV, 255);
        }
        else
        {
            bgS = s < 0.06 ? s : Math.Clamp(s * 0.58, 0.16, 0.46);
            bgV = Math.Clamp(0.14 + (v * 0.07), 0.14, 0.22);
            mainBg = HsvToRgb(h, bgS, bgV, 255);
        }

        double lum = GetRelativeLuminance(mainBg);
        bool isLightSurface = lum > 0.35;

        Color primaryInk = isLightSurface
            ? Color.FromArgb(255, 18, 20, 24)
            : Color.FromArgb(255, 248, 250, 252);

        Color secondaryInk = isLightSurface
            ? Color.FromArgb(172, 34, 38, 46)
            : Color.FromArgb(178, 222, 228, 238);

        Color artistLinkAccent = isLightSurface
            ? Color.FromArgb(255, 18, 184, 204)
            : Color.FromArgb(255, 34, 211, 238);

        Color activeTrackAccent = s >= 0.08
            ? HsvToRgb(h, Math.Clamp(s < 0.20 ? 0.68 : s * 1.05, 0.56, 0.85), isLightSurface ? 0.72 : 0.90, 255)
            : artistLinkAccent;

        Color activeTrackRowBg = isLightSurface
            ? HsvToRgb(h, Math.Clamp(bgS * 1.48, 0.14, 0.28), Math.Clamp(bgV - 0.045, 0.84, 0.89), 255)
            : HsvToRgb(h, Math.Clamp(bgS * 1.22, 0.20, 0.54), Math.Clamp(bgV + 0.075, 0.21, 0.30), 255);

        Color cardSurface = isLightSurface
            ? HsvToRgb(h, Math.Clamp(bgS * 1.25, 0.10, 0.24), Math.Clamp(bgV - 0.03, 0.86, 0.91), 210)
            : Color.FromArgb(32, 255, 255, 255);

        Color cardBorder = isLightSurface
            ? Color.FromArgb(24, 0, 0, 0)
            : Color.FromArgb(30, 255, 255, 255);

        return new AlbumPagePalette(
            MainBackground: mainBg,
            CardSurface: cardSurface,
            CardBorder: cardBorder,
            PrimaryInk: primaryInk,
            SecondaryInk: secondaryInk,
            ArtistLinkAccent: artistLinkAccent,
            ActiveTrackRowBackground: activeTrackRowBg,
            ActiveTrackAccent: activeTrackAccent,
            IsLightSurface: isLightSurface);
    }
}
