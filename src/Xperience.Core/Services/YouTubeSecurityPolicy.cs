using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Xperience.Core.Services;

/// <summary>
/// Pure security policy helpers for YouTube Music authentication, stream URL validation,
/// CDN cookie sanitization, public HTTPS endpoint verification, and safe visualizer file naming.
/// </summary>
public static partial class YouTubeSecurityPolicy
{
    private static readonly HashSet<string> AllowedGoogleAuthExactHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts.google.com",
        "accounts.youtube.com",
        "music.youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "myaccount.google.com",
        "consent.google.com",
        "consent.youtube.com",
        "gds.google.com"
    };

    private static readonly string[] AllowedGoogleAuthSuffixes =
    [
        ".google.com",
        ".youtube.com",
        ".gstatic.com",
        ".googleusercontent.com",
        ".googleapis.com"
    ];

    private static readonly HashSet<string> AllowedYouTubeApiHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "music.youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "youtube.com",
        "youtubei.googleapis.com"
    };

    private static readonly HashSet<string> GoogleAuthCookieNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SID",
        "HSID",
        "SSID",
        "APISID",
        "SAPISID",
        "__Secure-1PSID",
        "__Secure-3PSID",
        "__Secure-1PAPISID",
        "__Secure-3PAPISID",
        "__Secure-1PSIDTS",
        "__Secure-3PSIDTS",
        "__Secure-1PSIDCC",
        "__Secure-3PSIDCC",
        "SIDCC",
        "LOGIN_INFO",
        "OSID",
        "__Secure-OSID",
        "LSID"
    };

    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVisualizerIdRegex();

    /// <summary>
    /// Validates that <paramref name="url"/> is an HTTPS URL targeting an allowlisted Google / YouTube
    /// authentication or static asset host, preventing interactive WebView2 login navigation to untrusted origins.
    /// </summary>
    public static bool IsAllowedGoogleAuthUrl(string? url)
    {
        if (!TryParseCleanHttpsUri(url, out var uri))
        {
            return false;
        }

        string host = uri.IdnHost;
        if (string.IsNullOrWhiteSpace(host) || host.EndsWith('.'))
        {
            return false;
        }

        if (AllowedGoogleAuthExactHosts.Contains(host))
        {
            return true;
        }

        foreach (string suffix in AllowedGoogleAuthSuffixes)
        {
            if (host.Length > suffix.Length &&
                host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates whether <paramref name="host"/> is an allowlisted YouTube / YouTube Music API host
    /// permitted to receive Google account authentication cookies and SAPISIDHASH headers.
    /// </summary>
    public static bool IsAllowedYouTubeApiHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string cleanHost = host.Trim();
        return !cleanHost.EndsWith('.') && AllowedYouTubeApiHosts.Contains(cleanHost);
    }

    /// <summary>
    /// Validates whether <paramref name="host"/> is an allowlisted YouTube media CDN or YouTube host
    /// (<c>*.googlevideo.com</c>, <c>*.youtube.com</c>, or <c>youtube.com</c>).
    /// </summary>
    public static bool IsAllowedYouTubeStreamHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string cleanHost = host.Trim();
        if (cleanHost.EndsWith('.'))
        {
            return false;
        }

        if (string.Equals(cleanHost, "youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string googleVideoSuffix = ".googlevideo.com";
        if (cleanHost.Length > googleVideoSuffix.Length &&
            cleanHost.EndsWith(googleVideoSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string youtubeSuffix = ".youtube.com";
        if (cleanHost.Length > youtubeSuffix.Length &&
            cleanHost.EndsWith(youtubeSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates that <paramref name="url"/> is an HTTPS URL targeting an allowlisted YouTube media stream host.
    /// </summary>
    public static bool IsSafeHttpsYouTubeStreamUrl(string? url)
    {
        return TryParseCleanHttpsUri(url, out var uri) &&
               IsAllowedYouTubeStreamHost(uri.IdnHost);
    }

    /// <summary>
    /// Validates that <paramref name="url"/> is a public <c>https://</c> endpoint, rejecting localhost,
    /// loopback, link-local, RFC1918 private IPv4, IPv6 ULA/link-local, and single-label internal hostnames.
    /// </summary>
    public static bool IsSafePublicHttpsUrl(string? url)
    {
        if (!TryParseCleanHttpsUri(url, out var uri))
        {
            return false;
        }

        string host = uri.IdnHost.Trim();
        if (string.IsNullOrWhiteSpace(host) || host.EndsWith('.'))
        {
            return false;
        }

        // Unwrap IPv6 literal brackets if present
        string ipCandidate = host.Length >= 2 && host[0] == '[' && host[^1] == ']'
            ? host[1..^1]
            : host;

        if (IPAddress.TryParse(ipCandidate, out var ip))
        {
            return IsPublicIpAddress(ip);
        }

        // Reject localhost and common local-only DNS suffixes or single-label intranet hostnames
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase) ||
            !host.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Sanitizes a custom visualizer identifier so it strictly matches <c>^[A-Za-z0-9_-]{1,64}$</c>,
    /// rejects Windows reserved device names (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM1..9</c>, <c>LPT1..9</c>),
    /// and falls back to a fresh GUID-based identifier when invalid.
    /// </summary>
    public static string SanitizeVisualizerFileId(string? rawId)
    {
        if (!string.IsNullOrWhiteSpace(rawId))
        {
            string trimmed = rawId.Trim();
            if (SafeVisualizerIdRegex().IsMatch(trimmed) &&
                !WindowsReservedDeviceNames.Contains(trimmed))
            {
                return trimmed;
            }
        }

        return $"viz_{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Strips Google account authentication cookies (<c>SID</c>, <c>SAPISID</c>, <c>__Secure-3PSID</c>, etc.)
    /// from a <c>Cookie</c> header so media CDN requests (e.g. <c>googlevideo.com</c>) never transmit master account credentials.
    /// Non-auth cookies such as <c>SOCS</c> are preserved.
    /// </summary>
    public static string? StripAuthCookiesForCdn(string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        var kept = new List<string>();
        string[] parts = cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            int eqIdx = part.IndexOf('=');
            if (eqIdx <= 0)
            {
                continue;
            }

            string name = part[..eqIdx].Trim();
            if (string.IsNullOrEmpty(name) || GoogleAuthCookieNames.Contains(name))
            {
                continue;
            }

            kept.Add(part);
        }

        return kept.Count > 0 ? string.Join("; ", kept) : null;
    }

    private static bool TryParseCleanHttpsUri(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsPublicIpAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) ||
            ip.Equals(IPAddress.Any) ||
            ip.Equals(IPAddress.IPv6Any) ||
            ip.Equals(IPAddress.None))
        {
            return false;
        }

        byte[] bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork && bytes.Length == 4)
        {
            byte b0 = bytes[0];
            byte b1 = bytes[1];

            // 0.0.0.0/8 (this network), 10.0.0.0/8 (RFC1918), 127.0.0.0/8 (loopback)
            if (b0 == 0 || b0 == 10 || b0 == 127)
            {
                return false;
            }

            // 100.64.0.0/10 (CGNAT RFC6598)
            if (b0 == 100 && b1 >= 64 && b1 <= 127)
            {
                return false;
            }

            // 169.254.0.0/16 (link-local / metadata)
            if (b0 == 169 && b1 == 254)
            {
                return false;
            }

            // 172.16.0.0/12 (RFC1918)
            if (b0 == 172 && b1 >= 16 && b1 <= 31)
            {
                return false;
            }

            // 192.168.0.0/16 (RFC1918)
            if (b0 == 192 && b1 == 168)
            {
                return false;
            }

            // 224.0.0.0/4 (multicast) and 240.0.0.0/4 (reserved/broadcast)
            if (b0 >= 224)
            {
                return false;
            }

            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && bytes.Length == 16)
        {
            // Unique Local Address fc00::/7
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return false;
            }

            // Link-local fe80::/10
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            {
                return false;
            }

            // Site-local fec0::/10 (deprecated)
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0)
            {
                return false;
            }

            // Multicast ff00::/8
            if (bytes[0] == 0xFF)
            {
                return false;
            }

            return true;
        }

        return false;
    }
}
