using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Updates;

/// <summary>
/// Selects the best matching Windows installer asset (<c>.exe</c>, <c>.msix</c>, or <c>.msixbundle</c>)
/// from a GitHub Release for the target CPU architecture and packaging model.
/// </summary>
public static partial class WindowsReleaseAssetSelector
{
    private static readonly string[] AllowedExtensions = [".exe", ".msix", ".msixbundle"];

    /// <summary>
    /// Selects the most appropriate Windows installer asset for the current machine,
    /// preferring <see cref="RuntimeInformation.OSArchitecture"/> and falling back to
    /// <see cref="RuntimeInformation.ProcessArchitecture"/> when running under emulation.
    /// </summary>
    public static GitHubAssetDto? SelectForCurrentEnvironment(
        IReadOnlyList<GitHubAssetDto> assets,
        bool isPackagedApp = false)
    {
        var osArch = RuntimeInformation.OSArchitecture;
        var primary = SelectInstallerAsset(assets, osArch, isPackagedApp);
        if (primary is not null)
        {
            return primary;
        }

        var processArch = RuntimeInformation.ProcessArchitecture;
        if (processArch != osArch)
        {
            return SelectInstallerAsset(assets, processArch, isPackagedApp);
        }

        return null;
    }

    /// <summary>
    /// Selects the highest-priority compatible Windows installer asset for <paramref name="targetArchitecture"/>.
    /// Returns <c>null</c> if no HTTPS installer asset matches the architecture.
    /// </summary>
    public static GitHubAssetDto? SelectInstallerAsset(
        IReadOnlyList<GitHubAssetDto> assets,
        Architecture targetArchitecture,
        bool isPackagedApp = false)
    {
        if (assets is null || assets.Count == 0)
        {
            return null;
        }

        var candidates = new List<(GitHubAssetDto Asset, int Score, int OriginalIndex)>();

        for (int i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            if (!IsValidHttpsInstallerAsset(asset, out string extension))
            {
                continue;
            }

            var detectedArchitectures = DetectArchitectures(asset.Name);
            bool hasExplicitArch = detectedArchitectures.Count > 0;
            bool matchesTargetArch = detectedArchitectures.Contains(targetArchitecture);

            // Never select an asset explicitly built for a different architecture
            if (hasExplicitArch && !matchesTargetArch)
            {
                continue;
            }

            int score = ComputeAssetScore(asset.Name, extension, matchesTargetArch, hasExplicitArch, isPackagedApp);
            candidates.Add((asset, score, i));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        candidates.Sort(static (a, b) =>
        {
            int scoreCompare = b.Score.CompareTo(a.Score);
            return scoreCompare != 0 ? scoreCompare : a.OriginalIndex.CompareTo(b.OriginalIndex);
        });

        return candidates[0].Asset;
    }

    /// <summary>
    /// Validates whether a filename carries a supported Windows installer extension (<c>.exe</c>, <c>.msix</c>, <c>.msixbundle</c>).
    /// </summary>
    public static bool IsSupportedInstallerExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        foreach (string ext in AllowedExtensions)
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly HashSet<string> AllowedGitHubUpdateExactHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    /// <summary>
    /// Validates that <paramref name="url"/> is a well-formed absolute <c>https://</c> URI.
    /// </summary>
    public static bool IsValidHttpsUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(uri.Host) &&
               string.IsNullOrEmpty(uri.UserInfo);
    }

    /// <summary>
    /// Validates whether <paramref name="host"/> is an allowlisted GitHub Releases or GitHub release asset CDN host.
    /// </summary>
    public static bool IsAllowedGitHubUpdateHost(string? host)
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

        if (AllowedGitHubUpdateExactHosts.Contains(cleanHost))
        {
            return true;
        }

