using Microsoft.Extensions.Logging;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;
using Xperience.Core.Services;

namespace Xperience.App.Services;

public sealed partial class YouTubeMusicService
{
    private const int MaxLibraryPlaylistPages = 8;
    private const int MaxPlaylistTrackPages = 20;

    /// <inheritdoc />
    public async Task<IReadOnlyList<YouTubeMusicLibraryPlaylist>> FetchUserLibraryPlaylistsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated)
        {
            return [];
        }

        if (!_triedAccountMetaSync)
        {
            await EnsureAccountSessionMetadataAsync(cancellationToken).ConfigureAwait(false);
            if (!IsAuthenticated)
            {
                return [];
            }
        }

        var initialPayload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(),
            ["browseId"] = "FEmusic_liked_playlists"
        };

        using var initialDoc = await ExecuteInnerTubePostDocAsync(
            YtMusicBrowseEndpoint,
            initialPayload,
            includeAuthHeaders: true,
            cancellationToken).ConfigureAwait(false);

        if (initialDoc is null)
        {
            return [];
        }

        var collected = new List<YouTubeMusicLibraryPlaylist>();
        var seenPlaylistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedTokens = new HashSet<string>(StringComparer.Ordinal);

        var (firstPagePlaylists, continuationToken) =
            YouTubePlaylistImportParser.ParseLibraryPlaylistsResponse(initialDoc.RootElement);
        AppendUniqueLibraryPlaylists(firstPagePlaylists, collected, seenPlaylistIds);

        int page = 1;
        while (!string.IsNullOrWhiteSpace(continuationToken) &&
               page < MaxLibraryPlaylistPages &&
               visitedTokens.Add(continuationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contPayload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["continuation"] = continuationToken
            };

            using var contDoc = await ExecuteInnerTubePostDocAsync(
                YtMusicBrowseEndpoint,
                contPayload,
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);

            if (contDoc is null)
            {
                break;
            }

            var (pagePlaylists, nextToken) =
                YouTubePlaylistImportParser.ParseLibraryPlaylistsResponse(contDoc.RootElement);
            AppendUniqueLibraryPlaylists(pagePlaylists, collected, seenPlaylistIds);

            continuationToken = nextToken;
            page++;
        }

        if (collected.Count == 0)
        {
            return [];
        }

        // Normalize thumbnail URLs to high-resolution and warm the local artwork cache
        // so WinUI <Image> controls render playlist covers reliably.
        var normalizedList = new List<YouTubeMusicLibraryPlaylist>(collected.Count);
        var artworkUris = new List<string>(collected.Count);
        foreach (var item in collected)
        {
            string normalizedArt = NormalizeHighResThumbnailUrl(item.ArtworkUri, targetSizePx: 544);
            if (!string.IsNullOrWhiteSpace(normalizedArt))
            {
                artworkUris.Add(normalizedArt);
            }

            normalizedList.Add(item with { ArtworkUri = normalizedArt });
        }

        IReadOnlyDictionary<string, string> warmedMap;
        try
        {
            warmedMap = await WarmArtworkCacheAsync(artworkUris, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Artwork cache warm-up for library playlists partially failed.");
            warmedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (warmedMap.Count == 0)
        {
            return normalizedList;
        }

        var result = new List<YouTubeMusicLibraryPlaylist>(normalizedList.Count);
        foreach (var item in normalizedList)
        {
            if (!string.IsNullOrWhiteSpace(item.ArtworkUri) &&
                warmedMap.TryGetValue(item.ArtworkUri, out string? localUri) &&
                !string.IsNullOrWhiteSpace(localUri))
            {
                result.Add(item with { ArtworkUri = localUri });
            }
            else
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<TrackItem> Tracks, string? CoverUri, string? Description)> FetchUserPlaylistTracksAsync(
        YouTubeMusicLibraryPlaylist playlist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        if (!IsAuthenticated)
        {
            return ([], null, null);
        }

        if (!_triedAccountMetaSync)
        {
            await EnsureAccountSessionMetadataAsync(cancellationToken).ConfigureAwait(false);
            if (!IsAuthenticated)
            {
                return ([], null, null);
            }
        }

        string browseId = YouTubePlaylistImportParser.NormalizeBrowseId(playlist.BrowseId, playlist.PlaylistId);
        if (string.IsNullOrWhiteSpace(browseId))
        {
            return ([], null, null);
        }

        var initialPayload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(),
            ["browseId"] = browseId
        };

        using var initialDoc = await ExecuteInnerTubePostDocAsync(
            YtMusicBrowseEndpoint,
            initialPayload,
            includeAuthHeaders: true,
            cancellationToken).ConfigureAwait(false);

        if (initialDoc is null)
        {
            return ([], null, null);
        }

        var collectedTracks = new List<TrackItem>();
        var seenTrackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedTokens = new HashSet<string>(StringComparer.Ordinal);

        var (firstPageTracks, continuationToken, headerCoverUri, headerDescription) =
            YouTubePlaylistImportParser.ParsePlaylistTracksPage(initialDoc.RootElement);
        AppendUniqueNormalizedPlaylistTracks(firstPageTracks, collectedTracks, seenTrackIds);

        int page = 1;
        while (!string.IsNullOrWhiteSpace(continuationToken) &&
               page < MaxPlaylistTrackPages &&
               visitedTokens.Add(continuationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contPayload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["continuation"] = continuationToken
            };

            using var contDoc = await ExecuteInnerTubePostDocAsync(
                YtMusicBrowseEndpoint,
                contPayload,
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);

            if (contDoc is null)
            {
                break;
            }

            var (pageTracks, nextToken, pageHeaderCover, pageHeaderDesc) =
                YouTubePlaylistImportParser.ParsePlaylistTracksPage(contDoc.RootElement);
            AppendUniqueNormalizedPlaylistTracks(pageTracks, collectedTracks, seenTrackIds);

            if (string.IsNullOrWhiteSpace(headerCoverUri) && !string.IsNullOrWhiteSpace(pageHeaderCover))
            {
                headerCoverUri = pageHeaderCover;
            }

            if (string.IsNullOrWhiteSpace(headerDescription) && !string.IsNullOrWhiteSpace(pageHeaderDesc))
            {
                headerDescription = pageHeaderDesc;
            }

            continuationToken = nextToken;
            page++;
        }

        string? normalizedHeaderCover = !string.IsNullOrWhiteSpace(headerCoverUri)
            ? NormalizeHighResThumbnailUrl(headerCoverUri, targetSizePx: 544)
            : null;

        // Warm cover cache for header artwork and first 24 tracks so imported playlist cards & detail open crisply.
        var urisToWarm = new List<string>();
        if (!string.IsNullOrWhiteSpace(normalizedHeaderCover))
        {
            urisToWarm.Add(normalizedHeaderCover);
        }

        foreach (var tr in collectedTracks.Take(24))
        {
            if (!string.IsNullOrWhiteSpace(tr.CoverArtUri))
            {
                urisToWarm.Add(tr.CoverArtUri);
            }
        }

        if (urisToWarm.Count > 0)
        {
            try
            {
                var warmed = await WarmArtworkCacheAsync(urisToWarm, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(normalizedHeaderCover) &&
                    warmed.TryGetValue(normalizedHeaderCover, out string? localHeaderCover) &&
                    !string.IsNullOrWhiteSpace(localHeaderCover))
                {
                    normalizedHeaderCover = localHeaderCover;
                }

                if (warmed.Count > 0)
                {
                    for (int i = 0; i < collectedTracks.Count; i++)
                    {
                        var tr = collectedTracks[i];
                        if (!string.IsNullOrWhiteSpace(tr.CoverArtUri) &&
                            warmed.TryGetValue(tr.CoverArtUri, out string? localTrackCover) &&
                            !string.IsNullOrWhiteSpace(localTrackCover))
                        {
                            collectedTracks[i] = CloneTrackWithCoverUri(tr, localTrackCover);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Artwork cache warm-up for imported playlist tracks partially failed.");
            }
        }

        return (collectedTracks, normalizedHeaderCover, headerDescription);
    }

    private static void AppendUniqueLibraryPlaylists(
        IReadOnlyList<YouTubeMusicLibraryPlaylist> source,
        List<YouTubeMusicLibraryPlaylist> destination,
        HashSet<string> seenPlaylistIds)
    {
        foreach (var item in source)
        {
            if (seenPlaylistIds.Add(item.PlaylistId))
            {
                destination.Add(item);
            }
        }
    }

    private static void AppendUniqueNormalizedPlaylistTracks(
        IReadOnlyList<TrackItem> source,
        List<TrackItem> destination,
        HashSet<string> seenTrackIds)
    {
        foreach (var track in source)
        {
            if (track is null || string.IsNullOrWhiteSpace(track.Id) || !seenTrackIds.Add(track.Id))
            {
                continue;
            }

            string normalizedCover = NormalizeHighResThumbnailUrl(track.CoverArtUri, targetSizePx: 544);
            destination.Add(string.Equals(normalizedCover, track.CoverArtUri, StringComparison.Ordinal)
                ? track
                : CloneTrackWithCoverUri(track, normalizedCover));
        }
    }

    private static TrackItem CloneTrackWithCoverUri(TrackItem track, string coverUri) => new()
    {
        Id = track.Id,
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        Duration = track.Duration,
        CoverArtUri = coverUri,
        AudioSourceUri = track.AudioSourceUri,
        YouTubeVideoId = track.YouTubeVideoId,
        OfficialVideoId = track.OfficialVideoId,
        IsOfficialVideoItem = track.IsOfficialVideoItem,
        SourceType = track.SourceType,
        IsLiked = track.IsLiked,
        IsExplicit = track.IsExplicit,
        DateAddedMs = track.DateAddedMs,
        QualityBadge = track.QualityBadge,
        Genre = track.Genre,
        ReleaseYear = track.ReleaseYear,
        TrackNumber = track.TrackNumber,
        PlayCountText = track.PlayCountText,
        PlayCount = track.PlayCount,
        IsDownloadedOffline = track.IsDownloadedOffline,
        LocalOfflineFilePath = track.LocalOfflineFilePath
    };
}
