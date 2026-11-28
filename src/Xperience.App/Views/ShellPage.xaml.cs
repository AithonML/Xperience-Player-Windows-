using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;
using Xperience.App.Controls;
using Xperience.App.Helpers;
using Xperience.App.ViewModels;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Views;

/// <summary>
/// Main decoupled Shell View for Xperience Music Player.
/// Contains minimal UI-specific view helpers for compiled <c>x:Bind</c> expressions and fluid spring/spatial animations.
/// </summary>
public sealed partial class ShellPage : Page
{
    private readonly DispatcherQueueTimer _searchSpringTimer;
    private readonly DispatcherQueueTimer _searchPlaceholderTimer;
    private int _searchPlaceholderIndex;
    private int _searchPlaceholderCycleGen;
    private static readonly string[] OnlineSearchPlaceholderHints =
    [
        "Search songs, albums, artists...",
        "What's on your mind?",
        "Today's pick",
        "Trending tracks",
        "Search YouTube Music",
        "Find your vibe..."
    ];
    private Windows.Media.Playback.MediaPlayer? _sidebarCanvasPlayer;
    private Windows.Media.Playback.MediaPlayer? _fullscreenCanvasPlayer;
    private Windows.Media.Playback.MediaPlayer? _albumDetailCanvasPlayer;
    private Windows.Media.Playback.MediaPlayer? _artistDetailHeaderPlayer;
    private CanvasRenderTarget? _sidebarVideoRenderTarget;
    private CanvasRenderTarget? _artistHeaderVideoRenderTarget;
    private int _sidebarVideoFrameBusy;
    private int _artistHeaderVideoFrameBusy;
    private DispatcherQueueTimer? _artistVideoRetentionTimer;
    private string? _retainedArtistVideoKey;
    private string? _retainedArtistVideoUri;
    private bool _artistHeaderFirstFrameRendered;
    private int _artistPaletteGeneration;
    private string? _lastArtistAmbientImageUri;
    private string? _lastArtistAmbientSeedHex;
    private ArtistPagePalette _currentArtistPalette = ArtistPagePalette.CreateDefault(isLightTheme: false);
    private DispatcherQueueTimer? _artistSkeletonShimmerTimer;
    private double _artistSkeletonShimmerPhase;
    private Microsoft.UI.WindowId _attachedWindowId;
    private bool _passthroughSyncQueued;
    private bool _xamlRootChangedHooked;
    private SolidColorBrush? _artistAmbientBackgroundBrush;
    private SolidColorBrush? _artistCardBackgroundBrush;
    private SolidColorBrush? _artistCardBorderBrush;
    private SolidColorBrush? _artistPrimaryInkBrush;
    private SolidColorBrush? _artistSecondaryInkBrush;
    private SolidColorBrush? _artistSkeletonFillBrush;

    private int _albumPaletteGeneration;
    private string? _lastAlbumAmbientCoverUri;
    private AlbumPagePalette _currentAlbumPalette = AlbumPagePalette.CreateDefault(isLightTheme: false);
    private DispatcherQueueTimer? _albumSkeletonShimmerTimer;
    private double _albumSkeletonShimmerPhase;
    private SolidColorBrush? _albumAmbientBackgroundBrush;
    private SolidColorBrush? _albumCardBackgroundBrush;
    private SolidColorBrush? _albumCardBorderBrush;
    private SolidColorBrush? _albumPrimaryInkBrush;
    private SolidColorBrush? _albumSecondaryInkBrush;
    private SolidColorBrush? _albumArtistLinkBrush;
    private SolidColorBrush? _albumTrackActiveBackgroundBrush;
    private SolidColorBrush? _albumTrackActiveAccentBrush;
    private SolidColorBrush? _albumSkeletonFillBrush;

    private int _playlistPaletteGeneration;
    private string? _lastPlaylistAmbientCoverUri;
    private AlbumPagePalette _currentPlaylistPalette = AlbumPagePalette.CreateDefault(isLightTheme: false);
    private DispatcherQueueTimer? _playlistRelatedShimmerTimer;
    private double _playlistRelatedShimmerPhase;
    private SolidColorBrush? _playlistAmbientBackgroundBrush;
    private SolidColorBrush? _playlistCardBackgroundBrush;
    private SolidColorBrush? _playlistCardBorderBrush;
    private SolidColorBrush? _playlistPrimaryInkBrush;
    private SolidColorBrush? _playlistSecondaryInkBrush;
    private SolidColorBrush? _playlistArtistLinkBrush;
    private SolidColorBrush? _playlistTrackActiveBackgroundBrush;
    private SolidColorBrush? _playlistTrackActiveAccentBrush;
    private SolidColorBrush? _playlistSkeletonFillBrush;

    private DispatcherQueueTimer? _likedRecsShimmerTimer;
    private double _likedRecsShimmerPhase;
    private bool _isLikedNarrowMode;
    private double _searchCurrentWidth = 46.0;
    private double _searchTargetWidth = 46.0;
    private double _searchWidthVelocity;
    private long _lastRowSubButtonClickTick;
    private int _sectionTransitionGeneration;

    // Home Aurora Borealis background animation state
    private readonly DispatcherQueueTimer _auroraAnimTimer;
    private double _auroraElapsedSeconds;
    private AuroraPalette _currentAuroraPalette = AuroraPalette.Default;
    private AuroraPalette _targetAuroraPalette = AuroraPalette.Default;

    // Redesigned Now Playing Sidebar ambient, transport hover, volume jelly, EQ, and queue drag state
    private Color _currentAmbientColor = Color.FromArgb(255, 20, 17, 24);
    private NowPlayingAmbientPalette _currentAmbientPalette = ColorHelperEx.DeriveAmbientPaletteFromColor(Color.FromArgb(255, 239, 68, 68));
    private int _ambientAnimGeneration;
    private int _coverAspectAnimGen;
    private bool _isCoverTransportVisible;
    private bool _isPointerInsideCoverStage;
    private int _transportAnimGen;
    private DispatcherQueueTimer? _coverTransportHideTimer;
    private DispatcherQueueTimer? _volumeSpringTimer;
    private DispatcherQueueTimer? _volumeCollapseDelayTimer;
    private double _volumePillCurrentWidth = 36.0;
    private double _volumePillTargetWidth = 36.0;
    private double _volumePillVelocity;
    private bool _isPointerInVolumeHost;
    private DispatcherQueueTimer? _eqAnimTimer;
    private double _eqPhase;
    private TrackItem? _draggedQueueTrack;
    private UIElement? _dragActiveQueueContainer;
    private FrameworkElement? _dragHandleElement;
    private int _dragStartQueueIndex = -1;
    private int _dragCurrentQueueIndex = -1;
    private double _dragStartPointerY;
    private double _dragStartScrollOffset;
    private double _dragLastScreenY;
    private double _dragOffsetY;
    private double _dragVelocityY;
    private long _dragLastTimestampTicks;
    private int _queueDragGeneration;
    private (int From, int To)? _pendingQueueCommitMove;
    private readonly Dictionary<int, int> _queueNeighborShiftDirections = [];
    private DispatcherQueueTimer? _queueAutoScrollTimer;
    private double _queueLastPointerViewportY;
    private ItemsControl? _activeDragQueueItemsControl;
    private ScrollViewer? _activeDragQueueScrollViewer;
    private FrameworkElement? _activeDragQueueCoordinateHost;
    private CompositionEffectFactory? _fullscreenBackdropBlurFactory;
    private SpriteVisual? _fullscreenBackdropBlurVisual;
    private CompositionEffectBrush? _fullscreenBackdropBlurBrush;
    private CompositionBackdropBrush? _fullscreenBackdropBlurBackdrop;
    private int _fullscreenTransitionGen;
    private int _fullscreenTabAnimGen;

    // Shared Composition Gaussian blur effect factories and attached overlay visuals
    private CompositionEffectFactory? _progressiveBlurEffectFactory;
    private CompositionEffectFactory? _edgeBlurEffectFactory;
    private readonly Dictionary<FrameworkElement, CarouselEdgeOverlayState> _carouselEdgeStates = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, ProgressiveBlurVisualState> _progressiveBlurStates = new();

