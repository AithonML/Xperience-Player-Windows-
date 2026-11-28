namespace Xperience.Core.Domain.Enums;

/// <summary>
/// Responsive window width tiers for adaptive desktop-to-narrow layout switching.
/// </summary>
public enum WindowLayoutTier
{
    /// <summary>Narrow split-screen / phone-like window (&lt; 760 px).</summary>
    Compact = 0,

    /// <summary>Medium window (760 px .. 1099 px) where the right Now Playing sidebar auto-collapses.</summary>
    Medium = 1,

    /// <summary>Full wide desktop layout (&gt;= 1100 px).</summary>
    Wide = 2
}
