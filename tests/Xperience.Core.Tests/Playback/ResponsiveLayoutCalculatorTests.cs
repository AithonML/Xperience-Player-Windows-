using Xunit;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Playback;

namespace Xperience.Core.Tests.Playback;

public sealed class ResponsiveLayoutCalculatorTests
{
    [Theory]
    [InlineData(420, WindowLayoutTier.Compact)]
    [InlineData(480, WindowLayoutTier.Compact)]
    [InlineData(759.9, WindowLayoutTier.Compact)]
    [InlineData(760, WindowLayoutTier.Medium)]
    [InlineData(900, WindowLayoutTier.Medium)]
    [InlineData(1099.9, WindowLayoutTier.Medium)]
    [InlineData(1100, WindowLayoutTier.Wide)]
    [InlineData(1440, WindowLayoutTier.Wide)]
    public void ResolveTier_MatchesExpectedBreakpoints(double width, WindowLayoutTier expected)
    {
        Assert.Equal(expected, ResponsiveLayoutCalculator.ResolveTier(width));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(double.NaN)]
    public void ResolveTier_InvalidWidth_DefaultsToWide(double width)
    {
        Assert.Equal(WindowLayoutTier.Wide, ResponsiveLayoutCalculator.ResolveTier(width));
    }

    [Fact]
    public void Compute_WideDesktop_PreservesDesktopLayoutAndSidebarPreference()
    {
        var state = ResponsiveLayoutCalculator.Compute(
            windowWidth: 1366,
            isDetailSubView: false,
            isSearchOpen: false,
            isSettingsOpen: false,
            isFullscreenNowPlayingOpen: false,
            userPreferredSidebarVisible: true);

        Assert.Equal(WindowLayoutTier.Wide, state.Tier);
        Assert.True(state.ShouldShowNowPlayingSidebar);
        Assert.True(state.ShowDesktopTopNavBar);
        Assert.False(state.ShowDesktopHeaderMiniPlayer);
        Assert.False(state.ShowCompactBottomNavPill);
        Assert.False(state.ShowCompactDetailBottomNav);
        Assert.False(state.ShowCompactBottomMiniPlayer);
        Assert.False(state.StackDetailHeadersVertically);
        Assert.True(state.ShowTrackRowSecondaryColumns);
        Assert.False(state.UseTopSettingsNavigationPane);
        Assert.Equal(32.0, state.CatalogBottomPadding);
    }

    [Fact]
    public void Compute_MediumWidth_AutoCollapsesSidebarAndShowsHeaderMiniPlayer()
    {
        var state = ResponsiveLayoutCalculator.Compute(
            windowWidth: 920,
            isDetailSubView: false,
            isSearchOpen: false,
            isSettingsOpen: false,
            isFullscreenNowPlayingOpen: false,
            userPreferredSidebarVisible: true);

        Assert.Equal(WindowLayoutTier.Medium, state.Tier);
        Assert.False(state.ShouldShowNowPlayingSidebar);
        Assert.True(state.ShowDesktopTopNavBar);
        Assert.True(state.ShowDesktopHeaderMiniPlayer);
        Assert.False(state.ShowCompactBottomNavPill);
        Assert.False(state.ShowCompactBottomMiniPlayer);
        Assert.False(state.UseTopSettingsNavigationPane);
    }

    [Fact]
    public void Compute_CompactMainSection_ShowsFloatingBottomNavAndCompactMiniPlayer()
    {
        var state = ResponsiveLayoutCalculator.Compute(
            windowWidth: 460,
            isDetailSubView: false,
            isSearchOpen: false,
            isSettingsOpen: false,
            isFullscreenNowPlayingOpen: false,
            userPreferredSidebarVisible: true);

        Assert.Equal(WindowLayoutTier.Compact, state.Tier);
        Assert.False(state.ShouldShowNowPlayingSidebar);
        Assert.False(state.ShowDesktopTopNavBar);
        Assert.False(state.ShowDesktopHeaderMiniPlayer);
        Assert.True(state.ShowCompactBottomNavPill);
        Assert.False(state.ShowCompactDetailBottomNav);
        Assert.True(state.ShowCompactBottomMiniPlayer);
        Assert.True(state.StackDetailHeadersVertically);
        Assert.False(state.ShowTrackRowSecondaryColumns);
        Assert.True(state.UseTopSettingsNavigationPane);
        Assert.Equal(156.0, state.CatalogBottomPadding);
    }

    [Fact]
    public void Compute_CompactDetailView_SwitchesBottomNavToCompactCircles()
    {
        var state = ResponsiveLayoutCalculator.Compute(
            windowWidth: 480,
            isDetailSubView: true,
            isSearchOpen: false,
            isSettingsOpen: false,
            isFullscreenNowPlayingOpen: false,
            userPreferredSidebarVisible: true);

        Assert.Equal(WindowLayoutTier.Compact, state.Tier);
        Assert.False(state.ShowCompactBottomNavPill);
        Assert.True(state.ShowCompactDetailBottomNav);
        Assert.True(state.ShowCompactBottomMiniPlayer);
        Assert.True(state.StackDetailHeadersVertically);
    }

    [Fact]
    public void Compute_CompactFullscreenNowPlaying_HidesBottomChrome()
    {
        var state = ResponsiveLayoutCalculator.Compute(
            windowWidth: 440,
            isDetailSubView: false,
            isSearchOpen: false,
            isSettingsOpen: false,
            isFullscreenNowPlayingOpen: true,
            userPreferredSidebarVisible: true);

        Assert.False(state.ShowCompactBottomNavPill);
        Assert.False(state.ShowCompactDetailBottomNav);
        Assert.False(state.ShowCompactBottomMiniPlayer);
    }

    [Fact]
    public void Compute_CompactNarrowNowPlayingOverlay_HidesTopAndBottomChrome()
    {
        var state = ResponsiveLayoutCalculator.Compute(
            windowWidth: 460,
            isDetailSubView: false,
            isSearchOpen: false,
            isSettingsOpen: false,
            isFullscreenNowPlayingOpen: false,
            userPreferredSidebarVisible: true,
            isNarrowNowPlayingOverlayOpen: true);

        Assert.False(state.ShouldShowNowPlayingSidebar);
        Assert.False(state.ShowDesktopTopNavBar);
        Assert.False(state.ShowDesktopHeaderMiniPlayer);
        Assert.False(state.ShowCompactBottomNavPill);
        Assert.False(state.ShowCompactDetailBottomNav);
        Assert.False(state.ShowCompactBottomMiniPlayer);
    }
}
