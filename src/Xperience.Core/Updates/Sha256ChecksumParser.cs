using System.Text.RegularExpressions;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Updates;

/// <summary>
/// Resolves and validates expected SHA-256 digests from GitHub Release asset <c>digest</c> fields
/// and fallback checksum files (<c>checksums.txt</c>, <c>SHA256SUMS</c>, <c>*.sha256</c>).
/// Enforces strict 64-character hexadecimal validation (fail-closed).
/// </summary>
public static partial class Sha256ChecksumParser
{
    private static readonly string[] KnownMultiChecksumFilenames =
    [
        "checksums.txt",
        "sha256sums",
        "sha256sums.txt",
        "checksums-sha256.txt",
        "sha256.txt",
        "checksums.sha256"
    ];

    /// <summary>
    /// Normalizes a raw SHA-256 string (either <c>"sha256:&lt;64-hex&gt;"</c> or raw <c>"&lt;64-hex&gt;"</c>)
    /// to lowercase 64-character hex. Returns <c>false</c> for missing, malformed, or non-SHA-256 values.
    /// </summary>
    public static bool TryNormalizeSha256(string? raw, out string normalizedHex)
    {
        normalizedHex = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string candidate = TrimWhitespaceAndBom(raw);
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        int colonIndex = candidate.IndexOf(':');
        if (colonIndex >= 0)
        {
            string algorithm = TrimWhitespaceAndBom(candidate[..colonIndex]);
            if (!algorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase) &&
                !algorithm.Equals("sha-256", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            candidate = TrimWhitespaceAndBom(candidate[(colonIndex + 1)..]);
        }

        if (!IsValidSha256Hex(candidate))
        {
            return false;
        }

        normalizedHex = candidate.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Parses the GitHub Releases API asset <c>digest</c> field (e.g. <c>"sha256:..."</c> or raw 64-hex).
    /// Rejects non-SHA-256 algorithms and malformed strings.
    /// </summary>
    public static bool TryParseAssetDigest(string? digestField, out string normalizedHex)
    {
        return TryNormalizeSha256(digestField, out normalizedHex);
    }

    /// <summary>
    /// Finds candidate checksum files attached to the GitHub Release, ordered from most specific
    /// (<c>&lt;asset&gt;.sha256</c>) to multi-file manifests (<c>checksums.txt</c>, <c>SHA256SUMS</c>)
    /// and generic <c>*.sha256</c> assets. Only HTTPS assets are returned.
    /// </summary>
    public static IReadOnlyList<(GitHubAssetDto Asset, bool IsAssetSpecific)> FindChecksumAssets(
        IReadOnlyList<GitHubAssetDto> assets,
        string targetAssetName)
    {
        if (assets is null || assets.Count == 0 || string.IsNullOrWhiteSpace(targetAssetName))
        {
            return [];
        }

        string cleanTarget = Path.GetFileName(targetAssetName);
        var specificMatches = new List<(GitHubAssetDto Asset, bool IsAssetSpecific)>();
        var manifestMatches = new List<(GitHubAssetDto Asset, bool IsAssetSpecific)>();
        var genericShaMatches = new List<(GitHubAssetDto Asset, bool IsAssetSpecific)>();

        foreach (var asset in assets)
        {
            if (asset is null ||
                string.IsNullOrWhiteSpace(asset.Name) ||
                !WindowsReleaseAssetSelector.IsValidHttpsUrl(asset.BrowserDownloadUrl))
            {
                continue;
            }

            string name = asset.Name.Trim();
            if (name.Equals($"{cleanTarget}.sha256", StringComparison.OrdinalIgnoreCase) ||
                name.Equals($"{cleanTarget}.sha256sum", StringComparison.OrdinalIgnoreCase) ||
                name.Equals($"{cleanTarget}.sha256.txt", StringComparison.OrdinalIgnoreCase))
            {
                specificMatches.Add((asset, true));
                continue;
            }

            if (KnownMultiChecksumFilenames.Any(known => name.Equals(known, StringComparison.OrdinalIgnoreCase)))
            {
                manifestMatches.Add((asset, false));
                continue;
            }

            if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".sha256sum", StringComparison.OrdinalIgnoreCase))
            {
                genericShaMatches.Add((asset, false));
            }
        }

        var ordered = new List<(GitHubAssetDto Asset, bool IsAssetSpecific)>(
            specificMatches.Count + manifestMatches.Count + genericShaMatches.Count);
        ordered.AddRange(specificMatches);
        ordered.AddRange(manifestMatches);
        ordered.AddRange(genericShaMatches);
        return ordered;
    }

