using System.Numerics;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Updates;

/// <summary>
/// SemVer 2.0.0 parser and precedence comparator supporting leading <c>v</c>/<c>V</c> tag prefixes,
/// pre-release identifiers, and build metadata (ignored for precedence).
/// </summary>
public static class SemVerComparer
{
    /// <summary>
    /// Attempts to parse a version string (e.g. <c>v1.0.1</c>, <c>1.2.3-beta.2+build.9</c>) into a <see cref="SemVersion"/>.
    /// Also normalizes 2-part versions (<c>1.0</c> -&gt; <c>1.0.0</c>) and 4-part Windows assembly versions
    /// with zero revision (<c>1.0.0.0</c> -&gt; <c>1.0.0</c>).
    /// </summary>
    public static bool TryParse(string? raw, out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string text = raw.Trim();
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        string? buildMetadata = null;
        int plusIndex = text.IndexOf('+');
        if (plusIndex >= 0)
        {
            buildMetadata = text[(plusIndex + 1)..];
            text = text[..plusIndex];
            if (!IsValidDotSeparatedIdentifiers(buildMetadata, forbidNumericLeadingZeros: false))
            {
                return false;
            }
        }

        string? preRelease = null;
        int hyphenIndex = text.IndexOf('-');
        if (hyphenIndex >= 0)
        {
            preRelease = text[(hyphenIndex + 1)..];
            text = text[..hyphenIndex];
            if (!IsValidDotSeparatedIdentifiers(preRelease, forbidNumericLeadingZeros: true))
            {
                return false;
            }
        }

        string[] coreParts = text.Split('.');
        if (coreParts.Length < 2 || coreParts.Length > 4)
        {
            return false;
        }

        if (!TryParseCoreSegment(coreParts[0], out int major) ||
            !TryParseCoreSegment(coreParts[1], out int minor))
        {
            return false;
        }

        int patch = 0;
        if (coreParts.Length >= 3 && !TryParseCoreSegment(coreParts[2], out patch))
        {
            return false;
        }

        if (coreParts.Length == 4)
        {
            if (!TryParseCoreSegment(coreParts[3], out int revision) || revision != 0)
            {
                // Allow 4-segment Windows versions only when revision is 0 (e.g. 1.0.0.0)
                return false;
            }
        }

        version = new SemVersion(major, minor, patch, preRelease, buildMetadata);
        return true;
    }

    /// <summary>
    /// Parses a semantic version string or throws <see cref="FormatException"/>.
    /// </summary>
    public static SemVersion Parse(string raw)
    {
        if (TryParse(raw, out var version) && version is not null)
        {
            return version;
        }

        throw new FormatException($"'{raw}' is not a valid SemVer 2.0.0 version string.");
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="candidate"/> has strictly higher SemVer 2.0.0 precedence than <paramref name="current"/>.
    /// </summary>
    public static bool IsNewer(SemVersion candidate, SemVersion current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(current);
        return Compare(candidate, current) > 0;
    }

    /// <summary>
    /// Compares two <see cref="SemVersion"/> instances strictly per SemVer 2.0.0 precedence rules.
    /// Build metadata is ignored when determining precedence.
    /// </summary>
    public static int Compare(SemVersion? left, SemVersion? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        int majorCmp = left.Major.CompareTo(right.Major);
        if (majorCmp != 0)
        {
            return majorCmp;
        }

        int minorCmp = left.Minor.CompareTo(right.Minor);
        if (minorCmp != 0)
        {
            return minorCmp;
        }

        int patchCmp = left.Patch.CompareTo(right.Patch);
        if (patchCmp != 0)
        {
            return patchCmp;
        }

        // Pre-release versions have lower precedence than a normal version
        bool leftHasPre = left.IsPreRelease;
        bool rightHasPre = right.IsPreRelease;

        if (!leftHasPre && !rightHasPre)
        {
            return 0;
        }

        if (!leftHasPre)
        {
            return 1;
        }

        if (!rightHasPre)
        {
            return -1;
        }

        string[] leftIds = left.PreRelease!.Split('.');
        string[] rightIds = right.PreRelease!.Split('.');
        int minCount = Math.Min(leftIds.Length, rightIds.Length);

        for (int i = 0; i < minCount; i++)
        {
            int cmp = ComparePreReleaseIdentifier(leftIds[i], rightIds[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        // A larger set of pre-release fields has higher precedence if all preceding identifiers are equal
        return leftIds.Length.CompareTo(rightIds.Length);
    }

    private static int ComparePreReleaseIdentifier(string left, string right)
    {
        bool leftNumeric = IsAllAsciiDigits(left);
        bool rightNumeric = IsAllAsciiDigits(right);

        if (leftNumeric && rightNumeric)
        {
            var leftVal = BigInteger.Parse(left);
            var rightVal = BigInteger.Parse(right);
            return leftVal.CompareTo(rightVal);
        }

        // Numeric identifiers always have lower precedence than non-numeric identifiers
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        // Identifiers with letters or hyphens are compared lexically in ASCII sort order
        return string.CompareOrdinal(left, right);
    }

    private static bool TryParseCoreSegment(string segment, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(segment) || !IsAllAsciiDigits(segment))
        {
            return false;
        }

        // SemVer 2.0.0 forbids leading zeroes on numeric core segments (e.g. "01")
        if (segment.Length > 1 && segment[0] == '0')
        {
            return false;
        }

        return int.TryParse(segment, out value) && value >= 0;
    }

    private static bool IsValidDotSeparatedIdentifiers(string value, bool forbidNumericLeadingZeros)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string[] parts = value.Split('.');
        foreach (string part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                return false;
            }

            foreach (char ch in part)
            {
                bool valid = (ch >= '0' && ch <= '9') ||
                             (ch >= 'A' && ch <= 'Z') ||
                             (ch >= 'a' && ch <= 'z') ||
                             ch == '-';
                if (!valid)
                {
                    return false;
                }
            }

            if (forbidNumericLeadingZeros && IsAllAsciiDigits(part) && part.Length > 1 && part[0] == '0')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllAsciiDigits(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (char ch in text)
        {
            if (ch < '0' || ch > '9')
            {
                return false;
            }
        }

        return true;
    }
}
