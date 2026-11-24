using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Encapsulates the filtered results of a media catalog query across tracks, albums, artists, and recently added order.
/// </summary>
public sealed record CatalogQueryResult(
    IReadOnlyList<TrackItem> Songs,
    IReadOnlyList<AlbumItem> Albums,
    IReadOnlyList<ArtistItem> Artists,
    IReadOnlyList<ArtistItem> RecommendedArtists,
    IReadOnlyList<TrackItem>? RecentlyAdded = null);

/// <summary>
/// Provides unified access to local filesystem music libraries, YouTube Music streaming catalogs,
/// YouTube Music recommendations, Artist Detail profiles, and Album Detail profiles.
/// </summary>
public interface IMediaCatalogService
{
    /// <summary>
    /// Initializes the media catalog, generating cover artwork and loading initial tracks asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries and filters the catalog asynchronously without blocking the UI thread.
    /// </summary>
    /// <param name="searchQuery">Optional search text matching title, artist, or album.</param>
    /// <param name="category">Active category filter (All, Songs, Albums, Artists).</param>
    /// <param name="sourceType">Active source filter (All, Local, Online).</param>
    /// <param name="section">Active top-level navigation section (Home, Library, Liked, Playlists, Community).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="CatalogQueryResult"/> containing the matching entities.</returns>
    Task<CatalogQueryResult> QueryCatalogAsync(
        string? searchQuery,
        SearchCategoryFilter category,
        MediaSourceType sourceType,
        NavigationSection section,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the personalized Home discovery feed powered directly by YouTube Music
    /// based on the user's recently listened tracks, including genre recommendations and related artists.
    /// </summary>
    /// <param name="selectedGenreTag">Optional active genre tag to filter the genre spotlight rail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A populated <see cref="HomeDiscoveryFeed"/>.</returns>
    Task<HomeDiscoveryFeed> GetHomeDiscoveryFeedAsync(
        string? selectedGenreTag = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a track was played by the user so the Home screen recommendations and Recently Played album mosaic
    /// dynamically adapt to the user's listening habits (deduplicating by album and moving re-played albums to index 0).
    /// </summary>
    /// <param name="track">The track that started playing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordTrackPlayedAsync(
        TrackItem track,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a snapshot of the deduplicated recently played albums ordered from most recently played to oldest.
    /// </summary>
    IReadOnlyList<RecentAlbumItem> GetRecentlyPlayedAlbumsSnapshot();

    /// <summary>
    /// Resolves the complete Artist Detail profile including the official YouTube Music artist photo,
    /// YouTube Music biography &amp; listener metrics, Top songs, and combined YouTube Music + Local albums.
    /// </summary>
    /// <param name="artistName">The artist name to inspect.</param>
    /// <param name="existingAvatarUri">Optional known avatar URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A populated <see cref="ArtistDetailProfile"/>.</returns>
    Task<ArtistDetailProfile> GetArtistDetailAsync(
        string artistName,
        string? existingAvatarUri = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the complete Album Detail profile including metadata information, ordered album tracks,
    /// and recommended/other albums by the same artist or similar genres.
    /// </summary>
    /// <param name="album">The target album item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A populated <see cref="AlbumDetailProfile"/>.</returns>
    Task<AlbumDetailProfile> GetAlbumDetailAsync(
        AlbumItem album,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all liked tracks across both local files and online YouTube Music streams.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The list of liked <see cref="TrackItem"/> instances.</returns>
    Task<IReadOnlyList<TrackItem>> GetLikedSongsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans a local folder on disk for supported audio files (.mp3, .flac, .wav, .m4a, .ogg)
    /// and imports them into the local catalog using background I/O.
    /// </summary>
    /// <param name="folderPath">Absolute path to the directory to scan.</param>
    /// <param name="progress">Optional progress reporter indicating the number of imported tracks.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The list of newly discovered local tracks.</returns>
    Task<IReadOnlyList<TrackItem>> ScanLocalFolderAsync(
        string folderPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports specific local audio files into the catalog.
    /// </summary>
    /// <param name="filePaths">Collection of absolute file paths.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The imported <see cref="TrackItem"/> instances.</returns>
    Task<IReadOnlyList<TrackItem>> ImportLocalFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggles the liked/favorite status of a track (local or online) and persists the change.
    /// </summary>
    /// <param name="trackId">Target track ID.</param>
    /// <param name="trackSnapshot">Optional track object when liking an online YouTube Music track.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated liked state.</returns>
    Task<bool> ToggleTrackLikedAsync(
        string trackId,
        TrackItem? trackSnapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all cached tracks from the catalog and storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves or reloads synchronized lyrics (.lrc companion file on disk or embedded tags) for the specified track.
    /// </summary>
    /// <param name="track">The track to resolve lyrics for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ordered list of <see cref="LyricLine"/> entries.</returns>
    Task<IReadOnlyList<LyricLine>> ResolveTrackLyricsAsync(
        TrackItem track,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports an external .lrc file and associates its synchronized lyrics with the specified track.
    /// </summary>
    /// <param name="trackId">Target track ID.</param>
    /// <param name="lrcFilePath">Absolute path to the .lrc file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed list of <see cref="LyricLine"/> entries.</returns>
    Task<IReadOnlyList<LyricLine>> ImportLrcFileForTrackAsync(
        string trackId,
        string lrcFilePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the list of configured local music directories scanned by the library.
    /// </summary>
    IReadOnlyList<string> GetLocalMusicDirectories();

    /// <summary>
    /// Adds a local music directory to the library, scans its audio files, and persists the directory list.
    /// </summary>
    /// <param name="folderPath">Absolute folder path to add.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<TrackItem>> AddLocalMusicDirectoryAsync(
        string folderPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a local music directory from the configured directories and removes tracks belonging to that folder.
    /// </summary>
    /// <param name="folderPath">Absolute folder path to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveLocalMusicDirectoryAsync(
        string folderPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a local or downloaded offline equivalent track for the specified track (matching by YouTubeVideoId, file path, or Title + Artist).
    /// Used to prioritize offline playback over streaming when a song is already downloaded.
    /// </summary>
    /// <param name="track">The candidate track to check.</param>
    /// <returns>The local <see cref="TrackItem"/> if downloaded/local exists; otherwise <c>null</c>.</returns>
    TrackItem? FindLocalOfflineEquivalent(TrackItem track);

    /// <summary>
    /// Retrieves all user-created playlists.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<UserPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new user playlist with optional description, custom cover image, and initial track.
    /// </summary>
    /// <param name="title">Playlist title.</param>
    /// <param name="description">Optional playlist description.</param>
    /// <param name="customCoverPath">Optional custom cover image path.</param>
    /// <param name="initialTrack">Optional first track to add immediately.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UserPlaylist> CreatePlaylistAsync(
        string title,
        string? description = null,
        string? customCoverPath = null,
        TrackItem? initialTrack = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a user playlist by ID.
    /// </summary>
    /// <param name="playlistId">Playlist ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeletePlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a track to an existing user playlist and persists the playlist.
    /// </summary>
    /// <param name="playlistId">Target playlist ID.</param>
    /// <param name="track">Track to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UserPlaylist?> AddTrackToPlaylistAsync(
        string playlistId,
        TrackItem track,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a track from a user playlist and persists the playlist.
    /// </summary>
    /// <param name="playlistId">Target playlist ID.</param>
    /// <param name="trackId">Track ID to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UserPlaylist?> RemoveTrackFromPlaylistAsync(
        string playlistId,
        string trackId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates personalized smart playlist suggestions based on the user's playback history
    /// ("Top 20 de la última semana", "Mix de Géneros Escuchados", "En Repetición", "Descubrimiento según tus gustos")
    /// WITHOUT automatically saving them into the user's saved Playlists library.
    /// </summary>
    Task<IReadOnlyList<UserPlaylist>> GetSmartPlaylistSuggestionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly saves an auto-generated smart playlist suggestion into the user's saved Playlists library
    /// when the user clicks "Guardar en biblioteca".
    /// </summary>
    Task<UserPlaylist> SaveSmartPlaylistToLibraryAsync(
        UserPlaylist smartPlaylist,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports or refreshes a playlist from the user's YouTube Music account into the local Playlists library.
    /// Removing an imported playlist via <see cref="DeletePlaylistAsync"/> only deletes it locally from the app.
    /// </summary>
    Task<UserPlaylist> ImportYouTubeMusicPlaylistAsync(
        YouTubeMusicLibraryPlaylist playlist,
        IReadOnlyList<TrackItem> tracks,
        string? accountLabel = null,
        string? description = null,
        string? coverUri = null,
        CancellationToken cancellationToken = default);
}