    /// <summary>
    /// Gets the strongly-typed <see cref="ShellViewModel"/> resolved from the DI container.
    /// </summary>
    public ShellViewModel ViewModel { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShellPage"/> class.
    /// </summary>
    public ShellPage()
    {
        var app = (App)Application.Current;
        ViewModel = app.Services.GetRequiredService<ShellViewModel>();

        InitializeComponent();

        _artistAmbientBackgroundBrush = Resources["ArtistAmbientBackgroundBrush"] as SolidColorBrush;
        _artistCardBackgroundBrush = Resources["ArtistCardBackgroundBrush"] as SolidColorBrush;
        _artistCardBorderBrush = Resources["ArtistCardBorderBrush"] as SolidColorBrush;
        _artistPrimaryInkBrush = Resources["ArtistPrimaryInkBrush"] as SolidColorBrush;
        _artistSecondaryInkBrush = Resources["ArtistSecondaryInkBrush"] as SolidColorBrush;
        _artistSkeletonFillBrush = Resources["ArtistSkeletonFillBrush"] as SolidColorBrush;
        ApplyArtistPagePaletteImmediate(_currentArtistPalette);

        _albumAmbientBackgroundBrush = Resources["AlbumAmbientBackgroundBrush"] as SolidColorBrush;
        _albumCardBackgroundBrush = Resources["AlbumCardBackgroundBrush"] as SolidColorBrush;
        _albumCardBorderBrush = Resources["AlbumCardBorderBrush"] as SolidColorBrush;
        _albumPrimaryInkBrush = Resources["AlbumPrimaryInkBrush"] as SolidColorBrush;
        _albumSecondaryInkBrush = Resources["AlbumSecondaryInkBrush"] as SolidColorBrush;
        _albumArtistLinkBrush = Resources["AlbumArtistLinkBrush"] as SolidColorBrush;
        _albumTrackActiveBackgroundBrush = Resources["AlbumTrackActiveBackgroundBrush"] as SolidColorBrush;
        _albumTrackActiveAccentBrush = Resources["AlbumTrackActiveAccentBrush"] as SolidColorBrush;
        _albumSkeletonFillBrush = Resources["AlbumSkeletonFillBrush"] as SolidColorBrush;
        ApplyAlbumPagePaletteImmediate(_currentAlbumPalette);

        _playlistAmbientBackgroundBrush = Resources["PlaylistAmbientBackgroundBrush"] as SolidColorBrush;
        _playlistCardBackgroundBrush = Resources["PlaylistCardBackgroundBrush"] as SolidColorBrush;
        _playlistCardBorderBrush = Resources["PlaylistCardBorderBrush"] as SolidColorBrush;
        _playlistPrimaryInkBrush = Resources["PlaylistPrimaryInkBrush"] as SolidColorBrush;
        _playlistSecondaryInkBrush = Resources["PlaylistSecondaryInkBrush"] as SolidColorBrush;
        _playlistArtistLinkBrush = Resources["PlaylistArtistLinkBrush"] as SolidColorBrush;
        _playlistTrackActiveBackgroundBrush = Resources["PlaylistTrackActiveBackgroundBrush"] as SolidColorBrush;
        _playlistTrackActiveAccentBrush = Resources["PlaylistTrackActiveAccentBrush"] as SolidColorBrush;
        _playlistSkeletonFillBrush = Resources["PlaylistSkeletonFillBrush"] as SolidColorBrush;
        ApplyPlaylistPagePaletteImmediate(_currentPlaylistPalette);

        LiquidGlassBackdropManager.RegisterStageBackdrops(
            MainWorkspaceContainer,
            NowPlayingArtworkCleanBackdrop,
            FullscreenBackdropStage,
            HomeAuroraCanvas,
            ArtistDetailCleanBackdrop,
            FullscreenNowPlayingOverlay);
        CatalogScrollViewer.ViewChanged += (_, _) =>
        {
            LiquidGlassBackdropManager.NotifyViewportScrolled();
            if (ViewModel.HomeAuroraVisibility == Visibility.Visible)
            {
                HomeAuroraCanvas?.Invalidate();
            }

            if (ViewModel.LibraryAlphabetRailVisibility == Visibility.Visible)
            {
                UpdateLibraryAlphabetRailFromScroll();
            }

            if (ViewModel.LibraryViewVisibility == Visibility.Visible)
            {
                UpdateLibraryHeaderScrimOpacity();
            }

            if (ViewModel.LikedAlphabetRailVisibility == Visibility.Visible)
            {
                UpdateLikedAlphabetRailFromScroll();
            }

            if (ViewModel.LikedViewVisibility == Visibility.Visible)
            {
                UpdateLikedHeaderScrimOpacity();
            }
        };

        _searchSpringTimer = DispatcherQueue.CreateTimer();
        _searchSpringTimer.Interval = TimeSpan.FromMilliseconds(8);
        _searchSpringTimer.Tick += OnSearchSpringTimerTick;

        _searchPlaceholderTimer = DispatcherQueue.CreateTimer();
        _searchPlaceholderTimer.Interval = TimeSpan.FromMilliseconds(3500);
        _searchPlaceholderTimer.Tick += OnSearchPlaceholderTimerTick;

        _auroraAnimTimer = DispatcherQueue.CreateTimer();
        _auroraAnimTimer.Interval = TimeSpan.FromMilliseconds(33);
        _auroraAnimTimer.Tick += OnAuroraAnimTimerTick;

        _artistSkeletonShimmerTimer = DispatcherQueue.CreateTimer();
        _artistSkeletonShimmerTimer.Interval = TimeSpan.FromMilliseconds(33);
        _artistSkeletonShimmerTimer.Tick += OnArtistSkeletonShimmerTick;

        _albumSkeletonShimmerTimer = DispatcherQueue.CreateTimer();
        _albumSkeletonShimmerTimer.Interval = TimeSpan.FromMilliseconds(33);
        _albumSkeletonShimmerTimer.Tick += OnAlbumSkeletonShimmerTick;

        _playlistRelatedShimmerTimer = DispatcherQueue.CreateTimer();
        _playlistRelatedShimmerTimer.Interval = TimeSpan.FromMilliseconds(33);
        _playlistRelatedShimmerTimer.Tick += OnPlaylistRelatedShimmerTick;

        _likedRecsShimmerTimer = DispatcherQueue.CreateTimer();
        _likedRecsShimmerTimer.Interval = TimeSpan.FromMilliseconds(33);
        _likedRecsShimmerTimer.Tick += OnLikedRecommendationsShimmerTick;

        Loaded += OnPageLoaded;
        Loaded += (_, _) =>
        {
            UpdateAuroraAnimationState();
            UpdateLibraryHeaderScrimForTheme();
            UpdateLikedHeaderScrimForTheme();
            SyncLibraryAlphabetRail();
            SyncLikedAlphabetRail();
            UpdateLikedResponsiveLayout();
            UpdateLikedPlayAllButtonContrast();
            UpdateLikedRecommendationsShimmerState();
            UpdatePlaylistRelatedShimmerState();
            SyncDataUsageChartAccent();
        };
        ActualThemeChanged += (_, _) =>
        {
            _ = RefreshArtistPagePaletteForThemeAsync();
            _ = RefreshAlbumPagePaletteForThemeAsync();
            _ = RefreshPlaylistPagePaletteForThemeAsync();
            UpdateHomeGenrePillSelection();
            UpdateLibraryHeaderScrimForTheme();
            UpdateLikedHeaderScrimForTheme();
            LibraryAlphabetRail?.RefreshVisuals();
            LikedAlphabetRail?.RefreshVisuals();
            UpdateLikedPlayAllButtonContrast();
            if (ViewModel.HomeAuroraVisibility == Visibility.Visible)
            {
                HomeAuroraCanvas?.Invalidate();
            }
        };
        Unloaded += (_, _) =>
        {
            _searchSpringTimer.Stop();
            _searchPlaceholderTimer.Stop();
            _auroraAnimTimer.Stop();
            _artistSkeletonShimmerTimer?.Stop();
            _albumSkeletonShimmerTimer?.Stop();
            _playlistRelatedShimmerTimer?.Stop();
            _likedRecsShimmerTimer?.Stop();
            StopNowPlayingSidebarTimers();
            DisposeCanvasVideoResourcesCore();
            DisposeFullscreenMusicVideoResources();
            DisposeFullscreenBackdropBlur();
            DisposeAllCarouselEdgeOverlays();

            try { _fullscreenBackdropBlurFactory?.Dispose(); } catch { }
            _fullscreenBackdropBlurFactory = null;
            try { _progressiveBlurEffectFactory?.Dispose(); } catch { }
            _progressiveBlurEffectFactory = null;
            try { _edgeBlurEffectFactory?.Dispose(); } catch { }
            _edgeBlurEffectFactory = null;
        };
        ViewModel.AuroraPaletteChanged += OnAuroraPaletteChanged;
        ViewModel.ArtistAmbientSourceChanged += OnArtistAmbientSourceChanged;
        ViewModel.AlbumAmbientSourceChanged += OnAlbumAmbientSourceChanged;
        ViewModel.AlbumDescriptionExpandChanged += OnAlbumDescriptionExpandChanged;
        ViewModel.PlaylistAmbientSourceChanged += OnPlaylistAmbientSourceChanged;
        ViewModel.PlaylistDescriptionExpandChanged += OnPlaylistDescriptionExpandChanged;
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), handledEventsToo: true);
        NowPlayingSidebarPanel.AddHandler(PointerEnteredEvent, new PointerEventHandler(OnSidebarCoverStagePointerEntered), handledEventsToo: true);
        NowPlayingSidebarPanel.AddHandler(PointerMovedEvent, new PointerEventHandler(OnSidebarCoverStagePointerMoved), handledEventsToo: true);
        SidebarCoverInteractiveStage.AddHandler(PointerEnteredEvent, new PointerEventHandler(OnSidebarCoverStagePointerEntered), handledEventsToo: true);
        SidebarCoverInteractiveStage.AddHandler(PointerMovedEvent, new PointerEventHandler(OnSidebarCoverStagePointerMoved), handledEventsToo: true);
        ViewModel.ActiveLyricIndexChanged += OnActiveLyricIndexChanged;
        ViewModel.SearchExpandChanged += OnSearchExpandChanged;
        ViewModel.FullscreenTransitionRequested += OnFullscreenTransitionRequested;
        ViewModel.CanvasVideoChanged += OnCanvasVideoChanged;
        ViewModel.AlbumDetailCanvasVideoChanged += OnAlbumDetailCanvasVideoChanged;
        ViewModel.ArtistDetailHeaderVideoChanged += OnArtistDetailHeaderVideoChanged;
        ViewModel.NowPlayingSidebarVisibilityChanged += OnNowPlayingSidebarVisibilityChanged;
        ViewModel.NarrowNowPlayingOverlayChanged += OnNarrowNowPlayingOverlayChanged;
        ViewModel.NowPlayingTabTransitionRequested += OnNowPlayingTabTransitionRequested;
        ViewModel.QueueItemAddedNotification += OnQueueItemAddedNotification;
        ViewModel.Queue.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(OnQueueCollectionChangedResetLayout);
        ViewModel.FullscreenLyricsLayoutChanged += OnFullscreenLyricsLayoutChanged;
        ViewModel.FullscreenSideTabChanged += OnFullscreenSideTabChanged;
        ViewModel.SettingsCategoryTransitionRequested += OnSettingsCategoryTransitionRequested;
        ViewModel.InteractiveYouTubeLoginRequested += OnInteractiveYouTubeLoginRequested;
        ViewModel.LibraryIndexChanged += OnLibraryIndexChanged;
        ViewModel.LikedIndexChanged += OnLikedIndexChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.HomeGenreTags.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateHomeGenrePillSelection);
        ViewModel.HomeRecentlyPlayedAlbums.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (HomeRecentlyPlayedScrollViewer is { IsLoaded: true })
            {
                EnsureCarouselEdgeOverlays(HomeRecentlyPlayedScrollViewer);
                UpdateHorizontalCarouselTransform(HomeRecentlyPlayedScrollViewer);
            }
        });
        ViewModel.LibraryRecentlyAddedPreview.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshLibraryHorizontalCarousels);
        ViewModel.LibraryArtistsPreview.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshLibraryHorizontalCarousels);
        ViewModel.LibraryAlbumsPreview.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshLibraryHorizontalCarousels);
        Loaded += OnShellPageLoadedForPassthrough;
        SizeChanged += OnTopBarPassthroughGeometryChanged;
    }

    internal void AttachWindowNonClientRegions(Microsoft.UI.WindowId windowId)
    {
        _attachedWindowId = windowId;
        HookXamlRootForPassthrough();
        RefreshTitleBarPassthroughRegions();
    }

    internal void RefreshTitleBarPassthroughRegions()
    {
        if (_attachedWindowId.Value == 0 || _passthroughSyncQueued)
        {
            return;
        }

        _passthroughSyncQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _passthroughSyncQueued = false;
            UpdateTitleBarPassthroughRegions();
        });
    }

    private void OnShellPageLoadedForPassthrough(object sender, RoutedEventArgs e)
    {
        HookXamlRootForPassthrough();
        RefreshTitleBarPassthroughRegions();
    }

    private void HookXamlRootForPassthrough()
    {
        if (!_xamlRootChangedHooked && XamlRoot is not null)
        {
            _xamlRootChangedHooked = true;
            XamlRoot.Changed += (_, _) => RefreshTitleBarPassthroughRegions();
        }
    }

    private void OnTopBarPassthroughGeometryChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshTitleBarPassthroughRegions();
    }

    private void UpdateTitleBarPassthroughRegions()
    {
        if (_attachedWindowId.Value == 0 || !IsLoaded || XamlRoot is null)
        {
            return;
        }

        try
        {
            var nonClient = InputNonClientPointerSource.GetForWindowId(_attachedWindowId);
            double scale = XamlRoot.RasterizationScale;
            if (scale <= 0.0)
            {
                scale = 1.0;
            }

            var rects = new List<Windows.Graphics.RectInt32>(8);

            void TryAddPassthrough(FrameworkElement? element, double inflateDipX = 0.0, double inflateDipY = 0.0)
            {
                if (element is null ||
                    !element.IsLoaded ||
                    element.Visibility != Visibility.Visible ||
                    element.Opacity <= 0.01 ||
                    element.ActualWidth <= 1.0 ||
                    element.ActualHeight <= 1.0)
                {
                    return;
                }

                var transform = element.TransformToVisual(null);
                var bounds = transform.TransformBounds(new Rect(
                    -inflateDipX,
                    -inflateDipY,
                    element.ActualWidth + (2.0 * inflateDipX),
                    element.ActualHeight + (2.0 * inflateDipY)));

                // Only non-client region height near top caption strip needs passthrough,
                // though registering the full mini-player / top-capsule rect guarantees priority.
                int x = Math.Max(0, (int)Math.Floor(bounds.X * scale));
                int y = Math.Max(0, (int)Math.Floor(bounds.Y * scale));
                int right = (int)Math.Ceiling((bounds.X + bounds.Width) * scale);
                int bottom = (int)Math.Ceiling((bounds.Y + bounds.Height) * scale);
                int w = right - x;
                int h = bottom - y;
                if (w > 0 && h > 0)
                {
                    rects.Add(new Windows.Graphics.RectInt32(x, y, w, h));
                }
            }

            if (ViewModel.IsFullscreenNowPlayingOpen)
            {
                TryAddPassthrough(FullscreenBackPill, 4.0, 4.0);
                TryAddPassthrough(FullscreenTopActionBar, 4.0, 4.0);
            }
            else
            {
                // Give top floating Mini Player priority over window titlebar drag (inflated to cover 22px scrub border)
                TryAddPassthrough(MiniPlayerBorderHost, 12.0, 12.0);
                TryAddPassthrough(TopHeaderBrandPill, 2.0, 4.0);
                TryAddPassthrough(DesktopNavCapsulePanel, 2.0, 4.0);
                TryAddPassthrough(SearchCapsulePanel, 2.0, 4.0);
                TryAddPassthrough(TopHeaderSettingsPill, 2.0, 4.0);
                if (NowPlayingSidebarPanel is { Visibility: Visibility.Visible } && NowPlayingSidebarPanel.Opacity > 0.1)
                {
                    TryAddPassthrough(SidebarCloseButtonPill, 2.0, 4.0);
                    TryAddPassthrough(SidebarSongVideoSwitcherPill, 2.0, 4.0);
                    TryAddPassthrough(SidebarActionsPill, 2.0, 4.0);
                }
            }

            nonClient.SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch
        {
            // Ignore transient InputNonClientPointerSource errors during teardown or DPI transitions
        }
    }

    /// <summary>
    /// Resolves the active vs inactive pill segment button style for compiled <c>x:Bind</c>.
    /// </summary>
    public Style GetSegmentButtonStyle(bool isActive)
    {
        var key = isActive ? "LiquidPillActiveSegmentButtonStyle" : "LiquidPillSegmentButtonStyle";
        if (Application.Current.Resources.TryGetValue(key, out var resource) && resource is Style style)
        {
            return style;
        }

        return new Style(typeof(Button));
    }

    /// <summary>
    /// Resolves native Windows 11 <c>AccentButtonStyle</c> (dynamically tinted with Album Cover Accent)
    /// vs standard <c>DefaultButtonStyle</c> for native Settings selection buttons.
    /// </summary>
    public Style GetNativeSettingsButtonStyle(bool isActive)
    {
        var key = isActive ? "AccentButtonStyle" : "DefaultButtonStyle";
        if (Application.Current.Resources.TryGetValue(key, out var resource) && resource is Style style)
        {
            return style;
        }

        return new Style(typeof(Button));
    }

    /// <summary>
    /// Computes the pixel width of the cyan progress bar fill based on current position and duration.
    /// </summary>
    public double ComputeProgressWidth(double currentSeconds, double totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return 0;
        }

        double ratio = Math.Clamp(currentSeconds / totalSeconds, 0.0, 1.0);
        return ratio * 138.0;
    }

    /// <summary>
    /// Returns <paramref name="uri"/> only when <paramref name="isVisible"/> is <see langword="true"/>,
    /// preventing hidden template layers from decoding duplicate bitmaps.
    /// </summary>
    public static string? ResolveConditionalUri(bool isVisible, string? uri) => isVisible ? uri : null;

    /// <summary>
    /// Formats pixel-based shader values for compiled <c>x:Bind</c> readouts.
    /// </summary>
    public string FormatPx(double value) => $"{value:0.#} px";

    /// <summary>
    /// Formats frequency shader values for compiled <c>x:Bind</c> readouts.
    /// </summary>
    public string FormatFreq(double value) => $"{value:0.000}";

    /// <summary>
    /// Formats percentage shader values for compiled <c>x:Bind</c> readouts.
    /// </summary>
    public string FormatPercent(double value) => $"{value:0}%";

    private void SyncDataUsageChartAccent()
    {
        if (DataUsageChart is null)
        {
            return;
        }

        DataUsageChart.AccentColor = ColorHelperEx.ParseHex(
            ViewModel.ActiveAccentColorHex,
            Color.FromArgb(255, 45, 212, 191));
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        SyncDataUsageChartAccent();
        InitializeFullscreenMusicVideoFeatures();
        InitializeLyricsVisualFeatures();
        InitializeUpdateFeatures();
        LiquidGlassBackdropManager.RegisterStageBackdrops(
            MainWorkspaceContainer,
            NowPlayingArtworkCleanBackdrop,
            FullscreenBackdropStage,
            HomeAuroraCanvas,
            ArtistDetailCleanBackdrop,
            FullscreenNowPlayingOverlay);

        MainWorkspaceContainer.SizeChanged += OnWorkspaceSizeChanged;
        UpdateResponsiveNavBarMode();
        SyncNavSlicer(animate: false);

        await ViewModel.InitializeAsync();
        await Task.Delay(80);
        UpdateResponsiveNavBarMode();
        SyncAllSlicers(animate: false);
        if (MiniPlayerBorderHost is not null)
        {
            MiniPlayerBorderHost.AccentColor = _currentAmbientPalette.VibrantAccent;
        }
        if (CompactMiniPlayerBorderHost is not null)
        {
            CompactMiniPlayerBorderHost.AccentColor = _currentAmbientPalette.VibrantAccent;
        }

        _ = UpdateNowPlayingAmbientPaletteAsync();
        SyncSidebarSquigglyProgress();
        UpdateSidebarEqualizerAnimationState();
        await Task.Delay(60);
        UpdateResponsiveNavBarMode();
        SyncAllSlicers(animate: false);
    }

    private void EnsureArtistDetailHeaderMediaPlayerInitialized() => EnsureArtistDetailHeaderMediaPlayerInitializedCore();

    private void OnSidebarCanvasVideoCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args) =>
        OnSidebarCanvasVideoCanvasDrawCore(sender, args);

    private void OnArtistDetailHeaderVideoCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args) =>
        OnArtistDetailHeaderVideoCanvasDrawCore(sender, args);

    private static Rect ComputeUniformToFillRect(double srcW, double srcH, double destW, double destH)
    {
        if (srcW <= 1 || srcH <= 1 || destW <= 1 || destH <= 1)
        {
            return new Rect(0, 0, Math.Max(1, srcW), Math.Max(1, srcH));
        }

        double srcAspect = srcW / srcH;
        double destAspect = destW / destH;

        if (srcAspect > destAspect)
        {
            double croppedW = srcH * destAspect;
            double offsetX = (srcW - croppedW) * 0.5;
            return new Rect(offsetX, 0, croppedW, srcH);
        }
        else
        {
            double croppedH = srcW / destAspect;
            double offsetY = (srcH - croppedH) * 0.35;
            return new Rect(0, offsetY, srcW, croppedH);
        }
    }

    private void OnArtistDetailHeaderVideoChanged(object? sender, string? videoUri) =>
        OnArtistDetailHeaderVideoChangedCore(sender, videoUri);

    private void OnAlbumDetailCanvasVideoChanged(object? sender, string? videoUri) =>
        OnAlbumDetailCanvasVideoChangedCore(sender, videoUri);

    private void OnCanvasVideoChanged(object? sender, string? videoUri) =>
        OnCanvasVideoChangedCore(sender, videoUri);

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1.0)
        {
            UpdateResponsiveNavBarMode();
            UpdateLikedResponsiveLayout();
            SyncAllSlicers(animate: false);
            DispatcherQueue.TryEnqueue(() => SyncAllSlicers(animate: false));
        }

        LiquidGlassBackdropManager.NotifyViewportScrolled();
    }

    private bool _isNavBarCompactMode;

    /// <summary>
    /// Automatically collapses or restores the text labels inside the 4 top navigation buttons
    /// (leaving only the icons) when the window becomes narrow or the Search bar expands,
    /// and re-aligns <see cref="NavSlicerIndicatorHost"/> so it never overflows the navigation capsule.
    /// </summary>
    private void UpdateResponsiveNavBarMode()
    {
        if (MainWorkspaceContainer is null || NavTxtHome is null)
        {
            return;
        }

        double totalWidth = MainWorkspaceContainer.ActualWidth;
        if (totalWidth <= 0)
        {
            return;
        }

        ApplyAdaptiveWindowLayout(totalWidth);

        double rightSidebarWidth = ViewModel.IsNowPlayingSidebarVisible ? 324.0 : 156.0;
        double miniPlayerWidth = ViewModel.IsNowPlayingSidebarVisible ? 0.0 : 274.0;
        double availableHeaderLeftWidth = totalWidth - rightSidebarWidth - miniPlayerWidth - 48.0;

        double compactThreshold = ViewModel.IsSearchExpanded ? 920.0 : 620.0;
        bool shouldBeCompact = availableHeaderLeftWidth < compactThreshold;

        var labelVisibility = shouldBeCompact ? Visibility.Collapsed : Visibility.Visible;
        var buttonPadding = shouldBeCompact ? new Thickness(12, 7, 12, 7) : new Thickness(13, 7, 13, 7);

        if (_isNavBarCompactMode != shouldBeCompact || NavTxtHome.Visibility != labelVisibility)
        {
            _isNavBarCompactMode = shouldBeCompact;

            NavTxtHome.Visibility = labelVisibility;
            NavTxtLibrary.Visibility = labelVisibility;
            NavTxtLiked.Visibility = labelVisibility;
            NavTxtPlaylists.Visibility = labelVisibility;

            NavBtnHome.Padding = buttonPadding;
            NavBtnLibrary.Padding = buttonPadding;
            NavBtnLiked.Padding = buttonPadding;
            NavBtnPlaylists.Padding = buttonPadding;

            // Force immediate layout pass so the active button's ActualWidth reflects the new compact/full state
            NavSlicerContainer.UpdateLayout();
            _slicerTargetButtons.Remove(NavSlicerIndicatorHost);
            SyncNavSlicer(animate: false);
            DispatcherQueue.TryEnqueue(() => SyncNavSlicer(animate: false));
        }

        if (ViewModel.IsSearchExpanded)
        {
            _searchTargetWidth = ComputeExpandedSearchWidth();
            if (!_searchSpringTimer.IsRunning && Math.Abs(_searchCurrentWidth - _searchTargetWidth) > 2.0)
            {
                _searchSpringTimer.Start();
            }
        }
    }

    private double ComputeExpandedSearchWidth()
    {
        double totalWidth = MainWorkspaceContainer?.ActualWidth ?? 1280.0;
        if (totalWidth < 760.0)
        {
            return Math.Clamp(totalWidth - 260.0, 180.0, 296.0);
        }

        double rightReserve = ViewModel.IsNowPlayingSidebarVisible ? 324.0 : 430.0;
        double available = Math.Max(360.0, totalWidth - rightReserve);
        return Math.Clamp(available * 0.32, 236.0, 296.0);
    }

    private void ApplyAdaptiveWindowLayout(double totalWidth)
    {
        ViewModel.UpdateResponsiveWindowWidth(totalWidth);

        bool isCompact = ViewModel.IsCompactLayout;

        if (CatalogContentRootStackPanel is not null)
        {
            CatalogContentRootStackPanel.Padding = isCompact
                ? new Thickness(14, 68, 14, ViewModel.CatalogBottomPadding)
                : new Thickness(22, 68, 14, ViewModel.CatalogBottomPadding);
        }

        if (FloatingTopHeaderOverlay is not null)
        {
            FloatingTopHeaderOverlay.Padding = isCompact
                ? new Thickness(14, 12, 0, 0)
                : new Thickness(22, 12, 0, 0);
        }

        if (HomeYtmBannerButtonsPanel is not null && HomeYtmBannerButtonsColDef is not null)
        {
            if (isCompact)
            {
                HomeYtmBannerButtonsColDef.Width = new GridLength(0, GridUnitType.Pixel);
                Grid.SetRow(HomeYtmBannerButtonsPanel, 1);
                Grid.SetColumn(HomeYtmBannerButtonsPanel, 0);
                Grid.SetColumnSpan(HomeYtmBannerButtonsPanel, 2);
                HomeYtmBannerButtonsPanel.HorizontalAlignment = HorizontalAlignment.Right;
            }
            else
            {
                HomeYtmBannerButtonsColDef.Width = GridLength.Auto;
                Grid.SetRow(HomeYtmBannerButtonsPanel, 0);
                Grid.SetColumn(HomeYtmBannerButtonsPanel, 2);
                Grid.SetColumnSpan(HomeYtmBannerButtonsPanel, 1);
                HomeYtmBannerButtonsPanel.HorizontalAlignment = HorizontalAlignment.Right;
            }
        }

        if (ArtistDetailHeaderGrid is not null)
        {
            ArtistDetailHeaderGrid.MinHeight = isCompact ? 380.0 : 500.0;
        }

        if (ArtistTitleTextBlock is not null)
        {
            ArtistTitleTextBlock.FontSize = isCompact ? 32.0 : 46.0;
        }

        ApplyDetailSplitLayouts();
        ApplySettingsNavigationResponsivePaneMode();
    }

    private void OnDetailSplitGridLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDetailSplitLayouts();
    }

    private void ApplyDetailSplitLayouts()
    {
        bool stackVertically = ViewModel.StackDetailHeadersVertically;

        if (AlbumDetailSplitGrid is not null &&
            AlbumDetailHeroColDef is not null &&
            AlbumDetailTracksColDef is not null &&
            AlbumDetailHeroPanel is not null &&
            AlbumDetailTracksPanel is not null)
        {
            if (stackVertically)
            {
                AlbumDetailHeroColDef.Width = new GridLength(1, GridUnitType.Star);
                AlbumDetailTracksColDef.Width = new GridLength(0, GridUnitType.Pixel);
                AlbumDetailSplitGrid.ColumnSpacing = 0;
                AlbumDetailHeroPanel.Width = double.NaN;
                AlbumDetailHeroPanel.MaxWidth = 360;
                Grid.SetRow(AlbumDetailTracksPanel, 1);
                Grid.SetColumn(AlbumDetailTracksPanel, 0);
            }
            else
            {
                AlbumDetailHeroColDef.Width = new GridLength(320, GridUnitType.Pixel);
                AlbumDetailTracksColDef.Width = new GridLength(1, GridUnitType.Star);
                AlbumDetailSplitGrid.ColumnSpacing = 48;
                AlbumDetailHeroPanel.Width = 310;
                AlbumDetailHeroPanel.MaxWidth = double.PositiveInfinity;
                Grid.SetRow(AlbumDetailTracksPanel, 0);
                Grid.SetColumn(AlbumDetailTracksPanel, 1);
            }
        }

        if (PlaylistDetailSplitGrid is not null &&
            PlaylistDetailHeroColDef is not null &&
            PlaylistDetailTracksColDef is not null &&
            PlaylistDetailHeroPanel is not null &&
            PlaylistDetailTracksPanel is not null)
        {
            if (stackVertically)
            {
                PlaylistDetailHeroColDef.Width = new GridLength(1, GridUnitType.Star);
                PlaylistDetailTracksColDef.Width = new GridLength(0, GridUnitType.Pixel);
                PlaylistDetailSplitGrid.ColumnSpacing = 0;
                PlaylistDetailHeroPanel.Width = double.NaN;
                PlaylistDetailHeroPanel.MaxWidth = 360;
                Grid.SetRow(PlaylistDetailTracksPanel, 1);
                Grid.SetColumn(PlaylistDetailTracksPanel, 0);
            }
            else
            {
                PlaylistDetailHeroColDef.Width = new GridLength(320, GridUnitType.Pixel);
                PlaylistDetailTracksColDef.Width = new GridLength(1, GridUnitType.Star);
                PlaylistDetailSplitGrid.ColumnSpacing = 48;
                PlaylistDetailHeroPanel.Width = 310;
                PlaylistDetailHeroPanel.MaxWidth = double.PositiveInfinity;
                Grid.SetRow(PlaylistDetailTracksPanel, 0);
                Grid.SetColumn(PlaylistDetailTracksPanel, 1);
            }
        }
    }

    private void ApplySettingsNavigationResponsivePaneMode()
    {
        if (SettingsNavigationView is null)
        {
            return;
        }

        bool useTop = ViewModel.UseTopSettingsNavigationPane;
        var targetMode = useTop
            ? NavigationViewPaneDisplayMode.Top
            : NavigationViewPaneDisplayMode.Left;

        if (SettingsNavigationView.PaneDisplayMode != targetMode)
        {
            SettingsNavigationView.PaneDisplayMode = targetMode;
            SettingsNavigationView.IsPaneOpen = !useTop;
        }
    }

    /// <summary>
    /// Starts an underdamped spring physics animation expanding or contracting the Search capsule with a fluid bounce.
    /// </summary>
    private void OnSearchExpandChanged(object? sender, bool isExpanded)
    {
        _searchTargetWidth = isExpanded ? ComputeExpandedSearchWidth() : 46.0;

        if (SearchCapsulePanel is not null)
        {
            SearchCapsulePanel.EnableHoverScale = !isExpanded;
        }

        if (HeaderSearchExpandedHost is not null)
        {
            HeaderSearchExpandedHost.IsHitTestVisible = isExpanded;
            HeaderSearchExpandedHost.Opacity = isExpanded ? 1.0 : 0.0;
        }

        if (HeaderSearchIconIdle is not null)
        {
            HeaderSearchIconIdle.Opacity = isExpanded ? 0.0 : 1.0;
        }

        if (HeaderSearchIconActive is not null)
        {
            HeaderSearchIconActive.Opacity = isExpanded ? 1.0 : 0.0;
        }

        UpdateResponsiveNavBarMode();
        UpdateSearchPlaceholderCyclingState();

        if (!_searchSpringTimer.IsRunning)
        {
            _searchSpringTimer.Start();
        }

        if (isExpanded)
        {
            DispatcherQueue.TryEnqueue(() => HeaderSearchTextBox?.Focus(FocusState.Programmatic));
        }
    }

    private void UpdateSearchPlaceholderCyclingState()
    {
        if (SearchCyclingPlaceholderText is null)
        {
            return;
        }

        bool showPlaceholder = ViewModel.IsSearchExpanded && string.IsNullOrEmpty(ViewModel.SearchQuery);
        if (!showPlaceholder)
        {
            _searchPlaceholderTimer.Stop();
            _searchPlaceholderCycleGen++;
            SearchCyclingPlaceholderText.Opacity = 0.0;
            return;
        }

        if (!ViewModel.IsSearchOnlineMode)
        {
            _searchPlaceholderTimer.Stop();
            _searchPlaceholderCycleGen++;
            SearchCyclingPlaceholderText.Text = "Search local library...";
            SearchCyclingPlaceholderText.Opacity = 0.88;
            return;
        }

        SearchCyclingPlaceholderText.Text = OnlineSearchPlaceholderHints[_searchPlaceholderIndex % OnlineSearchPlaceholderHints.Length];
        SearchCyclingPlaceholderText.Opacity = 0.88;
        if (!_searchPlaceholderTimer.IsRunning)
        {
            _searchPlaceholderTimer.Start();
        }
    }

    private async void OnSearchPlaceholderTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (SearchCyclingPlaceholderText is null ||
            !ViewModel.IsSearchExpanded ||
            !ViewModel.IsSearchOnlineMode ||
            !string.IsNullOrEmpty(ViewModel.SearchQuery))
        {
            _searchPlaceholderTimer.Stop();
            return;
        }

        int gen = ++_searchPlaceholderCycleGen;
        // 400ms fade-out -> swap hint -> 600ms fade-in (matching Android DynamicSearchPlaceholder.kt)
        SearchCyclingPlaceholderText.Opacity = 0.0;
        await Task.Delay(400);

        if (gen != _searchPlaceholderCycleGen ||
            !ViewModel.IsSearchExpanded ||
            !ViewModel.IsSearchOnlineMode ||
            !string.IsNullOrEmpty(ViewModel.SearchQuery))
        {
            return;
        }

        _searchPlaceholderIndex = (_searchPlaceholderIndex + 1) % OnlineSearchPlaceholderHints.Length;
        SearchCyclingPlaceholderText.Text = OnlineSearchPlaceholderHints[_searchPlaceholderIndex];
        SearchCyclingPlaceholderText.Opacity = 0.88;
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // While the dedicated Search page is open, clicks on Recent chips, Mood/Genre cards,
        // or Search Results MUST keep the Search bar expanded and page open (closing happens via X / Esc / Nav tabs).
        if (ViewModel.IsSearchPageOpen || !ViewModel.IsSearchExpanded || SearchCapsulePanel is null)
        {
            return;
        }

        try
        {
            var pt = e.GetCurrentPoint(SearchCapsulePanel).Position;
            bool isInsideSearch = pt.X >= -4.0 && pt.Y >= -4.0 &&
                                  pt.X <= SearchCapsulePanel.ActualWidth + 4.0 &&
                                  pt.Y <= SearchCapsulePanel.ActualHeight + 4.0;
            if (!isInsideSearch)
            {
                ViewModel.IsSearchExpanded = false;
            }
        }
        catch
        {
            // Ignore transient coordinate transform errors
        }
    }

    private void OnSearchSpringTimerTick(DispatcherQueueTimer sender, object args)
    {
        // Underdamped spring simulation (produces a subtle, organic fluid bounce at the end of expansion)
        const double stiffness = 0.24;
        const double damping = 0.72;

        double displacement = _searchTargetWidth - _searchCurrentWidth;
        _searchWidthVelocity = (_searchWidthVelocity + (displacement * stiffness)) * damping;
        _searchCurrentWidth += _searchWidthVelocity;

        SearchCapsulePanel.Width = Math.Clamp(_searchCurrentWidth, 44.0, 320.0);

        if (Math.Abs(displacement) < 0.35 && Math.Abs(_searchWidthVelocity) < 0.35)
        {
            _searchCurrentWidth = _searchTargetWidth;
            _searchWidthVelocity = 0.0;
            SearchCapsulePanel.Width = _searchTargetWidth;
            _searchSpringTimer.Stop();
        }
    }

    private async void OnHeaderSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && (ViewModel.IsSearchExpanded || ViewModel.IsSearchPageOpen))
        {
            ViewModel.CloseSearchPageCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await ViewModel.SubmitSearchQueryAsync();
        }
    }

    /// <summary>
    /// Executes the spatial expand transition from the Now Playing sidebar to Fullscreen Mode,
    /// and the spatial contract transition back into the Now Playing sidebar on exit.
    /// </summary>
    private async void OnFullscreenTransitionRequested(object? sender, bool isOpening)
    {
        if (FullscreenNowPlayingOverlay is null)
        {
            return;
        }

        // Compute the relative center offset of the Now Playing sidebar so Fullscreen expands/contracts directly from/to it
        float offsetX = 420.0f;
        float offsetY = -36.0f;
        if (RootLayoutGrid.ActualWidth > 0 && NowPlayingSidebarPanel.ActualWidth > 0)
        {
            offsetX = (float)((RootLayoutGrid.ActualWidth * 0.5) - (NowPlayingSidebarPanel.ActualWidth * 0.5) - 20.0);
        }

        FullscreenNowPlayingOverlay.CenterPoint = new Vector3(
            (float)Math.Max(640.0, RootLayoutGrid.ActualWidth * 0.5),
            (float)Math.Max(360.0, RootLayoutGrid.ActualHeight * 0.5),
            0.0f);

        int transitionGen = ++_fullscreenTransitionGen;

        if (isOpening)
        {
            // Sync active Track Canvas state BEFORE Fullscreen becomes visible so there is zero black flash
            SyncFullscreenCanvasSurfaceOnOpenCore();

            // Seed starting state at the Now Playing card position before making visible
            FullscreenNowPlayingOverlay.Opacity = 0.0;
            FullscreenNowPlayingOverlay.Scale = new Vector3(0.34f, 0.34f, 1.0f);
            FullscreenNowPlayingOverlay.Translation = new Vector3(offsetX, offsetY, 0.0f);
            FullscreenNowPlayingOverlay.Visibility = Visibility.Visible;

            UpdateFullscreenBackdropBlur();
            SyncSidebarSquigglyProgress();
            UpdateSidebarAccentTintElements(_currentAmbientPalette.VibrantAccent);
            UpdateSidebarEqualizerAnimationState();

            await Task.Delay(16);
            if (transitionGen != _fullscreenTransitionGen)
            {
                return;
            }

            // Trigger hardware-accelerated Vector3 & Scalar transitions to full window
            FullscreenNowPlayingOverlay.Opacity = 1.0;
            FullscreenNowPlayingOverlay.Scale = Vector3.One;
            FullscreenNowPlayingOverlay.Translation = Vector3.Zero;

            await Task.Delay(60);
            if (transitionGen != _fullscreenTransitionGen)
            {
                return;
            }

            UpdateFullscreenBackdropBlur();
            ApplyFullscreenDeckLayout();
            SyncFullscreenSlicer(animate: false);
            UpdateFullscreenQueueEdgeFade();
            SyncFullscreenCanvasSurfaceOnOpenCore();

            await Task.Delay(180);
            if (transitionGen != _fullscreenTransitionGen)
            {
                return;
            }

            if (FullscreenSynchronizedLyricsControl is not null)
            {
                FullscreenSynchronizedLyricsControl.AccentColor = _currentAmbientPalette.VibrantAccent;
                FullscreenSynchronizedLyricsControl.RecenterOnActiveLine();
            }

            UpdateFullscreenQueueEdgeFade();
            LiquidGlassBackdropManager.NotifyViewportScrolled();
        }
        else
        {
            // Contract the entire Fullscreen stage back into the Now Playing card
            FullscreenNowPlayingOverlay.Opacity = 0.0;
            FullscreenNowPlayingOverlay.Scale = new Vector3(0.32f, 0.32f, 1.0f);
            FullscreenNowPlayingOverlay.Translation = new Vector3(offsetX, offsetY, 0.0f);

            await Task.Delay(390);
            if (transitionGen != _fullscreenTransitionGen)
            {
                return;
            }

            FullscreenNowPlayingOverlay.Visibility = Visibility.Collapsed;
            DisposeFullscreenBackdropBlur();
            _fullscreenMusicVideoPlayer?.Pause();
            if (!ViewModel.IsFullscreenVideoMode)
            {
                _loadedFullscreenMusicVideoUri = null;
                ReplaceMediaSource(_fullscreenMusicVideoPlayer, null);
            }
            ViewModel.IsFullscreenNowPlayingOpen = false;
            UpdateSidebarEqualizerAnimationState();
        }
    }

    /// <summary>
    /// Smoothly syncs the active synchronized .lrc lyric line index across both the
    /// Sidebar and Fullscreen Win2D SynchronizedLyricsCanvasControl instances.
    /// </summary>
    private void OnActiveLyricIndexChanged(object? sender, int activeIndex)
    {
        if (activeIndex < 0)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (SidebarSynchronizedLyricsControl is not null)
            {
                SidebarSynchronizedLyricsControl.ActiveLineIndex = activeIndex;
            }

            if (FullscreenSynchronizedLyricsControl is not null)
            {
                FullscreenSynchronizedLyricsControl.ActiveLineIndex = activeIndex;
            }
        });
    }

    private void OnFullscreenBackdropBlurLoaded(object sender, RoutedEventArgs e)
    {
        UpdateFullscreenBackdropBlur();
    }

    private void OnFullscreenBackdropBlurSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFullscreenBackdropBlur();
    }

    private void UpdateFullscreenBackdropBlur()
    {
        if (FullscreenBackdropBlurHost is null ||
            FullscreenNowPlayingOverlay is null ||
            FullscreenNowPlayingOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        float w = (float)FullscreenBackdropBlurHost.ActualWidth;
        float h = (float)FullscreenBackdropBlurHost.ActualHeight;
        if (w <= 4f || h <= 4f)
        {
            return;
        }

        try
        {
            if (_fullscreenBackdropBlurVisual is null)
            {
                var hostVisual = ElementCompositionPreview.GetElementVisual(FullscreenBackdropBlurHost);
                var compositor = hostVisual.Compositor;

                var factory = EnsureBlurEffectFactory(
                    compositor,
                    ref _fullscreenBackdropBlurFactory,
                    blurAmount: 40.0f,
                    name: "FullscreenBackdropGaussianBlur");

                var backdropBrush = compositor.CreateBackdropBrush();
                var blurBrush = factory.CreateBrush();
                blurBrush.SetSourceParameter("backdrop", backdropBrush);

                var spriteVisual = compositor.CreateSpriteVisual();
                spriteVisual.Brush = blurBrush;
                ElementCompositionPreview.SetElementChildVisual(FullscreenBackdropBlurHost, spriteVisual);

                _fullscreenBackdropBlurBackdrop = backdropBrush;
                _fullscreenBackdropBlurBrush = blurBrush;
                _fullscreenBackdropBlurVisual = spriteVisual;
            }

            _fullscreenBackdropBlurVisual.Size = new Vector2(w, h);
        }
        catch
        {
            // Keep backdrop safely visible if GPU composition device is resetting
        }
    }

    private void DisposeFullscreenBackdropBlur()
    {
        try
        {
            if (FullscreenBackdropBlurHost is not null)
            {
                ElementCompositionPreview.SetElementChildVisual(FullscreenBackdropBlurHost, null);
            }
        }
        catch
        {
        }

        try
        {
            if (_fullscreenBackdropBlurVisual is not null)
            {
                _fullscreenBackdropBlurVisual.Brush = null;
                _fullscreenBackdropBlurVisual.Dispose();
            }
        }
        catch
        {
        }
        finally
        {
            _fullscreenBackdropBlurVisual = null;
        }

        try
        {
            _fullscreenBackdropBlurBrush?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _fullscreenBackdropBlurBrush = null;
        }

        try
        {
            _fullscreenBackdropBlurBackdrop?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _fullscreenBackdropBlurBackdrop = null;
        }
    }

    private void OnFullscreenSynchronizedLyricLineClicked(object? sender, SynchronizedLyricLineViewModel line)
    {
        ViewModel.SeekToLyric(line);
    }

    private void OnFullscreenLyricsUserScrollingChanged(object? sender, bool isScrolling)
    {
        if (FullscreenLyricsSyncPillHost is not null)
        {
            FullscreenLyricsSyncPillHost.Visibility = isScrolling ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnFullscreenLyricsSyncClicked(object sender, RoutedEventArgs e)
    {
        FullscreenSynchronizedLyricsControl?.RecenterOnActiveLine();
    }

    private void OnFullscreenQueueScrollViewerLoaded(object sender, RoutedEventArgs e)
    {
        UpdateFullscreenQueueEdgeFade();
    }

    private void OnFullscreenQueueScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFullscreenQueueEdgeFade();
    }

    private void OnFullscreenQueueScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateFullscreenQueueEdgeFade();
        LiquidGlassBackdropManager.NotifyViewportScrolled();
    }

    /// <summary>
    /// Smoothly attenuates the opacity of floating borderless Fullscreen queue cards as they enter
    /// the top or bottom edge fade zones so cards dissolve cleanly over the luminous backdrop
    /// without wrapping the queue in a dark container box.
    /// </summary>
    private void UpdateFullscreenQueueEdgeFade()
    {
        if (FullscreenQueueScrollViewer is null || FullscreenQueueScrollViewer.ViewportHeight <= 32)
        {
            return;
        }

        double vh = FullscreenQueueScrollViewer.ViewportHeight;
        double topFadeZone = Math.Clamp(vh * 0.14, 44.0, 76.0);
        double bottomFadeZone = Math.Clamp(vh * 0.17, 56.0, 96.0);

        void ApplyEdgeFadeToElement(FrameworkElement? element)
        {
            if (element is null || element.ActualHeight <= 0 || ReferenceEquals(element, _dragActiveQueueContainer))
            {
                return;
            }

            try
            {
                var transform = element.TransformToVisual(FullscreenQueueScrollViewer);
                var topPt = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                double centerY = topPt.Y + (element.ActualHeight * 0.5);

                double factor = 1.0;
                if (centerY < topFadeZone)
                {
                    double norm = Math.Clamp((centerY + 8.0) / (topFadeZone + 8.0), 0.0, 1.0);
                    factor = norm * norm * (3.0 - (2.0 * norm));
                }
                else if (centerY > vh - bottomFadeZone)
                {
                    double norm = Math.Clamp((vh - centerY + 8.0) / (bottomFadeZone + 8.0), 0.0, 1.0);
                    factor = norm * norm * (3.0 - (2.0 * norm));
                }

                element.Opacity = Math.Clamp(factor, 0.0, 1.0);
            }
            catch
            {
                // Ignore transient layout transform errors during animation
            }
        }

        ApplyEdgeFadeToElement(FullscreenActiveQueueCardHost);

        if (FullscreenQueueItemsControl is not null)
        {
            int count = ViewModel.Queue.Count;
            for (int i = 0; i < count; i++)
            {
                if (GetQueueItemContainer(FullscreenQueueItemsControl, i) is FrameworkElement container)
                {
                    ApplyEdgeFadeToElement(container);
                }
            }
        }
    }

    private void OnVisualizerPresetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VisualizerDefinition preset })
        {
            ViewModel.SelectVisualizerCommand.Execute(preset);
        }
    }

    private async void OnMoveLyricsProviderUpClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LyricsProviderConfig provider })
        {
            await ViewModel.MoveLyricsProviderUpAsync(provider);
        }
    }

    private async void OnMoveLyricsProviderDownClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LyricsProviderConfig provider })
        {
            await ViewModel.MoveLyricsProviderDownAsync(provider);
        }
    }

    private async void OnToggleLyricsProviderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LyricsProviderConfig provider })
        {
            await ViewModel.ToggleLyricsProviderEnabledAsync(provider);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when a <see cref="UIElement.Tapped"/> event on a song row
    /// originated from a child <see cref="Microsoft.UI.Xaml.Controls.Primitives.ButtonBase"/> (such as the <c>"..."</c>
    /// context menu button, <c>Like</c> button, <c>Artist</c> link button, or <c>Play</c> button) so clicking <c>"..."</c>
    /// never accidentally starts playback.
    /// </summary>
    private bool ShouldSuppressRowTap(object sender, TappedRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return true;
        }

        if (Environment.TickCount64 - _lastRowSubButtonClickTick < 320)
        {
            return true;
        }

        try
        {
            var current = e.OriginalSource as DependencyObject;
            var stopAt = sender as DependencyObject;
            while (current is not null && !ReferenceEquals(current, stopAt))
            {
                if (current is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
                {
                    return true;
                }

                current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }
        }
        catch
        {
            // Ignore transient visual tree inspection errors
        }

        return false;
    }

    private async void OnSongRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ShouldSuppressRowTap(sender, e))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayTrackCommand.ExecuteAsync(track);
        }
    }

    private async void OnSongRowPlayClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayTrackCommand.ExecuteAsync(track);
        }
    }

    private async void OnLibrarySongRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ShouldSuppressRowTap(sender, e))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: TrackItem track } element)
        {
            if (IsDescendantOf(element, LikedSectionRoot))
            {
                await ViewModel.PlayLikedSongAsync(track);
                return;
            }

            if (IsDescendantOf(element, SearchMoodDetailRoot))
            {
                await ViewModel.PlayMoodDetailSongAsync(track);
                return;
            }

            if (IsDescendantOf(element, SearchResultsRoot))
            {
                await ViewModel.PlaySearchSongAsync(track);
                return;
            }

            bool fromAlphabeticalSongs = IsDescendantOf(element, LibrarySongsDirectoryRoot);
            bool fromRecentlyAdded = IsDescendantOf(element, LibraryRecentlyAddedViewRoot);
            await ViewModel.PlayLibrarySongAsync(track, fromAlphabeticalSongs, fromRecentlyAdded);
        }
    }

    private async void OnLibraryRecentlyAddedCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayLibrarySongAsync(track, fromAlphabeticalSongs: false, fromRecentlyAdded: true);
        }
    }

    private void OnLibrarySongRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid rowGrid && rowGrid.Children.Count > 2 && rowGrid.Children[2] is Border hoverBorder)
        {
            hoverBorder.Opacity = 0.52;
        }
    }

    private void OnLibrarySongRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid rowGrid && rowGrid.Children.Count > 2 && rowGrid.Children[2] is Border hoverBorder)
        {
            hoverBorder.Opacity = 0.0;
        }
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject? ancestor)
    {
        if (element is null || ancestor is null)
        {
            return false;
        }

        DependencyObject? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void OnLibraryIndexChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SyncLibraryAlphabetRail();
            if (ViewModel.LibraryViewVisibility == Visibility.Visible)
            {
                _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
            }
        });
    }

    private void SyncLibraryAlphabetRail()
    {
        if (LibraryAlphabetRail is null)
        {
            return;
        }

        var available = ViewModel.GetActiveLibraryAvailableLetters();
        LibraryAlphabetRail.SetAvailableLetters(available);
        if (available.Count > 0)
        {
            LibraryAlphabetRail.SyncActiveLetterFromScroll(available[0]);
        }
    }

    private ItemsControl? GetActiveLibraryGroupsControl()
    {
        return ViewModel.SelectedCategory switch
        {
            SearchCategoryFilter.Artists => LibraryArtistsGroupsControl,
            SearchCategoryFilter.Albums => LibraryAlbumsGroupsControl,
            SearchCategoryFilter.Songs => LibrarySongsGroupsControl,
            _ => null
        };
    }

    private static string? GetGroupLetterFromDataItem(object? item)
    {
        return item switch
        {
            LibraryArtistGroup artistGroup => artistGroup.Letter,
            LibraryAlbumGroup albumGroup => albumGroup.Letter,
            LibraryTrackGroup trackGroup => trackGroup.Letter,
            _ => null
        };
    }

    private void OnLibraryAlphabetLetterSelected(object? sender, string letter)
    {
        var groupsControl = GetActiveLibraryGroupsControl();
        if (groupsControl is null || CatalogScrollViewer?.Content is not UIElement scrollContent)
        {
            return;
        }

        int count = groupsControl.Items?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            string? itemLetter = GetGroupLetterFromDataItem(groupsControl.Items![i]);
            if (!string.Equals(itemLetter, letter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (groupsControl.ContainerFromIndex(i) is UIElement container)
            {
                try
                {
                    var pt = container.TransformToVisual(scrollContent).TransformPoint(new Windows.Foundation.Point(0, 0));
                    // Subtract sticky floating header + top nav bar height so letter badge sits cleanly below slicers
                    double targetY = Math.Clamp(pt.Y - 196.0, 0.0, CatalogScrollViewer.ScrollableHeight);
                    _ = CatalogScrollViewer.ChangeView(null, targetY, null, disableAnimation: true);
                }
                catch
                {
                    // Ignore transient layout transform issues during tab transitions
                }
            }

            break;
        }
    }

    private void UpdateLibraryAlphabetRailFromScroll()
    {
        var groupsControl = GetActiveLibraryGroupsControl();
        if (groupsControl is null || LibraryAlphabetRail is null || CatalogScrollViewer?.Content is not UIElement scrollContent)
        {
            return;
        }

        double anchorY = CatalogScrollViewer.VerticalOffset + 216.0;
        int count = groupsControl.Items?.Count ?? 0;
        string? activeLetter = null;

        for (int i = 0; i < count; i++)
        {
            string? itemLetter = GetGroupLetterFromDataItem(groupsControl.Items![i]);
            if (string.IsNullOrWhiteSpace(itemLetter))
            {
                continue;
            }

            if (groupsControl.ContainerFromIndex(i) is not UIElement container)
            {
                continue;
            }

            try
            {
                var pt = container.TransformToVisual(scrollContent).TransformPoint(new Windows.Foundation.Point(0, 0));
                if (pt.Y <= anchorY || activeLetter is null)
                {
                    activeLetter = itemLetter;
                }
                else
                {
                    break;
                }
            }
            catch
            {
                // Ignore transient layout errors
            }
        }

        if (!string.IsNullOrWhiteSpace(activeLetter))
        {
            LibraryAlphabetRail.SyncActiveLetterFromScroll(activeLetter);
        }
    }

    private void UpdateLibraryHeaderScrimForTheme()
    {
        if (LibraryHeaderScrimBorder is null)
        {
            return;
        }

        bool isLight = ActualTheme == ElementTheme.Light;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };

        if (isLight)
        {
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(234, 244, 246, 250), Offset = 0.0 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(198, 244, 246, 250), Offset = 0.62 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(104, 244, 246, 250), Offset = 0.86 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 244, 246, 250), Offset = 1.0 });
        }
        else
        {
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(230, 13, 15, 20), Offset = 0.0 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(196, 13, 15, 20), Offset = 0.62 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(102, 13, 15, 20), Offset = 0.86 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 13, 15, 20), Offset = 1.0 });
        }

        LibraryHeaderScrimBorder.Background = brush;
        UpdateLibraryHeaderScrimOpacity();
    }

    private void UpdateLibraryHeaderScrimOpacity()
    {
        if (LibraryHeaderScrimBorder is null || CatalogScrollViewer is null)
        {
            return;
        }

        double offset = CatalogScrollViewer.VerticalOffset;
        LibraryHeaderScrimBorder.Opacity = offset <= 4.0
            ? 0.0
            : Math.Clamp((offset - 4.0) / 60.0, 0.0, 0.88);
    }

    private void OnLikedIndexChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SyncLikedAlphabetRail();
            UpdateLikedResponsiveLayout();
            SyncLikedSortSlicer(animate: false);
        });
    }

    private void OnLikedSlicerContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) <= 1.0)
        {
            return;
        }

        if (ReferenceEquals(sender, LikedFilterSlicerContainer))
        {
            SyncLikedFilterSlicer(animate: false);
        }
        else if (ReferenceEquals(sender, LikedSortSlicerContainer))
        {
            SyncLikedSortSlicer(animate: false);
        }
    }

    private void SyncLikedAlphabetRail()
    {
        if (LikedAlphabetRail is not null)
        {
            var available = ViewModel.GetActiveLikedAvailableLetters();
            LikedAlphabetRail.SetAvailableLetters(available);
            if (available.Count > 0)
            {
                LikedAlphabetRail.SyncActiveLetterFromScroll(available[0]);
            }
        }

        UpdateLikedRailClearanceMargins();
    }

    private void UpdateLikedRailClearanceMargins()
    {
        bool showRailClearance = ViewModel.SelectedLikedSort == LikedSortMode.Alphabetical &&
                                 ViewModel.LikedAlphabetRailVisibility == Visibility.Visible;

        if (LikedPlayActionsBar is not null)
        {
            LikedPlayActionsBar.Margin = showRailClearance
                ? new Thickness(0, 0, 42, 0)
                : new Thickness(0);
        }

        if (LikedRecommendationsColumn is not null)
        {
            LikedRecommendationsColumn.Margin = (_isLikedNarrowMode && showRailClearance)
                ? new Thickness(0, 0, 42, 0)
                : new Thickness(0);
        }
    }

    private ItemsControl? GetActiveLikedGroupsControl()
    {
        return ViewModel.SelectedLikedFilter switch
        {
            LikedFilterTab.Artists => LikedArtistsGroupsControl,
            LikedFilterTab.Albums => LikedAlbumsGroupsControl,
            _ => LikedSongsGroupsControl
        };
    }

    private void OnLikedAlphabetLetterSelected(object? sender, string letter)
    {
        var groupsControl = GetActiveLikedGroupsControl();
        if (groupsControl is null || CatalogScrollViewer?.Content is not UIElement scrollContent)
        {
            return;
        }

        if (ViewModel.SelectedLikedFilter == LikedFilterTab.Songs)
        {
            var available = ViewModel.GetActiveLikedAvailableLetters();
            if (available.Count > 0 && string.Equals(available[0], letter, StringComparison.OrdinalIgnoreCase))
            {
                _ = CatalogScrollViewer.ChangeView(null, 0.0, null, disableAnimation: true);
                return;
            }
        }

        int count = groupsControl.Items?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            string? itemLetter = GetGroupLetterFromDataItem(groupsControl.Items![i]);
            if (!string.Equals(itemLetter, letter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (groupsControl.ContainerFromIndex(i) is UIElement container)
            {
                try
                {
                    var pt = container.TransformToVisual(scrollContent).TransformPoint(new Windows.Foundation.Point(0, 0));
                    double targetY = Math.Clamp(pt.Y - 196.0, 0.0, CatalogScrollViewer.ScrollableHeight);
                    _ = CatalogScrollViewer.ChangeView(null, targetY, null, disableAnimation: true);
                }
                catch
                {
                    // Ignore transient layout transform issues during tab transitions
                }
            }

            break;
        }
    }

    private void UpdateLikedAlphabetRailFromScroll()
    {
        var groupsControl = GetActiveLikedGroupsControl();
        if (groupsControl is null || LikedAlphabetRail is null || CatalogScrollViewer?.Content is not UIElement scrollContent)
        {
            return;
        }

        double anchorY = CatalogScrollViewer.VerticalOffset + 216.0;
        int count = groupsControl.Items?.Count ?? 0;
        string? activeLetter = null;

        for (int i = 0; i < count; i++)
        {
            string? itemLetter = GetGroupLetterFromDataItem(groupsControl.Items![i]);
            if (string.IsNullOrWhiteSpace(itemLetter))
            {
                continue;
            }

            if (groupsControl.ContainerFromIndex(i) is not UIElement container)
            {
                continue;
            }

            try
            {
                var pt = container.TransformToVisual(scrollContent).TransformPoint(new Windows.Foundation.Point(0, 0));
                if (pt.Y <= anchorY || activeLetter is null)
                {
                    activeLetter = itemLetter;
                }
                else
                {
                    break;
                }
            }
            catch
            {
                // Ignore transient layout errors
            }
        }

        if (!string.IsNullOrWhiteSpace(activeLetter))
        {
            LikedAlphabetRail.SyncActiveLetterFromScroll(activeLetter);
        }
    }

    private void UpdateLikedHeaderScrimForTheme()
    {
        if (LikedHeaderScrimBorder is null)
        {
            return;
        }

        bool isLight = ActualTheme == ElementTheme.Light;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };

        if (isLight)
        {
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(234, 244, 246, 250), Offset = 0.0 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(198, 244, 246, 250), Offset = 0.62 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(104, 244, 246, 250), Offset = 0.86 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 244, 246, 250), Offset = 1.0 });
        }
        else
        {
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(230, 13, 15, 20), Offset = 0.0 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(196, 13, 15, 20), Offset = 0.62 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(102, 13, 15, 20), Offset = 0.86 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 13, 15, 20), Offset = 1.0 });
        }

        LikedHeaderScrimBorder.Background = brush;
        UpdateLikedHeaderScrimOpacity();
    }

    private void UpdateLikedHeaderScrimOpacity()
    {
        if (LikedHeaderScrimBorder is null || CatalogScrollViewer is null)
        {
            return;
        }

        double offset = CatalogScrollViewer.VerticalOffset;
        LikedHeaderScrimBorder.Opacity = offset <= 4.0
            ? 0.0
            : Math.Clamp((offset - 4.0) / 60.0, 0.0, 0.88);
    }

    private void UpdateLikedResponsiveLayout()
    {
        if (CatalogScrollViewer is null || LikedSongsSplitGrid is null || LikedRecommendationsColumn is null || LikedSongsLeftColumnDef is null || LikedSongsRightColumnDef is null)
        {
            return;
        }

        double paneWidth = CatalogScrollViewer.ActualWidth > 0
            ? CatalogScrollViewer.ActualWidth
            : (MainWorkspaceContainer?.ActualWidth ?? 1200.0);

        bool narrow = paneWidth < 920.0;
        _isLikedNarrowMode = narrow;

        if (narrow)
        {
            LikedSongsLeftColumnDef.Width = new GridLength(1, GridUnitType.Star);
            LikedSongsRightColumnDef.Width = new GridLength(0, GridUnitType.Pixel);
            LikedSongsSplitGrid.ColumnSpacing = 0;
            Grid.SetRow(LikedRecommendationsColumn, 1);
            Grid.SetColumn(LikedRecommendationsColumn, 0);
        }
        else
        {
            LikedSongsLeftColumnDef.Width = new GridLength(58, GridUnitType.Star);
            LikedSongsRightColumnDef.Width = new GridLength(42, GridUnitType.Star);
            LikedSongsSplitGrid.ColumnSpacing = 22;
            Grid.SetRow(LikedRecommendationsColumn, 0);
            Grid.SetColumn(LikedRecommendationsColumn, 1);
        }

        if (LikedAlphabetRail is not null)
        {
            bool pinBetweenPanes = ViewModel.SelectedLikedFilter == LikedFilterTab.Songs && !narrow;
            Grid.SetColumn(LikedAlphabetRail, 0);
            Grid.SetColumnSpan(LikedAlphabetRail, pinBetweenPanes ? 1 : 2);
        }

        UpdateLikedRailClearanceMargins();
    }

    private void UpdateLikedPlayAllButtonContrast()
    {
        if (LikedPlayAllIcon is null || LikedPlayAllText is null)
        {
            return;
        }

        Color accent = ColorHelperEx.ParseHex(ViewModel.ActiveAccentColorHex, Color.FromArgb(255, 45, 212, 191));
        bool lightAccent = ColorHelperEx.GetRelativeLuminance(accent) > 0.32;
        var ink = new SolidColorBrush(lightAccent
            ? Color.FromArgb(255, 7, 16, 24)
            : Color.FromArgb(255, 248, 250, 252));
        LikedPlayAllIcon.Foreground = ink;
        LikedPlayAllText.Foreground = ink;
    }

    private async void OnLikedRecommendationTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ShouldSuppressRowTap(sender, e))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayLikedRecommendationAsync(track);
        }
    }

    private async void OnLikedRecommendationPlayClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayLikedRecommendationAsync(track);
        }
    }

    private void RefreshLibraryHorizontalCarousels()
    {
        ScrollViewer?[] carousels =
        [
            LibraryRecentlyAddedScrollViewer,
            LibraryArtistsScrollViewer,
            LibraryAlbumsScrollViewer
        ];

        foreach (var sv in carousels)
        {
            if (sv is { IsLoaded: true })
            {
                EnsureCarouselEdgeOverlays(sv);
                UpdateHorizontalCarouselTransform(sv);
            }
        }
    }

    private async void OnAlbumCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AlbumItem album })
        {
            _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
            await ViewModel.OpenAlbumDetailAsync(album);
        }
    }

    private async void OnArtistCircleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ArtistItem artist })
        {
            _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
            await ViewModel.OpenArtistDetailAsync(artist);
        }
    }

    private async void OnHomeRecentAlbumTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ShouldSuppressRowTap(sender, e))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: RecentAlbumItem recentAlbum })
        {
            _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
            await ViewModel.OpenRecentAlbumAsync(recentAlbum);
        }
    }

    private void OnHomeRecentAlbumRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RecentAlbumItem { SeedTrack: not null } recentAlbum })
        {
            e.Handled = true;
            ViewModel.OpenTrackContextMenu(recentAlbum.SeedTrack);
        }
    }

    private async void OnHomeRecommendedTrackTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ShouldSuppressRowTap(sender, e))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayTrackFromListAsync(track, ViewModel.HomeRecommendedTracks);
        }
    }

    private void OnHomeTrackRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid rowGrid && rowGrid.Children.Count > 1 && rowGrid.Children[1] is Border hoverBorder)
        {
            hoverBorder.Opacity = 0.55;
        }
    }

    private void OnHomeTrackRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid rowGrid && rowGrid.Children.Count > 1 && rowGrid.Children[1] is Border hoverBorder)
        {
            hoverBorder.Opacity = 0.0;
        }
    }

    private async void OnHomeGenrePillClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string genreTag })
        {
            await ViewModel.SelectHomeGenreAsync(genreTag);
            UpdateHomeGenrePillSelection();
        }
    }

    private void OnHomeGenrePillLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button pillButton)
        {
            ApplyHomeGenrePillVisual(pillButton);
        }
    }

    private void UpdateHomeGenrePillSelection()
    {
        if (HomeGenreTagsItemsControl?.ItemsPanelRoot is not Panel panel)
        {
            return;
        }

        foreach (UIElement child in panel.Children)
        {
            Button? button = child as Button ?? FindFirstDescendantButton(child);
            if (button is not null)
            {
                ApplyHomeGenrePillVisual(button);
            }
        }
    }

    private void ApplyHomeGenrePillVisual(Button pillButton)
    {
        string genreTag = pillButton.Tag as string ?? pillButton.Content as string ?? string.Empty;
        bool isSelected = !string.IsNullOrWhiteSpace(genreTag) &&
                          string.Equals(genreTag, ViewModel.SelectedHomeGenre, StringComparison.OrdinalIgnoreCase);

        var resources = Application.Current?.Resources;
        if (isSelected)
        {
            pillButton.Background = resources?["TokenAccentBadgeBackgroundBrush"] as Brush
                ?? new SolidColorBrush(Color.FromArgb(54, 45, 212, 191));
            pillButton.BorderBrush = resources?["TokenAccentBadgeBorderBrush"] as Brush
                ?? new SolidColorBrush(Color.FromArgb(140, 45, 212, 191));
            pillButton.Foreground = resources?["TokenPrimaryAccentBrush"] as Brush
                ?? new SolidColorBrush(Color.FromArgb(255, 45, 212, 191));
        }
        else
        {
            pillButton.Background = new SolidColorBrush(Colors.Transparent);
            pillButton.BorderBrush = resources?["TokenSurfaceBorderBrush"] as Brush
                ?? new SolidColorBrush(Color.FromArgb(64, 255, 255, 255));
            pillButton.Foreground = resources?["TokenSecondaryTextBrush"] as Brush
                ?? new SolidColorBrush(Color.FromArgb(220, 203, 213, 225));
        }
    }

    private static Button? FindFirstDescendantButton(DependencyObject? parent)
    {
        if (parent is null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button btn)
            {
                return btn;
            }

            Button? nested = FindFirstDescendantButton(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async void OnHomeGenreTrackTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ShouldSuppressRowTap(sender, e))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayTrackFromListAsync(track, ViewModel.HomeGenreTracks);
        }
    }

    private async void OnArtistTopTrackTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ShouldSuppressRowTap(sender, e))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayTrackFromListAsync(track, ViewModel.ArtistTopTracks);
        }
    }

    private async void OnArtistTopTrackPlayClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayTrackFromListAsync(track, ViewModel.ArtistTopTracks);
        }
    }

    private async void OnAlbumDetailTrackTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ShouldSuppressRowTap(sender, e))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayTrackFromListAsync(track, ViewModel.AlbumDetailTracks);
        }
    }

    private void OnAlbumTrackRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid rowGrid && rowGrid.Children.Count > 1 && rowGrid.Children[1] is Border hoverBorder)
        {
            hoverBorder.Opacity = 1.0;
        }
    }

    private void OnAlbumTrackRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid rowGrid && rowGrid.Children.Count > 1 && rowGrid.Children[1] is Border hoverBorder)
        {
            hoverBorder.Opacity = 0.0;
        }
    }

    private async void OnAlbumDescriptionExpandChanged(object? sender, bool isExpanded)
    {
        if (AlbumDescriptionAnimatedContainer is null)
        {
            return;
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(AlbumDescriptionAnimatedContainer, true);
            var visual = ElementCompositionPreview.GetElementVisual(AlbumDescriptionAnimatedContainer);
            var compositor = visual.Compositor;

            AlbumDescriptionAnimatedContainer.Opacity = 0.45;
            visual.Properties.InsertVector3("Translation", new Vector3(0f, isExpanded ? -5f : 5f, 0f));

            using var spring = compositor.CreateSpringVector3Animation();
            spring.Target = "Translation";
            spring.FinalValue = Vector3.Zero;
            spring.DampingRatio = 0.72f;
            spring.Period = TimeSpan.FromMilliseconds(36);
            visual.StartAnimation("Translation", spring);

            await Task.Delay(24);
            if (AlbumDescriptionAnimatedContainer is not null)
            {
                AlbumDescriptionAnimatedContainer.Opacity = 1.0;
            }
        }
        catch
        {
            if (AlbumDescriptionAnimatedContainer is not null)
            {
                AlbumDescriptionAnimatedContainer.Opacity = 1.0;
            }
        }
    }

    private async void OnPlaylistDescriptionExpandChanged(object? sender, bool isExpanded)
    {
        if (PlaylistDescriptionAnimatedContainer is null)
        {
            return;
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(PlaylistDescriptionAnimatedContainer, true);
            var visual = ElementCompositionPreview.GetElementVisual(PlaylistDescriptionAnimatedContainer);
            var compositor = visual.Compositor;

            PlaylistDescriptionAnimatedContainer.Opacity = 0.45;
            visual.Properties.InsertVector3("Translation", new Vector3(0f, isExpanded ? -5f : 5f, 0f));

            using var spring = compositor.CreateSpringVector3Animation();
            spring.Target = "Translation";
            spring.FinalValue = Vector3.Zero;
            spring.DampingRatio = 0.72f;
            spring.Period = TimeSpan.FromMilliseconds(36);
            visual.StartAnimation("Translation", spring);

            await Task.Delay(24);
            if (PlaylistDescriptionAnimatedContainer is not null)
            {
                PlaylistDescriptionAnimatedContainer.Opacity = 1.0;
            }
        }
        catch
        {
            if (PlaylistDescriptionAnimatedContainer is not null)
            {
                PlaylistDescriptionAnimatedContainer.Opacity = 1.0;
            }
        }
    }

    private async void OnAlbumDetailTrackPlayClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayTrackFromListAsync(track, ViewModel.AlbumDetailTracks);
        }
    }

    private async void OnLikedTrackTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ShouldSuppressRowTap(sender, e))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayLikedSongAsync(track);
        }
    }

    private async void OnTrackRowArtistClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        if (sender is FrameworkElement { Tag: string artistName } && !string.IsNullOrWhiteSpace(artistName))
        {
            _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
            await ViewModel.OpenArtistByNameAsync(artistName);
        }
    }

    private async void OnTrackRowLikeClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.ToggleLikeTrackItemAsync(track);
            if (sender is Button { Content: FontIcon icon })
            {
                icon.Glyph = track.LikeGlyph;
                icon.Opacity = track.LikeIconOpacity;
            }
        }
    }

    private async void OnQueueItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ShouldSuppressRowTap(sender, e) || _draggedQueueTrack is not null)
        {
            return;
        }

        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayQueueItemAsync(track);
        }
    }

    private void OnProgressBarTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement bar && bar.ActualWidth > 0)
        {
            var pt = e.GetPosition(bar);
            double ratio = Math.Clamp(pt.X / bar.ActualWidth, 0.0, 1.0);
            ViewModel.SeekToSeconds(ratio * ViewModel.TotalDurationSeconds);
        }
    }

    // =========================================================================
    // 1. HIDE / SHOW NOW PLAYING SIDEBAR (Smooth Slide-Right & Column Collapse)
    // =========================================================================

    private int _sidebarAnimGeneration;

    private void OnNarrowNowPlayingOverlayChanged(object? sender, bool isOpen)
    {
        if (NowPlayingSidebarPanel is null || MainWorkspaceContainer is null)
        {
            return;
        }

        // Cancel any in-flight sidebar collapse/expand animation
        ++_sidebarAnimGeneration;

        if (isOpen)
        {
            Grid.SetColumn(NowPlayingSidebarPanel, 0);
            Grid.SetColumnSpan(NowPlayingSidebarPanel, 2);
            Canvas.SetZIndex(NowPlayingSidebarPanel, 20);
            MainWorkspaceContainer.Padding = new Thickness(0);
            MainWorkspaceContainer.ColumnSpacing = 0;
            NowPlayingSidebarPanel.Visibility = Visibility.Visible;
            NowPlayingSidebarPanel.Translation = Vector3.Zero;
            NowPlayingSidebarPanel.Opacity = 1.0;

            if (ViewModel.IsFullscreenVideoMode && !string.IsNullOrWhiteSpace(ViewModel.CurrentMusicVideoStreamUri))
            {
                _ = ApplyFullscreenMusicVideoStateAsync(ViewModel.CurrentMusicVideoStreamUri, true);
            }
        }
        else
        {
            Grid.SetColumn(NowPlayingSidebarPanel, 1);
            Grid.SetColumnSpan(NowPlayingSidebarPanel, 1);
            Canvas.SetZIndex(NowPlayingSidebarPanel, 0);
            MainWorkspaceContainer.Padding = new Thickness(12, 0, 0, 0);

            bool showDesktopSidebar = ViewModel.IsNowPlayingSidebarVisible && ViewModel.IsWideLayout;
            WorkspaceRightSidebarColumn.Width = new GridLength(showDesktopSidebar ? 324.0 : 0.0, GridUnitType.Pixel);
            MainWorkspaceContainer.ColumnSpacing = 0.0;
            NowPlayingSidebarPanel.Visibility = showDesktopSidebar ? Visibility.Visible : Visibility.Collapsed;
            NowPlayingSidebarPanel.Translation = showDesktopSidebar ? Vector3.Zero : new Vector3(350.0f, 0.0f, 0.0f);
            NowPlayingSidebarPanel.Opacity = showDesktopSidebar ? 1.0 : 0.0;
        }

        LiquidGlassBackdropManager.NotifyViewportScrolled();
        RefreshTitleBarPassthroughRegions();
    }

    private async void OnNowPlayingSidebarVisibilityChanged(object? sender, bool isVisible)
    {
        if (ViewModel.IsNarrowNowPlayingOverlayOpen)
        {
            return;
        }
        int gen = ++_sidebarAnimGeneration;
        const double expandedWidth = 324.0;
        const double expandedSpacing = 16.0;

        double startWidth = WorkspaceRightSidebarColumn.Width.Value;
        double targetWidth = isVisible ? expandedWidth : 0.0;

        float startTransX = NowPlayingSidebarPanel.Translation.X;
        float targetTransX = isVisible ? 0.0f : 350.0f;

        double startOpacity = NowPlayingSidebarPanel.Opacity;
        double targetOpacity = isVisible ? 1.0 : 0.0;

        if (isVisible)
        {
            NowPlayingSidebarPanel.Visibility = Visibility.Visible;
        }

        const int steps = 22;
        for (int i = 1; i <= steps; i++)
        {
            if (gen != _sidebarAnimGeneration)
            {
                return;
            }

            double t = (double)i / steps;
            // Smooth cubic ease-out with subtle spring feel
            double ease = 1.0 - Math.Pow(1.0 - t, 3.0);

            double curW = Math.Max(0.0, startWidth + ((targetWidth - startWidth) * ease));
            float curX = (float)(startTransX + ((targetTransX - startTransX) * ease));
            double curOp = Math.Clamp(startOpacity + ((targetOpacity - startOpacity) * ease), 0.0, 1.0);
            double curSpacing = isVisible ? (expandedSpacing * ease) : (expandedSpacing * (1.0 - ease));

            WorkspaceRightSidebarColumn.Width = new GridLength(curW, GridUnitType.Pixel);
            LibraryOverlayRightSidebarColumn.Width = new GridLength(curW, GridUnitType.Pixel);
            if (LikedOverlayRightSidebarColumn is not null)
            {
                LikedOverlayRightSidebarColumn.Width = new GridLength(curW, GridUnitType.Pixel);
            }
            // Keep at least 156px reserved on the right of the top header so buttons never overlap Windows caption (- □ ×) buttons
            HeaderRightSidebarColumn.Width = new GridLength(Math.Max(156.0, curW), GridUnitType.Pixel);
            MainWorkspaceContainer.ColumnSpacing = 0.0;
            LibraryFloatingOverlayGrid.ColumnSpacing = 0.0;
            if (LikedFloatingOverlayGrid is not null)
            {
                LikedFloatingOverlayGrid.ColumnSpacing = 0.0;
            }
            FloatingTopHeaderOverlay.ColumnSpacing = curSpacing;

            NowPlayingSidebarPanel.Translation = new Vector3(curX, 0.0f, 0.0f);
            NowPlayingSidebarPanel.Opacity = curOp;

            await Task.Delay(12);
        }

        if (gen == _sidebarAnimGeneration)
        {
            WorkspaceRightSidebarColumn.Width = new GridLength(targetWidth, GridUnitType.Pixel);
            LibraryOverlayRightSidebarColumn.Width = new GridLength(targetWidth, GridUnitType.Pixel);
            if (LikedOverlayRightSidebarColumn is not null)
            {
                LikedOverlayRightSidebarColumn.Width = new GridLength(targetWidth, GridUnitType.Pixel);
            }
            HeaderRightSidebarColumn.Width = new GridLength(Math.Max(156.0, targetWidth), GridUnitType.Pixel);
            MainWorkspaceContainer.ColumnSpacing = 0.0;
            LibraryFloatingOverlayGrid.ColumnSpacing = 0.0;
            if (LikedFloatingOverlayGrid is not null)
            {
                LikedFloatingOverlayGrid.ColumnSpacing = 0.0;
            }
            FloatingTopHeaderOverlay.ColumnSpacing = isVisible ? expandedSpacing : 0.0;
            NowPlayingSidebarPanel.Translation = new Vector3(targetTransX, 0.0f, 0.0f);
            NowPlayingSidebarPanel.Opacity = targetOpacity;

            if (!isVisible)
            {
                NowPlayingSidebarPanel.Visibility = Visibility.Collapsed;
            }

            RefreshTitleBarPassthroughRegions();

            UpdateLikedResponsiveLayout();
            SyncAllSlicers(animate: false);
        }
    }

    // =========================================================================
    // 2. LIQUID GLASS SEGMENTED SLICERS (Pill Grow + Spring Move + Glass Fade)
    //    & FLUID SECTION ENTRANCE TRANSITIONS
    // =========================================================================

    private readonly Dictionary<FrameworkElement, int> _slicerGenerations = new();
    private readonly Dictionary<FrameworkElement, FrameworkElement> _slicerTargetButtons = new();
    private readonly HashSet<FrameworkElement> _slicerAnimatingHosts = new();
    private bool _isNavSlicerVisible = true;
    private int _navSlicerOpacityGen;
    private double _navSlicerTargetOpacity = 1.0;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.CurrentSection):
                DispatcherQueue.TryEnqueue(async () =>
                {
                    SyncNavSlicer(animate: true);
                    _ = AnimateActiveSectionTransitionAsync();
                    UpdateLibraryHeaderScrimOpacity();
                    UpdateLikedHeaderScrimOpacity();
                    SyncLikedAlphabetRail();
                    UpdateLikedResponsiveLayout();
                    UpdateLikedRecommendationsShimmerState();
                    UpdatePlaylistRelatedShimmerState();
                    await Task.Delay(50);
                    SyncCategorySlicer(animate: false);
                    SyncSourceSlicer(animate: false);
                    SyncLikedFilterSlicer(animate: false);
                    SyncLikedSortSlicer(animate: false);
                    RefreshLibraryHorizontalCarousels();
                    LiquidGlassBackdropManager.NotifyViewportScrolled();
                });
                break;
            case nameof(ShellViewModel.ActiveSubView):
                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncNavSlicer(animate: true);
                    _ = AnimateActiveSectionTransitionAsync();
                    UpdatePlaylistRelatedShimmerState();
                    UpdateLikedRecommendationsShimmerState();
                    UpdateLikedHeaderScrimOpacity();
                });
                break;
            case nameof(ShellViewModel.IsSearchPageOpen):
                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncNavSlicer(animate: true);
                    _ = AnimateActiveSectionTransitionAsync();
                    UpdatePlaylistRelatedShimmerState();
                    UpdateLikedRecommendationsShimmerState();
                    LiquidGlassBackdropManager.NotifyViewportScrolled();
                });
                break;
            case nameof(ShellViewModel.SearchQuery):
            case nameof(ShellViewModel.IsSearchOnlineMode):
                DispatcherQueue.TryEnqueue(UpdateSearchPlaceholderCyclingState);
                break;
            case nameof(ShellViewModel.SelectedCategory):
                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncCategorySlicer(animate: true);
                    SyncLibraryAlphabetRail();
                    UpdateLibraryHeaderScrimOpacity();
                    RefreshLibraryHorizontalCarousels();
                });
                break;
            case nameof(ShellViewModel.SelectedSourceType):
                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncSourceSlicer(animate: true);
                    SyncLibraryAlphabetRail();
                    RefreshLibraryHorizontalCarousels();
                });
                break;
            case nameof(ShellViewModel.IsLibraryRecentlyAddedExpanded):
                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncLibraryAlphabetRail();
                    _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
                });
                break;
            case nameof(ShellViewModel.SelectedLikedFilter):
                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncLikedFilterSlicer(animate: true);
                    SyncLikedAlphabetRail();
                    UpdateLikedResponsiveLayout();
                    UpdateLikedHeaderScrimOpacity();
                    if (ViewModel.LikedViewVisibility == Visibility.Visible)
                    {
                        _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
                    }
                });
                break;
            case nameof(ShellViewModel.SelectedLikedSort):
                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncLikedSortSlicer(animate: true);
                    SyncLikedAlphabetRail();
                    UpdateLikedResponsiveLayout();
                    UpdateLikedHeaderScrimOpacity();
                    if (ViewModel.LikedViewVisibility == Visibility.Visible)
                    {
                        _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
                    }
                });
                break;
            case nameof(ShellViewModel.IsLyricsTabSelected):
                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncNowPlayingSlicer(animate: true);
                    UpdateSidebarEqualizerAnimationState();
                });
                break;

            case nameof(ShellViewModel.CurrentTrack):
                DispatcherQueue.TryEnqueue(() =>
                {
                    MiniPlayerBorderHost?.ResetForTrackChange();
                    CompactMiniPlayerBorderHost?.ResetForTrackChange();
                    _ = UpdateNowPlayingAmbientPaletteAsync();
                    _ = RefreshArtistPagePaletteForThemeAsync();
                    _ = RefreshAlbumPagePaletteForThemeAsync();
                    _ = RefreshPlaylistPagePaletteForThemeAsync();
                    SyncSidebarSquigglyProgress();
                    UpdateHomeGenrePillSelection();
                    UpdateLibraryHeaderScrimForTheme();
                    UpdateLikedHeaderScrimForTheme();
                    LibraryAlphabetRail?.RefreshVisuals();
                    LikedAlphabetRail?.RefreshVisuals();
                    UpdateLikedPlayAllButtonContrast();
                    SyncDataUsageChartAccent();
                });
                break;
            case nameof(ShellViewModel.IsLightMode):
                DispatcherQueue.TryEnqueue(() =>
                {
                    _ = UpdateNowPlayingAmbientPaletteAsync();
                    _ = RefreshArtistPagePaletteForThemeAsync();
                    _ = RefreshAlbumPagePaletteForThemeAsync();
                    _ = RefreshPlaylistPagePaletteForThemeAsync();
                    SyncSidebarSquigglyProgress();
                    UpdateHomeGenrePillSelection();
                    UpdateLibraryHeaderScrimForTheme();
                    UpdateLikedHeaderScrimForTheme();
                    LibraryAlphabetRail?.RefreshVisuals();
                    LikedAlphabetRail?.RefreshVisuals();
                    UpdateLikedPlayAllButtonContrast();
                });
                break;

            case nameof(ShellViewModel.IsArtistDetailLoading):
                DispatcherQueue.TryEnqueue(UpdateArtistSkeletonShimmerState);
                break;

            case nameof(ShellViewModel.IsAlbumDetailLoading):
                DispatcherQueue.TryEnqueue(UpdateAlbumSkeletonShimmerState);
                break;

            case nameof(ShellViewModel.IsPlaylistRelatedLoading):
                DispatcherQueue.TryEnqueue(UpdatePlaylistRelatedShimmerState);
                break;

            case nameof(ShellViewModel.IsLoadingLikedRecommendations):
                DispatcherQueue.TryEnqueue(UpdateLikedRecommendationsShimmerState);
                break;

            case nameof(ShellViewModel.CurrentPositionSeconds):
            case nameof(ShellViewModel.TotalDurationSeconds):
                DispatcherQueue.TryEnqueue(() =>
                {
                    SyncSidebarSquigglyProgress();
                    SyncFullscreenMusicVideoPositionAndPlayState();
                });
                break;

            case nameof(ShellViewModel.LikeButtonGlyph):
                DispatcherQueue.TryEnqueue(() => UpdateSidebarAccentTintElements(_currentAmbientPalette.VibrantAccent));
                break;

            case nameof(ShellViewModel.IsPlaying):
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateSidebarEqualizerAnimationState();
                    SyncFullscreenMusicVideoPositionAndPlayState();
                });
                break;

            case nameof(ShellViewModel.IsNowPlayingSidebarVisible):
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateSidebarEqualizerAnimationState();
                    UpdateResponsiveNavBarMode();
                    SyncSidebarSquigglyProgress();
                    RefreshTitleBarPassthroughRegions();
                });
                break;

            case nameof(ShellViewModel.IsFullscreenNowPlayingOpen):
            case nameof(ShellViewModel.MiniPlayerHeaderVisibility):
            case nameof(ShellViewModel.IsCurrentTrackMusicVideoAvailable):
                DispatcherQueue.TryEnqueue(RefreshTitleBarPassthroughRegions);
                break;

            case nameof(ShellViewModel.HomeAuroraVisibility):
                UpdateAuroraAnimationState();
                break;

            case nameof(ShellViewModel.SelectedHomeGenre):
                DispatcherQueue.TryEnqueue(UpdateHomeGenrePillSelection);
                break;
        }
    }

    /// <summary>
    /// Plays a modern fluid entrance transition (smooth vertical glide + fade-in)
    /// whenever the user switches between sections or opens an Artist / Album / Playlist / Settings screen.
    /// Uses lightweight GPU Composition transitions instead of a multi-frame CPU loop so switching to
    /// large sections (like Library or Home) never drops FPS.
    /// </summary>
    private async Task AnimateActiveSectionTransitionAsync()
    {
        int gen = ++_sectionTransitionGeneration;
        bool isArtistDetail = ViewModel.ActiveSubView == WorkspaceSubView.ArtistDetail;

        if (!isArtistDetail)
        {
            PauseArtistHeaderVideoOnLeaveCore();
        }

        FrameworkElement? activeRoot = ViewModel.IsSearchPageOpen
            ? SearchSectionRoot
            : ViewModel.ActiveSubView switch
            {
                WorkspaceSubView.ArtistDetail => ArtistDetailSectionRoot,
                WorkspaceSubView.AlbumDetail => AlbumDetailSectionRoot,
                WorkspaceSubView.PlaylistDetail => PlaylistDetailSectionRoot,
                WorkspaceSubView.Settings => SettingsSectionRoot,
                WorkspaceSubView.HomeCategoryGrid => HomeCategoryGridSectionRoot,
                _ => ViewModel.CurrentSection switch
                {
                    NavigationSection.Home => HomeSectionRoot,
                    NavigationSection.Liked => LikedSectionRoot,
                    NavigationSection.Playlists => PlaylistsSectionRoot,
                    _ => LibrarySectionRoot
                }
            };

        if (activeRoot is null)
        {
            return;
        }

        _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: true);

        activeRoot.Opacity = 0.0;
        activeRoot.Translation = new Vector3(0.0f, 14.0f, 0.0f);

        await Task.Delay(14);
        if (gen != _sectionTransitionGeneration)
        {
            return;
        }

        const int steps = 8;
        for (int i = 1; i <= steps; i++)
        {
            if (gen != _sectionTransitionGeneration)
            {
                return;
            }

            double t = (double)i / steps;
            double ease = 1.0 - Math.Pow(1.0 - t, 3.0);

            activeRoot.Opacity = Math.Clamp(ease * 1.10, 0.0, 1.0);
            float ty = (float)(14.0 * (1.0 - ease));
            activeRoot.Translation = new Vector3(0.0f, ty, 0.0f);

            await Task.Delay(11);
        }

        if (gen == _sectionTransitionGeneration)
        {
            activeRoot.Opacity = 1.0;
            activeRoot.Translation = Vector3.Zero;
        }
    }

    private void SyncAllSlicers(bool animate)
    {
        SyncNavSlicer(animate);
        SyncCategorySlicer(animate);
        SyncSourceSlicer(animate);
        SyncLikedFilterSlicer(animate);
        SyncLikedSortSlicer(animate);
        SyncNowPlayingSlicer(animate);
    }

    private bool IsTopNavTabActive() =>
        !ViewModel.IsSearchPageOpen &&
        ViewModel.ActiveSubView == WorkspaceSubView.MainSection &&
        ViewModel.CurrentSection is NavigationSection.Home
            or NavigationSection.Library
            or NavigationSection.Liked
            or NavigationSection.Playlists;

    private Button GetNavTabButtonForSection(NavigationSection section) => section switch
    {
        NavigationSection.Home => NavBtnHome,
        NavigationSection.Library => NavBtnLibrary,
        NavigationSection.Liked => NavBtnLiked,
        NavigationSection.Playlists => NavBtnPlaylists,
        _ => NavBtnHome
    };

    private void OnNavSlicerContainerLoaded(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveNavBarMode();
        SyncNavSlicer(animate: false);
        DispatcherQueue.TryEnqueue(() => SyncNavSlicer(animate: false));
    }

    private void OnNavSlicerContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (NavSlicerIndicatorHost is null || e.NewSize.Width <= 0)
        {
            return;
        }

        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5 ||
            Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5 ||
            NavSlicerIndicatorHost.Width < 2.0)
        {
            SyncNavSlicer(animate: false);
        }
    }

    private async Task SetNavSlicerIndicatorOpacityAsync(double targetOpacity, bool animate)
    {
        if (NavSlicerIndicatorHost is null)
        {
            return;
        }

        double startOpacity = NavSlicerIndicatorHost.Opacity;
        if (animate && Math.Abs(_navSlicerTargetOpacity - targetOpacity) < 0.01 && Math.Abs(startOpacity - targetOpacity) > 0.01)
        {
            // Already animating toward the same target opacity
            return;
        }

        _navSlicerTargetOpacity = targetOpacity;
        int gen = ++_navSlicerOpacityGen;
        if (!animate || Math.Abs(targetOpacity - startOpacity) < 0.01)
        {
            NavSlicerIndicatorHost.Opacity = targetOpacity;
            return;
        }

        const int steps = 10;
        for (int i = 1; i <= steps; i++)
        {
            if (gen != _navSlicerOpacityGen || NavSlicerIndicatorHost is null)
            {
                return;
            }

            double t = (double)i / steps;
            double ease = 1.0 - Math.Pow(1.0 - t, 3.0);
            NavSlicerIndicatorHost.Opacity = startOpacity + ((targetOpacity - startOpacity) * ease);
            await Task.Delay(14);
        }

        if (gen == _navSlicerOpacityGen && NavSlicerIndicatorHost is not null)
        {
            NavSlicerIndicatorHost.Opacity = targetOpacity;
        }
    }

    private void OnNavSlicerButtonClicked(object sender, RoutedEventArgs e)
    {
        if (NavSlicerIndicatorHost is null || NavSlicerContainer is null || NavSlicerLiquidGlass is null)
        {
            return;
        }

        if (sender is Button targetBtn)
        {
            bool wasHidden = !_isNavSlicerVisible ||
                             NavSlicerIndicatorHost.Opacity < 0.05 ||
                             NavSlicerIndicatorHost.Width < 2.0;
            _isNavSlicerVisible = true;

            // Glide with full LiquidGlass lens animation when switching between visible tabs;
            // snap directly to exact bounds and fade in smoothly when returning from a hidden state (Search/Settings/Detail).
            _ = AnimateSlicerIndicatorAsync(NavSlicerContainer, NavSlicerIndicatorHost, NavSlicerLiquidGlass, targetBtn, animate: !wasHidden);
            _ = SetNavSlicerIndicatorOpacityAsync(1.0, animate: wasHidden);
        }
    }

    private void OnCategorySlicerButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button targetBtn)
        {
            _ = AnimateSlicerIndicatorAsync(CategorySlicerContainer, CategorySlicerIndicatorHost, CategorySlicerLiquidGlass, targetBtn, animate: true);
        }
    }

    private void OnSourceSlicerButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button targetBtn)
        {
            _ = AnimateSlicerIndicatorAsync(SourceSlicerContainer, SourceSlicerIndicatorHost, SourceSlicerLiquidGlass, targetBtn, animate: true);
        }
    }

    private void OnLikedFilterSlicerButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button targetBtn)
        {
            _ = AnimateSlicerIndicatorAsync(LikedFilterSlicerContainer, LikedFilterSlicerIndicatorHost, LikedFilterSlicerLiquidGlass, targetBtn, animate: true);
        }
    }

    private void OnLikedSortSlicerButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button targetBtn)
        {
            _ = AnimateSlicerIndicatorAsync(LikedSortSlicerContainer, LikedSortSlicerIndicatorHost, LikedSortSlicerLiquidGlass, targetBtn, animate: true);
        }
    }

    private void OnNowPlayingSlicerButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button targetBtn)
        {
            _ = AnimateSlicerIndicatorAsync(NowPlayingSlicerContainer, NowPlayingSlicerIndicatorHost, NowPlayingSlicerLiquidGlass, targetBtn, animate: true);
        }
    }

    private void SyncNavSlicer(bool animate)
    {
        if (NavSlicerIndicatorHost is null || NavSlicerContainer is null || NavSlicerLiquidGlass is null)
        {
            return;
        }

        if (!IsTopNavTabActive())
        {
            _isNavSlicerVisible = false;
            _slicerGenerations[NavSlicerIndicatorHost] = _slicerGenerations.GetValueOrDefault(NavSlicerIndicatorHost) + 1;
            _slicerAnimatingHosts.Remove(NavSlicerIndicatorHost);
            _slicerTargetButtons.Remove(NavSlicerIndicatorHost);
            NavSlicerLiquidGlass.Opacity = 0.0;
            _ = SetNavSlicerIndicatorOpacityAsync(0.0, animate);
            return;
        }

        Button target = GetNavTabButtonForSection(ViewModel.CurrentSection);
        bool wasHidden = !_isNavSlicerVisible ||
                         NavSlicerIndicatorHost.Opacity < 0.05 ||
                         NavSlicerIndicatorHost.Width < 2.0;
        _isNavSlicerVisible = true;

        bool shouldAnimateSlide = animate && !wasHidden;
        _ = AnimateSlicerIndicatorAsync(NavSlicerContainer, NavSlicerIndicatorHost, NavSlicerLiquidGlass, target, shouldAnimateSlide);
        _ = SetNavSlicerIndicatorOpacityAsync(1.0, animate: animate && wasHidden);
    }

    private void SyncCategorySlicer(bool animate)
    {
        Button target = ViewModel.SelectedCategory switch
        {
            SearchCategoryFilter.All => CatBtnAll,
            SearchCategoryFilter.Artists => CatBtnArtists,
            SearchCategoryFilter.Albums => CatBtnAlbums,
            SearchCategoryFilter.Songs => CatBtnSongs,
            _ => CatBtnAll
        };
        _ = AnimateSlicerIndicatorAsync(CategorySlicerContainer, CategorySlicerIndicatorHost, CategorySlicerLiquidGlass, target, animate);
    }

    private void SyncSourceSlicer(bool animate)
    {
        Button target = ViewModel.SelectedSourceType switch
        {
            MediaSourceType.Local => SrcBtnLocal,
            MediaSourceType.Online => SrcBtnOnline,
            _ => SrcBtnAll
        };
        _ = AnimateSlicerIndicatorAsync(SourceSlicerContainer, SourceSlicerIndicatorHost, SourceSlicerLiquidGlass, target, animate);
    }

    private void SyncLikedFilterSlicer(bool animate)
    {
        if (LikedFilterSlicerContainer is null || LikedBtnFilterSongs is null)
        {
            return;
        }

        Button target = ViewModel.SelectedLikedFilter switch
        {
            LikedFilterTab.Artists => LikedBtnFilterArtists,
            LikedFilterTab.Albums => LikedBtnFilterAlbums,
            _ => LikedBtnFilterSongs
        };
        _ = AnimateSlicerIndicatorAsync(LikedFilterSlicerContainer, LikedFilterSlicerIndicatorHost, LikedFilterSlicerLiquidGlass, target, animate);
    }

    private void SyncLikedSortSlicer(bool animate)
    {
        if (LikedSortSlicerContainer is null || LikedBtnSortRecent is null)
        {
            return;
        }

        Button target = ViewModel.SelectedLikedSort == LikedSortMode.Alphabetical
            ? LikedBtnSortAlpha
            : LikedBtnSortRecent;
        _ = AnimateSlicerIndicatorAsync(LikedSortSlicerContainer, LikedSortSlicerIndicatorHost, LikedSortSlicerLiquidGlass, target, animate);
    }

    private void SyncNowPlayingSlicer(bool animate)
    {
        Button target = ViewModel.IsLyricsTabSelected ? NpBtnLyrics : NpBtnQueue;
        _ = AnimateSlicerIndicatorAsync(NowPlayingSlicerContainer, NowPlayingSlicerIndicatorHost, NowPlayingSlicerLiquidGlass, target, animate);
    }

    private static Microsoft.UI.Xaml.Media.CompositeTransform EnsureSlicerTransform(FrameworkElement indicatorHost)
    {
        indicatorHost.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        if (indicatorHost.RenderTransform is Microsoft.UI.Xaml.Media.CompositeTransform existing)
        {
            return existing;
        }

        var created = new Microsoft.UI.Xaml.Media.CompositeTransform();
        indicatorHost.RenderTransform = created;
        return created;
    }

    private static Border? GetSlicerBaseBorder(FrameworkElement indicatorHost)
    {
        if (indicatorHost is Panel panel && panel.Children.Count > 0 && panel.Children[0] is Border border)
        {
            return border;
        }

        return null;
    }

    /// <summary>
    /// Animates a segmented slicer pill indicator in 3 distinct stages:
    /// 1. Grows in size and transforms from the resting semi-transparent pill into a live <see cref="Controls.LiquidGlassPanel"/> lens.
    /// 2. Glides smoothly with spring physics toward the selected tab while refracting the background in real time.
    /// 3. Shrinks back to normal size and crossfades back to the resting semi-transparent element (without LiquidGlass).
    /// Uses pure XAML <see cref="Microsoft.UI.Xaml.Media.CompositeTransform"/> to avoid WinUI 3 Composition facade conflicts.
    /// </summary>
    private async Task AnimateSlicerIndicatorAsync(
        FrameworkElement container,
        FrameworkElement indicatorHost,
        Controls.LiquidGlassPanel glassOverlay,
        FrameworkElement targetButton,
        bool animate)
    {
        if (container is null || indicatorHost is null || glassOverlay is null || targetButton is null)
        {
            return;
        }

        if (targetButton.ActualWidth <= 0 || container.ActualWidth <= 0)
        {
            return;
        }

        Windows.Foundation.Point targetPt;
        try
        {
            var transform = targetButton.TransformToVisual(container);
            targetPt = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        }
        catch
        {
            return;
        }

        double targetX = targetPt.X;
        double targetW = Math.Max(28.0, targetButton.ActualWidth);
        var xform = EnsureSlicerTransform(indicatorHost);
        var baseBorder = GetSlicerBaseBorder(indicatorHost);

        if (!animate)
        {
            // Cancel any in-flight glide so resize/layout snaps are never overwritten by a stale coroutine
            _slicerGenerations[indicatorHost] = _slicerGenerations.GetValueOrDefault(indicatorHost) + 1;
            _slicerAnimatingHosts.Remove(indicatorHost);
            _slicerTargetButtons[indicatorHost] = targetButton;
            indicatorHost.Width = targetW;
            xform.TranslateX = targetX;
            xform.ScaleX = 1.0;
            xform.ScaleY = 1.0;
            if (baseBorder is not null)
            {
                baseBorder.Opacity = 1.0;
            }

            glassOverlay.Opacity = 0.0;
            return;
        }

        // Prevent duplicate PropertyChanged triggers from restarting an animation already moving toward the same button
        if (_slicerTargetButtons.TryGetValue(indicatorHost, out var activeTarget) && ReferenceEquals(activeTarget, targetButton))
        {
            if (_slicerAnimatingHosts.Contains(indicatorHost))
            {
                return;
            }

            // Same resting target: still reconcile exact real bounds if layout/compact mode/localization changed size
            if (Math.Abs(indicatorHost.Width - targetW) > 0.5 || Math.Abs(xform.TranslateX - targetX) > 0.5)
            {
                indicatorHost.Width = targetW;
                xform.TranslateX = targetX;
            }

            return;
        }

        double startX = xform.TranslateX;
        double startW = indicatorHost.ActualWidth > 1.0
            ? indicatorHost.ActualWidth
            : (!double.IsNaN(indicatorHost.Width) && indicatorHost.Width > 1.0 ? indicatorHost.Width : targetW);

        if (Math.Abs(targetX - startX) < 1.0 && Math.Abs(targetW - startW) < 1.0)
        {
            _slicerGenerations[indicatorHost] = _slicerGenerations.GetValueOrDefault(indicatorHost) + 1;
            _slicerAnimatingHosts.Remove(indicatorHost);
            _slicerTargetButtons[indicatorHost] = targetButton;
            indicatorHost.Width = targetW;
            xform.TranslateX = targetX;
            xform.ScaleX = 1.0;
            xform.ScaleY = 1.0;
            if (baseBorder is not null)
            {
                baseBorder.Opacity = 1.0;
            }

            glassOverlay.Opacity = 0.0;
            return;
        }

        _slicerTargetButtons[indicatorHost] = targetButton;
        int gen = _slicerGenerations.TryGetValue(indicatorHost, out int prev) ? prev + 1 : 1;
        _slicerGenerations[indicatorHost] = gen;
        _slicerAnimatingHosts.Add(indicatorHost);

        try
        {
            // Stage 1: Swell organically in size and morph from resting glass puck into live refractive LiquidGlassPanel lens
            const int morphInSteps = 5;
            double initialScaleX = xform.ScaleX;
            double initialScaleY = xform.ScaleY;
            const double activeScaleX = 1.05;
            const double activeScaleY = 1.08;

            for (int i = 1; i <= morphInSteps; i++)
            {
                if (_slicerGenerations.GetValueOrDefault(indicatorHost) != gen)
                {
                    return;
                }

                double t = (double)i / morphInSteps;
                double ease = 1.0 - Math.Pow(1.0 - t, 2.4);

                xform.ScaleX = initialScaleX + ((activeScaleX - initialScaleX) * ease);
                xform.ScaleY = initialScaleY + ((activeScaleY - initialScaleY) * ease);

                if (baseBorder is not null)
                {
                    baseBorder.Opacity = Math.Max(0.08, 1.0 - (0.92 * ease));
                }

                glassOverlay.Opacity = Math.Clamp(ease, 0.0, 1.0);
                glassOverlay.InvalidateGlass();

                await Task.Delay(12);
            }

            // Stage 2: Smoothly glide with spring physics toward the selected tab as a live LiquidGlass lens
            double curX = startX;
            double velX = 0.0;
            double curW = startW;
            double velW = 0.0;

            const double stiffness = 0.14;
            const double damping = 0.78;

            for (int step = 0; step < 44; step++)
            {
                if (_slicerGenerations.GetValueOrDefault(indicatorHost) != gen)
                {
                    return;
                }

                double dispX = targetX - curX;
                velX = (velX + (dispX * stiffness)) * damping;
                curX += velX;

                double dispW = targetW - curW;
                velW = (velW + (dispW * 0.22)) * 0.76;
                curW += velW;

                indicatorHost.Width = Math.Max(28.0, curW);
                xform.TranslateX = curX;

                double speedBoost = Math.Clamp(Math.Abs(velX) / 9.0, 0.0, 1.0);
                xform.ScaleX = activeScaleX + (0.04 * speedBoost);
                xform.ScaleY = activeScaleY + (0.06 * speedBoost);

                if (baseBorder is not null)
                {
                    baseBorder.Opacity = 0.08;
                }

                glassOverlay.Opacity = 1.0;
                glassOverlay.InvalidateGlass();

                if (step > 12 && Math.Abs(dispX) < 0.35 && Math.Abs(velX) < 0.35 && Math.Abs(dispW) < 0.35)
                {
                    break;
                }

                await Task.Delay(14);
            }

            if (_slicerGenerations.GetValueOrDefault(indicatorHost) != gen)
            {
                return;
            }

            indicatorHost.Width = targetW;
            xform.TranslateX = targetX;

            // Stage 3: Shrink back to normal size (1.0) and return to the semi-transparent pill (without LiquidGlass)
            const int settleSteps = 14;
            double settleStartScaleX = xform.ScaleX;
            double settleStartScaleY = xform.ScaleY;
            double startGlassOp = glassOverlay.Opacity;
            double startBorderOp = baseBorder?.Opacity ?? 0.08;

            for (int i = 1; i <= settleSteps; i++)
            {
                if (_slicerGenerations.GetValueOrDefault(indicatorHost) != gen)
                {
                    return;
                }

                double t = (double)i / settleSteps;
                double ease = 1.0 - Math.Pow(1.0 - t, 2.6);

                xform.ScaleX = settleStartScaleX + ((1.0 - settleStartScaleX) * ease);
                xform.ScaleY = settleStartScaleY + ((1.0 - settleStartScaleY) * ease);

                if (baseBorder is not null)
                {
                    baseBorder.Opacity = Math.Clamp(startBorderOp + ((1.0 - startBorderOp) * ease), 0.0, 1.0);
                }

                glassOverlay.Opacity = Math.Max(0.0, startGlassOp * (1.0 - ease));

                await Task.Delay(13);
            }

            if (_slicerGenerations.GetValueOrDefault(indicatorHost) == gen)
            {
                xform.ScaleX = 1.0;
                xform.ScaleY = 1.0;
                if (baseBorder is not null)
                {
                    baseBorder.Opacity = 1.0;
                }

                glassOverlay.Opacity = 0.0;
            }
        }
        finally
        {
            if (_slicerGenerations.GetValueOrDefault(indicatorHost) == gen)
            {
                _slicerAnimatingHosts.Remove(indicatorHost);
            }
        }
    }

    // =========================================================================
    // 3. SONG LIST LIQUID GLASS CONTEXT MENU (Right-Click & '...' Button)
    // =========================================================================

    private int _contextMenuAnimGen;

    private void OnTrackRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            var clickPoint = e.GetPosition(RootLayoutGrid);
            ShowTrackContextMenuAtPoint(track, clickPoint);
            e.Handled = true;
        }
    }

    private void OnTrackRowMoreClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        if (sender is FrameworkElement { Tag: TrackItem track } button)
        {
            try
            {
                var transform = button.TransformToVisual(RootLayoutGrid);
                var pt = transform.TransformPoint(new Windows.Foundation.Point(0, button.ActualHeight + 4));
                ShowTrackContextMenuAtPoint(track, pt);
            }
            catch
            {
                ShowTrackContextMenuAtPoint(track, new Windows.Foundation.Point(340, 220));
            }
        }
    }

    /// <summary>
    /// Positions the LiquidGlass song context menu at the click point and animates it growing
    /// from the top-left corner (<c>CenterPoint = (0,0,0)</c>) with a fluid spring bounce.
    /// </summary>
    private async void ShowTrackContextMenuAtPoint(TrackItem track, Windows.Foundation.Point clickPoint)
    {
        int gen = ++_contextMenuAnimGen;

        const double menuWidth = 236.0;
        const double menuHeight = 408.0;

        double maxX = Math.Max(24.0, RootLayoutGrid.ActualWidth - menuWidth - 18.0);
        double maxY = Math.Max(24.0, RootLayoutGrid.ActualHeight - menuHeight - 18.0);

        double left = Math.Clamp(clickPoint.X, 16.0, maxX);
        double top = Math.Clamp(clickPoint.Y, 16.0, maxY);

        // Open first so x:Load realizes TrackContextMenuOverlay and TrackContextMenuPopupHost before positioning.
        ViewModel.OpenTrackContextMenu(track);
        if (TrackContextMenuPopupHost is null)
        {
            _ = FindName(nameof(TrackContextMenuOverlay));
        }

        var popupHost = TrackContextMenuPopupHost;
        if (popupHost is null)
        {
            return;
        }

        Canvas.SetLeft(popupHost, left);
        Canvas.SetTop(popupHost, top);

        // Top-left origin so the menu expands downward and to the right from the exact click point
        popupHost.CenterPoint = new Vector3(0.0f, 0.0f, 0.0f);
        popupHost.Scale = new Vector3(0.16f, 0.16f, 1.0f);
        popupHost.Opacity = 0.0;

        TrackContextMenuGlassPanel?.InvalidateGlass();

        double scale = 0.16;
        double vel = 0.0;
        const double targetScale = 1.0;
        const double stiffness = 0.28;
        const double damping = 0.68;

        for (int step = 0; step < 26; step++)
        {
            if (gen != _contextMenuAnimGen || !ViewModel.IsTrackContextMenuOpen || TrackContextMenuPopupHost is null)
            {
                return;
            }

            double disp = targetScale - scale;
            vel = (vel + (disp * stiffness)) * damping;
            scale += vel;

            float s = (float)Math.Max(0.10, scale);
            TrackContextMenuPopupHost.Scale = new Vector3(s, s, 1.0f);
            TrackContextMenuPopupHost.Opacity = Math.Clamp((scale - 0.16) / 0.55, 0.0, 1.0);

            if (step > 8 && Math.Abs(disp) < 0.008 && Math.Abs(vel) < 0.008)
            {
                break;
            }

            await Task.Delay(11);
        }

        if (gen == _contextMenuAnimGen && ViewModel.IsTrackContextMenuOpen && TrackContextMenuPopupHost is not null)
        {
            TrackContextMenuPopupHost.Scale = Vector3.One;
            TrackContextMenuPopupHost.Opacity = 1.0;
            TrackContextMenuGlassPanel?.InvalidateGlass();
        }
    }

    private void OnContextMenuBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.CloseTrackContextMenuCommand.Execute(null);
        e.Handled = true;
    }

    private void OnContextMenuBackdropRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        ViewModel.CloseTrackContextMenuCommand.Execute(null);
        e.Handled = true;
    }

    private void OnContextMenuCardTapped(object sender, TappedRoutedEventArgs e)
    {
        // Prevent clicks inside the context menu card from bubbling up to the backdrop dismiss handler
        e.Handled = true;
    }

    private void OnTrackInfoModalBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.CloseTrackInfoModalCommand.Execute(null);
        e.Handled = true;
    }

    private void OnAddToPlaylistModalBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.CloseAddToPlaylistModalCommand.Execute(null);
        e.Handled = true;
    }

    private void OnDownloadCandidatesModalBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.CloseDownloadOptionsModalCommand.Execute(null);
        e.Handled = true;
    }

    private void OnModalCardTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnDownloadBarTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.ToggleDownloadQueueFlyoutCommand.Execute(null);
        e.Handled = true;
    }

    private void OnCandidateDownloadButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Xperience.Core.Domain.Models.TrackDownloadCandidate candidate })
        {
            ViewModel.DownloadCandidateCommand.Execute(candidate);
        }
    }

    private void OnQueueItemPlayClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DownloadQueueItemViewModel item })
        {
            ViewModel.PlayDownloadedQueueItemCommand.Execute(item);
        }
    }

    private void OnSidebarCurrentTrackMoreClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentTrack is null || ViewModel.CurrentTrack.Id == "empty_state_track")
        {
            return;
        }

        if (sender is FrameworkElement button)
        {
            try
            {
                var transform = button.TransformToVisual(RootLayoutGrid);
                var pt = transform.TransformPoint(new Windows.Foundation.Point(-180, button.ActualHeight + 6));
                ShowTrackContextMenuAtPoint(ViewModel.CurrentTrack, pt);
                return;
            }
            catch
            {
                // Fallback below
            }
        }

        ShowTrackContextMenuAtPoint(ViewModel.CurrentTrack, new Windows.Foundation.Point(RootLayoutGrid.ActualWidth - 260, 120));
    }

    private void OnFullscreenCurrentTrackMoreClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentTrack is null || ViewModel.CurrentTrack.Id == "empty_state_track")
        {
            return;
        }

        if (sender is FrameworkElement button)
        {
            try
            {
                var transform = button.TransformToVisual(RootLayoutGrid);
                var pt = transform.TransformPoint(new Windows.Foundation.Point(-180, button.ActualHeight + 8));
                ShowTrackContextMenuAtPoint(ViewModel.CurrentTrack, pt);
                return;
            }
            catch
            {
                // Fallback below
            }
        }

        ShowTrackContextMenuAtPoint(
            ViewModel.CurrentTrack,
            new Windows.Foundation.Point(RootLayoutGrid.ActualWidth - 260, 96));
    }

    // =========================================================================
    // 4. PLAYLISTS, SETTINGS, SIDEBAR & FULLSCREEN MICROINTERACTIONS
    // =========================================================================

    private async void OnPlaylistCardClicked(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _lastRowSubButtonClickTick < 260)
        {
            return;
        }

        UserPlaylist? playlist = sender switch
        {
            FrameworkElement { Tag: PlaylistCardViewModel card } => card.Playlist,
            FrameworkElement { Tag: UserPlaylist pl } => pl,
            _ => null
        };

        if (playlist is not null)
        {
            _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
            await ViewModel.OpenPlaylistDetailAsync(playlist);
        }
    }

    private async void OnPlaylistCardQuickPlayClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        UserPlaylist? playlist = sender switch
        {
            FrameworkElement { Tag: UserPlaylist pl } => pl,
            FrameworkElement { Tag: PlaylistCardViewModel card } => card.Playlist,
            _ => null
        };

        if (playlist is not null)
        {
            await ViewModel.PlayPlaylistDirectlyAsync(playlist);
        }
    }

    private async void OnPlaylistDetailTrackTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ShouldSuppressRowTap(sender, e))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayTrackFromListAsync(track, ViewModel.PlaylistDetailTracks);
        }
    }

    private async void OnPlaylistRelatedTrackClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.PlayPlaylistRelatedTrackAsync(track);
        }
    }

    // =========================================================================
    // MINI PLAYER PERIMETER PROGRESS BORDER & DEDICATED SEARCH INTERACTIONS
    // =========================================================================

    private void OnMiniPlayerClicked(object? sender, EventArgs e)
    {
        if (ViewModel.IsMediumOrCompactLayout)
        {
            ViewModel.OpenCompactNowPlayingCommand.Execute(null);
            return;
        }

        if (!ViewModel.IsNowPlayingSidebarVisible)
        {
            ViewModel.ToggleNowPlayingSidebarCommand.Execute(null);
        }
    }

    private void OnMiniPlayerSeekRequested(object? sender, double progress)
    {
        if (ViewModel.TotalDurationSeconds <= 1.0)
        {
            return;
        }

        double targetSeconds = Math.Clamp(progress, 0.0, 1.0) * ViewModel.TotalDurationSeconds;
        ViewModel.SeekToSeconds(targetSeconds);
    }

    private async void OnRecentSearchChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string query } && !string.IsNullOrWhiteSpace(query))
        {
            await ViewModel.ApplyRecentSearchAsync(query);
        }
    }

    private async void OnRecentSearchRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string query } && !string.IsNullOrWhiteSpace(query))
        {
            await ViewModel.RemoveRecentSearchAsync(query);
        }
    }

    private async void OnMoodGenreTileClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MoodGenreTileViewModel tile })
        {
            await ViewModel.SelectMoodGenreCategoryAsync(tile);
        }
    }

    private async void OnSearchArtistCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ArtistItem artist })
        {
            await ViewModel.RecordActiveSearchQueryInHistoryAsync();
            _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
            await ViewModel.OpenArtistDetailAsync(artist);
        }
    }

    private async void OnSearchAlbumCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AlbumItem album })
        {
            await ViewModel.RecordActiveSearchQueryInHistoryAsync();
            _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
            await ViewModel.OpenAlbumDetailAsync(album);
        }
    }

    private async void OnSearchPlaylistCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RecommendedPlaylistItem playlist })
        {
            await ViewModel.RecordActiveSearchQueryInHistoryAsync();
            await OpenOrPlayRecommendedPlaylistAsync(playlist);
        }
    }

    private async Task OpenOrPlayRecommendedPlaylistAsync(RecommendedPlaylistItem playlist)
    {
        if (string.Equals(playlist.Author, "Your Playlist", StringComparison.OrdinalIgnoreCase))
        {
            var localMatch = ViewModel.Playlists.FirstOrDefault(p =>
                string.Equals(p.Id, playlist.PlaylistId, StringComparison.OrdinalIgnoreCase));
            if (localMatch is not null)
            {
                _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
                await ViewModel.OpenPlaylistDetailAsync(localMatch);
                return;
            }
        }

        await ViewModel.PlayRecommendedPlaylistAsync(playlist);
    }

    private async void OnRemovePlaylistTrackClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        if (sender is FrameworkElement { Tag: TrackItem track })
        {
            await ViewModel.RemoveTrackFromCurrentPlaylistAsync(track);
        }
    }

    private async void OnAddToPlaylistItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UserPlaylist playlist })
        {
            await ViewModel.AddPendingTrackToPlaylistAsync(playlist);
        }
    }

    private async void OnRemoveLocalDirectoryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string folderPath } && !string.IsNullOrWhiteSpace(folderPath))
        {
            await ViewModel.RemoveLocalDirectoryAsync(folderPath);
        }
    }

    private int _nowPlayingTabAnimGen;

    private async void OnNowPlayingTabTransitionRequested(object? sender, NowPlayingTab tab)
    {
        int gen = ++_nowPlayingTabAnimGen;
        bool isLyricsTab = tab == NowPlayingTab.Lyrics;
        FrameworkElement activePanel = isLyricsTab
            ? (FrameworkElement)NowPlayingLyricsPanelRoot
            : NowPlayingQueuePanelRoot;

        if (activePanel is null)
        {
            return;
        }

        float startX = isLyricsTab ? 22.0f : -22.0f;
        float startY = 10.0f;

        activePanel.Opacity = 0.0;
        activePanel.Translation = new Vector3(startX, startY, 0.0f);

        const int steps = 12;
        for (int i = 1; i <= steps; i++)
        {
            if (gen != _nowPlayingTabAnimGen)
            {
                return;
            }

            double t = (double)i / steps;
            double ease = 1.0 - Math.Pow(1.0 - t, 3.0);

            activePanel.Opacity = Math.Clamp(ease * 1.08, 0.0, 1.0);
            activePanel.Translation = new Vector3(
                (float)(startX * (1.0 - ease)),
                (float)(startY * (1.0 - ease)),
                0.0f);

            await Task.Delay(12);
        }

        if (gen == _nowPlayingTabAnimGen)
        {
            activePanel.Opacity = 1.0;
            activePanel.Translation = Vector3.Zero;
        }

        if (isLyricsTab)
        {
            await Task.Delay(40);
            SidebarSynchronizedLyricsControl?.RecenterOnActiveLine();
        }
    }

    private async void OnQueueItemAddedNotification(object? sender, TrackItem addedTrack)
    {
        if (SidebarQueueItemsControl is null)
        {
            return;
        }

        await Task.Delay(24);
        try
        {
            int idx = ViewModel.Queue.IndexOf(addedTrack);
            if (idx < 0)
            {
                idx = ViewModel.Queue.Count - 1;
            }

            if (idx >= 0 && GetQueueItemContainer(SidebarQueueItemsControl, idx) is UIElement container)
            {
                ElementCompositionPreview.SetIsTranslationEnabled(container, true);
                var visual = ElementCompositionPreview.GetElementVisual(container);
                if (container is FrameworkElement fe)
                {
                    visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
                }

                container.Opacity = 0.0;
                visual.Properties.InsertVector3("Translation", new Vector3(0.0f, 18.0f, 0.0f));
                visual.Scale = new Vector3(0.92f, 0.92f, 1.0f);

                for (int step = 1; step <= 10; step++)
                {
                    double t = step / 10.0;
                    double ease = 1.0 - Math.Pow(1.0 - t, 3.0);
                    container.Opacity = Math.Clamp(ease * 1.1, 0.0, 1.0);
                    visual.Properties.InsertVector3("Translation", new Vector3(0.0f, (float)(18.0 * (1.0 - ease)), 0.0f));
                    float sc = (float)(0.92 + (0.08 * ease));
                    visual.Scale = new Vector3(sc, sc, 1.0f);
                    await Task.Delay(12);
                }

                ResetContainerVisualImmediate(container);
            }
        }
        catch
        {
            // Ignore transient container lookup errors
        }
    }

    private void OnFullscreenCenterStageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyFullscreenDeckLayout();
        UpdateFullscreenQueueEdgeFade();
    }

    private void OnFullscreenLyricsLayoutChanged(object? sender, bool isSidePanelOpen)
    {
        ApplyFullscreenDeckLayout();
        UpdateSidebarEqualizerAnimationState();
        if (isSidePanelOpen)
        {
            SyncFullscreenSlicer(animate: false);
            if (ViewModel.IsFullscreenLyricsActive)
            {
                FullscreenSynchronizedLyricsControl?.RecenterOnActiveLine();
            }
            else if (ViewModel.IsFullscreenQueueActive)
            {
                DispatcherQueue.TryEnqueue(UpdateFullscreenQueueEdgeFade);
            }
        }
    }

    private void ApplyFullscreenDeckLayout()
    {
        if (FullscreenDeckColumnPanel is null || FullscreenSidePanelRoot is null || FullscreenCenterStage is null)
        {
            return;
        }

        double stageWidth = FullscreenCenterStage.ActualWidth;
        double stageHeight = FullscreenCenterStage.ActualHeight;

        // Responsive large hero album artwork sizing (250px to 384px depending on window height)
        if (FullscreenCoverOuterBorder is not null && stageHeight > 200)
        {
            double coverSize = Math.Clamp(stageHeight * 0.44, 240.0, 384.0);
            if (stageWidth > 200)
            {
                double maxFromCol = Math.Max(220.0, ((stageWidth - 48.0) * 0.48) - 24.0);
                coverSize = Math.Min(coverSize, maxFromCol);
            }

            if (Math.Abs(FullscreenCoverOuterBorder.Width - coverSize) > 2.0)
            {
                FullscreenCoverOuterBorder.Width = coverSize;
                FullscreenCoverOuterBorder.Height = coverSize;
            }
        }

        float deckCenterX = (float)Math.Max(180.0, FullscreenDeckColumnPanel.ActualWidth * 0.5);
        float deckCenterY = (float)Math.Max(260.0, FullscreenDeckColumnPanel.ActualHeight * 0.5);
        FullscreenDeckColumnPanel.CenterPoint = new Vector3(deckCenterX, deckCenterY, 0.0f);

        bool isSidePanelOpen = ViewModel.IsFullscreenSidePanelOpen;
        if (isSidePanelOpen)
        {
            FullscreenSidePanelRoot.IsHitTestVisible = true;
            FullscreenSidePanelRoot.Opacity = 1.0;
            FullscreenSidePanelRoot.Translation = Vector3.Zero;

            FullscreenDeckColumnPanel.Scale = Vector3.One;
            FullscreenDeckColumnPanel.Translation = Vector3.Zero;
        }
        else
        {
            // Dynamically compute exact horizontal center shift from Column 0 (0.48*) to dead center of stage
            double usableCols = Math.Max(400.0, stageWidth - 48.0);
            double col0CenterX = (usableCols * 0.48) * 0.5;
            double stageCenterX = stageWidth > 400.0 ? stageWidth * 0.5 : 540.0;
            float centerShiftX = (float)Math.Max(0.0, stageCenterX - col0CenterX);

            FullscreenSidePanelRoot.IsHitTestVisible = false;
            FullscreenSidePanelRoot.Opacity = 0.0;
            FullscreenSidePanelRoot.Translation = new Vector3(44.0f, 0.0f, 0.0f);

            FullscreenDeckColumnPanel.Scale = new Vector3(1.04f, 1.04f, 1.0f);
            FullscreenDeckColumnPanel.Translation = new Vector3(centerShiftX, 0.0f, 0.0f);
        }
    }

    private void OnFullscreenSlicerButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button targetBtn)
        {
            _ = AnimateSlicerIndicatorAsync(
                FullscreenSlicerContainer,
                FullscreenSlicerIndicatorHost,
                FullscreenSlicerLiquidGlass,
                targetBtn,
                animate: true);
        }
    }

    private void SyncFullscreenSlicer(bool animate)
    {
        if (FullscreenSlicerContainer is null || FullscreenSlicerIndicatorHost is null || FullscreenSlicerLiquidGlass is null)
        {
            return;
        }

        Button? target = ViewModel.ActiveFullscreenTab == NowPlayingTab.Queue ? FsBtnQueue : FsBtnLyrics;
        if (target is not null)
        {
            _ = AnimateSlicerIndicatorAsync(
                FullscreenSlicerContainer,
                FullscreenSlicerIndicatorHost,
                FullscreenSlicerLiquidGlass,
                target,
                animate);
        }
    }

    private async void OnFullscreenSideTabChanged(object? sender, NowPlayingTab tab)
    {
        SyncFullscreenSlicer(animate: true);
        UpdateSidebarEqualizerAnimationState();
        UpdateSidebarAccentTintElements(_currentAmbientPalette.VibrantAccent);

        if (!ViewModel.IsFullscreenSidePanelOpen)
        {
            return;
        }

        int gen = ++_fullscreenTabAnimGen;
        bool isLyricsTab = tab == NowPlayingTab.Lyrics;
        FrameworkElement? activePanel = isLyricsTab
            ? (FrameworkElement?)FullscreenLyricsPanelRoot
            : FullscreenQueuePanelRoot;

        if (activePanel is null)
        {
            return;
        }

        float startX = isLyricsTab ? -20.0f : 20.0f;
        float startY = 8.0f;

        activePanel.Opacity = 0.0;
        activePanel.Translation = new Vector3(startX, startY, 0.0f);

        const int steps = 12;
        for (int i = 1; i <= steps; i++)
        {
            if (gen != _fullscreenTabAnimGen)
            {
                return;
            }

            double t = (double)i / steps;
            double ease = 1.0 - Math.Pow(1.0 - t, 3.0);

            activePanel.Opacity = Math.Clamp(ease * 1.08, 0.0, 1.0);
            activePanel.Translation = new Vector3(
                (float)(startX * (1.0 - ease)),
                (float)(startY * (1.0 - ease)),
                0.0f);

            await Task.Delay(12);
        }

        if (gen == _fullscreenTabAnimGen)
        {
            activePanel.Opacity = 1.0;
            activePanel.Translation = Vector3.Zero;
        }

        if (isLyricsTab)
        {
            await Task.Delay(32);
            FullscreenSynchronizedLyricsControl?.RecenterOnActiveLine();
        }
        else
        {
            await Task.Delay(24);
            UpdateFullscreenQueueEdgeFade();
            LiquidGlassBackdropManager.NotifyViewportScrolled();
        }
    }

    private bool _isSyncingSettingsNav;

    private void OnSettingsNavigationViewLoaded(object sender, RoutedEventArgs e)
    {
        ApplySettingsNavigationResponsivePaneMode();
        SyncSettingsNavigationViewSelection(ViewModel.SelectedSettingsCategory);
    }

    private void OnSettingsNavigationViewSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_isSyncingSettingsNav)
        {
            return;
        }

        if (args.SelectedItemContainer is NavigationViewItem { Tag: string categoryTag } &&
            !string.IsNullOrWhiteSpace(categoryTag))
        {
            if (ViewModel.SelectSettingsCategoryCommand.CanExecute(categoryTag))
            {
                ViewModel.SelectSettingsCategoryCommand.Execute(categoryTag);
            }
        }
    }

    private async void OnToggleFolderRuleListTypeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Xperience.Core.Domain.Models.FolderScanRule rule })
        {
            await ViewModel.ToggleFolderRuleListTypeAsync(rule);
        }
    }

    private async void OnCycleFolderRuleDepthClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Xperience.Core.Domain.Models.FolderScanRule rule })
        {
            await ViewModel.CycleFolderRuleDepthModeAsync(rule);
        }
    }

    private async void OnRemoveFolderRuleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Xperience.Core.Domain.Models.FolderScanRule rule })
        {
            await ViewModel.RemoveFolderRuleAsync(rule);
        }
    }

    private async void OnDownloadSuggestedTrackClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Xperience.Core.Domain.Models.DataUsageDownloadSuggestionItem item })
        {
            await ViewModel.DownloadSuggestedTrackAsync(item);
        }
    }

    private void OnSettingsNavigationViewBackRequested(
        NavigationView sender,
        NavigationViewBackRequestedEventArgs args)
    {
        if (ViewModel.NavigateBackFromDetailCommand.CanExecute(null))
        {
            ViewModel.NavigateBackFromDetailCommand.Execute(null);
        }
    }

    private void SyncSettingsNavigationViewSelection(SettingsCategory category)
    {
        if (SettingsNavigationView is null)
        {
            return;
        }

        NavigationViewItem? targetItem = category switch
        {
            SettingsCategory.AppearanceAndGlass => SettingsNavAppearance,
            SettingsCategory.LibraryAndFolders => SettingsNavLibrary,
            SettingsCategory.YouTubeMusicAccount => SettingsNavYouTube,
            SettingsCategory.DownloadsAndOffline => SettingsNavDownloads,
            SettingsCategory.LyricsAndVisualizers => SettingsNavLyrics,
            SettingsCategory.PlaybackAndQuality => SettingsNavPlayback,
            SettingsCategory.DataUsage => SettingsNavDataUsage,
            SettingsCategory.Updates => SettingsNavUpdates,
            _ => SettingsNavAppearance
        };

        if (targetItem is not null && !ReferenceEquals(SettingsNavigationView.SelectedItem, targetItem))
        {
            _isSyncingSettingsNav = true;
            try
            {
                SettingsNavigationView.SelectedItem = targetItem;
            }
            finally
            {
                _isSyncingSettingsNav = false;
            }
        }
    }

    private async void OnSettingsCategoryTransitionRequested(object? sender, SettingsCategory category)
    {
        SyncSettingsNavigationViewSelection(category);
        if (category == SettingsCategory.DataUsage)
        {
            ViewModel.RefreshDataUsageSummary();
        }

        if (SettingsCategoryContentHost is null)
        {
            return;
        }

        // SettingsCategoryContentHost uses WinUI 3 OpacityTransition + TranslationTransition facades directly;
        // calling legacy ElementCompositionPreview.SetIsTranslationEnabled throws UnauthorizedAccessException.
        SettingsCategoryContentHost.Opacity = 0.0;
        SettingsCategoryContentHost.Translation = new Vector3(0.0f, 16.0f, 0.0f);

        await Task.Delay(18);

        if (SettingsCategoryContentHost is not null)
        {
            SettingsCategoryContentHost.Opacity = 1.0;
            SettingsCategoryContentHost.Translation = Vector3.Zero;
        }
    }

    private async void OnInteractiveYouTubeLoginRequested(object? sender, EventArgs e)
    {
        WebView2? webView = null;
        try
        {
            string loginUserDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XperienceMusicPlayer",
                "WebView2Login");
            Directory.CreateDirectory(loginUserDataDir);

            webView = new WebView2
            {
                Width = 520,
                Height = 540
            };

            var dialog = new ContentDialog
            {
                Title = "Sign in to YouTube Music",
                Content = webView,
                PrimaryButtonText = "Done (Save Session)",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            string? capturedCookieHeader = null;
            string? capturedVisitorData = null;
            string? capturedDataSyncId = null;
            bool autoCaptured = false;
            bool dialogClosed = false;

            async Task<(string CookieHeader, string? VisitorData, string? DataSyncId, bool LoggedIn)> TryExtractSessionAsync()
            {
                if (webView?.CoreWebView2 is null)
                {
                    return (string.Empty, null, null, false);
                }

                var ytmCookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync("https://music.youtube.com");
                var ytWwwCookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.youtube.com");
                var ytRootCookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync("https://youtube.com");

                static string? FormatCookies(IReadOnlyList<Microsoft.Web.WebView2.Core.CoreWebView2Cookie>? list) =>
                    list is { Count: > 0 }
                        ? string.Join("; ", list.Select(c => $"{c.Name}={c.Value}"))
                        : null;

                string cookieHeader = Services.YouTubeAuthHelper.MergeCookieStrings(
                    FormatCookies(ytmCookies),
                    FormatCookies(ytWwwCookies),
                    FormatCookies(ytRootCookies));

                string? visitorData = null;
                string? dataSyncId = null;
                bool loggedIn = false;
                try
                {
                    const string js = """
                        (function() {
                            try {
                                var cfg = window.ytcfg;
                                var cfgData = (cfg && cfg.data_) ? cfg.data_ : {};
                                var ytc = (window.yt && window.yt.config_) ? window.yt.config_ : {};
                                var vd = ytc.VISITOR_DATA || (cfg && cfg.get ? cfg.get('VISITOR_DATA') : '') || cfgData.VISITOR_DATA || '';
                                var ds = ytc.DATASYNC_ID || (cfg && cfg.get ? cfg.get('DATASYNC_ID') : '') || cfgData.DATASYNC_ID || '';
                                var li = (ytc.LOGGED_IN !== undefined) ? ytc.LOGGED_IN : ((cfg && cfg.get && cfg.get('LOGGED_IN') !== undefined) ? cfg.get('LOGGED_IN') : cfgData.LOGGED_IN);
                                return JSON.stringify({ vd: String(vd || ''), ds: String(ds || ''), li: li === true });
                            } catch (e) {
                                return JSON.stringify({ vd: '', ds: '', li: false });
                            }
                        })();
                        """;
                    string raw = await webView.CoreWebView2.ExecuteScriptAsync(js);
                    string? jsonText = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
                    if (!string.IsNullOrWhiteSpace(jsonText))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(jsonText);
                        if (doc.RootElement.TryGetProperty("vd", out var vdProp))
                        {
                            visitorData = vdProp.GetString();
                        }

                        if (doc.RootElement.TryGetProperty("ds", out var dsProp))
                        {
                            dataSyncId = dsProp.GetString();
                        }

                        if (doc.RootElement.TryGetProperty("li", out var liProp))
                        {
                            loggedIn = liProp.ValueKind == System.Text.Json.JsonValueKind.True;
                        }
                    }
                }
                catch
                {
                    // Fallback to InnerTube account_menu / sw.js_data resolution
                }

                return (cookieHeader, visitorData, dataSyncId, loggedIn);
            }

            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: loginUserDataDir,
                options: null);
            await webView.EnsureCoreWebView2Async(env);

            webView.CoreWebView2.NavigationCompleted += async (wv, navArgs) =>
            {
                if (dialogClosed || autoCaptured || !navArgs.IsSuccess)
                {
                    return;
                }

                if (!Uri.TryCreate(wv.Source, UriKind.Absolute, out var currentUri) ||
                    !string.Equals(currentUri.Host, "music.youtube.com", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                for (int attempt = 0; attempt < 8 && !dialogClosed && !autoCaptured; attempt++)
                {
                    var extracted = await TryExtractSessionAsync();
                    if (extracted.LoggedIn &&
                        Services.YouTubeAuthHelper.HasYouTubeAccountSession(extracted.CookieHeader))
                    {
                        capturedCookieHeader = extracted.CookieHeader;
                        capturedVisitorData = extracted.VisitorData;
                        capturedDataSyncId = extracted.DataSyncId;
                        autoCaptured = true;
                        dialog.Hide();
                        return;
                    }

                    await Task.Delay(350);
                }
            };

            webView.Source = new Uri("https://accounts.google.com/ServiceLogin?continue=https%3A%2F%2Fmusic.youtube.com");

            var result = await dialog.ShowAsync();
            dialogClosed = true;

            if (autoCaptured && !string.IsNullOrWhiteSpace(capturedCookieHeader))
            {
                await ViewModel.ConnectYouTubeBrowserSessionAsync(
                    capturedCookieHeader,
                    capturedVisitorData,
                    capturedDataSyncId);
            }
            else if (result == ContentDialogResult.Primary && webView.CoreWebView2 is not null)
            {
                var extracted = await TryExtractSessionAsync();
                if (!string.IsNullOrWhiteSpace(extracted.CookieHeader))
                {
                    await ViewModel.ConnectYouTubeBrowserSessionAsync(
                        extracted.CookieHeader,
                        extracted.VisitorData,
                        extracted.DataSyncId);
                }
            }
        }
        catch
        {
            ViewModel.StatusMessage = "Tip: You can also paste your music.youtube.com Cookie header directly in the box below.";
        }
        finally
        {
            try { webView?.Close(); } catch { }
        }
    }

    #region Home Aurora Borealis, Progressive Blur, Carousel Edges & Home Actions

    private const string CarouselEdgeOverlayTag = "HomeCarouselEdgeOverlay";
    private const float CarouselEdgeBlurWidth = 52.0f;

    private sealed class ProgressiveBlurVisualState : IDisposable
    {
        private bool _disposed;

        public required SpriteVisual Visual { get; init; }
        public required CompositionGeometricClip Clip { get; init; }
        public required CompositionRoundedRectangleGeometry ClipGeometry { get; init; }
        public required CompositionMaskBrush MaskBrush { get; init; }
        public required CompositionEffectBrush BlurBrush { get; init; }
        public required CompositionBackdropBrush BackdropBrush { get; init; }
        public required CompositionLinearGradientBrush MaskGradient { get; init; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { Visual.Brush = null; } catch { }
            try { Visual.Clip = null; } catch { }
            try { MaskBrush.Source = null; MaskBrush.Mask = null; } catch { }
            try { Visual.Dispose(); } catch { }
            try { Clip.Dispose(); } catch { }
            try { ClipGeometry.Dispose(); } catch { }
            try { MaskBrush.Dispose(); } catch { }
            try { BlurBrush.Dispose(); } catch { }
            try { BackdropBrush.Dispose(); } catch { }
            try { MaskGradient.Dispose(); } catch { }
        }
    }

    private sealed class CarouselEdgeOverlayState : IDisposable
    {
        private bool _disposed;

        public required Grid HostGrid { get; init; }
        public required Grid OverlayHost { get; init; }
        public required ContainerVisual Container { get; init; }
        public required SpriteVisual LeftVisual { get; init; }
        public required SpriteVisual RightVisual { get; init; }
        public required CompositionMaskBrush LeftMask { get; init; }
        public required CompositionEffectBrush LeftBrush { get; init; }
        public required CompositionBackdropBrush LeftBackdrop { get; init; }
        public required CompositionLinearGradientBrush LeftGradient { get; init; }
        public required CompositionMaskBrush RightMask { get; init; }
        public required CompositionEffectBrush RightBrush { get; init; }
        public required CompositionBackdropBrush RightBackdrop { get; init; }
        public required CompositionLinearGradientBrush RightGradient { get; init; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { Container.Children.RemoveAll(); } catch { }
            try { LeftVisual.Brush = null; } catch { }
            try { RightVisual.Brush = null; } catch { }
            try { LeftMask.Source = null; LeftMask.Mask = null; } catch { }
            try { RightMask.Source = null; RightMask.Mask = null; } catch { }
            try { LeftVisual.Dispose(); } catch { }
            try { RightVisual.Dispose(); } catch { }
            try { Container.Dispose(); } catch { }
            try { LeftMask.Dispose(); } catch { }
            try { LeftBrush.Dispose(); } catch { }
            try { LeftBackdrop.Dispose(); } catch { }
            try { LeftGradient.Dispose(); } catch { }
            try { RightMask.Dispose(); } catch { }
            try { RightBrush.Dispose(); } catch { }
            try { RightBackdrop.Dispose(); } catch { }
            try { RightGradient.Dispose(); } catch { }
        }
    }

    private void OnAuroraPaletteChanged(object? sender, AuroraPalette palette)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _targetAuroraPalette = palette;
            UpdateAuroraAnimationState();
        });
    }

    private void UpdateAuroraAnimationState()
    {
        bool isAuroraVisible = ViewModel.HomeAuroraVisibility == Visibility.Visible;
        if (isAuroraVisible)
        {
            if (!_auroraAnimTimer.IsRunning)
            {
                _auroraAnimTimer.Start();
            }

            HomeAuroraCanvas?.Invalidate();
        }
        else if (_auroraAnimTimer.IsRunning)
        {
            _auroraAnimTimer.Stop();
        }
    }

    private void OnAuroraAnimTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (ViewModel.HomeAuroraVisibility != Visibility.Visible)
        {
            sender.Stop();
            return;
        }

        _auroraElapsedSeconds += 0.033;
        _currentAuroraPalette = LerpPalette(_currentAuroraPalette, _targetAuroraPalette, 0.06);
        HomeAuroraCanvas?.Invalidate();
    }

    private void OnHomeAuroraCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        float width = (float)sender.Size.Width;
        float height = (float)sender.Size.Height;
        if (width <= 2f || height <= 2f)
        {
            return;
        }

        var ds = args.DrawingSession;
        ds.Clear(Colors.Transparent);

        // Android-matched scroll-linked dissolve & parallax so Aurora strictly stays top-to-mid-screen
        // and fades out completely before reaching lower shelves (Related Artists, Playlists, etc.).
        float scrollPx = (float)(CatalogScrollViewer?.VerticalOffset ?? 0.0);
        float scrollFade = Math.Clamp(scrollPx / (height * 1.05f), 0f, 1f);
        float intensity = 1f - (scrollFade * 0.96f);
        if (intensity <= 0.01f)
        {
            return;
        }

        float parallaxY = -scrollPx * 0.34f;
        bool isLight = ActualTheme == ElementTheme.Light;

        float primaryAlpha = (isLight ? 0.28f : 0.40f) * intensity;
        float secondaryAlpha = (isLight ? 0.24f : 0.35f) * intensity;
        float curtainAlpha = (isLight ? 0.21f : 0.30f) * intensity;
        float mistAlpha = (isLight ? 0.17f : 0.24f) * intensity;

        float t = (float)_auroraElapsedSeconds;
        float p1 = t * (2f * MathF.PI / 16f);
        float p2 = t * (2f * MathF.PI / 23f);
        float b = t * (2f * MathF.PI / 11f);

        float sinP1 = MathF.Sin(p1);
        float cosP1 = MathF.Cos(p1);
        float sinP2 = MathF.Sin(p2);
        float cosP2 = MathF.Cos(p2);
        float sinB = MathF.Sin(b);
        float breatheScale = 1f + (0.055f * sinB);

        float maxDim = MathF.Max(width, height);
        Color c1 = _currentAuroraPalette.Color1;
        Color c2 = _currentAuroraPalette.Color2;
        Color c3 = _currentAuroraPalette.Color3;
        Color c4 = _currentAuroraPalette.Color4;

        using var verticalFadeMask = new CanvasLinearGradientBrush(
            ds,
            [
                new CanvasGradientStop { Position = 0.00f, Color = Color.FromArgb(255, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.46f, Color = Color.FromArgb(255, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.68f, Color = Color.FromArgb(158, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.84f, Color = Color.FromArgb(46, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.94f, Color = Color.FromArgb(8, 255, 255, 255) },
                new CanvasGradientStop { Position = 1.00f, Color = Color.FromArgb(0, 255, 255, 255) }
            ])
        {
            StartPoint = new Vector2(0f, 0f),
            EndPoint = new Vector2(0f, height)
        };

        using var auroraList = new CanvasCommandList(sender);
        using (var auroraDs = auroraList.CreateDrawingSession())
        {
            // 1. Canopy glow
            DrawAuroraGlow(
                auroraDs,
                c1,
                width * (0.20f + 0.11f * sinP1),
                (height * (0.09f + 0.03f * cosP2)) + parallaxY,
                0.72f * maxDim * breatheScale,
                primaryAlpha);

            // 2. Arch glow
            DrawAuroraGlow(
                auroraDs,
                c2,
                width * (0.82f - 0.11f * cosP1),
                (height * (0.14f + 0.04f * sinB)) + parallaxY,
                0.68f * maxDim * (1f - 0.03f * sinB),
                secondaryAlpha);

            // 3. Curtain 1 (tilted ribbon)
            DrawAuroraRibbon(
                auroraDs,
                LerpColor(c1, c3, 0.45),
                c3,
                width * (0.44f + 0.14f * sinP2),
                (height * (0.23f + 0.04f * cosP1)) + parallaxY,
                0.48f * maxDim,
                scaleX: 1.75f,
                scaleY: 0.56f,
                rotationDegrees: -14f + (5f * sinP2),
                curtainAlpha);

            // 4. Curtain 2 (counter-tilted ribbon)
            DrawAuroraRibbon(
                auroraDs,
                LerpColor(c3, c2, 0.50),
                c4,
                width * (0.58f - 0.15f * cosP2),
                (height * (0.35f + 0.045f * sinP1)) + parallaxY,
                0.46f * maxDim,
                scaleX: 1.85f,
                scaleY: 0.52f,
                rotationDegrees: 13f + (4.5f * cosP1),
                curtainAlpha * 0.92f);

            // 5. Lower veil (soft mist dissolving into dark background)
            DrawAuroraRibbon(
                auroraDs,
                c4,
                LerpColor(c4, c1, 0.35),
                width * (0.48f + 0.12f * cosP1),
                (height * (0.48f + 0.035f * sinP2)) + parallaxY,
                0.50f * maxDim,
                scaleX: 1.60f,
                scaleY: 0.64f,
                rotationDegrees: -5f + (3f * sinB),
                mistAlpha);

            // 6. Top-crest shimmer
            DrawAuroraGlow(
                auroraDs,
                LerpColor(c1, c2, 0.50),
                width * (0.52f + 0.16f * sinP1),
                (height * 0.03f) + parallaxY,
                0.46f * maxDim * breatheScale,
                secondaryAlpha * 0.55f);

            // 7. Library secondary top-right radial accent glow (Android LibraryScreen parity)
            if (ViewModel.CurrentSection == NavigationSection.Library)
            {
                float libraryGlowAlpha = (isLight ? 0.10f : 0.18f) * intensity;
                DrawAuroraGlow(
                    auroraDs,
                    c1,
                    width * 0.85f,
                    (height * 0.12f) + (parallaxY * 0.45f),
                    width * 0.70f,
                    libraryGlowAlpha);
            }

            // 8. Liked Songs subtle top-left ruby radial bloom (Android LikedScreen parity)
            if (ViewModel.CurrentSection == NavigationSection.Liked &&
                ViewModel.ActiveSubView == WorkspaceSubView.MainSection &&
                !ViewModel.IsSearchPageOpen)
            {
                float likedRubyAlpha = (isLight ? 0.12f : 0.22f) * intensity;
                DrawAuroraGlow(
                    auroraDs,
                    Color.FromArgb(255, 255, 45, 85),
                    width * 0.16f,
                    (height * 0.14f) + (parallaxY * 0.35f),
                    width * 0.48f,
                    likedRubyAlpha);
            }
        }

        using var maskList = new CanvasCommandList(sender);
        using (var maskDs = maskList.CreateDrawingSession())
        {
            maskDs.FillRectangle(0f, 0f, width, height, verticalFadeMask);
        }

        using var maskedAurora = new AlphaMaskEffect
        {
            Source = auroraList,
            AlphaMask = maskList
        };
        ds.DrawImage(maskedAurora);
    }

    private static void DrawAuroraGlow(
        CanvasDrawingSession ds,
        Color color,
        float cx,
        float cy,
        float radius,
        float peakAlphaNormalized)
    {
        float clamped = Math.Clamp(peakAlphaNormalized, 0f, 1f);
        byte a0 = (byte)Math.Round(255f * clamped);
        byte a1 = (byte)Math.Round(255f * clamped * 0.56f);
        byte a2 = (byte)Math.Round(255f * clamped * 0.16f);
        float r = Math.Max(1f, radius);

        using var brush = new CanvasRadialGradientBrush(
            ds,
            [
                new CanvasGradientStop { Position = 0.00f, Color = Color.FromArgb(a0, color.R, color.G, color.B) },
                new CanvasGradientStop { Position = 0.38f, Color = Color.FromArgb(a1, color.R, color.G, color.B) },
                new CanvasGradientStop { Position = 0.72f, Color = Color.FromArgb(a2, color.R, color.G, color.B) },
                new CanvasGradientStop { Position = 1.00f, Color = Color.FromArgb(0, color.R, color.G, color.B) }
            ])
        {
            Center = new Vector2(cx, cy),
            RadiusX = r,
            RadiusY = r
        };

        ds.FillEllipse(cx, cy, r, r, brush);
    }

    private static void DrawAuroraRibbon(
        CanvasDrawingSession ds,
        Color coreColor,
        Color edgeColor,
        float cx,
        float cy,
        float radius,
        float scaleX,
        float scaleY,
        float rotationDegrees,
        float peakAlphaNormalized)
    {
        float clamped = Math.Clamp(peakAlphaNormalized, 0f, 1f);
        byte a0 = (byte)Math.Round(255f * clamped);
        byte a1 = (byte)Math.Round(255f * clamped * 0.58f);
        byte a2 = (byte)Math.Round(255f * clamped * 0.16f);
        float r = Math.Max(1f, radius);
        float angleRad = rotationDegrees * (MathF.PI / 180f);

        using var brush = new CanvasRadialGradientBrush(
            ds,
            [
                new CanvasGradientStop { Position = 0.00f, Color = Color.FromArgb(a0, coreColor.R, coreColor.G, coreColor.B) },
                new CanvasGradientStop { Position = 0.35f, Color = Color.FromArgb(a1, edgeColor.R, edgeColor.G, edgeColor.B) },
                new CanvasGradientStop { Position = 0.70f, Color = Color.FromArgb(a2, edgeColor.R, edgeColor.G, edgeColor.B) },
                new CanvasGradientStop { Position = 1.00f, Color = Color.FromArgb(0, edgeColor.R, edgeColor.G, edgeColor.B) }
            ])
        {
            Center = Vector2.Zero,
            RadiusX = r,
            RadiusY = r
        };

        Matrix3x2 prev = ds.Transform;
        ds.Transform =
            Matrix3x2.CreateScale(scaleX, scaleY) *
            Matrix3x2.CreateRotation(angleRad) *
            Matrix3x2.CreateTranslation(cx, cy) *
            prev;

        ds.FillEllipse(0f, 0f, r, r, brush);
        ds.Transform = prev;
    }

    private static AuroraPalette LerpPalette(AuroraPalette current, AuroraPalette target, double t) =>
        new(
            LerpColor(current.Color1, target.Color1, t),
            LerpColor(current.Color2, target.Color2, t),
            LerpColor(current.Color3, target.Color3, t),
            LerpColor(current.Color4, target.Color4, t));

    private static Color LerpColor(Color from, Color to, double t) =>
        Color.FromArgb(
            255,
            (byte)Math.Clamp(Math.Round(from.R + (to.R - from.R) * t), 0, 255),
            (byte)Math.Clamp(Math.Round(from.G + (to.G - from.G) * t), 0, 255),
            (byte)Math.Clamp(Math.Round(from.B + (to.B - from.B) * t), 0, 255));

    private static CompositionEffectFactory EnsureBlurEffectFactory(
        Compositor compositor,
        ref CompositionEffectFactory? cache,
        float blurAmount,
        string name)
    {
        if (cache != null)
        {
            return cache;
        }

        using var blurEffect = new GaussianBlurEffect
        {
            Name = name,
            BlurAmount = blurAmount,
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Balanced,
            Source = new CompositionEffectSourceParameter("backdrop")
        };

        cache = compositor.CreateEffectFactory(blurEffect);
        return cache;
    }

    private static CompositionLinearGradientBrush CreateMaskGradient(
        Compositor compositor,
        Vector2 start,
        Vector2 end,
        params (float Position, Color Color)[] stops)
    {
        var gradient = compositor.CreateLinearGradientBrush();
        gradient.MappingMode = CompositionMappingMode.Relative;
        gradient.StartPoint = start;
        gradient.EndPoint = end;

        foreach (var (position, color) in stops)
        {
            gradient.ColorStops.Add(compositor.CreateColorGradientStop(position, color));
        }

        return gradient;
    }

    private void OnProgressiveCardBlurLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement target)
        {
            target.Unloaded -= OnProgressiveCardBlurUnloaded;
            target.Unloaded += OnProgressiveCardBlurUnloaded;
            UpdateProgressiveCardBlur(target);
        }
    }

    private void OnProgressiveCardBlurUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target)
        {
            return;
        }

        target.Unloaded -= OnProgressiveCardBlurUnloaded;
        try
        {
            ElementCompositionPreview.SetElementChildVisual(target, null);
        }
        catch
        {
        }

        if (_progressiveBlurStates.TryGetValue(target, out ProgressiveBlurVisualState? state))
        {
            _progressiveBlurStates.Remove(target);
            state.Dispose();
        }
    }

    private void OnProgressiveCardBlurSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement target && target.IsLoaded)
        {
            UpdateProgressiveCardBlur(target);
        }
    }

    private void UpdateProgressiveCardBlur(FrameworkElement target)
    {
        if (!target.IsLoaded)
        {
            return;
        }

        float w = (float)target.ActualWidth;
        float h = (float)target.ActualHeight;
        if (w <= 2f || h <= 2f)
        {
            return;
        }

        try
        {
            float cornerRadius = ResolveCardCornerRadius(target);

            if (!_progressiveBlurStates.TryGetValue(target, out ProgressiveBlurVisualState? state))
            {
                var hostVisual = ElementCompositionPreview.GetElementVisual(target);
                var compositor = hostVisual.Compositor;

                var factory = EnsureBlurEffectFactory(
                    compositor,
                    ref _progressiveBlurEffectFactory,
                    blurAmount: 14.0f,
                    name: "HomeProgressiveCardBlur");

                var backdropBrush = compositor.CreateBackdropBrush();
                var blurBrush = factory.CreateBrush();
                blurBrush.SetSourceParameter("backdrop", backdropBrush);

                var maskGradient = CreateMaskGradient(
                    compositor,
                    new Vector2(0f, 0f),
                    new Vector2(0f, 1f),
                    (0.00f, Color.FromArgb(0, 255, 255, 255)),
                    (0.46f, Color.FromArgb(0, 255, 255, 255)),
                    (0.74f, Color.FromArgb(180, 255, 255, 255)),
                    (1.00f, Colors.White));

                var maskBrush = compositor.CreateMaskBrush();
                maskBrush.Source = blurBrush;
                maskBrush.Mask = maskGradient;

                var roundedClipGeometry = compositor.CreateRoundedRectangleGeometry();
                var clip = compositor.CreateGeometricClip(roundedClipGeometry);
                var spriteVisual = compositor.CreateSpriteVisual();
                spriteVisual.Brush = maskBrush;
                spriteVisual.Clip = clip;

                ElementCompositionPreview.SetElementChildVisual(target, spriteVisual);
                target.Unloaded -= OnProgressiveCardBlurUnloaded;
                target.Unloaded += OnProgressiveCardBlurUnloaded;

                state = new ProgressiveBlurVisualState
                {
                    Visual = spriteVisual,
                    Clip = clip,
                    ClipGeometry = roundedClipGeometry,
                    MaskBrush = maskBrush,
                    BlurBrush = blurBrush,
                    BackdropBrush = backdropBrush,
                    MaskGradient = maskGradient
                };
                _progressiveBlurStates.AddOrUpdate(target, state);
            }

            state.Visual.Size = new Vector2(w, h);
            state.ClipGeometry.Size = new Vector2(w, h);
            state.ClipGeometry.CornerRadius = new Vector2(cornerRadius, cornerRadius);
        }
        catch
        {
            // Fallback gracefully to the XAML gradient scrim when backdrop blur is unavailable.
        }
    }

    private static float ResolveCardCornerRadius(FrameworkElement target)
    {
        if (target is Border selfBorder && selfBorder.CornerRadius.TopLeft > 0)
        {
            return (float)selfBorder.CornerRadius.TopLeft;
        }

        DependencyObject? parent = VisualTreeHelper.GetParent(target);
        if (parent != null)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(parent, i) is Border sibling && sibling.CornerRadius.TopLeft > 0)
                {
                    return (float)sibling.CornerRadius.TopLeft;
                }
            }

            DependencyObject? grandParent = VisualTreeHelper.GetParent(parent);
            if (grandParent is Border parentBorder && parentBorder.CornerRadius.TopLeft > 0)
            {
                return (float)parentBorder.CornerRadius.TopLeft;
            }
        }

        return 18.0f;
    }

    private void OnHomeHorizontalCarouselLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv)
        {
            return;
        }

        EnsureCarouselEdgeOverlays(sv);
        UpdateHorizontalCarouselTransform(sv);

        DispatcherQueue.TryEnqueue(() =>
        {
            if (sv.IsLoaded)
            {
                EnsureCarouselEdgeOverlays(sv);
                UpdateHorizontalCarouselTransform(sv);
            }
        });
    }

    private void OnHomeHorizontalCarouselSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            EnsureCarouselEdgeOverlays(sv);
            UpdateHorizontalCarouselTransform(sv);
        }
    }

    private void OnHomeHorizontalCarouselViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            UpdateHorizontalCarouselTransform(sv);
        }
    }

    private void OnHomeHorizontalItemsPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is DependencyObject obj)
        {
            ScrollViewer? sv = FindAncestorScrollViewer(obj);
            if (sv != null)
            {
                EnsureCarouselEdgeOverlays(sv);
                UpdateHorizontalCarouselTransform(sv);
            }
        }
    }

    private void OnHomeRecentlyPlayedMosaicArranged(object? sender, EventArgs e)
    {
        if (HomeRecentlyPlayedScrollViewer is { IsLoaded: true })
        {
            EnsureCarouselEdgeOverlays(HomeRecentlyPlayedScrollViewer);
            UpdateHorizontalCarouselTransform(HomeRecentlyPlayedScrollViewer);
        }
    }

    private void OnHomeHorizontalCarouselUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv)
        {
            return;
        }

        sv.Unloaded -= OnHomeHorizontalCarouselUnloaded;
        if (_carouselEdgeStates.Remove(sv, out CarouselEdgeOverlayState? state))
        {
            try
            {
                ElementCompositionPreview.SetElementChildVisual(state.OverlayHost, null);
            }
            catch
            {
            }

            state.Dispose();
        }
    }

    private void DisposeAllCarouselEdgeOverlays()
    {
        foreach (var kvp in _carouselEdgeStates)
        {
            try
            {
                if (kvp.Key is ScrollViewer sv)
                {
                    sv.Unloaded -= OnHomeHorizontalCarouselUnloaded;
                }

                ElementCompositionPreview.SetElementChildVisual(kvp.Value.OverlayHost, null);
            }
            catch
            {
            }

            kvp.Value.Dispose();
        }

        _carouselEdgeStates.Clear();
    }

    private CarouselEdgeOverlayState? EnsureCarouselEdgeOverlays(ScrollViewer sv)
    {
        if (!sv.IsLoaded)
        {
            return null;
        }

        if (_carouselEdgeStates.TryGetValue(sv, out CarouselEdgeOverlayState? existing))
        {
            return existing;
        }

        if (VisualTreeHelper.GetParent(sv) is not Grid hostGrid)
        {
            return null;
        }

        try
        {
            Grid? overlayHost = null;
            foreach (UIElement child in hostGrid.Children)
            {
                if (child is Grid g && Equals(g.Tag, CarouselEdgeOverlayTag))
                {
                    overlayHost = g;
                    break;
                }
            }

            if (overlayHost == null)
            {
                overlayHost = new Grid
                {
                    IsHitTestVisible = false,
                    Tag = CarouselEdgeOverlayTag
                };
                hostGrid.Children.Add(overlayHost);
            }

            var hostVisual = ElementCompositionPreview.GetElementVisual(overlayHost);
            var compositor = hostVisual.Compositor;

            var factory = EnsureBlurEffectFactory(
                compositor,
                ref _edgeBlurEffectFactory,
                blurAmount: 10.0f,
                name: "HomeCarouselEdgeBlur");

            var leftBackdrop = compositor.CreateBackdropBrush();
            var leftBrush = factory.CreateBrush();
            leftBrush.SetSourceParameter("backdrop", leftBackdrop);
            var leftGradient = CreateMaskGradient(
                compositor,
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                (0.00f, Colors.White),
                (0.38f, Color.FromArgb(190, 255, 255, 255)),
                (1.00f, Color.FromArgb(0, 255, 255, 255)));
            var leftMask = compositor.CreateMaskBrush();
            leftMask.Source = leftBrush;
            leftMask.Mask = leftGradient;

            var leftVisual = compositor.CreateSpriteVisual();
            leftVisual.Brush = leftMask;
            leftVisual.Opacity = 0f;

            var rightBackdrop = compositor.CreateBackdropBrush();
            var rightBrush = factory.CreateBrush();
            rightBrush.SetSourceParameter("backdrop", rightBackdrop);
            var rightGradient = CreateMaskGradient(
                compositor,
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                (0.00f, Color.FromArgb(0, 255, 255, 255)),
                (0.62f, Color.FromArgb(190, 255, 255, 255)),
                (1.00f, Colors.White));
            var rightMask = compositor.CreateMaskBrush();
            rightMask.Source = rightBrush;
            rightMask.Mask = rightGradient;

            var rightVisual = compositor.CreateSpriteVisual();
            rightVisual.Brush = rightMask;
            rightVisual.Opacity = 0f;

            var container = compositor.CreateContainerVisual();
            container.Children.InsertAtTop(leftVisual);
            container.Children.InsertAtTop(rightVisual);
            ElementCompositionPreview.SetElementChildVisual(overlayHost, container);

            sv.Unloaded -= OnHomeHorizontalCarouselUnloaded;
            sv.Unloaded += OnHomeHorizontalCarouselUnloaded;

            var state = new CarouselEdgeOverlayState
            {
                HostGrid = hostGrid,
                OverlayHost = overlayHost,
                Container = container,
                LeftVisual = leftVisual,
                RightVisual = rightVisual,
                LeftMask = leftMask,
                LeftBrush = leftBrush,
                LeftBackdrop = leftBackdrop,
                LeftGradient = leftGradient,
                RightMask = rightMask,
                RightBrush = rightBrush,
                RightBackdrop = rightBackdrop,
                RightGradient = rightGradient
            };
            _carouselEdgeStates[sv] = state;
            return state;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateHorizontalCarouselTransform(ScrollViewer sv)
    {
        if (!sv.IsLoaded || sv.ActualWidth <= 20 || sv.ActualHeight <= 10)
        {
            return;
        }

        double hostWidth = sv.ActualWidth;
        double hostHeight = sv.ActualHeight;
        double offset = sv.HorizontalOffset;
        double maxOffset = sv.ScrollableWidth;
        bool canScrollLeft = offset > 2.0;
        bool canScrollRight = (maxOffset - offset) > 2.0;

        if (_carouselEdgeStates.TryGetValue(sv, out CarouselEdgeOverlayState? edgeState))
        {
            float edgeHeight = Math.Max(0f, (float)hostHeight - 14f);
            float edgeWidth = Math.Min(CarouselEdgeBlurWidth, Math.Max(16f, (float)hostWidth * 0.14f));

            edgeState.LeftVisual.Size = new Vector2(edgeWidth, edgeHeight);
            edgeState.LeftVisual.Offset = Vector3.Zero;
            edgeState.LeftVisual.Opacity = canScrollLeft
                ? (float)Math.Clamp(offset / 32.0, 0.0, 1.0)
                : 0.0f;

            edgeState.RightVisual.Size = new Vector2(edgeWidth, edgeHeight);
            edgeState.RightVisual.Offset = new Vector3(Math.Max(0f, (float)hostWidth - edgeWidth), 0f, 0f);
            edgeState.RightVisual.Opacity = canScrollRight
                ? (float)Math.Clamp((maxOffset - offset) / 32.0, 0.0, 1.0)
                : 0.0f;
        }

        if (sv.Content is not UIElement contentRoot)
        {
            return;
        }

        // Dedicated Android BentoGrid column-anchored scroll-edge shrinking for Recently Played mosaic
        if (contentRoot is ItemsControl recentItemsControl &&
            recentItemsControl.ItemsPanelRoot is RecentlyPlayedMosaicPanel mosaicPanel)
        {
            double leftPad = sv.Padding.Left;
            double viewportEnd = sv.ViewportWidth > 20.0 ? sv.ViewportWidth : hostWidth;

            foreach (UIElement element in mosaicPanel.Children)
            {
                if (element is not FrameworkElement fe || !mosaicPanel.TryGetTileLayout(element, out MosaicTileLayout layout))
                {
                    continue;
                }

                double itemStart = leftPad + layout.ColumnLeft - offset;
                double itemSize = Math.Max(1.0, layout.ColumnWidth);
                double itemEnd = itemStart + itemSize;

                double leftFraction = itemStart < 0.0 ? Math.Clamp(-itemStart / itemSize, 0.0, 1.0) : 0.0;
                double rightFraction = itemEnd > viewportEnd ? Math.Clamp((itemEnd - viewportEnd) / itemSize, 0.0, 1.0) : 0.0;

                double leftScale = 1.0 - (0.24 * leftFraction);
                double rightScale = 1.0 - (0.16 * rightFraction);
                float targetScale = (float)Math.Clamp(leftScale * rightScale, 0.74, 1.0);

                // Unified column pivot (0.90 left exit, 0.10 right entry, 0.50 center) expressed in tile-local coords
                // so all 2 or 3 tiles in a Bento column scale harmoniously together as a single unit.
                float pivotXFrac = itemStart < 0.0 ? 0.90f : (itemEnd > viewportEnd ? 0.10f : 0.50f);
                float centerX = pivotXFrac * (float)layout.ColumnWidth;
                float centerY = (float)((layout.ColumnTotalHeight * 0.5) - layout.TileTop);

                double targetOpacity = itemStart < 0.0 ? (1.0 - (0.25 * leftFraction)) : 1.0;

                var targetCenter = new Vector3(centerX, centerY, 0f);
                if (Vector3.DistanceSquared(fe.CenterPoint, targetCenter) > 0.25f)
                {
                    fe.CenterPoint = targetCenter;
                }

                if (Math.Abs(fe.Scale.X - targetScale) > 0.002f)
                {
                    fe.Scale = new Vector3(targetScale, targetScale, 1.0f);
                }

                if (Math.Abs(fe.Opacity - targetOpacity) > 0.004)
                {
                    fe.Opacity = targetOpacity;
                }
            }

            return;
        }

        var cards = new List<UIElement>(16);
        CollectCarouselCardElements(contentRoot, cards);

        foreach (UIElement element in cards)
        {
            if (element is not FrameworkElement fe || fe.ActualWidth <= 2 || fe.ActualHeight <= 2)
            {
                continue;
            }

            try
            {
                float w = (float)fe.ActualWidth;
                float h = (float)fe.ActualHeight;

                // Transform unscaled center point so scaling around CenterPoint causes zero coordinate feedback.
                Point centerInSv = fe.TransformToVisual(sv).TransformPoint(new Point(w * 0.5, h * 0.5));
                double cardLeft = centerInSv.X - (w * 0.5);
                double cardRight = centerInSv.X + (w * 0.5);

                double denom = Math.Max(42.0, w);
                double leftFraction = canScrollLeft && cardLeft < 0.0
                    ? Math.Clamp(-cardLeft / denom, 0.0, 1.0)
                    : 0.0;
                double rightFraction = canScrollRight && cardRight > hostWidth
                    ? Math.Clamp((cardRight - hostWidth) / denom, 0.0, 1.0)
                    : 0.0;

                // Edge-anchored pivot (0.88 left exit, 0.12 right exit, 0.50 center) matching Home Recently Played feel
                float pivotX = leftFraction > 0.0 ? (w * 0.88f) : (rightFraction > 0.0 ? (w * 0.12f) : (w * 0.5f));
                var targetCenter = new Vector3(pivotX, h * 0.5f, 0f);
                if (Vector3.DistanceSquared(fe.CenterPoint, targetCenter) > 0.25f)
                {
                    fe.CenterPoint = targetCenter;
                }

                double leftScale = 1.0 - (0.24 * leftFraction);
                double rightScale = 1.0 - (0.18 * rightFraction);
                float targetScale = (float)Math.Clamp(leftScale * rightScale, 0.74, 1.0);

                double leftOpacity = leftFraction > 0.0 ? (1.0 - (0.48 * leftFraction)) : 1.0;
                double rightOpacity = rightFraction > 0.0 ? (1.0 - (0.42 * rightFraction)) : 1.0;
                double targetOpacity = Math.Clamp(leftOpacity * rightOpacity, 0.42, 1.0);

                if (Math.Abs(fe.Scale.X - targetScale) > 0.002f)
                {
                    fe.Scale = new Vector3(targetScale, targetScale, 1.0f);
                }

                if (Math.Abs(fe.Opacity - targetOpacity) > 0.005)
                {
                    fe.Opacity = targetOpacity;
                }
            }
            catch
            {
                // Ignore transient detachments while virtualized items update.
            }
        }
    }

    private static void CollectCarouselCardElements(UIElement? root, List<UIElement> results)
    {
        if (root == null)
        {
            return;
        }

        if (root is ItemsControl itemsControl)
        {
            if (itemsControl.ItemsPanelRoot is Panel itemsPanel)
            {
                foreach (UIElement child in itemsPanel.Children)
                {
                    results.Add(child);
                }
            }

            return;
        }

        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (child is ItemsControl)
                {
                    CollectCarouselCardElements(child, results);
                }
                else
                {
                    results.Add(child);
                }
            }

            return;
        }

        results.Add(root);
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? start)
    {
        DependencyObject? current = start;
        while (current != null)
        {
            if (current is ScrollViewer sv)
            {
                return sv;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void OnTopPickCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TopPickItem pick })
        {
            await ViewModel.PlayOrOpenTopPickAsync(pick);
        }
    }

    private async void OnRecommendedPlaylistCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RecommendedPlaylistItem playlist })
        {
            await OpenOrPlayRecommendedPlaylistAsync(playlist);
        }
    }

    private async void OnRadioStationCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RadioStationItem station })
        {
            await ViewModel.PlayRadioStationAsync(station);
        }
    }

    private async void OnNewReleaseCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NewReleaseItem release })
        {
            await ViewModel.OpenOrPlayNewReleaseAsync(release);
        }
    }

    #region Redesigned Now Playing Sidebar Interactions

    private void StopNowPlayingSidebarTimers()
    {
        _coverTransportHideTimer?.Stop();
        _volumeSpringTimer?.Stop();
        _volumeCollapseDelayTimer?.Stop();
        _eqAnimTimer?.Stop();
        _queueAutoScrollTimer?.Stop();
        CommitPendingQueueMoveIfNeeded();
    }

    private async Task UpdateNowPlayingAmbientPaletteAsync()
    {
        int gen = ++_ambientAnimGeneration;
        Color fallbackAccent = ColorHelperEx.ParseHex(ViewModel.ActiveAccentColorHex, Color.FromArgb(255, 239, 68, 68));
        var track = ViewModel.CurrentTrack;
        string? uri = track is not null && track.Id != "empty_state_track" ? track.SafeCoverArtUri : null;

        var palette = await ColorHelperEx.ExtractNowPlayingAmbientPaletteAsync(uri, fallbackAccent);
        if (gen != _ambientAnimGeneration)
        {
            return;
        }

        _currentAmbientPalette = palette;
        bool isLight = ViewModel.IsLightMode;
        Color startAmbient = _currentAmbientColor;
        Color targetAmbient = isLight ? palette.LightAmbient : palette.DarkAmbient;
        Color targetVibrant = palette.VibrantAccent;

        LiquidGlassBackdropManager.UpdateAmbientPalette(
            targetAmbient,
            Color.FromArgb(255, (byte)(targetAmbient.R * 0.72), (byte)(targetAmbient.G * 0.72), (byte)(targetAmbient.B * 0.72)),
            Color.FromArgb(255, (byte)(targetAmbient.R * 0.45), (byte)(targetAmbient.G * 0.45), (byte)(targetAmbient.B * 0.45)),
            targetVibrant);

        if (SidebarSquigglyProgressBar is not null)
        {
            SidebarSquigglyProgressBar.AccentColor = targetVibrant;
        }

        if (FullscreenSquigglyProgressBar is not null)
        {
            FullscreenSquigglyProgressBar.AccentColor = targetVibrant;
        }

        if (MiniPlayerBorderHost is not null)
        {
            MiniPlayerBorderHost.AccentColor = targetVibrant;
        }

        if (CompactMiniPlayerBorderHost is not null)
        {
            CompactMiniPlayerBorderHost.AccentColor = targetVibrant;
        }

        if (SidebarSynchronizedLyricsControl is not null)
        {
            SidebarSynchronizedLyricsControl.AccentColor = targetVibrant;
        }

        if (FullscreenSynchronizedLyricsControl is not null)
        {
            FullscreenSynchronizedLyricsControl.AccentColor = targetVibrant;
        }

        UpdateLyricsStateAccentColor(targetVibrant);

        UpdateSidebarAccentTintElements(targetVibrant);

        if (DataUsageChart is not null)
        {
            DataUsageChart.AccentColor = targetVibrant;
        }

        const int steps = 20;
        for (int i = 1; i <= steps; i++)
        {
            if (gen != _ambientAnimGeneration)
            {
                return;
            }

            double t = 1.0 - Math.Pow(1.0 - ((double)i / steps), 3.0);
            Color c = LerpColor(startAmbient, targetAmbient, t);
            _currentAmbientColor = c;
            ApplyNowPlayingAmbientColorStops(c, isLight);
            await Task.Delay(18);
        }

        if (gen == _ambientAnimGeneration)
        {
            _currentAmbientColor = targetAmbient;
            ApplyNowPlayingAmbientColorStops(targetAmbient, isLight);
        }
    }

    private void ApplyNowPlayingAmbientColorStops(Color ambient, bool isLight)
    {
        if (NowPlayingAmbientBackgroundBrush is not null)
        {
            NowPlayingAmbientBackgroundBrush.Color = ambient;
        }

        byte topAlpha = isLight ? (byte)185 : (byte)180;
        if (AmbientStopTop is not null) AmbientStopTop.Color = Color.FromArgb(topAlpha, ambient.R, ambient.G, ambient.B);
        if (AmbientStopTopFade is not null) AmbientStopTopFade.Color = Color.FromArgb(0, ambient.R, ambient.G, ambient.B);
        if (AmbientStopMidClear is not null) AmbientStopMidClear.Color = Color.FromArgb(0, ambient.R, ambient.G, ambient.B);
        if (AmbientStopMidBlend is not null) AmbientStopMidBlend.Color = Color.FromArgb(165, ambient.R, ambient.G, ambient.B);
        if (AmbientStopNearBottom is not null) AmbientStopNearBottom.Color = Color.FromArgb(236, ambient.R, ambient.G, ambient.B);
        if (AmbientStopBottom is not null) AmbientStopBottom.Color = Color.FromArgb(255, ambient.R, ambient.G, ambient.B);

        // Fullscreen Luminous Backdrop (Matching Android NowPlayingBackground.kt in BOTH Dark & Light Modes)
        if (FullscreenAmbientBackgroundBrush is not null)
        {
            FullscreenAmbientBackgroundBrush.Color = ambient;
        }

        byte fsS0 = isLight ? (byte)20 : (byte)41;   // 0.08 Light / 0.16 Dark
        byte fsS2 = isLight ? (byte)26 : (byte)36;   // 0.10 Light / 0.14 Dark
        byte fsS3 = isLight ? (byte)92 : (byte)107;  // 0.36 Light / 0.42 Dark
        byte fsS4 = isLight ? (byte)184 : (byte)194; // 0.72 Light / 0.76 Dark
        byte fsS5 = isLight ? (byte)224 : (byte)230; // 0.88 Light / 0.90 Dark
        if (FullscreenScrimStop0 is not null) FullscreenScrimStop0.Color = Color.FromArgb(fsS0, ambient.R, ambient.G, ambient.B);
        if (FullscreenScrimStop1 is not null) FullscreenScrimStop1.Color = Color.FromArgb(0, ambient.R, ambient.G, ambient.B);
        if (FullscreenScrimStop2 is not null) FullscreenScrimStop2.Color = Color.FromArgb(fsS2, ambient.R, ambient.G, ambient.B);
        if (FullscreenScrimStop3 is not null) FullscreenScrimStop3.Color = Color.FromArgb(fsS3, ambient.R, ambient.G, ambient.B);
        if (FullscreenScrimStop4 is not null) FullscreenScrimStop4.Color = Color.FromArgb(fsS4, ambient.R, ambient.G, ambient.B);
        if (FullscreenScrimStop5 is not null) FullscreenScrimStop5.Color = Color.FromArgb(fsS5, ambient.R, ambient.G, ambient.B);

        // Subtle Bottom Album Primary-Color Gradient rising smoothly from the bottom edge
        Color vibrant = _currentAmbientPalette.VibrantAccent;
        byte bottomMidA = isLight ? (byte)36 : (byte)41;  // 0.14 Light / 0.16 Dark
        byte bottomEdgeA = isLight ? (byte)61 : (byte)77; // 0.24 Light / 0.30 Dark
        if (FullscreenBottomTintStop0 is not null) FullscreenBottomTintStop0.Color = Color.FromArgb(0, vibrant.R, vibrant.G, vibrant.B);
        if (FullscreenBottomTintStop1 is not null) FullscreenBottomTintStop1.Color = Color.FromArgb(0, vibrant.R, vibrant.G, vibrant.B);
        if (FullscreenBottomTintStop2 is not null) FullscreenBottomTintStop2.Color = Color.FromArgb(bottomMidA, vibrant.R, vibrant.G, vibrant.B);
        if (FullscreenBottomTintStop3 is not null) FullscreenBottomTintStop3.Color = Color.FromArgb(bottomEdgeA, vibrant.R, vibrant.G, vibrant.B);

        // Soft Horizontal Edge Vignette
        byte vigEdge = isLight ? (byte)36 : (byte)77; // 0.14 Light / 0.30 Dark
        byte vigSoft = isLight ? (byte)10 : (byte)26; // 0.04 Light / 0.10 Dark
        if (FullscreenVignetteStop0 is not null) FullscreenVignetteStop0.Color = Color.FromArgb(vigEdge, ambient.R, ambient.G, ambient.B);
        if (FullscreenVignetteStop1 is not null) FullscreenVignetteStop1.Color = Color.FromArgb(vigSoft, ambient.R, ambient.G, ambient.B);
        if (FullscreenVignetteStop2 is not null) FullscreenVignetteStop2.Color = Color.FromArgb(0, ambient.R, ambient.G, ambient.B);
        if (FullscreenVignetteStop3 is not null) FullscreenVignetteStop3.Color = Color.FromArgb(vigEdge, ambient.R, ambient.G, ambient.B);
    }

    private void OnArtistWordmarkImageOpened(object sender, RoutedEventArgs e)
    {
        if (ArtistWordmarkImage is null)
        {
            return;
        }

        if (ViewModel.CurrentArtistDetail?.HasCustomTitleLogo == true)
        {
            ArtistWordmarkImage.Visibility = Visibility.Visible;
            ArtistWordmarkImage.Opacity = 1.0;
            if (ArtistTitleTextBlock is not null)
            {
                ArtistTitleTextBlock.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnArtistWordmarkImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (ArtistWordmarkImage is not null)
        {
            ArtistWordmarkImage.Visibility = Visibility.Collapsed;
        }

        if (ArtistTitleTextBlock is not null)
        {
            ArtistTitleTextBlock.Visibility = Visibility.Visible;
            ArtistTitleTextBlock.Opacity = 1.0;
        }
    }

    private void OnArtistAmbientSourceChanged(object? sender, (string? ImageUri, string? SeedHex) source)
    {
        _lastArtistAmbientImageUri = source.ImageUri;
        _lastArtistAmbientSeedHex = source.SeedHex;
        DispatcherQueue.TryEnqueue(() => _ = ApplyArtistPagePaletteAsync(source.ImageUri, source.SeedHex, animate: true));
    }

    private Task RefreshArtistPagePaletteForThemeAsync()
    {
        if (ViewModel.ActiveSubView != WorkspaceSubView.ArtistDetail)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_lastArtistAmbientImageUri) &&
            string.IsNullOrWhiteSpace(_lastArtistAmbientSeedHex))
        {
            ArtistPagePalette def = ArtistPagePalette.CreateDefault(ViewModel.IsLightMode);
            _currentArtistPalette = def;
            ApplyArtistPagePaletteImmediate(def);
            return Task.CompletedTask;
        }

        return ApplyArtistPagePaletteAsync(_lastArtistAmbientImageUri, _lastArtistAmbientSeedHex, animate: true);
    }

    private async Task ApplyArtistPagePaletteAsync(string? imageUri, string? seedHex, bool animate)
    {
        int gen = ++_artistPaletteGeneration;
        bool isLight = ViewModel.IsLightMode;
        ArtistPagePalette targetPalette = await ColorHelperEx.ExtractArtistPagePaletteAsync(imageUri, seedHex, isLight);
        if (gen != _artistPaletteGeneration)
        {
            return;
        }

        ArtistPagePalette startPalette = _currentArtistPalette;
        if (!animate)
        {
            _currentArtistPalette = targetPalette;
            ApplyArtistPagePaletteImmediate(targetPalette);
            return;
        }

        const int steps = 20;
        for (int i = 1; i <= steps; i++)
        {
            if (gen != _artistPaletteGeneration)
            {
                return;
            }

            double t = 1.0 - Math.Pow(1.0 - ((double)i / steps), 3.0);
            ArtistPagePalette frame = new(
                LerpColor(startPalette.MainBackground, targetPalette.MainBackground, t),
                LerpColor(startPalette.HeroMidStop, targetPalette.HeroMidStop, t),
                LerpColor(startPalette.CardSurface, targetPalette.CardSurface, t),
                LerpColor(startPalette.CardBorder, targetPalette.CardBorder, t),
                LerpColor(startPalette.PrimaryInk, targetPalette.PrimaryInk, t),
                LerpColor(startPalette.SecondaryInk, targetPalette.SecondaryInk, t),
                LerpColor(startPalette.AccentColor, targetPalette.AccentColor, t),
                targetPalette.IsLightSurface);

            _currentArtistPalette = frame;
            ApplyArtistPagePaletteImmediate(frame);
            await Task.Delay(18);
        }

        if (gen == _artistPaletteGeneration)
        {
            _currentArtistPalette = targetPalette;
            ApplyArtistPagePaletteImmediate(targetPalette);
        }
    }

    private void ApplyArtistPagePaletteImmediate(ArtistPagePalette palette)
    {
        Color mainBg = palette.MainBackground;
        Color mid = palette.HeroMidStop;

        if (_artistAmbientBackgroundBrush is not null) _artistAmbientBackgroundBrush.Color = mainBg;
        if (_artistCardBackgroundBrush is not null) _artistCardBackgroundBrush.Color = palette.CardSurface;
        if (_artistCardBorderBrush is not null) _artistCardBorderBrush.Color = palette.CardBorder;
        if (_artistPrimaryInkBrush is not null) _artistPrimaryInkBrush.Color = palette.PrimaryInk;
        if (_artistSecondaryInkBrush is not null) _artistSecondaryInkBrush.Color = palette.SecondaryInk;
        if (_artistSkeletonFillBrush is not null)
        {
            byte skAlpha = palette.IsLightSurface ? (byte)34 : (byte)42;
            _artistSkeletonFillBrush.Color = Color.FromArgb(skAlpha, palette.PrimaryInk.R, palette.PrimaryInk.G, palette.PrimaryInk.B);
        }

        if (ArtistHeroStop0 is not null) ArtistHeroStop0.Color = Color.FromArgb(24, mid.R, mid.G, mid.B);
        if (ArtistHeroStop1 is not null) ArtistHeroStop1.Color = Color.FromArgb(0, mid.R, mid.G, mid.B);
        if (ArtistHeroStop2 is not null) ArtistHeroStop2.Color = Color.FromArgb(140, mid.R, mid.G, mid.B);
        if (ArtistHeroStop3 is not null) ArtistHeroStop3.Color = Color.FromArgb(232, mainBg.R, mainBg.G, mainBg.B);
        if (ArtistHeroStop4 is not null) ArtistHeroStop4.Color = Color.FromArgb(255, mainBg.R, mainBg.G, mainBg.B);
    }

    private void UpdateArtistSkeletonShimmerState()
    {
        if (_artistSkeletonShimmerTimer is null)
        {
            return;
        }

        if (ViewModel.IsArtistDetailLoading && ViewModel.ActiveSubView == WorkspaceSubView.ArtistDetail)
        {
            _artistSkeletonShimmerPhase = 0;
            if (!_artistSkeletonShimmerTimer.IsRunning)
            {
                _artistSkeletonShimmerTimer.Start();
            }
        }
        else
        {
            _artistSkeletonShimmerTimer.Stop();
            if (ArtistSkeletonContainer is not null)
            {
                ArtistSkeletonContainer.Opacity = 1.0;
            }
        }
    }

    private void OnArtistSkeletonShimmerTick(DispatcherQueueTimer sender, object args)
    {
        if (!ViewModel.IsArtistDetailLoading || ViewModel.ActiveSubView != WorkspaceSubView.ArtistDetail)
        {
            sender.Stop();
            if (ArtistSkeletonContainer is not null)
            {
                ArtistSkeletonContainer.Opacity = 1.0;
            }
            return;
        }

        _artistSkeletonShimmerPhase += 0.14;
        if (ArtistSkeletonContainer is not null)
        {
            ArtistSkeletonContainer.Opacity = 0.68 + (0.28 * (0.5 + (0.5 * Math.Sin(_artistSkeletonShimmerPhase))));
        }
    }

    private void OnAlbumAmbientSourceChanged(object? sender, string? coverUri)
    {
        _lastAlbumAmbientCoverUri = coverUri;
        DispatcherQueue.TryEnqueue(() => _ = ApplyAlbumPagePaletteAsync(coverUri, animate: true));
    }

    private Task RefreshAlbumPagePaletteForThemeAsync()
    {
        if (ViewModel.ActiveSubView != WorkspaceSubView.AlbumDetail)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_lastAlbumAmbientCoverUri))
        {
            AlbumPagePalette def = AlbumPagePalette.CreateDefault(ViewModel.IsLightMode);
            _currentAlbumPalette = def;
            ApplyAlbumPagePaletteImmediate(def);
            return Task.CompletedTask;
        }

        return ApplyAlbumPagePaletteAsync(_lastAlbumAmbientCoverUri, animate: true);
    }

    private async Task ApplyAlbumPagePaletteAsync(string? coverUri, bool animate)
    {
        int gen = ++_albumPaletteGeneration;
        bool isLight = ViewModel.IsLightMode;
        AlbumPagePalette targetPalette = await ColorHelperEx.ExtractAlbumPagePaletteAsync(coverUri, isLight);
        if (gen != _albumPaletteGeneration)
        {
            return;
        }

        AlbumPagePalette startPalette = _currentAlbumPalette;
        if (!animate)
        {
            _currentAlbumPalette = targetPalette;
            ApplyAlbumPagePaletteImmediate(targetPalette);
            return;
        }

        const int steps = 20;
        for (int i = 1; i <= steps; i++)
        {
            if (gen != _albumPaletteGeneration)
            {
                return;
            }

            double t = 1.0 - Math.Pow(1.0 - ((double)i / steps), 3.0);
            AlbumPagePalette frame = new(
                LerpColor(startPalette.MainBackground, targetPalette.MainBackground, t),
                LerpColor(startPalette.CardSurface, targetPalette.CardSurface, t),
                LerpColor(startPalette.CardBorder, targetPalette.CardBorder, t),
                LerpColor(startPalette.PrimaryInk, targetPalette.PrimaryInk, t),
                LerpColor(startPalette.SecondaryInk, targetPalette.SecondaryInk, t),
                LerpColor(startPalette.ArtistLinkAccent, targetPalette.ArtistLinkAccent, t),
                LerpColor(startPalette.ActiveTrackRowBackground, targetPalette.ActiveTrackRowBackground, t),
                LerpColor(startPalette.ActiveTrackAccent, targetPalette.ActiveTrackAccent, t),
                targetPalette.IsLightSurface);

            _currentAlbumPalette = frame;
            ApplyAlbumPagePaletteImmediate(frame);
            await Task.Delay(18);
        }

        if (gen == _albumPaletteGeneration)
        {
            _currentAlbumPalette = targetPalette;
            ApplyAlbumPagePaletteImmediate(targetPalette);
        }
    }

    private void ApplyAlbumPagePaletteImmediate(AlbumPagePalette palette)
    {
        if (_albumAmbientBackgroundBrush is not null) _albumAmbientBackgroundBrush.Color = palette.MainBackground;
        if (_albumCardBackgroundBrush is not null) _albumCardBackgroundBrush.Color = palette.CardSurface;
        if (_albumCardBorderBrush is not null) _albumCardBorderBrush.Color = palette.CardBorder;
        if (_albumPrimaryInkBrush is not null) _albumPrimaryInkBrush.Color = palette.PrimaryInk;
        if (_albumSecondaryInkBrush is not null) _albumSecondaryInkBrush.Color = palette.SecondaryInk;
        if (_albumArtistLinkBrush is not null) _albumArtistLinkBrush.Color = palette.ArtistLinkAccent;
        if (_albumTrackActiveBackgroundBrush is not null) _albumTrackActiveBackgroundBrush.Color = palette.ActiveTrackRowBackground;
        if (_albumTrackActiveAccentBrush is not null) _albumTrackActiveAccentBrush.Color = palette.ActiveTrackAccent;
        if (_albumSkeletonFillBrush is not null)
        {
            byte skAlpha = palette.IsLightSurface ? (byte)28 : (byte)38;
            _albumSkeletonFillBrush.Color = Color.FromArgb(skAlpha, palette.PrimaryInk.R, palette.PrimaryInk.G, palette.PrimaryInk.B);
        }
    }

    private void UpdateAlbumSkeletonShimmerState()
    {
        if (_albumSkeletonShimmerTimer is null)
        {
            return;
        }

        if (ViewModel.IsAlbumDetailLoading && ViewModel.ActiveSubView == WorkspaceSubView.AlbumDetail)
        {
            _albumSkeletonShimmerPhase = 0;
            if (!_albumSkeletonShimmerTimer.IsRunning)
            {
                _albumSkeletonShimmerTimer.Start();
            }
        }
        else
        {
            _albumSkeletonShimmerTimer.Stop();
            if (AlbumSkeletonContainer is not null)
            {
                AlbumSkeletonContainer.Opacity = 1.0;
            }
        }
    }

    private void OnAlbumSkeletonShimmerTick(DispatcherQueueTimer sender, object args)
    {
        if (!ViewModel.IsAlbumDetailLoading || ViewModel.ActiveSubView != WorkspaceSubView.AlbumDetail)
        {
            sender.Stop();
            if (AlbumSkeletonContainer is not null)
            {
                AlbumSkeletonContainer.Opacity = 1.0;
            }
            return;
        }

        _albumSkeletonShimmerPhase += 0.14;
        if (AlbumSkeletonContainer is not null)
        {
            AlbumSkeletonContainer.Opacity = 0.68 + (0.28 * (0.5 + (0.5 * Math.Sin(_albumSkeletonShimmerPhase))));
        }
    }

    private void OnPlaylistAmbientSourceChanged(object? sender, string? coverUri)
    {
        _lastPlaylistAmbientCoverUri = coverUri;
        DispatcherQueue.TryEnqueue(() => _ = ApplyPlaylistPagePaletteAsync(coverUri, animate: true));
    }

    private Task RefreshPlaylistPagePaletteForThemeAsync()
    {
        if (ViewModel.ActiveSubView != WorkspaceSubView.PlaylistDetail)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_lastPlaylistAmbientCoverUri))
        {
            AlbumPagePalette def = AlbumPagePalette.CreateDefault(ViewModel.IsLightMode);
            _currentPlaylistPalette = def;
            ApplyPlaylistPagePaletteImmediate(def);
            return Task.CompletedTask;
        }

        return ApplyPlaylistPagePaletteAsync(_lastPlaylistAmbientCoverUri, animate: true);
    }

    private async Task ApplyPlaylistPagePaletteAsync(string? coverUri, bool animate)
    {
        int gen = ++_playlistPaletteGeneration;
        bool isLight = ViewModel.IsLightMode;
        AlbumPagePalette defaultPalette = AlbumPagePalette.CreateDefault(isLight);
        AlbumPagePalette targetPalette = await ColorHelperEx.ExtractAlbumPagePaletteAsync(coverUri, isLight);

        if (gen != _playlistPaletteGeneration)
        {
            return;
        }

        // If primary cover failed to resolve/decode, walk remaining collage covers before settling on default
        if (targetPalette.Equals(defaultPalette) && ViewModel.CurrentPlaylistCard is { } card)
        {
            foreach (string fallbackUri in card.GetFallbackCoverUris())
            {
                if (string.IsNullOrWhiteSpace(fallbackUri) ||
                    string.Equals(fallbackUri, coverUri, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidatePalette = await ColorHelperEx.ExtractAlbumPagePaletteAsync(fallbackUri, isLight);
                if (gen != _playlistPaletteGeneration)
                {
                    return;
                }

                if (!candidatePalette.Equals(defaultPalette))
                {
                    targetPalette = candidatePalette;
                    break;
                }
            }
        }

        AlbumPagePalette startPalette = _currentPlaylistPalette;
        if (!animate)
        {
            _currentPlaylistPalette = targetPalette;
            ApplyPlaylistPagePaletteImmediate(targetPalette);
            return;
        }

        const int steps = 20;
        for (int i = 1; i <= steps; i++)
        {
            if (gen != _playlistPaletteGeneration)
            {
                return;
            }

            double t = 1.0 - Math.Pow(1.0 - ((double)i / steps), 3.0);
            AlbumPagePalette frame = new(
                LerpColor(startPalette.MainBackground, targetPalette.MainBackground, t),
                LerpColor(startPalette.CardSurface, targetPalette.CardSurface, t),
                LerpColor(startPalette.CardBorder, targetPalette.CardBorder, t),
                LerpColor(startPalette.PrimaryInk, targetPalette.PrimaryInk, t),
                LerpColor(startPalette.SecondaryInk, targetPalette.SecondaryInk, t),
                LerpColor(startPalette.ArtistLinkAccent, targetPalette.ArtistLinkAccent, t),
                LerpColor(startPalette.ActiveTrackRowBackground, targetPalette.ActiveTrackRowBackground, t),
                LerpColor(startPalette.ActiveTrackAccent, targetPalette.ActiveTrackAccent, t),
                targetPalette.IsLightSurface);

            _currentPlaylistPalette = frame;
            ApplyPlaylistPagePaletteImmediate(frame);
            await Task.Delay(18);
        }

        if (gen == _playlistPaletteGeneration)
        {
            _currentPlaylistPalette = targetPalette;
            ApplyPlaylistPagePaletteImmediate(targetPalette);
        }
    }

    private void ApplyPlaylistPagePaletteImmediate(AlbumPagePalette palette)
    {
        if (_playlistAmbientBackgroundBrush is not null) _playlistAmbientBackgroundBrush.Color = palette.MainBackground;
        if (_playlistCardBackgroundBrush is not null) _playlistCardBackgroundBrush.Color = palette.CardSurface;
        if (_playlistCardBorderBrush is not null) _playlistCardBorderBrush.Color = palette.CardBorder;
        if (_playlistPrimaryInkBrush is not null) _playlistPrimaryInkBrush.Color = palette.PrimaryInk;
        if (_playlistSecondaryInkBrush is not null) _playlistSecondaryInkBrush.Color = palette.SecondaryInk;
        if (_playlistArtistLinkBrush is not null) _playlistArtistLinkBrush.Color = palette.ArtistLinkAccent;
        if (_playlistTrackActiveBackgroundBrush is not null) _playlistTrackActiveBackgroundBrush.Color = palette.ActiveTrackRowBackground;
        if (_playlistTrackActiveAccentBrush is not null) _playlistTrackActiveAccentBrush.Color = palette.ActiveTrackAccent;
        if (_playlistSkeletonFillBrush is not null)
        {
            byte skAlpha = palette.IsLightSurface ? (byte)28 : (byte)38;
            _playlistSkeletonFillBrush.Color = Color.FromArgb(skAlpha, palette.PrimaryInk.R, palette.PrimaryInk.G, palette.PrimaryInk.B);
        }
    }

    private void UpdatePlaylistRelatedShimmerState()
    {
        if (_playlistRelatedShimmerTimer is null)
        {
            return;
        }

        if (ViewModel.IsPlaylistRelatedLoading &&
            ViewModel.PlaylistDetailViewVisibility == Visibility.Visible &&
            ViewModel.PlaylistRelatedTracks.Count == 0)
        {
            _playlistRelatedShimmerPhase = 0;
            if (!_playlistRelatedShimmerTimer.IsRunning)
            {
                _playlistRelatedShimmerTimer.Start();
            }
        }
        else
        {
            _playlistRelatedShimmerTimer.Stop();
            if (PlaylistRelatedSkeletonHost is not null)
            {
                PlaylistRelatedSkeletonHost.Opacity = 1.0;
            }
        }
    }

    private void OnPlaylistRelatedShimmerTick(DispatcherQueueTimer sender, object args)
    {
        if (!ViewModel.IsPlaylistRelatedLoading ||
            ViewModel.PlaylistDetailViewVisibility != Visibility.Visible ||
            ViewModel.PlaylistRelatedTracks.Count > 0)
        {
            sender.Stop();
            if (PlaylistRelatedSkeletonHost is not null)
            {
                PlaylistRelatedSkeletonHost.Opacity = 1.0;
            }
            return;
        }

        _playlistRelatedShimmerPhase += 0.14;
        if (PlaylistRelatedSkeletonHost is not null)
        {
            PlaylistRelatedSkeletonHost.Opacity = 0.68 + (0.28 * (0.5 + (0.5 * Math.Sin(_playlistRelatedShimmerPhase))));
        }
    }

    private void UpdateLikedRecommendationsShimmerState()
    {
        if (_likedRecsShimmerTimer is null)
        {
            return;
        }

        if (ViewModel.IsLoadingLikedRecommendations &&
            ViewModel.LikedViewVisibility == Visibility.Visible &&
            ViewModel.LikedRecommendations.Count == 0)
        {
            _likedRecsShimmerPhase = 0;
            if (!_likedRecsShimmerTimer.IsRunning)
            {
                _likedRecsShimmerTimer.Start();
            }
        }
        else
        {
            _likedRecsShimmerTimer.Stop();
            if (LikedRecommendationsSkeletonHost is not null)
            {
                LikedRecommendationsSkeletonHost.Opacity = 1.0;
            }
        }
    }

    private void OnLikedRecommendationsShimmerTick(DispatcherQueueTimer sender, object args)
    {
        if (!ViewModel.IsLoadingLikedRecommendations ||
            ViewModel.LikedViewVisibility != Visibility.Visible ||
            ViewModel.LikedRecommendations.Count > 0)
        {
            sender.Stop();
            if (LikedRecommendationsSkeletonHost is not null)
            {
                LikedRecommendationsSkeletonHost.Opacity = 1.0;
            }
            return;
        }

        _likedRecsShimmerPhase += 0.14;
        if (LikedRecommendationsSkeletonHost is not null)
        {
            LikedRecommendationsSkeletonHost.Opacity = 0.68 + (0.28 * (0.5 + (0.5 * Math.Sin(_likedRecsShimmerPhase))));
        }
    }

    private void UpdateSidebarAccentTintElements(Color vibrant)
    {
        if (SidebarActiveQueueCardBorder is not null)
        {
            SidebarActiveQueueCardBorder.Background = new SolidColorBrush(Color.FromArgb(34, vibrant.R, vibrant.G, vibrant.B));
            SidebarActiveQueueCardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(95, vibrant.R, vibrant.G, vibrant.B));
        }

        if (SidebarActiveCardTitleText is not null)
        {
            SidebarActiveCardTitleText.Foreground = new SolidColorBrush(vibrant);
        }

        if (SidebarActiveEqPill is not null)
        {
            SidebarActiveEqPill.Background = new SolidColorBrush(Color.FromArgb(40, vibrant.R, vibrant.G, vibrant.B));
        }

        var eqBrush = new SolidColorBrush(vibrant);
        if (EqBar1 is not null) EqBar1.Background = eqBrush;
        if (EqBar2 is not null) EqBar2.Background = eqBrush;
        if (EqBar3 is not null) EqBar3.Background = eqBrush;
        if (EqBar4 is not null) EqBar4.Background = eqBrush;

        if (SidebarLikeButtonIcon is not null)
        {
            if (ViewModel.CurrentTrack?.IsLiked == true)
            {
                SidebarLikeButtonIcon.Foreground = eqBrush;
            }
            else
            {
                SidebarLikeButtonIcon.ClearValue(FontIcon.ForegroundProperty);
            }
        }

        // Sync Fullscreen Now Playing Accent Elements
        var cardBg = new SolidColorBrush(Color.FromArgb(34, vibrant.R, vibrant.G, vibrant.B));
        var cardBorder = new SolidColorBrush(Color.FromArgb(95, vibrant.R, vibrant.G, vibrant.B));
        var pillBg = new SolidColorBrush(Color.FromArgb(44, vibrant.R, vibrant.G, vibrant.B));

        if (FullscreenActiveQueueCardBorder is not null)
        {
            FullscreenActiveQueueCardBorder.Background = cardBg;
            FullscreenActiveQueueCardBorder.BorderBrush = cardBorder;
        }

        if (FullscreenActiveCardTitleText is not null)
        {
            FullscreenActiveCardTitleText.Foreground = eqBrush;
        }

        if (FullscreenActiveEqPill is not null)
        {
            FullscreenActiveEqPill.Background = pillBg;
        }

        if (FullscreenEqBar1 is not null) FullscreenEqBar1.Background = eqBrush;
        if (FullscreenEqBar2 is not null) FullscreenEqBar2.Background = eqBrush;
        if (FullscreenEqBar3 is not null) FullscreenEqBar3.Background = eqBrush;
        if (FullscreenEqBar4 is not null) FullscreenEqBar4.Background = eqBrush;

        if (FullscreenPlayPauseAccentHalo is not null)
        {
            FullscreenPlayPauseAccentHalo.Background = eqBrush;
        }

        if (FullscreenLyricsActionHighlight is not null)
        {
            FullscreenLyricsActionHighlight.Background = pillBg;
            FullscreenLyricsActionHighlight.BorderBrush = cardBorder;
        }

        if (FullscreenQueueActionHighlight is not null)
        {
            FullscreenQueueActionHighlight.Background = pillBg;
            FullscreenQueueActionHighlight.BorderBrush = cardBorder;
        }

        if (FullscreenLyricsActionIcon is not null)
        {
            if (ViewModel.IsFullscreenLyricsActive)
            {
                FullscreenLyricsActionIcon.Foreground = eqBrush;
            }
            else
            {
                FullscreenLyricsActionIcon.ClearValue(FontIcon.ForegroundProperty);
            }
        }

        if (FullscreenQueueActionIcon is not null)
        {
            if (ViewModel.IsFullscreenQueueActive)
            {
                FullscreenQueueActionIcon.Foreground = eqBrush;
            }
            else
            {
                FullscreenQueueActionIcon.ClearValue(FontIcon.ForegroundProperty);
            }
        }

        if (FullscreenLikeButtonIcon is not null)
        {
            if (ViewModel.CurrentTrack?.IsLiked == true)
            {
                FullscreenLikeButtonIcon.Foreground = eqBrush;
            }
            else
            {
                FullscreenLikeButtonIcon.ClearValue(FontIcon.ForegroundProperty);
            }
        }
    }

    private void OnSidebarCoverImageOpened(object sender, RoutedEventArgs e)
    {
        if (_trackCanvasRevealed)
        {
            return;
        }

        if (sender is Microsoft.UI.Xaml.Controls.Image { Source: BitmapImage bmp } &&
            bmp.PixelWidth > 0 &&
            bmp.PixelHeight > 0)
        {
            UpdateSidebarCoverAspectHeight((double)bmp.PixelHeight / bmp.PixelWidth);
        }
    }

    private async void UpdateSidebarCoverAspectHeight(double aspectRatio)
    {
        if (SidebarCoverBackdropHost is null || SidebarCoverInteractiveStage is null)
        {
            return;
        }

        int gen = ++_coverAspectAnimGen;
        double width = SidebarCoverBackdropHost.ActualWidth > 10 ? SidebarCoverBackdropHost.ActualWidth : 324.0;
        double targetBackdropHeight = Math.Clamp(width * aspectRatio, 276.0, 386.0);
        double targetStageHeight = Math.Max(238.0, targetBackdropHeight - 38.0);

        double startBackdropHeight = SidebarCoverBackdropHost.Height;
        double startStageHeight = SidebarCoverInteractiveStage.Height;
        if (Math.Abs(startBackdropHeight - targetBackdropHeight) < 1.0)
        {
            return;
        }

        const int steps = 14;
        for (int i = 1; i <= steps; i++)
        {
            if (gen != _coverAspectAnimGen)
            {
                return;
            }

            double ease = 1.0 - Math.Pow(1.0 - ((double)i / steps), 3.0);
            SidebarCoverBackdropHost.Height = startBackdropHeight + ((targetBackdropHeight - startBackdropHeight) * ease);
            SidebarCoverInteractiveStage.Height = startStageHeight + ((targetStageHeight - startStageHeight) * ease);
            await Task.Delay(14);
        }

        if (gen == _coverAspectAnimGen)
        {
            SidebarCoverBackdropHost.Height = targetBackdropHeight;
            SidebarCoverInteractiveStage.Height = targetStageHeight;
        }
    }

    private void OnSidebarCoverStagePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInsideCoverStage = true;
        _coverTransportHideTimer?.Stop();
        if (!_isCoverTransportVisible)
        {
            AnimateSidebarTransportControls(show: true);
        }
    }

    private void OnSidebarCoverStagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInsideCoverStage = true;
        _coverTransportHideTimer?.Stop();
        if (!_isCoverTransportVisible)
        {
            AnimateSidebarTransportControls(show: true);
        }
    }

    private void OnSidebarCoverStagePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, NowPlayingSidebarPanel) &&
            NowPlayingSidebarPanel.ActualWidth > 0 &&
            NowPlayingSidebarPanel.ActualHeight > 0)
        {
            var pt = e.GetCurrentPoint(NowPlayingSidebarPanel).Position;
            if (pt.X >= 0 && pt.Y >= 0 && pt.X <= NowPlayingSidebarPanel.ActualWidth && pt.Y <= NowPlayingSidebarPanel.ActualHeight)
            {
                return;
            }
        }

        _isPointerInsideCoverStage = false;
        _coverTransportHideTimer ??= DispatcherQueue.CreateTimer();
        _coverTransportHideTimer.Stop();
        _coverTransportHideTimer.Interval = TimeSpan.FromSeconds(5);
        _coverTransportHideTimer.IsRepeating = false;
        _coverTransportHideTimer.Tick -= OnCoverTransportHideTimerTick;
        _coverTransportHideTimer.Tick += OnCoverTransportHideTimerTick;
        _coverTransportHideTimer.Start();
    }

    private void OnCoverTransportHideTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_isPointerInsideCoverStage && _isCoverTransportVisible)
        {
            AnimateSidebarTransportControls(show: false);
        }
    }

    private static void EnsureTransportSlotCenterPoint(FrameworkElement slot)
    {
        float w = (float)(slot.ActualWidth > 1.0
            ? slot.ActualWidth
            : (!double.IsNaN(slot.Width) && slot.Width > 1.0 ? slot.Width : 44.0));
        float h = (float)(slot.ActualHeight > 1.0
            ? slot.ActualHeight
            : (!double.IsNaN(slot.Height) && slot.Height > 1.0 ? slot.Height : 44.0));
        slot.CenterPoint = new Vector3(w * 0.5f, h * 0.5f, 0f);
    }

    private async void AnimateSidebarTransportControls(bool show)
    {
        if (SidebarTransportControlsHost is null)
        {
            return;
        }

        int gen = ++_transportAnimGen;
        _isCoverTransportVisible = show;

        FrameworkElement[] allSlots =
        [
            SidebarTransportSlotRepeat,
            SidebarTransportSlotPrev,
            SidebarTransportSlotPlayPause,
            SidebarTransportSlotNext,
            SidebarTransportSlotShuffle
        ];

        FrameworkElement[] centerWave = [SidebarTransportSlotPlayPause];
        FrameworkElement[] midWave = [SidebarTransportSlotPrev, SidebarTransportSlotNext];
        FrameworkElement[] outerWave = [SidebarTransportSlotRepeat, SidebarTransportSlotShuffle];

        FrameworkElement[][] orderedWaves = show
            ? [centerWave, midWave, outerWave]
            : [outerWave, midWave, centerWave];

        if (show)
        {
            foreach (var slot in allSlots)
            {
                if (slot is null) continue;
                EnsureTransportSlotCenterPoint(slot);
                slot.Opacity = 0.0;
                slot.Translation = new Vector3(0f, 18f, 0f);
                slot.Scale = new Vector3(0.45f, 0.45f, 1f);
            }

            SidebarTransportControlsHost.Visibility = Visibility.Visible;
            SidebarTransportControlsHost.IsHitTestVisible = true;
            SidebarTransportControlsHost.UpdateLayout();

            foreach (var slot in allSlots)
            {
                if (slot is null) continue;
                EnsureTransportSlotCenterPoint(slot);
                RefreshTransportSlotGlass(slot);
            }

            const int totalMs = 380;
            const int stepMs = 15;
            const float waveDelayMs = 65f;
            const float waveDurationMs = 240f;

            for (int elapsed = 0; elapsed <= totalMs; elapsed += stepMs)
            {
                if (gen != _transportAnimGen)
                {
                    return;
                }

                for (int w = 0; w < orderedWaves.Length; w++)
                {
                    float localMs = elapsed - (w * waveDelayMs);
                    float p = Math.Clamp(localMs / waveDurationMs, 0f, 1f);
                    float spring = p <= 0f
                        ? 0f
                        : (p >= 1f ? 1f : 1f - (MathF.Exp(-6.2f * p) * MathF.Cos(9.5f * p)));
                    double opacity = p <= 0f ? 0.0 : Math.Clamp(p * 2.2f, 0.0, 1.0);
                    float offsetY = 18f * (1f - spring);
                    float scale = 0.45f + (0.55f * spring);

                    foreach (var slot in orderedWaves[w])
                    {
                        if (slot is null) continue;
                        slot.Opacity = opacity;
                        slot.Translation = new Vector3(0f, offsetY, 0f);
                        slot.Scale = new Vector3(scale, scale, 1f);
                    }
                }

                await Task.Delay(stepMs);
            }

            if (gen == _transportAnimGen)
            {
                foreach (var slot in allSlots)
                {
                    if (slot is null) continue;
                    EnsureTransportSlotCenterPoint(slot);
                    slot.Opacity = 1.0;
                    slot.Translation = Vector3.Zero;
                    slot.Scale = Vector3.One;
                    RefreshTransportSlotGlass(slot);
                }
            }
        }
        else
        {
            SidebarTransportControlsHost.IsHitTestVisible = false;

            const int totalMs = 340;
            const int stepMs = 15;
            const float waveDelayMs = 65f;
            const float waveDurationMs = 200f;

            for (int elapsed = 0; elapsed <= totalMs; elapsed += stepMs)
            {
                if (gen != _transportAnimGen)
                {
                    return;
                }

                for (int w = 0; w < orderedWaves.Length; w++)
                {
                    float localMs = elapsed - (w * waveDelayMs);
                    float p = Math.Clamp(localMs / waveDurationMs, 0f, 1f);
                    float offsetY = p <= 0f
                        ? 0f
                        : (p < 0.25f
                            ? (-3.5f * (p / 0.25f))
                            : (-3.5f + (21.5f * MathF.Pow((p - 0.25f) / 0.75f, 1.6f))));
                    float scale = p <= 0f ? 1f : 1f - (0.58f * MathF.Pow(p, 1.4f));
                    double opacity = p <= 0f ? 1.0 : Math.Clamp(1f - MathF.Pow(p, 1.3f), 0.0, 1.0);

                    foreach (var slot in orderedWaves[w])
                    {
                        if (slot is null) continue;
                        EnsureTransportSlotCenterPoint(slot);
                        slot.Opacity = opacity;
                        slot.Translation = new Vector3(0f, offsetY, 0f);
                        slot.Scale = new Vector3(scale, scale, 1f);
                    }
                }

                await Task.Delay(stepMs);
            }

            if (gen == _transportAnimGen && !_isCoverTransportVisible)
            {
                foreach (var slot in allSlots)
                {
                    if (slot is null) continue;
                    slot.Opacity = 0.0;
                    slot.Translation = new Vector3(0f, 18f, 0f);
                    slot.Scale = new Vector3(0.45f, 0.45f, 1f);
                }

                SidebarTransportControlsHost.IsHitTestVisible = false;
                SidebarTransportControlsHost.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static void RefreshTransportSlotGlass(FrameworkElement? slot)
    {
        if (slot is null)
        {
            return;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(slot);
        for (int i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(slot, i) is Controls.LiquidGlassPanel panel)
            {
                panel.InvalidateGlass();
            }
        }
    }

    private void OnSidebarVolumeHostPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInVolumeHost = true;
        _volumeCollapseDelayTimer?.Stop();
        StartVolumePillSpring(targetWidth: 124.0);
    }

    private void OnSidebarVolumeHostPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInVolumeHost = false;
        _volumeCollapseDelayTimer ??= DispatcherQueue.CreateTimer();
        _volumeCollapseDelayTimer.Stop();
        _volumeCollapseDelayTimer.Interval = TimeSpan.FromMilliseconds(130);
        _volumeCollapseDelayTimer.IsRepeating = false;
        _volumeCollapseDelayTimer.Tick -= OnVolumeCollapseDelayTick;
        _volumeCollapseDelayTimer.Tick += OnVolumeCollapseDelayTick;
        _volumeCollapseDelayTimer.Start();
    }

    private void OnVolumeCollapseDelayTick(DispatcherQueueTimer sender, object args)
    {
        if (_isPointerInVolumeHost)
        {
            sender.Stop();
            return;
        }

        if (SidebarVolumeSlider?.IsDragging == true)
        {
            sender.Interval = TimeSpan.FromMilliseconds(80);
            sender.Start();
            return;
        }

        sender.Stop();
        StartVolumePillSpring(targetWidth: 36.0);
    }

    private void StartVolumePillSpring(double targetWidth)
    {
        _volumePillTargetWidth = targetWidth;
        _volumeSpringTimer ??= DispatcherQueue.CreateTimer();
        _volumeSpringTimer.Interval = TimeSpan.FromMilliseconds(8);
        _volumeSpringTimer.Tick -= OnVolumeSpringTimerTick;
        _volumeSpringTimer.Tick += OnVolumeSpringTimerTick;
        if (!_volumeSpringTimer.IsRunning)
        {
            _volumeSpringTimer.Start();
        }
    }

    private void OnVolumeSpringTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (SidebarVolumeExpandablePill is null || SidebarVolumeSlider is null)
        {
            sender.Stop();
            return;
        }

        const double dt = 0.010;
        const double stiffness = 275.0;
        const double damping = 17.5;

        double displacement = _volumePillTargetWidth - _volumePillCurrentWidth;
        double accel = (displacement * stiffness) - (_volumePillVelocity * damping);
        _volumePillVelocity += accel * dt;
        _volumePillCurrentWidth += _volumePillVelocity * dt;

        double clampedWidth = Math.Max(36.0, _volumePillCurrentWidth);
        SidebarVolumeExpandablePill.Width = clampedWidth;

        // Subtle jelly vertical squash/stretch anchored on left center during motion
        var pillVisual = ElementCompositionPreview.GetElementVisual(SidebarVolumeExpandablePill);
        pillVisual.CenterPoint = new Vector3(18f, 18f, 0f);
        double stretch = Math.Clamp(_volumePillVelocity / 1150.0, -0.14, 0.14);
        pillVisual.Scale = new Vector3(1f + (float)(stretch * 0.25), 1f - (float)(stretch * 0.65), 1f);

        double openFraction = Math.Clamp((clampedWidth - 46.0) / 52.0, 0.0, 1.0);
        SidebarVolumeSlider.Opacity = openFraction;
        SidebarVolumeSlider.IsHitTestVisible = openFraction > 0.35;

        if (Math.Abs(displacement) < 0.25 && Math.Abs(_volumePillVelocity) < 1.8)
        {
            sender.Stop();
            _volumePillCurrentWidth = _volumePillTargetWidth;
            _volumePillVelocity = 0.0;
            SidebarVolumeExpandablePill.Width = _volumePillTargetWidth;
            pillVisual.Scale = Vector3.One;
            double finalOpen = _volumePillTargetWidth > 50.0 ? 1.0 : 0.0;
            SidebarVolumeSlider.Opacity = finalOpen;
            SidebarVolumeSlider.IsHitTestVisible = finalOpen > 0.5;
        }
    }

    private void SyncSidebarSquigglyProgress()
    {
        double total = ViewModel.TotalDurationSeconds;
        double progress = total > 0.05 ? Math.Clamp(ViewModel.CurrentPositionSeconds / total, 0.0, 1.0) : 0.0;

        if (SidebarSquigglyProgressBar is not null && !SidebarSquigglyProgressBar.IsScrubbing)
        {
            SidebarSquigglyProgressBar.Progress = progress;
        }

        if (FullscreenSquigglyProgressBar is not null && !FullscreenSquigglyProgressBar.IsScrubbing)
        {
            FullscreenSquigglyProgressBar.Progress = progress;
        }

        if (MiniPlayerBorderHost is not null && !MiniPlayerBorderHost.IsScrubbing)
        {
            MiniPlayerBorderHost.Progress = progress;
        }

        if (CompactMiniPlayerBorderHost is not null && !CompactMiniPlayerBorderHost.IsScrubbing)
        {
            CompactMiniPlayerBorderHost.Progress = progress;
        }
    }

    private void OnSidebarSquigglySeekRequested(object? sender, double progress)
    {
        double targetSeconds = Math.Clamp(progress, 0.0, 1.0) * ViewModel.TotalDurationSeconds;
        ViewModel.SeekToSeconds(targetSeconds);
        if (SidebarPositionTextBlock is not null)
        {
            SidebarPositionTextBlock.Text = ViewModel.FormattedPosition;
        }
    }

    private void OnSidebarSquigglyScrubbingStateChanged(object? sender, bool isScrubbing)
    {
        if (!isScrubbing && SidebarPositionTextBlock is not null)
        {
            SidebarPositionTextBlock.Text = ViewModel.FormattedPosition;
        }
    }

    private void OnSidebarSquigglyScrubbingProgressChanged(object? sender, double progress)
    {
        if (SidebarPositionTextBlock is not null && ViewModel.TotalDurationSeconds > 0)
        {
            double sec = Math.Clamp(progress, 0.0, 1.0) * ViewModel.TotalDurationSeconds;
            int totalSec = Math.Max(0, (int)Math.Round(sec));
            SidebarPositionTextBlock.Text = $"{totalSec / 60}:{totalSec % 60:D2}";
        }
    }

    private void OnFullscreenSquigglySeekRequested(object? sender, double progress)
    {
        double targetSeconds = Math.Clamp(progress, 0.0, 1.0) * ViewModel.TotalDurationSeconds;
        ViewModel.SeekToSeconds(targetSeconds);
        if (FullscreenPositionTextBlock is not null)
        {
            FullscreenPositionTextBlock.Text = ViewModel.FormattedPosition;
        }

        if (SidebarPositionTextBlock is not null)
        {
            SidebarPositionTextBlock.Text = ViewModel.FormattedPosition;
        }
    }

    private void OnFullscreenSquigglyScrubbingStateChanged(object? sender, bool isScrubbing)
    {
        if (!isScrubbing && FullscreenPositionTextBlock is not null)
        {
            FullscreenPositionTextBlock.Text = ViewModel.FormattedPosition;
        }
    }

    private void OnFullscreenSquigglyScrubbingProgressChanged(object? sender, double progress)
    {
        if (FullscreenPositionTextBlock is not null && ViewModel.TotalDurationSeconds > 0)
        {
            double sec = Math.Clamp(progress, 0.0, 1.0) * ViewModel.TotalDurationSeconds;
            int totalSec = Math.Max(0, (int)Math.Round(sec));
            FullscreenPositionTextBlock.Text = $"{totalSec / 60}:{totalSec % 60:D2}";
        }
    }

    private void OnSidebarCastClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target)
        {
            return;
        }

        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 4,
                MaxWidth = 220,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Cast to Device",
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        FontSize = 13
                    },
                    new TextBlock
                    {
                        Text = "Wireless casting will be available in an upcoming update.",
                        Opacity = 0.78,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12
                    }
                }
            }
        };
        flyout.ShowAt(target);
    }

    private void OnSidebarCurrentTrackRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ViewModel.CurrentTrack is null || ViewModel.CurrentTrack.Id == "empty_state_track")
        {
            return;
        }

        var pt = e.GetPosition(RootLayoutGrid);
        ShowTrackContextMenuAtPoint(ViewModel.CurrentTrack, pt);
        e.Handled = true;
    }

    private void UpdateSidebarEqualizerAnimationState()
    {
        bool hasSidebarEq = EqBar1 is not null && EqBar2 is not null && EqBar3 is not null && EqBar4 is not null;
        bool hasFullscreenEq = FullscreenEqBar1 is not null && FullscreenEqBar2 is not null && FullscreenEqBar3 is not null && FullscreenEqBar4 is not null;
        if (!hasSidebarEq && !hasFullscreenEq)
        {
            return;
        }

        bool sidebarActive = ViewModel.IsPlaying && ViewModel.IsNowPlayingSidebarVisible && !ViewModel.IsLyricsTabSelected;
        bool fullscreenActive = ViewModel.IsPlaying && ViewModel.IsFullscreenNowPlayingOpen && ViewModel.IsFullscreenQueueActive;
        bool shouldAnimate = sidebarActive || fullscreenActive;

        if (shouldAnimate)
        {
            _eqAnimTimer ??= DispatcherQueue.CreateTimer();
            _eqAnimTimer.Interval = TimeSpan.FromMilliseconds(33);
            _eqAnimTimer.Tick -= OnSidebarEqualizerTimerTick;
            _eqAnimTimer.Tick += OnSidebarEqualizerTimerTick;
            if (!_eqAnimTimer.IsRunning)
            {
                _eqAnimTimer.Start();
            }
        }
        else
        {
            _eqAnimTimer?.Stop();
            if (hasSidebarEq)
            {
                EqBar1!.Height = 4.5;
                EqBar2!.Height = 8.5;
                EqBar3!.Height = 5.5;
                EqBar4!.Height = 7.0;
            }

            if (hasFullscreenEq)
            {
                FullscreenEqBar1!.Height = 4.5;
                FullscreenEqBar2!.Height = 8.5;
                FullscreenEqBar3!.Height = 5.5;
                FullscreenEqBar4!.Height = 7.0;
            }
        }
    }

    private void OnSidebarEqualizerTimerTick(DispatcherQueueTimer sender, object args)
    {
        bool sidebarActive = ViewModel.IsPlaying && ViewModel.IsNowPlayingSidebarVisible && !ViewModel.IsLyricsTabSelected;
        bool fullscreenActive = ViewModel.IsPlaying && ViewModel.IsFullscreenNowPlayingOpen && ViewModel.IsFullscreenQueueActive;
        if (!sidebarActive && !fullscreenActive)
        {
            UpdateSidebarEqualizerAnimationState();
            return;
        }

        _eqPhase += 0.14;
        double h1 = 3.5 + 10.5 * (0.5 + 0.5 * Math.Sin(_eqPhase * 1.15));
        double h2 = 3.5 + 10.5 * (0.5 + 0.5 * Math.Sin(_eqPhase * 1.55 + 1.2));
        double h3 = 3.5 + 10.5 * (0.5 + 0.5 * Math.Sin(_eqPhase * 1.32 + 2.5));
        double h4 = 3.5 + 10.5 * (0.5 + 0.5 * Math.Sin(_eqPhase * 1.78 + 3.8));

        if (sidebarActive && EqBar1 is not null && EqBar2 is not null && EqBar3 is not null && EqBar4 is not null)
        {
            EqBar1.Height = h1;
            EqBar2.Height = h2;
            EqBar3.Height = h3;
            EqBar4.Height = h4;
        }

        if (fullscreenActive && FullscreenEqBar1 is not null && FullscreenEqBar2 is not null && FullscreenEqBar3 is not null && FullscreenEqBar4 is not null)
        {
            FullscreenEqBar1.Height = h1;
            FullscreenEqBar2.Height = h2;
            FullscreenEqBar3.Height = h3;
            FullscreenEqBar4.Height = h4;
        }
    }

    private void OnQueueDragHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        StartQueueDrag(sender, e, SidebarQueueItemsControl, NowPlayingQueueScrollViewer, NowPlayingTabContentHost);
    }

    private void OnFullscreenQueueDragHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        StartQueueDrag(sender, e, FullscreenQueueItemsControl, FullscreenQueueScrollViewer, FullscreenSideTabContentHost);
    }

    private void StartQueueDrag(
        object sender,
        PointerRoutedEventArgs e,
        ItemsControl? itemsControl,
        ScrollViewer? scrollViewer,
        FrameworkElement? coordHost)
    {
        if (sender is not FrameworkElement { Tag: TrackItem track } handle ||
            !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Flush any previous drag settle before capturing the next drag indices
        ++_queueDragGeneration;
        CommitPendingQueueMoveIfNeeded();

        int index = ViewModel.Queue.IndexOf(track);
        if (index < 0)
        {
            return;
        }

        var container = GetQueueItemContainer(itemsControl, index);
        if (container is null)
        {
            return;
        }

        handle.CapturePointer(e.Pointer);
        _activeDragQueueItemsControl = itemsControl;
        _activeDragQueueScrollViewer = scrollViewer;
        _activeDragQueueCoordinateHost = coordHost;
        _dragHandleElement = handle;
        _draggedQueueTrack = track;
        _dragStartQueueIndex = index;
        _dragCurrentQueueIndex = index;
        _dragActiveQueueContainer = container;
        _queueNeighborShiftDirections.Clear();
        _dragStartPointerY = e.GetCurrentPoint(coordHost).Position.Y;
        _dragLastScreenY = _dragStartPointerY;
        _dragStartScrollOffset = scrollViewer?.VerticalOffset ?? 0.0;
        _dragOffsetY = 0.0;
        _dragVelocityY = 0.0;
        _dragLastTimestampTicks = Stopwatch.GetTimestamp();
        _queueLastPointerViewportY = e.GetCurrentPoint(scrollViewer).Position.Y;

        container.Opacity = 1.0f;
        Canvas.SetZIndex(container, 50);
        ApplyQueueCardGrabImpulse(container);
        e.Handled = true;
    }

    private void OnQueueDragHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggedQueueTrack is null || _dragActiveQueueContainer is null)
        {
            return;
        }

        var coordHost = _activeDragQueueCoordinateHost ?? NowPlayingTabContentHost;
        var scrollViewer = _activeDragQueueScrollViewer ?? NowPlayingQueueScrollViewer;
        double currentY = e.GetCurrentPoint(coordHost).Position.Y;
        double currentScrollOffset = scrollViewer?.VerticalOffset ?? 0.0;
        _queueLastPointerViewportY = e.GetCurrentPoint(scrollViewer).Position.Y;

        long now = Stopwatch.GetTimestamp();
        double dt = Math.Clamp((now - _dragLastTimestampTicks) / (double)Stopwatch.Frequency, 0.004, 0.06);
        _dragLastTimestampTicks = now;

        double deltaMove = currentY - _dragLastScreenY;
        _dragLastScreenY = currentY;
        double instantVelocity = deltaMove / dt;
        _dragVelocityY = (_dragVelocityY * 0.55) + (instantVelocity * 0.45);
        _dragOffsetY = (currentY - _dragStartPointerY) + (currentScrollOffset - _dragStartScrollOffset);

        UpdateDraggedQueueContainerTransform();
        EvaluateQueueDragSwap();
        UpdateQueueAutoScrollState();
        e.Handled = true;
    }

    private void OnQueueDragHandlePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CompleteQueueDrag(sender as FrameworkElement, e.Pointer);
        e.Handled = true;
    }

    private void OnQueueDragHandlePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        CompleteQueueDrag(sender as FrameworkElement, e.Pointer);
    }

    private void OnQueueDragHandlePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        CompleteQueueDrag(sender as FrameworkElement, pointer: null);
    }

    private void CompleteQueueDrag(FrameworkElement? handle, Pointer? pointer)
    {
        _queueAutoScrollTimer?.Stop();
        if (handle is not null && pointer is not null)
        {
            handle.ReleasePointerCapture(pointer);
        }

        if (_draggedQueueTrack is null)
        {
            return;
        }

        var container = _dragActiveQueueContainer;
        int fromIndex = _dragStartQueueIndex;
        int toIndex = _dragCurrentQueueIndex;
        double spacing = ReferenceEquals(_activeDragQueueItemsControl, FullscreenQueueItemsControl) ? 8.0 : 7.0;
        double itemHeight = (container as FrameworkElement)?.ActualHeight ?? 56.0;
        double itemStep = Math.Max(48.0, itemHeight + spacing);
        float finalSlotOffsetY = (fromIndex >= 0 && toIndex >= 0)
            ? (float)((toIndex - fromIndex) * itemStep)
            : 0f;

        _draggedQueueTrack = null;
        _dragActiveQueueContainer = null;
        _dragHandleElement = null;
        _activeDragQueueItemsControl = null;
        _activeDragQueueScrollViewer = null;
        _activeDragQueueCoordinateHost = null;
        _dragStartQueueIndex = -1;
        _dragCurrentQueueIndex = -1;
        _dragOffsetY = 0.0;
        _dragVelocityY = 0.0;

        if (container is not null)
        {
            _ = AnimateQueueCardLandingAndCommitAsync(container, finalSlotOffsetY, fromIndex, toIndex);
        }
        else if (fromIndex >= 0 && toIndex >= 0 && fromIndex != toIndex)
        {
            _pendingQueueCommitMove = (fromIndex, toIndex);
            CommitPendingQueueMoveIfNeeded();
        }
        else
        {
            ResetAllQueueContainerVisuals();
        }
    }

    private async Task AnimateQueueCardLandingAndCommitAsync(UIElement container, float targetOffsetY, int fromIndex, int toIndex)
    {
        int gen = ++_queueDragGeneration;
        _pendingQueueCommitMove = (fromIndex >= 0 && toIndex >= 0 && fromIndex != toIndex)
            ? (fromIndex, toIndex)
            : null;

        AnimateQueueCardLandingSettle(container, targetOffsetY);
        await Task.Delay(195);

        if (gen == _queueDragGeneration)
        {
            CommitPendingQueueMoveIfNeeded();
        }
    }

    private void CommitPendingQueueMoveIfNeeded()
    {
        var pending = _pendingQueueCommitMove;
        _pendingQueueCommitMove = null;
        _queueNeighborShiftDirections.Clear();
        ResetAllQueueContainerVisuals();

        if (pending is { } move &&
            move.From >= 0 &&
            move.To >= 0 &&
            move.From != move.To &&
            move.From < ViewModel.Queue.Count &&
            move.To < ViewModel.Queue.Count)
        {
            ViewModel.MoveQueueItem(move.From, move.To);
            SidebarQueueItemsControl?.UpdateLayout();
            FullscreenQueueItemsControl?.UpdateLayout();
        }

        if (ViewModel.IsFullscreenNowPlayingOpen)
        {
            UpdateFullscreenQueueEdgeFade();
        }
    }

    private void ResetAllQueueContainerVisuals()
    {
        int count = ViewModel.Queue.Count;
        if (SidebarQueueItemsControl is not null)
        {
            for (int i = 0; i < count; i++)
            {
                if (GetQueueItemContainer(SidebarQueueItemsControl, i) is UIElement c)
                {
                    ResetContainerVisualImmediate(c);
                }
            }
        }

        if (FullscreenQueueItemsControl is not null)
        {
            for (int i = 0; i < count; i++)
            {
                if (GetQueueItemContainer(FullscreenQueueItemsControl, i) is UIElement c)
                {
                    ResetContainerVisualImmediate(c);
                }
            }
        }
    }

    private void UpdateDraggedQueueContainerTransform()
    {
        if (_dragActiveQueueContainer is null)
        {
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(_dragActiveQueueContainer, true);
        var visual = ElementCompositionPreview.GetElementVisual(_dragActiveQueueContainer);
        if (_dragActiveQueueContainer is FrameworkElement fe)
        {
            visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
        }

        float tilt = Math.Clamp((float)(_dragVelocityY * 0.0042), -2.4f, 2.4f);
        float velFrac = Math.Clamp(MathF.Abs((float)_dragVelocityY) / 1400f, 0f, 1f);
        float stretchY = 1.03f + velFrac * 0.045f;
        float squashX = 1.03f - velFrac * 0.032f;

        visual.Properties.InsertVector3("Translation", new Vector3(0f, (float)_dragOffsetY, 0f));
        visual.RotationAngleInDegrees = tilt;
        visual.Scale = new Vector3(squashX, stretchY, 1f);
    }

    private void EvaluateQueueDragSwap()
    {
        var itemsControl = _activeDragQueueItemsControl ?? SidebarQueueItemsControl;
        if (_dragActiveQueueContainer is null ||
            itemsControl is null ||
            _dragStartQueueIndex < 0 ||
            _dragCurrentQueueIndex < 0)
        {
            return;
        }

        double spacing = ReferenceEquals(itemsControl, FullscreenQueueItemsControl) ? 8.0 : 7.0;
        double itemHeight = (_dragActiveQueueContainer as FrameworkElement)?.ActualHeight ?? 56.0;
        double itemStep = Math.Max(48.0, itemHeight + spacing);
        int queueCount = ViewModel.Queue.Count;
        bool slotChanged = false;

        double offsetFromCurrentSlot = _dragOffsetY - ((_dragCurrentQueueIndex - _dragStartQueueIndex) * itemStep);
        while (offsetFromCurrentSlot > itemStep * 0.52 && _dragCurrentQueueIndex < queueCount - 1)
        {
            _dragCurrentQueueIndex++;
            offsetFromCurrentSlot = _dragOffsetY - ((_dragCurrentQueueIndex - _dragStartQueueIndex) * itemStep);
            slotChanged = true;
        }

        while (offsetFromCurrentSlot < -itemStep * 0.48 && _dragCurrentQueueIndex > 0)
        {
            _dragCurrentQueueIndex--;
            offsetFromCurrentSlot = _dragOffsetY - ((_dragCurrentQueueIndex - _dragStartQueueIndex) * itemStep);
            slotChanged = true;
        }

        if (!slotChanged)
        {
            return;
        }

        for (int i = 0; i < queueCount; i++)
        {
            if (i == _dragStartQueueIndex)
            {
                continue;
            }

            int shift = (i > _dragStartQueueIndex && i <= _dragCurrentQueueIndex) ? -1
                : (i >= _dragCurrentQueueIndex && i < _dragStartQueueIndex) ? 1
                : 0;

            int previousShift = _queueNeighborShiftDirections.GetValueOrDefault(i, 0);
            if (shift != previousShift)
            {
                _queueNeighborShiftDirections[i] = shift;
                if (GetQueueItemContainer(itemsControl, i) is UIElement neighbor)
                {
                    AnimateNeighborQueueDisplacement(neighbor, targetOffsetY: shift * (float)itemStep);
                }
            }
        }
    }

    private void UpdateQueueAutoScrollState()
    {
        var scrollViewer = _activeDragQueueScrollViewer ?? NowPlayingQueueScrollViewer;
        if (scrollViewer is null || _draggedQueueTrack is null)
        {
            _queueAutoScrollTimer?.Stop();
            return;
        }

        double h = scrollViewer.ViewportHeight;
        bool nearEdge = h > 80 && (_queueLastPointerViewportY < 42.0 || _queueLastPointerViewportY > h - 42.0);
        if (nearEdge)
        {
            _queueAutoScrollTimer ??= DispatcherQueue.CreateTimer();
            _queueAutoScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
            _queueAutoScrollTimer.Tick -= OnQueueAutoScrollTick;
            _queueAutoScrollTimer.Tick += OnQueueAutoScrollTick;
            if (!_queueAutoScrollTimer.IsRunning)
            {
                _queueAutoScrollTimer.Start();
            }
        }
        else
        {
            _queueAutoScrollTimer?.Stop();
        }
    }

    private void OnQueueAutoScrollTick(DispatcherQueueTimer sender, object args)
    {
        var scrollViewer = _activeDragQueueScrollViewer ?? NowPlayingQueueScrollViewer;
        if (_draggedQueueTrack is null || scrollViewer is null)
        {
            sender.Stop();
            return;
        }

        double h = scrollViewer.ViewportHeight;
        double delta = 0.0;
        if (_queueLastPointerViewportY < 42.0)
        {
            delta = -6.0 * (1.0 - Math.Clamp(_queueLastPointerViewportY / 42.0, 0.0, 1.0));
        }
        else if (_queueLastPointerViewportY > h - 42.0)
        {
            delta = 6.0 * (1.0 - Math.Clamp((h - _queueLastPointerViewportY) / 42.0, 0.0, 1.0));
        }

        if (Math.Abs(delta) < 0.2)
        {
            return;
        }

        double prevOffset = scrollViewer.VerticalOffset;
        double nextOffset = Math.Clamp(prevOffset + delta, 0.0, scrollViewer.ScrollableHeight);
        double actualDelta = nextOffset - prevOffset;
        if (Math.Abs(actualDelta) > 0.05)
        {
            _ = scrollViewer.ChangeView(null, nextOffset, null, disableAnimation: true);
            _dragOffsetY += actualDelta;
            UpdateDraggedQueueContainerTransform();
            EvaluateQueueDragSwap();
        }
    }

    private static UIElement? GetQueueItemContainer(ItemsControl? control, int index)
    {
        if (control is null || index < 0)
        {
            return null;
        }

        return control.ContainerFromIndex(index) as UIElement;
    }

    private void OnQueueCollectionChangedResetLayout()
    {
        if (_draggedQueueTrack is not null)
        {
            return;
        }

        _queueNeighborShiftDirections.Clear();
        ResetAllQueueContainerVisuals();
        if (ViewModel.IsFullscreenNowPlayingOpen)
        {
            UpdateFullscreenQueueEdgeFade();
        }
    }

    private static void ResetContainerVisualImmediate(UIElement container)
    {
        try
        {
            container.Opacity = 1.0;
            ElementCompositionPreview.SetIsTranslationEnabled(container, true);
            var v = ElementCompositionPreview.GetElementVisual(container);
            v.StopAnimation("Translation");
            v.StopAnimation("Scale");
            v.StopAnimation("RotationAngleInDegrees");
            v.Properties.InsertVector3("Translation", Vector3.Zero);
            v.Scale = Vector3.One;
            v.RotationAngleInDegrees = 0f;
            Canvas.SetZIndex(container, 0);
        }
        catch
        {
            // Never throw from ItemsRepeater ElementPrepared/ElementClearing WinRT callbacks
        }
    }

    private static void ApplyQueueCardGrabImpulse(UIElement container)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(container, true);
        var visual = ElementCompositionPreview.GetElementVisual(container);
        if (container is FrameworkElement fe)
        {
            visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
        }

        using var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.InitialValue = new Vector3(1.06f, 0.935f, 1f);
        spring.FinalValue = new Vector3(1.03f, 1.03f, 1f);
        spring.DampingRatio = 0.45f;
        spring.Period = TimeSpan.FromMilliseconds(52);
        visual.StartAnimation("Scale", spring);
    }

    private static void AnimateNeighborQueueDisplacement(UIElement neighbor, float targetOffsetY)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(neighbor, true);
        var visual = ElementCompositionPreview.GetElementVisual(neighbor);
        var compositor = visual.Compositor;
        if (neighbor is FrameworkElement fe)
        {
            visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
        }

        using var slideSpring = compositor.CreateSpringVector3Animation();
        slideSpring.FinalValue = new Vector3(0f, targetOffsetY, 0f);
        slideSpring.DampingRatio = 0.54f;
        slideSpring.Period = TimeSpan.FromMilliseconds(56);

        using var jellySpring = compositor.CreateSpringVector3Animation();
        jellySpring.InitialValue = new Vector3(1.055f, 0.935f, 1f);
        jellySpring.FinalValue = Vector3.One;
        jellySpring.DampingRatio = 0.46f;
        jellySpring.Period = TimeSpan.FromMilliseconds(52);

        visual.StartAnimation("Translation", slideSpring);
        visual.StartAnimation("Scale", jellySpring);
    }

    private static void AnimateQueueCardLandingSettle(UIElement container, float targetOffsetY)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(container, true);
        var visual = ElementCompositionPreview.GetElementVisual(container);
        var compositor = visual.Compositor;
        if (container is FrameworkElement fe)
        {
            visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
        }

        using var transSpring = compositor.CreateSpringVector3Animation();
        transSpring.FinalValue = new Vector3(0f, targetOffsetY, 0f);
        transSpring.DampingRatio = 0.52f;
        transSpring.Period = TimeSpan.FromMilliseconds(56);

        using var scaleSpring = compositor.CreateSpringVector3Animation();
        scaleSpring.InitialValue = new Vector3(1.065f, 0.925f, 1f);
        scaleSpring.FinalValue = Vector3.One;
        scaleSpring.DampingRatio = 0.46f;
        scaleSpring.Period = TimeSpan.FromMilliseconds(50);

        using var rotSpring = compositor.CreateSpringScalarAnimation();
        rotSpring.FinalValue = 0f;
        rotSpring.DampingRatio = 0.58f;
        rotSpring.Period = TimeSpan.FromMilliseconds(54);

        visual.StartAnimation("Translation", transSpring);
        visual.StartAnimation("Scale", scaleSpring);
        visual.StartAnimation("RotationAngleInDegrees", rotSpring);
    }

    private void OnSidebarSynchronizedLyricLineClicked(object? sender, SynchronizedLyricLineViewModel line)
    {
        ViewModel.SeekToLyric(line);
    }

    private void OnSidebarLyricsUserScrollingChanged(object? sender, bool isScrolling)
    {
        if (SidebarLyricsSyncPill is not null)
        {
            SidebarLyricsSyncPill.Visibility = isScrolling ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnSidebarLyricsSyncClicked(object sender, RoutedEventArgs e)
    {
        SidebarSynchronizedLyricsControl?.RecenterOnActiveLine();
    }

    #endregion

    #endregion
}
