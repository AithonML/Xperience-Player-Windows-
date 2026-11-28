using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Xperience.App.Services;
using Xperience.Core.Collections;

namespace Xperience.App.Helpers;

/// <summary>
/// Attached properties for bounded-resolution <see cref="BitmapImage"/> decoding on
/// <see cref="Image"/> and <see cref="ImageBrush"/> targets, backed by <see cref="ArtworkCacheManager"/>
/// so remote covers are cached on disk &amp; in memory and never re-downloaded across views.
/// </summary>
public static class BitmapDecodeHelper
{
    private const int DefaultDecodeWidth = 192;
    private static readonly BoundedLruCache<string, BitmapImage> DecodedBitmapCache =
        new(maxCapacity: 180, comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Identifies the <c>BitmapDecodeHelper.SourceUri</c> attached dependency property.
    /// </summary>
    public static readonly DependencyProperty SourceUriProperty =
        DependencyProperty.RegisterAttached(
            "SourceUri",
            typeof(string),
            typeof(BitmapDecodeHelper),
            new PropertyMetadata(null, OnDecodePropertyChanged));

    /// <summary>
    /// Identifies the <c>BitmapDecodeHelper.DecodeWidth</c> attached dependency property.
    /// Defaults to <c>0</c>, which falls back to <c>192</c> logical pixels.
    /// </summary>
    public static readonly DependencyProperty DecodeWidthProperty =
        DependencyProperty.RegisterAttached(
            "DecodeWidth",
            typeof(int),
            typeof(BitmapDecodeHelper),
            new PropertyMetadata(0, OnDecodePropertyChanged));

    /// <summary>
    /// Gets the source URI string configured on the specified <see cref="Image"/> or <see cref="ImageBrush"/>.
    /// </summary>
    public static string? GetSourceUri(DependencyObject obj) => (string?)obj.GetValue(SourceUriProperty);

    /// <summary>
    /// Sets the source URI string on the specified <see cref="Image"/> or <see cref="ImageBrush"/>.
    /// </summary>
    public static void SetSourceUri(DependencyObject obj, string? value) => obj.SetValue(SourceUriProperty, value);

    /// <summary>
    /// Gets the target logical decode width in DIPs configured on the specified element.
    /// </summary>
    public static int GetDecodeWidth(DependencyObject obj) => (int)obj.GetValue(DecodeWidthProperty);

    /// <summary>
    /// Sets the target logical decode width in DIPs on the specified element.
    /// </summary>
    public static void SetDecodeWidth(DependencyObject obj, int value) => obj.SetValue(DecodeWidthProperty, value);

    private static void OnDecodePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image && d is not ImageBrush)
        {
            return;
        }

        string? rawUri = GetSourceUri(d);
        int decodeWidth = GetDecodeWidth(d);
        int targetDecodeWidth = decodeWidth > 0 ? decodeWidth : DefaultDecodeWidth;

        if (!TryResolveImageUri(rawUri, out Uri? uri))
        {
            SetTargetSource(d, null);
            return;
        }

        bool isHttp = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase);

        if (isHttp)
        {
            if (ArtworkCacheManager.TryGetCachedFileUri(uri.AbsoluteUri, targetDecodeWidth, out string? cachedFileUri, out _) &&
                Uri.TryCreate(cachedFileUri, UriKind.Absolute, out Uri? localUri))
            {
                ApplyLocalOrAppBitmap(d, localUri, targetDecodeWidth);
                return;
            }

            string capturedRaw = rawUri!;
            string requestUrl = uri.AbsoluteUri;
            int cacheMinDim = requestUrl.Contains("mzstatic.com/image/thumb/", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(targetDecodeWidth, 544)
                : Math.Max(targetDecodeWidth, 544);

            _ = ResolveAndAssignRemoteBitmapAsync(d, capturedRaw, requestUrl, targetDecodeWidth, cacheMinDim);
            return;
        }

        ApplyLocalOrAppBitmap(d, uri, targetDecodeWidth);
    }

    private static void ApplyLocalOrAppBitmap(DependencyObject d, Uri uri, int targetDecodeWidth)
    {
        string cacheKey = $"{uri.AbsoluteUri}|{targetDecodeWidth}";
        ImageSource? currentSource = GetTargetSource(d);
        if (currentSource is BitmapImage existing &&
            existing.UriSource is not null &&
            existing.DecodePixelWidth == targetDecodeWidth &&
            existing.DecodePixelType == DecodePixelType.Logical &&
            string.Equals(existing.UriSource.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (!DecodedBitmapCache.TryGet(cacheKey, out var bitmap) || bitmap is null)
            {
                bitmap = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical,
                    DecodePixelWidth = targetDecodeWidth,
                    UriSource = uri
                };
                DecodedBitmapCache.Set(cacheKey, bitmap);
            }

            SetTargetSource(d, bitmap);
        }
        catch
        {
            SetTargetSource(d, null);
        }
    }

    private static async Task ResolveAndAssignRemoteBitmapAsync(
        DependencyObject target,
        string expectedRawUri,
        string requestUrl,
        int targetDecodeWidth,
        int cacheMinDim)
    {
        try
        {
            var outcome = await ArtworkCacheManager.GetOrDownloadAsync(requestUrl, cacheMinDim).ConfigureAwait(false);
            string effectiveUriStr = !string.IsNullOrWhiteSpace(outcome.LocalFileUri)
                ? outcome.LocalFileUri!
                : requestUrl;

            if (!Uri.TryCreate(effectiveUriStr, UriKind.Absolute, out Uri? resolvedUri))
            {
                return;
            }

            var dq = target.DispatcherQueue;
            if (dq is not null)
            {
                dq.TryEnqueue(() =>
                {
                    if (string.Equals(GetSourceUri(target), expectedRawUri, StringComparison.Ordinal))
                    {
                        ApplyLocalOrAppBitmap(target, resolvedUri, targetDecodeWidth);
                    }
                });
            }
        }
        catch
        {
        }
    }

    private static bool TryResolveImageUri(string? rawUri, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            return false;
        }

        string trimmed = rawUri.Trim();

        // Support app-relative asset paths such as "/Assets/..." or "Assets/..."
        if (trimmed.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "ms-appx:///" + trimmed.TrimStart('/');
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        if (!IsSupportedScheme(parsed.Scheme))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsSupportedScheme(string scheme) =>
        string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "ms-appx", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "ms-appdata", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "ms-resource", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase);

    private static ImageSource? GetTargetSource(DependencyObject target) => target switch
    {
        Image image => image.Source,
        ImageBrush brush => brush.ImageSource,
        _ => null
    };

    private static void SetTargetSource(DependencyObject target, ImageSource? source)
    {
        switch (target)
        {
            case Image image:
                if (!ReferenceEquals(image.Source, source))
                {
                    image.Source = source;
                }
                break;
            case ImageBrush brush:
                if (!ReferenceEquals(brush.ImageSource, source))
                {
                    brush.ImageSource = source;
                }
                break;
        }
    }
}
