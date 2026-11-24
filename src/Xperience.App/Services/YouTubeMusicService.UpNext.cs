using Microsoft.Extensions.Logging;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Playback;

namespace Xperience.App.Services;

public sealed partial class YouTubeMusicService
{
    private const int MaxSimilarSeedAttempts = 3;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackItem>> FetchSimilarTracksForQueueAsync(
        IReadOnlyList<TrackItem> seedTracks,
        IReadOnlyCollection<string> excludeTrackIds,
        int maxCount = 20,
        CancellationToken cancellationToken = default)
    {
        if (seedTracks is null || seedTracks.Count == 0 || maxCount <= 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Layer-1 deduplication sets matching Android's fetchSimilarTracksForQueue:
        // tracks raw, ytm_-stripped, ytm_-prefixed, and URI-extracted IDs plus title::primaryArtist keys.
        var excludedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludeTrackIds is not null)
        {
            foreach (string rawExcluded in excludeTrackIds)
            {
                AddExcludedIdForms(rawExcluded, excludedIds);
            }
        }

        var seenTitleArtistKeys = new HashSet<string>(StringComparer.Ordinal);
        TrackItem? primarySeed = null;
        foreach (var seed in seedTracks)
        {
            if (seed is null)
            {
                continue;
            }

            primarySeed ??= seed;

            if (!string.IsNullOrWhiteSpace(seed.Title))
            {
                seenTitleArtistKeys.Add(
                    TrackDeduplicationHelper.BuildTitlePrimaryArtistKey(seed.Title, seed.Artist));
            }

            AddExcludedTrackForms(seed, excludedIds);
        }

        if (primarySeed is null)
        {
            return [];
        }

        var results = new List<TrackItem>(Math.Min(maxCount, 32));

        void AcceptCandidate(TrackItem? candidate)
        {
            if (candidate is null || results.Count >= maxCount || string.IsNullOrWhiteSpace(candidate.Title))
            {
                return;
            }

            string candidateId = candidate.Id?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(candidateId) && excludedIds.Contains(candidateId))
            {
                return;
            }

            string? candidateVideoId = ExtractYtmSeedVideoId(candidate);
            if (!string.IsNullOrEmpty(candidateVideoId) &&
                (excludedIds.Contains(candidateVideoId) || excludedIds.Contains($"ytm_{candidateVideoId}")))
            {
                return;
            }

            foreach (string normalizedId in TrackDeduplicationHelper.ExtractNormalizedTrackIds(candidate))
            {
                if (excludedIds.Contains(normalizedId) || excludedIds.Contains($"ytm_{normalizedId}"))
                {
                    return;
                }
            }

            string titleArtistKey = TrackDeduplicationHelper.BuildTitlePrimaryArtistKey(candidate.Title, candidate.Artist);
            if (!seenTitleArtistKeys.Add(titleArtistKey))
            {
                return;
            }

            AddExcludedTrackForms(candidate, excludedIds);
            results.Add(candidate);
        }

        var attemptedAtvSeeds = new HashSet<string>(StringComparer.Ordinal);
        int processedSeeds = 0;

        foreach (var seed in seedTracks)
        {
            if (seed is null)
            {
                continue;
            }

            if (processedSeeds >= MaxSimilarSeedAttempts || results.Count >= maxCount)
            {
                break;
            }

            processedSeeds++;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string resolvedSeedId = await ResolveSeedAtvVideoIdAsync(seed, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(resolvedSeedId) || !attemptedAtvSeeds.Add(resolvedSeedId))
                {
                    continue;
                }

                AddExcludedIdForms(resolvedSeedId, excludedIds);

                var nextPayload = new Dictionary<string, object?>
                {
                    ["context"] = BuildWebRemixContext(),
                    ["isAudioOnly"] = true,
                    ["videoId"] = resolvedSeedId,
                    ["playlistId"] = $"RDAMVM{resolvedSeedId}"
                };

                using var nextDoc = await ExecuteInnerTubePostDocAsync(
                    YtMusicNextEndpoint,
                    nextPayload,
                    includeAuthHeaders: true,
                    cancellationToken).ConfigureAwait(false);

                if (nextDoc is null)
                {
                    continue;
                }

                foreach (var candidate in ParseNextPanelTracks(nextDoc.RootElement))
                {
                    AcceptCandidate(candidate);
                    if (results.Count >= maxCount)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Up-next radio seed resolution failed for '{Title}'.", seed.Title);
            }
        }

        // Remote search fallback ONLY if /next yielded zero accepted tracks across all seeds.
        if (results.Count == 0)
        {
            foreach (string query in BuildFallbackQueries(primarySeed))
            {
                if (results.Count >= maxCount)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var searchCandidates = await ExecuteSongsSearchAsync(
                        query,
                        useSongsFilter: true,
                        cancellationToken).ConfigureAwait(false);

                    foreach (var candidate in searchCandidates)
                    {
                        AcceptCandidate(candidate);
                        if (results.Count >= maxCount)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Similar-tracks search fallback failed for query '{Query}'.", query);
                }
            }
        }

