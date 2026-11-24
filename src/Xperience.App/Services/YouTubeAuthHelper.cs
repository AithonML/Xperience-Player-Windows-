using System.Security.Cryptography;
using System.Text;

namespace Xperience.App.Services;

/// <summary>
/// Pure helper utilities for YouTube Music session cookies, <c>SAPISIDHASH</c> authorization
/// headers (including <c>1P</c>/<c>3P</c> and <c>_u</c> delegated session variants),
/// <c>DATASYNC_ID</c> parsing, and <c>SOCS</c> consent cookies.
/// </summary>
internal static class YouTubeAuthHelper
{
    public const string YouTubeMusicOrigin = "https://music.youtube.com";
    public const string YouTubeWwwOrigin = "https://www.youtube.com";
    public const string DefaultSocsCookie = "SOCS=CAISEwgDEgk4MTM4MzYzNTIaAmVuIAEaBgiApPzGBg";

    private static readonly HashSet<string> AuthenticatedCookieNames = new(StringComparer.Ordinal)
    {
        "SID",
        "HSID",
        "SSID",
        "APISID",
        "SAPISID",
        "__Secure-1PSID",
        "__Secure-3PSID",
        "__Secure-1PAPISID",
        "__Secure-3PAPISID"
    };

    public static Dictionary<string, string> ParseCookieString(string? cookie)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return result;
        }

        foreach (var rawPart in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int splitIndex = rawPart.IndexOf('=');
            if (splitIndex <= 0)
            {
                continue;
            }

            string name = rawPart[..splitIndex].Trim();
            string value = rawPart[(splitIndex + 1)..].Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!result.TryGetValue(name, out var existing) || string.IsNullOrWhiteSpace(existing) || !string.IsNullOrWhiteSpace(value))
            {
                result[name] = value;
            }
        }

        return result;
    }

    public static string MergeCookieStrings(params string?[] cookieStrings)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var raw in cookieStrings)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (var (name, value) in ParseCookieString(raw))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (!merged.TryGetValue(name, out var existing) || string.IsNullOrWhiteSpace(existing))
                {
                    if (!merged.ContainsKey(name))
                    {
                        order.Add(name);
                    }

                    merged[name] = value;
                }
            }
        }

        return string.Join("; ", order.Select(name => $"{name}={merged[name]}"));
    }

    public static string? ExtractSapisid(IReadOnlyDictionary<string, string> cookieMap)
    {
        if (cookieMap.TryGetValue("SAPISID", out var sapisid) && !string.IsNullOrWhiteSpace(sapisid))
        {
            return sapisid;
        }

        if (cookieMap.TryGetValue("__Secure-3PAPISID", out var p3) && !string.IsNullOrWhiteSpace(p3))
        {
            return p3;
        }

        if (cookieMap.TryGetValue("__Secure-1PAPISID", out var p1) && !string.IsNullOrWhiteSpace(p1))
        {
            return p1;
        }

        return null;
    }

    public static string? ExtractSapisid(string? cookie) =>
        ExtractSapisid(ParseCookieString(cookie));

    public static bool HasYouTubeAccountSession(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return false;
        }

        var map = ParseCookieString(cookie);
        bool hasAuthCookie = map.Any(kvp =>
            AuthenticatedCookieNames.Contains(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value));

        return hasAuthCookie && ExtractSapisid(map) is not null;
    }

    public static string? DecodePercentEncoded(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!trimmed.Contains('%', StringComparison.Ordinal))
        {
            return trimmed;
        }

        try
        {
            string decoded = Uri.UnescapeDataString(trimmed).Trim();
            return string.IsNullOrWhiteSpace(decoded) || string.Equals(decoded, "null", StringComparison.OrdinalIgnoreCase)
                ? null
                : decoded;
        }
        catch
        {
            return trimmed;
        }
    }

    public static string? GetOnBehalfOfUser(string? dataSyncId)
    {
        if (string.IsNullOrWhiteSpace(dataSyncId))
        {
            return null;
        }

        int sep = dataSyncId.IndexOf("||", StringComparison.Ordinal);
        string candidate = (sep >= 0 ? dataSyncId[..sep] : dataSyncId).Trim();
        return string.IsNullOrWhiteSpace(candidate) || string.Equals(candidate, "null", StringComparison.OrdinalIgnoreCase)
            ? null
            : candidate;
    }

    public static string? GetUserSessionId(string? dataSyncId)
    {
        if (string.IsNullOrWhiteSpace(dataSyncId))
        {
            return null;
        }

        int sep = dataSyncId.IndexOf("||", StringComparison.Ordinal);
        if (sep < 0)
        {
            return null;
        }

        string candidate = dataSyncId[(sep + 2)..].Trim();
        return string.IsNullOrWhiteSpace(candidate) || string.Equals(candidate, "null", StringComparison.OrdinalIgnoreCase)
            ? null
            : candidate;
    }

    /// <summary>
    /// Returns a delegated Brand Account pageId only when <c>DATASYNC_ID</c> identifies a delegated
    /// channel distinct from the underlying Gaia user session id. Sending <c>X-Goog-PageId</c> for
    /// personal accounts causes Google InnerTube APIs to reject requests with 400/403.
    /// </summary>
    public static string? GetDelegatedPageId(string? dataSyncId)
    {
        string? pageId = GetOnBehalfOfUser(dataSyncId);
        string? userSessionId = GetUserSessionId(dataSyncId);
        if (pageId is null || userSessionId is null)
        {
            return null;
        }

        if (userSessionId == "0" || string.Equals(pageId, userSessionId, StringComparison.Ordinal))
        {
            return null;
        }

        return pageId;
    }

    public static string EnsureConsentCookie(string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return DefaultSocsCookie;
        }

        var parsed = ParseCookieString(cookie);
        if (parsed.TryGetValue("SOCS", out var socs) && !string.IsNullOrWhiteSpace(socs))
        {
            return cookie;
        }

        return $"{cookie}; {DefaultSocsCookie}";
    }

    public static string? ComputeAuthorizationHeader(
        string cookie,
        long timestampSeconds,
        string origin = YouTubeMusicOrigin,
        string? dataSyncId = null)
    {
        var cookieMap = ParseCookieString(cookie);
        string? sapisid = ExtractSapisid(cookieMap);
        if (string.IsNullOrWhiteSpace(sapisid))
        {
            return null;
        }

        var tokens = new List<string>(6)
        {
            $"SAPISIDHASH {timestampSeconds}_{Sha1Hex($"{timestampSeconds} {sapisid} {origin}")}"
        };

        string? userSessionId = GetUserSessionId(dataSyncId);
        if (!string.IsNullOrWhiteSpace(userSessionId))
        {
            tokens.Add($"SAPISIDHASH {timestampSeconds}_{Sha1Hex($"{userSessionId} {timestampSeconds} {sapisid} {origin}")}_u");
        }

        if (cookieMap.TryGetValue("__Secure-1PAPISID", out var sapisid1p) && !string.IsNullOrWhiteSpace(sapisid1p))
        {
            tokens.Add($"SAPISID1PHASH {timestampSeconds}_{Sha1Hex($"{timestampSeconds} {sapisid1p} {origin}")}");
            if (!string.IsNullOrWhiteSpace(userSessionId))
            {
                tokens.Add($"SAPISID1PHASH {timestampSeconds}_{Sha1Hex($"{userSessionId} {timestampSeconds} {sapisid1p} {origin}")}_u");
            }
        }

        if (cookieMap.TryGetValue("__Secure-3PAPISID", out var sapisid3p) && !string.IsNullOrWhiteSpace(sapisid3p))
        {
            tokens.Add($"SAPISID3PHASH {timestampSeconds}_{Sha1Hex($"{timestampSeconds} {sapisid3p} {origin}")}");
            if (!string.IsNullOrWhiteSpace(userSessionId))
            {
                tokens.Add($"SAPISID3PHASH {timestampSeconds}_{Sha1Hex($"{userSessionId} {timestampSeconds} {sapisid3p} {origin}")}_u");
            }
        }

        return string.Join(" ", tokens);
    }

    private static string Sha1Hex(string input)
    {
        byte[] hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
