using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Provides multi-quality track downloading (FLAC Lossless via Mornye Tidal/Qobuz/Deezer pipeline,
/// MP3 320kbps via YouTube Music, and M4A), complete ID3v2/Vorbis/MP4 metadata tagging,
/// synchronized/static lyrics embedding (.lrc), and offline Apple Music Canvas video sidecars (.canvas.mp4).
/// </summary>
public interface IMusicDownloadService
{
    /// <summary>
    /// Gets the current user-configured download and metadata options.
    /// </summary>
    DownloadMetadataOptions Options { get; }

    /// <summary>
    /// Loads persisted download and metadata preferences from storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves updated download and metadata preferences to persistent storage.
    /// </summary>
    /// <param name="options">The updated options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveOptionsAsync(DownloadMetadataOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches Mornye lossless/metadata providers (Tidal, Deezer, GDStudio, and YouTube Music)
    /// for the specified track, returning the best recommended match first followed by alternative versions/suggestions.
    /// </summary>
    /// <param name="track">The track to search candidates for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A ranked list of <see cref="TrackDownloadCandidate"/> items.</returns>
    Task<IReadOnlyList<TrackDownloadCandidate>> SearchDownloadCandidatesAsync(
        TrackItem track,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the specified track in the requested <paramref name="formatOverride"/> (or <see cref="Options"/> default format),
    /// enriches it with Tidal/Qobuz/Deezer/YouTube Music metadata, embeds cover art and lyrics, saves companion
    /// <c>.lrc</c> and <c>.canvas.mp4</c> files for offline mode, and imports the resulting file into the local library.
    /// </summary>
    /// <param name="track">The track to download.</param>
    /// <param name="formatOverride">Optional explicit format override (FLAC Lossless, MP3 320kbps, or M4A).</param>
    /// <param name="progressStatus">Optional progress status callback for live UI feedback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="selectedCandidate">Optional user-selected candidate version to download.</param>
    /// <param name="detailedProgress">Optional structured percentage + stage progress callback.</param>
    /// <returns>A <see cref="TrackDownloadResult"/> describing the downloaded artifacts.</returns>
    Task<TrackDownloadResult> DownloadTrackAsync(
        TrackItem track,
        DownloadAudioFormat? formatOverride = null,
        IProgress<string>? progressStatus = null,
        CancellationToken cancellationToken = default,
        TrackDownloadCandidate? selectedCandidate = null,
        IProgress<TrackDownloadProgressReport>? detailedProgress = null);
}
