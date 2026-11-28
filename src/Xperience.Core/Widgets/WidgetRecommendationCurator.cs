using System.Text.RegularExpressions;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Widgets;

/// <summary>
/// Pure recommendation curation, ranking, deduplication, and fallback engine for Windows 11 widgets.
/// Interleaves discovery playlists ("Made For You"), auto-generated smart mixes, saved user playlists,
/// radio stations, and virtual Liked / Recently Played shortcuts so the 2x2 widget grid is always rich
/// and playable both online and offline.
/// </summary>
public static class WidgetRecommendationCurator
{
    public const string LikedSongsVirtualId = "widget_virtual_liked_songs";
    public const string RecentlyPlayedVirtualId = "widget_virtual_recently_played";
    public const string FallbackDailyMixId = "widget_fallback_daily_mix";
    public const string FallbackLateNightId = "widget_fallback_late_night";
    public const string FallbackQuickRadioId = "widget_fallback_quick_radio";
    public const int DefaultMaxItems = 12;

    private static readonly Regex DigitsRegex = new(@"\d+", RegexOptions.Compiled);

    public static IReadOnlyList<WidgetPlaylistItem> Curate(
        IReadOnlyList<UserPlaylist>? userPlaylists = null,
        IReadOnlyList<UserPlaylist>? smartPlaylists = null,
        IReadOnlyList<RecommendedPlaylistItem>? madeForYou = null,
        IReadOnlyList<RadioStationItem>? radioStations = null,
        int likedCount = 0,
        string? likedArtworkUri = null,
        IReadOnlyList<TrackItem>? recentlyPlayedTracks = null,
        int maxItems = DefaultMaxItems)
    {
        int limit = Math.Max(4, maxItems);

        var madeForYouItems = (madeForYou ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Title) &&
                           (!string.IsNullOrWhiteSpace(item.PlaylistId) || !string.IsNullOrWhiteSpace(item.Id)))
            .Select(item =>
            {
                string resolvedId = !string.IsNullOrWhiteSpace(item.PlaylistId) ? item.PlaylistId : item.Id;
                string subtitle = FirstNonBlank(item.TrackCountText, item.Author, item.Subtitle) ?? "For You";
                return new WidgetPlaylistItem
                {
                    Id = resolvedId,
                    Title = item.Title.Trim(),
                    Subtitle = subtitle.Trim(),
                    TrackCount = ExtractTrackCount(item.TrackCountText),
                    ArtworkUri = NormalizeOptionalUri(item.ArtworkUri),
                    SourceType = WidgetPlaylistSourceType.MadeForYou,
                    PlaylistId = NormalizeOptionalUri(item.PlaylistId),
                    BrowseId = NormalizeOptionalUri(item.BrowseId)
                };
            })
            .ToList();

        var smartItems = (smartPlaylists ?? [])
            .Where(pl => !string.IsNullOrWhiteSpace(pl.Title) && pl.Tracks.Count > 0)
            .Select(pl => new WidgetPlaylistItem
            {
                Id = pl.Id,
                Title = pl.Title.Trim(),
                Subtitle = !string.IsNullOrWhiteSpace(pl.SmartBadgeText)
                    ? pl.SmartBadgeText.Trim()
                    : FormatSongCount(pl.Tracks.Count),
                TrackCount = pl.Tracks.Count,
                ArtworkUri = ResolvePlaylistArtwork(pl),
                SourceType = WidgetPlaylistSourceType.SmartPlaylist
            })
            .ToList();

        var populatedUserItems = (userPlaylists ?? [])
            .Where(pl => !string.IsNullOrWhiteSpace(pl.Title) && pl.Tracks.Count > 0)
            .OrderByDescending(pl => pl.Tracks.Count)
            .Select(pl => new WidgetPlaylistItem
            {
                Id = pl.Id,
                Title = pl.Title.Trim(),
                Subtitle = FormatSongCount(pl.Tracks.Count),
                TrackCount = pl.Tracks.Count,
                ArtworkUri = ResolvePlaylistArtwork(pl),
                SourceType = WidgetPlaylistSourceType.UserPlaylist
            })
            .ToList();

