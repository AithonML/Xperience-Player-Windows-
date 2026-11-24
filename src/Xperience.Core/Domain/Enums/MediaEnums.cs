namespace Xperience.Core.Domain.Enums;

/// <summary>
/// Defines the origin source of a media item or catalog query.
/// </summary>
public enum MediaSourceType
{
    /// <summary>
    /// Includes both local filesystem and online streaming catalog items.
    /// </summary>
    All = 0,

    /// <summary>
    /// Media stored locally on the user's device.
    /// </summary>
    Local = 1,

    /// <summary>
    /// Media streamed from online endpoints or cloud providers.
    /// </summary>
    Online = 2
}

/// <summary>
/// Defines the active category filter in the main catalog/search view.
/// </summary>
public enum SearchCategoryFilter
{
    /// <summary>
    /// Displays all sections (Songs, Albums, Artists, Recommendations).
    /// </summary>
    All = 0,

    /// <summary>
    /// Focuses on individual song tracks.
    /// </summary>
    Songs = 1,

    /// <summary>
    /// Focuses on music albums.
    /// </summary>
    Albums = 2,

    /// <summary>
    /// Focuses on recording artists.
    /// </summary>
    Artists = 3
}

/// <summary>
/// Defines the top-level navigation sections in the application header bar.
/// </summary>
public enum NavigationSection
{
    /// <summary>
    /// Personalized Home discovery feed powered directly by YouTube Music and recent listening.
    /// </summary>
    Home = 0,

    /// <summary>
    /// Main music library and unified search discovery view.
    /// </summary>
    Library = 1,

    /// <summary>
    /// User's favorited/liked tracks and albums (supports List and Grid views).
    /// </summary>
    Liked = 2,

    /// <summary>
    /// Custom and curated playlists.
    /// </summary>
    Playlists = 3,

    /// <summary>
    /// Community shared playlists, live rooms, and recommendations.
    /// </summary>
    Community = 4
}

/// <summary>
/// Defines the active drill-down detail view inside the main workspace.
/// </summary>
public enum WorkspaceSubView
{
    /// <summary>
    /// Displays the active top-level <see cref="NavigationSection"/> (Home, Library, Liked, etc.).
    /// </summary>
    MainSection = 0,

    /// <summary>
    /// Displays the dedicated Artist Detail view (official photo, bio, Top 10 songs, combined YT Music + Local albums).
    /// </summary>
    ArtistDetail = 1,

    /// <summary>
    /// Displays the dedicated Album Detail view (animated Apple Canvas cover, metadata, tracklist, and related albums).
    /// </summary>
    AlbumDetail = 2,

    /// <summary>
    /// Displays the dedicated User Playlist Detail view (collage/custom cover, metadata, and tracklist).
    /// </summary>
    PlaylistDetail = 3,

    /// <summary>
    /// Displays the full-workspace Settings Studio view with category navigation sidebar.
    /// </summary>
    Settings = 4,

    /// <summary>
    /// Displays the expanded desktop grid sub-page for Home New Releases and Radio Stations.
    /// </summary>
    HomeCategoryGrid = 5
}

/// <summary>
/// Identifies the active category inside the Home expanded grid sub-page.
/// </summary>
public enum HomeGridCategory
{
    /// <summary>
    /// New music releases (albums and singles).
    /// </summary>
    NewReleases = 0,

    /// <summary>
    /// Recommended radio stations and personal mixes.
    /// </summary>
    RadioStations = 1
}

/// <summary>
/// Filter chips for the New Releases expanded grid sub-page.
/// </summary>
public enum NewReleasesFilter
{
    /// <summary>
    /// Displays both albums and singles.
    /// </summary>
    All = 0,

    /// <summary>
    /// Displays only studio albums and EPs.
    /// </summary>
    Albums = 1,

    /// <summary>
    /// Displays only singles.
    /// </summary>
    Singles = 2
}

/// <summary>
/// Defines the categories inside the full-workspace Settings screen.
/// </summary>
public enum SettingsCategory
{
    /// <summary>
    /// Theme, Accent Palette, Backdrop Material, and Liquid Glass Refraction settings.
    /// </summary>
    AppearanceAndGlass = 0,

    /// <summary>
    /// Local music folders management (add/remove multiple directories) and audio file import.
    /// </summary>
    LibraryAndFolders = 1,

    /// <summary>
    /// YouTube Music account authentication for personalized Home feed recommendations.
    /// </summary>
    YouTubeMusicAccount = 2,

    /// <summary>
    /// Offline music downloader defaults (MP3/FLAC, embedded tags/cover/lyrics, target folder).
    /// </summary>
    DownloadsAndOffline = 3,

    /// <summary>
    /// Synchronized lyrics providers, .LRC import, and GPU audio visualizer presets.
    /// </summary>
    LyricsAndVisualizers = 4,

    /// <summary>
    /// Application updates, release verification, and installed version details.
    /// </summary>
    Updates = 5
}

/// <summary>
/// Defines the layout presentation mode for the Liked Songs screen.
/// </summary>
public enum LikedSongsViewMode
{
    /// <summary>
    /// Displays liked songs as sleek horizontal pill rows.
    /// </summary>
    List = 0,

    /// <summary>
    /// Displays liked songs as a responsive card grid.
    /// </summary>
    Grid = 1
}

/// <summary>
/// Defines the active sub-panel in the Now Playing right sidebar.
/// </summary>
public enum NowPlayingTab
{
    /// <summary>
    /// Displays the upcoming playback queue.
    /// </summary>
    Queue = 0,

    /// <summary>
    /// Displays time-synchronized or static track lyrics.
    /// </summary>
    Lyrics = 1
}

/// <summary>
/// Defines the window and surface backdrop material rendering strategy.
/// </summary>
public enum BackdropMaterialKind
{
    /// <summary>
    /// Combines Windows 11 Mica with custom Win2D GPU Liquid Glass refraction panels.
    /// </summary>
    LiquidGlassMica = 0,

    /// <summary>
    /// Uses Windows 11 Mica Alt backdrop with subtle glass surfaces.
    /// </summary>
    MicaAlt = 1,

    /// <summary>
    /// Uses Desktop Acrylic backdrop with translucent blur.
    /// </summary>
    DesktopAcrylic = 2,

    /// <summary>
    /// Disables GPU backdrop shaders and uses solid surfaces for maximum battery/CPU efficiency.
    /// </summary>
    SolidPerformance = 3
}

/// <summary>
/// Defines the playback repeat behavior.
/// </summary>
public enum PlaybackRepeatMode
{
    /// <summary>
    /// Plays through the queue once.
    /// </summary>
    None = 0,

    /// <summary>
    /// Loops the entire playback queue indefinitely.
    /// </summary>
    RepeatAll = 1,

    /// <summary>
    /// Loops the currently active track indefinitely.
    /// </summary>
    RepeatOne = 2
}
