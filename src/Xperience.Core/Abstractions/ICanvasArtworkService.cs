namespace Xperience.Core.Abstractions;

/// <summary>
/// Represents a resolved animated album cover (Apple Music Canvas / Motion Artwork) video stream.
/// </summary>
public sealed record CanvasArtworkResult(
    string VideoUri,
    string ProviderName,
    string? PreviewFrameUri = null,
    string? CanonicalCanvasKey = null,
    string? AppleAlbumId = null);

/// <summary>
/// Represents cached or freshly resolved Apple Music artist media assets (wordmark, motion video, banner, palette).
/// </summary>
public sealed record ArtistAssetsResult(
    string AppleArtistId = "",
    string ArtistName = "",
    string? WordmarkUri = null,
    string? HeaderVideoUri = null,
    string? BannerUri = null,
    string? BgColorHex = null,
    string? KeyColorHex = null);

/// <summary>
/// Resolves animated album cover artwork (Apple Music Editorial Motion Video / Canvas,
/// plus EchoMusic and ViviMusic Canvas fallbacks) for the currently playing track.
/// </summary>
public interface ICanvasArtworkService
{
    /// <summary>
    /// Synchronously returns any in-memory or disk-cached animated Canvas artwork for the specified track/album
    /// without performing any network I/O. Returns <c>null</c> if not yet cached locally.
    /// </summary>
    CanvasArtworkResult? PeekCanvasArtwork(
        string title,
        string artist,
        string? album = null);

    /// <summary>
    /// Ensures a local file path or remote MP4/HLS canvas video URI is cached locally and normalized
    /// into a standard fast-start flat MP4 so Windows Media Foundation loops it with zero stalls or network traffic.
    /// </summary>
    Task<string?> EnsureLocalLoopableVideoAsync(
        string videoUriOrPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches Apple Music's catalog (and fallback canvas providers) for an animated motion artwork
    /// matching the specified song <paramref name="title"/>, <paramref name="artist"/>, and optional <paramref name="album"/>.
    /// Resolves and caches an H.264 SDR <c>.mp4</c> loopable video stream compatible with Windows Media Foundation.
    /// </summary>
    /// <param name="title">The track title.</param>
    /// <param name="artist">The track artist.</param>
    /// <param name="album">The optional album title.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="CanvasArtworkResult"/> if an animated cover exists; otherwise <c>null</c>.</returns>
    Task<CanvasArtworkResult?> GetCanvasArtworkAsync(
        string title,
        string artist,
        string? album = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously returns any in-memory or persisted Apple Music artist assets for <paramref name="artistName"/> without network I/O.
    /// </summary>
    ArtistAssetsResult? PeekArtistAssets(string artistName);

    /// <summary>
    /// Resolves rich Apple Music artist assets (wordmark PNG, progressive 16:9 header motion video stream,
    /// editorial wide banner, and seed color hex) using the fast Apple Music AMP API + disk cache.
    /// </summary>
    Task<ArtistAssetsResult?> GetArtistAssetsAsync(
        string artistName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the custom stylized artist title logo/wordmark artwork URL (<c>1200x400bb.png</c>)
    /// from Apple Music's artist header (<c>artistLogo</c>) when the artist has configured one.
    /// </summary>
    /// <param name="artistName">The artist name to look up on Apple Music.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The high-resolution transparent PNG wordmark URL if available; otherwise <c>null</c>.</returns>
    Task<string?> GetArtistWordmarkLogoUriAsync(
        string artistName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the widescreen 16:9 looping artist header motion video (<c>wideVideoArtwork</c> /
    /// <c>motionArtistFullscreen16x9</c> / <c>motionArtistWide16x9</c>) from Apple Music into a direct
    /// H.264 SDR <c>1920x1080-.mp4</c> stream or local cached file when available for the artist.
    /// </summary>
    /// <param name="artistName">The artist name to look up on Apple Music.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A direct playable <c>.mp4</c> URI (or cached file path) if the artist has a video header; otherwise <c>null</c>.</returns>
    Task<string?> GetArtistHeaderVideoUriAsync(
        string artistName,
        CancellationToken cancellationToken = default);
}