        var radioItems = (radioStations ?? [])
            .Where(st => !string.IsNullOrWhiteSpace(st.Title) &&
                         (!string.IsNullOrWhiteSpace(st.PlaylistId) || !string.IsNullOrWhiteSpace(st.Id)))
            .Select(st => new WidgetPlaylistItem
            {
                Id = !string.IsNullOrWhiteSpace(st.PlaylistId) ? st.PlaylistId : st.Id,
                Title = st.Title.Trim(),
                Subtitle = !string.IsNullOrWhiteSpace(st.Subtitle) ? st.Subtitle.Trim() : "Quick Mix",
                TrackCount = 0,
                ArtworkUri = NormalizeOptionalUri(st.ArtworkUri),
                SourceType = WidgetPlaylistSourceType.RadioStation,
                PlaylistId = NormalizeOptionalUri(st.PlaylistId),
                SeedVideoId = NormalizeOptionalUri(st.SeedVideoId),
                Params = NormalizeOptionalUri(st.Params)
            })
            .ToList();

        WidgetPlaylistItem? likedItem = likedCount > 0
            ? new WidgetPlaylistItem
            {
                Id = LikedSongsVirtualId,
                Title = "Liked Songs",
                Subtitle = FormatSongCount(likedCount),
                TrackCount = likedCount,
                ArtworkUri = NormalizeOptionalUri(likedArtworkUri),
                SourceType = WidgetPlaylistSourceType.LikedSongs
            }
            : null;

        WidgetPlaylistItem? recentItem = recentlyPlayedTracks is { Count: > 0 }
            ? new WidgetPlaylistItem
            {
                Id = RecentlyPlayedVirtualId,
                Title = "Recently Played",
                Subtitle = FormatSongCount(recentlyPlayedTracks.Count),
                TrackCount = recentlyPlayedTracks.Count,
                ArtworkUri = recentlyPlayedTracks
                    .Select(t => NormalizeOptionalUri(t.CoverArtUri))
                    .FirstOrDefault(uri => uri is not null),
                SourceType = WidgetPlaylistSourceType.RecentlyPlayed
            }
            : null;

        var result = new List<WidgetPlaylistItem>(limit);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool TryAdd(WidgetPlaylistItem? candidate)
        {
            if (candidate is null || result.Count >= limit)
            {
                return false;
            }

            string titleKey = candidate.Title.Trim();
            if (string.IsNullOrWhiteSpace(titleKey))
            {
                return false;
            }

            string idKey = $"{candidate.SourceType}::{candidate.Id.Trim()}";
            if (!seenIds.Add(idKey) || !seenTitles.Add(titleKey))
            {
                return false;
            }

            result.Add(candidate);
            return true;
        }

        var buckets = new List<Queue<WidgetPlaylistItem>>();
        if (madeForYouItems.Count > 0) buckets.Add(new Queue<WidgetPlaylistItem>(madeForYouItems));
        if (smartItems.Count > 0) buckets.Add(new Queue<WidgetPlaylistItem>(smartItems));
        if (populatedUserItems.Count > 0) buckets.Add(new Queue<WidgetPlaylistItem>(populatedUserItems));
        if (radioItems.Count > 0) buckets.Add(new Queue<WidgetPlaylistItem>(radioItems));

        int round = 0;
        while (result.Count < limit && buckets.Any(b => b.Count > 0))
        {
            foreach (var bucket in buckets)
            {
                if (result.Count >= limit)
                {
                    break;
                }

                while (bucket.Count > 0)
                {
                    if (TryAdd(bucket.Dequeue()))
                    {
                        break;
                    }
                }
            }

            if (round == 0)
            {
                TryAdd(likedItem);
            }

            round++;
        }

        TryAdd(likedItem);
        TryAdd(recentItem);

        // Include empty user playlists only as late fallback if still under 4 items
        if (result.Count < 4 && userPlaylists is not null)
        {
            foreach (var emptyPl in userPlaylists.Where(pl => !string.IsNullOrWhiteSpace(pl.Title) && pl.Tracks.Count == 0))
            {
                TryAdd(new WidgetPlaylistItem
                {
                    Id = emptyPl.Id,
                    Title = emptyPl.Title.Trim(),
                    Subtitle = "0 songs",
                    TrackCount = 0,
                    ArtworkUri = ResolvePlaylistArtwork(emptyPl),
                    SourceType = WidgetPlaylistSourceType.UserPlaylist
                });
            }
        }

