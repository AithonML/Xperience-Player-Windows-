using System.Net;

namespace Xperience.Core.Services;

/// <summary>
/// <see cref="DelegatingHandler"/> that manually follows up to a bounded number of HTTPS-only redirects
/// and strips sensitive Google/YouTube authentication headers (<c>Cookie</c>, <c>Authorization</c>,
/// <c>X-Goog-PageId</c>, <c>X-Goog-AuthUser</c>, <c>X-Youtube-Bootstrap-Logged-In</c>) whenever
/// a redirect crosses host boundaries or targets a host outside the allowlist.
/// </summary>
public sealed class SafeRedirectDelegatingHandler : DelegatingHandler
{
    public const int DefaultMaxRedirects = 5;

    private static readonly HashSet<string> SensitiveAuthHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookie",
        "Authorization",
        "X-Goog-PageId",
        "X-Goog-AuthUser",
        "X-Youtube-Bootstrap-Logged-In"
    };

    private readonly Func<string?, bool> _isCredentialAllowedHost;
    private readonly int _maxRedirects;

    /// <summary>
    /// Initializes a new instance of <see cref="SafeRedirectDelegatingHandler"/>.
    /// The <paramref name="innerHandler"/> should be configured with <c>AllowAutoRedirect = false</c>.
    /// </summary>
    public SafeRedirectDelegatingHandler(
        HttpMessageHandler innerHandler,
        Func<string?, bool>? isCredentialAllowedHost = null,
        int maxRedirects = DefaultMaxRedirects)
        : base(innerHandler)
    {
        _isCredentialAllowedHost = isCredentialAllowedHost ?? YouTubeSecurityPolicy.IsAllowedYouTubeApiHost;
        _maxRedirects = Math.Clamp(maxRedirects, 1, 10);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        byte[]? bufferedBody = null;
        string? bufferedContentType = null;
        if (request.Content is not null)
        {
            bufferedBody = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            bufferedContentType = request.Content.Headers.ContentType?.ToString();
        }

        HttpRequestMessage currentRequest = request;
        bool ownsCurrentRequest = false;

        try
        {
            for (int redirectCount = 0; ; redirectCount++)
            {
                HttpResponseMessage response = await base.SendAsync(currentRequest, cancellationToken)
                    .ConfigureAwait(false);

                if (!IsRedirectStatusCode(response.StatusCode) || response.Headers.Location is null)
                {
                    return response;
                }

                if (redirectCount >= _maxRedirects)
                {
                    response.Dispose();
                    throw new HttpRequestException($"Exceeded maximum redirect limit ({_maxRedirects}).");
                }

                Uri? currentUri = currentRequest.RequestUri;
                Uri location = response.Headers.Location;
                Uri nextUri = location.IsAbsoluteUri
                    ? location
                    : (currentUri is not null ? new Uri(currentUri, location) : location);

                // Strictly block any non-HTTPS redirect (including HTTPS -> HTTP downgrade)
                if (!nextUri.IsAbsoluteUri ||
                    !string.Equals(nextUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    response.Dispose();
                    throw new HttpRequestException($"Blocked non-HTTPS redirect to scheme '{(nextUri.IsAbsoluteUri ? nextUri.Scheme : "relative")}'.");
                }

                bool hostChanged = currentUri is null ||
                                   !string.Equals(currentUri.Host, nextUri.Host, StringComparison.OrdinalIgnoreCase);
                bool stripCredentials = hostChanged || !_isCredentialAllowedHost(nextUri.Host);

                HttpMethod nextMethod = ResolveRedirectMethod(currentRequest.Method, response.StatusCode);
                bool preserveBody = nextMethod == currentRequest.Method &&
                                    response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

                var nextRequest = CloneRequest(
                    currentRequest,
                    nextUri,
                    nextMethod,
                    stripCredentials,
                    preserveBody ? bufferedBody : null,
                    preserveBody ? bufferedContentType : null);

                response.Dispose();

                if (ownsCurrentRequest)
                {
                    currentRequest.Dispose();
                }

                currentRequest = nextRequest;
                ownsCurrentRequest = true;
            }
        }
        finally
        {
            if (ownsCurrentRequest)
            {
                currentRequest.Dispose();
            }
        }
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static HttpMethod ResolveRedirectMethod(HttpMethod currentMethod, HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.SeeOther)
        {
            return currentMethod == HttpMethod.Head ? HttpMethod.Head : HttpMethod.Get;
        }

        if (statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found &&
            currentMethod == HttpMethod.Post)
        {
            return HttpMethod.Get;
        }

        return currentMethod;
    }

    private static HttpRequestMessage CloneRequest(
        HttpRequestMessage source,
        Uri targetUri,
        HttpMethod method,
        bool stripCredentials,
        byte[]? bodyBytes,
        string? contentType)
    {
        var clone = new HttpRequestMessage(method, targetUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };

        foreach (var header in source.Headers)
        {
            if (stripCredentials && SensitiveAuthHeaders.Contains(header.Key))
            {
                continue;
            }

            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (bodyBytes is not null)
        {
            var content = new ByteArrayContent(bodyBytes);
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            clone.Content = content;
        }

        return clone;
    }
}