    /// <summary>
    /// Extracts the expected SHA-256 hex hash for <paramref name="targetAssetName"/> from checksum file text.
    /// Supports GNU (<c>&lt;hex&gt;  filename</c>, <c>&lt;hex&gt; *filename</c>), BSD (<c>SHA256 (filename) = &lt;hex&gt;</c>),
    /// and colon/space filename-first formats. Bare hash lines without a filename are accepted ONLY when
    /// <paramref name="isAssetSpecificFile"/> is <c>true</c>.
    /// </summary>
    public static bool TryExtractHashFromChecksumText(
        string? content,
        string targetAssetName,
        bool isAssetSpecificFile,
        out string normalizedHex)
    {
        normalizedHex = string.Empty;
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(targetAssetName))
        {
            return false;
        }

        string expectedFileName = Path.GetFileName(TrimWhitespaceAndBom(targetAssetName));
        string? bareHashCandidate = null;
        int nonCommentLines = 0;

        using var reader = new StringReader(TrimWhitespaceAndBom(content));
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            string line = TrimWhitespaceAndBom(rawLine);
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }

            nonCommentLines++;

            // 1. BSD format: SHA256 (filename) = <64-hex>
            var bsdMatch = BsdSha256LineRegex().Match(line);
            if (bsdMatch.Success)
            {
                string fileToken = NormalizeChecksumFileName(bsdMatch.Groups["file"].Value);
                if (fileToken.Equals(expectedFileName, StringComparison.OrdinalIgnoreCase) &&
                    TryNormalizeSha256(bsdMatch.Groups["hash"].Value, out normalizedHex))
                {
                    return true;
                }

                continue;
            }

            // 2. GNU format: <64-hex> [*]./filename (or sha256:<64-hex>  filename)
            var gnuMatch = GnuSha256LineRegex().Match(line);
            if (gnuMatch.Success)
            {
                string fileToken = NormalizeChecksumFileName(gnuMatch.Groups["file"].Value);
                if (fileToken.Equals(expectedFileName, StringComparison.OrdinalIgnoreCase) &&
                    TryNormalizeSha256(gnuMatch.Groups["hash"].Value, out normalizedHex))
                {
                    return true;
                }

                continue;
            }

            // 3. Filename-first format: filename: <64-hex> or filename <64-hex>
            var fileFirstMatch = FileFirstSha256LineRegex().Match(line);
            if (fileFirstMatch.Success)
            {
                string fileToken = NormalizeChecksumFileName(fileFirstMatch.Groups["file"].Value);
                if (fileToken.Equals(expectedFileName, StringComparison.OrdinalIgnoreCase) &&
                    TryNormalizeSha256(fileFirstMatch.Groups["hash"].Value, out normalizedHex))
                {
                    return true;
                }

                continue;
            }

            // 4. Bare single-line hash allowed ONLY for asset-specific checksum files (<asset>.sha256)
            if (isAssetSpecificFile && TryNormalizeSha256(line, out string bareHex))
            {
                bareHashCandidate = bareHex;
            }
        }

        if (isAssetSpecificFile && nonCommentLines == 1 && bareHashCandidate is not null)
        {
            normalizedHex = bareHashCandidate;
            return true;
        }

        return false;
    }

    private static string TrimWhitespaceAndBom(string value)
    {
        return value.Trim().Trim('\uFEFF').Trim();
    }

    private static string NormalizeChecksumFileName(string rawFileToken)
    {
        string cleaned = TrimWhitespaceAndBom(rawFileToken).Trim('"', '\'');
        if (cleaned.StartsWith('*'))
        {
            cleaned = cleaned[1..];
        }

        while (cleaned.StartsWith("./", StringComparison.Ordinal) ||
               cleaned.StartsWith(".\\", StringComparison.Ordinal))
        {
            cleaned = cleaned[2..];
        }

        int lastSlash = Math.Max(cleaned.LastIndexOf('/'), cleaned.LastIndexOf('\\'));
        return lastSlash >= 0 ? cleaned[(lastSlash + 1)..] : cleaned;
    }

    private static bool IsValidSha256Hex(string candidate)
    {
        if (candidate.Length != 64)
        {
            return false;
        }

        foreach (char ch in candidate)
        {
            bool isHex = (ch >= '0' && ch <= '9') ||
                         (ch >= 'a' && ch <= 'f') ||
                         (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"^SHA-?256\s*\((?<file>.+?)\)\s*=\s*(?<hash>[0-9a-fA-F]{64})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BsdSha256LineRegex();

    [GeneratedRegex(@"^(?:sha-?256:)?(?<hash>[0-9a-fA-F]{64})\s+\*?(?<file>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GnuSha256LineRegex();

    [GeneratedRegex(@"^(?<file>.+?)\s*[:=]\s*(?:sha-?256:)?(?<hash>[0-9a-fA-F]{64})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileFirstSha256LineRegex();
}