        return results.Count <= maxCount ? results : results.GetRange(0, maxCount);
    }

    private async Task<string> ResolveSeedAtvVideoIdAsync(TrackItem seed, CancellationToken cancellationToken)
    {
        string? directYtmId = ExtractYtmSeedVideoId(seed);
        if (!string.IsNullOrWhiteSpace(directYtmId))
        {
            string resolved = await ResolveCanonicalAtvVideoIdAsync(directYtmId, cancellationToken).ConfigureAwait(false);
            return ExtractNormalizedRawVideoId(resolved);
        }

        string cleanTitle = (seed.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            return string.Empty;
        }

        string primaryArtist = TrackDeduplicationHelper.PrimaryArtistName(seed.Artist);
        string searchQuery = !IsPlaceholderArtist(primaryArtist)
            ? $"{cleanTitle} {primaryArtist}".Trim()
            : cleanTitle;

        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return string.Empty;
        }

        var matchedSongs = await ExecuteSongsSearchAsync(
            searchQuery,
            useSongsFilter: true,
            cancellationToken).ConfigureAwait(false);

        TrackItem? matched = matchedSongs.FirstOrDefault();
        string? matchedVideoId = ExtractYtmSeedVideoId(matched);
        if (string.IsNullOrWhiteSpace(matchedVideoId))
        {
            return string.Empty;
        }

        string resolvedMatched = await ResolveCanonicalAtvVideoIdAsync(matchedVideoId, cancellationToken).ConfigureAwait(false);
        return ExtractNormalizedRawVideoId(resolvedMatched);
    }

    private static string? ExtractYtmSeedVideoId(TrackItem? track)
    {
        if (track is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId))
        {
            string cleaned = ExtractNormalizedRawVideoId(track.YouTubeVideoId);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        if (!string.IsNullOrWhiteSpace(track.Id) &&
            track.Id.Trim().StartsWith("ytm_", StringComparison.OrdinalIgnoreCase))
        {
            string stripped = TrackDeduplicationHelper.StripYtmPrefix(track.Id);
            if (!string.IsNullOrWhiteSpace(stripped))
            {
                return stripped;
            }
        }

        string? uriVideoId = TrackDeduplicationHelper.ExtractYouTubeVideoIdFromUri(track.AudioSourceUri);
        if (!string.IsNullOrWhiteSpace(uriVideoId))
        {
            string stripped = TrackDeduplicationHelper.StripYtmPrefix(uriVideoId);
            if (!string.IsNullOrWhiteSpace(stripped))
            {
                return stripped;
            }
        }

        return null;
    }

    private static string ExtractNormalizedRawVideoId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        string? fromUri = TrackDeduplicationHelper.ExtractYouTubeVideoIdFromUri(trimmed);
        if (!string.IsNullOrWhiteSpace(fromUri))
        {
            return TrackDeduplicationHelper.StripYtmPrefix(fromUri);
        }

        return TrackDeduplicationHelper.StripYtmPrefix(trimmed);
    }

    private static void AddExcludedTrackForms(TrackItem track, HashSet<string> excludedIds)
    {
        AddExcludedIdForms(track.Id, excludedIds);
        AddExcludedIdForms(track.YouTubeVideoId, excludedIds);

        string? ytmSeedId = ExtractYtmSeedVideoId(track);
        if (!string.IsNullOrWhiteSpace(ytmSeedId))
        {
            AddExcludedIdForms(ytmSeedId, excludedIds);
        }

        foreach (string normalizedId in TrackDeduplicationHelper.ExtractNormalizedTrackIds(track))
        {
            if (!string.IsNullOrWhiteSpace(normalizedId))
            {
                excludedIds.Add(normalizedId);
                excludedIds.Add($"ytm_{normalizedId}");
            }
        }
    }

    private static void AddExcludedIdForms(string? rawId, HashSet<string> excludedIds)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return;
        }

        string trimmed = rawId.Trim();
        excludedIds.Add(trimmed);

        string cleaned = ExtractNormalizedRawVideoId(trimmed);
        if (!string.IsNullOrEmpty(cleaned))
        {
            excludedIds.Add(cleaned);
            excludedIds.Add($"ytm_{cleaned}");
        }

        string normalized = TrackDeduplicationHelper.NormalizeTrackId(cleaned);
        if (!string.IsNullOrEmpty(normalized))
        {
            excludedIds.Add(normalized);
            excludedIds.Add($"ytm_{normalized}");
        }
    }

    private static List<string> BuildFallbackQueries(TrackItem primarySeed)
    {
        string primaryArtist = TrackDeduplicationHelper.PrimaryArtistName(primarySeed.Artist);
        string cleanArtist = IsPlaceholderArtist(primaryArtist) ? string.Empty : primaryArtist;
        string cleanTitle = (primarySeed.Title ?? string.Empty).Trim();

        var queries = new List<string>(2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string combined = !string.IsNullOrWhiteSpace(cleanArtist) && !string.IsNullOrWhiteSpace(cleanTitle)
            ? $"{cleanArtist} {cleanTitle}"
            : !string.IsNullOrWhiteSpace(cleanArtist)
                ? cleanArtist
                : cleanTitle;

        if (!string.IsNullOrWhiteSpace(combined) && seen.Add(combined))
        {
            queries.Add(combined);
        }

        if (!string.IsNullOrWhiteSpace(cleanArtist) &&
            !string.Equals(cleanArtist, cleanTitle, StringComparison.OrdinalIgnoreCase) &&
            seen.Add(cleanArtist))
        {
            queries.Add(cleanArtist);
        }

        return queries;
    }

    private static bool IsPlaceholderArtist(string? artist) =>
        string.IsNullOrWhiteSpace(artist) ||
        string.Equals(artist, "Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(artist, "YouTube Artist", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(artist, "Featured Artist", StringComparison.OrdinalIgnoreCase);
}
