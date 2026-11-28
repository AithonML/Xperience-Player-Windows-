using Xperience.Core.Domain.Enums;

namespace Xperience.Core.Playback;

/// <summary>
/// Immutable snapshot of responsive shell layout decisions computed from the current window width and view state.
/// </summary>
public sealed record ResponsiveLayoutState(
    WindowLayoutTier Tier,
    bool ShouldShowNowPlayingSidebar,
    bool ShowDesktopTopNavBar,
    bool ShowDesktopHeaderMiniPlayer,
    bool ShowCompactBottomNavPill,
    bool ShowCompactDetailBottomNav,
    bool ShowCompactBottomMiniPlayer,
    bool StackDetailHeadersVertically,
    bool ShowTrackRowSecondaryColumns,
    bool UseTopSettingsNavigationPane,
    double CatalogBottomPadding);

/// <summary>
/// Pure breakpoint and adaptive layout calculator shared across the shell and unit tests.
/// </summary>
public static class ResponsiveLayoutCalculator
{
    public const double CompactMaxWidthExclusive = 760.0;
    public const double WideMinWidthInclusive = 1100.0;
    public const double DetailStackMaxWidthExclusive = 860.0;

    /// <summary>
    /// Maps a window width in DIPs to its responsive <see cref="WindowLayoutTier"/>.
    /// </summary>
    public static WindowLayoutTier ResolveTier(double windowWidth)
    {
        if (double.IsNaN(windowWidth) || double.IsInfinity(windowWidth) || windowWidth <= 0)
        {
            return WindowLayoutTier.Wide;
        }

        if (windowWidth < CompactMaxWidthExclusive)
        {
            return WindowLayoutTier.Compact;
        }

        if (windowWidth < WideMinWidthInclusive)
        {
            return WindowLayoutTier.Medium;
        }

        return WindowLayoutTier.Wide;
    }

    /// <summary>
    /// Computes the complete adaptive layout state for the shell.
    /// </summary>
    public static ResponsiveLayoutState Compute(
        double windowWidth,
        bool isDetailSubView,
        bool isSearchOpen,
        bool isSettingsOpen,
        bool isFullscreenNowPlayingOpen,
        bool userPreferredSidebarVisible,
        bool isNarrowNowPlayingOverlayOpen = false)
    {
        WindowLayoutTier tier = ResolveTier(windowWidth);
        bool isCompact = tier == WindowLayoutTier.Compact;
        bool isWide = tier == WindowLayoutTier.Wide;
        bool narrowOverlayActive = isNarrowNowPlayingOverlayOpen && !isWide;

        // Auto-collapse right Now Playing sidebar on Medium/Compact so main content has full breathing room.
        bool showSidebar = isWide && userPreferredSidebarVisible;

        bool showDesktopTopNav = !isCompact && !narrowOverlayActive;
        bool showDesktopHeaderMiniPlayer = !isCompact && !showSidebar && !narrowOverlayActive;

        bool canShowCompactBottomChrome = isCompact && !isFullscreenNowPlayingOpen && !narrowOverlayActive;
        bool showCompactBottomNavPill = canShowCompactBottomChrome && !isDetailSubView && !isSearchOpen && !isSettingsOpen;
        bool showCompactDetailBottomNav = canShowCompactBottomChrome && isDetailSubView && !isSearchOpen && !isSettingsOpen;
        bool showCompactBottomMiniPlayer = canShowCompactBottomChrome;

        bool stackDetailHeaders = !double.IsNaN(windowWidth) && windowWidth > 0 && windowWidth < DetailStackMaxWidthExclusive;
        bool showTrackRowSecondaryColumns = !isCompact;
        bool useTopSettingsPane = isCompact;

        double catalogBottomPadding = isCompact
            ? (showCompactBottomNavPill || showCompactDetailBottomNav ? 156.0 : 96.0)
            : 32.0;

        return new ResponsiveLayoutState(
            Tier: tier,
            ShouldShowNowPlayingSidebar: showSidebar,
            ShowDesktopTopNavBar: showDesktopTopNav,
            ShowDesktopHeaderMiniPlayer: showDesktopHeaderMiniPlayer,
            ShowCompactBottomNavPill: showCompactBottomNavPill,
            ShowCompactDetailBottomNav: showCompactDetailBottomNav,
            ShowCompactBottomMiniPlayer: showCompactBottomMiniPlayer,
            StackDetailHeadersVertically: stackDetailHeaders,
            ShowTrackRowSecondaryColumns: showTrackRowSecondaryColumns,
            UseTopSettingsNavigationPane: useTopSettingsPane,
            CatalogBottomPadding: catalogBottomPadding);
    }
}
