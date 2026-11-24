using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Provides lightweight reverse-engineered YouTube Music (InnerTube WEB_REMIX + iOS/Android player)
/// search, metadata extraction, and direct audio stream URI resolution inspired by <c>sigma67/ytmusicapi</c>.
/// </summary>
public interface IYouTubeMusicService
{
    /// <summary>
    /// Searches YouTube Music for songs, albums, and artists matching the specified query.
    /// When <paramref name="query"/> is empty, returns a curated discovery feed from YouTube Music.
    /// </summary>
    /// <param name="query">The search query (e.g. song title, artist, or album).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="CatalogQueryResult"/> populated with YouTube Music tracks, albums, and artists.</returns>
    Task<CatalogQueryResult> SearchCatalogAsync(
        string? query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a direct, playable high-bitrate audio stream URL (M4A/AAC or WebM/Opus) for a YouTube Music <paramref name="videoId"/>.
    /// </summary>
    /// <param name="videoId">The 11-character YouTube Music videoId.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The direct HTTP stream URL, or <c>null</c> if resolution failed.</returns>
    Task<string?> ResolveAudioStreamUriAsync(
        string videoId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the official ordered tracklist for a specific album on YouTube Music
    /// using the InnerTube <c>browse</c> (<c>MPREb_...</c>) album endpoint.
    /// </summary>
    /// <param name="artistName">The album's artist name.</param>
    /// <param name="albumTitle">The album title.</param>
    /// <param name="fallbackCoverArtUri">Optional fallback cover art URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="explicitBrowseId">Optional explicit InnerTube browseId (e.g., MPREb_... or VL...).</param>
    /// <returns>The ordered list of tracks belonging strictly to this album.</returns>
    Task<IReadOnlyList<TrackItem>> GetAlbumTracksAsync(
        string artistName,
        string albumTitle,
        string? fallbackCoverArtUri = null,
        CancellationToken cancellationToken = default,
        string? explicitBrowseId = null);

    /// <summary>
    /// Retrieves the complete official YouTube Music Album Profile (high-resolution cover artwork,
    /// release year, editorial description, ordered 1..N tracklist, Other Versions, and More by Artist)
    /// directly from the InnerTube <c>browse</c> (<c>MPREb_...</c> or <c>VL...</c>) endpoint.
    /// </summary>
    /// <param name="artistName">The album's artist name.</param>
    /// <param name="albumTitle">The album title.</param>
    /// <param name="fallbackCoverArtUri">Optional fallback cover art URI.</param>
    /// <param name="explicitBrowseId">Optional explicit InnerTube browseId (e.g., MPREb_... or VL...).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="AlbumDetailProfile"/> populated directly from YouTube Music, or <c>null</c> if not found.</returns>
    Task<AlbumDetailProfile?> GetAlbumProfileAsync(
        string artistName,
        string albumTitle,
        string? fallbackCoverArtUri = null,
        string? explicitBrowseId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the complete official YouTube Music Artist Profile (wide hero banner, monthly listeners,
    /// subscriber count, full biography, top songs ordered by play count, studio albums, Singles &amp; EPs,
    /// artist/featured playlists, and related artists) directly from the InnerTube <c>browse</c> endpoint.
    /// </summary>
    /// <param name="artistName">The artist name to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="ArtistDetailProfile"/> populated exclusively from YouTube Music, or <c>null</c> if not found.</returns>
    Task<ArtistDetailProfile?> GetArtistProfileAsync(
        string artistName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the official high-resolution artist portrait image URL directly from YouTube Music
    /// using the InnerTube Artists search filter (<c>EgWKAQIgAWoMEA4QChADEAQQCRAF</c>).
    /// </summary>
    /// <param name="artistName">The artist name to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The high-resolution artist portrait URL, or <c>null</c> if not found.</returns>
    Task<string?> GetArtistAvatarUriAsync(
        string artistName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves both the official high-resolution artist portrait image URL and YouTube Music audience subtitle
    /// (e.g., "65.4M monthly audience") for the specified artist using cached InnerTube Artist search results.
    /// </summary>
    Task<(string? AvatarUri, string? MonthlyAudienceText)> GetArtistSummaryAsync(
        string artistName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches YouTube Music specifically for albums matching <paramref name="query"/> using the InnerTube Albums filter.
    /// </summary>
    Task<IReadOnlyList<AlbumItem>> SearchAlbumsAsync(
        string query,
        bool strictlyAlbumsOnly = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the real track count for a YouTube Music album via cached InnerTube browse metadata.
    /// Returns <c>null</c> if the album's real track count cannot be determined.
    /// </summary>
    Task<int?> GetAlbumTrackCountAsync(
        string? browseId,
        string artistName,
        string albumTitle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches default trending songs, albums, and artists from YouTube Music charts (<c>FEmusic_charts</c>)
    /// with global top songs/albums fallbacks when no listening seeds exist yet.
    /// </summary>
    Task<CatalogQueryResult> FetchChartsCatalogAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when the YouTube Music account session state changes (connected, disconnected, or expired).
    /// </summary>
    event EventHandler? AccountSessionChanged;

    /// <summary>
    /// Gets a value indicating whether the user has an active, non-expired YouTube Music account session.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Gets a value indicating whether a previously saved YouTube Music cookie session was detected as expired/logged out by YouTube.
    /// </summary>
    bool IsSessionExpired { get; }

    /// <summary>
    /// Gets the display name or status of the connected YouTube Music account.
    /// </summary>
    string ConnectedAccountLabel { get; }

    /// <summary>
    /// Gets a human-readable explanation when the most recent stream resolution failed (e.g. age restriction or expired cookies).
    /// </summary>
    string? LastStreamFailureReason { get; }

    /// <summary>
    /// Saves and activates a YouTube Music session cookie header (containing SAPISID / __Secure-3PAPISID)
    /// for authenticated InnerTube <c>SAPISIDHASH</c> requests and age-restricted/explicit playback.
    /// </summary>
    /// <param name="cookieHeader">Raw Cookie header string from music.youtube.com.</param>
    /// <param name="accountLabel">Optional display name/email for the connected account.</param>
    /// <param name="visitorData">Optional YouTube VISITOR_DATA token captured from the browser session.</param>
    /// <param name="dataSyncId">Optional YouTube DATASYNC_ID token captured from the browser session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ConnectAccountCookieAsync(
        string cookieHeader,
        string? accountLabel = null,
        string? visitorData = null,
        string? dataSyncId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects the currently stored YouTube Music account session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DisconnectAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches personalized recommendations from the user's authenticated YouTube Music Home feed (<c>FEmusic_home</c>).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Personalized tracks recommended by the user's YouTube Music account.</returns>
    Task<IReadOnlyList<TrackItem>> GetAccountPersonalizedRecommendationsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches rich Home shelves (tracks, albums, playlists, radios, new releases, top picks) from <c>FEmusic_home</c>.
    /// </summary>
    Task<YtmHomeFeedResult> FetchPersonalizedHomeFeedAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches New Releases and Radio Stations from <c>FEmusic_new_releases_albums</c> and <c>FEmusic_explore</c>.
    /// </summary>
    Task<(IReadOnlyList<NewReleaseItem> NewReleases, IReadOnlyList<RadioStationItem> Radios)> FetchExploreAndNewReleasesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches YouTube Music for curated playlists matching the query.
    /// </summary>
    Task<IReadOnlyList<RecommendedPlaylistItem>> SearchPlaylistsAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves up to <paramref name="limit"/> similar albums for a given seed album ("More like: [Album]").
    /// </summary>
    Task<IReadOnlyList<AlbumItem>> GetSimilarAlbumsForSeedAsync(
        string seedAlbumTitle,
        string seedArtist,
        string? seedBrowseId = null,
        int limit = 15,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves playable tracks for a radio station or personal mix via InnerTube <c>next</c>.
    /// </summary>
    Task<IReadOnlyList<TrackItem>> ResolveRadioStationTracksAsync(
        RadioStationItem station,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves playable tracks for a recommended playlist via InnerTube <c>browse</c> or <c>next</c>.
    /// </summary>
    Task<IReadOnlyList<TrackItem>> ResolvePlaylistTracksAsync(
        RecommendedPlaylistItem playlist,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and caches remote YouTube Music artwork URIs to local disk using pooled HTTP/2 requests,
    /// returning a map from original URI to loadable <c>file:///</c> URI so WinUI image controls never drop covers.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> WarmArtworkCacheAsync(
        IEnumerable<string> artworkUris,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches YouTube Music Moods &amp; Genres sections (<c>For you</c>, <c>Moods &amp; moments</c>, <c>Genres</c>)
    /// from InnerTube <c>FEmusic_moods_and_genres</c> with a 30-minute in-memory cache and a clean curated fallback when offline.
    /// </summary>
    Task<MoodsAndGenresFeed> GetMoodsAndGenresAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Browses a selected YouTube Music Mood or Genre category using InnerTube <c>browseId</c> and optional <c>params</c>,
    /// returning curated playlists, featured albums, and songs with WIC-warmed cover artwork.
    /// </summary>
    Task<MoodGenreDetailResult> GetMoodGenreCategoryContentAsync(
        string browseId,
        string? @params,
        string categoryTitle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a rich categorized YouTube Music search returning a ranked Top Result alongside
    /// Songs, Albums, Artists, and Playlists, with all covers and artist portraits warmed via WIC artwork caching.
    /// </summary>
    Task<YouTubeMusicSearchResult> SearchRichCatalogAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches up to <paramref name="maxCount"/> unique similar studio audio (<c>ATV</c>) tracks seeded from
    /// <paramref name="seedTracks"/> via InnerTube <c>/next</c> (<c>RDAMVM&lt;videoId&gt;</c>) with filtered
    /// Songs search fallback, excluding all IDs present in <paramref name="excludeTrackIds"/>.
    /// </summary>
    Task<IReadOnlyList<TrackItem>> FetchSimilarTracksForQueueAsync(
        IReadOnlyList<TrackItem> seedTracks,
        IReadOnlyCollection<string> excludeTrackIds,
        int maxCount = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches YouTube Music for official music videos (<c>MUSIC_VIDEO_TYPE_OMV</c>) by the specified artist.
    /// </summary>
    Task<IReadOnlyList<TrackItem>> SearchArtistOfficialVideosAsync(
        string artistName,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a playable official music video counterpart (<c>MUSIC_VIDEO_TYPE_OMV</c>) and its local/direct MP4 video stream
    /// for the specified track to power the Fullscreen Audio / Video Switcher ("Canción | Video").
    /// </summary>
    Task<ResolvedMusicVideoStream?> ResolveMusicVideoStreamAsync(
        TrackItem track,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the signed-in user's YouTube Music library playlists from InnerTube <c>FEmusic_liked_playlists</c>
    /// across continuation pages. Returns an empty list if not authenticated.
    /// </summary>
    Task<IReadOnlyList<YouTubeMusicLibraryPlaylist>> FetchUserLibraryPlaylistsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches all playable tracks (including studio ATV promotions and retained OMV/video items),
    /// header artwork, and description for a user's YouTube Music playlist across continuation pages.
    /// </summary>
    Task<(IReadOnlyList<TrackItem> Tracks, string? CoverUri, string? Description)> FetchUserPlaylistTracksAsync(
        YouTubeMusicLibraryPlaylist playlist,
        CancellationToken cancellationToken = default);
}
