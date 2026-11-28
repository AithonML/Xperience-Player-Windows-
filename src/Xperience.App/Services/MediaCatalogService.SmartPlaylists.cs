using System.Globalization;
using Microsoft.Extensions.Logging;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Services;

public sealed partial class MediaCatalogService
{
    private const string PersistedPlaybackHistoryKey = "PlaybackHistoryLog_v1";
    private const int MaxPlaybackHistoryEntries = 200;
    private const long SevenDaysMs = 7L * 24 * 60 * 60 * 1000;

    private readonly List<PlaybackHistoryEntry> _playbackHistory = [];
    private List<TrackItem> _cachedRecommendedPool = [];

    private async Task LoadPlaybackHistoryOnInitAsync(CancellationToken cancellationToken)
    {
        try
        {
            var savedHistory = await _storageService.LoadAsync<List<PlaybackHistoryEntry>>(
                PersistedPlaybackHistoryKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);

            lock (_playbackHistory)
            {
                _playbackHistory.Clear();
                if (savedHistory is { Count: > 0 })
                {
                    foreach (var item in savedHistory)
                    {
                        if (item?.Track is not null && !string.IsNullOrWhiteSpace(item.Track.Title))
                        {
                            DecorateTrackWithLocalOfflineMatch(item.Track);
                            _playbackHistory.Add(item);
                        }
                    }
                }
            }

            if (_playbackHistory.Count == 0)
            {
                SeedInitialPlaybackHistoryFromRecentAndLibrary();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load playback history log.");
        }
    }

    private void SeedInitialPlaybackHistoryFromRecentAndLibrary()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seedTracks = new List<TrackItem>();

        lock (_recentlyPlayedTracks)
        {
            seedTracks.AddRange(_recentlyPlayedTracks);
        }

        lock (_likedOnlineTracks)
        {
            foreach (var liked in _likedOnlineTracks)
            {
                if (seedTracks.All(t => !string.Equals(t.Id, liked.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    seedTracks.Add(liked);
                }
            }
        }

        lock (_tracks)
        {
            foreach (var local in _tracks.OrderByDescending(t => t.IsLiked).ThenByDescending(t => t.DateAddedMs))
            {
                if (seedTracks.Count >= 40)
                {
                    break;
                }

                if (seedTracks.All(t => !string.Equals(t.Id, local.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    seedTracks.Add(local);
                }
            }
        }

        lock (_playbackHistory)
        {
            for (int i = 0; i < seedTracks.Count; i++)
            {
                var tr = seedTracks[i];
                long ts = nowMs - (i * 3_600_000L);
                int seededPlays = Math.Max(1, 5 - (i / 4));
                var timestamps = new List<long>(seededPlays);
                for (int p = 0; p < seededPlays; p++)
                {
                    timestamps.Add(ts - (p * 1_800_000L));
                }

                _playbackHistory.Add(new PlaybackHistoryEntry
                {
                    TrackId = tr.Id,
                    Track = tr,
                    LastPlayedTimestampMs = ts,
                    PlayTimestampsMs = timestamps,
                    TotalPlayCount = seededPlays,
                    Artist = GetPrimaryArtistName(tr.Artist),
                    Genre = InferTrackGenreTag(tr)
                });
            }
        }
    }

    private async Task RecordPlaybackHistoryEventAsync(TrackItem track, CancellationToken cancellationToken)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string resolvedGenre = InferTrackGenreTag(track);
        string primaryArtist = GetPrimaryArtistName(track.Artist);
        List<PlaybackHistoryEntry> snapshot;

        lock (_playbackHistory)
        {
            var existing = _playbackHistory.FirstOrDefault(e =>
                (!string.IsNullOrWhiteSpace(track.Id) && string.Equals(e.TrackId, track.Id, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(track.YouTubeVideoId) &&
                 string.Equals(e.Track.YouTubeVideoId, track.YouTubeVideoId, StringComparison.Ordinal)) ||
                (string.Equals(e.Track.Title, track.Title, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(GetPrimaryArtistName(e.Track.Artist), primaryArtist, StringComparison.OrdinalIgnoreCase)));

            if (existing is not null)
            {
                existing.Track = track;
                existing.LastPlayedTimestampMs = nowMs;
                existing.TotalPlayCount = Math.Max(1, existing.TotalPlayCount + 1);
                existing.PlayTimestampsMs ??= [];
                existing.PlayTimestampsMs.Insert(0, nowMs);
                if (existing.PlayTimestampsMs.Count > 50)
                {
                    existing.PlayTimestampsMs.RemoveRange(50, existing.PlayTimestampsMs.Count - 50);
                }

                if (!string.IsNullOrWhiteSpace(primaryArtist))
                {
                    existing.Artist = primaryArtist;
                }

                if (!string.IsNullOrWhiteSpace(resolvedGenre))
                {
                    existing.Genre = resolvedGenre;
                }

                _playbackHistory.Remove(existing);
                _playbackHistory.Insert(0, existing);
            }
            else
            {
                _playbackHistory.Insert(0, new PlaybackHistoryEntry
                {
                    TrackId = track.Id,
                    Track = track,
                    LastPlayedTimestampMs = nowMs,
                    PlayTimestampsMs = [nowMs],
                    TotalPlayCount = 1,
                    Artist = primaryArtist,
                    Genre = resolvedGenre
                });
            }

            if (_playbackHistory.Count > MaxPlaybackHistoryEntries)
            {
                _playbackHistory.RemoveRange(MaxPlaybackHistoryEntries, _playbackHistory.Count - MaxPlaybackHistoryEntries);
            }

            snapshot = [.. _playbackHistory];
        }

        try
        {
            await _storageService.SaveAsync(PersistedPlaybackHistoryKey, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore transient IO contention
        }
    }

    private static string InferTrackGenreTag(TrackItem track)
    {
        if (!string.IsNullOrWhiteSpace(track.Genre))
        {
            string trimmed = track.Genre.Trim();
            if (!trimmed.Equals("Streaming", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Equals("YouTube Music", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Equals("Online", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Equals("Official Video", StringComparison.OrdinalIgnoreCase))
            {
                return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(trimmed.ToLowerInvariant());
            }
        }

        string combined = $"{track.Artist} {track.Title} {track.Album}".ToLowerInvariant();
        if (combined.Contains("synth") || combined.Contains("weeknd") || combined.Contains("daft punk") || combined.Contains("kavinsky"))
        {
            return "Synthwave";
        }

        if (combined.Contains("rock") || combined.Contains("metal") || combined.Contains("linkin") || combined.Contains("queen") || combined.Contains("nirvana"))
        {
            return "Rock";
        }

        if (combined.Contains("electro") || combined.Contains("edm") || combined.Contains("house") || combined.Contains("remix") || combined.Contains("club"))
        {
            return "Electronic";
        }

        if (combined.Contains("indie") || combined.Contains("alternative") || combined.Contains("lorde") || combined.Contains("lana") || combined.Contains("marina"))
        {
            return "Indie Pop";
        }

        if (combined.Contains("r&b") || combined.Contains("soul") || combined.Contains("sza") || combined.Contains("frank ocean"))
        {
            return "R&B";
        }

        if (combined.Contains("hip hop") || combined.Contains("rap") || combined.Contains("trap") || combined.Contains("drake") || combined.Contains("kendrick"))
        {
            return "Hip-Hop";
        }

        if (combined.Contains("latin") || combined.Contains("reggaeton") || combined.Contains("urbano") || combined.Contains("bad bunny") || combined.Contains("rosal"))
        {
            return "Urbano & Latino";
        }

        return "Pop";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserPlaylist>> GetSmartPlaylistSuggestionsAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        List<PlaybackHistoryEntry> historySnapshot;
        lock (_playbackHistory)
        {
            if (_playbackHistory.Count == 0)
            {
                SeedInitialPlaybackHistoryFromRecentAndLibrary();
            }

            historySnapshot = [.. _playbackHistory];
        }

        var candidatePool = new List<TrackItem>();
        foreach (var entry in historySnapshot)
        {
            if (entry.Track is not null && !string.IsNullOrWhiteSpace(entry.Track.Title))
            {
                DecorateTrackWithLocalOfflineMatch(entry.Track);
                AddUniqueTrackToPool(candidatePool, entry.Track);
            }
        }

        lock (_recentlyPlayedTracks)
        {
            foreach (var rt in _recentlyPlayedTracks)
            {
                DecorateTrackWithLocalOfflineMatch(rt);
                AddUniqueTrackToPool(candidatePool, rt);
            }
        }

        lock (_likedOnlineTracks)
        {
            foreach (var lt in _likedOnlineTracks)
            {
                DecorateTrackWithLocalOfflineMatch(lt);
                AddUniqueTrackToPool(candidatePool, lt);
            }
        }

        lock (_tracks)
        {
            foreach (var local in _tracks)
            {
                AddUniqueTrackToPool(candidatePool, local);
            }
        }

        foreach (var cachedRec in _cachedRecommendedPool)
        {
            AddUniqueTrackToPool(candidatePool, cachedRec);
        }

        // If candidate pool has fewer than 28 songs, fetch additional tracks from YouTube Music
        if (candidatePool.Count < 28)
        {
            try
            {
                string? topArtist = historySnapshot
                    .Select(h => h.Artist)
                    .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
                string topGenre = historySnapshot
                    .Select(h => h.Genre)
                    .FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)) ?? "Pop";

                var extraSearch = !string.IsNullOrWhiteSpace(topArtist)
                    ? await _youTubeMusicService
                        .SearchCatalogAsync($"{topArtist} {topGenre} hits songs", cancellationToken)
                        .ConfigureAwait(false)
                    : await _youTubeMusicService
                        .FetchChartsCatalogAsync(cancellationToken)
                        .ConfigureAwait(false);

                foreach (var song in extraSearch.Songs)
                {
                    DecorateTrackWithLocalOfflineMatch(song);
                    AddUniqueTrackToPool(candidatePool, song);
                }

                _cachedRecommendedPool = extraSearch.Songs.ToList();
            }
            catch
            {
                // Offline fallback uses existing local + recent tracks
            }
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long cutoff7DaysMs = nowMs - SevenDaysMs;

        // 1. "Top 20 de la última semana" (Top 20 most played songs in the last 7 days)
        var top20WeekTracks = historySnapshot
            .Select(entry =>
            {
                int playsLast7Days = entry.PlayTimestampsMs?.Count(ts => ts >= cutoff7DaysMs) ?? 0;
                if (playsLast7Days == 0 && entry.LastPlayedTimestampMs >= cutoff7DaysMs)
                {
                    playsLast7Days = Math.Max(1, entry.TotalPlayCount);
                }

                return (Entry: entry, Plays7d: playsLast7Days);
            })
            .Where(x => x.Plays7d > 0)
            .OrderByDescending(x => x.Plays7d)
            .ThenByDescending(x => x.Entry.LastPlayedTimestampMs)
            .Select(x => x.Entry.Track)
            .ToList();

        foreach (var fallbackTrack in candidatePool)
        {
            if (top20WeekTracks.Count >= 20)
            {
                break;
            }

            AddUniqueTrackToPool(top20WeekTracks, fallbackTrack);
        }

        top20WeekTracks = top20WeekTracks.Take(20).ToList();

        // 2. "Mix de Géneros Escuchados" (Selección de 25 canciones de los géneros escuchados por el usuario)
        var userGenres = historySnapshot
            .Select(h => string.IsNullOrWhiteSpace(h.Genre) ? InferTrackGenreTag(h.Track) : h.Genre)
            .Concat(candidatePool.Select(InferTrackGenreTag))
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(4)
            .ToList();

        if (userGenres.Count == 0)
        {
            userGenres.AddRange(["Pop", "Rock", "Electronic"]);
        }

        var genreMix25Tracks = new List<TrackItem>();
        var genreBuckets = candidatePool
            .GroupBy(InferTrackGenreTag, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Interleave tracks across the user's top listened genres up to 25 tracks
        int round = 0;
        while (genreMix25Tracks.Count < 25 && round < 25)
        {
            bool addedInRound = false;
            foreach (string genre in userGenres)
            {
                if (genreBuckets.TryGetValue(genre, out var listForGenre) && round < listForGenre.Count)
                {
                    AddUniqueTrackToPool(genreMix25Tracks, listForGenre[round]);
                    addedInRound = true;
                    if (genreMix25Tracks.Count >= 25)
                    {
                        break;
                    }
                }
            }

            if (!addedInRound)
            {
                break;
            }

            round++;
        }

        foreach (var tr in candidatePool)
        {
            if (genreMix25Tracks.Count >= 25)
            {
                break;
            }

            AddUniqueTrackToPool(genreMix25Tracks, tr);
        }

        genreMix25Tracks = genreMix25Tracks.Take(25).ToList();
        string genresSummary = string.Join(", ", userGenres.Take(3));

        // 3. "En Repetición" (Most repeated & favorited songs)
        var onRepeatTracks = historySnapshot
            .OrderByDescending(h => h.TotalPlayCount + (h.Track.IsLiked ? 2 : 0))
            .ThenByDescending(h => h.LastPlayedTimestampMs)
            .Select(h => h.Track)
            .ToList();

        foreach (var tr in candidatePool.OrderByDescending(t => t.IsLiked))
        {
            if (onRepeatTracks.Count >= 20)
            {
                break;
            }

            AddUniqueTrackToPool(onRepeatTracks, tr);
        }

        onRepeatTracks = onRepeatTracks.Take(20).ToList();

        // 4. "Descubrimiento según tus gustos" (Curated mix based on user's top artists & musical profile)
        var topArtists = historySnapshot
            .Select(h => h.Artist)
            .Concat(candidatePool.Select(t => GetPrimaryArtistName(t.Artist)))
            .Where(a => !string.IsNullOrWhiteSpace(a) && !a.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        var tasteDiscoveryTracks = new List<TrackItem>();
        foreach (var tr in _cachedRecommendedPool.Concat(candidatePool.AsEnumerable().Reverse()))
        {
            if (tasteDiscoveryTracks.Count >= 20)
            {
                break;
            }

            AddUniqueTrackToPool(tasteDiscoveryTracks, tr);
        }

        string artistsSummary = topArtists.Count > 0 ? string.Join(", ", topArtists) : "your favorite artists";

        HashSet<string> savedTitles;
        lock (_playlists)
        {
            savedTitles = _playlists
                .Select(p => p.Title.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var suggestions = new List<UserPlaylist>
        {
            new()
            {
                Id = "smart_top20_week",
                Title = "Top 20 This Week",
                Description = $"Your {top20WeekTracks.Count} most played tracks from the last 7 days",
                IsSmartSuggestion = true,
                SmartBadgeText = "Top 20 • 7 Days",
                SmartCategoryGlyph = "\uE735",
                IsSavedToLibrary = savedTitles.Contains("Top 20 This Week"),
                Tracks = top20WeekTracks
            },
            new()
            {
                Id = "smart_genre_mix_25",
                Title = "Genre Mix",
                Description = $"{genreMix25Tracks.Count}-track selection from your genres ({genresSummary})",
                IsSmartSuggestion = true,
                SmartBadgeText = "25-Track Selection",
                SmartCategoryGlyph = "\uE90B",
                IsSavedToLibrary = savedTitles.Contains("Genre Mix"),
                Tracks = genreMix25Tracks
            },
            new()
            {
                Id = "smart_on_repeat",
                Title = "On Repeat",
                Description = "The songs you've been replaying the most recently",
                IsSmartSuggestion = true,
                SmartBadgeText = "On Repeat",
                SmartCategoryGlyph = "\uE8EE",
                IsSavedToLibrary = savedTitles.Contains("On Repeat"),
                Tracks = onRepeatTracks
            },
            new()
            {
                Id = "smart_taste_discovery",
                Title = "Discovery Mix",
                Description = $"Inspired by {artistsSummary} and your listening history",
                IsSmartSuggestion = true,
                SmartBadgeText = "For You",
                SmartCategoryGlyph = "\uE734",
                IsSavedToLibrary = savedTitles.Contains("Discovery Mix"),
                Tracks = tasteDiscoveryTracks
            }
        };

        return suggestions.Where(s => s.Tracks.Count > 0).ToList();
    }

    /// <inheritdoc />
    public async Task<UserPlaylist> SaveSmartPlaylistToLibraryAsync(
        UserPlaylist smartPlaylist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(smartPlaylist);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        UserPlaylist savedPlaylist;
        List<UserPlaylist> snapshot;

        lock (_playlists)
        {
            var existing = _playlists.FirstOrDefault(p =>
                string.Equals(p.Title, smartPlaylist.Title, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Description = smartPlaylist.Description;
                existing.Tracks = [.. smartPlaylist.Tracks];
                existing.IsSmartSuggestion = false;
                existing.IsSavedToLibrary = true;
                savedPlaylist = existing;
            }
            else
            {
                savedPlaylist = new UserPlaylist
                {
                    Id = $"pl_{Guid.NewGuid():N}",
                    Title = smartPlaylist.Title,
                    Description = smartPlaylist.Description,
                    CustomCoverImagePath = smartPlaylist.CustomCoverImagePath,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsSmartSuggestion = false,
                    IsSavedToLibrary = true,
                    Tracks = [.. smartPlaylist.Tracks]
                };
                _playlists.Insert(0, savedPlaylist);
            }

            snapshot = [.. _playlists];
        }

        smartPlaylist.IsSavedToLibrary = true;
        await _storageService.SaveAsync(PersistedPlaylistsKey, snapshot, cancellationToken).ConfigureAwait(false);
        return savedPlaylist;
    }

    private static void AddUniqueTrackToPool(List<TrackItem> pool, TrackItem? track)
    {
        if (track is null || string.IsNullOrWhiteSpace(track.Title) || track.Id == "empty_state_track")
        {
            return;
        }

        if (pool.Any(existing =>
                (!string.IsNullOrWhiteSpace(track.Id) && string.Equals(existing.Id, track.Id, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(track.YouTubeVideoId) && string.Equals(existing.YouTubeVideoId, track.YouTubeVideoId, StringComparison.Ordinal)) ||
                (string.Equals(existing.Title, track.Title, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(existing.Artist, track.Artist, StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }

        pool.Add(track);
    }
}
