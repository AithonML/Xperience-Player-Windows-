using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Playback;

/// <summary>
/// Pure evaluator for Local Scan Blacklist/Whitelist and Shallow/Deep (global + per-folder) filtering rules.
/// </summary>
public static class LocalScanFilterEvaluator
{
    public static string NormalizeDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string trimmed = path.Trim().Replace('/', Path.DirectorySeparatorChar);
        try
        {
            trimmed = Path.GetFullPath(trimmed);
        }
        catch
        {
            // Keep trimmed path if invalid characters exist in unit tests
        }

        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool ResolveEffectiveDeepScan(FolderScanDepthMode depthMode, bool globalDeepScan) =>
        depthMode switch
        {
            FolderScanDepthMode.Shallow => false,
            FolderScanDepthMode.Deep => true,
            _ => globalDeepScan
        };

    /// <summary>
    /// Returns true if <paramref name="filePath"/> is directly inside <paramref name="ruleFolderPath"/> (shallow)
    /// or inside any nested subdirectory of <paramref name="ruleFolderPath"/> (deep).
    /// </summary>
    public static bool DoesFileMatchFolderRule(string filePath, string ruleFolderPath, bool isDeep)
    {
        string normalizedRule = NormalizeDirectoryPath(ruleFolderPath);
        if (string.IsNullOrEmpty(normalizedRule) || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string fileDir = NormalizeDirectoryPath(Path.GetDirectoryName(filePath));
        if (string.IsNullOrEmpty(fileDir))
        {
            return false;
        }

        if (string.Equals(fileDir, normalizedRule, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!isDeep)
        {
            return false;
        }

        string prefix = normalizedRule + Path.DirectorySeparatorChar;
        return fileDir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Evaluates whether an audio file path should be included in the local library given the current filter settings.
    /// </summary>
    public static bool ShouldIncludeFile(string filePath, LocalScanFilterSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        if (settings is null || settings.FilterMode == LocalScanFilterMode.All)
        {
            return true;
        }

        if (settings.FilterMode == LocalScanFilterMode.Blacklist)
        {
            foreach (var rule in settings.Rules)
            {
                if (rule.ListType != FolderScanListType.Blacklist)
                {
                    continue;
                }

                bool isDeep = ResolveEffectiveDeepScan(rule.DepthMode, settings.GlobalDeepScan);
                if (DoesFileMatchFolderRule(filePath, rule.FolderPath, isDeep))
                {
                    return false;
                }
            }

            return true;
        }

        if (settings.FilterMode == LocalScanFilterMode.Whitelist)
        {
            var whitelistRules = settings.Rules.Where(r => r.ListType == FolderScanListType.Whitelist).ToList();
            if (whitelistRules.Count == 0)
            {
                return false;
            }

            foreach (var rule in whitelistRules)
            {
                bool isDeep = ResolveEffectiveDeepScan(rule.DepthMode, settings.GlobalDeepScan);
                if (DoesFileMatchFolderRule(filePath, rule.FolderPath, isDeep))
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }
}
