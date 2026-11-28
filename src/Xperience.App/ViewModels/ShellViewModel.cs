using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Xperience.App.Helpers;
using Xperience.App.Services;
using Xperience.Core.Abstractions;
using Xperience.Core.Collections;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;
using Xperience.Core.Playback;
using Xperience.Core.Services;

namespace Xperience.App.ViewModels;

/// <summary>
/// Primary MVVM ViewModel coordinating catalog state, audio playback, real-time localization (LTR/RTL),
/// dynamic theming, and Win2D Liquid Glass shader parameters.
/// Uses CommunityToolkit.Mvvm source generators (<see cref="ObservablePropertyAttribute"/>, <see cref="RelayCommandAttribute"/>).
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IMediaCatalogService _catalogService;
    private readonly IAudioPlayerService _audioPlayerService;
    private readonly IYouTubeMusicService _youTubeMusicService;
    private readonly ILyricsProviderService _lyricsProviderService;
    private readonly ICanvasArtworkService _canvasArtworkService;
    private readonly IMusicDownloadService _musicDownloadService;
    private readonly IThemeService _themeService;
    private readonly ILocalizationService _localizationService;
    private readonly INavigationService _navigationService;
    private readonly IStorageService _storageService;
    private readonly IDataUsageService _dataUsageService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly WindowsInstallerLauncher _installerLauncher;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly DispatcherQueue _dispatcherQueue;

    private const int MaxVisibleQueueItems = 60;
    private const int MaxCompletedDownloadQueueItems = 50;

    private static readonly Lazy<Microsoft.UI.Xaml.Media.SolidColorBrush> FallbackMoodStripeBrush =
        new(() => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 99, 102, 241)));

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _moodDetailCts;
    private CancellationTokenSource? _songsBatchCts;
    private CancellationTokenSource? _canvasLookupCts;
    private CancellationTokenSource? _trackChangeSecondaryCts;
    private CancellationTokenSource? _albumCanvasLookupCts;
    private CancellationTokenSource? _artistVideoLookupCts;
    private CancellationTokenSource? _auroraCts;
    private CancellationTokenSource? _feedResolveCts;
    private CancellationTokenSource? _likedRecsCts;
    private CancellationTokenSource? _playlistRelatedLoadCts;
    private CancellationTokenSource? _homeGenreLoadCts;
    private string? _lastLikedRecsSeedKey;
    private readonly BoundedLruCache<string, (string Fingerprint, List<TrackItem> Tracks)> _playlistRelatedCache =
        new(maxCapacity: 20, StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<TrackItem> _homeRecommendedSnapshot = [];
    private volatile IReadOnlyList<TrackItem> _homeGenreSnapshot = [];
    private volatile IReadOnlyList<TrackItem> _likedSongsSnapshot = [];
    private volatile IReadOnlyList<TrackItem> _librarySongsSnapshot = [];
    private volatile IReadOnlyList<TrackItem> _homeRecentlyPlayedSnapshot = [];
    private readonly List<NewReleaseItem> _allNewReleases = [];
    private readonly List<RadioStationItem> _allRadioStations = [];
    private readonly TaskCompletionSource _initialHomeReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isLibraryHydrated;
    private bool _isLikedHydrated;
    private bool _isPlaylistsHydrated;
    private bool _isVisualizersInitialized;
    private bool _isLibraryDirty = true;
    private bool _isLikedDirty = true;
    private bool _isLikedRecsDirty = true;
    private bool _isPlaylistsDirty = true;
    private bool _hasUserInitiatedPlayback;
    private string? _deferredPlaybackStartTrackId;
    private int _lastFormattedPositionSecond = -1;
    private int _lastFormattedDurationSecond = -1;
    private long _positionSyncGeneration;
    private bool _isSuppressingGlassCallback;
    private bool _isSuppressingDownloadOptionsSave;
    private bool _isSuppressingQueueSync;
    private bool _isHydratingPlayerUiSettings;
    private PlayerUiSettings _persistedPlayerUiSettings = new();
    private long _lastPlaybackSessionPersistTickMs;
    private bool _isSuppressingSearchQueryChange;
    private bool _hasLoadedMoodsAndGenres;
    private bool _hasLoadedRecentSearches;
    private bool _returnToSearchAfterDetail;
    private int _trackLoadGeneration;

    private static void CancelAndDisposeCts(ref CancellationTokenSource? cts)
    {
        var previous = Interlocked.Exchange(ref cts, null);
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
            finally
            {
                previous.Dispose();
            }
        }
    }

    private static CancellationTokenSource ReplaceCts(
        ref CancellationTokenSource? cts,
        CancellationTokenSource? replacement = null)
    {
        var next = replacement ?? new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref cts, next);
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
            finally
            {
                previous.Dispose();
            }
        }

        return next;
    }

    /// <summary>
    /// Gets a task that completes when initial catalog loading and Home discovery feed population finish.
    /// </summary>
    public Task InitialHomeReadyTask => _initialHomeReadyTcs.Task;

    /// <summary>
    /// Gets the filtered song tracks displayed in the Songs section.
    /// </summary>
    public ObservableCollection<TrackItem> Songs { get; } = [];

    /// <summary>
    /// Gets the filtered albums displayed in the Albums section.
    /// </summary>
    public ObservableCollection<AlbumItem> Albums { get; } = [];

    /// <summary>
    /// Gets the filtered artists displayed in the Artists section.
    /// </summary>
    public ObservableCollection<ArtistItem> Artists { get; } = [];

    /// <summary>
    /// Gets the recommended artists displayed in the "Artists You May Like" section.
    /// </summary>
    public ObservableCollection<ArtistItem> RecommendedArtists { get; } = [];

    /// <summary>
    /// Gets all tracks in the active Library source sorted by recently added descending.
    /// </summary>
    public ObservableCollection<TrackItem> LibraryRecentlyAdded { get; } = [];

    /// <summary>
    /// Gets the top 10 recently added tracks for the Library "All" overview carousel.
    /// </summary>
    public ObservableCollection<TrackItem> LibraryRecentlyAddedPreview { get; } = [];

    /// <summary>
    /// Gets the top 10 artists for the Library "All" overview horizontal carousel.
    /// </summary>
    public ObservableCollection<ArtistItem> LibraryArtistsPreview { get; } = [];

    /// <summary>
    /// Gets the top 10 albums for the Library "All" overview horizontal carousel.
    /// </summary>
    public ObservableCollection<AlbumItem> LibraryAlbumsPreview { get; } = [];

    /// <summary>
    /// Gets the first 15 songs for the Library "All" overview multi-column grid.
    /// </summary>
    public ObservableCollection<TrackItem> LibrarySongsPreview { get; } = [];

    /// <summary>
    /// Gets the A–Z + '#' grouped artists displayed in the Library Artists directory tab.
    /// </summary>
    public ObservableCollection<LibraryArtistGroup> LibraryArtistGroups { get; } = [];

    /// <summary>
    /// Gets the A–Z + '#' grouped albums displayed in the Library Albums directory tab.
    /// </summary>
    public ObservableCollection<LibraryAlbumGroup> LibraryAlbumGroups { get; } = [];

    /// <summary>
    /// Gets the A–Z + '#' grouped songs displayed in the Library Songs directory tab.
    /// </summary>
    public ObservableCollection<LibraryTrackGroup> LibrarySongGroups { get; } = [];

    /// <summary>
    /// Gets the chronological time-bucket groups displayed in the expanded Library Recently Added view.
    /// </summary>
    public ObservableCollection<LibraryTrackGroup> LibraryRecentlyAddedGroups { get; } = [];

    /// <summary>
    /// Gets the flattened A–Z ordered songs used as playback queue when playing from the Songs directory.
    /// </summary>
    public ObservableCollection<TrackItem> LibraryOrderedSongs { get; } = [];

    /// <summary>
    /// Raised whenever Library groups or active directory tab change so the view can sync the A–Z index rail.
    /// </summary>
    public event EventHandler? LibraryIndexChanged;

    private string? _lastLoadedLibraryQueryKey;
    private MediaSourceType? _lastLoadedLibrarySource;

    /// <summary>
    /// Gets the recently played tracks displayed on the Home discovery screen.
    /// </summary>
    public ObservableCollection<TrackItem> HomeRecentlyPlayed { get; } = [];

    /// <summary>
    /// Gets the album-deduplicated recently played items displayed in the Home irregular mosaic carousel.
    /// </summary>
    public ObservableCollection<RecentAlbumItem> HomeRecentlyPlayedAlbums { get; } = [];

    /// <summary>
    /// Gets the personalized recommended tracks displayed on the Home discovery screen.
    /// </summary>
    public ObservableCollection<TrackItem> HomeRecommendedTracks { get; } = [];

    /// <summary>
    /// Gets the recommended genre tags displayed on the Home discovery screen.
    /// </summary>
    public ObservableCollection<string> HomeGenreTags { get; } = [];

    /// <summary>
    /// Gets the recommended tracks for the active genre on the Home discovery screen.
    /// </summary>
    public ObservableCollection<TrackItem> HomeGenreTracks { get; } = [];

    /// <summary>
    /// Gets the related artists (with official YouTube Music avatars) displayed on the Home discovery screen.
    /// </summary>
    public ObservableCollection<ArtistItem> HomeRelatedArtists { get; } = [];

    /// <summary>
    /// Gets the recommended albums displayed on the Home discovery screen.
    /// </summary>
    public ObservableCollection<AlbumItem> HomeRecommendedAlbums { get; } = [];

    /// <summary>
    /// Gets the editorial "Top picks for you" cards displayed on the Home screen.
    /// </summary>
    public ObservableCollection<TopPickItem> HomeTopPicks { get; } = [];

    /// <summary>
    /// Gets the curated "Playlists made for you" cards displayed on the Home screen.
    /// </summary>
    public ObservableCollection<RecommendedPlaylistItem> HomePlaylistsMadeForYou { get; } = [];

    /// <summary>
    /// Gets the recommended "Radio Stations" displayed on the Home screen.
    /// </summary>
    public ObservableCollection<RadioStationItem> HomeRadioStations { get; } = [];

    /// <summary>
    /// Gets the "New Releases" cards displayed on the Home screen.
    /// </summary>
    public ObservableCollection<NewReleaseItem> HomeNewReleases { get; } = [];

    /// <summary>
    /// Gets the similar albums for the first dynamic "More like: [Album]" section.
    /// </summary>
    public ObservableCollection<AlbumItem> HomeMoreLike1 { get; } = [];

    /// <summary>
    /// Gets the similar albums for the second dynamic "More like: [Album]" section.
    /// </summary>
    public ObservableCollection<AlbumItem> HomeMoreLike2 { get; } = [];

    /// <summary>
    /// Gets the similar albums for the third dynamic "More like: [Album]" section.
    /// </summary>
    public ObservableCollection<AlbumItem> HomeMoreLike3 { get; } = [];

    /// <summary>
    /// Gets the filtered items displayed inside the New Releases category grid sub-page.
    /// </summary>
    public ObservableCollection<NewReleaseItem> FilteredNewReleasesGridItems { get; } = [];

    /// <summary>
    /// Gets the items displayed inside the Radio Stations category grid sub-page.
    /// </summary>
    public ObservableCollection<RadioStationItem> RadioStationsGridItems { get; } = [];

    /// <summary>
    /// Gets the top 10 most listened tracks displayed on the Artist Detail screen.
    /// </summary>
    public ObservableCollection<TrackItem> ArtistTopTracks { get; } = [];

    /// <summary>
    /// Gets the combined YouTube Music and local albums displayed on the Artist Detail screen.
    /// </summary>
    public ObservableCollection<AlbumItem> ArtistCombinedAlbums { get; } = [];

    /// <summary>
    /// Gets the Singles and EPs by the artist from YouTube Music displayed on the Artist Detail screen.
    /// </summary>
    public ObservableCollection<AlbumItem> ArtistSinglesAndEps { get; } = [];

    /// <summary>
    /// Gets the artist playlists and Featured On playlists from YouTube Music displayed on the Artist Detail screen.
    /// </summary>
    public ObservableCollection<AlbumItem> ArtistPlaylists { get; } = [];

    /// <summary>
    /// Gets the similar artists ("Puede que también te guste") from YouTube Music displayed on the Artist Detail screen.
    /// </summary>
    public ObservableCollection<ArtistItem> ArtistSimilarArtists { get; } = [];

    /// <summary>
    /// Gets the ordered album tracks displayed on the Album Detail screen.
    /// </summary>
    public ObservableCollection<TrackItem> AlbumDetailTracks { get; } = [];

    /// <summary>
    /// Gets the alternate versions of the current album ("Other Versions") from YouTube Music.
    /// </summary>
    public ObservableCollection<AlbumItem> AlbumOtherVersions { get; } = [];

    /// <summary>
    /// Gets the other albums and EPs by the same artist ("More by [Artist]") at the bottom of the Album Detail screen.
    /// </summary>
    public ObservableCollection<AlbumItem> AlbumOtherAlbums { get; } = [];

    /// <summary>
    /// Gets all liked songs (local + online YouTube Music) displayed on the Liked Songs screen.
    /// </summary>
    public ObservableCollection<TrackItem> LikedSongs { get; } = [];

    /// <summary>
    /// Gets the A–Z grouped liked songs when A–Z sort is selected on the Liked screen.
    /// </summary>
    public ObservableCollection<LibraryTrackGroup> LikedSongGroups { get; } = [];

    /// <summary>
    /// Gets the flattened A–Z ordered liked songs used for Play All in alphabetical sort mode.
    /// </summary>
    public ObservableCollection<TrackItem> LikedOrderedSongs { get; } = [];

    /// <summary>
    /// Gets distinct artists derived from liked songs (recent/popularity order).
    /// </summary>
    public ObservableCollection<ArtistItem> LikedDerivedArtists { get; } = [];

    /// <summary>
    /// Gets A–Z grouped artists derived from liked songs.
    /// </summary>
    public ObservableCollection<LibraryArtistGroup> LikedArtistGroups { get; } = [];

    /// <summary>
    /// Gets distinct albums derived from liked songs (recent order).
    /// </summary>
    public ObservableCollection<AlbumItem> LikedDerivedAlbums { get; } = [];

    /// <summary>
    /// Gets A–Z grouped albums derived from liked songs.
    /// </summary>
    public ObservableCollection<LibraryAlbumGroup> LikedAlbumGroups { get; } = [];

    /// <summary>
    /// Gets personalized YouTube Music recommendations ("Fans Also Like &amp; Recommended") displayed on the Liked Songs screen.
    /// </summary>
    public ObservableCollection<TrackItem> LikedRecommendations { get; } = [];

    /// <summary>
    /// Gets all user-created playlists displayed in the Add-to-Playlist modal and catalog cache.
    /// </summary>
    public ObservableCollection<UserPlaylist> Playlists { get; } = [];

    /// <summary>
    /// Gets presentation cards for the Playlists grid with 4-state cover resolution, track badges, and durations.
    /// </summary>
    public ObservableCollection<PlaylistCardViewModel> PlaylistCards { get; } = [];

    /// <summary>
    /// Gets the tracks inside the currently opened Playlist Detail view.
    /// </summary>
    public ObservableCollection<TrackItem> PlaylistDetailTracks { get; } = [];

    /// <summary>
    /// Gets indexed track rows for the Playlist Detail flat tracklist.
    /// </summary>
    public ObservableCollection<PlaylistTrackRowItem> PlaylistDetailTrackRows { get; } = [];

    /// <summary>
    /// Gets recommended similar tracks ("Related to this playlist") displayed at the bottom of the Playlist Detail screen.
    /// </summary>
    public ObservableCollection<TrackItem> PlaylistRelatedTracks { get; } = [];

    /// <summary>
    /// Gets persisted recent search queries displayed as removable chips on the dedicated Search page.
    /// </summary>
    public ObservableCollection<string> RecentSearches { get; } = [];

    /// <summary>
    /// Gets the YouTube Music Moods &amp; Genres sections ("For you", "Moods &amp; moments", "Genres") for the Search explorer.
    /// </summary>
    public ObservableCollection<MoodGenreSectionViewModel> MoodGenreSections { get; } = [];

    /// <summary>
    /// Gets curated playlists for the currently selected Mood or Genre category.
    /// </summary>
    public ObservableCollection<RecommendedPlaylistItem> MoodDetailPlaylists { get; } = [];

    /// <summary>
    /// Gets featured albums for the currently selected Mood or Genre category.
    /// </summary>
    public ObservableCollection<AlbumItem> MoodDetailAlbums { get; } = [];

    /// <summary>
    /// Gets curated songs for the currently selected Mood or Genre category.
    /// </summary>
    public ObservableCollection<TrackItem> MoodDetailSongs { get; } = [];

    /// <summary>
    /// Gets categorized song results for the dedicated Search page.
    /// </summary>
    public ObservableCollection<TrackItem> SearchResultSongs { get; } = [];

    /// <summary>
    /// Gets categorized artist results for the dedicated Search page.
    /// </summary>
    public ObservableCollection<ArtistItem> SearchResultArtists { get; } = [];

    /// <summary>
    /// Gets categorized album results for the dedicated Search page.
    /// </summary>
    public ObservableCollection<AlbumItem> SearchResultAlbums { get; } = [];

    /// <summary>
    /// Gets categorized playlist results for the dedicated Search page.
    /// </summary>
    public ObservableCollection<RecommendedPlaylistItem> SearchResultPlaylists { get; } = [];

    /// <summary>
    /// Gets the list of local music directories configured in Settings.
    /// </summary>
    public ObservableCollection<string> LocalMusicDirectories { get; } = [];

    /// <summary>
    /// Gets the upcoming playback queue displayed in the Now Playing sidebar.
    /// </summary>
    public ObservableCollection<TrackItem> Queue { get; } = [];

    /// <summary>
    /// Raised whenever the active synchronized lyric line index changes during playback.
    /// </summary>
    public event EventHandler<int>? ActiveLyricIndexChanged;

    /// <summary>
    /// Raised when the user switches between Queue and Lyrics in the Now Playing sidebar.
    /// </summary>
    public event EventHandler<NowPlayingTab>? NowPlayingTabTransitionRequested;

    /// <summary>
    /// Raised when a track is newly added to the playback queue so the View can animate its emergence.
    /// </summary>
    public event EventHandler<TrackItem>? QueueItemAddedNotification;

    /// <summary>
    /// Raised when Fullscreen right side-panel visibility changes so the left playback deck can smoothly glide to/from center.
    /// </summary>
    public event EventHandler<bool>? FullscreenLyricsLayoutChanged;

    /// <summary>
    /// Raised when the active Fullscreen right side-panel tab (Lyrics vs Queue) changes.
    /// </summary>
    public event EventHandler<NowPlayingTab>? FullscreenSideTabChanged;

    /// <summary>
    /// Raised when the active category in Full-Page Settings changes so the content pane can animate.
    /// </summary>
    public event EventHandler<SettingsCategory>? SettingsCategoryTransitionRequested;

    /// <summary>
    /// Raised when the user clicks "Sign in with Browser (WebView2)" in YouTube Music Account settings.
    /// </summary>
    public event EventHandler? InteractiveYouTubeLoginRequested;

    /// <summary>
    /// Raised when a new 4-color Aurora Borealis palette is extracted from active cover artwork.
    /// </summary>
    public event EventHandler<AuroraPalette>? AuroraPaletteChanged;

    /// <summary>
    /// Raised when the user opens or closes Fullscreen Now Playing mode so the View can execute
    /// its expand-from-Now-Playing or contract-to-Now-Playing animation.
    /// </summary>
    public event EventHandler<bool>? FullscreenTransitionRequested;

    /// <summary>
    /// Raised when the search box expand state changes so the View can animate the Search capsule width with a fluid bounce.
    /// </summary>
    public event EventHandler<bool>? SearchExpandChanged;

    /// <summary>
    /// Raised when an Apple Music Canvas (or fallback motion artwork) video URI is resolved or cleared for the active track.
    /// </summary>
    public event EventHandler<string?>? CanvasVideoChanged;

    /// <summary>
    /// Raised when an Apple Music Canvas video URI is resolved or cleared for the currently opened Album Detail screen.
    /// </summary>
    public event EventHandler<string?>? AlbumDetailCanvasVideoChanged;

    /// <summary>
    /// Raised when an Apple Music 16:9 artist motion video URI is resolved or cleared for the currently opened Artist Detail header.
    /// </summary>
    public event EventHandler<string?>? ArtistDetailHeaderVideoChanged;

    /// <summary>
    /// Raised when the Artist Detail screen's photo/banner or seed color updates so the View can extract and apply its main background palette.
    /// </summary>
    public event EventHandler<(string? ImageUri, string? SeedHex)>? ArtistAmbientSourceChanged;

    /// <summary>
    /// Raised when the Album Detail screen's cover art updates so the View can extract and apply its ambient background palette.
    /// </summary>
    public event EventHandler<string?>? AlbumAmbientSourceChanged;

    /// <summary>
    /// Raised when the Album Detail description expand/collapse state changes so the View can smoothly animate it.
    /// </summary>
    public event EventHandler<bool>? AlbumDescriptionExpandChanged;

    /// <summary>
    /// Raised when the Playlist Detail screen's primary cover art updates so the View can extract and apply its ambient background palette.
    /// </summary>
    public event EventHandler<string?>? PlaylistAmbientSourceChanged;

    /// <summary>
    /// Raised when the Playlist Detail description expand/collapse state changes so the View can smoothly animate it.
    /// </summary>
    public event EventHandler<bool>? PlaylistDescriptionExpandChanged;

    /// <summary>
    /// Raised whenever Liked alphabetical groups, filter tab, or sort order change so the View can sync the Liked A–Z/# index rail.
    /// </summary>
    public event EventHandler? LikedIndexChanged;

    private CancellationTokenSource? _artistDetailLoadCts;
    private CancellationTokenSource? _albumDetailLoadCts;

    /// <summary>
    /// Raised when the user toggles the right-hand Now Playing sidebar visibility so the View can smoothly slide it right/left.
    /// </summary>
    public event EventHandler<bool>? NowPlayingSidebarVisibilityChanged;

    /// <summary>
    /// Gets the synchronized lyrics for the currently active track.
    /// </summary>
    public ObservableCollection<SynchronizedLyricLineViewModel> CurrentLyrics { get; } = [];

    /// <summary>
    /// Gets the available visual theme definitions.
    /// </summary>
    public ObservableCollection<ThemePaletteDefinition> AvailableThemes { get; } = [];

    /// <summary>
    /// Gets the available built-in and user-imported GPU music visualizers.
    /// </summary>
    public ObservableCollection<VisualizerDefinition> AvailableVisualizers { get; } = [];

    /// <summary>
    /// Gets the priority-ordered online synchronized lyrics providers configurable in Settings Studio.
    /// </summary>
    public ObservableCollection<LyricsProviderConfig> LyricsProviders { get; } = [];

    /// <summary>
    /// Gets the supported application languages.
    /// </summary>
    public IReadOnlyList<LanguageOption> SupportedLanguages => _localizationService.SupportedLanguages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSection))]
    [NotifyPropertyChangedFor(nameof(IsLibrarySection))]
    [NotifyPropertyChangedFor(nameof(IsLikedSection))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistsSection))]
    [NotifyPropertyChangedFor(nameof(IsCommunitySection))]
    [NotifyPropertyChangedFor(nameof(IsHomeViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsLibraryViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsLikedViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistsViewOpen))]
    [NotifyPropertyChangedFor(nameof(HomeViewVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryViewVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedViewVisibility))]
    [NotifyPropertyChangedFor(nameof(PlaylistsViewVisibility))]
    [NotifyPropertyChangedFor(nameof(HomeAuroraVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryAlphabetRailVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedAlphabetRailVisibility))]
    public partial NavigationSection CurrentSection { get; set; } = NavigationSection.Home;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSection))]
    [NotifyPropertyChangedFor(nameof(IsLibrarySection))]
    [NotifyPropertyChangedFor(nameof(IsLikedSection))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistsSection))]
    [NotifyPropertyChangedFor(nameof(IsCommunitySection))]
    [NotifyPropertyChangedFor(nameof(IsHomeViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsLibraryViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsLikedViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistsViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsArtistDetailViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsAlbumDetailViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistDetailViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsSettingsViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsHomeCategoryGridViewOpen))]
    [NotifyPropertyChangedFor(nameof(HomeViewVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryViewVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedViewVisibility))]
    [NotifyPropertyChangedFor(nameof(PlaylistsViewVisibility))]
    [NotifyPropertyChangedFor(nameof(ArtistDetailViewVisibility))]
    [NotifyPropertyChangedFor(nameof(AlbumDetailViewVisibility))]
    [NotifyPropertyChangedFor(nameof(PlaylistDetailViewVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsViewVisibility))]
    [NotifyPropertyChangedFor(nameof(HomeCategoryGridViewVisibility))]
    [NotifyPropertyChangedFor(nameof(HomeAuroraVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryAlphabetRailVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedAlphabetRailVisibility))]
    [NotifyPropertyChangedFor(nameof(DetailBackButtonVisibility))]
    public partial WorkspaceSubView ActiveSubView { get; set; } = WorkspaceSubView.MainSection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLikedListMode))]
    [NotifyPropertyChangedFor(nameof(IsLikedGridMode))]
    [NotifyPropertyChangedFor(nameof(LikedListViewVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedGridViewVisibility))]
    public partial LikedSongsViewMode SelectedLikedViewMode { get; set; } = LikedSongsViewMode.List;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLikedFilterSongs))]
    [NotifyPropertyChangedFor(nameof(IsLikedFilterArtists))]
    [NotifyPropertyChangedFor(nameof(IsLikedFilterAlbums))]
    [NotifyPropertyChangedFor(nameof(LikedSongsRecentVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedSongsAlphaVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedArtistsRecentVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedArtistsAlphaVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedAlbumsRecentVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedAlbumsAlphaVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedAlphabetRailVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedPlayActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedRightRecommendationsPaneVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedSongsEmptyVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedArtistsEmptyVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedAlbumsEmptyVisibility))]
    public partial LikedFilterTab SelectedLikedFilter { get; set; } = LikedFilterTab.Songs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLikedSortAlphabetical))]
    [NotifyPropertyChangedFor(nameof(IsLikedSortRecent))]
    [NotifyPropertyChangedFor(nameof(LikedSongsRecentVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedSongsAlphaVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedArtistsRecentVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedArtistsAlphaVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedAlbumsRecentVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedAlbumsAlphaVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedAlphabetRailVisibility))]
    public partial LikedSortMode SelectedLikedSort { get; set; } = LikedSortMode.Alphabetical;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LikedRecommendationsLoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedRecommendationsEmptyVisibility))]
    public partial bool IsLoadingLikedRecommendations { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSection))]
    [NotifyPropertyChangedFor(nameof(IsLibrarySection))]
    [NotifyPropertyChangedFor(nameof(IsLikedSection))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistsSection))]
    [NotifyPropertyChangedFor(nameof(IsCommunitySection))]
    [NotifyPropertyChangedFor(nameof(IsSearchViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsHomeViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsLibraryViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsLikedViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistsViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsArtistDetailViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsAlbumDetailViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsPlaylistDetailViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsSettingsViewOpen))]
    [NotifyPropertyChangedFor(nameof(IsHomeCategoryGridViewOpen))]
    [NotifyPropertyChangedFor(nameof(SearchViewVisibility))]
    [NotifyPropertyChangedFor(nameof(HomeViewVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryViewVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedViewVisibility))]
    [NotifyPropertyChangedFor(nameof(PlaylistsViewVisibility))]
    [NotifyPropertyChangedFor(nameof(ArtistDetailViewVisibility))]
    [NotifyPropertyChangedFor(nameof(AlbumDetailViewVisibility))]
    [NotifyPropertyChangedFor(nameof(PlaylistDetailViewVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsViewVisibility))]
    [NotifyPropertyChangedFor(nameof(HomeCategoryGridViewVisibility))]
    [NotifyPropertyChangedFor(nameof(HomeAuroraVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryAlphabetRailVisibility))]
    [NotifyPropertyChangedFor(nameof(LikedAlphabetRailVisibility))]
    public partial bool IsSearchPageOpen { get; set; }

    partial void OnCurrentSectionChanged(NavigationSection value)
    {
        if (value == NavigationSection.Liked)
        {
            LikedIndexChanged?.Invoke(this, EventArgs.Empty);
        }

        RefreshResponsiveLayoutState();
    }

    partial void OnActiveSubViewChanged(WorkspaceSubView value)
    {
        if (value == WorkspaceSubView.MainSection && CurrentSection == NavigationSection.Liked)
        {
            LikedIndexChanged?.Invoke(this, EventArgs.Empty);
        }

        RefreshResponsiveLayoutState();
    }

    partial void OnIsSearchPageOpenChanged(bool value)
    {
        if (!value && CurrentSection == NavigationSection.Liked && ActiveSubView == WorkspaceSubView.MainSection)
        {
            LikedIndexChanged?.Invoke(this, EventArgs.Empty);
        }

        RefreshResponsiveLayoutState();
    }

    partial void OnSelectedLikedFilterChanged(LikedFilterTab value)
    {
        LikedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedLikedSortChanged(LikedSortMode value)
    {
        LikedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSourceGlyph))]
    [NotifyPropertyChangedFor(nameof(SearchSourceTooltip))]
    [NotifyPropertyChangedFor(nameof(SearchSourceBadgeText))]
    public partial bool IsSearchOnlineMode { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoodsLoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(MoodsContentVisibility))]
    public partial bool IsLoadingMoodsAndGenres { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoodDetailEmptyPromptVisibility))]
    [NotifyPropertyChangedFor(nameof(MoodDetailLoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(MoodDetailContentVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchPageSubtitle))]
    public partial bool IsLoadingMoodDetail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoodDetailEmptyPromptVisibility))]
    [NotifyPropertyChangedFor(nameof(MoodDetailLoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(MoodDetailContentVisibility))]
    [NotifyPropertyChangedFor(nameof(SelectedMoodTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedMoodStripeBrush))]
    [NotifyPropertyChangedFor(nameof(SearchPageSubtitle))]
    public partial MoodGenreTileViewModel? SelectedMoodGenreTile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchLoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchResultsContentVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchNoResultsVisibility))]
    public partial bool IsSearchingCatalog { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchTopResultVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchNoResultsVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchTopResultTitle))]
    [NotifyPropertyChangedFor(nameof(SearchTopResultSubtitle))]
    [NotifyPropertyChangedFor(nameof(SearchTopResultKindBadgeText))]
    [NotifyPropertyChangedFor(nameof(SearchTopResultSafeArtworkUri))]
    [NotifyPropertyChangedFor(nameof(SearchTopResultArtworkOpacity))]
    [NotifyPropertyChangedFor(nameof(SearchTopResultDefaultIconOpacity))]
    [NotifyPropertyChangedFor(nameof(SearchTopResultCornerRadius))]
    public partial SearchTopResultItem? SearchResultTopResult { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsSearchFilterSongs))]
    [NotifyPropertyChangedFor(nameof(IsSearchFilterArtists))]
    [NotifyPropertyChangedFor(nameof(IsSearchFilterAlbums))]
    [NotifyPropertyChangedFor(nameof(IsSearchFilterPlaylists))]
    [NotifyPropertyChangedFor(nameof(HasSearchTopResultVisibility))]
    [NotifyPropertyChangedFor(nameof(HasSearchSongsSectionVisibility))]
    [NotifyPropertyChangedFor(nameof(HasSearchArtistsSectionVisibility))]
    [NotifyPropertyChangedFor(nameof(HasSearchAlbumsSectionVisibility))]
    [NotifyPropertyChangedFor(nameof(HasSearchPlaylistsSectionVisibility))]
    public partial SearchResultFilterTab SelectedSearchResultFilter { get; set; } = SearchResultFilterTab.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeekCurrentTrack))]
    public partial bool IsTrackLoading { get; set; }

    [ObservableProperty]
    public partial PlaylistCardViewModel CurrentPlaylistCard { get; set; } = new(new UserPlaylist());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscoveryLoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(IsHomeSkeletonActive))]
    [NotifyPropertyChangedFor(nameof(HomeSkeletonVisibility))]
    public partial bool IsLoadingDiscovery { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSkeletonActive))]
    [NotifyPropertyChangedFor(nameof(HomeSkeletonVisibility))]
    public partial bool IsHomeFeedInitialLoading { get; set; } = true;

    [ObservableProperty]
    public partial string HomePersonalizedSubtitle { get; set; } = "Personalized recommendations from your recent listening";

    [ObservableProperty]
    public partial string SelectedHomeGenre { get; set; } = "Pop";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HomeYtmLoginPromptVisibility))]
    public partial bool IsYtmLoginBannerSnoozed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HomeYtmLoginPromptVisibility))]
    public partial bool IsYtmLoginBannerDismissed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HomeCategoryGridHeaderTitle))]
    [NotifyPropertyChangedFor(nameof(HomeCategoryGridSubtitle))]
    [NotifyPropertyChangedFor(nameof(HomeCategoryGridToggleLabel))]
    [NotifyPropertyChangedFor(nameof(HomeCategoryGridToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(HomeNewReleasesFilterBarVisibility))]
    [NotifyPropertyChangedFor(nameof(HomeNewReleasesGridVisibility))]
    [NotifyPropertyChangedFor(nameof(HomeRadioStationsGridVisibility))]
    public partial HomeGridCategory ActiveHomeGridCategory { get; set; } = HomeGridCategory.NewReleases;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAllSelected))]
    [NotifyPropertyChangedFor(nameof(IsFilterAlbumsSelected))]
    [NotifyPropertyChangedFor(nameof(IsFilterSinglesSelected))]
    public partial NewReleasesFilter ActiveNewReleasesFilter { get; set; } = NewReleasesFilter.All;

    [ObservableProperty]
    public partial string HomeMoreLike1Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AlbumItem? HomeMoreLike1SeedAlbum { get; set; }

    [ObservableProperty]
    public partial string HomeMoreLike2Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AlbumItem? HomeMoreLike2SeedAlbum { get; set; }

    [ObservableProperty]
    public partial string HomeMoreLike3Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AlbumItem? HomeMoreLike3SeedAlbum { get; set; }

    [ObservableProperty]
    public partial ArtistDetailProfile CurrentArtistDetail { get; set; } = new();

    partial void OnCurrentArtistDetailChanged(ArtistDetailProfile value)
    {
        NotifyArtistDetailPresentationStateChanged();
    }

    [ObservableProperty]
    public partial bool IsArtistDetailLoading { get; set; }

    partial void OnIsArtistDetailLoadingChanged(bool value)
    {
        NotifyArtistDetailPresentationStateChanged();
    }

    [ObservableProperty]
    public partial bool IsArtistHeaderMediaLoading { get; set; }

    partial void OnIsArtistHeaderMediaLoadingChanged(bool value)
    {
        NotifyArtistDetailPresentationStateChanged();
    }

    [ObservableProperty]
    public partial string? CurrentArtistDetailVideoUri { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistBioMaxLines))]
    [NotifyPropertyChangedFor(nameof(ArtistBioToggleLabel))]
    public partial bool IsArtistBioExpanded { get; set; }

    public int ArtistBioMaxLines => IsArtistBioExpanded ? 24 : 2;
    public string ArtistBioToggleLabel => IsArtistBioExpanded ? "LESS" : "MORE";

    public Visibility ArtistSkeletonVisibility => IsArtistDetailLoading && ArtistTopTracks.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ArtistContentVisibility => !IsArtistDetailLoading || ArtistTopTracks.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ArtistWordmarkSkeletonVisibility =>
        IsArtistHeaderMediaLoading && !CurrentArtistDetail.HasCustomTitleLogo && string.IsNullOrWhiteSpace(CurrentArtistDetail.Name)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ArtistWordmarkImageVisibility => CurrentArtistDetail.HasCustomTitleLogo
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ArtistTitleTextVisibility => !CurrentArtistDetail.HasCustomTitleLogo
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ArtistSubscribersPillVisibility =>
        !IsArtistDetailLoading || !string.IsNullOrWhiteSpace(CurrentArtistDetail.SubscribersCountText)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ArtistSubscribersSkeletonVisibility =>
        IsArtistDetailLoading && string.IsNullOrWhiteSpace(CurrentArtistDetail.SubscribersCountText)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ArtistHeroLoadingVisibility =>
        IsArtistDetailLoading && ArtistTopTracks.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ArtistHeroLoadedVisibility =>
        !IsArtistDetailLoading || ArtistTopTracks.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ArtistBioToggleVisibility =>
        !IsArtistDetailLoading && (CurrentArtistDetail.Biography?.Length ?? 0) > 100
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasArtistAlbumsVisibility => ArtistCombinedAlbums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasArtistSinglesVisibility => ArtistSinglesAndEps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasArtistPlaylistsVisibility => ArtistPlaylists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasArtistSimilarVisibility => ArtistSimilarArtists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void NotifyArtistDetailPresentationStateChanged()
    {
        OnPropertyChanged(nameof(ArtistSkeletonVisibility));
        OnPropertyChanged(nameof(ArtistContentVisibility));
        OnPropertyChanged(nameof(ArtistWordmarkSkeletonVisibility));
        OnPropertyChanged(nameof(ArtistWordmarkImageVisibility));
        OnPropertyChanged(nameof(ArtistTitleTextVisibility));
        OnPropertyChanged(nameof(ArtistSubscribersPillVisibility));
        OnPropertyChanged(nameof(ArtistSubscribersSkeletonVisibility));
        OnPropertyChanged(nameof(ArtistHeroLoadingVisibility));
        OnPropertyChanged(nameof(ArtistHeroLoadedVisibility));
        OnPropertyChanged(nameof(ArtistBioToggleVisibility));
    }

    [RelayCommand]
    private void ToggleArtistBioExpanded()
    {
        IsArtistBioExpanded = !IsArtistBioExpanded;
    }

    [ObservableProperty]
    public partial AlbumDetailProfile CurrentAlbumDetail { get; set; } = new();

    partial void OnCurrentAlbumDetailChanged(AlbumDetailProfile value)
    {
        NotifyAlbumDetailPresentationStateChanged();
    }

    [ObservableProperty]
    public partial bool IsAlbumDetailLoading { get; set; }

    partial void OnIsAlbumDetailLoadingChanged(bool value)
    {
        NotifyAlbumDetailPresentationStateChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlbumDescriptionMaxLines))]
    [NotifyPropertyChangedFor(nameof(AlbumDescriptionToggleLabel))]
    public partial bool IsAlbumDescriptionExpanded { get; set; }

    public int AlbumDescriptionMaxLines => IsAlbumDescriptionExpanded ? 36 : 2;
    public string AlbumDescriptionToggleLabel => IsAlbumDescriptionExpanded ? "LESS" : "MORE";

    public string AlbumMoreByArtistHeader => string.IsNullOrWhiteSpace(CurrentAlbumDetail.Artist)
        ? "More by Artist"
        : $"More by {CurrentAlbumDetail.Artist}";

    public Visibility AlbumSkeletonVisibility => IsAlbumDetailLoading && AlbumDetailTracks.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AlbumContentVisibility => !IsAlbumDetailLoading || AlbumDetailTracks.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AlbumMetadataLoadedVisibility => !IsAlbumDetailLoading || AlbumDetailTracks.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AlbumMetadataSkeletonVisibility => IsAlbumDetailLoading && AlbumDetailTracks.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AlbumDescriptionVisibility =>
        !IsAlbumDetailLoading && !string.IsNullOrWhiteSpace(CurrentAlbumDetail.Description)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility AlbumDescriptionToggleVisibility =>
        !IsAlbumDetailLoading && (CurrentAlbumDetail.Description?.Length ?? 0) > 105
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasAlbumOtherVersionsVisibility =>
        !IsAlbumDetailLoading && AlbumOtherVersions.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasAlbumOtherAlbumsVisibility =>
        !IsAlbumDetailLoading && AlbumOtherAlbums.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void NotifyAlbumDetailPresentationStateChanged()
    {
        OnPropertyChanged(nameof(AlbumMoreByArtistHeader));
        OnPropertyChanged(nameof(AlbumSkeletonVisibility));
        OnPropertyChanged(nameof(AlbumContentVisibility));
        OnPropertyChanged(nameof(AlbumMetadataLoadedVisibility));
        OnPropertyChanged(nameof(AlbumMetadataSkeletonVisibility));
        OnPropertyChanged(nameof(AlbumDescriptionVisibility));
        OnPropertyChanged(nameof(AlbumDescriptionToggleVisibility));
        OnPropertyChanged(nameof(HasAlbumOtherVersionsVisibility));
        OnPropertyChanged(nameof(HasAlbumOtherAlbumsVisibility));
    }

    [RelayCommand]
    private void ToggleAlbumDescriptionExpanded()
    {
        IsAlbumDescriptionExpanded = !IsAlbumDescriptionExpanded;
        AlbumDescriptionExpandChanged?.Invoke(this, IsAlbumDescriptionExpanded);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaylistDescriptionVisibility))]
    [NotifyPropertyChangedFor(nameof(PlaylistDescriptionToggleVisibility))]
    public partial UserPlaylist CurrentPlaylistDetail { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaylistDescriptionMaxLines))]
    [NotifyPropertyChangedFor(nameof(PlaylistDescriptionToggleText))]
    public partial bool IsPlaylistDescriptionExpanded { get; set; }

    public int PlaylistDescriptionMaxLines => IsPlaylistDescriptionExpanded ? 36 : 2;
    public string PlaylistDescriptionToggleText => IsPlaylistDescriptionExpanded ? "LESS" : "MORE";

    public Visibility PlaylistDescriptionVisibility =>
        !string.IsNullOrWhiteSpace(CurrentPlaylistDetail?.Description)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility PlaylistDescriptionToggleVisibility =>
        (CurrentPlaylistDetail?.Description?.Trim().Length ?? 0) > 105
            ? Visibility.Visible
            : Visibility.Collapsed;

    [RelayCommand]
    private void TogglePlaylistDescriptionExpanded()
    {
        IsPlaylistDescriptionExpanded = !IsPlaylistDescriptionExpanded;
        PlaylistDescriptionExpandChanged?.Invoke(this, IsPlaylistDescriptionExpanded);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaylistRelatedSkeletonVisibility))]
    [NotifyPropertyChangedFor(nameof(PlaylistRelatedSectionVisibility))]
    public partial bool IsPlaylistRelatedLoading { get; set; }

    public Visibility PlaylistRelatedSkeletonVisibility =>
        IsPlaylistRelatedLoading && PlaylistRelatedTracks.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasPlaylistRelatedVisibility =>
        PlaylistRelatedTracks.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility PlaylistRelatedSectionVisibility =>
        IsPlaylistRelatedLoading || PlaylistRelatedTracks.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsAppearanceCategoryVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsLibraryCategoryVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsYouTubeCategoryVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsDownloadsCategoryVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsLyricsCategoryVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsUpdatesCategoryVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsPlaybackCategoryVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsDataUsageCategoryVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsAppearanceVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsLibraryFoldersVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsYouTubeAccountVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsDownloadsVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsLyricsVisualizersVisibility))]
    [NotifyPropertyChangedFor(nameof(SettingsUpdatesVisibility))]
    [NotifyPropertyChangedFor(nameof(IsSettingsAppearanceActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsLibraryFoldersActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsYouTubeAccountActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsDownloadsActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsLyricsVisualizersActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsUpdatesActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsPlaybackActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsDataUsageActive))]
    [NotifyPropertyChangedFor(nameof(SettingsCatAppearanceActiveOpacity))]
    [NotifyPropertyChangedFor(nameof(SettingsCatLibraryActiveOpacity))]
    [NotifyPropertyChangedFor(nameof(SettingsCatYouTubeActiveOpacity))]
    [NotifyPropertyChangedFor(nameof(SettingsCatDownloadsActiveOpacity))]
    [NotifyPropertyChangedFor(nameof(SettingsCatLyricsActiveOpacity))]
    [NotifyPropertyChangedFor(nameof(SettingsCatUpdatesActiveOpacity))]
    [NotifyPropertyChangedFor(nameof(SettingsCatPlaybackActiveOpacity))]
    [NotifyPropertyChangedFor(nameof(SettingsCatDataUsageActiveOpacity))]
    public partial SettingsCategory SelectedSettingsCategory { get; set; } = SettingsCategory.AppearanceAndGlass;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddToPlaylistModalVisibility))]
    public partial bool IsAddToPlaylistModalOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewPlaylistTitleInput))]
    public partial string NewPlaylistTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewPlaylistDescriptionInput))]
    public partial string NewPlaylistDescription { get; set; } = string.Empty;

    public string NewPlaylistTitleInput
    {
        get => NewPlaylistTitle;
        set => NewPlaylistTitle = value;
    }

    public string NewPlaylistDescriptionInput
    {
        get => NewPlaylistDescription;
        set => NewPlaylistDescription = value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewPlaylistCustomCoverLabel))]
    [NotifyPropertyChangedFor(nameof(NewPlaylistCoverStatusText))]
    public partial string NewPlaylistCustomCoverPath { get; set; } = string.Empty;

    public string NewPlaylistCustomCoverLabel => string.IsNullOrWhiteSpace(NewPlaylistCustomCoverPath)
        ? "Auto 2×2 Cover Collage (or click to pick custom photo)"
        : Path.GetFileName(NewPlaylistCustomCoverPath);

    public string NewPlaylistCoverStatusText => NewPlaylistCustomCoverLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YouTubeMusicSignedInVisibility))]
    [NotifyPropertyChangedFor(nameof(YouTubeMusicSignedOutVisibility))]
    [NotifyPropertyChangedFor(nameof(YouTubeDisconnectButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(HomeYtmLoginPromptVisibility))]
    public partial bool IsYouTubeMusicAuthenticated { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YouTubeAccountStatusText))]
    public partial string YouTubeMusicAccountStatusText { get; set; } = "Not signed in";

    public string YouTubeAccountStatusText => YouTubeMusicAccountStatusText;

    [ObservableProperty]
    public partial string YouTubeAccountLabelInput { get; set; } = "My YouTube Music";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YouTubeCookieInput))]
    public partial string YouTubeMusicCookieHeaderInput { get; set; } = string.Empty;

    public string YouTubeCookieInput
    {
        get => YouTubeMusicCookieHeaderInput;
        set => YouTubeMusicCookieHeaderInput = value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatModeGlyph))]
    [NotifyPropertyChangedFor(nameof(RepeatModeOpacity))]
    [NotifyPropertyChangedFor(nameof(RepeatButtonAccentOpacity))]
    [NotifyPropertyChangedFor(nameof(RepeatModeTooltip))]
    public partial PlaybackRepeatMode RepeatMode { get; set; } = PlaybackRepeatMode.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleButtonOpacity))]
    [NotifyPropertyChangedFor(nameof(ShuffleButtonAccentOpacity))]
    public partial bool IsShuffleEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullscreenQueueVisibility))]
    public partial bool IsFullscreenQueueOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbumDetailCanvasVideo))]
    public partial string? CurrentAlbumDetailCanvasUri { get; set; }

    /// <summary>
    /// Gets a value indicating whether an Apple Music Canvas video is active for the currently opened Album Detail screen.
    /// </summary>
    public bool HasAlbumDetailCanvasVideo => IsAppleMusicCanvasEnabled && !string.IsNullOrWhiteSpace(CurrentAlbumDetailCanvasUri);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterSongs))]
    [NotifyPropertyChangedFor(nameof(IsFilterAlbums))]
    [NotifyPropertyChangedFor(nameof(IsFilterArtists))]
    [NotifyPropertyChangedFor(nameof(IsSongsSectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsAlbumsSectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsArtistsSectionVisible))]
    [NotifyPropertyChangedFor(nameof(LibraryOverviewVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryArtistsDirectoryVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryAlbumsDirectoryVisibility))]
    [NotifyPropertyChangedFor(nameof(LibrarySongsDirectoryVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryAlphabetRailVisibility))]
    public partial SearchCategoryFilter SelectedCategory { get; set; } = SearchCategoryFilter.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceLocal))]
    [NotifyPropertyChangedFor(nameof(IsSourceOnline))]
    [NotifyPropertyChangedFor(nameof(IsSourceAll))]
    [NotifyPropertyChangedFor(nameof(LibraryHeaderTitle))]
    [NotifyPropertyChangedFor(nameof(LibraryHeaderSubtitle))]
    [NotifyPropertyChangedFor(nameof(LibraryEmptyHeadline))]
    [NotifyPropertyChangedFor(nameof(LibraryEmptyBody))]
    [NotifyPropertyChangedFor(nameof(LibraryEmptyImportActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryLocalToolsBarVisibility))]
    public partial MediaSourceType SelectedSourceType { get; set; } = MediaSourceType.Local;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryOverviewVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryRecentlyAddedViewVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryArtistsDirectoryVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryAlbumsDirectoryVisibility))]
    [NotifyPropertyChangedFor(nameof(LibrarySongsDirectoryVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryAlphabetRailVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryHeaderTitle))]
    [NotifyPropertyChangedFor(nameof(LibraryHeaderSubtitle))]
    [NotifyPropertyChangedFor(nameof(LibraryRecentlyAddedBackVisibility))]
    public partial bool IsLibraryRecentlyAddedExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryBusy))]
    [NotifyPropertyChangedFor(nameof(LibraryLoadingRingVisibility))]
    public partial bool IsLibraryLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueueTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsLyricsTabSelected))]
    [NotifyPropertyChangedFor(nameof(QueuePanelVisibility))]
    [NotifyPropertyChangedFor(nameof(LyricsPanelVisibility))]
    public partial NowPlayingTab ActiveNowPlayingTab { get; set; } = NowPlayingTab.Queue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchQuery))]
    [NotifyPropertyChangedFor(nameof(SearchExploreVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchResultsVisibility))]
    [NotifyPropertyChangedFor(nameof(SearchPageSubtitle))]
    [NotifyPropertyChangedFor(nameof(SearchClearOrCloseTooltip))]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchBoxVisibility))]
    [NotifyPropertyChangedFor(nameof(DesktopSearchCapsuleVisibility))]
    public partial bool IsSearchExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsStudioVisibility))]
    public partial bool IsSettingsStudioOpen { get; set; }

    partial void OnIsSettingsStudioOpenChanged(bool value) => RefreshResponsiveLayoutState();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraggableLensVisibility))]
    public partial bool IsDraggableLensVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LightDarkModeGlyph))]
    public partial bool IsLightMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeSystemActiveOpacity))]
    [NotifyPropertyChangedFor(nameof(ThemeSystemInactiveOpacity))]
    [NotifyPropertyChangedFor(nameof(ThemeDarkActiveOpacity))]
    [NotifyPropertyChangedFor(nameof(ThemeDarkInactiveOpacity))]
    [NotifyPropertyChangedFor(nameof(ThemeLightActiveOpacity))]
    [NotifyPropertyChangedFor(nameof(ThemeLightInactiveOpacity))]
    public partial AppThemePreference ThemePreference { get; set; } = AppThemePreference.Dark;

    public double ThemeSystemActiveOpacity => ThemePreference == AppThemePreference.System ? 1.0 : 0.0;
    public double ThemeSystemInactiveOpacity => ThemePreference == AppThemePreference.System ? 0.0 : 1.0;
    public double ThemeDarkActiveOpacity => ThemePreference == AppThemePreference.Dark ? 1.0 : 0.0;
    public double ThemeDarkInactiveOpacity => ThemePreference == AppThemePreference.Dark ? 0.0 : 1.0;
    public double ThemeLightActiveOpacity => ThemePreference == AppThemePreference.Light ? 1.0 : 0.0;
    public double ThemeLightInactiveOpacity => ThemePreference == AppThemePreference.Light ? 0.0 : 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NowPlayingSidebarToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(MiniPlayerHeaderVisibility))]
    public partial bool IsNowPlayingSidebarVisible { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextMenuLikeGlyph))]
    [NotifyPropertyChangedFor(nameof(ContextMenuLikeLabel))]
    [NotifyPropertyChangedFor(nameof(ContextMenuGoToArtistLabel))]
    [NotifyPropertyChangedFor(nameof(ContextMenuGoToAlbumLabel))]
    [NotifyPropertyChangedFor(nameof(ContextMenuTrackSourceDetail))]
    [NotifyPropertyChangedFor(nameof(ContextMenuDownloadVisibility))]
    public partial TrackItem? ContextMenuTrack { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackContextMenuVisibility))]
    public partial bool IsTrackContextMenuOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackInfoModalVisibility))]
    public partial bool IsTrackInfoModalOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedVolumePercent))]
    public partial double VolumePercent { get; set; } = 85.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullscreenVisualizerActive))]
    [NotifyPropertyChangedFor(nameof(FullscreenMenuVisualizerItemVisibility))]
    public partial bool IsFullscreenNowPlayingOpen { get; set; }

    partial void OnIsFullscreenNowPlayingOpenChanged(bool value) => RefreshResponsiveLayoutState();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullscreenLyricsColumnVisibility))]
    public partial bool IsFullscreenLyricsVisible { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullscreenSidePanelLyricsVisibility))]
    [NotifyPropertyChangedFor(nameof(FullscreenSidePanelQueueVisibility))]
    [NotifyPropertyChangedFor(nameof(IsFullscreenLyricsActive))]
    [NotifyPropertyChangedFor(nameof(IsFullscreenQueueActive))]
    [NotifyPropertyChangedFor(nameof(FullscreenLyricsPillHighlightOpacity))]
    [NotifyPropertyChangedFor(nameof(FullscreenQueuePillHighlightOpacity))]
    public partial bool IsFullscreenSidePanelOpen { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullscreenSidePanelLyricsVisibility))]
    [NotifyPropertyChangedFor(nameof(FullscreenSidePanelQueueVisibility))]
    [NotifyPropertyChangedFor(nameof(IsFullscreenLyricsActive))]
    [NotifyPropertyChangedFor(nameof(IsFullscreenQueueActive))]
    [NotifyPropertyChangedFor(nameof(FullscreenLyricsPillHighlightOpacity))]
    [NotifyPropertyChangedFor(nameof(FullscreenQueuePillHighlightOpacity))]
    public partial NowPlayingTab ActiveFullscreenTab { get; set; } = NowPlayingTab.Lyrics;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullscreenSeekButtonsVisibility))]
    public partial bool ShowFullscreenSeekButtons { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisualizerSelectorVisibility))]
    public partial bool IsVisualizerSelectorOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGpuVisualizerActive))]
    [NotifyPropertyChangedFor(nameof(IsFullscreenVisualizerActive))]
    [NotifyPropertyChangedFor(nameof(SelectedVisualizerDisplayName))]
    public partial VisualizerDefinition SelectedVisualizer { get; set; } = new VisualizerDefinition
    {
        Id = "BackdropOnly",
        Name = "Cover Backdrop Only (Off)",
        Author = "Xperience Studio",
        Description = "Displays only the atmospheric album cover backdrop without 3D spectrum geometry.",
        RenderMode = VisualizerRenderMode.None,
        UseDynamicCoverAccent = true,
        Sensitivity = 1.0,
        Speed = 1.0,
        GridResolution = 32,
        WaveLayers = 4,
        ParticleCount = 0,
        BackgroundDimOpacity = 0.55
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeekCurrentTrack))]
    public partial TrackItem CurrentTrack { get; set; } = new TrackItem
    {
        Id = "empty_state_track",
        Title = "No Track Loaded",
        Artist = "Click '+' to scan a folder or search online",
        Album = string.Empty,
        Duration = TimeSpan.Zero,
        CoverArtUri = string.Empty,
        AudioSourceUri = string.Empty,
        SourceType = MediaSourceType.Local,
        IsLiked = false
    };

    [ObservableProperty]
    public partial string CurrentLyricsProviderBadge { get; set; } = "Synced Lyrics";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveCanvasVideo))]
    [NotifyPropertyChangedFor(nameof(CanvasBadgeVisibility))]
    public partial string? CurrentCanvasVideoUri { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanvasBadgeVisibility))]
    public partial string CurrentCanvasProviderBadge { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveCanvasVideo))]
    [NotifyPropertyChangedFor(nameof(CanvasBadgeVisibility))]
    public partial bool IsAppleMusicCanvasEnabled { get; set; } = true;

    /// <summary>
    /// Gets a value indicating whether an animated Apple Music Canvas video is active for the current track.
    /// </summary>
    public bool HasActiveCanvasVideo => IsAppleMusicCanvasEnabled && !string.IsNullOrWhiteSpace(CurrentCanvasVideoUri);

    /// <summary>
    /// Gets the visibility of the Apple Music Canvas badge pill.
    /// </summary>
    public Visibility CanvasBadgeVisibility => HasActiveCanvasVideo ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseGlyph))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeGlyph))]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LikeButtonGlyph))]
    public partial bool IsCurrentTrackLiked { get; set; }

    [ObservableProperty]
    public partial double CurrentPositionSeconds { get; set; }

    [ObservableProperty]
    public partial double LyricsPositionSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeekCurrentTrack))]
    public partial double TotalDurationSeconds { get; set; } = 1.0;

    [ObservableProperty]
    public partial string FormattedPosition { get; set; } = "0:00";

    [ObservableProperty]
    public partial string FormattedDuration { get; set; } = "0:00";

    [ObservableProperty]
    public partial FlowDirection CurrentFlowDirection { get; set; } = FlowDirection.LeftToRight;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryBusy))]
    [NotifyPropertyChangedFor(nameof(LibraryLoadingRingVisibility))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial bool IsDynamicCoverAccentEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string ActiveAccentColorHex { get; set; } = "#FF2DD4BF";

    // Composition + Win2D Liquid Glass live parameters (Aligned with Android Liquid Glass defaults: 2.5, 0.008, 65%, 55%, 65%)
    private CancellationTokenSource? _glassPersistCts;

    [ObservableProperty]
    public partial double FrostBlurRadius { get; set; } = 2.0;

    [ObservableProperty]
    public partial double TurbulenceFrequency { get; set; } = 0.008;

    [ObservableProperty]
    public partial double DistortionStrength { get; set; } = 60.0;

    [ObservableProperty]
    public partial double TintOpacityPercent { get; set; } = 50.0;

    [ObservableProperty]
    public partial double SpecularRimPercent { get; set; } = 55.0;

    // Mornye & YouTube Music Download Quality & Offline Metadata Settings
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadFormatFlac))]
    [NotifyPropertyChangedFor(nameof(IsDownloadFormatMp3))]
    [NotifyPropertyChangedFor(nameof(IsDownloadFormatM4a))]
    [NotifyPropertyChangedFor(nameof(ContextMenuDefaultDownloadLabel))]
    public partial DownloadAudioFormat SelectedDownloadFormat { get; set; } = DownloadAudioFormat.FlacLossless;

    [ObservableProperty]
    public partial string DownloadTargetDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool? DownloadIncludeTitleAndArtist { get; set; } = true;

    [ObservableProperty]
    public partial bool? DownloadIncludeAlbumAndTrackNumber { get; set; } = true;

    [ObservableProperty]
    public partial bool? DownloadIncludeHighResCoverArt { get; set; } = true;

    [ObservableProperty]
    public partial bool? DownloadIncludeGenreAndReleaseYear { get; set; } = true;

    [ObservableProperty]
    public partial bool? DownloadIncludeIsrcAndCopyright { get; set; } = true;

    [ObservableProperty]
    public partial bool? DownloadIncludeReplayGain { get; set; } = true;

    [ObservableProperty]
    public partial bool? DownloadIncludeSynchronizedLyrics { get; set; } = true;

    [ObservableProperty]
    public partial bool? DownloadIncludeStaticLyrics { get; set; } = true;

    [ObservableProperty]
    public partial bool? DownloadIncludeAppleCanvasOffline { get; set; } = true;

    public bool IsDownloadFormatFlac => SelectedDownloadFormat == DownloadAudioFormat.FlacLossless;
    public bool IsDownloadFormatMp3 => SelectedDownloadFormat == DownloadAudioFormat.Mp3MaxQuality;
    public bool IsDownloadFormatM4a => SelectedDownloadFormat == DownloadAudioFormat.M4aNative;

    public string ContextMenuDefaultDownloadLabel => SelectedDownloadFormat switch
    {
        DownloadAudioFormat.FlacLossless => "Download (FLAC Lossless • Mornye)",
        DownloadAudioFormat.Mp3MaxQuality => "Download (MP3 320kbps • YT Music)",
        _ => "Download (M4A Native)"
    };

    // =========================================================================
    // FLOATING LIQUIDGLASS DOWNLOAD BAR & OFFLINE QUEUE STATE
    // =========================================================================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadBarVisibility))]
    public partial bool IsDownloadBarVisible { get; set; }

    public Visibility DownloadBarVisibility => IsDownloadBarVisible ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentDownloadPercentText))]
    public partial double CurrentDownloadPercent { get; set; }

    public string CurrentDownloadPercentText => $"{Math.Round(CurrentDownloadPercent)}%";

    [ObservableProperty]
    public partial string CurrentDownloadTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentDownloadArtist { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentDownloadStage { get; set; } = "Starting download...";

    [ObservableProperty]
    public partial string CurrentDownloadBadge { get; set; } = "FLAC Lossless";

    [ObservableProperty]
    public partial bool IsCurrentlyDownloading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadQueueFlyoutVisibility))]
    public partial bool IsDownloadQueueFlyoutOpen { get; set; }

    public Visibility DownloadQueueFlyoutVisibility => IsDownloadQueueFlyoutOpen ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<DownloadQueueItemViewModel> DownloadQueue { get; } = new();

    public int ActiveDownloadsCount => DownloadQueue.Count(i => i.IsDownloading);

    public int CompletedDownloadsCount => DownloadQueue.Count(i => i.IsCompleted);

    public string DownloadQueueSummaryText => $"{ActiveDownloadsCount} en curso • {CompletedDownloadsCount} completadas";

    // =========================================================================
    // RECOMMENDED DOWNLOAD & CANDIDATE VERSIONS MODAL STATE
    // =========================================================================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadCandidatesModalVisibility))]
    public partial bool IsDownloadCandidatesModalOpen { get; set; }

    public Visibility DownloadCandidatesModalVisibility => IsDownloadCandidatesModalOpen ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchingCandidatesVisibility))]
    [NotifyPropertyChangedFor(nameof(CandidatesContentVisibility))]
    public partial bool IsSearchingCandidates { get; set; }

    public Visibility SearchingCandidatesVisibility => IsSearchingCandidates ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CandidatesContentVisibility => IsSearchingCandidates ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    public partial TrackItem? TargetDownloadTrack { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecommendedCandidate))]
    [NotifyPropertyChangedFor(nameof(RecommendedCandidateVisibility))]
    public partial TrackDownloadCandidate? RecommendedCandidate { get; set; }

    public bool HasRecommendedCandidate => RecommendedCandidate is not null;

    public Visibility RecommendedCandidateVisibility => RecommendedCandidate is not null ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<TrackDownloadCandidate> AlternativeCandidates { get; } = new();

    public Visibility AlternativeCandidatesHeaderVisibility => AlternativeCandidates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the Segoe Fluent Icon glyph for Play vs Pause.
    /// </summary>
    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";

    /// <summary>
    /// Gets the Segoe Fluent Icon glyph for Speaker vs Muted.
    /// </summary>
    public string VolumeGlyph => IsMuted ? "\uE74F" : "\uE767";

    /// <summary>
    /// Gets the Segoe Fluent Icon glyph for Heart/Liked state.
    /// </summary>
    public string LikeButtonGlyph => IsCurrentTrackLiked ? "\uEB52" : "\uEB51";

    /// <summary>
    /// Gets the Sun/Moon glyph for the Light/Dark mode quick toggle.
    /// </summary>
    public string LightDarkModeGlyph => IsLightMode ? "\uE708" : "\uE706";

    /// <summary>
    /// Gets the integer formatted volume string (e.g. "85") for the Fullscreen playback bar.
    /// </summary>
    public string FormattedVolumePercent => $"{Math.Round(VolumePercent, 0):0}";

    /// <summary>
    /// Gets a value indicating whether a GPU spectrum visualizer is currently active.
    /// </summary>
    public bool IsGpuVisualizerActive => SelectedVisualizer.RenderMode != VisualizerRenderMode.None;

    /// <summary>
    /// Gets whether the Fullscreen GPU visualizer Win2D canvas should be loaded and actively rendering.
    /// </summary>
    public bool IsFullscreenVisualizerActive => IsFullscreenNowPlayingOpen && IsGpuVisualizerActive;

    /// <summary>
    /// Gets the display name of the currently active visualizer.
    /// </summary>
    public string SelectedVisualizerDisplayName => SelectedVisualizer.Name;

    /// <summary>
    /// Gets the visibility of the right-hand synchronized lyrics column inside Fullscreen Now Playing.
    /// </summary>
    public Visibility FullscreenLyricsColumnVisibility => IsFullscreenSidePanelOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets whether the Fullscreen Lyrics tab is actively displayed on the right side panel.
    /// </summary>
    public bool IsFullscreenLyricsActive => IsFullscreenSidePanelOpen && ActiveFullscreenTab == NowPlayingTab.Lyrics;

    /// <summary>
    /// Gets whether the Fullscreen Queue tab is actively displayed on the right side panel.
    /// </summary>
    public bool IsFullscreenQueueActive => IsFullscreenSidePanelOpen && ActiveFullscreenTab == NowPlayingTab.Queue;

    /// <summary>
    /// Gets the visibility of the Lyrics pane inside the Fullscreen right side panel.
    /// </summary>
    public Visibility FullscreenSidePanelLyricsVisibility =>
        IsFullscreenSidePanelOpen && ActiveFullscreenTab == NowPlayingTab.Lyrics
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Gets the visibility of the Queue pane inside the Fullscreen right side panel.
    /// </summary>
    public Visibility FullscreenSidePanelQueueVisibility =>
        IsFullscreenSidePanelOpen && ActiveFullscreenTab == NowPlayingTab.Queue
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Gets the active highlight opacity for the Lyrics icon in the Fullscreen bottom action pill.
    /// </summary>
    public double FullscreenLyricsPillHighlightOpacity => IsFullscreenLyricsActive ? 1.0 : 0.0;

    /// <summary>
    /// Gets the active highlight opacity for the Queue icon in the Fullscreen bottom action pill.
    /// </summary>
    public double FullscreenQueuePillHighlightOpacity => IsFullscreenQueueActive ? 1.0 : 0.0;

    /// <summary>
    /// Gets the visibility of the optional -5s / +10s seek buttons in Fullscreen Now Playing (hidden by default).
    /// </summary>
    public Visibility FullscreenSeekButtonsVisibility =>
        ShowFullscreenSeekButtons ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the visibility of the GPU Music Visualizers item inside the track context menu when Fullscreen is open.
    /// </summary>
    public Visibility FullscreenMenuVisualizerItemVisibility =>
        IsFullscreenNowPlayingOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the visibility of the floating GPU Visualizer Selector popup inside Fullscreen Now Playing.
    /// </summary>
    public Visibility VisualizerSelectorVisibility => IsVisualizerSelectorOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the maximum height of the scrollable Songs container (expands when Songs category filter is active).
    /// </summary>
    public double SongsListMaxHeight => SelectedCategory == SearchCategoryFilter.Songs ? 460.0 : 210.0;

    // Section active helpers for compiled x:Bind
    public bool IsHomeSection => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.MainSection && CurrentSection == NavigationSection.Home;
    public bool IsLibrarySection => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.MainSection && CurrentSection == NavigationSection.Library;
    public bool IsLikedSection => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.MainSection && CurrentSection == NavigationSection.Liked;
    public bool IsPlaylistsSection => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.MainSection && CurrentSection == NavigationSection.Playlists;
    public bool IsCommunitySection => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.MainSection && CurrentSection == NavigationSection.Community;

    // Clean boolean flags for WinUI x:Load deferred UI realization & compiled x:Bind
    public bool IsSearchViewOpen => IsSearchPageOpen;
    public bool IsHomeViewOpen => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.MainSection && CurrentSection == NavigationSection.Home;
    public bool IsLibraryViewOpen => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.MainSection && CurrentSection is NavigationSection.Library or NavigationSection.Community;
    public bool IsLikedViewOpen => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.MainSection && CurrentSection == NavigationSection.Liked;
    public bool IsPlaylistsViewOpen => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.MainSection && CurrentSection == NavigationSection.Playlists;
    public bool IsArtistDetailViewOpen => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.ArtistDetail;
    public bool IsAlbumDetailViewOpen => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.AlbumDetail;
    public bool IsPlaylistDetailViewOpen => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.PlaylistDetail;
    public bool IsSettingsViewOpen => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.Settings;
    public bool IsHomeCategoryGridViewOpen => !IsSearchPageOpen && ActiveSubView == WorkspaceSubView.HomeCategoryGrid;

    // Dedicated Search Page & workspace visibilities (Search overlays without hijacking CurrentSection/ActiveSubView)
    public Visibility SearchViewVisibility => IsSearchViewOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility HomeViewVisibility => IsHomeViewOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility LibraryViewVisibility => IsLibraryViewOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility LikedViewVisibility => IsLikedViewOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PlaylistsViewVisibility => IsPlaylistsViewOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ArtistDetailViewVisibility => IsArtistDetailViewOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AlbumDetailViewVisibility => IsAlbumDetailViewOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PlaylistDetailViewVisibility => IsPlaylistDetailViewOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SettingsViewVisibility => IsSettingsViewOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility HomeCategoryGridViewVisibility => IsHomeCategoryGridViewOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility HomeAuroraVisibility =>
        IsSearchPageOpen ||
        (ActiveSubView == WorkspaceSubView.MainSection && CurrentSection is NavigationSection.Home or NavigationSection.Library or NavigationSection.Liked or NavigationSection.Playlists or NavigationSection.Community) ||
        ActiveSubView == WorkspaceSubView.HomeCategoryGrid
            ? Visibility.Visible
            : Visibility.Collapsed;

    // Dedicated Search & Moods/Genres computed properties
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    public string SearchSourceGlyph => IsSearchOnlineMode ? "\uE774" : "\uE8B7";

    public string SearchSourceTooltip => IsSearchOnlineMode
        ? "Searching YouTube Music — click for Local Library"
        : "Searching Local Library — click for YouTube Music";

    public string SearchSourceBadgeText => IsSearchOnlineMode ? "YouTube Music" : "Local Library";

    public string SearchClearOrCloseTooltip => HasSearchQuery
        ? "Clear search text"
        : "Close Search and return";

    public string SearchPageSubtitle
    {
        get
        {
            if (HasSearchQuery)
            {
                return $"Results for \"{SearchQuery.Trim()}\" · {SearchSourceBadgeText}";
            }

            if (SelectedMoodGenreTile is not null)
            {
                return $"Moods & Genres · {SelectedMoodGenreTile.Title}";
            }

            return "Explore music, moods & genres";
        }
    }

    public Visibility SearchExploreVisibility => !HasSearchQuery ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SearchResultsVisibility => HasSearchQuery ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasRecentSearchesVisibility => RecentSearches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MoodsLoadingVisibility => IsLoadingMoodsAndGenres && MoodGenreSections.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MoodsContentVisibility => !IsLoadingMoodsAndGenres || MoodGenreSections.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MoodDetailEmptyPromptVisibility => SelectedMoodGenreTile is null && !IsLoadingMoodDetail
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MoodDetailLoadingVisibility => IsLoadingMoodDetail
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MoodDetailContentVisibility => SelectedMoodGenreTile is not null && !IsLoadingMoodDetail
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string SelectedMoodTitle => SelectedMoodGenreTile?.Title ?? string.Empty;

    public Microsoft.UI.Xaml.Media.SolidColorBrush SelectedMoodStripeBrush =>
        SelectedMoodGenreTile?.StripeBrush ?? FallbackMoodStripeBrush.Value;

    public Visibility HasMoodDetailPlaylistsVisibility => MoodDetailPlaylists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasMoodDetailAlbumsVisibility => MoodDetailAlbums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasMoodDetailSongsVisibility => MoodDetailSongs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MoodDetailEmptyResultVisibility =>
        SelectedMoodGenreTile is not null && !IsLoadingMoodDetail &&
        MoodDetailPlaylists.Count == 0 && MoodDetailAlbums.Count == 0 && MoodDetailSongs.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool IsSearchFilterAll => SelectedSearchResultFilter == SearchResultFilterTab.All;
    public bool IsSearchFilterSongs => SelectedSearchResultFilter == SearchResultFilterTab.Songs;
    public bool IsSearchFilterArtists => SelectedSearchResultFilter == SearchResultFilterTab.Artists;
    public bool IsSearchFilterAlbums => SelectedSearchResultFilter == SearchResultFilterTab.Albums;
    public bool IsSearchFilterPlaylists => SelectedSearchResultFilter == SearchResultFilterTab.Playlists;

    public Visibility SearchLoadingVisibility => IsSearchingCatalog ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SearchResultsContentVisibility => !IsSearchingCatalog && HasAnySearchResults
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SearchNoResultsVisibility => HasSearchQuery && !IsSearchingCatalog && !HasAnySearchResults
        ? Visibility.Visible
        : Visibility.Collapsed;

    private bool HasAnySearchResults =>
        SearchResultTopResult is not null ||
        SearchResultSongs.Count > 0 ||
        SearchResultArtists.Count > 0 ||
        SearchResultAlbums.Count > 0 ||
        SearchResultPlaylists.Count > 0;

    public Visibility HasSearchTopResultVisibility =>
        !IsSearchingCatalog && SelectedSearchResultFilter == SearchResultFilterTab.All && SearchResultTopResult is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasSearchSongsSectionVisibility =>
        !IsSearchingCatalog && (SelectedSearchResultFilter is SearchResultFilterTab.All or SearchResultFilterTab.Songs) && SearchResultSongs.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasSearchArtistsSectionVisibility =>
        !IsSearchingCatalog && (SelectedSearchResultFilter is SearchResultFilterTab.All or SearchResultFilterTab.Artists) && SearchResultArtists.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasSearchAlbumsSectionVisibility =>
        !IsSearchingCatalog && (SelectedSearchResultFilter is SearchResultFilterTab.All or SearchResultFilterTab.Albums) && SearchResultAlbums.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasSearchPlaylistsSectionVisibility =>
        !IsSearchingCatalog && (SelectedSearchResultFilter is SearchResultFilterTab.All or SearchResultFilterTab.Playlists) && SearchResultPlaylists.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string SearchSongsCountLabel => $"{SearchResultSongs.Count}";
    public string SearchArtistsCountLabel => $"{SearchResultArtists.Count}";
    public string SearchAlbumsCountLabel => $"{SearchResultAlbums.Count}";
    public string SearchPlaylistsCountLabel => $"{SearchResultPlaylists.Count}";

    public string SearchTopResultTitle => SearchResultTopResult?.Title ?? string.Empty;
    public string SearchTopResultSubtitle => SearchResultTopResult?.Subtitle ?? string.Empty;
    public string SearchTopResultKindBadgeText => SearchResultTopResult?.KindBadgeText ?? "Top Result";
    public string SearchTopResultSafeArtworkUri => SearchResultTopResult?.SafeArtworkUri ?? TrackItem.FallbackTransparentCoverUri;
    public double SearchTopResultArtworkOpacity => SearchResultTopResult?.ArtworkOpacity ?? 0.0;
    public double SearchTopResultDefaultIconOpacity => SearchResultTopResult?.DefaultIconOpacity ?? 1.0;
    public CornerRadius SearchTopResultCornerRadius => SearchResultTopResult?.IsArtist == true ? new CornerRadius(38) : new CornerRadius(16);

    public bool CanSeekCurrentTrack =>
        !IsTrackLoading &&
        CurrentTrack is not null &&
        !string.Equals(CurrentTrack.Id, "empty_state_track", StringComparison.Ordinal) &&
        TotalDurationSeconds > 1.0;

    public Visibility HomeYtmLoginPromptVisibility =>
        !IsYouTubeMusicAuthenticated && !IsYtmLoginBannerSnoozed && !IsYtmLoginBannerDismissed
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HomeTopPicksVisibility => HomeTopPicks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HomePlaylistsMadeForYouVisibility => HomePlaylistsMadeForYou.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HomeRadioStationsVisibility => HomeRadioStations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HomeNewReleasesVisibility => HomeNewReleases.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HomeMoreLike1Visibility => HomeMoreLike1.Count > 0 && !string.IsNullOrWhiteSpace(HomeMoreLike1Title) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HomeMoreLike2Visibility => HomeMoreLike2.Count > 0 && !string.IsNullOrWhiteSpace(HomeMoreLike2Title) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HomeMoreLike3Visibility => HomeMoreLike3.Count > 0 && !string.IsNullOrWhiteSpace(HomeMoreLike3Title) ? Visibility.Visible : Visibility.Collapsed;

    public string HomeCategoryGridHeaderTitle => ActiveHomeGridCategory == HomeGridCategory.NewReleases
        ? "New Releases"
        : "Radio Stations";

    public string HomeCategoryGridSubtitle => ActiveHomeGridCategory == HomeGridCategory.NewReleases
        ? $"{_allNewReleases.Count} recent releases"
        : $"{_allRadioStations.Count} recommended stations";

    public string HomeCategoryGridToggleLabel => ActiveHomeGridCategory == HomeGridCategory.NewReleases
        ? "Radio Stations"
        : "New Releases";

    public string HomeCategoryGridToggleGlyph => ActiveHomeGridCategory == HomeGridCategory.NewReleases
        ? "\uE95A"
        : "\uEC4F";

    public Visibility HomeNewReleasesFilterBarVisibility => ActiveHomeGridCategory == HomeGridCategory.NewReleases
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility HomeNewReleasesGridVisibility => ActiveHomeGridCategory == HomeGridCategory.NewReleases
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility HomeRadioStationsGridVisibility => ActiveHomeGridCategory == HomeGridCategory.RadioStations
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsFilterAllSelected => ActiveNewReleasesFilter == NewReleasesFilter.All;
    public bool IsFilterAlbumsSelected => ActiveNewReleasesFilter == NewReleasesFilter.Albums;
    public bool IsFilterSinglesSelected => ActiveNewReleasesFilter == NewReleasesFilter.Singles;

    public string NewReleasesFilterAllLabel => $"All ({_allNewReleases.Count})";
    public string NewReleasesFilterAlbumsLabel => $"Albums ({_allNewReleases.Count(r => r.IsAlbum)})";
    public string NewReleasesFilterSinglesLabel => $"Singles ({_allNewReleases.Count(r => !r.IsAlbum)})";

    public Visibility DetailBackButtonVisibility => ActiveSubView != WorkspaceSubView.MainSection
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility DiscoveryLoadingVisibility => IsLoadingDiscovery ? Visibility.Visible : Visibility.Collapsed;

    public bool IsHomeSkeletonActive =>
        (IsHomeFeedInitialLoading || IsLoadingDiscovery) &&
        HomeTopPicks.Count == 0 &&
        HomeRecentlyPlayedAlbums.Count == 0 &&
        HomeMoreLike1.Count == 0;

    public Visibility HomeSkeletonVisibility => IsHomeSkeletonActive ? Visibility.Visible : Visibility.Collapsed;

    // Settings category visibilities & active indicators
    public Visibility SettingsAppearanceCategoryVisibility => SelectedSettingsCategory == SettingsCategory.AppearanceAndGlass ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SettingsLibraryCategoryVisibility => SelectedSettingsCategory == SettingsCategory.LibraryAndFolders ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SettingsYouTubeCategoryVisibility => SelectedSettingsCategory == SettingsCategory.YouTubeMusicAccount ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SettingsDownloadsCategoryVisibility => SelectedSettingsCategory == SettingsCategory.DownloadsAndOffline ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SettingsLyricsCategoryVisibility => SelectedSettingsCategory == SettingsCategory.LyricsAndVisualizers ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SettingsUpdatesCategoryVisibility => SelectedSettingsCategory == SettingsCategory.Updates ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SettingsAppearanceVisibility => SettingsAppearanceCategoryVisibility;
    public Visibility SettingsLibraryFoldersVisibility => SettingsLibraryCategoryVisibility;
    public Visibility SettingsYouTubeAccountVisibility => SettingsYouTubeCategoryVisibility;
    public Visibility SettingsDownloadsVisibility => SettingsDownloadsCategoryVisibility;
    public Visibility SettingsLyricsVisualizersVisibility => SettingsLyricsCategoryVisibility;
    public Visibility SettingsUpdatesVisibility => SettingsUpdatesCategoryVisibility;

    public bool IsSettingsAppearanceActive => SelectedSettingsCategory == SettingsCategory.AppearanceAndGlass;
    public bool IsSettingsLibraryFoldersActive => SelectedSettingsCategory == SettingsCategory.LibraryAndFolders;
    public bool IsSettingsYouTubeAccountActive => SelectedSettingsCategory == SettingsCategory.YouTubeMusicAccount;
    public bool IsSettingsDownloadsActive => SelectedSettingsCategory == SettingsCategory.DownloadsAndOffline;
    public bool IsSettingsLyricsVisualizersActive => SelectedSettingsCategory == SettingsCategory.LyricsAndVisualizers;
    public bool IsSettingsUpdatesActive => SelectedSettingsCategory == SettingsCategory.Updates;

    public double SettingsCatAppearanceActiveOpacity => SelectedSettingsCategory == SettingsCategory.AppearanceAndGlass ? 1.0 : 0.0;
    public double SettingsCatLibraryActiveOpacity => SelectedSettingsCategory == SettingsCategory.LibraryAndFolders ? 1.0 : 0.0;
    public double SettingsCatYouTubeActiveOpacity => SelectedSettingsCategory == SettingsCategory.YouTubeMusicAccount ? 1.0 : 0.0;
    public double SettingsCatDownloadsActiveOpacity => SelectedSettingsCategory == SettingsCategory.DownloadsAndOffline ? 1.0 : 0.0;
    public double SettingsCatLyricsActiveOpacity => SelectedSettingsCategory == SettingsCategory.LyricsAndVisualizers ? 1.0 : 0.0;
    public double SettingsCatUpdatesActiveOpacity => SelectedSettingsCategory == SettingsCategory.Updates ? 1.0 : 0.0;

    // Playlists, Local Directories & Add-to-Playlist modal visibilities
    public Visibility EmptyPlaylistsVisibility => Playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasPlaylistsVisibility => Playlists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyLocalDirectoriesVisibility => LocalMusicDirectories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasLocalDirectoriesVisibility => LocalMusicDirectories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AddToPlaylistModalVisibility => IsAddToPlaylistModalOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility YouTubeMusicSignedInVisibility => IsYouTubeMusicAuthenticated ? Visibility.Visible : Visibility.Collapsed;
    public Visibility YouTubeMusicSignedOutVisibility => IsYouTubeMusicAuthenticated ? Visibility.Collapsed : Visibility.Visible;
    public Visibility YouTubeDisconnectButtonVisibility => YouTubeMusicSignedInVisibility;

    // Repeat, Shuffle & Fullscreen Queue helpers
    public string RepeatModeGlyph => RepeatMode == PlaybackRepeatMode.RepeatOne ? "\uE8ED" : "\uE8EE";
    public double RepeatModeOpacity => RepeatMode == PlaybackRepeatMode.None ? 0.50 : 1.0;
    public double RepeatButtonAccentOpacity => RepeatModeOpacity;
    public string RepeatModeTooltip => RepeatMode switch
    {
        PlaybackRepeatMode.RepeatOne => "Repeat Mode: Repeat Current Song",
        PlaybackRepeatMode.RepeatAll => "Repeat Mode: Repeat Queue",
        _ => "Repeat Mode: Off"
    };
    public double ShuffleButtonOpacity => IsShuffleEnabled ? 1.0 : 0.50;
    public double ShuffleButtonAccentOpacity => ShuffleButtonOpacity;
    public Visibility FullscreenQueueVisibility => IsFullscreenQueueOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContextMenuDownloadVisibility => ContextMenuTrack is { IsDownloadedOrLocal: false } ? Visibility.Visible : Visibility.Collapsed;

    // Command aliases for compiled x:Bind
    public IAsyncRelayCommand AddLocalMusicDirectoryCommand => ScanUserMusicFolderCommand;
    public IRelayCommand LaunchInteractiveYouTubeLoginCommand => OpenYouTubeMusicWebSignInCommand;
    public IAsyncRelayCommand DisconnectYouTubeAccountCommand => DisconnectYouTubeMusicAccountCommand;
    public IAsyncRelayCommand ConnectYouTubeAccountCookieCommand => ConnectYouTubeMusicCookieCommand;
    public IAsyncRelayCommand PlayCurrentPlaylistCommand => PlayAllCurrentPlaylistCommand;

    // Liked songs filter/sort & view helpers (Android parity: Songs | Artists | Albums & A–Z | Recent)
    public bool IsLikedListMode => SelectedLikedViewMode == LikedSongsViewMode.List;
    public bool IsLikedGridMode => SelectedLikedViewMode == LikedSongsViewMode.Grid;
    public Visibility LikedListViewVisibility => SelectedLikedViewMode == LikedSongsViewMode.List ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LikedGridViewVisibility => SelectedLikedViewMode == LikedSongsViewMode.Grid ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyLikedVisibility => LikedSongs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasLikedSongsVisibility => LikedSongs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsLikedFilterSongs => SelectedLikedFilter == LikedFilterTab.Songs;
    public bool IsLikedFilterArtists => SelectedLikedFilter == LikedFilterTab.Artists;
    public bool IsLikedFilterAlbums => SelectedLikedFilter == LikedFilterTab.Albums;
    public bool IsLikedSortAlphabetical => SelectedLikedSort == LikedSortMode.Alphabetical;
    public bool IsLikedSortRecent => SelectedLikedSort == LikedSortMode.Recent;

    public Visibility LikedSongsRecentVisibility =>
        LikedSongs.Count > 0 && SelectedLikedFilter == LikedFilterTab.Songs && SelectedLikedSort == LikedSortMode.Recent
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedSongsAlphaVisibility =>
        LikedSongs.Count > 0 && SelectedLikedFilter == LikedFilterTab.Songs && SelectedLikedSort == LikedSortMode.Alphabetical
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedSongsEmptyVisibility =>
        SelectedLikedFilter == LikedFilterTab.Songs && LikedSongs.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedPlayActionsVisibility =>
        SelectedLikedFilter == LikedFilterTab.Songs && LikedSongs.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedRightRecommendationsPaneVisibility =>
        SelectedLikedFilter == LikedFilterTab.Songs
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedArtistsRecentVisibility =>
        LikedDerivedArtists.Count > 0 && SelectedLikedFilter == LikedFilterTab.Artists && SelectedLikedSort == LikedSortMode.Recent
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedArtistsAlphaVisibility =>
        LikedDerivedArtists.Count > 0 && SelectedLikedFilter == LikedFilterTab.Artists && SelectedLikedSort == LikedSortMode.Alphabetical
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedArtistsEmptyVisibility =>
        SelectedLikedFilter == LikedFilterTab.Artists && LikedDerivedArtists.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedAlbumsRecentVisibility =>
        LikedDerivedAlbums.Count > 0 && SelectedLikedFilter == LikedFilterTab.Albums && SelectedLikedSort == LikedSortMode.Recent
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedAlbumsAlphaVisibility =>
        LikedDerivedAlbums.Count > 0 && SelectedLikedFilter == LikedFilterTab.Albums && SelectedLikedSort == LikedSortMode.Alphabetical
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedAlbumsEmptyVisibility =>
        SelectedLikedFilter == LikedFilterTab.Albums && LikedDerivedAlbums.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedAlphabetRailVisibility =>
        CurrentSection == NavigationSection.Liked &&
        ActiveSubView == WorkspaceSubView.MainSection &&
        !IsSearchPageOpen &&
        SelectedLikedSort == LikedSortMode.Alphabetical &&
        GetActiveLikedAvailableLetters().Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility LikedRecommendationsLoadingVisibility =>
        IsLoadingLikedRecommendations && LikedRecommendations.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedRecommendationsEmptyVisibility =>
        !IsLoadingLikedRecommendations && LikedRecommendations.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LikedRecommendationsListVisibility =>
        LikedRecommendations.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Returns the ordered available A–Z + '#' letters for the currently active Liked alphabetical tab.
    /// </summary>
    public IReadOnlyList<string> GetActiveLikedAvailableLetters()
    {
        return SelectedLikedFilter switch
        {
            LikedFilterTab.Artists => LikedArtistGroups.Select(g => g.Letter).ToList(),
            LikedFilterTab.Albums => LikedAlbumGroups.Select(g => g.Letter).ToList(),
            _ => LikedSongGroups.Select(g => g.Letter).ToList()
        };
    }

    public string LikedStatsSubtitle
    {
        get
        {
            if (LikedSongs.Count == 0)
            {
                return "Your favorite tracks from Local Library and YouTube Music";
            }

            string songWord = LikedSongs.Count == 1 ? "1 song" : $"{LikedSongs.Count} songs";
            string duration = FormatCollectionDuration(LikedSongs.Sum(t => t.Duration.TotalSeconds));
            int localCount = LikedSongs.Count(t => t.SourceType == MediaSourceType.Local || t.IsDownloadedOrLocal);
            return localCount > 0
                ? $"{songWord} • {duration} • {localCount} local"
                : $"{songWord} • {duration}";
        }
    }

    // Playlists & Playlist Detail computed headers/durations (Android parity)
    public string PlaylistsStatsSubtitle
    {
        get
        {
            if (Playlists.Count == 0)
            {
                return "Curate your personal collections";
            }

            int totalTracks = Playlists.Sum(p => p.Tracks.Count);
            string plWord = Playlists.Count == 1 ? "1 playlist" : $"{Playlists.Count} playlists";
            string trWord = totalTracks == 1 ? "1 track" : $"{totalTracks} tracks";
            return $"{plWord} · {trWord}";
        }
    }

    public string PlaylistsTotalDurationText
    {
        get
        {
            double totalSec = Playlists.SelectMany(p => p.Tracks).Sum(t => t.Duration.TotalSeconds);
            return totalSec > 1.0 ? FormatCollectionDuration(totalSec) : "Ready to mix";
        }
    }

    public Visibility EmptyPlaylistDetailTracksVisibility => PlaylistDetailTracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasPlaylistDetailTracksVisibility => PlaylistDetailTracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string PlaylistDetailTrackCountText => PlaylistDetailTracks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string PlaylistDetailDurationText => PlaylistDetailTracks.Count > 0
        ? FormatCollectionDuration(PlaylistDetailTracks.Sum(t => t.Duration.TotalSeconds))
        : string.Empty;

    internal static string FormatCollectionDuration(double totalSeconds)
    {
        int totalMinutes = Math.Max(0, (int)Math.Round(totalSeconds / 60.0));
        if (totalMinutes < 60)
        {
            return $"{Math.Max(1, totalMinutes)}m";
        }

        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
    }

    public static Visibility NonEmptyVisibility(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    public static double BoolToActiveOpacity(bool isActive) => isActive ? 1.0 : 0.0;
    public Visibility HasHomeRecentlyPlayedVisibility =>
        HomeRecentlyPlayedAlbums.Count > 0 || HomeRecentlyPlayed.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasHomeRelatedArtistsVisibility =>
        HomeRelatedArtists.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    // Filter active helpers for compiled x:Bind
    public bool IsFilterAll => SelectedCategory == SearchCategoryFilter.All;
    public bool IsFilterSongs => SelectedCategory == SearchCategoryFilter.Songs;
    public bool IsFilterAlbums => SelectedCategory == SearchCategoryFilter.Albums;
    public bool IsFilterArtists => SelectedCategory == SearchCategoryFilter.Artists;

    public bool IsSourceLocal => SelectedSourceType == MediaSourceType.Local;
    public bool IsSourceOnline => SelectedSourceType == MediaSourceType.Online;
    public bool IsSourceAll => SelectedSourceType == MediaSourceType.All;

    public bool IsQueueTabSelected => ActiveNowPlayingTab == NowPlayingTab.Queue;
    public bool IsLyricsTabSelected => ActiveNowPlayingTab == NowPlayingTab.Lyrics;

    public Visibility QueuePanelVisibility => ActiveNowPlayingTab == NowPlayingTab.Queue ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LyricsPanelVisibility => ActiveNowPlayingTab == NowPlayingTab.Lyrics ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the compact queue position/length badge text (e.g., "1 / 14 songs").
    /// Uses the authoritative audio engine queue count even when the UI list is capped.
    /// </summary>
    public string QueueCountSummary
    {
        get
        {
            int upcomingCount = _audioPlayerService.Queue?.Count ?? Queue.Count;
            return upcomingCount == 0 ? "0 songs" : $"1 / {upcomingCount + 1} songs";
        }
    }

    public Visibility IsSongsSectionVisible => SelectedCategory is SearchCategoryFilter.All or SearchCategoryFilter.Songs ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsAlbumsSectionVisible => SelectedCategory is SearchCategoryFilter.All or SearchCategoryFilter.Albums ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsArtistsSectionVisible => SelectedCategory is SearchCategoryFilter.All or SearchCategoryFilter.Artists ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyCatalogVisibility => Songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasSongsVisibility => Songs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasAlbumsVisibility => IsAlbumsSectionVisible == Visibility.Visible && Albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasArtistsVisibility => IsArtistsSectionVisible == Visibility.Visible && Artists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasRecommendedArtistsVisibility => IsArtistsSectionVisible == Visibility.Visible && RecommendedArtists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Android-aligned Library 2.0 tab visibilities, section counts, and A–Z index rail helpers
    public bool IsLibraryBusy => IsScanning || IsLibraryLoading;
    public Visibility LibraryLoadingRingVisibility => IsLibraryBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LibraryRecentlyAddedBackVisibility => IsLibraryRecentlyAddedExpanded ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LibraryOverviewVisibility =>
        !IsLibraryRecentlyAddedExpanded && SelectedCategory == SearchCategoryFilter.All
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility LibraryRecentlyAddedViewVisibility =>
        IsLibraryRecentlyAddedExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility LibraryArtistsDirectoryVisibility =>
        !IsLibraryRecentlyAddedExpanded && SelectedCategory == SearchCategoryFilter.Artists
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility LibraryAlbumsDirectoryVisibility =>
        !IsLibraryRecentlyAddedExpanded && SelectedCategory == SearchCategoryFilter.Albums
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility LibrarySongsDirectoryVisibility =>
        !IsLibraryRecentlyAddedExpanded && SelectedCategory == SearchCategoryFilter.Songs
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility LibraryAlphabetRailVisibility =>
        LibraryViewVisibility == Visibility.Visible &&
        !IsLibraryRecentlyAddedExpanded &&
        SelectedCategory is SearchCategoryFilter.Artists or SearchCategoryFilter.Albums or SearchCategoryFilter.Songs &&
        GetActiveLibraryAvailableLetters().Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasLibraryRecentlyAddedVisibility => LibraryRecentlyAddedPreview.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasLibraryOverviewArtistsVisibility => LibraryArtistsPreview.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasLibraryOverviewAlbumsVisibility => LibraryAlbumsPreview.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasLibraryOverviewSongsVisibility => LibrarySongsPreview.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LibraryOverviewEmptyVisibility =>
        Songs.Count == 0 && Albums.Count == 0 && Artists.Count == 0 && !IsLibraryLoading
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility LibraryEmptyImportActionsVisibility =>
        SelectedSourceType != MediaSourceType.Online ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LibraryLocalToolsBarVisibility =>
        SelectedSourceType != MediaSourceType.Online &&
        LibraryOverviewEmptyVisibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility LibraryArtistsEmptyVisibility => LibraryArtistGroups.Count == 0 && !IsLibraryLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LibraryAlbumsEmptyVisibility => LibraryAlbumGroups.Count == 0 && !IsLibraryLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LibrarySongsEmptyVisibility => LibrarySongGroups.Count == 0 && !IsLibraryLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LibraryRecentlyAddedEmptyVisibility => LibraryRecentlyAddedGroups.Count == 0 && !IsLibraryLoading ? Visibility.Visible : Visibility.Collapsed;

    public string LibraryRecentlyAddedMoreCardCountText => LibraryRecentlyAdded.Count > 10
        ? $"+{LibraryRecentlyAdded.Count - 10} more"
        : "Timeline";

    public string LibraryArtistsMoreLabel => Artists.Count > 10 ? $"More ({Artists.Count})" : $"{Artists.Count}";
    public Visibility LibraryArtistsMoreCardVisibility => Artists.Count > 10 ? Visibility.Visible : Visibility.Collapsed;
    public string LibraryArtistsMoreCardCountText => $"+{Math.Max(0, Artists.Count - 10)} more";

    public string LibraryAlbumsMoreLabel => Albums.Count > 10 ? $"See more ({Albums.Count})" : $"{Albums.Count}";
    public Visibility LibraryAlbumsMoreCardVisibility => Albums.Count > 10 ? Visibility.Visible : Visibility.Collapsed;
    public string LibraryAlbumsMoreCardCountText => $"+{Math.Max(0, Albums.Count - 10)} more";

    public string LibrarySongsMoreLabel => $"See all ({Songs.Count})";
    public string LibraryViewAllSongsButtonLabel => $"View all songs ({Songs.Count})";
    public Visibility LibraryViewAllSongsButtonVisibility => Songs.Count > 15 ? Visibility.Visible : Visibility.Collapsed;

    public string LibraryEmptyHeadline => SelectedSourceType switch
    {
        MediaSourceType.Online => "No online songs yet",
        MediaSourceType.All => "Your library is still empty",
        _ => "Your local library is empty"
    };

    public string LibraryEmptyBody => SelectedSourceType switch
    {
        MediaSourceType.Online => "Sign in to YouTube Music or search for your favorite artists to explore trending online tracks.",
        MediaSourceType.All => "Scan a local music folder or explore YouTube Music online to build your combined music library.",
        _ => "Scan a folder or import audio files (.mp3, .flac, .m4a, .wav, .ogg, .opus) to extract Title, Artist, Album, Cover Art & Synchronized Lyrics."
    };

    /// <summary>
    /// Returns the ordered available A–Z + '#' letters for the currently active Library alphabetical directory tab.
    /// </summary>
    public IReadOnlyList<string> GetActiveLibraryAvailableLetters()
    {
        return SelectedCategory switch
        {
            SearchCategoryFilter.Artists => LibraryArtistGroups.Select(g => g.Letter).ToList(),
            SearchCategoryFilter.Albums => LibraryAlbumGroups.Select(g => g.Letter).ToList(),
            SearchCategoryFilter.Songs => LibrarySongGroups.Select(g => g.Letter).ToList(),
            _ => []
        };
    }

    public Visibility SearchBoxVisibility => IsSearchExpanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SettingsStudioVisibility => IsSettingsStudioOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DraggableLensVisibility => IsDraggableLensVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TrackContextMenuVisibility => IsTrackContextMenuOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TrackInfoModalVisibility => IsTrackInfoModalOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the glyph for toggling the right-hand Now Playing sidebar panel.
    /// </summary>
    public string NowPlayingSidebarToggleGlyph => IsNarrowNowPlayingOverlayOpen
        ? "\uE72B"
        : (IsNowPlayingSidebarVisible ? "\uE8A0" : "\uE89F");

    /// <summary>
    /// Gets the visibility of the top header mini-player capsule.
    /// Only visible when the Now Playing right sidebar is hidden, freeing maximum header width for navigation.
    /// </summary>
    public Visibility MiniPlayerHeaderVisibility => (!IsNowPlayingSidebarVisible && !IsCompactLayout) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the dynamic title for the Library workspace header (matching Android LibraryScreen).
    /// </summary>
    public string LibraryHeaderTitle
    {
        get
        {
            if (IsLibraryRecentlyAddedExpanded)
            {
                return "Recently Added";
            }

            return SelectedSourceType switch
            {
                MediaSourceType.Online => "Online Music",
                MediaSourceType.All => "All Music",
                _ => "Library"
            };
        }
    }

    /// <summary>
    /// Gets the dynamic subtitle for the Library workspace header (matching Android LibraryScreen).
    /// </summary>
    public string LibraryHeaderSubtitle
    {
        get
        {
            if (IsLibraryRecentlyAddedExpanded)
            {
                string sourceLabel = SelectedSourceType switch
                {
                    MediaSourceType.Online => "YouTube Music",
                    MediaSourceType.All => "Local & YouTube Music",
                    _ => "Local Library"
                };
                return $"{LibraryRecentlyAdded.Count} Songs ordered chronologically • {sourceLabel}";
            }

            return SelectedSourceType switch
            {
                MediaSourceType.Online => $"Explore trending songs and artists on YouTube Music • {Songs.Count} Songs",
                MediaSourceType.All => $"Combined Local Library & YouTube Music • {Songs.Count} Songs",
                _ => $"{Artists.Count} Artists • {Albums.Count} Albums • {Songs.Count} Songs from your local music library"
            };
        }
    }

    public string ContextMenuLikeGlyph => ContextMenuTrack?.IsLiked == true ? "\uEB52" : "\uEB51";
    public string ContextMenuLikeLabel => ContextMenuTrack?.IsLiked == true ? "Liked" : "Like";
    public string ContextMenuGoToArtistLabel => $"Go to {(string.IsNullOrWhiteSpace(ContextMenuTrack?.Artist) ? "Artist" : ContextMenuTrack!.Artist)}";
    public string ContextMenuGoToAlbumLabel => $"Go to {(string.IsNullOrWhiteSpace(ContextMenuTrack?.Album) ? ContextMenuTrack?.Title ?? "Album" : ContextMenuTrack!.Album)}";
    public string ContextMenuTrackSourceDetail => ContextMenuTrack?.SourceType == MediaSourceType.Online
        ? $"YouTube Music Online Stream (Video ID: {ContextMenuTrack?.YouTubeVideoId})"
        : string.IsNullOrWhiteSpace(ContextMenuTrack?.AudioSourceUri)
            ? "Local Audio File"
            : ContextMenuTrack!.AudioSourceUri;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShellViewModel"/> class.
    /// </summary>
    public ShellViewModel(
        IMediaCatalogService catalogService,
        IAudioPlayerService audioPlayerService,
        IYouTubeMusicService youTubeMusicService,
        ILyricsProviderService lyricsProviderService,
        ICanvasArtworkService canvasArtworkService,
        IMusicDownloadService musicDownloadService,
        IThemeService themeService,
        ILocalizationService localizationService,
        INavigationService navigationService,
        IStorageService storageService,
        IDataUsageService dataUsageService,
        IAppUpdateService appUpdateService,
        WindowsInstallerLauncher installerLauncher,
        ILogger<ShellViewModel> logger)
    {
        _catalogService = catalogService;
        _audioPlayerService = audioPlayerService;
        _youTubeMusicService = youTubeMusicService;
        _lyricsProviderService = lyricsProviderService;
        _canvasArtworkService = canvasArtworkService;
        _musicDownloadService = musicDownloadService;
        _themeService = themeService;
        _localizationService = localizationService;
        _navigationService = navigationService;
        _storageService = storageService;
        _dataUsageService = dataUsageService;
        _appUpdateService = appUpdateService;
        _installerLauncher = installerLauncher;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _audioPlayerService.CurrentTrackChanged += OnAudioTrackChanged;
        _audioPlayerService.IsPlayingChanged += OnAudioIsPlayingChanged;
        _audioPlayerService.PositionChanged += OnAudioPositionChanged;
        _audioPlayerService.QueueChanged += OnAudioQueueChanged;
        _audioPlayerService.PlaybackErrorOccurred += OnAudioPlaybackErrorOccurred;
        _audioPlayerService.CurrentTrackPlaybackInfoChanged += OnCurrentTrackPlaybackInfoChanged;
        _dataUsageService.UsageChanged += OnDataUsageChanged;
        _dataUsageService.DataLimitReached += OnDataUsageLimitReached;
        _audioPlayerService.FallbackSimilarTracksProvider = BuildOfflineSimilarTracksFallback;
        RepeatMode = _audioPlayerService.RepeatMode;
        IsShuffleEnabled = _audioPlayerService.IsShuffleEnabled;
        _youTubeMusicService.AccountSessionChanged += OnYouTubeAccountSessionChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
        _themeService.ThemeChanged += OnThemeChanged;
        _themeService.AccentColorChanged += OnAccentColorChanged;
    }

    private void OnYouTubeAccountSessionChanged(object? sender, EventArgs e)
    {
        _dispatcherQueue?.TryEnqueue(SyncYouTubeMusicAccountStatus);
    }

    private void OnAudioPlaybackErrorOccurred(object? sender, string message)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            IsTrackLoading = false;
            StatusMessage = message;
            SyncYouTubeMusicAccountStatus();
        });
    }

    /// <summary>
    /// Initializes essential startup services, prepares a lightweight initial track without eagerly populating hidden tabs,
    /// and loads the Home discovery feed.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            await _localizationService.InitializeAsync();
            await _lyricsProviderService.InitializeAsync();
            await _musicDownloadService.InitializeAsync();
            await _catalogService.InitializeAsync();

            SyncDownloadOptionsFromService(_musicDownloadService.Options);
            SyncLyricsProvidersList();
            SyncGlassSlidersFromService(_themeService.LiquidGlass);
            IsDynamicCoverAccentEnabled = _themeService.IsDynamicCoverAccentEnabled;
            ActiveAccentColorHex = _themeService.ActiveAccentHex;
            IsLightMode = !_themeService.CurrentTheme.IsDark;
            ThemePreference = _themeService.ThemePreference;
            VolumePercent = Math.Round(_audioPlayerService.Volume * 100.0, 0);

            _isHydratingPlayerUiSettings = true;
            try
            {
                var playerUiSettings = await _storageService.LoadPlayerUiSettingsAsync();
                _persistedPlayerUiSettings = playerUiSettings;
                ShowFullscreenSeekButtons = playerUiSettings.ShowFullscreenSeekButtons;

                if (playerUiSettings.IsGpuVisualizerEnabled &&
                    !string.IsNullOrWhiteSpace(playerUiSettings.SelectedVisualizerId) &&
                    !string.Equals(playerUiSettings.SelectedVisualizerId, "BackdropOnly", StringComparison.OrdinalIgnoreCase))
                {
                    await EnsureVisualizersInitializedAsync();
                }
            }
            finally
            {
                _isHydratingPlayerUiSettings = false;
            }

            await HydrateUpdateSettingsAsync();
            await HydratePlaybackAndDataSettingsAsync();

            AvailableThemes.Clear();
            foreach (var theme in _themeService.AvailableThemes)
            {
                AvailableThemes.Add(theme);
            }

            CurrentFlowDirection = _localizationService.IsRightToLeft
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;

            // Always start on the Home screen per user requirement
            CurrentSection = NavigationSection.Home;
            ActiveSubView = WorkspaceSubView.MainSection;

            SyncLocalMusicDirectoriesList();
            await LoadYtmPromptDismissStateAsync();
            SyncYouTubeMusicAccountStatus();
            RepeatMode = _audioPlayerService.RepeatMode;
            IsShuffleEnabled = _audioPlayerService.IsShuffleEnabled;

            bool restoredSession = false;
            try
            {
                var savedSession = await _storageService.LoadAsync<PersistedPlaybackSessionState>(
                    PersistedPlaybackSessionState.StorageKey);

                if (savedSession is { QueueItems.Count: > 0 })
                {
                    var validQueue = new List<TrackItem>(savedSession.QueueItems.Count);
                    int adjustedIndex = -1;
                    for (int i = 0; i < savedSession.QueueItems.Count; i++)
                    {
                        var item = savedSession.QueueItems[i];
                        if (!IsRestorableSessionTrack(item))
                        {
                            continue;
                        }

                        var resolved = _catalogService.FindLocalOfflineEquivalent(item!) ?? item!;
                        if (i == savedSession.CurrentIndex)
                        {
                            adjustedIndex = validQueue.Count;
                        }
                        else if (adjustedIndex < 0 && i > savedSession.CurrentIndex)
                        {
                            adjustedIndex = validQueue.Count;
                        }

                        validQueue.Add(resolved);
                    }

                    if (validQueue.Count > 0)
                    {
                        var validCanonical = (savedSession.CanonicalOrder ?? [])
                            .Where(IsRestorableSessionTrack)
                            .Select(t => _catalogService.FindLocalOfflineEquivalent(t!) ?? t!)
                            .ToList();

                        var sanitizedSession = savedSession with
                        {
                            QueueItems = validQueue,
                            CanonicalOrder = validCanonical,
                            CurrentIndex = adjustedIndex >= 0 ? adjustedIndex : 0
                        };

                        if (_audioPlayerService.RestorePlaybackSession(sanitizedSession))
                        {
                            restoredSession = true;
                            VolumePercent = Math.Round(_audioPlayerService.Volume * 100.0, 0);
                            RepeatMode = _audioPlayerService.RepeatMode;
                            IsShuffleEnabled = _audioPlayerService.IsShuffleEnabled;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not restore prior playback session; falling back to default seed.");
            }

            if (!restoredSession)
            {
                // Lightweight initial track selection (zero Library/Liked/Playlists UI collection hydration at startup)
                var initialTracks = _catalogService.GetRecentlyPlayedAlbumsSnapshot()
                    .Select(a => a.SeedTrack)
                    .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Title))
                    .Take(31)
                    .Select(t => _catalogService.FindLocalOfflineEquivalent(t!) ?? t!)
                    .ToList();

                if (initialTracks.Count == 0)
                {
                    var likedSeed = await _catalogService.GetLikedSongsAsync();
                    if (likedSeed.Count > 0)
                    {
                        initialTracks = likedSeed
                            .Take(31)
                            .Select(t => _catalogService.FindLocalOfflineEquivalent(t) ?? t)
                            .ToList();
                    }
                }

                if (initialTracks.Count > 0)
                {
                    // Deferred idle queue setup: OnAudioTrackChanged handles cover accent + aurora palette ONCE
                    // without spinning up decoders, canvas, video, or lyrics until playback/lyrics/fullscreen is opened.
                    await _audioPlayerService.SetQueueAsync(initialTracks, startIndex: 0, autoPlay: false);
                }
                else
                {
                    if (IsDynamicCoverAccentEnabled)
                    {
                        await _themeService.ApplyAlbumCoverAccentAsync(CurrentTrack.SafeCoverArtUri);
                    }

                    _ = TriggerAuroraPaletteExtractionAsync(null);
                }
            }

            // Load personalized Home discovery feed in parallel and signal Splash Screen when ready
            _ = RefreshHomeDiscoveryFeedAsync().ContinueWith(
                _ => _initialHomeReadyTcs.TrySetResult(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // Perform non-blocking silent background update check on startup if enabled
            TriggerSilentStartupUpdateCheck();
        }
        catch
        {
            _initialHomeReadyTcs.TrySetResult();
            throw;
        }
    }

    private async Task EnsureLibraryHydratedAsync(CancellationToken cancellationToken = default)
    {
        bool needsHydration =
            !_isLibraryHydrated ||
            _isLibraryDirty ||
            _lastLoadedLibrarySource != SelectedSourceType ||
            !string.Equals(_lastLoadedLibraryQueryKey, string.Empty, StringComparison.Ordinal);

        if (!needsHydration)
        {
            SyncLibraryPlayingTrackState();
            NotifyLibraryComputedProperties();
            LibraryIndexChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        await RefreshCatalogAsync(cancellationToken);
    }

    private async Task EnsureLikedHydratedAsync(CancellationToken cancellationToken = default)
    {
        if (!_isLikedHydrated || _isLikedDirty)
        {
            await RefreshLikedSongsAsync(cancellationToken);
        }
        else
        {
            SyncLikedAndPlaylistPlayingTrackState();
            LikedIndexChanged?.Invoke(this, EventArgs.Empty);
        }

        if ((_isLikedRecsDirty || LikedRecommendations.Count == 0) && !IsLoadingLikedRecommendations)
        {
            _ = RefreshLikedRecommendationsAsync(forceRefresh: _isLikedRecsDirty);
        }
    }

    private async Task EnsurePlaylistsHydratedAsync(
        CancellationToken cancellationToken = default,
        bool includeSmartSuggestions = false)
    {
        if (!_isPlaylistsHydrated || _isPlaylistsDirty)
        {
            await RefreshPlaylistsAsync(cancellationToken, refreshSmartSuggestions: includeSmartSuggestions);
        }
        else if (includeSmartSuggestions && SmartSuggestedPlaylistCards.Count == 0)
        {
            await RefreshSmartSuggestedPlaylistsAsync(cancellationToken: cancellationToken);
        }
    }

    private async Task EnsureVisualizersInitializedAsync()
    {
        if (_isVisualizersInitialized)
        {
            return;
        }

        _isVisualizersInitialized = true;
        try
        {
            await InitializeVisualizersAsync();
        }
        catch
        {
            _isVisualizersInitialized = false;
            throw;
        }
    }

    partial void OnVolumePercentChanged(double value)
    {
        double clamped = Math.Clamp(value / 100.0, 0.0, 1.0);
        if (Math.Abs(_audioPlayerService.Volume - clamped) > 0.005)
        {
            _audioPlayerService.Volume = clamped;
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (_isSuppressingSearchQueryChange)
        {
            return;
        }

        var token = ReplaceCts(ref _searchCts).Token;

        string trimmed = value?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(trimmed) && !IsSearchPageOpen)
        {
            IsSearchPageOpen = true;
            IsSearchExpanded = true;
            SearchExpandChanged?.Invoke(this, true);
        }

        if (string.IsNullOrEmpty(trimmed))
        {
            IsSearchingCatalog = false;
            ClearSearchResultsCollections();
            NotifySearchComputedProperties();
            return;
        }

        IsSearchingCatalog = true;
        NotifySearchComputedProperties();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(280, token);
                if (!token.IsCancellationRequested)
                {
                    _dispatcherQueue.TryEnqueue(async () => await ExecuteDedicatedSearchAsync(trimmed, saveToHistory: false, token));
                }
            }
            catch (OperationCanceledException)
            {
                // Debounced
            }
        }, token);
    }

    partial void OnFrostBlurRadiusChanged(double value) => PushLiveGlassUpdate();
    partial void OnTurbulenceFrequencyChanged(double value) => PushLiveGlassUpdate();
    partial void OnDistortionStrengthChanged(double value) => PushLiveGlassUpdate();
    partial void OnTintOpacityPercentChanged(double value) => PushLiveGlassUpdate();
    partial void OnSpecularRimPercentChanged(double value) => PushLiveGlassUpdate();

    private void PushLiveGlassUpdate()
    {
        if (_isSuppressingGlassCallback)
        {
            return;
        }

        var updated = _themeService.LiquidGlass with
        {
            FrostBlurRadius = Math.Round(FrostBlurRadius, 1),
            TurbulenceFrequency = Math.Round(TurbulenceFrequency, 4),
            DistortionStrength = Math.Round(DistortionStrength, 1),
            TintOpacity = Math.Round(TintOpacityPercent / 100.0, 3),
            SpecularRimIntensity = Math.Round(SpecularRimPercent / 100.0, 3)
        };

        _ = _themeService.UpdateLiquidGlassParametersAsync(updated, persist: false);

        var persistToken = ReplaceCts(ref _glassPersistCts).Token;
        _ = PersistGlassParametersDebouncedAsync(updated, persistToken);
    }

    private async Task PersistGlassParametersDebouncedAsync(LiquidGlassParameters parameters, CancellationToken token)
    {
        try
        {
            await Task.Delay(350, token);
            if (!token.IsCancellationRequested)
            {
                await _themeService.UpdateLiquidGlassParametersAsync(parameters, persist: true);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer slider change.
        }
    }

    private void SyncGlassSlidersFromService(LiquidGlassParameters glass)
    {
        _isSuppressingGlassCallback = true;
        try
        {
            FrostBlurRadius = glass.FrostBlurRadius;
            TurbulenceFrequency = glass.TurbulenceFrequency;
            DistortionStrength = glass.DistortionStrength;
            TintOpacityPercent = Math.Round(glass.TintOpacity * 100.0, 0);
            SpecularRimPercent = Math.Round(glass.SpecularRimIntensity * 100.0, 0);
        }
        finally
        {
            _isSuppressingGlassCallback = false;
        }
    }

    partial void OnSelectedDownloadFormatChanged(DownloadAudioFormat value) => PushDownloadOptionsUpdate();
    partial void OnDownloadIncludeTitleAndArtistChanged(bool? value) => PushDownloadOptionsUpdate();
    partial void OnDownloadIncludeAlbumAndTrackNumberChanged(bool? value) => PushDownloadOptionsUpdate();
    partial void OnDownloadIncludeHighResCoverArtChanged(bool? value) => PushDownloadOptionsUpdate();
    partial void OnDownloadIncludeGenreAndReleaseYearChanged(bool? value) => PushDownloadOptionsUpdate();
    partial void OnDownloadIncludeIsrcAndCopyrightChanged(bool? value) => PushDownloadOptionsUpdate();
    partial void OnDownloadIncludeReplayGainChanged(bool? value) => PushDownloadOptionsUpdate();
    partial void OnDownloadIncludeSynchronizedLyricsChanged(bool? value) => PushDownloadOptionsUpdate();
    partial void OnDownloadIncludeStaticLyricsChanged(bool? value) => PushDownloadOptionsUpdate();
    partial void OnDownloadIncludeAppleCanvasOfflineChanged(bool? value) => PushDownloadOptionsUpdate();

    private void SyncDownloadOptionsFromService(DownloadMetadataOptions opts)
    {
        _isSuppressingDownloadOptionsSave = true;
        try
        {
            SelectedDownloadFormat = opts.DefaultFormat;
            DownloadTargetDirectory = opts.DownloadDirectory;
            DownloadIncludeTitleAndArtist = opts.IncludeTitleAndArtist;
            DownloadIncludeAlbumAndTrackNumber = opts.IncludeAlbumAndTrackNumber;
            DownloadIncludeHighResCoverArt = opts.IncludeHighResCoverArt;
            DownloadIncludeGenreAndReleaseYear = opts.IncludeGenreAndReleaseYear;
            DownloadIncludeIsrcAndCopyright = opts.IncludeIsrcAndCopyright;
            DownloadIncludeReplayGain = opts.IncludeReplayGain;
            DownloadIncludeSynchronizedLyrics = opts.IncludeSynchronizedLyrics;
            DownloadIncludeStaticLyrics = opts.IncludeStaticLyrics;
            DownloadIncludeAppleCanvasOffline = opts.IncludeAppleCanvasOffline;
        }
        finally
        {
            _isSuppressingDownloadOptionsSave = false;
        }
    }

    private void PushDownloadOptionsUpdate()
    {
        if (_isSuppressingDownloadOptionsSave)
        {
            return;
        }

        var updated = new DownloadMetadataOptions
        {
            DefaultFormat = SelectedDownloadFormat,
            DownloadDirectory = DownloadTargetDirectory,
            IncludeTitleAndArtist = DownloadIncludeTitleAndArtist ?? true,
            IncludeAlbumAndTrackNumber = DownloadIncludeAlbumAndTrackNumber ?? true,
            IncludeHighResCoverArt = DownloadIncludeHighResCoverArt ?? true,
            IncludeGenreAndReleaseYear = DownloadIncludeGenreAndReleaseYear ?? true,
            IncludeIsrcAndCopyright = DownloadIncludeIsrcAndCopyright ?? true,
            IncludeReplayGain = DownloadIncludeReplayGain ?? true,
            IncludeSynchronizedLyrics = DownloadIncludeSynchronizedLyrics ?? true,
            IncludeStaticLyrics = DownloadIncludeStaticLyrics ?? true,
            IncludeAppleCanvasOffline = DownloadIncludeAppleCanvasOffline ?? true
        };

        _ = _musicDownloadService.SaveOptionsAsync(updated);
    }

    [RelayCommand]
    private void SelectDownloadFormat(string formatName)
    {
        DownloadAudioFormat resolved = formatName?.Trim().ToUpperInvariant() switch
        {
            "FLAC" or "FLACLOSSLESS" => DownloadAudioFormat.FlacLossless,
            "MP3" or "MP3MAXQUALITY" => DownloadAudioFormat.Mp3MaxQuality,
            "M4A" or "M4ANATIVE" => DownloadAudioFormat.M4aNative,
            _ => DownloadAudioFormat.FlacLossless
        };

        SelectedDownloadFormat = resolved;
        StatusMessage = $"Default download format: {resolved}";
    }

    [RelayCommand]
    private async Task PickDownloadDirectoryAsync()
    {
        try
        {
            var folderPicker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };
            folderPicker.FileTypeFilter.Add("*");

            if (App.MainWindowInstance is Window mainWindow)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);
            }

            var pickedFolder = await folderPicker.PickSingleFolderAsync();
            if (pickedFolder is not null)
            {
                DownloadTargetDirectory = pickedFolder.Path;
                PushDownloadOptionsUpdate();
                StatusMessage = $"Download folder set to: {pickedFolder.Path}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not pick download folder.");
        }
    }

    [RelayCommand]
    private async Task SelectSectionAsync(string sectionName)
    {
        if (Enum.TryParse<NavigationSection>(sectionName, ignoreCase: true, out var section))
        {
            _returnToSearchAfterDetail = false;
            CloseSearchOverlayIfOpen(clearQuery: true);
            CloseDetailSubView();
            CurrentSection = section;
            _navigationService.NavigateTo(section);

            if (section == NavigationSection.Home)
            {
                if (HomeRecommendedTracks.Count == 0)
                {
                    await RefreshHomeDiscoveryFeedAsync();
                }
            }
            else if (section == NavigationSection.Liked)
            {
                await EnsureLikedHydratedAsync();
            }
            else if (section == NavigationSection.Playlists)
            {
                await EnsurePlaylistsHydratedAsync(includeSmartSuggestions: SmartSuggestedPlaylistCards.Count == 0);
            }
            else
            {
                await EnsureLibraryHydratedAsync();
            }
        }
    }

    [RelayCommand]
    private void NavigateBackFromDetail()
    {
        bool shouldReturnToSearch = _returnToSearchAfterDetail;
        _returnToSearchAfterDetail = false;
        CloseDetailSubView();

        if (shouldReturnToSearch)
        {
            IsSearchPageOpen = true;
            IsSearchExpanded = true;
            SearchExpandChanged?.Invoke(this, true);
        }
    }

    private void CloseDetailSubView()
    {
        ActiveSubView = WorkspaceSubView.MainSection;

        CancelAndDisposeCts(ref _artistDetailLoadCts);
        CancelAndDisposeCts(ref _albumDetailLoadCts);
        CancelAndDisposeCts(ref _albumCanvasLookupCts);
        CancelAndDisposeCts(ref _artistVideoLookupCts);
        CancelAndDisposeCts(ref _playlistRelatedLoadCts);

        CurrentAlbumDetailCanvasUri = null;
        AlbumDetailCanvasVideoChanged?.Invoke(this, null);

        CurrentArtistDetailVideoUri = null;
        ArtistDetailHeaderVideoChanged?.Invoke(this, null);

        IsArtistDetailLoading = false;
        IsArtistHeaderMediaLoading = false;
        IsAlbumDetailLoading = false;
        IsPlaylistRelatedLoading = false;

        ClearArtistDetailState();
        ClearAlbumDetailState();
        ClearPlaylistDetailState();

        FilteredNewReleasesGridItems.Clear();
        RadioStationsGridItems.Clear();
    }

    private void ClearArtistDetailState()
    {
        CurrentArtistDetail = new ArtistDetailProfile();
        ArtistTopTracks.Clear();
        ArtistCombinedAlbums.Clear();
        ArtistSinglesAndEps.Clear();
        ArtistPlaylists.Clear();
        ArtistSimilarArtists.Clear();
        ArtistOfficialVideos.Clear();
        NotifyArtistSectionsVisibilityChanged();
    }

    private void ClearAlbumDetailState()
    {
        CurrentAlbumDetail = new AlbumDetailProfile();
        AlbumDetailTracks.Clear();
        AlbumOtherVersions.Clear();
        AlbumOtherAlbums.Clear();
        NotifyAlbumDetailPresentationStateChanged();
    }

    private void ClearPlaylistDetailState()
    {
        var emptyPlaylist = new UserPlaylist();
        CurrentPlaylistDetail = emptyPlaylist;
        CurrentPlaylistCard = new PlaylistCardViewModel(emptyPlaylist);
        PlaylistDetailTracks.Clear();
        PlaylistDetailTrackRows.Clear();
        PlaylistRelatedTracks.Clear();
        OnPropertyChanged(nameof(EmptyPlaylistDetailTracksVisibility));
        OnPropertyChanged(nameof(HasPlaylistDetailTracksVisibility));
        OnPropertyChanged(nameof(PlaylistDetailTrackCountText));
        OnPropertyChanged(nameof(PlaylistDetailDurationText));
        OnPropertyChanged(nameof(PlaylistRelatedSkeletonVisibility));
        OnPropertyChanged(nameof(HasPlaylistRelatedVisibility));
        OnPropertyChanged(nameof(PlaylistRelatedSectionVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistSaveToLibraryVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistSavedBadgeVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistDeleteButtonVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistTypeAccentLabel));
        OnPropertyChanged(nameof(CurrentPlaylistYouTubeBadgeVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistRefreshButtonVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistDeleteButtonTooltip));
    }

    [RelayCommand]
    private async Task RefreshHomeFeedAsync()
    {
        await RefreshHomeDiscoveryFeedAsync(SelectedHomeGenre);
    }

    /// <summary>
    /// Selects a genre filter pill on the Home screen and updates ONLY <see cref="HomeGenreTracks"/>
    /// without tearing down and rebuilding all other Home discovery collections.
    /// </summary>
    public async Task SelectHomeGenreAsync(string? genreTag)
    {
        if (string.IsNullOrWhiteSpace(genreTag))
        {
            return;
        }

        string trimmedGenre = genreTag.Trim();
        if (string.Equals(SelectedHomeGenre, trimmedGenre, StringComparison.OrdinalIgnoreCase) &&
            HomeGenreTracks.Count > 0)
        {
            return;
        }

        SelectedHomeGenre = trimmedGenre;
        var token = ReplaceCts(ref _homeGenreLoadCts).Token;

        try
        {
            var genreCatalog = await _youTubeMusicService.SearchCatalogAsync($"{trimmedGenre} hits songs", token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var rawTracks = genreCatalog.Songs
                .Take(12)
                .Select(t => _catalogService.FindLocalOfflineEquivalent(t) ?? t)
                .ToList();

            var warmed = await WarmTrackCoversAsync(rawTracks, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (_isLikedHydrated)
            {
                SyncTracksLikedStateAgainstLikedSongs(warmed);
            }

            HomeGenreTracks.Clear();
            foreach (var gTrack in warmed)
            {
                HomeGenreTracks.Add(gTrack);
            }

            _homeGenreSnapshot = [.. warmed];
            SyncTrackCollectionPlayingState(HomeGenreTracks);
        }
        catch (OperationCanceledException)
        {
            // Superseded by another genre selection
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update Home genre tracks for '{Genre}'.", trimmedGenre);
        }
    }

    /// <summary>
    /// Opens the dedicated Artist Detail screen for an <see cref="ArtistItem"/>.
    /// </summary>
    public async Task OpenArtistDetailAsync(ArtistItem? artist)
    {
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return;
        }

        await OpenArtistByNameAsync(artist.Name, artist.AvatarUri, artist.ExtractChannelBrowseId());
    }

    /// <summary>
    /// Opens the dedicated Artist Detail screen by artist name (e.g., clicking the artist name in Now Playing or Album Detail).
    /// </summary>
    public async Task OpenArtistByNameAsync(string? artistName, string? existingAvatarUri = null, string? explicitBrowseId = null)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return;
        }

        if (IsSearchPageOpen)
        {
            _returnToSearchAfterDetail = true;
            CloseSearchOverlayIfOpen(clearQuery: false);
        }

        // If artist name contains multiple artists separated by commas, take the primary artist
        string primaryArtist = artistName.Split([',', '&', ';'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (string.IsNullOrWhiteSpace(primaryArtist))
        {
            primaryArtist = artistName.Trim();
        }

        CancelAndDisposeCts(ref _albumDetailLoadCts);
        CancelAndDisposeCts(ref _albumCanvasLookupCts);
        CurrentAlbumDetailCanvasUri = null;
        AlbumDetailCanvasVideoChanged?.Invoke(this, null);

        CancelAndDisposeCts(ref _artistVideoLookupCts);
        CurrentArtistDetailVideoUri = null;

        CancelAndDisposeCts(ref _playlistRelatedLoadCts);
        IsPlaylistRelatedLoading = false;
        ClearAlbumDetailState();
        ClearPlaylistDetailState();

        var loadToken = ReplaceCts(ref _artistDetailLoadCts).Token;

        var cachedAssets = _canvasArtworkService.PeekArtistAssets(primaryArtist);
        string initialBanner = !string.IsNullOrWhiteSpace(cachedAssets?.BannerUri)
            ? cachedAssets!.BannerUri!
            : (existingAvatarUri ?? string.Empty);

        IsArtistBioExpanded = false;
        ArtistTopTracks.Clear();
        ArtistCombinedAlbums.Clear();
        ArtistSinglesAndEps.Clear();
        ArtistPlaylists.Clear();
        ArtistSimilarArtists.Clear();
        ArtistOfficialVideos.Clear();

        CurrentArtistDetail = new ArtistDetailProfile
        {
            Name = !string.IsNullOrWhiteSpace(cachedAssets?.ArtistName) ? cachedAssets!.ArtistName : primaryArtist,
            AvatarUri = existingAvatarUri ?? string.Empty,
            BannerImageUri = initialBanner,
            CustomTitleLogoUri = cachedAssets?.WordmarkUri ?? string.Empty,
            HeaderVideoUri = cachedAssets?.HeaderVideoUri ?? string.Empty,
            KeyColorHex = cachedAssets?.BgColorHex ?? cachedAssets?.KeyColorHex ?? string.Empty,
            Biography = string.Empty,
            MetadataSubtitle = string.Empty
        };

        IsArtistHeaderMediaLoading = cachedAssets is null;
        IsArtistDetailLoading = true;
        NotifyArtistSectionsVisibilityChanged();

        ActiveSubView = WorkspaceSubView.ArtistDetail;
        ArtistDetailHeaderVideoChanged?.Invoke(this, $"__PREPARE__:{primaryArtist}");
        ArtistAmbientSourceChanged?.Invoke(this, (CurrentArtistDetail.HeroImageUri, cachedAssets?.BgColorHex));

        if (!string.IsNullOrWhiteSpace(cachedAssets?.HeaderVideoUri))
        {
            CurrentArtistDetailVideoUri = cachedAssets.HeaderVideoUri;
            ArtistDetailHeaderVideoChanged?.Invoke(this, cachedAssets.HeaderVideoUri);
        }

        IsLoadingDiscovery = true;

        try
        {
            // Fire Apple Music AMP API (wordmark + progressive video + banner) and YouTube Music catalog scrape concurrently
            var appleAssetsTask = Task.Run(
                () => _canvasArtworkService.GetArtistAssetsAsync(primaryArtist, loadToken),
                loadToken);
            var catalogTask = _catalogService.GetArtistDetailAsync(primaryArtist, existingAvatarUri, loadToken, explicitBrowseId);

            // Push Apple Music wordmark & progressive video the instant Apple Music resolves (~150-300ms), without waiting for YTM!
            _ = appleAssetsTask.ContinueWith(t =>
            {
                if (t.IsCanceled || t.IsFaulted || loadToken.IsCancellationRequested)
                {
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        if (!loadToken.IsCancellationRequested && ActiveSubView == WorkspaceSubView.ArtistDetail)
                        {
                            IsArtistHeaderMediaLoading = false;
                        }
                    });
                    return;
                }

                var assets = t.Result;
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (loadToken.IsCancellationRequested || ActiveSubView != WorkspaceSubView.ArtistDetail)
                    {
                        return;
                    }

                    IsArtistHeaderMediaLoading = false;
                    if (assets is null)
                    {
                        return;
                    }

                    bool bannerUpdated = string.IsNullOrWhiteSpace(CurrentArtistDetail.BannerImageUri) &&
                                         !string.IsNullOrWhiteSpace(assets.BannerUri);
                    string nextWordmark = !string.IsNullOrWhiteSpace(assets.WordmarkUri)
                        ? assets.WordmarkUri!
                        : CurrentArtistDetail.CustomTitleLogoUri;
                    string nextBanner = !string.IsNullOrWhiteSpace(CurrentArtistDetail.BannerImageUri)
                        ? CurrentArtistDetail.BannerImageUri
                        : (assets.BannerUri ?? CurrentArtistDetail.AvatarUri);
                    string nextHex = !string.IsNullOrWhiteSpace(assets.BgColorHex)
                        ? assets.BgColorHex!
                        : (assets.KeyColorHex ?? CurrentArtistDetail.KeyColorHex);

                    CurrentArtistDetail = new ArtistDetailProfile
                    {
                        Name = CurrentArtistDetail.Name,
                        AvatarUri = !string.IsNullOrWhiteSpace(CurrentArtistDetail.AvatarUri)
                            ? CurrentArtistDetail.AvatarUri
                            : (assets.BannerUri ?? string.Empty),
                        BannerImageUri = nextBanner,
                        CustomTitleLogoUri = nextWordmark,
                        HeaderVideoUri = assets.HeaderVideoUri ?? CurrentArtistDetail.HeaderVideoUri,
                        KeyColorHex = nextHex,
                        SubscribersCountText = CurrentArtistDetail.SubscribersCountText,
                        MonthlyListenersText = CurrentArtistDetail.MonthlyListenersText,
                        Biography = CurrentArtistDetail.Biography,
                        MetadataSubtitle = CurrentArtistDetail.MetadataSubtitle,
                        TopTracks = CurrentArtistDetail.TopTracks,
                        CombinedAlbums = CurrentArtistDetail.CombinedAlbums,
                        SinglesAndEps = CurrentArtistDetail.SinglesAndEps,
                        ArtistPlaylists = CurrentArtistDetail.ArtistPlaylists,
                        SimilarArtists = CurrentArtistDetail.SimilarArtists,
                        LatestRelease = CurrentArtistDetail.LatestRelease,
                        LatestReleaseTypeBadge = CurrentArtistDetail.LatestReleaseTypeBadge,
                        LatestReleaseDateText = CurrentArtistDetail.LatestReleaseDateText,
                        OfficialVideos = CurrentArtistDetail.OfficialVideos
                    };

                    if (bannerUpdated || !string.IsNullOrWhiteSpace(nextHex))
                    {
                        ArtistAmbientSourceChanged?.Invoke(this, (CurrentArtistDetail.HeroImageUri, nextHex));
                    }

                    if (!string.IsNullOrWhiteSpace(assets.HeaderVideoUri) &&
                        !string.Equals(CurrentArtistDetailVideoUri, assets.HeaderVideoUri, StringComparison.OrdinalIgnoreCase))
                    {
                        CurrentArtistDetailVideoUri = assets.HeaderVideoUri;
                        ArtistDetailHeaderVideoChanged?.Invoke(this, assets.HeaderVideoUri);
                    }
                });
            }, TaskScheduler.Default);

            var detail = await catalogTask;
            if (loadToken.IsCancellationRequested || ActiveSubView != WorkspaceSubView.ArtistDetail)
            {
                return;
            }

            // Ensure any already-resolved Apple Music wordmark/video is preserved if YTM completed simultaneously
            var settledAssets = appleAssetsTask.IsCompletedSuccessfully ? appleAssetsTask.Result : cachedAssets;
            if (settledAssets is not null)
            {
                detail = new ArtistDetailProfile
                {
                    Name = detail.Name,
                    AvatarUri = detail.AvatarUri,
                    BannerImageUri = detail.HasBanner ? detail.BannerImageUri : (settledAssets.BannerUri ?? detail.AvatarUri),
                    CustomTitleLogoUri = !string.IsNullOrWhiteSpace(detail.CustomTitleLogoUri)
                        ? detail.CustomTitleLogoUri
                        : (settledAssets.WordmarkUri ?? string.Empty),
                    HeaderVideoUri = !string.IsNullOrWhiteSpace(detail.HeaderVideoUri)
                        ? detail.HeaderVideoUri
                        : (settledAssets.HeaderVideoUri ?? string.Empty),
                    KeyColorHex = !string.IsNullOrWhiteSpace(detail.KeyColorHex)
                        ? detail.KeyColorHex
                        : (settledAssets.BgColorHex ?? settledAssets.KeyColorHex ?? string.Empty),
                    SubscribersCountText = detail.SubscribersCountText,
                    MonthlyListenersText = detail.MonthlyListenersText,
                    Biography = detail.Biography,
                    MetadataSubtitle = detail.MetadataSubtitle,
                    TopTracks = detail.TopTracks,
                    CombinedAlbums = detail.CombinedAlbums,
                    SinglesAndEps = detail.SinglesAndEps,
                    ArtistPlaylists = detail.ArtistPlaylists,
                    SimilarArtists = detail.SimilarArtists,
                    LatestRelease = detail.LatestRelease,
                    LatestReleaseTypeBadge = detail.LatestReleaseTypeBadge,
                    LatestReleaseDateText = detail.LatestReleaseDateText,
                    OfficialVideos = detail.OfficialVideos
                };
            }

            CurrentArtistDetail = detail;

            ArtistOfficialVideos.Clear();
            foreach (var vid in detail.OfficialVideos)
            {
                ArtistOfficialVideos.Add(vid);
            }

            ArtistTopTracks.Clear();
            foreach (var track in detail.TopTracks)
            {
                ArtistTopTracks.Add(track);
            }

            ArtistCombinedAlbums.Clear();
            foreach (var album in detail.CombinedAlbums)
            {
                ArtistCombinedAlbums.Add(album);
            }

            ArtistSinglesAndEps.Clear();
            foreach (var single in detail.SinglesAndEps)
            {
                ArtistSinglesAndEps.Add(single);
            }

            ArtistPlaylists.Clear();
            foreach (var pl in detail.ArtistPlaylists)
            {
                ArtistPlaylists.Add(pl);
            }

            ArtistSimilarArtists.Clear();
            foreach (var sim in detail.SimilarArtists)
            {
                ArtistSimilarArtists.Add(sim);
            }

            IsArtistDetailLoading = false;
            NotifyArtistSectionsVisibilityChanged();
            ArtistAmbientSourceChanged?.Invoke(this, (detail.HeroImageUri, detail.KeyColorHex));

            if (!string.IsNullOrWhiteSpace(detail.HeaderVideoUri) &&
                !string.Equals(CurrentArtistDetailVideoUri, detail.HeaderVideoUri, StringComparison.OrdinalIgnoreCase))
            {
                CurrentArtistDetailVideoUri = detail.HeaderVideoUri;
                ArtistDetailHeaderVideoChanged?.Invoke(this, detail.HeaderVideoUri);
            }
            else if (string.IsNullOrWhiteSpace(CurrentArtistDetailVideoUri))
            {
                _ = TriggerArtistDetailHeaderVideoLookupAsync(detail.Name);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by another navigation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Artist Detail for {Artist}", primaryArtist);
        }
        finally
        {
            if (!loadToken.IsCancellationRequested)
            {
                IsArtistDetailLoading = false;
                IsArtistHeaderMediaLoading = false;
                IsLoadingDiscovery = false;
                NotifyArtistSectionsVisibilityChanged();
            }
        }
    }

    private async Task TriggerArtistDetailHeaderVideoLookupAsync(string artistName)
    {
        var token = ReplaceCts(ref _artistVideoLookupCts).Token;

        try
        {
            string? videoUri = await Task.Run(
                () => _canvasArtworkService.GetArtistHeaderVideoUriAsync(artistName, token),
                token);

            if (token.IsCancellationRequested || ActiveSubView != WorkspaceSubView.ArtistDetail)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(videoUri))
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (!token.IsCancellationRequested && ActiveSubView == WorkspaceSubView.ArtistDetail)
                    {
                        CurrentArtistDetailVideoUri = videoUri;
                        ArtistDetailHeaderVideoChanged?.Invoke(this, videoUri);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled when user navigated away
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Artist header motion video lookup skipped for {Artist}.", artistName);
        }
    }

    private void NotifyArtistSectionsVisibilityChanged()
    {
        OnPropertyChanged(nameof(HasArtistLatestReleaseVisibility));
        OnPropertyChanged(nameof(HasArtistOfficialVideosVisibility));
        OnPropertyChanged(nameof(HasArtistAlbumsVisibility));
        OnPropertyChanged(nameof(HasArtistSinglesVisibility));
        OnPropertyChanged(nameof(HasArtistPlaylistsVisibility));
        OnPropertyChanged(nameof(HasArtistSimilarVisibility));
        NotifyArtistDetailPresentationStateChanged();
    }

    [RelayCommand]
    private async Task OpenCurrentTrackArtistAsync()
    {
        if (CurrentTrack is null || CurrentTrack.Id == "empty_state_track")
        {
            return;
        }

        if (IsFullscreenNowPlayingOpen)
        {
            CloseFullscreenNowPlaying();
        }

        await OpenArtistByNameAsync(CurrentTrack.Artist);
    }

    [RelayCommand]
    private async Task OpenAlbumDetailArtistAsync()
    {
        if (CurrentAlbumDetail is not null && !string.IsNullOrWhiteSpace(CurrentAlbumDetail.Artist))
        {
            await OpenArtistByNameAsync(CurrentAlbumDetail.Artist);
        }
    }

    [RelayCommand]
    private async Task PlayArtistTopTracksAsync()
    {
        if (ArtistTopTracks.Count == 0)
        {
            return;
        }

        await PlayTrackFromListAsync(ArtistTopTracks[0], ArtistTopTracks.ToList());
    }

    [RelayCommand]
    private async Task ShuffleArtistTopTracksAsync()
    {
        if (ArtistTopTracks.Count == 0)
        {
            return;
        }

        await SetShuffledQueueAndPlayAsync(ArtistTopTracks.ToList());
    }

    /// <summary>
    /// Opens the dedicated Album Detail screen, loads its ordered tracklist, Other Versions, and More by Artist
    /// directly from YouTube Music, and resolves faithful Apple Music Canvas animated cover artwork.
    /// </summary>
    public async Task OpenAlbumDetailAsync(AlbumItem? album)
    {
        if (album is null)
        {
            return;
        }

        if (IsSearchPageOpen)
        {
            _returnToSearchAfterDetail = true;
            CloseSearchOverlayIfOpen(clearQuery: false);
        }

        CancelAndDisposeCts(ref _artistDetailLoadCts);
        CancelAndDisposeCts(ref _playlistRelatedLoadCts);
        IsPlaylistRelatedLoading = false;
        ClearArtistDetailState();
        ClearPlaylistDetailState();

        var loadToken = ReplaceCts(ref _albumDetailLoadCts).Token;

        CancelAndDisposeCts(ref _albumCanvasLookupCts);
        CurrentAlbumDetailCanvasUri = null;
        AlbumDetailCanvasVideoChanged?.Invoke(this, null);

        CancelAndDisposeCts(ref _artistVideoLookupCts);
        CurrentArtistDetailVideoUri = null;
        ArtistDetailHeaderVideoChanged?.Invoke(this, null);

        IsAlbumDescriptionExpanded = false;

        CurrentAlbumDetail = new AlbumDetailProfile
        {
            Id = album.Id,
            Title = album.Title,
            Artist = album.Artist,
            CoverArtUri = album.CoverArtUri,
            ReleaseYear = album.ReleaseYear
        };
        AlbumDetailTracks.Clear();
        AlbumOtherVersions.Clear();
        AlbumOtherAlbums.Clear();

        IsAlbumDetailLoading = true;
        NotifyAlbumDetailPresentationStateChanged();

        ActiveSubView = WorkspaceSubView.AlbumDetail;
        AlbumAmbientSourceChanged?.Invoke(this, CurrentAlbumDetail.SafeCoverArtUri);

        IsLoadingDiscovery = true;

        try
        {
            var detail = await _catalogService.GetAlbumDetailAsync(album, loadToken);
            if (loadToken.IsCancellationRequested || ActiveSubView != WorkspaceSubView.AlbumDetail)
            {
                return;
            }

            CurrentAlbumDetail = detail;

            AlbumDetailTracks.Clear();
            foreach (var track in detail.Tracks)
            {
                AlbumDetailTracks.Add(track);
            }

            SyncAlbumDetailPlayingTrackState();

            AlbumOtherVersions.Clear();
            foreach (var ver in detail.OtherVersions)
            {
                AlbumOtherVersions.Add(ver);
            }

            AlbumOtherAlbums.Clear();
            foreach (var recAlbum in detail.OtherAlbums)
            {
                AlbumOtherAlbums.Add(recAlbum);
            }

            IsAlbumDetailLoading = false;
            NotifyAlbumDetailPresentationStateChanged();
            AlbumAmbientSourceChanged?.Invoke(this, detail.SafeCoverArtUri);

            // Trigger Apple Music Canvas lookup strictly for this album's real title
            _ = TriggerAlbumDetailCanvasLookupAsync(detail);
        }
        catch (OperationCanceledException)
        {
            // Superseded by another navigation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Album Detail for {Album}", album.Title);
        }
        finally
        {
            if (!loadToken.IsCancellationRequested)
            {
                IsAlbumDetailLoading = false;
                IsLoadingDiscovery = false;
                NotifyAlbumDetailPresentationStateChanged();
            }
        }
    }

    private void SyncAlbumDetailPlayingTrackState()
    {
        SyncTrackCollectionPlayingState(AlbumDetailTracks);
    }

    private void SyncHomePlayingTrackState()
    {
        SyncTrackCollectionPlayingState(HomeRecommendedTracks);
        SyncTrackCollectionPlayingState(HomeGenreTracks);
    }

    private void SyncLibraryPlayingTrackState()
    {
        SyncTrackCollectionPlayingState(Songs);
        SyncTrackCollectionPlayingState(LibraryRecentlyAdded);
        SyncTrackCollectionPlayingState(LibraryRecentlyAddedPreview);
        SyncTrackCollectionPlayingState(LibrarySongsPreview);
        SyncTrackCollectionPlayingState(LibraryOrderedSongs);

        foreach (var group in LibrarySongGroups)
        {
            SyncTrackCollectionPlayingState(group.Items);
        }

        foreach (var group in LibraryRecentlyAddedGroups)
        {
            SyncTrackCollectionPlayingState(group.Items);
        }
    }

    private void SyncLikedAndPlaylistPlayingTrackState()
    {
        SyncTrackCollectionPlayingState(LikedSongs);
        SyncTrackCollectionPlayingState(LikedOrderedSongs);
        foreach (var group in LikedSongGroups)
        {
            SyncTrackCollectionPlayingState(group.Items);
        }

        SyncTrackCollectionPlayingState(LikedRecommendations);
        SyncTrackCollectionPlayingState(PlaylistDetailTracks);
        SyncTrackCollectionPlayingState(PlaylistRelatedTracks);
    }

    private void SyncSearchPlayingTrackState()
    {
        SyncTrackCollectionPlayingState(SearchResultSongs);
        SyncTrackCollectionPlayingState(MoodDetailSongs);
    }

    private void SyncTrackCollectionPlayingState(IEnumerable<TrackItem> tracks)
    {
        var current = CurrentTrack;
        bool hasCurrent = current is not null && !string.Equals(current.Id, "empty_state_track", StringComparison.Ordinal);

        foreach (var item in tracks)
        {
            if (!hasCurrent)
            {
                item.IsCurrentlyPlaying = false;
                continue;
            }

            bool isMatch =
                string.Equals(item.Id, current!.Id, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(item.YouTubeVideoId) &&
                 string.Equals(item.YouTubeVideoId, current.YouTubeVideoId, StringComparison.Ordinal)) ||
                (string.Equals(item.Title, current.Title, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(item.Artist, current.Artist, StringComparison.OrdinalIgnoreCase));

            item.IsCurrentlyPlaying = isMatch;
        }
    }

    /// <summary>
    /// Opens the Album Detail screen for a recently played mosaic tile (or plays its seed track if it has no album metadata).
    /// </summary>
    public async Task OpenRecentAlbumAsync(RecentAlbumItem? recentAlbum)
    {
        if (recentAlbum is null)
        {
            return;
        }

        if (recentAlbum.IsSingleRelease &&
            string.IsNullOrWhiteSpace(recentAlbum.BrowseId) &&
            string.IsNullOrWhiteSpace(recentAlbum.AlbumTitle) &&
            recentAlbum.SeedTrack is not null)
        {
            await PlaySingleTrackAsync(recentAlbum.SeedTrack);
            return;
        }

        await OpenAlbumDetailAsync(recentAlbum.ToAlbumItem());
    }

    private Task TriggerAlbumDetailCanvasLookupAsync(AlbumDetailProfile albumDetail) =>
        TriggerAlbumDetailCanvasLookupCoreAsync(albumDetail);

    [RelayCommand]
    private async Task PlayAlbumTracksAsync()
    {
        if (AlbumDetailTracks.Count == 0)
        {
            return;
        }

        await PlayTrackFromListAsync(AlbumDetailTracks[0], AlbumDetailTracks.ToList());
    }

    [RelayCommand]
    private async Task ShuffleAlbumTracksAsync()
    {
        if (AlbumDetailTracks.Count == 0)
        {
            return;
        }

        await SetShuffledQueueAndPlayAsync(AlbumDetailTracks.ToList());
    }

    private void ApplyOptimisticTrackSelection(TrackItem track)
    {
        if (track is null || string.Equals(track.Id, "empty_state_track", StringComparison.Ordinal))
        {
            return;
        }

        _hasUserInitiatedPlayback = true;
        _deferredPlaybackStartTrackId = null;

        if (!ReferenceEquals(CurrentTrack, track) &&
            !string.Equals(CurrentTrack?.Id, track.Id, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _positionSyncGeneration);
            PrepareCanvasForIncomingTrackSelection(track);
            CurrentTrack = track;
            TotalDurationSeconds = Math.Max(1.0, track.Duration.TotalSeconds);
            _lastFormattedDurationSecond = Math.Max(0, (int)track.Duration.TotalSeconds);
            FormattedDuration = track.FormattedDuration;
            CurrentPositionSeconds = 0;
            _lastFormattedPositionSecond = 0;
            FormattedPosition = "0:00";
            BeginTrackLyricsPipeline(track);
            SyncAlbumDetailPlayingTrackState();
            SyncHomePlayingTrackState();
            SyncLibraryPlayingTrackState();
            SyncLikedAndPlaylistPlayingTrackState();
            SyncSearchPlayingTrackState();
        }
    }

    /// <summary>
    /// Replaces the playback queue with a single seed track (preferring any local offline equivalent)
    /// and immediately triggers progressive similar-track prefetching.
    /// </summary>
    public async Task PlaySingleTrackAsync(TrackItem? track)
    {
        if (track is null || string.Equals(track.Id, "empty_state_track", StringComparison.Ordinal))
        {
            return;
        }

        var effectiveTrack = _catalogService.FindLocalOfflineEquivalent(track) ?? track;
        ApplyOptimisticTrackSelection(effectiveTrack);

        int loadGen = ++_trackLoadGeneration;
        IsTrackLoading = true;
        try
        {
            await _audioPlayerService.PlaySingleTrackAsync(effectiveTrack, autoPlay: true);
        }
        finally
        {
            if (loadGen == _trackLoadGeneration)
            {
                IsTrackLoading = false;
            }
        }
    }

    private async Task SetQueueAndPlayAsync(IReadOnlyList<TrackItem> tracks, int startIndex = 0, bool autoPlay = true)
    {
        if (tracks is null || tracks.Count == 0)
        {
            return;
        }

        var orderedList = new List<TrackItem>(tracks.Count);
        foreach (var item in tracks)
        {
            if (item is not null)
            {
                orderedList.Add(item);
            }
        }

        if (orderedList.Count == 0)
        {
            return;
        }

        int clampedStart = Math.Clamp(startIndex, 0, orderedList.Count - 1);
        orderedList[clampedStart] = _catalogService.FindLocalOfflineEquivalent(orderedList[clampedStart]) ?? orderedList[clampedStart];
        if (autoPlay)
        {
            ApplyOptimisticTrackSelection(orderedList[clampedStart]);
        }

        int loadGen = ++_trackLoadGeneration;
        if (autoPlay)
        {
            IsTrackLoading = true;
        }

        try
        {
            await _audioPlayerService.SetQueueAsync(orderedList, clampedStart, autoPlay: autoPlay);
        }
        finally
        {
            if (autoPlay && loadGen == _trackLoadGeneration)
            {
                IsTrackLoading = false;
            }
        }
    }

    private async Task SetShuffledQueueAndPlayAsync(IReadOnlyList<TrackItem> canonicalTracks)
    {
        if (canonicalTracks is null || canonicalTracks.Count == 0)
        {
            return;
        }

        var orderedList = new List<TrackItem>(canonicalTracks.Count);
        foreach (var item in canonicalTracks)
        {
            if (item is not null)
            {
                orderedList.Add(_catalogService.FindLocalOfflineEquivalent(item) ?? item);
            }
        }

        if (orderedList.Count == 0)
        {
            return;
        }

        _hasUserInitiatedPlayback = true;
        IsShuffleEnabled = true;

        int loadGen = ++_trackLoadGeneration;
        IsTrackLoading = true;
        try
        {
            await _audioPlayerService.SetShuffledQueueAsync(orderedList, startCanonicalIndex: null, autoPlay: true);
            TriggerPlaybackSessionPersist(forceImmediate: true);
        }
        finally
        {
            if (loadGen == _trackLoadGeneration)
            {
                IsTrackLoading = false;
            }
        }
    }

    /// <summary>
    /// Plays a track from an ordered collection (Album tracklist, Artist Top Songs, Playlist, Liked Songs,
    /// Library, Mood Detail, or Home song shelf) preserving exact order without wrap-around so earlier tracks
    /// become session history and remaining tracks form the upcoming queue (matching Android <c>playQueue</c>).
    /// Falls back to <see cref="PlaySingleTrackAsync"/> when <paramref name="sourceList"/> is empty or does not contain <paramref name="track"/>.
    /// </summary>
    public async Task PlayTrackFromListAsync(TrackItem? track, IReadOnlyList<TrackItem>? sourceList)
    {
        if (track is null || string.Equals(track.Id, "empty_state_track", StringComparison.Ordinal))
        {
            return;
        }

        // Offline Priority: Resolve local offline equivalent for the clicked track immediately (O(1) instead of scanning all N tracks on the UI thread)
        var effectiveTrack = _catalogService.FindLocalOfflineEquivalent(track) ?? track;

        if (sourceList is null || sourceList.Count == 0)
        {
            await PlaySingleTrackAsync(effectiveTrack);
            return;
        }

        int startIndex = -1;
        for (int i = 0; i < sourceList.Count; i++)
        {
            if (ReferenceEquals(sourceList[i], track))
            {
                startIndex = i;
                break;
            }
        }

        if (startIndex < 0)
        {
            for (int i = 0; i < sourceList.Count; i++)
            {
                var candidate = sourceList[i];
                if (candidate is null)
                {
                    continue;
                }

                if (string.Equals(candidate.Id, track.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.Id, effectiveTrack.Id, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(track.YouTubeVideoId) &&
                     string.Equals(candidate.YouTubeVideoId, track.YouTubeVideoId, StringComparison.Ordinal)))
                {
                    startIndex = i;
                    break;
                }
            }
        }

        if (startIndex < 0)
        {
            await PlaySingleTrackAsync(effectiveTrack);
            return;
        }

        var orderedList = new List<TrackItem>(sourceList.Count);
        int resolvedStartIndex = 0;
        for (int i = 0; i < sourceList.Count; i++)
        {
            var item = sourceList[i];
            if (item is null)
            {
                continue;
            }

            if (i == startIndex)
            {
                resolvedStartIndex = orderedList.Count;
                orderedList.Add(effectiveTrack);
            }
            else
            {
                orderedList.Add(item);
            }
        }

        if (orderedList.Count == 0)
        {
            await PlaySingleTrackAsync(effectiveTrack);
            return;
        }

        ApplyOptimisticTrackSelection(effectiveTrack);

        int loadGen = ++_trackLoadGeneration;
        IsTrackLoading = true;
        try
        {
            await _audioPlayerService.SetQueueAsync(orderedList, resolvedStartIndex, autoPlay: true);
        }
        finally
        {
            if (loadGen == _trackLoadGeneration)
            {
                IsTrackLoading = false;
            }
        }
    }

    /// <summary>
    /// Thread-safe offline/catalog fallback provider wired to <see cref="IAudioPlayerService.FallbackSimilarTracksProvider"/>.
    /// Mirrors Android's <c>HomeViewModel.buildOfflineSimilarTracksFallback</c> using immutable UI-refreshed snapshots.
    /// </summary>
    private IReadOnlyList<TrackItem> BuildOfflineSimilarTracksFallback(
        IReadOnlyList<TrackItem> seedTracks,
        IReadOnlySet<string> excludeIds,
        int maxCount)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        var seedArtists = new HashSet<string>(StringComparer.Ordinal);
        var seedAlbums = new HashSet<string>(StringComparer.Ordinal);
        var seenNormalizedIds = new HashSet<string>(StringComparer.Ordinal);
        var seenTitleArtist = new HashSet<string>(StringComparer.Ordinal);

        foreach (string rawId in excludeIds)
        {
            string norm = TrackDeduplicationHelper.NormalizeTrackId(rawId);
            if (!string.IsNullOrEmpty(norm))
            {
                seenNormalizedIds.Add(norm);
            }
        }

        foreach (var seed in seedTracks)
        {
            if (seed is null)
            {
                continue;
            }

            string primary = TrackDeduplicationHelper.PrimaryArtistName(seed.Artist).ToLowerInvariant();
            if (!string.IsNullOrEmpty(primary))
            {
                seedArtists.Add(primary);
            }

            string album = seed.Album?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!string.IsNullOrEmpty(album) && !RecentAlbumItem.IsGenericAlbumTitle(album))
            {
                seedAlbums.Add(album);
            }

            foreach (string normId in TrackDeduplicationHelper.ExtractNormalizedTrackIds(seed))
            {
                seenNormalizedIds.Add(normId);
            }

            if (!string.IsNullOrWhiteSpace(seed.Title))
            {
                seenTitleArtist.Add(TrackDeduplicationHelper.BuildTitleArtistKey(seed.Title, seed.Artist));
            }
        }

        var homeRec = _homeRecommendedSnapshot;
        var liked = _likedSongsSnapshot;
        var library = _librarySongsSnapshot;
        var homeGenre = _homeGenreSnapshot;
        var recent = _homeRecentlyPlayedSnapshot;

        var matchingArtistOrAlbum = new List<TrackItem>();
        var otherTracks = new List<TrackItem>();

        void CollectFromPool(IReadOnlyList<TrackItem> pool)
        {
            for (int i = 0; i < pool.Count; i++)
            {
                var candidate = pool[i];
                if (candidate is null ||
                    string.IsNullOrWhiteSpace(candidate.Title) ||
                    string.Equals(candidate.Id, "empty_state_track", StringComparison.Ordinal))
                {
                    continue;
                }

                var effective = _catalogService.FindLocalOfflineEquivalent(candidate) ?? candidate;

                if (excludeIds.Contains(candidate.Id) || excludeIds.Contains(effective.Id))
                {
                    continue;
                }

                var candidateIds = TrackDeduplicationHelper.ExtractNormalizedTrackIds(candidate);
                if (!ReferenceEquals(effective, candidate))
                {
                    foreach (string effId in TrackDeduplicationHelper.ExtractNormalizedTrackIds(effective))
                    {
                        candidateIds.Add(effId);
                    }
                }

                bool alreadyExcludedOrSeen = false;
                foreach (string cid in candidateIds)
                {
                    if (seenNormalizedIds.Contains(cid))
                    {
                        alreadyExcludedOrSeen = true;
                        break;
                    }
                }

                if (alreadyExcludedOrSeen)
                {
                    continue;
                }

                string titleArtistKey = TrackDeduplicationHelper.BuildTitleArtistKey(effective.Title, effective.Artist);
                if (!seenTitleArtist.Add(titleArtistKey))
                {
                    continue;
                }

                foreach (string cid in candidateIds)
                {
                    seenNormalizedIds.Add(cid);
                }

                string artistKey = TrackDeduplicationHelper.PrimaryArtistName(effective.Artist).ToLowerInvariant();
                string albumKey = effective.Album?.Trim().ToLowerInvariant() ?? string.Empty;

                if ((!string.IsNullOrEmpty(artistKey) && seedArtists.Contains(artistKey)) ||
                    (!string.IsNullOrEmpty(albumKey) && !RecentAlbumItem.IsGenericAlbumTitle(albumKey) && seedAlbums.Contains(albumKey)))
                {
                    matchingArtistOrAlbum.Add(effective);
                }
                else
                {
                    otherTracks.Add(effective);
                }
            }
        }

        CollectFromPool(homeRec);
        CollectFromPool(liked);
        CollectFromPool(library);
        CollectFromPool(homeGenre);
        CollectFromPool(recent);

        var combined = new List<TrackItem>(Math.Min(maxCount, matchingArtistOrAlbum.Count + otherTracks.Count));
        for (int i = 0; i < matchingArtistOrAlbum.Count && combined.Count < maxCount; i++)
        {
            combined.Add(matchingArtistOrAlbum[i]);
        }

        for (int i = 0; i < otherTracks.Count && combined.Count < maxCount; i++)
        {
            combined.Add(otherTracks[i]);
        }

        return combined;
    }

    /// <summary>
    /// Moves a track in the upcoming playback queue without rebuilding the entire <see cref="Queue"/> collection,
    /// preserving active drag-and-drop container state.
    /// </summary>
    public void MoveQueueItem(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Queue.Count ||
            toIndex < 0 || toIndex >= Queue.Count ||
            fromIndex == toIndex)
        {
            return;
        }

        _isSuppressingQueueSync = true;
        try
        {
            Queue.Move(fromIndex, toIndex);
            _audioPlayerService.MoveQueueItem(fromIndex, toIndex);
            OnPropertyChanged(nameof(QueueCountSummary));
        }
        finally
        {
            _isSuppressingQueueSync = false;
        }
    }

    /// <summary>
    /// Starts playback from a clicked track inside the upcoming Queue.
    /// </summary>
    public async Task PlayQueueItemAsync(TrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        int index = Queue.IndexOf(track);
        if (index >= 0)
        {
            _hasUserInitiatedPlayback = true;
            _deferredPlaybackStartTrackId = null;
            int loadGen = ++_trackLoadGeneration;
            IsTrackLoading = true;
            try
            {
                await _audioPlayerService.PlayQueueIndexAsync(index);
            }
            finally
            {
                if (loadGen == _trackLoadGeneration)
                {
                    IsTrackLoading = false;
                }
            }
        }
        else
        {
            await PlaySingleTrackAsync(track);
        }
    }

    [RelayCommand]
    private void SelectLikedViewMode(string modeName)
    {
        if (Enum.TryParse<LikedSongsViewMode>(modeName, ignoreCase: true, out var mode))
        {
            SelectedLikedViewMode = mode;
        }
    }

    [RelayCommand]
    private void SelectLikedFilter(string filterName)
    {
        if (Enum.TryParse<LikedFilterTab>(filterName, ignoreCase: true, out var filter))
        {
            SelectedLikedFilter = filter;
        }
    }

    [RelayCommand]
    private void SelectLikedSort(string sortName)
    {
        if (Enum.TryParse<LikedSortMode>(sortName, ignoreCase: true, out var sort))
        {
            SelectedLikedSort = sort;
        }
    }

    /// <summary>
    /// Plays a track from the Liked Songs view preserving the currently active sort order (A–Z vs Recent) in the queue.
    /// </summary>
    public async Task PlayLikedSongAsync(TrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        var source = SelectedLikedSort == LikedSortMode.Alphabetical && LikedOrderedSongs.Count > 0
            ? LikedOrderedSongs
            : LikedSongs;
        await PlayTrackFromListAsync(track, source.ToList());
    }

    [RelayCommand]
    private async Task PlayAllLikedSongsAsync()
    {
        var source = SelectedLikedSort == LikedSortMode.Alphabetical && LikedOrderedSongs.Count > 0
            ? LikedOrderedSongs
            : LikedSongs;
        if (source.Count == 0)
        {
            return;
        }

        await PlayTrackFromListAsync(source[0], source.ToList());
    }

    [RelayCommand]
    private async Task ShuffleLikedSongsAsync()
    {
        if (LikedSongs.Count == 0)
        {
            return;
        }

        await SetShuffledQueueAndPlayAsync(LikedSongs.ToList());
    }

    /// <summary>
    /// Plays a recommended track from the Liked screen's "Fans Also Like &amp; Recommended" pane,
    /// queueing the visible recommendations list (matching Android <c>LikedScreen</c>).
    /// </summary>
    [RelayCommand]
    public async Task PlayLikedRecommendationAsync(TrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        var list = LikedRecommendations.ToList();
        if (list.Count == 0)
        {
            list = [track];
        }

        await PlayTrackFromListAsync(track, list);
    }

    /// <summary>
    /// Refreshes the Liked screen's "Fans Also Like &amp; Recommended" tracks from YouTube Music
    /// seeded from the user's liked songs (matching Android <c>MediaCatalogService.getRecommendationsForLiked</c>).
    /// </summary>
    public async Task RefreshLikedRecommendationsAsync(bool forceRefresh = false)
    {
        const int MaxVisibleLikedRecommendations = 10;

        var likedSnapshot = _dispatcherQueue is null || _dispatcherQueue.HasThreadAccess
            ? LikedSongs.ToList()
            : _likedSongsSnapshot.ToList();

        string query;
        if (likedSnapshot.Count == 0)
        {
            query = "Top hits songs";
        }
        else
        {
            var seedArtists = new List<string>(3);
            var seenArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in likedSnapshot)
            {
                string artist = song.Artist?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(artist) ||
                    artist.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
                    artist.Equals("<unknown>", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seenArtists.Add(artist))
                {
                    seedArtists.Add(artist);
                    if (seedArtists.Count >= 3)
                    {
                        break;
                    }
                }
            }

            if (seedArtists.Count > 0)
            {
                query = $"{string.Join(" ", seedArtists)} mix songs";
            }
            else
            {
                var seedTitles = likedSnapshot
                    .Select(t => t.Title?.Trim() ?? string.Empty)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Take(2)
                    .ToList();

                query = seedTitles.Count > 0
                    ? $"{string.Join(" ", seedTitles)} similar songs"
                    : "Top hits songs";
            }
        }

        string seedKey = $"{query}||{string.Join("|", likedSnapshot.Select(t => t.Id))}";
        if (!forceRefresh &&
            string.Equals(seedKey, _lastLikedRecsSeedKey, StringComparison.Ordinal) &&
            LikedRecommendations.Count > 0)
        {
            return;
        }

        var cts = ReplaceCts(ref _likedRecsCts);
        var ct = cts.Token;

        await RunOnUiThreadAsync(() =>
        {
            if (ReferenceEquals(_likedRecsCts, cts))
            {
                IsLoadingLikedRecommendations = true;
            }
        });

        try
        {
            var excludedIds = BuildTrackExclusionIds(likedSnapshot);
            var likedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in likedSnapshot)
            {
                string trimmedTitle = song.Title?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(trimmedTitle))
                {
                    likedTitles.Add(trimmedTitle);
                }
            }

            var catalogResult = await _youTubeMusicService.SearchCatalogAsync(query, ct);
            ct.ThrowIfCancellationRequested();

            var filtered = new List<TrackItem>(MaxVisibleLikedRecommendations);
            var batchSeenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var batchSeenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAcceptCandidate(TrackItem? candidate, bool filterAgainstLiked)
            {
                if (candidate is null || filtered.Count >= MaxVisibleLikedRecommendations)
                {
                    return;
                }

                string trimmedTitle = candidate.Title?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(trimmedTitle))
                {
                    return;
                }

                if (filterAgainstLiked)
                {
                    if (IsTrackExcludedById(candidate, excludedIds) || likedTitles.Contains(trimmedTitle))
                    {
                        return;
                    }
                }

                if (IsTrackExcludedById(candidate, batchSeenIds) || !batchSeenTitles.Add(trimmedTitle))
                {
                    return;
                }

                AddTrackIdsToSet(candidate, batchSeenIds);
                filtered.Add(candidate);
            }

            foreach (var song in catalogResult.Songs)
            {
                TryAcceptCandidate(song, filterAgainstLiked: true);
                if (filtered.Count >= MaxVisibleLikedRecommendations)
                {
                    break;
                }
            }

            // Fallback to InnerTube /next radio recommendations if catalog search was completely filtered out
            if (filtered.Count == 0 && likedSnapshot.Count > 0)
            {
                var similarFallback = await _youTubeMusicService.FetchSimilarTracksForQueueAsync(
                    likedSnapshot.Take(3).ToList(),
                    excludedIds,
                    maxCount: 12,
                    cancellationToken: ct);
                ct.ThrowIfCancellationRequested();

                foreach (var song in similarFallback)
                {
                    TryAcceptCandidate(song, filterAgainstLiked: true);
                    if (filtered.Count >= MaxVisibleLikedRecommendations)
                    {
                        break;
                    }
                }
            }

            // Final fallback matching Android: if every search result was already liked, still show search songs
            if (filtered.Count == 0 && catalogResult.Songs.Count > 0)
            {
                foreach (var song in catalogResult.Songs)
                {
                    TryAcceptCandidate(song, filterAgainstLiked: false);
                    if (filtered.Count >= MaxVisibleLikedRecommendations)
                    {
                        break;
                    }
                }
            }

            var warmed = await WarmTrackCoversAsync(filtered, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                if (ct.IsCancellationRequested || !ReferenceEquals(_likedRecsCts, cts))
                {
                    return;
                }

                SyncTracksLikedStateAgainstLikedSongs(warmed);
                LikedRecommendations.Clear();
                foreach (var rec in warmed)
                {
                    LikedRecommendations.Add(rec);
                }

                SyncTrackCollectionPlayingState(LikedRecommendations);
                _lastLikedRecsSeedKey = seedKey;
                _isLikedRecsDirty = false;

                OnPropertyChanged(nameof(LikedRecommendationsLoadingVisibility));
                OnPropertyChanged(nameof(LikedRecommendationsEmptyVisibility));
                OnPropertyChanged(nameof(LikedRecommendationsListVisibility));
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer like/unlike update
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh Liked screen recommendations.");
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                if (ReferenceEquals(_likedRecsCts, cts))
                {
                    IsLoadingLikedRecommendations = false;
                    OnPropertyChanged(nameof(LikedRecommendationsLoadingVisibility));
                    OnPropertyChanged(nameof(LikedRecommendationsEmptyVisibility));
                    OnPropertyChanged(nameof(LikedRecommendationsListVisibility));
                }
            });
        }
    }

    /// <summary>
    /// Toggles the liked state of any track item (local or online) and updates the Liked Songs view.
    /// </summary>
    public async Task ToggleLikeTrackItemAsync(TrackItem? track)
    {
        if (track is null || track.Id == "empty_state_track")
        {
            return;
        }

        bool isLiked = await _catalogService.ToggleTrackLikedAsync(track.Id, track);
        track.IsLiked = isLiked;

        foreach (var item in HomeRecommendedTracks
                     .Concat(HomeGenreTracks)
                     .Concat(AlbumDetailTracks)
                     .Concat(PlaylistDetailTracks)
                     .Concat(PlaylistRelatedTracks)
                     .Concat(LikedRecommendations)
                     .Concat(SearchResultSongs)
                     .Concat(MoodDetailSongs)
                     .Concat(Songs)
                     .Concat(LibraryRecentlyAdded)
                     .Concat(LibraryRecentlyAddedPreview)
                     .Concat(LibrarySongsPreview)
                     .Concat(LibraryOrderedSongs))
        {
            if (string.Equals(item.Id, track.Id, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(track.YouTubeVideoId) &&
                 string.Equals(item.YouTubeVideoId, track.YouTubeVideoId, StringComparison.Ordinal)))
            {
                item.IsLiked = isLiked;
            }
        }

        if (CurrentTrack is not null && string.Equals(CurrentTrack.Id, track.Id, StringComparison.OrdinalIgnoreCase))
        {
            CurrentTrack.IsLiked = isLiked;
            IsCurrentTrackLiked = isLiked;
        }

        _isLikedDirty = true;
        _isLikedRecsDirty = true;
        _lastLikedRecsSeedKey = null;

        if (IsLikedViewOpen)
        {
            await RefreshLikedSongsAsync();
            _ = RefreshLikedRecommendationsAsync(forceRefresh: true);
        }
    }

    [RelayCommand]
    private async Task SelectCategoryAsync(string categoryName)
    {
        if (Enum.TryParse<SearchCategoryFilter>(categoryName, ignoreCase: true, out var category))
        {
            IsLibraryRecentlyAddedExpanded = false;
            SelectedCategory = category;

            bool hasCurrentSourceData =
                _isLibraryHydrated &&
                !_isLibraryDirty &&
                _lastLoadedLibrarySource == SelectedSourceType &&
                string.Equals(_lastLoadedLibraryQueryKey, string.Empty, StringComparison.Ordinal) &&
                (Songs.Count > 0 || Albums.Count > 0 || Artists.Count > 0);

            if (!hasCurrentSourceData)
            {
                await RefreshCatalogAsync();
            }
            else
            {
                NotifyLibraryComputedProperties();
                LibraryIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    [RelayCommand]
    private async Task SelectSourceAsync(string sourceName)
    {
        if (Enum.TryParse<MediaSourceType>(sourceName, ignoreCase: true, out var source))
        {
            IsLibraryRecentlyAddedExpanded = false;
            SelectedSourceType = source;
            _isLibraryDirty = true;
            await RefreshCatalogAsync();
        }
    }

    [RelayCommand]
    private void OpenLibraryRecentlyAdded()
    {
        IsLibraryRecentlyAddedExpanded = true;
        LibraryIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CloseLibraryRecentlyAdded()
    {
        IsLibraryRecentlyAddedExpanded = false;
        LibraryIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Plays a Library track with the contextually appropriate queue (Overview Songs, A–Z Ordered Songs, or Recently Added).
    /// </summary>
    public async Task PlayLibrarySongAsync(TrackItem? track, bool fromAlphabeticalSongs = false, bool fromRecentlyAdded = false)
    {
        if (track is null)
        {
            return;
        }

        IReadOnlyList<TrackItem> queueSource = fromRecentlyAdded
            ? (LibraryRecentlyAdded.Count > 0 ? LibraryRecentlyAdded : Songs)
            : fromAlphabeticalSongs
                ? (LibraryOrderedSongs.Count > 0 ? LibraryOrderedSongs : Songs)
                : Songs;

        await PlayTrackFromListAsync(track, queueSource);
    }

    [RelayCommand]
    private void SelectNowPlayingTab(string tabName)
    {
        if (Enum.TryParse<NowPlayingTab>(tabName, ignoreCase: true, out var tab))
        {
            if (ActiveNowPlayingTab != tab)
            {
                ActiveNowPlayingTab = tab;
                NowPlayingTabTransitionRequested?.Invoke(this, tab);
            }

            if (tab == NowPlayingTab.Lyrics &&
                CurrentTrack is { } track &&
                track.Id != "empty_state_track" &&
                (_activeLyricsTrack is null || !IsSameTrack(_activeLyricsTrack, track)))
            {
                BeginTrackLyricsPipeline(track);
            }
        }
    }

    [RelayCommand]
    private void CycleRepeatMode()
    {
        RepeatMode = _audioPlayerService.CycleRepeatMode();
        StatusMessage = RepeatModeTooltip;
        TriggerPlaybackSessionPersist(forceImmediate: true);
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        IsShuffleEnabled = _audioPlayerService.ToggleShuffle();
        StatusMessage = IsShuffleEnabled ? "Shuffle: Enabled" : "Shuffle: Off";
        TriggerPlaybackSessionPersist(forceImmediate: true);
    }

    [RelayCommand]
    private void Rewind5Seconds()
    {
        SeekRelativeCore(TimeSpan.FromSeconds(-5));
    }

    [RelayCommand]
    private void Forward10Seconds()
    {
        SeekRelativeCore(TimeSpan.FromSeconds(10));
    }

    private void SeekRelativeCore(TimeSpan delta)
    {
        if (CurrentTrack is null ||
            string.Equals(CurrentTrack.Id, "empty_state_track", StringComparison.Ordinal))
        {
            return;
        }

        double maxDurationSec = _audioPlayerService.Duration.TotalSeconds > 0.0
            ? Math.Max(1.0, _audioPlayerService.Duration.TotalSeconds)
            : Math.Max(1.0, TotalDurationSeconds);

        _hasUserInitiatedPlayback = true;
        _audioPlayerService.SeekRelative(delta);
        Interlocked.Increment(ref _positionSyncGeneration);

        var effectiveDuration = _audioPlayerService.Duration > TimeSpan.Zero
            ? _audioPlayerService.Duration
            : TimeSpan.FromSeconds(maxDurationSec);
        var effectivePosition = _audioPlayerService.Position >= TimeSpan.Zero
            ? _audioPlayerService.Position
            : TimeSpan.Zero;

        ApplyPlaybackPositionToUi(effectivePosition, effectiveDuration, forceLyricNotify: true);
    }

    partial void OnShowFullscreenSeekButtonsChanged(bool value)
    {
        if (!_isHydratingPlayerUiSettings)
        {
            _ = _storageService.SetShowFullscreenSeekButtonsAsync(value);
        }
    }

    [RelayCommand]
    private void ToggleFullscreenQueue()
    {
        if (IsFullscreenSidePanelOpen && ActiveFullscreenTab == NowPlayingTab.Queue)
        {
            IsFullscreenSidePanelOpen = false;
            IsFullscreenQueueOpen = false;
            IsFullscreenLyricsVisible = false;
        }
        else
        {
            ActiveFullscreenTab = NowPlayingTab.Queue;
            IsFullscreenSidePanelOpen = true;
            IsFullscreenQueueOpen = true;
            IsFullscreenLyricsVisible = false;
            IsVisualizerSelectorOpen = false;
        }

        FullscreenLyricsLayoutChanged?.Invoke(this, IsFullscreenSidePanelOpen);
        FullscreenSideTabChanged?.Invoke(this, ActiveFullscreenTab);
    }

    [RelayCommand]
    private void CloseFullscreenQueue()
    {
        IsFullscreenQueueOpen = false;
        if (ActiveFullscreenTab == NowPlayingTab.Queue)
        {
            IsFullscreenSidePanelOpen = false;
            FullscreenLyricsLayoutChanged?.Invoke(this, false);
        }
    }

    [RelayCommand]
    private void OpenCurrentTrackContextMenu()
    {
        if (CurrentTrack is not null && CurrentTrack.Id != "empty_state_track")
        {
            OpenTrackContextMenu(CurrentTrack);
        }
    }

    [RelayCommand]
    private async Task PlayTrackAsync(TrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        await PlayTrackFromListAsync(track, Songs);
    }

    [RelayCommand]
    private async Task TogglePlayPauseAsync()
    {
        if (CurrentTrack is null ||
            string.Equals(CurrentTrack.Id, "empty_state_track", StringComparison.Ordinal))
        {
            return;
        }

        _hasUserInitiatedPlayback = true;
        await _audioPlayerService.TogglePlayPauseAsync();
    }

    [RelayCommand]
    private async Task NextTrackAsync()
    {
        _hasUserInitiatedPlayback = true;
        _deferredPlaybackStartTrackId = null;
        int loadGen = ++_trackLoadGeneration;
        IsTrackLoading = true;
        try
        {
            await _audioPlayerService.NextAsync();
        }
        finally
        {
            if (loadGen == _trackLoadGeneration)
            {
                IsTrackLoading = false;
            }
        }
    }

    [RelayCommand]
    private async Task PreviousTrackAsync()
    {
        _hasUserInitiatedPlayback = true;
        _deferredPlaybackStartTrackId = null;
        int loadGen = ++_trackLoadGeneration;
        IsTrackLoading = true;
        try
        {
            await _audioPlayerService.PreviousAsync();
        }
        finally
        {
            if (loadGen == _trackLoadGeneration)
            {
                IsTrackLoading = false;
            }
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        _audioPlayerService.IsMuted = !_audioPlayerService.IsMuted;
        IsMuted = _audioPlayerService.IsMuted;
    }

    [RelayCommand]
    private async Task ToggleLikeCurrentTrackAsync()
    {
        if (CurrentTrack is null || CurrentTrack.Id == "empty_state_track")
        {
            return;
        }

        await ToggleLikeTrackItemAsync(CurrentTrack);
    }

    [RelayCommand]
    private void ToggleSearchBox()
    {
        if (!IsSearchPageOpen)
        {
            OpenSearchPage();
        }
        else if (!IsSearchExpanded)
        {
            IsSearchExpanded = true;
            SearchExpandChanged?.Invoke(this, true);
        }
        else
        {
            CloseSearchPage();
        }
    }

    /// <summary>
    /// Immediately opens the dedicated Search screen and expands the inline Search capsule
    /// without touching the underlying <see cref="CurrentSection"/> or <see cref="ActiveSubView"/>.
    /// </summary>
    [RelayCommand]
    public void OpenSearchPage()
    {
        _returnToSearchAfterDetail = false;
        IsSearchPageOpen = true;
        IsSearchExpanded = true;
        SearchExpandChanged?.Invoke(this, true);

        _ = EnsureSearchBootstrapDataLoadedAsync();
    }

    /// <summary>
    /// Closes the dedicated Search screen and collapses the inline Search bar,
    /// restoring the exact previous screen seamlessly.
    /// </summary>
    [RelayCommand]
    public void CloseSearchPage()
    {
        _returnToSearchAfterDetail = false;
        CloseSearchOverlayIfOpen(clearQuery: true);
    }

    /// <summary>
    /// Bound to the inline Search bar 'X' button:
    /// clears non-empty query text (keeping Search open on Moods &amp; Genres),
    /// or closes Search and returns to the previous screen when already empty.
    /// </summary>
    [RelayCommand]
    private void ClearOrCloseSearch()
    {
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchQuery = string.Empty;
            SelectedSearchResultFilter = SearchResultFilterTab.All;
        }
        else
        {
            CloseSearchPage();
        }
    }

    /// <summary>
    /// Toggles the dedicated Search scope between YouTube Music (Online) and Local Library.
    /// </summary>
    [RelayCommand]
    private void ToggleSearchSource()
    {
        IsSearchOnlineMode = !IsSearchOnlineMode;
        OnPropertyChanged(nameof(SearchPageSubtitle));
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var token = ReplaceCts(ref _searchCts).Token;
            _ = ExecuteDedicatedSearchAsync(SearchQuery.Trim(), saveToHistory: false, token);
        }
    }

    /// <summary>
    /// Submits the current Search query immediately (e.g., on Enter) and saves it to persisted Recent Searches.
    /// </summary>
    [RelayCommand]
    public async Task SubmitSearchQueryAsync()
    {
        string trimmed = SearchQuery?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        var token = ReplaceCts(ref _searchCts).Token;
        await ExecuteDedicatedSearchAsync(trimmed, saveToHistory: true, token);
    }

    /// <summary>
    /// Applies a clicked Recent Search chip as the active query and refreshes results.
    /// </summary>
    public async Task ApplyRecentSearchAsync(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        string trimmed = query.Trim();
        _isSuppressingSearchQueryChange = true;
        try
        {
            SearchQuery = trimmed;
        }
        finally
        {
            _isSuppressingSearchQueryChange = false;
        }

        OnPropertyChanged(nameof(HasSearchQuery));
        OnPropertyChanged(nameof(SearchExploreVisibility));
        OnPropertyChanged(nameof(SearchResultsVisibility));
        OnPropertyChanged(nameof(SearchPageSubtitle));
        OnPropertyChanged(nameof(SearchClearOrCloseTooltip));

        var token = ReplaceCts(ref _searchCts).Token;
        await ExecuteDedicatedSearchAsync(trimmed, saveToHistory: true, token);
    }

    /// <summary>
    /// Removes a single entry from persisted Recent Searches.
    /// </summary>
    public async Task RemoveRecentSearchAsync(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        try
        {
            var updated = await _storageService.RemoveRecentSearchAsync(query);
            ReplaceRecentSearchesCollection(updated);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove recent search '{Query}'.", query);
        }
    }

    [RelayCommand]
    private async Task ClearRecentSearchesAsync()
    {
        try
        {
            await _storageService.ClearRecentSearchesAsync();
            RecentSearches.Clear();
            OnPropertyChanged(nameof(HasRecentSearchesVisibility));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clear recent searches.");
        }
    }

    [RelayCommand]
    private void SelectSearchResultFilter(string filterName)
    {
        if (Enum.TryParse<SearchResultFilterTab>(filterName, ignoreCase: true, out var filter))
        {
            SelectedSearchResultFilter = filter;
        }
    }

    [RelayCommand]
    private void ClearSelectedMood()
    {
        CancelAndDisposeCts(ref _moodDetailCts);
        IsLoadingMoodDetail = false;
        if (SelectedMoodGenreTile is not null)
        {
            SelectedMoodGenreTile.IsSelected = false;
            SelectedMoodGenreTile = null;
        }

        MoodDetailPlaylists.Clear();
        MoodDetailAlbums.Clear();
        MoodDetailSongs.Clear();
        NotifyMoodDetailComputedProperties();
    }

    /// <summary>
    /// Selects a Mood or Genre tile in the left column and loads its curated Playlists, Albums, and Songs on the right.
    /// </summary>
    public async Task SelectMoodGenreCategoryAsync(MoodGenreTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        foreach (var sec in MoodGenreSections)
        {
            foreach (var item in sec.Items)
            {
                item.IsSelected = ReferenceEquals(item, tile);
            }
        }

        SelectedMoodGenreTile = tile;
        var ct = ReplaceCts(ref _moodDetailCts).Token;

        IsLoadingMoodDetail = true;
        MoodDetailPlaylists.Clear();
        MoodDetailAlbums.Clear();
        MoodDetailSongs.Clear();
        NotifyMoodDetailComputedProperties();

        try
        {
            var detail = await _youTubeMusicService.GetMoodGenreCategoryContentAsync(
                tile.Category.BrowseId,
                tile.Category.Params,
                tile.Category.Title,
                ct);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            MoodDetailPlaylists.Clear();
            foreach (var pl in detail.Playlists)
            {
                MoodDetailPlaylists.Add(pl);
            }

            MoodDetailAlbums.Clear();
            foreach (var alb in detail.Albums)
            {
                MoodDetailAlbums.Add(alb);
            }

            MoodDetailSongs.Clear();
            foreach (var song in detail.Songs)
            {
                MoodDetailSongs.Add(song);
            }

            SyncSearchPlayingTrackState();
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer category click
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Mood/Genre category '{Title}'.", tile.Title);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingMoodDetail = false;
                NotifyMoodDetailComputedProperties();
            }
        }
    }

    /// <summary>
    /// Opens or plays the highlighted Top Result card from Search results and persists the active query in Recent Searches.
    /// </summary>
    [RelayCommand]
    public async Task OpenSearchTopResultAsync()
    {
        var top = SearchResultTopResult;
        if (top is null)
        {
            return;
        }

        await RecordActiveSearchQueryInHistoryAsync();

        switch (top.Kind)
        {
            case SearchTopResultKind.Song when top.Track is not null:
                await PlaySearchSongAsync(top.Track);
                break;
            case SearchTopResultKind.Artist when top.Artist is not null:
                await OpenArtistDetailAsync(top.Artist);
                break;
            case SearchTopResultKind.Album when top.Album is not null:
                await OpenAlbumDetailAsync(top.Album);
                break;
            case SearchTopResultKind.Playlist when top.Playlist is not null:
                await PlayRecommendedPlaylistAsync(top.Playlist);
                break;
        }
    }

    /// <summary>
    /// Plays a track from Search results as a 1-track seed queue (immediately triggering 20 progressive
    /// similar recommendations instead of queuing sibling search results, matching Android Search behavior)
    /// and saves the active query to Recent Searches.
    /// </summary>
    public async Task PlaySearchSongAsync(TrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        await RecordActiveSearchQueryInHistoryAsync();
        await PlaySingleTrackAsync(track);
    }

    /// <summary>
    /// Plays a track from the Mood/Genre right-column detail songs list.
    /// </summary>
    public async Task PlayMoodDetailSongAsync(TrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        await PlayTrackFromListAsync(track, MoodDetailSongs.Count > 0 ? MoodDetailSongs.ToList() : [track]);
    }

    /// <summary>
    /// Persists the currently active search query into Recent Searches when the user engages with any result.
    /// </summary>
    public async Task RecordActiveSearchQueryInHistoryAsync()
    {
        string trimmed = SearchQuery?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        try
        {
            var updated = await _storageService.AddRecentSearchAsync(trimmed);
            ReplaceRecentSearchesCollection(updated);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not save recent search '{Query}'.", trimmed);
        }
    }

    private void CloseSearchOverlayIfOpen(bool clearQuery)
    {
        CancelAndDisposeCts(ref _searchCts);
        CancelAndDisposeCts(ref _moodDetailCts);

        IsLoadingMoodDetail = false;
        if (SelectedMoodGenreTile is not null)
        {
            SelectedMoodGenreTile.IsSelected = false;
            SelectedMoodGenreTile = null;
        }

        MoodDetailPlaylists.Clear();
        MoodDetailAlbums.Clear();
        MoodDetailSongs.Clear();
        NotifyMoodDetailComputedProperties();

        if (clearQuery)
        {
            if (!string.IsNullOrEmpty(SearchQuery))
            {
                _isSuppressingSearchQueryChange = true;
                try
                {
                    SearchQuery = string.Empty;
                }
                finally
                {
                    _isSuppressingSearchQueryChange = false;
                }
            }

            IsSearchingCatalog = false;
            ClearSearchResultsCollections();
            SelectedSearchResultFilter = SearchResultFilterTab.All;
            OnPropertyChanged(nameof(HasSearchQuery));
            OnPropertyChanged(nameof(SearchExploreVisibility));
            OnPropertyChanged(nameof(SearchResultsVisibility));
            OnPropertyChanged(nameof(SearchPageSubtitle));
            OnPropertyChanged(nameof(SearchClearOrCloseTooltip));
            NotifySearchComputedProperties();
        }

        bool wasExpanded = IsSearchExpanded;
        IsSearchPageOpen = false;
        IsSearchExpanded = false;
        if (wasExpanded)
        {
            SearchExpandChanged?.Invoke(this, false);
        }
    }

    private async Task EnsureSearchBootstrapDataLoadedAsync()
    {
        if (!_hasLoadedRecentSearches)
        {
            _hasLoadedRecentSearches = true;
            try
            {
                var recent = await _storageService.LoadRecentSearchesAsync();
                ReplaceRecentSearchesCollection(recent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not load recent searches.");
            }
        }

        if (!_hasLoadedMoodsAndGenres)
        {
            _hasLoadedMoodsAndGenres = true;
            IsLoadingMoodsAndGenres = true;
            try
            {
                var feed = await _youTubeMusicService.GetMoodsAndGenresAsync();
                MoodGenreSections.Clear();
                foreach (var sec in feed.Sections)
                {
                    var tiles = sec.Items
                        .Select(item => new MoodGenreTileViewModel(item, IsLightMode))
                        .ToList();
                    if (tiles.Count > 0)
                    {
                        MoodGenreSections.Add(new MoodGenreSectionViewModel(sec.Title, tiles));
                    }
                }
            }
            catch (Exception ex)
            {
                _hasLoadedMoodsAndGenres = false;
                _logger.LogWarning(ex, "Failed to load YouTube Music Moods & Genres feed.");
            }
            finally
            {
                IsLoadingMoodsAndGenres = false;
                OnPropertyChanged(nameof(MoodsLoadingVisibility));
                OnPropertyChanged(nameof(MoodsContentVisibility));
            }
        }
    }

    private async Task ExecuteDedicatedSearchAsync(string query, bool saveToHistory, CancellationToken cancellationToken)
    {
        IsSearchingCatalog = true;
        NotifySearchComputedProperties();

        try
        {
            if (saveToHistory)
            {
                var updatedRecent = await _storageService.AddRecentSearchAsync(query, cancellationToken);
                ReplaceRecentSearchesCollection(updatedRecent);
            }

            if (IsSearchOnlineMode)
            {
                var rich = await _youTubeMusicService.SearchRichCatalogAsync(query, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                PopulateSearchResults(rich);
            }
            else
            {
                await EnsurePlaylistsHydratedAsync(cancellationToken, includeSmartSuggestions: false);

                var local = await _catalogService.QueryCatalogAsync(
                    query,
                    SearchCategoryFilter.All,
                    MediaSourceType.Local,
                    NavigationSection.Library,
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var matchedPlaylists = Playlists
                    .Where(p => p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                p.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(p => new RecommendedPlaylistItem(
                        Id: p.Id,
                        Title: p.Title,
                        Subtitle: p.SummarySubtitle,
                        ArtworkUri: p.HasCustomCover ? p.SafeCustomCoverUri : p.CollageCover1,
                        PlaylistId: p.Id,
                        BrowseId: null,
                        Author: "Your Playlist",
                        TrackCountText: p.TrackCountSubtitle))
                    .ToList();

                SearchTopResultItem? top = null;
                if (local.Artists.Count > 0 && local.Artists[0].Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    var a = local.Artists[0];
                    top = new SearchTopResultItem(SearchTopResultKind.Artist, a.Name, a.DisplaySubtitle, a.SafeAvatarUri, Artist: a);
                }
                else if (local.Songs.Count > 0)
                {
                    var s = local.Songs[0];
                    top = new SearchTopResultItem(SearchTopResultKind.Song, s.Title, s.ArtistAlbumDotSubtitle, s.SafeCoverArtUri, Track: s);
                }
                else if (local.Albums.Count > 0)
                {
                    var alb = local.Albums[0];
                    top = new SearchTopResultItem(SearchTopResultKind.Album, alb.Title, alb.ArtistYearSubtitle, alb.SafeCoverArtUri, Album: alb);
                }

                PopulateSearchResults(new YouTubeMusicSearchResult(
                    query,
                    top,
                    local.Songs,
                    local.Albums,
                    local.Artists,
                    matchedPlaylists));
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer search query
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dedicated search failed for query '{Query}'.", query);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsSearchingCatalog = false;
                NotifySearchComputedProperties();
            }
        }
    }

    private void PopulateSearchResults(YouTubeMusicSearchResult result)
    {
        SearchResultTopResult = result.TopResult;

        SearchResultSongs.Clear();
        foreach (var s in result.Songs)
        {
            SearchResultSongs.Add(s);
        }

        SearchResultArtists.Clear();
        foreach (var a in result.Artists)
        {
            SearchResultArtists.Add(a);
        }

        SearchResultAlbums.Clear();
        foreach (var alb in result.Albums)
        {
            SearchResultAlbums.Add(alb);
        }

        SearchResultPlaylists.Clear();
        foreach (var pl in result.Playlists)
        {
            SearchResultPlaylists.Add(pl);
        }

        SyncSearchPlayingTrackState();
        NotifySearchComputedProperties();
    }

    private void ClearSearchResultsCollections()
    {
        SearchResultTopResult = null;
        SearchResultSongs.Clear();
        SearchResultArtists.Clear();
        SearchResultAlbums.Clear();
        SearchResultPlaylists.Clear();
    }

    private void ReplaceRecentSearchesCollection(IReadOnlyList<string> items)
    {
        RecentSearches.Clear();
        foreach (var item in items)
        {
            RecentSearches.Add(item);
        }

        OnPropertyChanged(nameof(HasRecentSearchesVisibility));
    }

    private void NotifyMoodDetailComputedProperties()
    {
        OnPropertyChanged(nameof(MoodDetailEmptyPromptVisibility));
        OnPropertyChanged(nameof(MoodDetailLoadingVisibility));
        OnPropertyChanged(nameof(MoodDetailContentVisibility));
        OnPropertyChanged(nameof(HasMoodDetailPlaylistsVisibility));
        OnPropertyChanged(nameof(HasMoodDetailAlbumsVisibility));
        OnPropertyChanged(nameof(HasMoodDetailSongsVisibility));
        OnPropertyChanged(nameof(MoodDetailEmptyResultVisibility));
        OnPropertyChanged(nameof(SearchPageSubtitle));
    }

    private void NotifySearchComputedProperties()
    {
        OnPropertyChanged(nameof(SearchLoadingVisibility));
        OnPropertyChanged(nameof(SearchResultsContentVisibility));
        OnPropertyChanged(nameof(SearchNoResultsVisibility));
        OnPropertyChanged(nameof(HasSearchTopResultVisibility));
        OnPropertyChanged(nameof(HasSearchSongsSectionVisibility));
        OnPropertyChanged(nameof(HasSearchArtistsSectionVisibility));
        OnPropertyChanged(nameof(HasSearchAlbumsSectionVisibility));
        OnPropertyChanged(nameof(HasSearchPlaylistsSectionVisibility));
        OnPropertyChanged(nameof(SearchSongsCountLabel));
        OnPropertyChanged(nameof(SearchArtistsCountLabel));
        OnPropertyChanged(nameof(SearchAlbumsCountLabel));
        OnPropertyChanged(nameof(SearchPlaylistsCountLabel));
    }

    [RelayCommand]
    private async Task ToggleLightDarkModeAsync()
    {
        // Fix: Default dark theme ID in ThemeService is "ObsidianLiquidDark"
        var targetThemeId = _themeService.CurrentTheme.IsDark ? "CrystalDaylight" : "ObsidianLiquidDark";
        await ApplyThemeAsync(targetThemeId);
    }

    [RelayCommand]
    private void ToggleNowPlayingSidebar()
    {
        if (IsMediumOrCompactLayout)
        {
            IsNarrowNowPlayingOverlayOpen = !IsNarrowNowPlayingOverlayOpen;
            return;
        }

        IsNowPlayingSidebarVisible = !IsNowPlayingSidebarVisible;
        _userPreferredSidebarVisible = IsNowPlayingSidebarVisible;
        NowPlayingSidebarVisibilityChanged?.Invoke(this, IsNowPlayingSidebarVisible);
    }

    /// <summary>
    /// Opens the LiquidGlass Song Context Menu for the specified track.
    /// </summary>
    public void OpenTrackContextMenu(TrackItem? track)
    {
        if (track is null || track.Id == "empty_state_track")
        {
            return;
        }

        ContextMenuTrack = track;
        OnPropertyChanged(nameof(ContextMenuLikeGlyph));
        OnPropertyChanged(nameof(ContextMenuLikeLabel));
        OnPropertyChanged(nameof(ContextMenuGoToArtistLabel));
        OnPropertyChanged(nameof(ContextMenuGoToAlbumLabel));
        OnPropertyChanged(nameof(ContextMenuTrackSourceDetail));
        IsTrackContextMenuOpen = true;
    }

    [RelayCommand]
    private void CloseTrackContextMenu()
    {
        IsTrackContextMenuOpen = false;
        ClearContextMenuTrackIfAllClosed();
    }

    [RelayCommand]
    private void CloseTrackInfoModal()
    {
        IsTrackInfoModalOpen = false;
        ClearContextMenuTrackIfAllClosed();
    }

    private void ClearContextMenuTrackIfAllClosed()
    {
        if (!IsTrackContextMenuOpen && !IsTrackInfoModalOpen && !IsAddToPlaylistModalOpen)
        {
            ContextMenuTrack = null;
        }
    }

    [RelayCommand]
    private async Task ContextMenuLikeAsync()
    {
        var track = ContextMenuTrack;
        IsTrackContextMenuOpen = false;
        ClearContextMenuTrackIfAllClosed();
        if (track is not null)
        {
            await ToggleLikeTrackItemAsync(track);
            OnPropertyChanged(nameof(ContextMenuLikeGlyph));
            OnPropertyChanged(nameof(ContextMenuLikeLabel));
            StatusMessage = track.IsLiked
                ? $"Added '{track.Title}' to Liked Songs"
                : $"Removed '{track.Title}' from Liked Songs";
        }
    }

    [RelayCommand]
    private async Task ContextMenuDownloadAsync()
    {
        // Open the Recommended Download & Version Suggestions modal so the user can verify or select versions
        var track = ContextMenuTrack;
        await OpenDownloadOptionsModalAsync(track);
    }

    [RelayCommand]
    private async Task ContextMenuDownloadFlacAsync()
    {
        var track = ContextMenuTrack;
        IsTrackContextMenuOpen = false;
        ClearContextMenuTrackIfAllClosed();
        await ExecuteTrackDownloadWithFormatAsync(track, DownloadAudioFormat.FlacLossless);
    }

    [RelayCommand]
    private async Task ContextMenuDownloadMp3Async()
    {
        var track = ContextMenuTrack;
        IsTrackContextMenuOpen = false;
        ClearContextMenuTrackIfAllClosed();
        await ExecuteTrackDownloadWithFormatAsync(track, DownloadAudioFormat.Mp3MaxQuality);
    }

    [RelayCommand]
    public async Task OpenDownloadOptionsModalAsync(TrackItem? track)
    {
        IsTrackContextMenuOpen = false;
        ClearContextMenuTrackIfAllClosed();
        if (track is null || track.Id == "empty_state_track")
        {
            return;
        }

        TargetDownloadTrack = track;
        IsDownloadCandidatesModalOpen = true;
        IsSearchingCandidates = true;
        RecommendedCandidate = null;
        AlternativeCandidates.Clear();
        OnPropertyChanged(nameof(AlternativeCandidatesHeaderVisibility));

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var candidates = await _musicDownloadService.SearchDownloadCandidatesAsync(track, cts.Token);

            var rec = candidates.FirstOrDefault(c => c.IsRecommended) ?? candidates.FirstOrDefault();
            RecommendedCandidate = rec;

            AlternativeCandidates.Clear();
            if (candidates.Count > 0)
            {
                foreach (var cand in candidates.Where(c => c != rec))
                {
                    AlternativeCandidates.Add(cand);
                }
            }
            OnPropertyChanged(nameof(AlternativeCandidatesHeaderVisibility));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search candidates for '{Title}'.", track.Title);
        }
        finally
        {
            IsSearchingCandidates = false;
        }
    }

    [RelayCommand]
    private void CloseDownloadOptionsModal()
    {
        IsDownloadCandidatesModalOpen = false;
        ClearDownloadOptionsModalState();
    }

    private void ClearDownloadOptionsModalState()
    {
        AlternativeCandidates.Clear();
        RecommendedCandidate = null;
        TargetDownloadTrack = null;
        OnPropertyChanged(nameof(AlternativeCandidatesHeaderVisibility));
    }

    [RelayCommand]
    private async Task DownloadCandidateAsync(TrackDownloadCandidate? candidate)
    {
        var targetTrack = TargetDownloadTrack;
        IsDownloadCandidatesModalOpen = false;
        ClearDownloadOptionsModalState();
        if (candidate is null || targetTrack is null)
        {
            return;
        }

        await ExecuteTrackDownloadWithFormatAsync(targetTrack, formatOverride: null, selectedCandidate: candidate);
    }

    [RelayCommand]
    private async Task DownloadRecommendedCandidateAsync()
    {
        var targetTrack = TargetDownloadTrack;
        var recommended = RecommendedCandidate;
        IsDownloadCandidatesModalOpen = false;
        ClearDownloadOptionsModalState();
        if (targetTrack is null)
        {
            return;
        }

        await ExecuteTrackDownloadWithFormatAsync(targetTrack, formatOverride: null, selectedCandidate: recommended);
    }

    [RelayCommand]
    private void ToggleDownloadQueueFlyout()
    {
        IsDownloadQueueFlyoutOpen = !IsDownloadQueueFlyoutOpen;
        if (IsDownloadQueueFlyoutOpen)
        {
            IsDownloadBarVisible = true;
        }
    }

    [RelayCommand]
    private void CloseDownloadQueueFlyout()
    {
        IsDownloadQueueFlyoutOpen = false;
    }

    [RelayCommand]
    private void DismissDownloadBar()
    {
        IsDownloadBarVisible = false;
        IsDownloadQueueFlyoutOpen = false;
    }

    [RelayCommand]
    private async Task OpenDownloadsFolderAsync()
    {
        await Task.Yield();
        try
        {
            string dir = !string.IsNullOrWhiteSpace(DownloadTargetDirectory) && Directory.Exists(DownloadTargetDirectory)
                ? DownloadTargetDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

            if (Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open downloads folder.");
        }
    }

    [RelayCommand]
    private void ClearCompletedDownloads()
    {
        var completed = DownloadQueue.Where(i => i.IsCompleted || i.IsFailed).ToList();
        foreach (var item in completed)
        {
            DownloadQueue.Remove(item);
        }
        OnPropertyChanged(nameof(ActiveDownloadsCount));
        OnPropertyChanged(nameof(CompletedDownloadsCount));
        OnPropertyChanged(nameof(DownloadQueueSummaryText));

        if (DownloadQueue.Count == 0 && !IsCurrentlyDownloading)
        {
            IsDownloadBarVisible = false;
            IsDownloadQueueFlyoutOpen = false;
        }
    }

    [RelayCommand]
    private async Task PlayDownloadedQueueItemAsync(DownloadQueueItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.DownloadedAudioFilePath) && File.Exists(item.DownloadedAudioFilePath))
        {
            var matchingLocal = Songs.FirstOrDefault(t => string.Equals(t.AudioSourceUri, item.DownloadedAudioFilePath, StringComparison.OrdinalIgnoreCase))
                ?? _catalogService.FindLocalOfflineEquivalent(item.Track);
            if (matchingLocal is not null)
            {
                await PlayTrackAsync(matchingLocal);
                return;
            }
        }

        await PlayTrackAsync(item.Track);
    }

    private async Task ExecuteTrackDownloadWithFormatAsync(
        TrackItem? track,
        DownloadAudioFormat? formatOverride,
        TrackDownloadCandidate? selectedCandidate = null)
    {
        IsTrackContextMenuOpen = false;
        if (track is null || track.Id == "empty_state_track")
        {
            return;
        }

        string qualityBadge = selectedCandidate?.QualityBadge ?? (formatOverride == DownloadAudioFormat.Mp3MaxQuality ? "MP3 320k" : "FLAC Lossless");
        string versionBadge = selectedCandidate?.VersionBadge ?? "Original Studio";

        var queueItem = new DownloadQueueItemViewModel(track)
        {
            ProviderBadge = qualityBadge,
            VersionBadge = versionBadge,
            StageStatus = "Starting download...",
            IsDownloading = true,
            ProgressPercent = 5.0
        };

        DownloadQueue.Insert(0, queueItem);
        OnPropertyChanged(nameof(ActiveDownloadsCount));
        OnPropertyChanged(nameof(CompletedDownloadsCount));
        OnPropertyChanged(nameof(DownloadQueueSummaryText));

        IsDownloadBarVisible = true;
        IsCurrentlyDownloading = true;
        CurrentDownloadTitle = track.Title;
        CurrentDownloadArtist = track.Artist;
        CurrentDownloadStage = "Connecting to Lossless sources...";
        CurrentDownloadBadge = qualityBadge;
        CurrentDownloadPercent = 5.0;

        try
        {
            var progress = new Progress<string>(msg =>
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    StatusMessage = msg;
                    queueItem.StageStatus = msg;
                    CurrentDownloadStage = msg;
                });
            });

            var detailedProgress = new Progress<TrackDownloadProgressReport>(rep =>
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    queueItem.ProgressPercent = Math.Clamp(rep.Percent, 0.0, 100.0);
                    CurrentDownloadPercent = queueItem.ProgressPercent;
                    if (!string.IsNullOrWhiteSpace(rep.StageStatus))
                    {
                        queueItem.StageStatus = rep.StageStatus;
                        CurrentDownloadStage = rep.StageStatus;
                    }
                    if (!string.IsNullOrWhiteSpace(rep.ProviderBadge))
                    {
                        queueItem.ProviderBadge = rep.ProviderBadge;
                        CurrentDownloadBadge = rep.ProviderBadge;
                    }
                });
            });

            var result = await _musicDownloadService.DownloadTrackAsync(
                track,
                formatOverride,
                progress,
                CancellationToken.None,
                selectedCandidate,
                detailedProgress);

            queueItem.IsDownloading = false;

            if (result.Success)
            {
                queueItem.IsCompleted = true;
                queueItem.ProgressPercent = 100.0;
                queueItem.DownloadedAudioFilePath = result.AudioFilePath;
                queueItem.DownloadedLrcFilePath = result.LrcFilePath;
                queueItem.DownloadedCanvasFilePath = result.CanvasVideoFilePath;
                queueItem.StageStatus = $"Downloaded ({result.FormatBadge}) ✓";

                CurrentDownloadStage = $"✓ Downloaded successfully: {track.Title}";
                CurrentDownloadPercent = 100.0;

                _isLibraryDirty = true;
                if (IsLibraryViewOpen)
                {
                    await RefreshCatalogAsync();
                }

                string extras = string.Empty;
                if (!string.IsNullOrWhiteSpace(result.LrcFilePath))
                {
                    extras += " + .LRC";
                }

                if (!string.IsNullOrWhiteSpace(result.CanvasVideoFilePath))
                {
                    extras += " + Apple Canvas";
                }

                StatusMessage = $"Downloaded '{track.Title}' [{result.FormatBadge}{extras}] via {result.ProviderSummary}";
            }
            else
            {
                queueItem.IsFailed = true;
                queueItem.ErrorMessage = result.ErrorMessage;
                queueItem.StageStatus = result.ErrorMessage ?? "Download error";

                CurrentDownloadStage = $"Error: {result.ErrorMessage ?? "Download failed"}";
                StatusMessage = result.ErrorMessage ?? $"Could not download '{track.Title}'.";
            }
        }
        catch (Exception ex)
        {
            queueItem.IsDownloading = false;
            queueItem.IsFailed = true;
            queueItem.ErrorMessage = ex.Message;
            queueItem.StageStatus = $"Error: {ex.Message}";

            CurrentDownloadStage = $"Error: {ex.Message}";
            _logger.LogWarning(ex, "Download failed for track '{Title}'.", track.Title);
            StatusMessage = $"Download error for '{track.Title}': {ex.Message}";
        }
        finally
        {
            TrimCompletedDownloadQueueItems();
            IsCurrentlyDownloading = DownloadQueue.Any(i => i.IsDownloading);
            OnPropertyChanged(nameof(ActiveDownloadsCount));
            OnPropertyChanged(nameof(CompletedDownloadsCount));
            OnPropertyChanged(nameof(DownloadQueueSummaryText));

            // Auto dismiss floating bar after 6 seconds if nothing is downloading and queue is closed
            _ = Task.Run(async () =>
            {
                await Task.Delay(6000);
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (!IsCurrentlyDownloading && !IsDownloadQueueFlyoutOpen)
                    {
                        IsDownloadBarVisible = false;
                    }
                });
            });
        }
    }

    private void TrimCompletedDownloadQueueItems()
    {
        int completedCount = 0;
        for (int i = 0; i < DownloadQueue.Count; i++)
        {
            var entry = DownloadQueue[i];
            if (entry.IsCompleted || entry.IsFailed)
            {
                completedCount++;
                if (completedCount > MaxCompletedDownloadQueueItems)
                {
                    DownloadQueue.RemoveAt(i);
                    i--;
                }
            }
        }
    }

    [RelayCommand]
    private void ContextMenuPlayNext()
    {
        var track = ContextMenuTrack;
        IsTrackContextMenuOpen = false;
        ClearContextMenuTrackIfAllClosed();
        if (track is not null && track.Id != "empty_state_track")
        {
            var effective = _catalogService.FindLocalOfflineEquivalent(track) ?? track;
            _audioPlayerService.EnqueuePlayNext(effective);
            QueueItemAddedNotification?.Invoke(this, effective);
            StatusMessage = $"Playing next: {effective.Title}";
        }
    }

    [RelayCommand]
    private void ContextMenuAddToQueue()
    {
        var track = ContextMenuTrack;
        IsTrackContextMenuOpen = false;
        ClearContextMenuTrackIfAllClosed();
        if (track is not null && track.Id != "empty_state_track")
        {
            var effective = _catalogService.FindLocalOfflineEquivalent(track) ?? track;
            _audioPlayerService.EnqueueTrack(effective);
            QueueItemAddedNotification?.Invoke(this, effective);
            StatusMessage = $"Added to queue: {effective.Title}";
        }
    }

    [RelayCommand]
    private async Task ContextMenuAddToPlaylistAsync()
    {
        var track = ContextMenuTrack;
        IsTrackContextMenuOpen = false;
        if (track is null || track.Id == "empty_state_track")
        {
            ClearContextMenuTrackIfAllClosed();
            return;
        }

        await EnsurePlaylistsHydratedAsync(includeSmartSuggestions: false);
        NewPlaylistTitle = string.Empty;
        NewPlaylistDescription = string.Empty;
        NewPlaylistCustomCoverPath = string.Empty;
        IsAddToPlaylistModalOpen = true;
    }

    [RelayCommand]
    private async Task ContextMenuGoToArtistAsync()
    {
        var track = ContextMenuTrack;
        IsTrackContextMenuOpen = false;
        ClearContextMenuTrackIfAllClosed();
        if (track is not null && !string.IsNullOrWhiteSpace(track.Artist))
        {
            await OpenArtistByNameAsync(track.Artist);
        }
    }

    [RelayCommand]
    private async Task ContextMenuGoToAlbumAsync()
    {
        var track = ContextMenuTrack;
        IsTrackContextMenuOpen = false;
        ClearContextMenuTrackIfAllClosed();
        if (track is not null)
        {
            string albumTitle = string.IsNullOrWhiteSpace(track.Album) ? track.Title : track.Album;
            await OpenAlbumDetailAsync(new AlbumItem
            {
                Id = $"alb_ctx_{track.Id}",
                Title = albumTitle,
                Artist = track.Artist,
                CoverArtUri = track.CoverArtUri,
                ReleaseYear = track.ReleaseYear > 0 ? track.ReleaseYear : DateTime.Now.Year,
                SourceType = track.SourceType
            });
        }
    }

    [RelayCommand]
    private void ContextMenuMoreInfo()
    {
        IsTrackContextMenuOpen = false;
        PopulateActiveSongInfoForTrack(ContextMenuTrack ?? CurrentTrack);
        OnPropertyChanged(nameof(ContextMenuTrackSourceDetail));
        IsTrackInfoModalOpen = true;
    }

    [RelayCommand]
    private async Task OpenFullscreenNowPlayingAsync()
    {
        await EnsureVisualizersInitializedAsync();
        IsFullscreenNowPlayingOpen = true;
        FullscreenTransitionRequested?.Invoke(this, true);

        if (CurrentTrack is { } activeTrack && activeTrack.Id != "empty_state_track")
        {
            if (_activeLyricsTrack is null || !IsSameTrack(_activeLyricsTrack, activeTrack))
            {
                BeginTrackLyricsPipeline(activeTrack);
            }

            if (IsAppleMusicCanvasEnabled &&
                string.IsNullOrWhiteSpace(CurrentCanvasVideoUri) &&
                !string.IsNullOrWhiteSpace(_deferredPlaybackStartTrackId))
            {
                _ = TriggerCanvasLookupForTrackAsync(activeTrack);
            }

            if (!string.Equals(_resolvedMusicVideoTrackId, activeTrack.Id, StringComparison.OrdinalIgnoreCase) &&
                !IsResolvingMusicVideo)
            {
                _ = TriggerMusicVideoLookupForTrackAsync(activeTrack);
            }
        }
    }

    [RelayCommand]
    private void CloseFullscreenNowPlaying()
    {
        IsVisualizerSelectorOpen = false;
        IsFullscreenQueueOpen = false;
        DismissFullscreenVideoError();
        FullscreenTransitionRequested?.Invoke(this, false);
    }

    [RelayCommand]
    private void ToggleFullscreenLyrics()
    {
        if (IsFullscreenSidePanelOpen && ActiveFullscreenTab == NowPlayingTab.Lyrics)
        {
            IsFullscreenSidePanelOpen = false;
            IsFullscreenLyricsVisible = false;
            IsFullscreenQueueOpen = false;
        }
        else
        {
            ActiveFullscreenTab = NowPlayingTab.Lyrics;
            IsFullscreenSidePanelOpen = true;
            IsFullscreenLyricsVisible = true;
            IsFullscreenQueueOpen = false;
            IsVisualizerSelectorOpen = false;
        }

        FullscreenLyricsLayoutChanged?.Invoke(this, IsFullscreenSidePanelOpen);
        FullscreenSideTabChanged?.Invoke(this, ActiveFullscreenTab);
    }

    [RelayCommand]
    private void SelectFullscreenTab(string? tabName)
    {
        var targetTab = string.Equals(tabName, "Queue", StringComparison.OrdinalIgnoreCase)
            ? NowPlayingTab.Queue
            : NowPlayingTab.Lyrics;

        bool wasOpen = IsFullscreenSidePanelOpen;
        ActiveFullscreenTab = targetTab;
        IsFullscreenSidePanelOpen = true;
        IsFullscreenLyricsVisible = targetTab == NowPlayingTab.Lyrics;
        IsFullscreenQueueOpen = targetTab == NowPlayingTab.Queue;
        IsVisualizerSelectorOpen = false;

        if (!wasOpen)
        {
            FullscreenLyricsLayoutChanged?.Invoke(this, true);
        }

        FullscreenSideTabChanged?.Invoke(this, ActiveFullscreenTab);
    }

    [RelayCommand]
    private void ContextMenuOpenVisualizers()
    {
        IsTrackContextMenuOpen = false;
        IsVisualizerSelectorOpen = !IsVisualizerSelectorOpen;
        if (IsVisualizerSelectorOpen && !_isVisualizersInitialized)
        {
            _ = EnsureVisualizersInitializedAsync();
        }
    }

    [RelayCommand]
    private void ToggleVisualizerSelector()
    {
        IsVisualizerSelectorOpen = !IsVisualizerSelectorOpen;
        if (IsVisualizerSelectorOpen)
        {
            IsFullscreenQueueOpen = false;
            if (!_isVisualizersInitialized)
            {
                _ = EnsureVisualizersInitializedAsync();
            }
        }
    }

    [RelayCommand]
    private void SelectVisualizer(VisualizerDefinition? preset)
    {
        if (preset is null)
        {
            return;
        }

        SelectedVisualizer = preset;
        IsVisualizerSelectorOpen = false;
        StatusMessage = $"Visualizer: {preset.Name}";
        _ = PersistVisualizerSelectionAsync(preset);
    }

    [RelayCommand]
    private async Task CycleVisualizerPresetAsync()
    {
        if (!_isVisualizersInitialized)
        {
            await EnsureVisualizersInitializedAsync();
        }

        if (AvailableVisualizers.Count == 0)
        {
            return;
        }

        var list = AvailableVisualizers.ToList();
        int idx = list.FindIndex(v => string.Equals(v.Id, SelectedVisualizer.Id, StringComparison.OrdinalIgnoreCase));
        int nextIdx = (idx + 1) % list.Count;
        SelectedVisualizer = list[nextIdx];
        StatusMessage = $"Visualizer: {SelectedVisualizer.Name}";
        _ = PersistVisualizerSelectionAsync(SelectedVisualizer);
    }

    /// <summary>
    /// Opens a native FileOpenPicker to import a custom GPU music visualizer definition (.xviz.json or .json).
    /// </summary>
    [RelayCommand]
    private async Task ImportCustomVisualizerAsync()
    {
        await EnsureVisualizersInitializedAsync();
        try
        {
            var filePicker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };
            filePicker.FileTypeFilter.Add(".json");

            if (App.MainWindowInstance is Window mainWindow)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hwnd);
            }

            var pickedFile = await filePicker.PickSingleFileAsync();
            if (pickedFile is null)
            {
                return;
            }

            const long maxVisualizerFileBytes = 1024 * 1024;
            var pickedFileInfo = new FileInfo(pickedFile.Path);
            if (!pickedFileInfo.Exists || pickedFileInfo.Length > maxVisualizerFileBytes)
            {
                StatusMessage = "Visualizer file exceeds the 1 MB maximum size limit.";
                return;
            }

            var imported = await _storageService.ImportJsonFileAsync<VisualizerDefinition>(pickedFile.Path);
            if (imported is null || string.IsNullOrWhiteSpace(imported.Name))
            {
                StatusMessage = "Invalid visualizer JSON definition.";
                return;
            }

            string sanitizedId = YouTubeSecurityPolicy.SanitizeVisualizerFileId(imported.Id);
            var normalized = imported with
            {
                Id = sanitizedId,
                IsCustomImported = true,
                GridResolution = Math.Clamp(imported.GridResolution, 16, 96),
                WaveLayers = Math.Clamp(imported.WaveLayers, 2, 14),
                ParticleCount = Math.Clamp(imported.ParticleCount, 0, 160),
                Sensitivity = Math.Clamp(imported.Sensitivity, 0.2, 3.0),
                Speed = Math.Clamp(imported.Speed, 0.1, 4.0)
            };

            var customDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XperienceMusicPlayer",
                "Visualizers");
            Directory.CreateDirectory(customDir);

            string safeFileName = sanitizedId + ".xviz.json";
            await _storageService.ExportJsonFileAsync(Path.Combine(customDir, safeFileName), normalized);

            var existingIndex = AvailableVisualizers.ToList()
                .FindIndex(v => string.Equals(v.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                AvailableVisualizers[existingIndex] = normalized;
            }
            else
            {
                AvailableVisualizers.Add(normalized);
            }

            SelectedVisualizer = normalized;
            IsVisualizerSelectorOpen = false;
            StatusMessage = $"Imported custom visualizer: {normalized.Name}";
            _ = PersistVisualizerSelectionAsync(normalized);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import custom GPU visualizer JSON.");
            StatusMessage = "Failed to import custom visualizer file.";
        }
    }

    private async Task PersistVisualizerSelectionAsync(VisualizerDefinition? preset)
    {
        if (preset is null || _isHydratingPlayerUiSettings)
        {
            return;
        }

        try
        {
            bool isEnabled = preset.RenderMode != VisualizerRenderMode.None;
            string? lastActiveId = isEnabled
                ? preset.Id
                : _persistedPlayerUiSettings.LastActiveVisualizerId;

            _persistedPlayerUiSettings = await _storageService.SetVisualizerPreferencesAsync(
                isEnabled,
                preset.Id,
                lastActiveId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist visualizer preference.");
        }
    }

    private async Task InitializeVisualizersAsync()
    {
        AvailableVisualizers.Clear();

        var builtIns = new List<VisualizerDefinition>
        {
            new()
            {
                Id = "BackdropOnly",
                Name = "Cover Backdrop Only (Off)",
                Author = "Xperience Studio",
                Description = "Displays only the atmospheric album cover backdrop without 3D spectrum geometry.",
                RenderMode = VisualizerRenderMode.None,
                UseDynamicCoverAccent = true,
                Sensitivity = 1.0,
                Speed = 1.0,
                GridResolution = 32,
                WaveLayers = 4,
                ParticleCount = 0,
                BackgroundDimOpacity = 0.55
            },
            new()
            {
                Id = "NeonTerrainPeaks",
                Name = "Neon Terrain Peaks 3D",
                Author = "Xperience Studio",
                Description = "3D perspective wave terrain with vertical spectrum needle peaks and glowing particles.",
                RenderMode = VisualizerRenderMode.NeonTerrainPeaks,
                UseDynamicCoverAccent = false,
                ColorPaletteHex = ["#FFFFBE0B", "#FF38B000", "#FF9D4EDD", "#FFFF007F", "#FF00F5D4", "#FF00BBF9"],
                Sensitivity = 1.35,
                Speed = 1.0,
                GridResolution = 58,
                WaveLayers = 7,
                ParticleCount = 68,
                BassDeformation = 1.45,
                MidDeformation = 1.15,
                TrebleDeformation = 1.30,
                BloomRadius = 14.0,
                BackgroundDimOpacity = 0.72
            },
            new()
            {
                Id = "HarmonicWireframeSphere",
                Name = "See Your Music (3D Orb)",
                Author = "Xperience Studio",
                Description = "3D rotating spherical harmonic wireframe mesh deformed by bass, vocal, and treble energy.",
                RenderMode = VisualizerRenderMode.HarmonicWireframeSphere,
                UseDynamicCoverAccent = false,
                ColorPaletteHex = ["#FFFF007F", "#FF9D4EDD", "#FF00BBF9", "#FF00F5D4", "#FF38B000", "#FFFFBE0B"],
                Sensitivity = 1.45,
                Speed = 1.15,
                GridResolution = 56,
                WaveLayers = 8,
                ParticleCount = 72,
                BassDeformation = 1.55,
                MidDeformation = 1.25,
                TrebleDeformation = 1.35,
                BloomRadius = 15.0,
                BackgroundDimOpacity = 0.78
            },
            new()
            {
                Id = "AuroraPrismaticWaves",
                Name = "Aurora Prismatic Ribbon",
                Author = "Xperience Studio",
                Description = "Flowing multi-harmonic Aurora ribbon waves synced to the active album cover accent.",
                RenderMode = VisualizerRenderMode.AuroraPrismaticWaves,
                UseDynamicCoverAccent = true,
                ColorPaletteHex = ["#FF2DD4BF", "#FF38BDF8", "#FF6366F1", "#FFA855F7", "#FFF43F5E"],
                Sensitivity = 1.30,
                Speed = 0.95,
                GridResolution = 52,
                WaveLayers = 6,
                ParticleCount = 54,
                BassDeformation = 1.35,
                MidDeformation = 1.20,
                TrebleDeformation = 1.15,
                BloomRadius = 16.0,
                BackgroundDimOpacity = 0.68
            },
            new()
            {
                Id = "RadialCyberPulsar",
                Name = "Radial Cyber Pulsar",
                Author = "Xperience Studio",
                Description = "360-degree radial cybernetic FFT equalizer with bass shockwaves and orbital particles.",
                RenderMode = VisualizerRenderMode.RadialCyberPulsar,
                UseDynamicCoverAccent = true,
                ColorPaletteHex = ["#FF00F5D4", "#FF00BBF9", "#FF9D4EDD", "#FFFF007F", "#FFFFBE0B"],
                Sensitivity = 1.50,
                Speed = 1.20,
                GridResolution = 64,
                WaveLayers = 6,
                ParticleCount = 64,
                BassDeformation = 1.50,
                MidDeformation = 1.20,
                TrebleDeformation = 1.35,
                BloomRadius = 14.0,
                BackgroundDimOpacity = 0.74
            }
        };

        foreach (var preset in builtIns)
        {
            AvailableVisualizers.Add(preset);
        }

        try
        {
            var customDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XperienceMusicPlayer",
                "Visualizers");

            if (Directory.Exists(customDir))
            {
                foreach (var file in Directory.EnumerateFiles(customDir, "*.json"))
                {
                    var custom = await _storageService.ImportJsonFileAsync<VisualizerDefinition>(file);
                    if (custom is not null && !string.IsNullOrWhiteSpace(custom.Name))
                    {
                        AvailableVisualizers.Add(custom);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enumerate custom visualizer presets.");
        }

        var currentId = SelectedVisualizer?.Id;
        string desiredId = !string.IsNullOrWhiteSpace(currentId) &&
                           !string.Equals(currentId, "BackdropOnly", StringComparison.OrdinalIgnoreCase)
            ? currentId!
            : (_persistedPlayerUiSettings.IsGpuVisualizerEnabled &&
               !string.IsNullOrWhiteSpace(_persistedPlayerUiSettings.SelectedVisualizerId)
                ? _persistedPlayerUiSettings.SelectedVisualizerId
                : "BackdropOnly");

        SelectedVisualizer = AvailableVisualizers.FirstOrDefault(v =>
            string.Equals(v.Id, desiredId, StringComparison.OrdinalIgnoreCase)) ?? AvailableVisualizers[0];
    }

    [RelayCommand]
    private Task ToggleSettingsStudioAsync()
    {
        if (IsSearchPageOpen)
        {
            _returnToSearchAfterDetail = false;
            CloseSearchOverlayIfOpen(clearQuery: false);
        }

        if (ActiveSubView == WorkspaceSubView.Settings)
        {
            IsSettingsStudioOpen = false;
            CloseDetailSubView();
            return Task.CompletedTask;
        }

        CancelAndDisposeCts(ref _artistDetailLoadCts);
        CancelAndDisposeCts(ref _albumDetailLoadCts);
        CancelAndDisposeCts(ref _albumCanvasLookupCts);
        CurrentAlbumDetailCanvasUri = null;
        AlbumDetailCanvasVideoChanged?.Invoke(this, null);

        CancelAndDisposeCts(ref _artistVideoLookupCts);
        CurrentArtistDetailVideoUri = null;

        CancelAndDisposeCts(ref _playlistRelatedLoadCts);
        IsPlaylistRelatedLoading = false;
        ClearArtistDetailState();
        ClearAlbumDetailState();
        ClearPlaylistDetailState();

        SyncLocalMusicDirectoriesList();
        SyncYouTubeMusicAccountStatus();
        IsSettingsStudioOpen = false;
        ActiveSubView = WorkspaceSubView.Settings;
        ArtistDetailHeaderVideoChanged?.Invoke(this, null);

        if (SelectedSettingsCategory == SettingsCategory.LyricsAndVisualizers)
        {
            _ = EnsureVisualizersInitializedAsync();
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void SelectSettingsCategory(string categoryName)
    {
        if (Enum.TryParse<SettingsCategory>(categoryName, ignoreCase: true, out var cat))
        {
            if (SelectedSettingsCategory != cat)
            {
                SelectedSettingsCategory = cat;
                SettingsCategoryTransitionRequested?.Invoke(this, cat);
            }

            if (cat == SettingsCategory.LyricsAndVisualizers)
            {
                _ = EnsureVisualizersInitializedAsync();
            }
        }
    }

    private void SyncLocalMusicDirectoriesList()
    {
        LocalMusicDirectories.Clear();
        foreach (var dir in _catalogService.GetLocalMusicDirectories())
        {
            LocalMusicDirectories.Add(dir);
        }

        OnPropertyChanged(nameof(EmptyLocalDirectoriesVisibility));
        OnPropertyChanged(nameof(HasLocalDirectoriesVisibility));
    }

    public void SyncYouTubeMusicAccountStatus()
    {
        IsYouTubeMusicAuthenticated = _youTubeMusicService.IsAuthenticated;
        YouTubeMusicAccountStatusText = _youTubeMusicService.ConnectedAccountLabel;
        OnPropertyChanged(nameof(HomeYtmLoginPromptVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistRefreshButtonVisibility));
    }

    private sealed class YtmPromptDismissState
    {
        public bool Dismissed { get; set; }
    }

    private static string GetYtmPromptDismissFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XperienceMusicPlayer",
        "ytm_login_prompt_dismissed.json");

    private async Task LoadYtmPromptDismissStateAsync()
    {
        try
        {
            string filePath = GetYtmPromptDismissFilePath();
            if (File.Exists(filePath))
            {
                var state = await _storageService.ImportJsonFileAsync<YtmPromptDismissState>(filePath);
                if (state is not null)
                {
                    IsYtmLoginBannerDismissed = state.Dismissed;
                }
            }
        }
        catch
        {
            // Ignore corrupt or missing prompt dismiss file
        }
    }

    [RelayCommand]
    private void SnoozeYtmLoginPrompt()
    {
        IsYtmLoginBannerSnoozed = true;
    }

    [RelayCommand]
    private async Task DismissYtmLoginPromptAsync()
    {
        IsYtmLoginBannerDismissed = true;
        try
        {
            string filePath = GetYtmPromptDismissFilePath();
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await _storageService.ExportJsonFileAsync(filePath, new YtmPromptDismissState { Dismissed = true });
        }
        catch
        {
            // Ignore persistence errors
        }
    }

    [RelayCommand]
    private void OpenNewReleasesGrid()
    {
        CurrentSection = NavigationSection.Home;
        ActiveHomeGridCategory = HomeGridCategory.NewReleases;
        ActiveNewReleasesFilter = NewReleasesFilter.All;
        RadioStationsGridItems.Clear();
        ApplyNewReleasesFilter();
        ActiveSubView = WorkspaceSubView.HomeCategoryGrid;
    }

    [RelayCommand]
    private void OpenRadioStationsGrid()
    {
        CurrentSection = NavigationSection.Home;
        ActiveHomeGridCategory = HomeGridCategory.RadioStations;
        FilteredNewReleasesGridItems.Clear();
        PopulateRadioStationsGrid();
        ActiveSubView = WorkspaceSubView.HomeCategoryGrid;
    }

    [RelayCommand]
    private void ToggleHomeCategoryGridMode()
    {
        ActiveHomeGridCategory = ActiveHomeGridCategory == HomeGridCategory.NewReleases
            ? HomeGridCategory.RadioStations
            : HomeGridCategory.NewReleases;
        if (ActiveHomeGridCategory == HomeGridCategory.NewReleases)
        {
            RadioStationsGridItems.Clear();
            ApplyNewReleasesFilter();
        }
        else
        {
            FilteredNewReleasesGridItems.Clear();
            PopulateRadioStationsGrid();
        }
    }

    private void PopulateRadioStationsGrid()
    {
        RadioStationsGridItems.Clear();
        foreach (var radio in _allRadioStations)
        {
            RadioStationsGridItems.Add(radio);
        }
    }

    [RelayCommand]
    private void SelectNewReleasesFilter(string? filterKey)
    {
        if (Enum.TryParse<NewReleasesFilter>(filterKey, ignoreCase: true, out var parsed))
        {
            ActiveNewReleasesFilter = parsed;
            ApplyNewReleasesFilter();
        }
    }

    private void ApplyNewReleasesFilter()
    {
        FilteredNewReleasesGridItems.Clear();
        IEnumerable<NewReleaseItem> source = ActiveNewReleasesFilter switch
        {
            NewReleasesFilter.Albums => _allNewReleases.Where(r => r.IsAlbum),
            NewReleasesFilter.Singles => _allNewReleases.Where(r => !r.IsAlbum),
            _ => _allNewReleases
        };

        foreach (var item in source.Take(24))
        {
            FilteredNewReleasesGridItems.Add(item);
        }
    }

    [RelayCommand]
    private async Task OpenMoreLikeSeedAlbumAsync(AlbumItem? seed)
    {
        if (seed is not null)
        {
            await OpenAlbumDetailAsync(seed);
        }
    }

    private void CancelPendingFeedResolve()
    {
        bool hadPending = Volatile.Read(ref _feedResolveCts) is not null;
        CancelAndDisposeCts(ref _feedResolveCts);
        if (hadPending)
        {
            IsLoadingDiscovery = false;
        }
    }

    private CancellationTokenSource ResetFeedResolveCts() => ReplaceCts(ref _feedResolveCts);

    public async Task PlayOrOpenTopPickAsync(TopPickItem? item)
    {
        if (item is null)
        {
            return;
        }

        CancelPendingFeedResolve();

        switch (item.Kind)
        {
            case TopPickKind.ArtistSpotlight:
                string? artistName = !string.IsNullOrWhiteSpace(item.ArtistName)
                    ? item.ArtistName.Trim()
                    : (!string.IsNullOrWhiteSpace(item.Title) ? item.Title.Trim() : null);
                if (!string.IsNullOrWhiteSpace(artistName))
                {
                    await OpenArtistByNameAsync(artistName, item.ArtworkUri, item.BrowseId);
                }

                break;

            case TopPickKind.FeaturedPlaylist:
                string? featuredPlId = !string.IsNullOrWhiteSpace(item.PlaylistId) ? item.PlaylistId.Trim() : null;
                string? featuredBrowseId = !string.IsNullOrWhiteSpace(item.BrowseId) ? item.BrowseId.Trim() : null;
                if (featuredPlId is not null || featuredBrowseId is not null)
                {
                    await PlayRecommendedPlaylistAsync(
                        new RecommendedPlaylistItem(
                            Id: item.Id,
                            Title: item.Title,
                            Subtitle: item.Subtitle,
                            ArtworkUri: item.ArtworkUri,
                            PlaylistId: featuredPlId ?? (featuredBrowseId!.StartsWith("VL", StringComparison.OrdinalIgnoreCase)
                                ? featuredBrowseId[2..]
                                : featuredBrowseId),
                            BrowseId: featuredBrowseId),
                        fallbackTrack: item.SeedTrack);
                }
                else if (item.SeedTrack is not null)
                {
                    await PlaySingleTrackAsync(item.SeedTrack);
                }
                else if (!string.IsNullOrWhiteSpace(item.VideoId))
                {
                    await PlaySingleTrackAsync(BuildTrackFromTopPick(item));
                }

                break;

            case TopPickKind.NewRelease:
                string? nrBrowseId = !string.IsNullOrWhiteSpace(item.BrowseId) ? item.BrowseId.Trim() : null;
                string? nrAlbumTitle = !string.IsNullOrWhiteSpace(item.AlbumTitle) ? item.AlbumTitle.Trim() : null;
                if (nrBrowseId is not null || nrAlbumTitle is not null)
                {
                    await OpenAlbumDetailAsync(BuildAlbumFromTopPick(item));
                }
                else if (item.SeedTrack is not null)
                {
                    await PlaySingleTrackAsync(item.SeedTrack);
                }
                else if (!string.IsNullOrWhiteSpace(item.VideoId))
                {
                    await PlaySingleTrackAsync(BuildTrackFromTopPick(item));
                }
                else if (!string.IsNullOrWhiteSpace(item.PlaylistId))
                {
                    await PlayRecommendedPlaylistAsync(
                        new RecommendedPlaylistItem(
                            Id: item.Id,
                            Title: item.Title,
                            Subtitle: item.Subtitle,
                            ArtworkUri: item.ArtworkUri,
                            PlaylistId: item.PlaylistId.Trim(),
                            BrowseId: item.BrowseId),
                        fallbackTrack: item.SeedTrack);
                }

                break;

            case TopPickKind.PersonalStation:
            default:
                string? stationPlId = !string.IsNullOrWhiteSpace(item.PlaylistId) ? item.PlaylistId.Trim() : null;
                string? stationBrowseId = !string.IsNullOrWhiteSpace(item.BrowseId) ? item.BrowseId.Trim() : null;
                if (stationPlId is not null)
                {
                    await PlayRadioStationAsync(
                        new RadioStationItem(
                            Id: item.Id,
                            Title: item.Title,
                            Subtitle: item.Subtitle,
                            ArtworkUri: item.ArtworkUri,
                            PlaylistId: stationPlId,
                            SeedVideoId: !string.IsNullOrWhiteSpace(item.VideoId) ? item.VideoId.Trim() : null),
                        fallbackTrack: item.SeedTrack);
                }
                else if (item.SeedTrack is not null)
                {
                    await PlaySingleTrackAsync(item.SeedTrack);
                }
                else if (!string.IsNullOrWhiteSpace(item.VideoId))
                {
                    await PlaySingleTrackAsync(BuildTrackFromTopPick(item));
                }
                else if (stationBrowseId is not null)
                {
                    if (stationBrowseId.StartsWith("UC", StringComparison.Ordinal))
                    {
                        string targetArtist = !string.IsNullOrWhiteSpace(item.ArtistName) ? item.ArtistName.Trim() : item.Title;
                        await OpenArtistByNameAsync(targetArtist, item.ArtworkUri, stationBrowseId);
                    }
                    else
                    {
                        await OpenAlbumDetailAsync(BuildAlbumFromTopPick(item));
                    }
                }

                break;
        }
    }

    public async Task PlayRecommendedPlaylistAsync(RecommendedPlaylistItem? playlist, TrackItem? fallbackTrack = null)
    {
        if (playlist is null)
        {
            return;
        }

        var cts = ResetFeedResolveCts();
        var token = cts.Token;

        try
        {
            IsLoadingDiscovery = true;
            var tracks = await _youTubeMusicService.ResolvePlaylistTracksAsync(playlist, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (tracks.Count > 0)
            {
                await SetQueueAndPlayAsync(tracks, startIndex: 0, autoPlay: true);
                StatusMessage = $"Playing playlist \"{playlist.Title}\" ({tracks.Count} tracks)";
            }
            else if (fallbackTrack is not null)
            {
                await PlaySingleTrackAsync(fallbackTrack);
            }
            else
            {
                StatusMessage = $"Could not load tracks for \"{playlist.Title}\".";
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by another feed interaction
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to play playlist '{Title}'.", playlist.Title);
            if (!token.IsCancellationRequested && fallbackTrack is not null)
            {
                await PlaySingleTrackAsync(fallbackTrack);
            }
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _feedResolveCts, null, cts), cts))
            {
                IsLoadingDiscovery = false;
                cts.Dispose();
            }
        }
    }

    public async Task PlayRadioStationAsync(RadioStationItem? station, TrackItem? fallbackTrack = null)
    {
        if (station is null)
        {
            return;
        }

        var cts = ResetFeedResolveCts();
        var token = cts.Token;

        try
        {
            IsLoadingDiscovery = true;
            var tracks = await _youTubeMusicService.ResolveRadioStationTracksAsync(station, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (tracks.Count > 0)
            {
                await SetQueueAndPlayAsync(tracks, startIndex: 0, autoPlay: true);
                StatusMessage = $"Playing station \"{station.Title}\" ({tracks.Count} tracks)";
            }
            else if (fallbackTrack is not null)
            {
                await PlaySingleTrackAsync(fallbackTrack);
            }
            else
            {
                StatusMessage = $"Could not tune station \"{station.Title}\".";
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by another feed interaction
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to play radio station '{Title}'.", station.Title);
            if (!token.IsCancellationRequested && fallbackTrack is not null)
            {
                await PlaySingleTrackAsync(fallbackTrack);
            }
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _feedResolveCts, null, cts), cts))
            {
                IsLoadingDiscovery = false;
                cts.Dispose();
            }
        }
    }

    public async Task OpenOrPlayNewReleaseAsync(NewReleaseItem? release)
    {
        if (release is null)
        {
            return;
        }

        CancelPendingFeedResolve();

        if (release.IsAlbum)
        {
            await OpenAlbumDetailAsync(BuildAlbumFromNewRelease(release));
            return;
        }

        if (!string.IsNullOrWhiteSpace(release.VideoId))
        {
            string cleanVid = TrackDeduplicationHelper.StripYtmPrefix(release.VideoId);
            var singleTrack = new TrackItem
            {
                Id = !string.IsNullOrWhiteSpace(cleanVid) ? $"ytm_{cleanVid}" : release.Id,
                YouTubeVideoId = cleanVid,
                Title = release.Title,
                Artist = string.IsNullOrWhiteSpace(release.Artist) ? "YouTube Music" : release.Artist.Trim(),
                Album = release.Title,
                Duration = TimeSpan.FromMinutes(3.5),
                CoverArtUri = release.ArtworkUri,
                AudioSourceUri = !string.IsNullOrWhiteSpace(cleanVid) ? $"ytmusic://{cleanVid}" : string.Empty,
                SourceType = MediaSourceType.Online,
                QualityBadge = "HQ AAC",
                Genre = "New Release",
                ReleaseYear = int.TryParse(release.Year, out int yr) ? yr : DateTime.Now.Year
            };
            await PlaySingleTrackAsync(singleTrack);
            return;
        }

        if (!string.IsNullOrWhiteSpace(release.BrowseId))
        {
            await OpenAlbumDetailAsync(BuildAlbumFromNewRelease(release));
        }
    }

    private static AlbumItem BuildAlbumFromNewRelease(NewReleaseItem release)
    {
        int parsedYear = int.TryParse(release.Year, out int yr)
            ? yr
            : ExtractYearOrDefault(release.Subtitle, DateTime.Now.Year);
        string browseId = release.BrowseId?.Trim() ?? string.Empty;
        return new AlbumItem
        {
            Id = !string.IsNullOrWhiteSpace(browseId) ? $"ytm_alb_{browseId}" : release.Id,
            BrowseId = browseId,
            Title = release.Title,
            Artist = string.IsNullOrWhiteSpace(release.Artist) ? "YouTube Music" : release.Artist.Trim(),
            Subtitle = release.Subtitle,
            ReleaseYear = parsedYear,
            CoverArtUri = release.ArtworkUri,
            SourceType = MediaSourceType.Online
        };
    }

    private static AlbumItem BuildAlbumFromTopPick(TopPickItem item)
    {
        string artist = !string.IsNullOrWhiteSpace(item.ArtistName)
            ? item.ArtistName.Trim()
            : ExtractArtistBeforeBullet(item.Subtitle, "YouTube Music");
        string browseId = item.BrowseId?.Trim() ?? string.Empty;
        return new AlbumItem
        {
            Id = !string.IsNullOrWhiteSpace(browseId) ? $"ytm_alb_{browseId}" : item.Id,
            BrowseId = browseId,
            Title = !string.IsNullOrWhiteSpace(item.AlbumTitle) ? item.AlbumTitle.Trim() : item.Title,
            Artist = artist,
            Subtitle = item.Subtitle,
            CoverArtUri = item.ArtworkUri,
            ReleaseYear = ExtractYearOrDefault(item.Subtitle, DateTime.Now.Year),
            SourceType = MediaSourceType.Online
        };
    }

    private static TrackItem BuildTrackFromTopPick(TopPickItem item)
    {
        string cleanVid = TrackDeduplicationHelper.StripYtmPrefix(item.VideoId);
        string artist = !string.IsNullOrWhiteSpace(item.ArtistName)
            ? item.ArtistName.Trim()
            : ExtractArtistBeforeBullet(item.Subtitle, "YouTube Music");
        return new TrackItem
        {
            Id = !string.IsNullOrWhiteSpace(cleanVid) ? $"ytm_{cleanVid}" : item.Id,
            YouTubeVideoId = cleanVid,
            Title = item.Title,
            Artist = artist,
            Album = !string.IsNullOrWhiteSpace(item.AlbumTitle) ? item.AlbumTitle.Trim() : item.Title,
            Duration = TimeSpan.FromMinutes(3.5),
            CoverArtUri = item.ArtworkUri,
            AudioSourceUri = !string.IsNullOrWhiteSpace(cleanVid) ? $"ytmusic://{cleanVid}" : string.Empty,
            SourceType = MediaSourceType.Online,
            QualityBadge = "HQ AAC",
            Genre = "Streaming",
            ReleaseYear = ExtractYearOrDefault(item.Subtitle, DateTime.Now.Year)
        };
    }

    private static string ExtractArtistBeforeBullet(string? subtitle, string fallback)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return fallback;
        }

        int bulletIdx = subtitle.IndexOf('•');
        string head = (bulletIdx >= 0 ? subtitle[..bulletIdx] : subtitle).Trim();
        return string.IsNullOrWhiteSpace(head) ? fallback : head;
    }

    private static int ExtractYearOrDefault(string? text, int fallbackYear)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            for (int i = 0; i <= text.Length - 4; i++)
            {
                if ((text[i] == '1' && text[i + 1] == '9' || text[i] == '2' && text[i + 1] == '0') &&
                    char.IsDigit(text[i + 2]) && char.IsDigit(text[i + 3]) &&
                    (i == 0 || !char.IsDigit(text[i - 1])) &&
                    (i + 4 == text.Length || !char.IsDigit(text[i + 4])) &&
                    int.TryParse(text.AsSpan(i, 4), out int parsed))
                {
                    return parsed;
                }
            }
        }

        return fallbackYear;
    }

    [RelayCommand]
    private void OpenYouTubeMusicWebSignIn()
    {
        InteractiveYouTubeLoginRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task ConnectYouTubeMusicCookieAsync()
    {
        if (string.IsNullOrWhiteSpace(YouTubeMusicCookieHeaderInput))
        {
            StatusMessage = "Please paste a valid YouTube Music Cookie string containing SAPISID.";
            return;
        }

        bool wasAuthenticated = _youTubeMusicService.IsAuthenticated;
        string previousAccount = _youTubeMusicService.ConnectedAccountLabel;

        string? label = string.IsNullOrWhiteSpace(YouTubeAccountLabelInput) ||
                        string.Equals(YouTubeAccountLabelInput.Trim(), "My YouTube Music", StringComparison.OrdinalIgnoreCase)
            ? null
            : YouTubeAccountLabelInput.Trim();

        await _youTubeMusicService.ConnectAccountCookieAsync(
            YouTubeMusicCookieHeaderInput,
            accountLabel: label);
        YouTubeMusicCookieHeaderInput = string.Empty;
        SyncYouTubeMusicAccountStatus();
        StatusMessage = _youTubeMusicService.IsAuthenticated
            ? "Connected YouTube Music Account! Refreshing personalized Home feed..."
            : "Cookie did not contain SAPISID token. Try signing in via Browser.";

        if (_youTubeMusicService.IsAuthenticated)
        {
            bool newlySignedIn = !wasAuthenticated ||
                                 !string.Equals(previousAccount, _youTubeMusicService.ConnectedAccountLabel, StringComparison.OrdinalIgnoreCase);
            if (newlySignedIn && !IsYouTubePlaylistImportModalOpen)
            {
                _ = OpenYouTubePlaylistImportModalAsync();
            }

            await RefreshHomeDiscoveryFeedAsync();
        }
    }

    public async Task ConnectYouTubeBrowserSessionAsync(
        string cookieHeader,
        string? visitorData,
        string? dataSyncId)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return;
        }

        bool wasAuthenticated = _youTubeMusicService.IsAuthenticated;
        string previousAccount = _youTubeMusicService.ConnectedAccountLabel;

        await _youTubeMusicService.ConnectAccountCookieAsync(
            cookieHeader,
            accountLabel: null,
            visitorData: visitorData,
            dataSyncId: dataSyncId);
        SyncYouTubeMusicAccountStatus();
        StatusMessage = _youTubeMusicService.IsAuthenticated
            ? "Connected YouTube Music Account! Refreshing personalized Home feed..."
            : (_youTubeMusicService.IsSessionExpired
                ? "Browser cookies were rejected or expired by YouTube. Please complete sign-in inside the browser."
                : "Browser session did not contain SAPISID token. Make sure you finished signing in on music.youtube.com.");

        if (_youTubeMusicService.IsAuthenticated)
        {
            bool newlySignedIn = !wasAuthenticated ||
                                 !string.Equals(previousAccount, _youTubeMusicService.ConnectedAccountLabel, StringComparison.OrdinalIgnoreCase);
            if (newlySignedIn && !IsYouTubePlaylistImportModalOpen)
            {
                _ = OpenYouTubePlaylistImportModalAsync();
            }

            await RefreshHomeDiscoveryFeedAsync();
        }
    }

    [RelayCommand]
    private async Task DisconnectYouTubeMusicAccountAsync()
    {
        await _youTubeMusicService.DisconnectAccountAsync();
        YouTubeMusicCookieHeaderInput = string.Empty;
        YouTubeAccountLabelInput = string.Empty;
        YouTubeLibraryImportItems.Clear();
        SyncYouTubeMusicAccountStatus();
        StatusMessage = "Disconnected YouTube Music account.";
        await RefreshHomeDiscoveryFeedAsync();
    }

    public async Task RemoveLocalDirectoryAsync(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        await _catalogService.RemoveLocalMusicDirectoryAsync(folderPath);
        SyncLocalMusicDirectoriesList();
        _isLibraryDirty = true;
        if (IsLibraryViewOpen)
        {
            await RefreshCatalogAsync();
        }

        StatusMessage = $"Removed music directory: {folderPath}";
    }

    public async Task RefreshPlaylistsAsync(
        CancellationToken cancellationToken = default,
        bool refreshSmartSuggestions = true)
    {
        try
        {
            var list = await _catalogService.GetPlaylistsAsync(cancellationToken);
            Playlists.Clear();
            PlaylistCards.Clear();
            foreach (var pl in list)
            {
                Playlists.Add(pl);
                PlaylistCards.Add(new PlaylistCardViewModel(pl));
            }

            _isPlaylistsHydrated = true;
            _isPlaylistsDirty = false;

            OnPropertyChanged(nameof(EmptyPlaylistsVisibility));
            OnPropertyChanged(nameof(HasPlaylistsVisibility));
            OnPropertyChanged(nameof(PlaylistsStatsSubtitle));
            OnPropertyChanged(nameof(PlaylistsTotalDurationText));

            if (refreshSmartSuggestions)
            {
                await RefreshSmartSuggestedPlaylistsAsync(cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh user playlists.");
        }
    }

    /// <summary>
    /// Immediately starts playback of a user playlist (used by the Quick-Play button on playlist cards).
    /// </summary>
    public async Task PlayPlaylistDirectlyAsync(UserPlaylist? playlist)
    {
        if (playlist is null || playlist.Tracks.Count == 0)
        {
            return;
        }

        await PlayTrackFromListAsync(playlist.Tracks[0], playlist.Tracks.ToList());
    }

    [RelayCommand]
    private async Task OpenCreatePlaylistModalAsync()
    {
        await EnsurePlaylistsHydratedAsync(includeSmartSuggestions: false);
        ContextMenuTrack = null;
        NewPlaylistTitle = string.Empty;
        NewPlaylistDescription = string.Empty;
        NewPlaylistCustomCoverPath = string.Empty;
        IsAddToPlaylistModalOpen = true;
    }

    [RelayCommand]
    private void CloseAddToPlaylistModal()
    {
        IsAddToPlaylistModalOpen = false;
        ClearContextMenuTrackIfAllClosed();
    }

    [RelayCommand]
    private async Task PickNewPlaylistCoverImageAsync()
    {
        try
        {
            var filePicker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail
            };
            filePicker.FileTypeFilter.Add(".jpg");
            filePicker.FileTypeFilter.Add(".jpeg");
            filePicker.FileTypeFilter.Add(".png");
            filePicker.FileTypeFilter.Add(".webp");

            if (App.MainWindowInstance is Window mainWindow)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hwnd);
            }

            var picked = await filePicker.PickSingleFileAsync();
            if (picked is not null)
            {
                NewPlaylistCustomCoverPath = picked.Path;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not pick custom playlist cover photo.");
        }
    }

    [RelayCommand]
    private async Task CreateNewPlaylistFromModalAsync()
    {
        string title = string.IsNullOrWhiteSpace(NewPlaylistTitle) ? $"Playlist #{Playlists.Count + 1}" : NewPlaylistTitle.Trim();
        TrackItem? initialTrack = ContextMenuTrack is not null && ContextMenuTrack.Id != "empty_state_track" && !string.IsNullOrWhiteSpace(ContextMenuTrack.Title)
            ? ContextMenuTrack
            : null;

        var created = await _catalogService.CreatePlaylistAsync(
            title,
            NewPlaylistDescription,
            NewPlaylistCustomCoverPath,
            initialTrack);

        IsAddToPlaylistModalOpen = false;
        ClearContextMenuTrackIfAllClosed();
        NewPlaylistTitle = string.Empty;
        NewPlaylistDescription = string.Empty;
        NewPlaylistCustomCoverPath = string.Empty;
        _isPlaylistsDirty = true;
        await RefreshPlaylistsAsync(refreshSmartSuggestions: false);

        StatusMessage = initialTrack is not null
            ? $"Created playlist '{created.Title}' and added '{initialTrack.Title}'"
            : $"Created playlist '{created.Title}'";
    }

    public async Task AddPendingTrackToPlaylistAsync(UserPlaylist? playlist)
    {
        if (playlist is null)
        {
            return;
        }

        var track = ContextMenuTrack;
        IsAddToPlaylistModalOpen = false;
        ClearContextMenuTrackIfAllClosed();

        if (track is null || track.Id == "empty_state_track" || string.IsNullOrWhiteSpace(track.Title))
        {
            await OpenPlaylistDetailAsync(playlist);
            return;
        }

        var updated = await _catalogService.AddTrackToPlaylistAsync(playlist.Id, track);
        _isPlaylistsDirty = true;
        await RefreshPlaylistsAsync(refreshSmartSuggestions: false);
        if (updated is not null && ActiveSubView == WorkspaceSubView.PlaylistDetail && string.Equals(CurrentPlaylistDetail.Id, updated.Id, StringComparison.OrdinalIgnoreCase))
        {
            await OpenPlaylistDetailAsync(updated);
        }

        StatusMessage = $"Added '{track.Title}' to '{playlist.Title}'";
    }

    public async Task OpenPlaylistDetailAsync(UserPlaylist? playlist)
    {
        if (playlist is null)
        {
            return;
        }

        if (IsSearchPageOpen)
        {
            _returnToSearchAfterDetail = true;
            CloseSearchOverlayIfOpen(clearQuery: false);
        }

        await EnsurePlaylistsHydratedAsync(includeSmartSuggestions: false);
        var fresh = Playlists.FirstOrDefault(p => string.Equals(p.Id, playlist.Id, StringComparison.OrdinalIgnoreCase)) ?? playlist;
        CurrentPlaylistDetail = fresh;
        CurrentPlaylistCard = new PlaylistCardViewModel(fresh);
        IsPlaylistDescriptionExpanded = false;

        PlaylistDetailTracks.Clear();
        PlaylistDetailTrackRows.Clear();
        for (int i = 0; i < fresh.Tracks.Count; i++)
        {
            var tr = fresh.Tracks[i];
            PlaylistDetailTracks.Add(tr);
            PlaylistDetailTrackRows.Add(new PlaylistTrackRowItem(i + 1, tr));
        }

        SyncLikedAndPlaylistPlayingTrackState();
        OnPropertyChanged(nameof(EmptyPlaylistDetailTracksVisibility));
        OnPropertyChanged(nameof(HasPlaylistDetailTracksVisibility));
        OnPropertyChanged(nameof(PlaylistDetailTrackCountText));
        OnPropertyChanged(nameof(PlaylistDetailDurationText));
        OnPropertyChanged(nameof(PlaylistDescriptionVisibility));
        OnPropertyChanged(nameof(PlaylistDescriptionToggleVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistSaveToLibraryVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistSavedBadgeVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistDeleteButtonVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistTypeAccentLabel));
        OnPropertyChanged(nameof(CurrentPlaylistYouTubeBadgeVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistRefreshButtonVisibility));
        OnPropertyChanged(nameof(CurrentPlaylistDeleteButtonTooltip));

        CancelAndDisposeCts(ref _artistDetailLoadCts);
        CancelAndDisposeCts(ref _albumDetailLoadCts);
        CancelAndDisposeCts(ref _albumCanvasLookupCts);
        CurrentAlbumDetailCanvasUri = null;
        AlbumDetailCanvasVideoChanged?.Invoke(this, null);

        CancelAndDisposeCts(ref _artistVideoLookupCts);
        CurrentArtistDetailVideoUri = null;
        ClearArtistDetailState();
        ClearAlbumDetailState();

        ActiveSubView = WorkspaceSubView.PlaylistDetail;
        ArtistDetailHeaderVideoChanged?.Invoke(this, null);
        PlaylistAmbientSourceChanged?.Invoke(this, CurrentPlaylistCard?.PrimaryCoverArtUri);

        _ = LoadPlaylistRelatedTracksAsync(fresh);
    }

    /// <summary>
    /// Plays a recommended track from the Playlist Detail "Related to this playlist" carousel,
    /// queueing the visible related tracks list.
    /// </summary>
    [RelayCommand]
    public async Task PlayPlaylistRelatedTrackAsync(TrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        var list = PlaylistRelatedTracks.ToList();
        if (list.Count == 0)
        {
            list = [track];
        }

        await PlayTrackFromListAsync(track, list);
    }

    private async Task LoadPlaylistRelatedTracksAsync(UserPlaylist playlist)
    {
        const int MaxPlaylistRelatedTracks = 16;

        CancelAndDisposeCts(ref _playlistRelatedLoadCts);

        if (playlist is null || playlist.Tracks.Count == 0)
        {
            await RunOnUiThreadAsync(() =>
            {
                PlaylistRelatedTracks.Clear();
                IsPlaylistRelatedLoading = false;
                OnPropertyChanged(nameof(PlaylistRelatedSkeletonVisibility));
                OnPropertyChanged(nameof(HasPlaylistRelatedVisibility));
                OnPropertyChanged(nameof(PlaylistRelatedSectionVisibility));
            });
            return;
        }

        string playlistId = playlist.Id;
        var tracksSnapshot = playlist.Tracks.ToList();
        string fingerprint = string.Join("|", tracksSnapshot.Select(t => t.Id));

        if (_playlistRelatedCache.TryGet(playlistId, out var cached) &&
            string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            await RunOnUiThreadAsync(() =>
            {
                if (ActiveSubView != WorkspaceSubView.PlaylistDetail ||
                    !string.Equals(CurrentPlaylistDetail?.Id, playlistId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                SyncTracksLikedStateAgainstLikedSongs(cached.Tracks);
                PlaylistRelatedTracks.Clear();
                foreach (var item in cached.Tracks)
                {
                    PlaylistRelatedTracks.Add(item);
                }

                SyncTrackCollectionPlayingState(PlaylistRelatedTracks);
                IsPlaylistRelatedLoading = false;
                OnPropertyChanged(nameof(PlaylistRelatedSkeletonVisibility));
                OnPropertyChanged(nameof(HasPlaylistRelatedVisibility));
                OnPropertyChanged(nameof(PlaylistRelatedSectionVisibility));
            });
            return;
        }

        var cts = ReplaceCts(ref _playlistRelatedLoadCts);
        var ct = cts.Token;

        await RunOnUiThreadAsync(() =>
        {
            if (!ReferenceEquals(_playlistRelatedLoadCts, cts))
            {
                return;
            }

            PlaylistRelatedTracks.Clear();
            IsPlaylistRelatedLoading = true;
            OnPropertyChanged(nameof(PlaylistRelatedSkeletonVisibility));
            OnPropertyChanged(nameof(HasPlaylistRelatedVisibility));
            OnPropertyChanged(nameof(PlaylistRelatedSectionVisibility));
        });

        try
        {
            var seeds = SelectDiversePlaylistSeeds(tracksSnapshot, maxSeeds: 3);
            var excludedIds = BuildTrackExclusionIds(tracksSnapshot);
            var excludedTitleArtistKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tr in tracksSnapshot)
            {
                if (!string.IsNullOrWhiteSpace(tr.Title))
                {
                    excludedTitleArtistKeys.Add(TrackDeduplicationHelper.BuildTitlePrimaryArtistKey(tr.Title, tr.Artist));
                }
            }

            var rawCandidates = await _youTubeMusicService.FetchSimilarTracksForQueueAsync(
                seeds,
                excludedIds,
                maxCount: 24,
                cancellationToken: ct);
            ct.ThrowIfCancellationRequested();

            var filtered = new List<TrackItem>(MaxPlaylistRelatedTracks);
            foreach (var candidate in rawCandidates)
            {
                if (candidate is null || string.IsNullOrWhiteSpace(candidate.Title))
                {
                    continue;
                }

                if (IsTrackExcludedById(candidate, excludedIds))
                {
                    continue;
                }

                string titleArtistKey = TrackDeduplicationHelper.BuildTitlePrimaryArtistKey(candidate.Title, candidate.Artist);
                if (!excludedTitleArtistKeys.Add(titleArtistKey))
                {
                    continue;
                }

                AddTrackIdsToSet(candidate, excludedIds);
                filtered.Add(candidate);
                if (filtered.Count >= MaxPlaylistRelatedTracks)
                {
                    break;
                }
            }

            var warmed = await WarmTrackCoversAsync(filtered, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                if (ct.IsCancellationRequested ||
                    !ReferenceEquals(_playlistRelatedLoadCts, cts) ||
                    ActiveSubView != WorkspaceSubView.PlaylistDetail ||
                    !string.Equals(CurrentPlaylistDetail?.Id, playlistId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                SyncTracksLikedStateAgainstLikedSongs(warmed);
                PlaylistRelatedTracks.Clear();
                foreach (var item in warmed)
                {
                    PlaylistRelatedTracks.Add(item);
                }

                SyncTrackCollectionPlayingState(PlaylistRelatedTracks);
                if (warmed.Count > 0)
                {
                    _playlistRelatedCache.Set(playlistId, (fingerprint, warmed));
                }

                OnPropertyChanged(nameof(PlaylistRelatedSkeletonVisibility));
                OnPropertyChanged(nameof(HasPlaylistRelatedVisibility));
                OnPropertyChanged(nameof(PlaylistRelatedSectionVisibility));
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by playlist switch or navigation away
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load related tracks for playlist '{PlaylistId}'.", playlistId);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                if (ReferenceEquals(_playlistRelatedLoadCts, cts))
                {
                    IsPlaylistRelatedLoading = false;
                    OnPropertyChanged(nameof(PlaylistRelatedSkeletonVisibility));
                    OnPropertyChanged(nameof(HasPlaylistRelatedVisibility));
                    OnPropertyChanged(nameof(PlaylistRelatedSectionVisibility));
                }
            });
        }
    }

    private static List<TrackItem> SelectDiversePlaylistSeeds(IReadOnlyList<TrackItem> tracks, int maxSeeds)
    {
        var seeds = new List<TrackItem>(Math.Min(maxSeeds, tracks.Count));
        if (tracks.Count == 0 || maxSeeds <= 0)
        {
            return seeds;
        }

        // Probe spread across the playlist: newest/last, first, middle, then remaining tracks
        var probeIndices = new List<int>(tracks.Count);
        var addedIndices = new HashSet<int>();
        void AddIndex(int idx)
        {
            if (idx >= 0 && idx < tracks.Count && addedIndices.Add(idx))
            {
                probeIndices.Add(idx);
            }
        }

        AddIndex(tracks.Count - 1);
        AddIndex(0);
        AddIndex(tracks.Count / 2);
        for (int i = 0; i < tracks.Count; i++)
        {
            AddIndex(i);
        }

        var usedIndices = new HashSet<int>();
        var seenArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: prioritize distinct primary artists
        foreach (int idx in probeIndices)
        {
            var track = tracks[idx];
            if (track is null || string.IsNullOrWhiteSpace(track.Title))
            {
                continue;
            }

            string primaryArtist = TrackDeduplicationHelper.PrimaryArtistName(track.Artist);
            if (!string.IsNullOrWhiteSpace(primaryArtist) && seenArtists.Add(primaryArtist))
            {
                seeds.Add(track);
                usedIndices.Add(idx);
                if (seeds.Count >= maxSeeds)
                {
                    return seeds;
                }
            }
        }

        // Pass 2: fill any remaining seed slots if fewer than maxSeeds distinct artists exist
        foreach (int idx in probeIndices)
        {
            if (usedIndices.Contains(idx))
            {
                continue;
            }

            var track = tracks[idx];
            if (track is null || string.IsNullOrWhiteSpace(track.Title))
            {
                continue;
            }

            seeds.Add(track);
            usedIndices.Add(idx);
            if (seeds.Count >= maxSeeds)
            {
                break;
            }
        }

        return seeds;
    }

    private static HashSet<string> BuildTrackExclusionIds(IEnumerable<TrackItem> tracks)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            if (track is not null)
            {
                AddTrackIdsToSet(track, ids);
            }
        }

        return ids;
    }

    private static void AddTrackIdsToSet(TrackItem track, HashSet<string> destination)
    {
        if (!string.IsNullOrWhiteSpace(track.Id))
        {
            destination.Add(track.Id.Trim());
        }

        if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId))
        {
            string vid = track.YouTubeVideoId.Trim();
            destination.Add(vid);
            destination.Add($"ytm_{vid}");
        }

        foreach (string normId in TrackDeduplicationHelper.ExtractNormalizedTrackIds(track))
        {
            destination.Add(normId);
            destination.Add($"ytm_{normId}");
        }
    }

    private static bool IsTrackExcludedById(TrackItem track, HashSet<string> excludedIds)
    {
        if (!string.IsNullOrWhiteSpace(track.Id) && excludedIds.Contains(track.Id.Trim()))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId))
        {
            string vid = track.YouTubeVideoId.Trim();
            if (excludedIds.Contains(vid) || excludedIds.Contains($"ytm_{vid}"))
            {
                return true;
            }
        }

        foreach (string normId in TrackDeduplicationHelper.ExtractNormalizedTrackIds(track))
        {
            if (excludedIds.Contains(normId) || excludedIds.Contains($"ytm_{normId}"))
            {
                return true;
            }
        }

        return false;
    }

    private void SyncTracksLikedStateAgainstLikedSongs(IEnumerable<TrackItem> tracks)
    {
        var likedIds = BuildTrackExclusionIds(LikedSongs);
        foreach (var item in tracks)
        {
            if (item is not null)
            {
                item.IsLiked = IsTrackExcludedById(item, likedIds);
            }
        }
    }

    private async Task<List<TrackItem>> WarmTrackCoversAsync(
        IReadOnlyList<TrackItem> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return [];
        }

        var remoteUris = tracks
            .Select(t => t.CoverArtUri?.Trim())
            .Where(uri => !string.IsNullOrWhiteSpace(uri) &&
                          (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (remoteUris.Count == 0)
        {
            return tracks is List<TrackItem> list ? list : tracks.ToList();
        }

        IReadOnlyDictionary<string, string> warmedMap;
        try
        {
            warmedMap = await _youTubeMusicService.WarmArtworkCacheAsync(remoteUris, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return tracks is List<TrackItem> list ? list : tracks.ToList();
        }

        if (warmedMap.Count == 0)
        {
            return tracks is List<TrackItem> list ? list : tracks.ToList();
        }

        var result = new List<TrackItem>(tracks.Count);
        foreach (var track in tracks)
        {
            if (!string.IsNullOrWhiteSpace(track.CoverArtUri) &&
                warmedMap.TryGetValue(track.CoverArtUri.Trim(), out string? localUri) &&
                !string.IsNullOrWhiteSpace(localUri))
            {
                result.Add(new TrackItem
                {
                    Id = track.Id,
                    Title = track.Title,
                    Artist = track.Artist,
                    Album = track.Album,
                    Duration = track.Duration,
                    CoverArtUri = localUri,
                    AudioSourceUri = track.AudioSourceUri,
                    YouTubeVideoId = track.YouTubeVideoId,
                    SourceType = track.SourceType,
                    IsLiked = track.IsLiked,
                    IsCurrentlyPlaying = track.IsCurrentlyPlaying,
                    IsExplicit = track.IsExplicit,
                    DateAddedMs = track.DateAddedMs,
                    QualityBadge = track.QualityBadge,
                    Genre = track.Genre,
                    ReleaseYear = track.ReleaseYear,
                    TrackNumber = track.TrackNumber,
                    PlayCountText = track.PlayCountText,
                    PlayCount = track.PlayCount,
                    LyricsProviderName = track.LyricsProviderName,
                    Lyrics = track.Lyrics,
                    IsDownloadedOffline = track.IsDownloadedOffline,
                    LocalOfflineFilePath = track.LocalOfflineFilePath
                });
            }
            else
            {
                result.Add(track);
            }
        }

        return result;
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetResult();
        }

        return tcs.Task;
    }

    [RelayCommand]
    private async Task PlayAllCurrentPlaylistAsync()
    {
        if (PlaylistDetailTracks.Count == 0)
        {
            return;
        }

        await PlayTrackFromListAsync(PlaylistDetailTracks[0], PlaylistDetailTracks.ToList());
    }

    [RelayCommand]
    private async Task ShuffleCurrentPlaylistAsync()
    {
        if (PlaylistDetailTracks.Count == 0)
        {
            return;
        }

        await SetShuffledQueueAndPlayAsync(PlaylistDetailTracks.ToList());
    }

    [RelayCommand]
    private async Task DeleteCurrentPlaylistAsync()
    {
        if (CurrentPlaylistDetail is null || string.IsNullOrWhiteSpace(CurrentPlaylistDetail.Id))
        {
            return;
        }

        string playlistId = CurrentPlaylistDetail.Id;
        string title = CurrentPlaylistDetail.Title;

        CancelAndDisposeCts(ref _playlistRelatedLoadCts);
        _playlistRelatedCache.Remove(playlistId);
        PlaylistRelatedTracks.Clear();
        IsPlaylistRelatedLoading = false;
        OnPropertyChanged(nameof(PlaylistRelatedSkeletonVisibility));
        OnPropertyChanged(nameof(HasPlaylistRelatedVisibility));
        OnPropertyChanged(nameof(PlaylistRelatedSectionVisibility));

        await _catalogService.DeletePlaylistAsync(playlistId);
        _isPlaylistsDirty = true;
        await RefreshPlaylistsAsync(refreshSmartSuggestions: false);
        ClearPlaylistDetailState();
        ActiveSubView = WorkspaceSubView.MainSection;
        CurrentSection = NavigationSection.Playlists;
        StatusMessage = $"Deleted playlist '{title}'";
    }

    public async Task RemoveTrackFromCurrentPlaylistAsync(TrackItem? track)
    {
        if (track is null || CurrentPlaylistDetail is null)
        {
            return;
        }

        var updated = await _catalogService.RemoveTrackFromPlaylistAsync(CurrentPlaylistDetail.Id, track.Id);
        if (updated is not null)
        {
            _isPlaylistsDirty = true;
            await OpenPlaylistDetailAsync(updated);
            StatusMessage = $"Removed '{track.Title}' from '{updated.Title}'";
        }
    }

    [RelayCommand]
    private Task ToggleDraggableLensAsync()
    {
        IsDraggableLensVisible = !IsDraggableLensVisible;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ChangeLanguageAsync(string cultureCode)
    {
        await _localizationService.SetLanguageAsync(cultureCode);
    }

    [RelayCommand]
    private async Task ApplyThemeAsync(string themeId)
    {
        await _themeService.ApplyThemeAsync(themeId);
        IsLightMode = !_themeService.CurrentTheme.IsDark;
        ThemePreference = _themeService.ThemePreference;
        SyncGlassSlidersFromService(_themeService.LiquidGlass);
    }

    [RelayCommand]
    private async Task SetThemePreferenceAsync(string preferenceName)
    {
        if (!Enum.TryParse<AppThemePreference>(preferenceName, ignoreCase: true, out var preference))
        {
            return;
        }

        await _themeService.SetThemePreferenceAsync(preference);
        IsLightMode = !_themeService.CurrentTheme.IsDark;
        ThemePreference = _themeService.ThemePreference;
        SyncGlassSlidersFromService(_themeService.LiquidGlass);
    }

    [RelayCommand]
    private async Task ChangeBackdropAsync(string backdropName)
    {
        if (Enum.TryParse<BackdropMaterialKind>(backdropName, ignoreCase: true, out var kind))
        {
            await _themeService.SetBackdropMaterialAsync(kind);
        }
    }

    [RelayCommand]
    private async Task ResetLiquidGlassPresetAsync()
    {
        var defaultGlass = new LiquidGlassParameters();
        SyncGlassSlidersFromService(defaultGlass);
        await _themeService.UpdateLiquidGlassParametersAsync(defaultGlass, persist: true);
    }

    partial void OnIsDynamicCoverAccentEnabledChanged(bool value)
    {
        if (_themeService.IsDynamicCoverAccentEnabled == value)
        {
            return;
        }

        _ = _themeService.SetDynamicCoverAccentEnabledAsync(value, CurrentTrack.CoverArtUri);
    }

    /// <summary>
    /// Manually sets a custom user-preferred accent color from the settings color swatches and disables automatic cover extraction.
    /// </summary>
    [RelayCommand]
    private async Task SelectManualAccentColorAsync(string hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor))
        {
            return;
        }

        await _themeService.SetCustomAccentColorAsync(hexColor);
        IsDynamicCoverAccentEnabled = false;
        ActiveAccentColorHex = _themeService.ActiveAccentHex;
    }

    /// <summary>
    /// Opens a native Windows 11 FolderPicker to scan any user-selected folder recursively for audio files and metadata.
    /// </summary>
    [RelayCommand]
    private async Task ScanUserMusicFolderAsync()
    {
        if (IsScanning)
        {
            return;
        }

        try
        {
            var folderPicker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };
            folderPicker.FileTypeFilter.Add("*");

            if (App.MainWindowInstance is Window mainWindow)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);
            }

            var pickedFolder = await folderPicker.PickSingleFolderAsync();
            if (pickedFolder is null)
            {
                return;
            }

            IsScanning = true;
            StatusMessage = $"Scanning '{pickedFolder.Name}'...";

            var imported = await _catalogService.AddLocalMusicDirectoryAsync(pickedFolder.Path);
            SyncLocalMusicDirectoriesList();
            _isLibraryDirty = true;
            if (IsLibraryViewOpen)
            {
                await RefreshCatalogAsync();
            }

            if (imported.Count > 0)
            {
                StatusMessage = $"Imported {imported.Count} tracks from '{pickedFolder.Name}'";
                if (CurrentTrack.Id == "empty_state_track" || string.IsNullOrEmpty(CurrentTrack.AudioSourceUri))
                {
                    await PlayTrackFromListAsync(imported[0], imported);
                }
            }
            else
            {
                StatusMessage = $"No supported audio files found in '{pickedFolder.Name}'";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pick or scan local music directory.");
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Opens a native Windows 11 FileOpenPicker to import one or multiple audio files directly.
    /// </summary>
    [RelayCommand]
    private async Task PickAndImportFilesAsync()
    {
        if (IsScanning)
        {
            return;
        }

        try
        {
            var filePicker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };

            string[] extensions = [".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".alac"];
            foreach (var ext in extensions)
            {
                filePicker.FileTypeFilter.Add(ext);
            }

            if (App.MainWindowInstance is Window mainWindow)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hwnd);
            }

            var pickedFiles = await filePicker.PickMultipleFilesAsync();
            if (pickedFiles is null || pickedFiles.Count == 0)
            {
                return;
            }

            IsScanning = true;
            var paths = pickedFiles.Select(f => f.Path).ToList();
            var imported = await _catalogService.ImportLocalFilesAsync(paths);
            _isLibraryDirty = true;
            if (IsLibraryViewOpen)
            {
                await RefreshCatalogAsync();
            }

            if (imported.Count > 0)
            {
                StatusMessage = $"Imported {imported.Count} audio files";
                await PlayTrackFromListAsync(imported[0], imported);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pick or import local audio files.");
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Clears all scanned local tracks from the catalog.
    /// </summary>
    [RelayCommand]
    private async Task ClearCatalogAsync()
    {
        await _catalogService.ClearCatalogAsync();
        _isLibraryDirty = true;
        _isLikedDirty = true;
        if (_isLibraryHydrated)
        {
            await RefreshCatalogAsync();
        }
    }

    [RelayCommand]
    private async Task ExportCustomThemeJsonAsync()
    {
        try
        {
            var exportPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XperienceMusicPlayer",
                "custom_theme_export.json");

            var customSnapshot = _themeService.CurrentTheme with
            {
                Id = "CustomUserPalette",
                DisplayName = "Custom JSON Theme",
                LiquidGlass = _themeService.LiquidGlass
            };

            await _storageService.ExportJsonFileAsync(exportPath, customSnapshot);
            StatusMessage = $"Theme exported: {Path.GetFileName(exportPath)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export custom JSON theme.");
        }
    }

    /// <summary>
    /// Opens a FileOpenPicker to import a synchronized .lrc lyric file for the currently active track.
    /// </summary>
    [RelayCommand]
    private async Task ImportLrcForCurrentTrackAsync()
    {
        if (CurrentTrack is null || CurrentTrack.Id == "empty_state_track")
        {
            return;
        }

        try
        {
            var filePicker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };
            filePicker.FileTypeFilter.Add(".lrc");
            filePicker.FileTypeFilter.Add(".txt");

            if (App.MainWindowInstance is Window mainWindow)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hwnd);
            }

            var pickedFile = await filePicker.PickSingleFileAsync();
            if (pickedFile is null)
            {
                return;
            }

            var parsedLines = await _catalogService.ImportLrcFileForTrackAsync(CurrentTrack.Id, pickedFile.Path);
            if (parsedLines.Count > 0)
            {
                ApplyImportedLrcLyricsForTrack(CurrentTrack, parsedLines);
                ActiveNowPlayingTab = NowPlayingTab.Lyrics;
                StatusMessage = $"Loaded {parsedLines.Count} synchronized lyrics from '{pickedFile.Name}'";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import .lrc file for current track.");
        }
    }

    /// <summary>
    /// Synchronizes the observable <see cref="LyricsProviders"/> collection from <see cref="ILyricsProviderService"/>.
    /// </summary>
    private void SyncLyricsProvidersList()
    {
        LyricsProviders.Clear();
        foreach (var provider in _lyricsProviderService.Providers.OrderBy(p => p.PriorityOrder))
        {
            LyricsProviders.Add(provider);
        }
    }

    /// <summary>
    /// Moves a lyrics provider higher in priority (up in the list) and refreshes online lyrics if applicable.
    /// </summary>
    public async Task MoveLyricsProviderUpAsync(LyricsProviderConfig? provider)
    {
        if (provider is null)
        {
            return;
        }

        await _lyricsProviderService.MoveProviderPriorityAsync(provider.Id, -1);
        InvalidateAllLyricsCachesForProviderChange();
        SyncLyricsProvidersList();
        StatusMessage = $"Lyrics priority updated: {provider.DisplayName} moved up";
        await RefreshCurrentTrackOnlineLyricsAsync();
    }

    /// <summary>
    /// Moves a lyrics provider lower in priority (down in the list) and refreshes online lyrics if applicable.
    /// </summary>
    public async Task MoveLyricsProviderDownAsync(LyricsProviderConfig? provider)
    {
        if (provider is null)
        {
            return;
        }

        await _lyricsProviderService.MoveProviderPriorityAsync(provider.Id, +1);
        InvalidateAllLyricsCachesForProviderChange();
        SyncLyricsProvidersList();
        StatusMessage = $"Lyrics priority updated: {provider.DisplayName} moved down";
        await RefreshCurrentTrackOnlineLyricsAsync();
    }

    /// <summary>
    /// Toggles whether a specific lyrics provider is enabled in the cascade.
    /// </summary>
    public async Task ToggleLyricsProviderEnabledAsync(LyricsProviderConfig? provider)
    {
        if (provider is null)
        {
            return;
        }

        bool newState = !provider.IsEnabled;
        await _lyricsProviderService.SetProviderEnabledAsync(provider.Id, newState);
        InvalidateAllLyricsCachesForProviderChange();
        SyncLyricsProvidersList();
        StatusMessage = $"{provider.DisplayName}: {(newState ? "Enabled" : "Disabled")}";
        await RefreshCurrentTrackOnlineLyricsAsync();
    }

    private Task RefreshCurrentTrackOnlineLyricsAsync()
    {
        if (CurrentTrack is null || CurrentTrack.Id == "empty_state_track")
        {
            return Task.CompletedTask;
        }

        BeginTrackLyricsPipeline(CurrentTrack, forceRefresh: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Seeks the audio player to a clicked lyric line's timestamp.
    /// </summary>
    public void SeekToLyric(SynchronizedLyricLineViewModel? lyricLine)
    {
        if (lyricLine is null)
        {
            return;
        }

        double targetSeconds = lyricLine.Timestamp.TotalSeconds;
        if (IsFullscreenVideoMode && _currentOfficialVideoMetadata is not null)
        {
            var videoDuration = TotalDurationSeconds > 1.0
                ? TimeSpan.FromSeconds(TotalDurationSeconds)
                : (TimeSpan?)null;
            targetSeconds = _currentOfficialVideoMetadata
                .MapSongToVideoPosition(lyricLine.Timestamp, videoDuration)
                .TotalSeconds;
        }

        SeekToSeconds(targetSeconds);
    }

    /// <summary>
    /// Seeks the audio player when the user interacts with the progress slider.
    /// </summary>
    public void SeekToSeconds(double seconds)
    {
        if (double.IsNaN(seconds) ||
            double.IsInfinity(seconds) ||
            CurrentTrack is null ||
            string.Equals(CurrentTrack.Id, "empty_state_track", StringComparison.Ordinal))
        {
            return;
        }

        double maxDurationSec = _audioPlayerService.Duration.TotalSeconds > 0.0
            ? Math.Max(1.0, _audioPlayerService.Duration.TotalSeconds)
            : Math.Max(1.0, TotalDurationSeconds);
        double clampedSec = Math.Clamp(seconds, 0.0, maxDurationSec);
        var target = TimeSpan.FromSeconds(clampedSec);

        _hasUserInitiatedPlayback = true;
        _audioPlayerService.Seek(target);
        Interlocked.Increment(ref _positionSyncGeneration);

        var effectiveDuration = _audioPlayerService.Duration > TimeSpan.Zero
            ? _audioPlayerService.Duration
            : TimeSpan.FromSeconds(maxDurationSec);

        ApplyPlaybackPositionToUi(target, effectiveDuration, forceLyricNotify: true);
    }

    private void ApplyPlaybackPositionToUi(TimeSpan position, TimeSpan duration, bool forceLyricNotify)
    {
        bool hasRealTrack = CurrentTrack is not null &&
            !string.Equals(CurrentTrack.Id, "empty_state_track", StringComparison.Ordinal);

        double effectiveDurationSec;
        if (duration.TotalSeconds > 0.0)
        {
            effectiveDurationSec = Math.Max(1.0, duration.TotalSeconds);
            TotalDurationSeconds = effectiveDurationSec;

            int durSecond = Math.Max(0, (int)duration.TotalSeconds);
            if (durSecond != _lastFormattedDurationSecond)
            {
                _lastFormattedDurationSecond = durSecond;
                FormattedDuration = $"{durSecond / 60}:{durSecond % 60:D2}";
            }
        }
        else if (!hasRealTrack)
        {
            effectiveDurationSec = 1.0;
            TotalDurationSeconds = 1.0;
            if (_lastFormattedDurationSecond != 0)
            {
                _lastFormattedDurationSecond = 0;
                FormattedDuration = "0:00";
            }
        }
        else
        {
            effectiveDurationSec = Math.Max(1.0, TotalDurationSeconds);
        }

        double clampedPosSec = Math.Clamp(position.TotalSeconds, 0.0, effectiveDurationSec);
        CurrentPositionSeconds = clampedPosSec;
        LyricsPositionSeconds = ComputeEffectiveLyricsPositionSeconds(clampedPosSec);

        int posSecond = Math.Max(0, (int)clampedPosSec);
        if (posSecond != _lastFormattedPositionSecond)
        {
            _lastFormattedPositionSecond = posSecond;
            FormattedPosition = $"{posSecond / 60}:{posSecond % 60:D2}";
        }

        UpdateActiveLyricForPosition(TimeSpan.FromSeconds(clampedPosSec), forceNotify: forceLyricNotify);
    }

    private async Task RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        var batchToken = ReplaceCts(
            ref _songsBatchCts,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)).Token;

        IsLibraryLoading = true;
        try
        {
            var effectiveSection = CurrentSection == NavigationSection.Community
                ? NavigationSection.Community
                : NavigationSection.Library;

            // Always query SearchCategoryFilter.All with empty search query so Library is never hijacked by dedicated Search
            var result = await _catalogService.QueryCatalogAsync(
                string.Empty,
                SearchCategoryFilter.All,
                SelectedSourceType,
                effectiveSection,
                batchToken);

            if (batchToken.IsCancellationRequested)
            {
                return;
            }

            _lastLoadedLibrarySource = SelectedSourceType;
            _lastLoadedLibraryQueryKey = string.Empty;

            Albums.Clear();
            foreach (var album in result.Albums)
            {
                Albums.Add(album);
            }

            Artists.Clear();
            foreach (var artist in result.Artists)
            {
                Artists.Add(artist);
            }

            RecommendedArtists.Clear();
            foreach (var rec in result.RecommendedArtists)
            {
                RecommendedArtists.Add(rec);
            }

            Songs.Clear();
            foreach (var song in result.Songs)
            {
                Songs.Add(song);
            }

            _librarySongsSnapshot = [.. result.Songs];

            // Populate Recently Added (preferring catalog-provided chronological order, falling back to DateAddedMs DESC)
            var recentlyAddedSource = result.RecentlyAdded is { Count: > 0 }
                ? result.RecentlyAdded
                : result.Songs
                    .OrderByDescending(t => t.DateAddedMs)
                    .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.Id, StringComparer.Ordinal)
                    .ToList();

            LibraryRecentlyAdded.Clear();
            foreach (var recent in recentlyAddedSource)
            {
                LibraryRecentlyAdded.Add(recent);
            }

            // Populate All-tab capped previews matching Android LibraryScreen (10 / 10 / 10 / 15)
            LibraryRecentlyAddedPreview.Clear();
            foreach (var recent in LibraryRecentlyAdded.Take(10))
            {
                LibraryRecentlyAddedPreview.Add(recent);
            }

            LibraryArtistsPreview.Clear();
            foreach (var artist in Artists.Take(10))
            {
                LibraryArtistsPreview.Add(artist);
            }

            LibraryAlbumsPreview.Clear();
            foreach (var album in Albums.Take(10))
            {
                LibraryAlbumsPreview.Add(album);
            }

            LibrarySongsPreview.Clear();
            foreach (var song in Songs.Take(15))
            {
                LibrarySongsPreview.Add(song);
            }

            // Build A–Z + '#' groups for Artists, Albums, Songs and chronological buckets for Recently Added
            LibraryArtistGroups.Clear();
            foreach (var group in LibraryAlphabetHelper.GroupArtists(Artists))
            {
                LibraryArtistGroups.Add(group);
            }

            LibraryAlbumGroups.Clear();
            foreach (var group in LibraryAlphabetHelper.GroupAlbums(Albums))
            {
                LibraryAlbumGroups.Add(group);
            }

            LibrarySongGroups.Clear();
            LibraryOrderedSongs.Clear();
            foreach (var group in LibraryAlphabetHelper.GroupTracks(Songs))
            {
                LibrarySongGroups.Add(group);
                foreach (var song in group.Items)
                {
                    LibraryOrderedSongs.Add(song);
                }
            }

            LibraryRecentlyAddedGroups.Clear();
            foreach (var group in LibraryAlphabetHelper.GroupRecentlyAddedByPeriod(LibraryRecentlyAdded))
            {
                LibraryRecentlyAddedGroups.Add(group);
            }

            _isLibraryHydrated = true;
            _isLibraryDirty = false;

            SyncLibraryPlayingTrackState();
            NotifyLibraryComputedProperties();
            LibraryIndexChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer catalog query
        }
        finally
        {
            if (!batchToken.IsCancellationRequested)
            {
                IsLibraryLoading = false;
                NotifyLibraryComputedProperties();
            }
        }
    }

    private void NotifyLibraryComputedProperties()
    {
        OnPropertyChanged(nameof(EmptyCatalogVisibility));
        OnPropertyChanged(nameof(HasSongsVisibility));
        OnPropertyChanged(nameof(HasAlbumsVisibility));
        OnPropertyChanged(nameof(HasArtistsVisibility));
        OnPropertyChanged(nameof(HasRecommendedArtistsVisibility));
        OnPropertyChanged(nameof(HasLibraryRecentlyAddedVisibility));
        OnPropertyChanged(nameof(HasLibraryOverviewArtistsVisibility));
        OnPropertyChanged(nameof(HasLibraryOverviewAlbumsVisibility));
        OnPropertyChanged(nameof(HasLibraryOverviewSongsVisibility));
        OnPropertyChanged(nameof(LibraryOverviewEmptyVisibility));
        OnPropertyChanged(nameof(LibraryLocalToolsBarVisibility));
        OnPropertyChanged(nameof(LibraryArtistsEmptyVisibility));
        OnPropertyChanged(nameof(LibraryAlbumsEmptyVisibility));
        OnPropertyChanged(nameof(LibrarySongsEmptyVisibility));
        OnPropertyChanged(nameof(LibraryRecentlyAddedEmptyVisibility));
        OnPropertyChanged(nameof(LibraryAlphabetRailVisibility));
        OnPropertyChanged(nameof(LibraryRecentlyAddedMoreCardCountText));
        OnPropertyChanged(nameof(LibraryArtistsMoreLabel));
        OnPropertyChanged(nameof(LibraryArtistsMoreCardVisibility));
        OnPropertyChanged(nameof(LibraryArtistsMoreCardCountText));
        OnPropertyChanged(nameof(LibraryAlbumsMoreLabel));
        OnPropertyChanged(nameof(LibraryAlbumsMoreCardVisibility));
        OnPropertyChanged(nameof(LibraryAlbumsMoreCardCountText));
        OnPropertyChanged(nameof(LibrarySongsMoreLabel));
        OnPropertyChanged(nameof(LibraryViewAllSongsButtonLabel));
        OnPropertyChanged(nameof(LibraryViewAllSongsButtonVisibility));
        OnPropertyChanged(nameof(LibraryHeaderTitle));
        OnPropertyChanged(nameof(LibraryHeaderSubtitle));
    }

    private async Task RefreshHomeDiscoveryFeedAsync(string? genreOverride = null, CancellationToken cancellationToken = default)
    {
        try
        {
            IsLoadingDiscovery = true;
            var feed = await _catalogService.GetHomeDiscoveryFeedAsync(genreOverride ?? SelectedHomeGenre, cancellationToken);

            HomePersonalizedSubtitle = feed.PersonalizedSubtitle;
            SelectedHomeGenre = feed.SelectedGenreTag;

            HomeRecentlyPlayed.Clear();
            foreach (var recent in feed.RecentlyPlayedTracks)
            {
                HomeRecentlyPlayed.Add(recent);
            }

            HomeRecentlyPlayedAlbums.Clear();
            if (feed.RecentlyPlayedAlbums is { Count: > 0 })
            {
                foreach (var recentAlbum in feed.RecentlyPlayedAlbums)
                {
                    HomeRecentlyPlayedAlbums.Add(recentAlbum);
                }
            }
            else
            {
                foreach (var recent in feed.RecentlyPlayedTracks)
                {
                    var albumEntry = RecentAlbumItem.FromTrack(recent);
                    if (!HomeRecentlyPlayedAlbums.Any(existing => existing.MatchesAlbum(albumEntry)))
                    {
                        HomeRecentlyPlayedAlbums.Add(albumEntry);
                    }
                }
            }

            HomeRecommendedTracks.Clear();
            foreach (var recTrack in feed.RecommendedTracks)
            {
                HomeRecommendedTracks.Add(recTrack);
            }

            HomeGenreTags.Clear();
            foreach (var tag in feed.GenreTags)
            {
                HomeGenreTags.Add(tag);
            }

            HomeGenreTracks.Clear();
            foreach (var gTrack in feed.GenreTracks)
            {
                HomeGenreTracks.Add(gTrack);
            }

            _homeRecommendedSnapshot = [.. feed.RecommendedTracks];
            _homeGenreSnapshot = [.. feed.GenreTracks];
            _homeRecentlyPlayedSnapshot = [.. feed.RecentlyPlayedTracks];

            SyncHomePlayingTrackState();

            HomeRelatedArtists.Clear();
            foreach (var artist in feed.RelatedArtists)
            {
                HomeRelatedArtists.Add(artist);
            }
            OnPropertyChanged(nameof(HasHomeRelatedArtistsVisibility));

            HomeRecommendedAlbums.Clear();
            foreach (var album in feed.RecommendedAlbums)
            {
                HomeRecommendedAlbums.Add(album);
            }

            HomeTopPicks.Clear();
            if (feed.TopPicks is not null)
            {
                foreach (var pick in feed.TopPicks)
                {
                    HomeTopPicks.Add(pick);
                }
            }

            HomePlaylistsMadeForYou.Clear();
            if (feed.PlaylistsMadeForYou is not null)
            {
                foreach (var pl in feed.PlaylistsMadeForYou)
                {
                    HomePlaylistsMadeForYou.Add(pl);
                }
            }

            if (feed.SmartSuggestedPlaylists is not null)
            {
                await RefreshSmartSuggestedPlaylistsAsync(feed.SmartSuggestedPlaylists);
            }

            _allRadioStations.Clear();
            HomeRadioStations.Clear();
            if (feed.RadioStations is not null)
            {
                foreach (var radio in feed.RadioStations)
                {
                    _allRadioStations.Add(radio);
                    if (HomeRadioStations.Count < 14)
                    {
                        HomeRadioStations.Add(radio);
                    }
                }
            }

            _allNewReleases.Clear();
            HomeNewReleases.Clear();
            if (feed.NewReleases is not null)
            {
                foreach (var nr in feed.NewReleases)
                {
                    _allNewReleases.Add(nr);
                    if (HomeNewReleases.Count < 14)
                    {
                        HomeNewReleases.Add(nr);
                    }
                }
            }

            if (ActiveSubView == WorkspaceSubView.HomeCategoryGrid)
            {
                if (ActiveHomeGridCategory == HomeGridCategory.NewReleases)
                {
                    ApplyNewReleasesFilter();
                }
                else
                {
                    PopulateRadioStationsGrid();
                }
            }

            var moreLikeSections = feed.MoreLikeAlbumSections ?? [];
            PopulateMoreLikeSlot(moreLikeSections, 0, HomeMoreLike1, t => HomeMoreLike1Title = t, s => HomeMoreLike1SeedAlbum = s);
            PopulateMoreLikeSlot(moreLikeSections, 1, HomeMoreLike2, t => HomeMoreLike2Title = t, s => HomeMoreLike2SeedAlbum = s);
            PopulateMoreLikeSlot(moreLikeSections, 2, HomeMoreLike3, t => HomeMoreLike3Title = t, s => HomeMoreLike3SeedAlbum = s);

            OnPropertyChanged(nameof(HasHomeRecentlyPlayedVisibility));
            OnPropertyChanged(nameof(HomeTopPicksVisibility));
            OnPropertyChanged(nameof(HomePlaylistsMadeForYouVisibility));
            OnPropertyChanged(nameof(HomeRadioStationsVisibility));
            OnPropertyChanged(nameof(HomeNewReleasesVisibility));
            OnPropertyChanged(nameof(HomeMoreLike1Visibility));
            OnPropertyChanged(nameof(HomeMoreLike2Visibility));
            OnPropertyChanged(nameof(HomeMoreLike3Visibility));
            OnPropertyChanged(nameof(HomeCategoryGridSubtitle));
            OnPropertyChanged(nameof(NewReleasesFilterAllLabel));
            OnPropertyChanged(nameof(NewReleasesFilterAlbumsLabel));
            OnPropertyChanged(nameof(NewReleasesFilterSinglesLabel));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Home discovery feed.");
        }
        finally
        {
            IsHomeFeedInitialLoading = false;
            IsLoadingDiscovery = false;
            OnPropertyChanged(nameof(IsHomeSkeletonActive));
            OnPropertyChanged(nameof(HomeSkeletonVisibility));
        }
    }

    private static void PopulateMoreLikeSlot(
        IReadOnlyList<MoreLikeAlbumSectionData> sections,
        int index,
        ObservableCollection<AlbumItem> targetCollection,
        Action<string> setTitle,
        Action<AlbumItem?> setSeedAlbum)
    {
        targetCollection.Clear();
        if (index < sections.Count)
        {
            var sec = sections[index];
            setTitle(sec.HeaderTitle);
            setSeedAlbum(sec.SeedAlbum);
            foreach (var alb in sec.Albums.Take(15))
            {
                targetCollection.Add(alb);
            }
        }
        else
        {
            setTitle(string.Empty);
            setSeedAlbum(null);
        }
    }

    private async Task RefreshLikedSongsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var liked = await _catalogService.GetLikedSongsAsync(cancellationToken);
            LikedSongs.Clear();
            foreach (var song in liked)
            {
                LikedSongs.Add(song);
            }

            _likedSongsSnapshot = [.. liked];

            // Build A–Z song groups & flattened alphabetical list
            LikedSongGroups.Clear();
            LikedOrderedSongs.Clear();
            foreach (var group in LibraryAlphabetHelper.GroupTracks(LikedSongs))
            {
                LikedSongGroups.Add(group);
                foreach (var item in group.Items)
                {
                    LikedOrderedSongs.Add(item);
                }
            }

            // Derive distinct Liked Artists (matching Android deriveLikedArtists: skip blank / Unknown Artist / <unknown>)
            var artistBuckets = new Dictionary<string, (string Name, string Cover, int Count, MediaSourceType Source)>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in LikedSongs)
            {
                string rawArtist = song.Artist?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawArtist) ||
                    rawArtist.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
                    rawArtist.Equals("<unknown>", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string primaryArtist = rawArtist.Split([',', '&', ';'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                if (string.IsNullOrWhiteSpace(primaryArtist) ||
                    primaryArtist.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
                    primaryArtist.Equals("<unknown>", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (artistBuckets.TryGetValue(primaryArtist, out var existing))
                {
                    string cover = !string.IsNullOrWhiteSpace(existing.Cover) ? existing.Cover : (song.HasCoverArt ? song.SafeCoverArtUri : string.Empty);
                    artistBuckets[primaryArtist] = (existing.Name, cover, existing.Count + 1, existing.Source);
                }
                else
                {
                    artistBuckets[primaryArtist] = (primaryArtist, song.HasCoverArt ? song.SafeCoverArtUri : string.Empty, 1, song.SourceType);
                }
            }

            LikedDerivedArtists.Clear();
            foreach (var entry in artistBuckets.Values)
            {
                LikedDerivedArtists.Add(new ArtistItem
                {
                    Id = $"liked_art_{entry.Name.ToLowerInvariant()}",
                    Name = entry.Name,
                    AvatarUri = entry.Cover,
                    Genre = entry.Count == 1 ? "1 liked song" : $"{entry.Count} liked songs",
                    TrackCount = entry.Count,
                    SourceType = entry.Source
                });
            }

            LikedArtistGroups.Clear();
            foreach (var group in LibraryAlphabetHelper.GroupArtists(LikedDerivedArtists))
            {
                LikedArtistGroups.Add(group);
            }

            // Derive distinct Liked Albums (matching Android deriveLikedAlbums: skip blank / Unknown Album / YouTube Music)
            var albumBuckets = new Dictionary<string, (string Title, string Artist, string Cover, int Count, MediaSourceType Source)>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in LikedSongs)
            {
                string albumTitle = song.Album?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(albumTitle) ||
                    albumTitle.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase) ||
                    albumTitle.Equals("YouTube Music", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string artist = string.IsNullOrWhiteSpace(song.Artist) ? "Unknown Artist" : song.Artist.Trim();
                string key = $"{albumTitle}|{artist}";

                if (albumBuckets.TryGetValue(key, out var existing))
                {
                    string cover = !string.IsNullOrWhiteSpace(existing.Cover) ? existing.Cover : (song.HasCoverArt ? song.SafeCoverArtUri : string.Empty);
                    albumBuckets[key] = (existing.Title, existing.Artist, cover, existing.Count + 1, existing.Source);
                }
                else
                {
                    albumBuckets[key] = (albumTitle, artist, song.HasCoverArt ? song.SafeCoverArtUri : string.Empty, 1, song.SourceType);
                }
            }

            LikedDerivedAlbums.Clear();
            foreach (var entry in albumBuckets.Values)
            {
                LikedDerivedAlbums.Add(new AlbumItem
                {
                    Id = $"liked_alb_{entry.Title.ToLowerInvariant()}_{entry.Artist.ToLowerInvariant()}",
                    Title = entry.Title,
                    Artist = entry.Artist,
                    CoverArtUri = entry.Cover,
                    TrackCount = entry.Count,
                    SourceType = entry.Source
                });
            }

            LikedAlbumGroups.Clear();
            foreach (var group in LibraryAlphabetHelper.GroupAlbums(LikedDerivedAlbums))
            {
                LikedAlbumGroups.Add(group);
            }

            _isLikedHydrated = true;
            _isLikedDirty = false;

            SyncLikedAndPlaylistPlayingTrackState();

            OnPropertyChanged(nameof(EmptyLikedVisibility));
            OnPropertyChanged(nameof(HasLikedSongsVisibility));
            OnPropertyChanged(nameof(LikedStatsSubtitle));
            OnPropertyChanged(nameof(LikedSongsRecentVisibility));
            OnPropertyChanged(nameof(LikedSongsAlphaVisibility));
            OnPropertyChanged(nameof(LikedSongsEmptyVisibility));
            OnPropertyChanged(nameof(LikedPlayActionsVisibility));
            OnPropertyChanged(nameof(LikedRightRecommendationsPaneVisibility));
            OnPropertyChanged(nameof(LikedArtistsRecentVisibility));
            OnPropertyChanged(nameof(LikedArtistsAlphaVisibility));
            OnPropertyChanged(nameof(LikedArtistsEmptyVisibility));
            OnPropertyChanged(nameof(LikedAlbumsRecentVisibility));
            OnPropertyChanged(nameof(LikedAlbumsAlphaVisibility));
            OnPropertyChanged(nameof(LikedAlbumsEmptyVisibility));
            OnPropertyChanged(nameof(LikedAlphabetRailVisibility));
            LikedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh liked songs collection.");
        }
    }

    private void PopulateCurrentLyrics(IReadOnlyList<LyricLine> lines)
    {
        CurrentLyrics.Clear();
        for (int i = 0; i < lines.Count; i++)
        {
            TimeSpan? nextTimestamp = (i + 1 < lines.Count) ? lines[i + 1].Timestamp : null;
            CurrentLyrics.Add(new SynchronizedLyricLineViewModel(i, lines[i], nextTimestamp));
        }

        _activeLyricIndex = -1;
    }

    private int _activeLyricIndex = -1;

    private double ComputeEffectiveLyricsPositionSeconds(double audioPositionSeconds)
    {
        if (IsFullscreenVideoMode && _currentOfficialVideoMetadata is not null)
        {
            return _currentOfficialVideoMetadata
                .MapVideoToSongPosition(TimeSpan.FromSeconds(Math.Max(0.0, audioPositionSeconds)), CurrentTrack?.Duration)
                .TotalSeconds;
        }

        return Math.Max(0.0, audioPositionSeconds);
    }

    private void UpdateActiveLyricForPosition(TimeSpan position, bool forceNotify)
    {
        if (CurrentLyrics.Count == 0)
        {
            return;
        }

        if (IsFullscreenVideoMode && _currentOfficialVideoMetadata is not null)
        {
            position = _currentOfficialVideoMetadata.MapVideoToSongPosition(position, CurrentTrack?.Duration);
        }

        // Look ahead by 100ms so the visual spring transition lands right on the vocal beat
        var effectiveTime = position + TimeSpan.FromMilliseconds(100);
        int candidateIndex = 0;

        for (int i = 0; i < CurrentLyrics.Count; i++)
        {
            if (CurrentLyrics[i].Timestamp <= effectiveTime)
            {
                candidateIndex = i;
            }
            else
            {
                break;
            }
        }

        if (!forceNotify && candidateIndex == _activeLyricIndex)
        {
            CurrentLyrics[candidateIndex].UpdateProgressivePosition(effectiveTime);
            return;
        }

        _activeLyricIndex = candidateIndex;

        for (int i = 0; i < CurrentLyrics.Count; i++)
        {
            CurrentLyrics[i].UpdatePlaybackState(candidateIndex);
        }

        CurrentLyrics[candidateIndex].UpdateProgressivePosition(effectiveTime);

        ActiveLyricIndexChanged?.Invoke(this, candidateIndex);
    }

    partial void OnIsAppleMusicCanvasEnabledChanged(bool value) =>
        HandleIsAppleMusicCanvasEnabledChangedCore(value);

    private Task TriggerCanvasLookupForTrackAsync(TrackItem track) =>
        TriggerCanvasLookupForTrackCoreAsync(track);

    private void OnAudioTrackChanged(object? sender, TrackItem? track)
    {
        Interlocked.Increment(ref _positionSyncGeneration);

        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (track is null)
            {
                return;
            }

            try
            {
                // Drop stale track-change notifications superseded by a newer selection before this UI tick ran
                var activeServiceTrack = _audioPlayerService.CurrentTrack;
                if (activeServiceTrack is not null && !IsSameTrack(activeServiceTrack, track))
                {
                    return;
                }

                // Cancel and dispose any in-flight secondary work from the previous track immediately
                var secondaryToken = ReplaceCts(ref _trackChangeSecondaryCts).Token;
                _ = TrackRemoteArtworkDataUsageAsync(track, secondaryToken);

                // 1. Immediate (<16ms) UI metadata, cover art, liked state, and playing-row updates — ZERO blocking awaits!
                bool isLiked = track.IsLiked || (_isLikedHydrated && LikedSongs.Any(l =>
                    string.Equals(l.Id, track.Id, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(track.YouTubeVideoId) &&
                     string.Equals(l.YouTubeVideoId, track.YouTubeVideoId, StringComparison.Ordinal))));
                if (track.IsLiked != isLiked)
                {
                    track.IsLiked = isLiked;
                }

                bool isSameAsCurrentTrack = CurrentTrack is not null && IsSameTrack(CurrentTrack, track);
                var effectiveDuration = _audioPlayerService.Duration > TimeSpan.Zero
                    ? _audioPlayerService.Duration
                    : track.Duration;
                double totalDurationSec = Math.Max(1.0, effectiveDuration.TotalSeconds);

                // Preserve any non-zero seeked position when hydrating/reloading the same track (e.g. deferred track seek or resume)
                var servicePos = _audioPlayerService.Position;
                TimeSpan effectivePosition = servicePos > TimeSpan.Zero
                    ? TimeSpan.FromSeconds(Math.Clamp(servicePos.TotalSeconds, 0.0, totalDurationSec))
                    : TimeSpan.Zero;

                CurrentTrack = track;
                IsCurrentTrackLiked = isLiked;
                TotalDurationSeconds = totalDurationSec;
                int durSecond = Math.Max(0, (int)effectiveDuration.TotalSeconds);
                _lastFormattedDurationSecond = durSecond;
                FormattedDuration = effectiveDuration > TimeSpan.Zero
                    ? $"{durSecond / 60}:{durSecond % 60:D2}"
                    : track.FormattedDuration;

                int posSecond = Math.Max(0, (int)effectivePosition.TotalSeconds);
                CurrentPositionSeconds = effectivePosition.TotalSeconds;
                LyricsPositionSeconds = ComputeEffectiveLyricsPositionSeconds(effectivePosition.TotalSeconds);
                _lastFormattedPositionSecond = posSecond;
                FormattedPosition = $"{posSecond / 60}:{posSecond % 60:D2}";

                bool isIdleStartupTrack = !_hasUserInitiatedPlayback && !IsPlaying && !_audioPlayerService.IsPlaying;

                SyncAlbumDetailPlayingTrackState();
                SyncHomePlayingTrackState();
                SyncLibraryPlayingTrackState();
                SyncLikedAndPlaylistPlayingTrackState();
                SyncSearchPlayingTrackState();
                OnPropertyChanged(nameof(QueueCountSummary));

                if (isIdleStartupTrack)
                {
                    // Initial idle startup track: extract ambient cover palette & accent ONCE,
                    // and defer heavy Canvas, Music Video, Lyrics, history persistence, and Smart Playlists
                    // until playback actually begins (or the user explicitly opens Lyrics/Fullscreen).
                    _deferredPlaybackStartTrackId = track.Id != "empty_state_track" ? track.Id : null;
                    ResetMusicVideoStateForTrackChange();
                    _ = TriggerAuroraPaletteExtractionAsync(track.SafeCoverArtUri);
                    if (_themeService.IsDynamicCoverAccentEnabled)
                    {
                        _ = ApplyCoverAccentInBackgroundAsync(track, secondaryToken);
                    }

                    return;
                }

                bool wasDeferredSameTrack =
                    isSameAsCurrentTrack &&
                    _deferredPlaybackStartTrackId is not null &&
                    string.Equals(_deferredPlaybackStartTrackId, track.Id, StringComparison.OrdinalIgnoreCase);

                _deferredPlaybackStartTrackId = null;
                BeginTrackLyricsPipeline(track);
                if (effectivePosition > TimeSpan.Zero)
                {
                    UpdateActiveLyricForPosition(effectivePosition, forceNotify: true);
                }

                RecordTrackPlayedAndUpdateHomeRecent(track, secondaryToken);
                TriggerPlaybackSessionPersist(forceImmediate: true);

                // 2. Launch secondary work IN PARALLEL so Canvas, Cover Accent, Aurora, Video, and Lyrics NEVER block each other!
                _ = TriggerCanvasLookupForTrackAsync(track);
                _ = CheckOfficialMusicVideoAvailabilityAsync(track);
                if (!wasDeferredSameTrack)
                {
                    _ = TriggerAuroraPaletteExtractionAsync(track.SafeCoverArtUri);

                    if (_themeService.IsDynamicCoverAccentEnabled)
                    {
                        _ = ApplyCoverAccentInBackgroundAsync(track, secondaryToken);
                    }
                }

                _ = RefreshSmartPlaylistsDebouncedAsync(secondaryToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OnAudioTrackChanged UI sync encountered an error for '{Title}'.", track.Title);
            }
        });
    }

    private void RecordTrackPlayedAndUpdateHomeRecent(TrackItem track, CancellationToken secondaryToken)
    {
        if (track.Id == "empty_state_track")
        {
            return;
        }

        // Update in-memory recently played immediately (disk history persistence runs off-thread below)
        var playedAlbum = RecentAlbumItem.FromTrack(track);
        for (int i = HomeRecentlyPlayedAlbums.Count - 1; i >= 0; i--)
        {
            if (HomeRecentlyPlayedAlbums[i].MatchesAlbum(playedAlbum))
            {
                playedAlbum = HomeRecentlyPlayedAlbums[i].MergeWithNewer(playedAlbum);
                HomeRecentlyPlayedAlbums.RemoveAt(i);
            }
        }

        HomeRecentlyPlayedAlbums.Insert(0, playedAlbum);
        while (HomeRecentlyPlayedAlbums.Count > 20)
        {
            HomeRecentlyPlayedAlbums.RemoveAt(HomeRecentlyPlayedAlbums.Count - 1);
        }

        for (int i = HomeRecentlyPlayed.Count - 1; i >= 0; i--)
        {
            if (RecentAlbumItem.FromTrack(HomeRecentlyPlayed[i]).MatchesAlbum(playedAlbum))
            {
                HomeRecentlyPlayed.RemoveAt(i);
            }
        }

        HomeRecentlyPlayed.Insert(0, track);
        while (HomeRecentlyPlayed.Count > 20)
        {
            HomeRecentlyPlayed.RemoveAt(HomeRecentlyPlayed.Count - 1);
        }

        _homeRecentlyPlayedSnapshot = [.. HomeRecentlyPlayed];
        OnPropertyChanged(nameof(HasHomeRecentlyPlayedVisibility));

        // Persist history off the UI thread so disk I/O never stalls cover, canvas, or lyrics
        _ = Task.Run(async () =>
        {
            try
            {
                await _catalogService.RecordTrackPlayedAsync(track, secondaryToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background playback history persistence skipped for '{Title}'.", track.Title);
            }
        }, secondaryToken);
    }

    private async Task ApplyCoverAccentInBackgroundAsync(TrackItem track, CancellationToken token)
    {
        try
        {
            await _themeService.ApplyAlbumCoverAccentAsync(track.SafeCoverArtUri);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cover accent extraction skipped for '{Title}'.", track.Title);
        }
    }

    private async Task RefreshSmartPlaylistsDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
            if (!token.IsCancellationRequested)
            {
                await RefreshSmartSuggestedPlaylistsAsync(cancellationToken: token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Debounced smart playlists refresh skipped.");
        }
    }

    private void OnThemeChanged(object? sender, ThemePaletteDefinition theme)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            IsLightMode = !theme.IsDark;
            ThemePreference = _themeService.ThemePreference;
            ActiveAccentColorHex = _themeService.ActiveAccentHex;
            foreach (var sec in MoodGenreSections)
            {
                foreach (var tile in sec.Items)
                {
                    tile.RefreshBrushesForTheme(IsLightMode);
                }
            }

            TriggerAuroraPaletteExtractionFromCurrentTrack();
        });
    }

    private void TriggerAuroraPaletteExtractionFromCurrentTrack()
    {
        _ = TriggerAuroraPaletteExtractionAsync(CurrentTrack?.SafeCoverArtUri);
    }

    private async Task TriggerAuroraPaletteExtractionAsync(string? coverUri)
    {
        var ct = ReplaceCts(ref _auroraCts).Token;

        try
        {
            var palette = await ColorHelperEx.ExtractAuroraPaletteAsync(coverUri, ct);
            if (!ct.IsCancellationRequested)
            {
                AuroraPaletteChanged?.Invoke(this, palette);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer track
        }
    }

    private void OnAccentColorChanged(object? sender, string accentHex)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            ActiveAccentColorHex = accentHex;
            IsDynamicCoverAccentEnabled = _themeService.IsDynamicCoverAccentEnabled;
        });
    }

    private void OnAudioIsPlayingChanged(object? sender, bool playing)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            IsPlaying = playing;
            if (playing)
            {
                IsTrackLoading = false;
                _hasUserInitiatedPlayback = true;

                // If the initial idle startup track just began playing for the first time,
                // run its deferred Canvas, Lyrics, Video (if Fullscreen), History, and Smart Playlist pipeline now.
                if (_deferredPlaybackStartTrackId is not null &&
                    CurrentTrack is { } activeTrack &&
                    activeTrack.Id != "empty_state_track" &&
                    string.Equals(_deferredPlaybackStartTrackId, activeTrack.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _deferredPlaybackStartTrackId = null;
                    var secondaryToken = ReplaceCts(ref _trackChangeSecondaryCts).Token;

                    BeginTrackLyricsPipeline(activeTrack);
                    RecordTrackPlayedAndUpdateHomeRecent(activeTrack, secondaryToken);
                    _ = TriggerCanvasLookupForTrackAsync(activeTrack);
                    if (IsFullscreenNowPlayingOpen)
                    {
                        _ = TriggerMusicVideoLookupForTrackAsync(activeTrack);
                    }

                    _ = RefreshSmartPlaylistsDebouncedAsync(secondaryToken);
                    ScheduleNextTrackLyricsPrefetch();
                }
            }
            else if (_hasUserInitiatedPlayback)
            {
                TriggerPlaybackSessionPersist(forceImmediate: true);
            }
        });
    }

    private void OnAudioPositionChanged(object? sender, PlaybackPositionEventArgs e)
    {
        long capturedGeneration = Interlocked.Read(ref _positionSyncGeneration);
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (capturedGeneration != Interlocked.Read(ref _positionSyncGeneration))
            {
                return;
            }

            if (IsTrackLoading && e.Position.TotalMilliseconds > 80)
            {
                IsTrackLoading = false;
            }

            ApplyPlaybackPositionToUi(e.Position, e.Duration, forceLyricNotify: false);
            if (IsPlaying || _hasUserInitiatedPlayback)
            {
                TriggerPlaybackSessionPersist(forceImmediate: false);
            }
        });
    }

    private void OnAudioQueueChanged(object? sender, IReadOnlyList<TrackItem> queue)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSuppressingQueueSync)
            {
                return;
            }

            // Read the authoritative latest upcoming queue on the UI thread so out-of-order background
            // notifications can never overwrite a newer queue with a stale snapshot.
            var snapshot = _audioPlayerService.Queue ?? [];
            int targetCount = Math.Min(snapshot.Count, MaxVisibleQueueItems);

            if (targetCount == 0)
            {
                if (Queue.Count > 0)
                {
                    Queue.Clear();
                }
            }
            else
            {
                // 1. Pure tail append / no-op (speculative prefetch append or identical post-MoveQueueItem notification)
                bool isPureTailAppend = Queue.Count > 0 && targetCount >= Queue.Count;
                if (isPureTailAppend)
                {
                    for (int i = 0; i < Queue.Count; i++)
                    {
                        if (!ReferenceEquals(Queue[i], snapshot[i]))
                        {
                            isPureTailAppend = false;
                            break;
                        }
                    }
                }

                if (isPureTailAppend)
                {
                    for (int i = Queue.Count; i < targetCount; i++)
                    {
                        Queue.Add(snapshot[i]);
                    }
                }
                else
                {
                    // 2. Single-track head advance (normal track transition: Queue[1..] shifts left to snapshot[0..])
                    bool isSingleHeadAdvance =
                        Queue.Count > 1 &&
                        ReferenceEquals(Queue[1], snapshot[0]);
                    if (isSingleHeadAdvance)
                    {
                        int overlap = Math.Min(Queue.Count - 1, targetCount);
                        for (int i = 1; i < overlap; i++)
                        {
                            if (!ReferenceEquals(Queue[i + 1], snapshot[i]))
                            {
                                isSingleHeadAdvance = false;
                                break;
                            }
                        }
                    }

                    if (isSingleHeadAdvance)
                    {
                        Queue.RemoveAt(0);
                        while (Queue.Count > targetCount)
                        {
                            Queue.RemoveAt(Queue.Count - 1);
                        }

                        for (int i = Queue.Count; i < targetCount; i++)
                        {
                            Queue.Add(snapshot[i]);
                        }
                    }
                    else
                    {
                        // 3. Single-track head insert (Play Next: snapshot[1..] matches Queue[0..])
                        bool isSingleHeadInsert =
                            Queue.Count > 0 &&
                            targetCount > 1 &&
                            ReferenceEquals(Queue[0], snapshot[1]);
                        if (isSingleHeadInsert)
                        {
                            int overlap = Math.Min(Queue.Count, targetCount - 1);
                            for (int i = 1; i < overlap; i++)
                            {
                                if (!ReferenceEquals(Queue[i], snapshot[i + 1]))
                                {
                                    isSingleHeadInsert = false;
                                    break;
                                }
                            }
                        }

                        if (isSingleHeadInsert)
                        {
                            Queue.Insert(0, snapshot[0]);
                            while (Queue.Count > targetCount)
                            {
                                Queue.RemoveAt(Queue.Count - 1);
                            }

                            for (int i = Queue.Count; i < targetCount; i++)
                            {
                                Queue.Add(snapshot[i]);
                            }
                        }
                        else
                        {
                            // 4. General change: incremental in-place positional updates + trim/append (avoids Queue.Clear() teardown)
                            int common = Math.Min(Queue.Count, targetCount);
                            for (int i = 0; i < common; i++)
                            {
                                if (!ReferenceEquals(Queue[i], snapshot[i]))
                                {
                                    Queue[i] = snapshot[i];
                                }
                            }

                            while (Queue.Count > targetCount)
                            {
                                Queue.RemoveAt(Queue.Count - 1);
                            }

                            for (int i = Queue.Count; i < targetCount; i++)
                            {
                                Queue.Add(snapshot[i]);
                            }
                        }
                    }
                }
            }

            OnPropertyChanged(nameof(QueueCountSummary));
            if (_hasUserInitiatedPlayback || IsPlaying)
            {
                ScheduleNextTrackLyricsPrefetch();
                TriggerPlaybackSessionPersist(forceImmediate: true);
            }
        });
    }

    private static bool IsRestorableSessionTrack(TrackItem? item)
    {
        if (item is null ||
            string.IsNullOrWhiteSpace(item.Title) ||
            string.Equals(item.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (item.SourceType == MediaSourceType.Local)
        {
            return !string.IsNullOrWhiteSpace(item.AudioSourceUri) && File.Exists(item.AudioSourceUri);
        }

        return true;
    }

    private void TriggerPlaybackSessionPersist(bool forceImmediate)
    {
        long nowMs = Environment.TickCount64;
        if (!forceImmediate)
        {
            long prev = Volatile.Read(ref _lastPlaybackSessionPersistTickMs);
            if (nowMs - prev < 15_000)
            {
                return;
            }
        }

        Volatile.Write(ref _lastPlaybackSessionPersistTickMs, nowMs);
        _ = PersistPlaybackSessionAsync();
    }

    /// <summary>
    /// Saves the active queue, current track, playback position, shuffle/repeat, and volume to disk.
    /// </summary>
    public async Task PersistPlaybackSessionAsync()
    {
        try
        {
            var snapshot = _audioPlayerService.CapturePlaybackSessionSnapshot();
            if (snapshot is null)
            {
                return;
            }

            await _storageService.SaveAsync(PersistedPlaybackSessionState.StorageKey, snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist playback session state.");
        }
    }

    private void OnLanguageChanged(object? sender, LanguageOption language)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            CurrentFlowDirection = language.IsRightToLeft
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;

            Loc.RefreshAllTrackedElements(_localizationService);
            RefreshUpdateLocalizedText();
        });
    }
}

/// <summary>
/// Category filter tabs on the Android-aligned Liked screen.
/// </summary>
public enum LikedFilterTab
{
    Songs = 0,
    Artists = 1,
    Albums = 2
}

/// <summary>
/// Sort modes on the Android-aligned Liked screen.
/// </summary>
public enum LikedSortMode
{
    Alphabetical = 0,
    Recent = 1
}

/// <summary>
/// Category filter pills on the dedicated Search Results view.
/// </summary>
public enum SearchResultFilterTab
{
    All = 0,
    Songs = 1,
    Artists = 2,
    Albums = 3,
    Playlists = 4
}

/// <summary>
/// Presentation tile model for a single Mood or Genre card in the Search explorer
/// (with precomputed horizontal stripe gradient matching Android <c>CategoryStripeCard</c>).
/// </summary>
public sealed partial class MoodGenreTileViewModel : ObservableObject
{
    public MoodGenreCategoryItem Category { get; }

    public string Title => Category.Title;

    public string StripeColorHex => Category.StripeColorHex;

    public Windows.UI.Color StripeColor { get; }

    public Microsoft.UI.Xaml.Media.SolidColorBrush StripeBrush { get; }

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.LinearGradientBrush CardGradientBrush { get; set; }

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.SolidColorBrush CardBorderBrush { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRingOpacity))]
    public partial bool IsSelected { get; set; }

    public double SelectedRingOpacity => IsSelected ? 1.0 : 0.0;

    public MoodGenreTileViewModel(MoodGenreCategoryItem category, bool isLightMode)
    {
        Category = category;
        uint argb = category.StripeColorArgb;
        StripeColor = Windows.UI.Color.FromArgb(
            255,
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));
        StripeBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(StripeColor);
        CardGradientBrush = BuildGradientBrush(StripeColor, isLightMode);
        CardBorderBrush = BuildBorderBrush(StripeColor, isLightMode);
    }

    public void RefreshBrushesForTheme(bool isLightMode)
    {
        CardGradientBrush = BuildGradientBrush(StripeColor, isLightMode);
        CardBorderBrush = BuildBorderBrush(StripeColor, isLightMode);
    }

    private static Microsoft.UI.Xaml.Media.LinearGradientBrush BuildGradientBrush(Windows.UI.Color stripe, bool isLightMode)
    {
        byte startAlpha = isLightMode ? (byte)38 : (byte)64; // ~15% light / ~25% dark
        var startColor = Windows.UI.Color.FromArgb(startAlpha, stripe.R, stripe.G, stripe.B);
        var midColor = isLightMode
            ? Windows.UI.Color.FromArgb(20, stripe.R, stripe.G, stripe.B)
            : Windows.UI.Color.FromArgb(28, stripe.R, stripe.G, stripe.B);
        var endColor = isLightMode
            ? Windows.UI.Color.FromArgb(230, 245, 247, 252)
            : Windows.UI.Color.FromArgb(184, 18, 19, 26);

        var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = startColor, Offset = 0.0 });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = midColor, Offset = 0.42 });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = endColor, Offset = 1.0 });
        return brush;
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush BuildBorderBrush(Windows.UI.Color stripe, bool isLightMode)
    {
        byte alpha = isLightMode ? (byte)54 : (byte)68;
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(alpha, stripe.R, stripe.G, stripe.B));
    }
}

/// <summary>
/// Section wrapper for a group of Mood/Genre cards ("For you", "Moods &amp; moments", "Genres").
/// </summary>
public sealed class MoodGenreSectionViewModel
{
    public string Title { get; }

    public string CountText { get; }

    public ObservableCollection<MoodGenreTileViewModel> Items { get; }

    public MoodGenreSectionViewModel(string title, IEnumerable<MoodGenreTileViewModel> items)
    {
        Title = title;
        Items = new ObservableCollection<MoodGenreTileViewModel>(items);
        CountText = Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Presentation wrapper for <see cref="UserPlaylist"/> providing exact 4-state cover resolution
/// (custom cover, 2×2 collage when >= 2 distinct covers, single full-bleed cover when 1 cover, or default icon),
/// track count badge, and formatted duration without modifying Core entities.
/// </summary>
public sealed class PlaylistCardViewModel
{
    private readonly IReadOnlyList<string> _distinctCovers;

    public UserPlaylist Playlist { get; }

    public string Id => Playlist.Id;

    public string Title => Playlist.Title;

    public string Description => Playlist.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Playlist.Description);

    public Visibility DescriptionVisibility => HasDescription ? Visibility.Visible : Visibility.Collapsed;

    public bool HasCustomCover => Playlist.HasCustomCover;

    public string SafeCustomCoverUri => Playlist.SafeCustomCoverUri;

    public bool HasTracks => Playlist.Tracks.Count > 0;

    public bool IsSmartSuggestion => Playlist.IsSmartSuggestion;

    public bool IsFromYouTubeMusic => Playlist.IsFromYouTubeMusic;

    public Visibility YouTubeMusicBadgeVisibility =>
        IsFromYouTubeMusic ? Visibility.Visible : Visibility.Collapsed;

    public string RemoveActionLabel =>
        IsFromYouTubeMusic ? "Remove from App" : "Delete Playlist";

    public string SmartBadgeText => !string.IsNullOrWhiteSpace(Playlist.SmartBadgeText)
        ? Playlist.SmartBadgeText
        : TrackBadgeText;

    public string SmartCategoryGlyph => Playlist.SmartCategoryGlyph;

    public bool IsSavedToLibrary => Playlist.IsSavedToLibrary;

    public string SaveButtonLabel => Playlist.SaveButtonLabel;

    public string SaveButtonGlyph => Playlist.SaveButtonGlyph;

    public Visibility SmartSuggestionSaveVisibility =>
        IsSmartSuggestion && !IsSavedToLibrary ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SmartSuggestionSavedBadgeVisibility =>
        IsSmartSuggestion && IsSavedToLibrary ? Visibility.Visible : Visibility.Collapsed;

    public Visibility QuickPlayVisibility => HasTracks ? Visibility.Visible : Visibility.Collapsed;

    public double ActionButtonOpacity => HasTracks ? 1.0 : 0.45;

    public string TrackBadgeText => Playlist.Tracks.Count == 1 ? "1 track" : $"{Playlist.Tracks.Count} tracks";

    public string DisplaySubtitle => !string.IsNullOrWhiteSpace(Playlist.Description)
        ? Playlist.Description
        : Playlist.TrackCountSubtitle;

    public string FormattedDuration { get; }

    public string DetailMetadataPillText { get; }

    public double CustomCoverOpacity => HasCustomCover ? 1.0 : 0.0;

    public double CollageGridOpacity => !HasCustomCover && _distinctCovers.Count >= 2 ? 1.0 : 0.0;

    public double SingleCoverOpacity => !HasCustomCover && _distinctCovers.Count == 1 ? 1.0 : 0.0;

    public double DefaultCoverIconOpacity => !HasCustomCover && _distinctCovers.Count == 0 ? 1.0 : 0.0;

    public string SingleCoverUri => _distinctCovers.Count > 0
        ? _distinctCovers[0]
        : TrackItem.FallbackTransparentCoverUri;

    /// <summary>
    /// Gets the primary cover art URI for dynamic Playlist Detail ambient palette extraction
    /// (custom cover photo first, then first distinct collage/track cover, or <c>null</c> for default theme fallback).
    /// </summary>
    public string? PrimaryCoverArtUri => HasCustomCover
        ? SafeCustomCoverUri
        : (_distinctCovers.Count > 0 ? _distinctCovers[0] : null);

    /// <summary>
    /// Returns all distinct track covers available on this playlist as ordered fallbacks if primary extraction fails.
    /// </summary>
    public IReadOnlyList<string> GetFallbackCoverUris() => _distinctCovers;

    public string CollageCover1 => GetSlotUri(0);
    public string CollageCover2 => GetSlotUri(1);
    public string CollageCover3 => GetSlotUri(2);
    public string CollageCover4 => GetSlotUri(3);

    public PlaylistCardViewModel(UserPlaylist playlist)
    {
        Playlist = playlist;
        _distinctCovers = playlist.GetDistinctCollageCovers();
        double totalSeconds = playlist.Tracks.Sum(t => t.Duration.TotalSeconds);
        FormattedDuration = totalSeconds > 1.0 ? ShellViewModel.FormatCollectionDuration(totalSeconds) : "0m";
        DetailMetadataPillText = playlist.Tracks.Count > 0
            ? $"{playlist.TrackCountSubtitle} · {FormattedDuration}"
            : "0 songs";
    }

    private string GetSlotUri(int index)
    {
        if (_distinctCovers.Count == 0)
        {
            return TrackItem.FallbackTransparentCoverUri;
        }

        return _distinctCovers[index % _distinctCovers.Count];
    }
}

/// <summary>
/// Indexed track row item for the Playlist Detail flat tracklist (1-based index + reactive <see cref="TrackItem"/>).
/// </summary>
public sealed class PlaylistTrackRowItem
{
    public int Index { get; }

    public string IndexText => Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public TrackItem Track { get; }

    public PlaylistTrackRowItem(int index, TrackItem track)
    {
        Index = index;
        Track = track;
    }
}