        const string ghUserContentSuffix = ".githubusercontent.com";
        return cleanHost.Length > ghUserContentSuffix.Length &&
               cleanHost.EndsWith(ghUserContentSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates that <paramref name="url"/> is an <c>https://</c> URI targeting an allowlisted GitHub update host.
    /// </summary>
    public static bool IsAllowedGitHubUpdateUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               IsAllowedGitHubUpdateHost(uri.IdnHost);
    }

    internal static HashSet<Architecture> DetectArchitectures(string assetName)
    {
        var result = new HashSet<Architecture>();
        if (string.IsNullOrWhiteSpace(assetName))
        {
            return result;
        }

        string lower = assetName.ToLowerInvariant();

        // Normalize composite x86_64 / x86-64 tokens before splitting so "x86" is not falsely matched
        string normalized = X8664Regex().Replace(lower, " x64 ");
        string[] tokens = TokenSplitRegex().Split(normalized);

        foreach (string token in tokens)
        {
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            switch (token)
            {
                case "arm64":
                case "aarch64":
                case "winarm64":
                    result.Add(Architecture.Arm64);
                    break;
                case "x64":
                case "amd64":
                case "win64":
                    result.Add(Architecture.X64);
                    break;
                case "x86":
                case "win32":
                case "ia32":
                    result.Add(Architecture.X86);
                    break;
            }
        }

        return result;
    }

    private static bool IsValidHttpsInstallerAsset(GitHubAssetDto asset, out string extension)
    {
        extension = string.Empty;
        if (asset is null ||
            string.IsNullOrWhiteSpace(asset.Name) ||
            !IsAllowedGitHubUpdateUrl(asset.BrowserDownloadUrl))
        {
            return false;
        }

        foreach (string ext in AllowedExtensions)
        {
            if (asset.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                extension = ext;
                return true;
            }
        }

        return false;
    }

    private static int ComputeAssetScore(
        string assetName,
        string extension,
        bool matchesTargetArch,
        bool hasExplicitArch,
        bool isPackagedApp)
    {
        bool isExe = string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase);
        bool isMsixBundle = string.Equals(extension, ".msixbundle", StringComparison.OrdinalIgnoreCase);
        bool isMsix = string.Equals(extension, ".msix", StringComparison.OrdinalIgnoreCase);

        int score = 0;

        if (isPackagedApp)
        {
            // Packaged MSIX app prefers .msixbundle / .msix first, then .exe
            if (isMsix && matchesTargetArch) score += 1000;
            else if (isMsixBundle && matchesTargetArch) score += 950;
            else if (isMsixBundle && !hasExplicitArch) score += 900;
            else if (isMsix && !hasExplicitArch) score += 800;
            else if (isExe && matchesTargetArch) score += 600;
            else if (isExe && !hasExplicitArch) score += 500;
        }
        else
        {
            // Unpackaged WinUI 3 desktop app (WindowsPackageType=None) prefers .exe installer first,
            // with .msix / .msixbundle fallback
            if (isExe && matchesTargetArch) score += 1000;
            else if (isExe && !hasExplicitArch) score += 850;
            else if (isMsix && matchesTargetArch) score += 700;
            else if (isMsixBundle && matchesTargetArch) score += 680;
            else if (isMsixBundle && !hasExplicitArch) score += 650;
            else if (isMsix && !hasExplicitArch) score += 600;
        }

        string lower = assetName.ToLowerInvariant();
        if (lower.Contains("setup", StringComparison.Ordinal) ||
            lower.Contains("installer", StringComparison.Ordinal))
        {
            score += 40;
        }

        if (lower.Contains("windows", StringComparison.Ordinal) ||
            lower.Contains("win-", StringComparison.Ordinal) ||
            lower.Contains("win_", StringComparison.Ordinal))
        {
            score += 20;
        }

        if (lower.Contains("portable", StringComparison.Ordinal))
        {
            score -= 30;
        }

        return score;
    }

    [GeneratedRegex(@"(?<![a-z0-9])x86[_-]64(?![a-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex X8664Regex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenSplitRegex();
}