        // Ensure at least 4 cards using sensible reference fallbacks (Daily Mix, Liked Songs, Late Night Vibes, Quick Radio)
        if (result.Count < 4)
        {
            foreach (var fallback in BuildDefaultFallbacks(likedCount))
            {
                if (result.Count >= 4)
                {
                    break;
                }

                TryAdd(fallback);
            }
        }

        return result;
    }

    /// <summary>
    /// Selects exactly <paramref name="count"/> items starting at cyclic <paramref name="offset"/>,
    /// padding with default fallback cards when fewer than <paramref name="count"/> items are available.
    /// </summary>
    public static IReadOnlyList<WidgetPlaylistItem> SelectWindow(
        IReadOnlyList<WidgetPlaylistItem>? items,
        int offset,
        int count = 4,
        int likedCount = 0)
    {
        if (count <= 0)
        {
            return [];
        }

        var source = items is { Count: > 0 } ? items : BuildDefaultFallbacks(likedCount);
        int takeCount = Math.Min(source.Count, count);
        int safeOffset = ((offset % source.Count) + source.Count) % source.Count;

        var selected = new List<WidgetPlaylistItem>(count);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < takeCount; i++)
        {
            var candidate = source[(safeOffset + i) % source.Count];
            selected.Add(candidate);
            seenTitles.Add(candidate.Title);
        }

        if (selected.Count < count)
        {
            foreach (var fallback in BuildDefaultFallbacks(likedCount))
            {
                if (selected.Count >= count)
                {
                    break;
                }

                if (seenTitles.Add(fallback.Title))
                {
                    selected.Add(fallback);
                }
            }
        }

        return selected;
    }

    /// <summary>
    /// Builds the 4 canonical fallback cards modeled on the Xperience widget design.
    /// </summary>
    public static IReadOnlyList<WidgetPlaylistItem> BuildDefaultFallbacks(int likedCount = 0)
    {
        string likedSubtitle = likedCount > 0 ? FormatSongCount(likedCount) : "Liked";
        return
        [
            new WidgetPlaylistItem
            {
                Id = FallbackDailyMixId,
                Title = "Daily Mix",
                Subtitle = "For You",
                SourceType = WidgetPlaylistSourceType.Fallback
            },
            new WidgetPlaylistItem
            {
                Id = LikedSongsVirtualId,
                Title = "Liked Songs",
                Subtitle = likedSubtitle,
                TrackCount = Math.Max(0, likedCount),
                SourceType = WidgetPlaylistSourceType.LikedSongs
            },
            new WidgetPlaylistItem
            {
                Id = FallbackLateNightId,
                Title = "Late Night Vibes",
                Subtitle = "Smart Mix",
                SourceType = WidgetPlaylistSourceType.Fallback
            },
            new WidgetPlaylistItem
            {
                Id = FallbackQuickRadioId,
                Title = "Quick Radio",
                Subtitle = "Quick Mix",
                SourceType = WidgetPlaylistSourceType.Fallback
            }
        ];
    }

    private static string FormatSongCount(int count) =>
        count == 1 ? "1 song" : $"{Math.Max(0, count)} songs";

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }

        return null;
    }

    private static string? NormalizeOptionalUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        string trimmed = uri.Trim();
        if (trimmed.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private static string? ResolvePlaylistArtwork(UserPlaylist playlist)
    {
        var custom = NormalizeOptionalUri(playlist.CustomCoverImagePath);
        if (custom is not null)
        {
            return custom;
        }

        return playlist.Tracks
            .Select(t => NormalizeOptionalUri(t.CoverArtUri))
            .FirstOrDefault(u => u is not null);
    }

    private static int ExtractTrackCount(string? trackCountText)
    {
        if (string.IsNullOrWhiteSpace(trackCountText))
        {
            return 0;
        }

        var match = DigitsRegex.Match(trackCountText);
        return match.Success && int.TryParse(match.Value, out int parsed)
            ? Math.Max(0, parsed)
            : 0;
    }
}
