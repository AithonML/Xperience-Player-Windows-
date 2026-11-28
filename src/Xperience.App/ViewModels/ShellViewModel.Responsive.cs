using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Playback;

namespace Xperience.App.ViewModels;

/// <summary>
/// Adaptive narrow-window layout state and helpers for <see cref="ShellViewModel"/>.
/// Keeps wide desktop layout (&gt;= 1100 px) untouched while providing an Android-inspired
/// floating frosted bottom navigation bar, perimeter-progress mini-player, and full-width
/// Now Playing sheet on narrow windows (&lt; 760 px).
/// </summary>
public partial class ShellViewModel
{
    private double _lastWindowWidth = 1280.0;
    private bool _userPreferredSidebarVisible = true;
    private bool _isNarrowNowPlayingOverlayOpen;
    private ResponsiveLayoutState _responsiveLayout = ResponsiveLayoutCalculator.Compute(
        windowWidth: 1280.0,
        isDetailSubView: false,
        isSearchOpen: false,
        isSettingsOpen: false,
        isFullscreenNowPlayingOpen: false,
        userPreferredSidebarVisible: true,
        isNarrowNowPlayingOverlayOpen: false);

    public event EventHandler<bool>? NarrowNowPlayingOverlayChanged;

    public bool IsNarrowNowPlayingOverlayOpen
    {
        get => _isNarrowNowPlayingOverlayOpen;
        set
        {
            if (_isNarrowNowPlayingOverlayOpen == value)
            {
                return;
            }

            _isNarrowNowPlayingOverlayOpen = value;
            OnPropertyChanged(nameof(IsNarrowNowPlayingOverlayOpen));
            OnPropertyChanged(nameof(FloatingTopHeaderVisibility));
            OnPropertyChanged(nameof(NowPlayingSidebarToggleGlyph));
            RefreshResponsiveLayoutState();
            NarrowNowPlayingOverlayChanged?.Invoke(this, value);
        }
    }

    public WindowLayoutTier CurrentLayoutTier => _responsiveLayout.Tier;

    public bool IsCompactLayout => _responsiveLayout.Tier == WindowLayoutTier.Compact;

    public bool IsMediumOrCompactLayout => _responsiveLayout.Tier != WindowLayoutTier.Wide;

    public bool IsWideLayout => _responsiveLayout.Tier == WindowLayoutTier.Wide;

    public bool StackDetailHeadersVertically => _responsiveLayout.StackDetailHeadersVertically;

    public bool UseTopSettingsNavigationPane => _responsiveLayout.UseTopSettingsNavigationPane;

    public double CatalogBottomPadding => _responsiveLayout.CatalogBottomPadding;

    public Visibility FloatingTopHeaderVisibility =>
        (IsNarrowNowPlayingOverlayOpen && IsMediumOrCompactLayout) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DesktopTopNavVisibility =>
        _responsiveLayout.ShowDesktopTopNavBar ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DesktopSearchCapsuleVisibility =>
        (_responsiveLayout.ShowDesktopTopNavBar || (IsSearchExpanded && !IsNarrowNowPlayingOverlayOpen))
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility CompactBottomNavPillVisibility =>
        _responsiveLayout.ShowCompactBottomNavPill ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CompactDetailBottomNavVisibility =>
        _responsiveLayout.ShowCompactDetailBottomNav ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CompactBottomMiniPlayerVisibility =>
        _responsiveLayout.ShowCompactBottomMiniPlayer ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CompactBottomChromeVisibility =>
        (_responsiveLayout.ShowCompactBottomMiniPlayer ||
         _responsiveLayout.ShowCompactBottomNavPill ||
         _responsiveLayout.ShowCompactDetailBottomNav)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility TrackRowSecondaryColumnVisibility =>
        _responsiveLayout.ShowTrackRowSecondaryColumns ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HomeRefreshTextVisibility =>
        IsCompactLayout ? Visibility.Collapsed : Visibility.Visible;

    public double CompactNavHomeActiveOpacity => IsHomeSection ? 1.0 : 0.0;
    public double CompactNavLibraryActiveOpacity => IsLibrarySection ? 1.0 : 0.0;
    public double CompactNavLikedActiveOpacity => IsLikedSection ? 1.0 : 0.0;
    public double CompactNavPlaylistsActiveOpacity => IsPlaylistsSection ? 1.0 : 0.0;

    public double CompactNavHomeInactiveOpacity => IsHomeSection ? 0.68 : 0.88;
    public double CompactNavLibraryInactiveOpacity => IsLibrarySection ? 0.68 : 0.88;
    public double CompactNavLikedInactiveOpacity => IsLikedSection ? 0.68 : 0.88;
    public double CompactNavPlaylistsInactiveOpacity => IsPlaylistsSection ? 0.68 : 0.88;

    /// <summary>
    /// Updates the responsive breakpoint state for the given window width in DIPs.
    /// Automatically collapses the right Now Playing sidebar below 1100 px and restores the
    /// user's preferred sidebar state when returning to wide desktop widths.
    /// </summary>
    public void UpdateResponsiveWindowWidth(double windowWidth)
    {
        if (double.IsNaN(windowWidth) || double.IsInfinity(windowWidth) || windowWidth <= 0)
        {
            return;
        }

        _lastWindowWidth = windowWidth;
        RefreshResponsiveLayoutState(applySidebarTransition: true);
    }

