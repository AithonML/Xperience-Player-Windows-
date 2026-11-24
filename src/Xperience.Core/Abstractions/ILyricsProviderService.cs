using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Orchestrates multi-provider online synchronized lyrics resolution across
/// <c>YouLyPlus</c>, <c>Paxsenix</c>, <c>Better Lyrics</c>, <c>LRCLIB</c>, <c>KuGou</c>, and <c>SimpMusic</c>
/// with user-configurable priority ordering and progressive letter-by-letter / syllable-by-syllable timing.
/// </summary>
public interface ILyricsProviderService
{
    /// <summary>
    /// Gets the ordered list of lyrics provider configurations (in current priority order).
    /// </summary>
    IReadOnlyList<LyricsProviderConfig> Providers { get; }

    /// <summary>
    /// Initializes the service and loads persisted provider priority order from storage.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the specified provider up or down in the priority order and persists the change.
    /// </summary>
    /// <param name="providerId">The provider to move.</param>
    /// <param name="direction">-1 to move up (higher priority), +1 to move down (lower priority).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MoveProviderPriorityAsync(
        LyricsProviderId providerId,
        int direction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables a specific lyrics provider in the resolution pipeline and persists the change.
    /// </summary>
    Task SetProviderEnabledAsync(
        LyricsProviderId providerId,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the enabled online lyrics providers in priority order for the specified track.
    /// Supports both progressive word/syllable/letter synchronization and line-level synchronization.
    /// </summary>
    /// <param name="track">The track to fetch synchronized lyrics for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <see cref="LyricsResolutionResult"/> containing the provider name and synchronized lines.</returns>
    Task<LyricsResolutionResult?> FetchOnlineLyricsAsync(
        TrackItem track,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a synchronous in-memory cache check for <paramref name="track"/> without network I/O.
    /// Returns <c>true</c> when cached synchronized lyrics are immediately available.
    /// </summary>
    bool TryGetCachedLyrics(
        TrackItem track,
        out LyricsResolutionResult? result);

    /// <summary>
    /// Performs a synchronous in-memory cache check for <paramref name="track"/>, distinguishing
    /// between a positive hit, a recent negative (not-found) result, and a cache miss.
    /// </summary>
    LyricsCacheLookupState TryGetCachedLookup(
        TrackItem track,
        out LyricsResolutionResult? result);

    /// <summary>
    /// Silently prefetches and caches online synchronized lyrics for an upcoming track in the background
    /// without throwing on network errors or cancellation.
    /// </summary>
    Task PrefetchLyricsAsync(
        TrackItem track,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any cached positive or negative lyrics entries for <paramref name="track"/>.
    /// </summary>
    void InvalidateTrackCache(TrackItem track);
}