    internal void RefreshResponsiveLayoutState(bool applySidebarTransition = false)
    {
        bool isDetailSubView = ActiveSubView is WorkspaceSubView.ArtistDetail
            or WorkspaceSubView.AlbumDetail
            or WorkspaceSubView.PlaylistDetail
            or WorkspaceSubView.HomeCategoryGrid;

        bool isSettingsOpen = IsSettingsStudioOpen || ActiveSubView == WorkspaceSubView.Settings;

        var targetTier = ResponsiveLayoutCalculator.ResolveTier(_lastWindowWidth);
        if (targetTier == WindowLayoutTier.Wide && _isNarrowNowPlayingOverlayOpen)
        {
            _isNarrowNowPlayingOverlayOpen = false;
            OnPropertyChanged(nameof(IsNarrowNowPlayingOverlayOpen));
            OnPropertyChanged(nameof(FloatingTopHeaderVisibility));
            OnPropertyChanged(nameof(NowPlayingSidebarToggleGlyph));
            NarrowNowPlayingOverlayChanged?.Invoke(this, false);
        }

        var next = ResponsiveLayoutCalculator.Compute(
            windowWidth: _lastWindowWidth,
            isDetailSubView: isDetailSubView,
            isSearchOpen: IsSearchPageOpen,
            isSettingsOpen: isSettingsOpen,
            isFullscreenNowPlayingOpen: IsFullscreenNowPlayingOpen,
            userPreferredSidebarVisible: _userPreferredSidebarVisible,
            isNarrowNowPlayingOverlayOpen: _isNarrowNowPlayingOverlayOpen);

        bool changed = next != _responsiveLayout;
        _responsiveLayout = next;

        if (applySidebarTransition && !_isNarrowNowPlayingOverlayOpen && IsNowPlayingSidebarVisible != next.ShouldShowNowPlayingSidebar)
        {
            IsNowPlayingSidebarVisible = next.ShouldShowNowPlayingSidebar;
            NowPlayingSidebarVisibilityChanged?.Invoke(this, IsNowPlayingSidebarVisible);
        }

        if (changed)
        {
            NotifyResponsivePropertiesChanged();
        }
        else
        {
            NotifyCompactNavActiveIndicatorsChanged();
        }
    }

    private void NotifyResponsivePropertiesChanged()
    {
        OnPropertyChanged(nameof(CurrentLayoutTier));
        OnPropertyChanged(nameof(IsCompactLayout));
        OnPropertyChanged(nameof(IsMediumOrCompactLayout));
        OnPropertyChanged(nameof(IsWideLayout));
        OnPropertyChanged(nameof(StackDetailHeadersVertically));
        OnPropertyChanged(nameof(UseTopSettingsNavigationPane));
        OnPropertyChanged(nameof(CatalogBottomPadding));
        OnPropertyChanged(nameof(FloatingTopHeaderVisibility));
        OnPropertyChanged(nameof(DesktopTopNavVisibility));
        OnPropertyChanged(nameof(DesktopSearchCapsuleVisibility));
        OnPropertyChanged(nameof(MiniPlayerHeaderVisibility));
        OnPropertyChanged(nameof(CompactBottomNavPillVisibility));
        OnPropertyChanged(nameof(CompactDetailBottomNavVisibility));
        OnPropertyChanged(nameof(CompactBottomMiniPlayerVisibility));
        OnPropertyChanged(nameof(CompactBottomChromeVisibility));
        OnPropertyChanged(nameof(TrackRowSecondaryColumnVisibility));
        OnPropertyChanged(nameof(HomeRefreshTextVisibility));
        OnPropertyChanged(nameof(NowPlayingSidebarToggleGlyph));
        NotifyCompactNavActiveIndicatorsChanged();
    }

    private void NotifyCompactNavActiveIndicatorsChanged()
    {
        OnPropertyChanged(nameof(CompactNavHomeActiveOpacity));
        OnPropertyChanged(nameof(CompactNavLibraryActiveOpacity));
        OnPropertyChanged(nameof(CompactNavLikedActiveOpacity));
        OnPropertyChanged(nameof(CompactNavPlaylistsActiveOpacity));
        OnPropertyChanged(nameof(CompactNavHomeInactiveOpacity));
        OnPropertyChanged(nameof(CompactNavLibraryInactiveOpacity));
        OnPropertyChanged(nameof(CompactNavLikedInactiveOpacity));
        OnPropertyChanged(nameof(CompactNavPlaylistsInactiveOpacity));
    }

    [RelayCommand]
    private void OpenCompactNowPlaying()
    {
        if (IsMediumOrCompactLayout)
        {
            IsNarrowNowPlayingOverlayOpen = true;
            return;
        }

        if (!IsNowPlayingSidebarVisible)
        {
            ToggleNowPlayingSidebarCommand.Execute(null);
        }
    }
}
