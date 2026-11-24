using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Xperience.Core.Collections;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;
using YoutubeExplode.Videos.Streams;

namespace Xperience.App.Services;

public sealed partial class YouTubeMusicService
{
    private const string YtMusicVideosFilterParams = "EgWKAQIQAWoMEA4QChADEAQQCRAF";

    private static readonly string[] EnglishMonthNames =
    [
        "",
        "January",
        "February",
        "March",
        "April",
        "May",
        "June",
        "July",
        "August",
        "September",
        "October",
        "November",
        "December"
    ];

    private static readonly Dictionary<string, int> MonthTokenToNumber = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["january"] = 1, ["ene"] = 1, ["enero"] = 1,
        ["feb"] = 2, ["february"] = 2, ["febrero"] = 2,
        ["mar"] = 3, ["march"] = 3, ["marzo"] = 3,
        ["apr"] = 4, ["april"] = 4, ["abr"] = 4, ["abril"] = 4,
        ["may"] = 5, ["mayo"] = 5,
        ["jun"] = 6, ["june"] = 6, ["junio"] = 6,
        ["jul"] = 7, ["july"] = 7, ["julio"] = 7,
        ["aug"] = 8, ["august"] = 8, ["ago"] = 8, ["agosto"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["september"] = 9, ["set"] = 9, ["septiembre"] = 9,
        ["oct"] = 10, ["october"] = 10, ["octubre"] = 10,
        ["nov"] = 11, ["november"] = 11, ["noviembre"] = 11,
        ["dec"] = 12, ["december"] = 12, ["dic"] = 12, ["diciembre"] = 12
    };

    private sealed record CachedMusicVideoEntry(ResolvedMusicVideoStream Stream, DateTimeOffset ExpiresAt);

    private sealed record ResolvedVideoPlayerCandidate(
        ResolvedAudioStream? ProgressiveMp4,
        ResolvedAudioStream? AdaptiveMp4,
        bool BlockedByAuth);

    private sealed record VideoSearchCandidate(
        string VideoId,
        string Title,
        string Artist,
        string ThumbnailUri,
        TimeSpan Duration);

    private readonly BoundedLruCache<string, CachedMusicVideoEntry> _resolvedMusicVideoCache =
        new(maxCapacity: 40, comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Formats a release year and month (1..12) into an English date badge (e.g., "March 2025").
    /// </summary>
    public static string FormatReleaseMonthYearBadge(int year, int month)
    {
        if (year <= 1900)
        {
            return string.Empty;
        }

        if (month is >= 1 and <= 12)
        {
            return $"{EnglishMonthNames[month]} {year}";
        }

        return year.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Extracts a release month (1..12) from a subtitle or ISO date string when present.
    /// </summary>
    internal static int ExtractMonthFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var isoMatch = IsoYearMonthRegex().Match(text);
        if (isoMatch.Success &&
            int.TryParse(isoMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int isoMonth) &&
            isoMonth is >= 1 and <= 12)
        {
            return isoMonth;
        }

        foreach (Match wordMatch in WordTokenRegex().Matches(text))
        {
            string token = wordMatch.Value.TrimEnd('.');
            if (MonthTokenToNumber.TryGetValue(token, out int mappedMonth))
            {
                return mappedMonth;
            }
        }

        return 0;
    }

    /// <summary>
    /// Parses an official video card from an artist carousel <c>musicTwoRowItemRenderer</c>.
    /// </summary>
    internal static TrackItem? TryParseArtistVideoTwoRowItem(
        JsonElement twoRow,
        string defaultArtistName,
        uint indexOrder)
    {
        string? videoId = null;
        if (twoRow.TryGetProperty("navigationEndpoint", out var navEp))
        {
            videoId = FindFirstPropertyString(navEp, "videoId");
        }

        videoId ??= FindFirstPropertyString(twoRow, "videoId");
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        string title = twoRow.TryGetProperty("title", out var tObj) ? ExtractAllRunsText(tObj) : string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string subtitle = twoRow.TryGetProperty("subtitle", out var sObj) ? ExtractAllRunsText(sObj) : string.Empty;
        string viewsText = string.Empty;
        long viewsCount = 0;
        string parsedArtist = defaultArtistName;

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var parts = subtitle.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (IsPlayCountLabel(part))
                {
                    viewsText = part;
                    viewsCount = ParseApproximatePlayCount(part);
                }
                else if (!part.Equals("Video", StringComparison.OrdinalIgnoreCase) &&
                         !part.Equals("Vídeo", StringComparison.OrdinalIgnoreCase) &&
                         !part.Equals("Official Video", StringComparison.OrdinalIgnoreCase))
                {
                    parsedArtist = part;
                }
            }
        }

        string coverUri = twoRow.TryGetProperty("thumbnailRenderer", out var tr)
            ? ExtractHighResThumbnailUrl(tr)
            : ExtractHighResThumbnailUrl(twoRow);
        if (string.IsNullOrWhiteSpace(coverUri))
        {
            coverUri = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
        }

        return new TrackItem
        {
            Id = $"ytm_vid_{videoId}",
            YouTubeVideoId = videoId,
            OfficialVideoId = videoId,
            IsOfficialVideoItem = true,
            Title = title,
            Artist = string.IsNullOrWhiteSpace(parsedArtist) ? defaultArtistName : parsedArtist,
            Album = "Official Video",
            Duration = TimeSpan.FromMinutes(3.5),
            CoverArtUri = coverUri,
            AudioSourceUri = $"ytmusic://{videoId}",
            SourceType = MediaSourceType.Online,
            IsExplicit = HasExplicitBadge(twoRow),
            QualityBadge = "HD VIDEO",
            Genre = "Official Video",
            ReleaseYear = DateTime.Now.Year,
            TrackNumber = indexOrder,
            PlayCountText = !string.IsNullOrWhiteSpace(viewsText) ? viewsText : "Official Video",
            PlayCount = viewsCount
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackItem>> SearchArtistOfficialVideosAsync(
        string artistName,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName) || limit <= 0)
        {
            return [];
        }

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["query"] = $"{artistName.Trim()} official music video",
                ["params"] = YtMusicVideosFilterParams
            };

            using var doc = await ExecuteInnerTubePostDocAsync(
                YtMusicSearchEndpoint,
                payload,
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                return [];
            }

            var videos = new List<TrackItem>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            CollectOfficialVideoRenderersRecursive(doc.RootElement, artistName.Trim(), videos, seenIds, limit);
            return videos;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not search official videos for artist '{Artist}'.", artistName);
            return [];
        }
    }

    private static void CollectOfficialVideoRenderersRecursive(
        JsonElement element,
        string targetArtist,
        List<TrackItem> results,
        HashSet<string> seenIds,
        int limit)
    {
        if (results.Count >= limit)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                var parsed = TryParseOfficialVideoResponsiveItem(renderer, targetArtist, (uint)(results.Count + 1));
                if (parsed is not null && seenIds.Add(parsed.YouTubeVideoId))
                {
                    results.Add(parsed);
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                CollectOfficialVideoRenderersRecursive(prop.Value, targetArtist, results, seenIds, limit);
                if (results.Count >= limit)
                {
                    return;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectOfficialVideoRenderersRecursive(item, targetArtist, results, seenIds, limit);
                if (results.Count >= limit)
                {
                    return;
                }
            }
        }
    }

    private static TrackItem? TryParseOfficialVideoResponsiveItem(
        JsonElement renderer,
        string defaultArtist,
        uint trackNumber)
    {
        string? videoId = null;
        if (renderer.TryGetProperty("playlistItemData", out var pid) &&
            pid.TryGetProperty("videoId", out var vidProp))
        {
            videoId = vidProp.GetString();
        }

        videoId ??= FindFirstPropertyString(renderer, "videoId");
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        if (!renderer.TryGetProperty("flexColumns", out var flexCols) ||
            flexCols.ValueKind != JsonValueKind.Array ||
            flexCols.GetArrayLength() < 1)
        {
            return null;
        }

        string title = ExtractRunsText(flexCols[0]);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string artist = defaultArtist;
        string viewsText = string.Empty;
        long viewsCount = 0;
        TimeSpan duration = TimeSpan.FromMinutes(3.5);

        if (flexCols.GetArrayLength() > 1)
        {
            string metaText = ExtractRunsText(flexCols[1]);
            if (!string.IsNullOrWhiteSpace(metaText))
            {
                var parts = metaText.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    if (IsPlayCountLabel(part))
                    {
                        viewsText = part;
                        viewsCount = ParseApproximatePlayCount(part);
                    }
                    else if (TryParseDurationString(part, out var parsedDur))
                    {
                        duration = parsedDur;
                    }
                    else if (!part.Equals("Video", StringComparison.OrdinalIgnoreCase) &&
                             !part.Equals("Vídeo", StringComparison.OrdinalIgnoreCase))
                    {
                        artist = part;
                    }
                }
            }
        }

        if (renderer.TryGetProperty("fixedColumns", out var fixedCols) &&
            fixedCols.ValueKind == JsonValueKind.Array &&
            fixedCols.GetArrayLength() > 0)
        {
            string fixedText = ExtractRunsText(fixedCols[0]).Trim();
            if (TryParseDurationString(fixedText, out var fixedDur))
            {
                duration = fixedDur;
            }
        }

        string coverUri = ExtractHighResThumbnailUrl(renderer);
        if (string.IsNullOrWhiteSpace(coverUri))
        {
            coverUri = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
        }

        return new TrackItem
        {
            Id = $"ytm_vid_{videoId}",
            YouTubeVideoId = videoId,
            OfficialVideoId = videoId,
            IsOfficialVideoItem = true,
            Title = title,
            Artist = artist,
            Album = "Official Video",
            Duration = duration,
            CoverArtUri = coverUri,
            AudioSourceUri = $"ytmusic://{videoId}",
            SourceType = MediaSourceType.Online,
            IsExplicit = HasExplicitBadge(renderer),
            QualityBadge = "HD VIDEO",
            Genre = "Official Video",
            ReleaseYear = DateTime.Now.Year,
            TrackNumber = trackNumber,
            PlayCountText = !string.IsNullOrWhiteSpace(viewsText) ? viewsText : "Official Video",
            PlayCount = viewsCount
        };
    }

    /// <summary>
    /// Determines the artist's latest release (Single or Album) based on release year and release month.
    /// Queries InnerTube release microformat metadata for top newest-year candidates to extract the exact release month.
    /// </summary>
    internal async Task<(AlbumItem? Release, string TypeBadge, string DateText)> ResolveArtistLatestReleaseByMonthAsync(
        string artistName,
        IReadOnlyList<AlbumItem> studioAlbums,
        IReadOnlyList<AlbumItem> singlesAndEps,
        IReadOnlyList<AlbumItem> featuredReleases,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(AlbumItem Item, bool IsSingle, int OrderIndex)>();
        int order = 0;

        foreach (var feat in featuredReleases)
        {
            candidates.Add((feat, feat.IsSingleReleaseType, order++));
        }

        foreach (var single in singlesAndEps)
        {
            if (candidates.All(c => !string.Equals(c.Item.Title, single.Title, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add((single, true, order++));
            }
        }

        foreach (var album in studioAlbums)
        {
            if (candidates.All(c => !string.Equals(c.Item.Title, album.Title, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add((album, album.IsSingleReleaseType, order++));
            }
        }

        if (candidates.Count == 0)
        {
            return (null, string.Empty, string.Empty);
        }

        int maxYear = candidates.Max(c => c.Item.ReleaseYear);
        var topCandidates = maxYear > 1900
            ? candidates.Where(c => c.Item.ReleaseYear >= maxYear - 1).OrderByDescending(c => c.Item.ReleaseYear).ThenBy(c => c.OrderIndex).Take(4).ToList()
            : candidates.Take(3).ToList();

        // Enrich top newest-year candidates with exact release month from InnerTube album/player microformat
        var enriched = new List<(AlbumItem Item, bool IsSingle, int Year, int Month, int Day, int OrderIndex)>();
        foreach (var cand in topCandidates)
        {
            int year = cand.Item.ReleaseYear;
            int month = cand.Item.ReleaseMonth > 0 ? cand.Item.ReleaseMonth : ExtractMonthFromText(cand.Item.Subtitle);
            int day = 1;

            if (month == 0 && !string.IsNullOrWhiteSpace(cand.Item.BrowseId))
            {
                var (resolvedYear, resolvedMonth, resolvedDay) = await TryProbeReleaseDateFromBrowseIdAsync(
                    cand.Item.BrowseId,
                    cancellationToken).ConfigureAwait(false);
                if (resolvedYear > 1900)
                {
                    year = resolvedYear;
                }

                if (resolvedMonth is >= 1 and <= 12)
                {
                    month = resolvedMonth;
                    day = Math.Max(1, resolvedDay);
                }
            }

            string dateText = FormatReleaseMonthYearBadge(year, month);
            var updatedAlbum = new AlbumItem
            {
                Id = cand.Item.Id,
                BrowseId = cand.Item.BrowseId,
                Title = cand.Item.Title,
                Artist = string.IsNullOrWhiteSpace(cand.Item.Artist) ? artistName : cand.Item.Artist,
                ReleaseYear = year,
                ReleaseMonth = month,
                ReleaseDateText = dateText,
                IsSingleOrEp = cand.IsSingle,
                TrackCount = cand.Item.TrackCount,
                CoverArtUri = cand.Item.CoverArtUri,
                SourceType = cand.Item.SourceType,
                Subtitle = !string.IsNullOrWhiteSpace(dateText)
                    ? $"{(cand.IsSingle ? "Single" : "Album")} • {dateText}"
                    : cand.Item.Subtitle
            };

            enriched.Add((updatedAlbum, cand.IsSingle, year, month, day, cand.OrderIndex));
        }

        var winner = enriched
            .OrderByDescending(e => e.Year)
            .ThenByDescending(e => e.Month)
            .ThenByDescending(e => e.Day)
            .ThenBy(e => e.OrderIndex)
            .First();

        string typeBadge = winner.IsSingle ? "SINGLE" : "ALBUM";
        string finalDateText = !string.IsNullOrWhiteSpace(winner.Item.ReleaseDateText)
            ? winner.Item.ReleaseDateText
            : (winner.Year > 1900 ? winner.Year.ToString(CultureInfo.InvariantCulture) : "Recent");

        return (winner.Item, typeBadge, finalDateText);
    }

    private async Task<(int Year, int Month, int Day)> TryProbeReleaseDateFromBrowseIdAsync(
        string browseId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1800));

            var browsePayload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["browseId"] = browseId
            };

            using var doc = await ExecuteInnerTubePostDocAsync(
                YtMusicBrowseEndpoint,
                browsePayload,
                includeAuthHeaders: true,
                timeoutCts.Token).ConfigureAwait(false);
            if (doc is null)
            {
                return (0, 0, 0);
            }

            // Look for first track videoId in the album browse response to check its microformat publishDate (YYYY-MM-DD)
            string? firstVideoId = FindFirstPropertyString(doc.RootElement, "videoId");
            if (!string.IsNullOrWhiteSpace(firstVideoId))
            {
                var playerPayload = new Dictionary<string, object?>
                {
                    ["context"] = BuildWebRemixContext(),
                    ["videoId"] = firstVideoId,
                    ["contentCheckOk"] = true,
                    ["racyCheckOk"] = true
                };

                using var playerDoc = await ExecuteInnerTubePostDocAsync(
                    YtMusicPlayerEndpoint,
                    playerPayload,
                    includeAuthHeaders: true,
                    timeoutCts.Token).ConfigureAwait(false);

                if (playerDoc is not null)
                {
                    string? publishDate = FindFirstPropertyString(playerDoc.RootElement, "publishDate")
                                          ?? FindFirstPropertyString(playerDoc.RootElement, "uploadDate");
                    if (!string.IsNullOrWhiteSpace(publishDate))
                    {
                        var isoMatch = IsoYearMonthDayRegex().Match(publishDate);
                        if (isoMatch.Success &&
                            int.TryParse(isoMatch.Groups[1].Value, out int y) &&
                            int.TryParse(isoMatch.Groups[2].Value, out int m) &&
                            int.TryParse(isoMatch.Groups[3].Value, out int d))
                        {
                            return (y, m, d);
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore probe timeout and fall back to subtitle year/month
        }

        return (0, 0, 0);
    }

    /// <summary>
    /// Invalidates any cached resolved music video stream for the specified track so the next resolution fetches fresh URLs.
    /// </summary>
    public void InvalidateCachedMusicVideo(TrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        string cacheKey = BuildMusicVideoCacheKey(track);
        _resolvedMusicVideoCache.Remove(cacheKey);
    }

    private static string BuildMusicVideoCacheKey(TrackItem track) =>
        !string.IsNullOrWhiteSpace(track.YouTubeVideoId)
            ? ExtractCleanVideoId(track.YouTubeVideoId)
            : $"{track.Artist.Trim()}|{track.Title.Trim()}";

    /// <inheritdoc />
    public Task<ResolvedMusicVideoStream?> ResolveMusicVideoStreamAsync(
        TrackItem track,
        CancellationToken cancellationToken = default) =>
        ResolveMusicVideoStreamAsync(track, forceLocalDownload: false, cancellationToken);

    /// <summary>
    /// Resolves a guaranteed Windows Media Foundation playable MP4 stream for <paramref name="track"/>.
    /// Prefers verified progressive non-DASH (<c>ftypmp42</c>) MP4 for instant playback and falls back to
    /// a locally cached, edit-list-neutralized (<c>edts -> free</c>) H.264 MP4 when progressive formats are
    /// unavailable or <paramref name="forceLocalDownload"/> is requested after a playback error.
    /// </summary>
    public async Task<ResolvedMusicVideoStream?> ResolveMusicVideoStreamAsync(
        TrackItem track,
        bool forceLocalDownload,
        CancellationToken cancellationToken = default)
    {
        if (track is null || string.IsNullOrWhiteSpace(track.Title) || track.Id == "empty_state_track")
        {
            return null;
        }

        string cacheKey = BuildMusicVideoCacheKey(track);

        if (!forceLocalDownload && _resolvedMusicVideoCache.TryGet(cacheKey, out var cached))
        {
            bool isLocalValid = File.Exists(cached.Stream.StreamUri);
            bool isHttpFresh = cached.Stream.StreamUri.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                               DateTimeOffset.UtcNow < cached.ExpiresAt;
            if (isLocalValid || isHttpFresh)
            {
                return cached.Stream;
            }

            _resolvedMusicVideoCache.Remove(cacheKey);
        }

        try
        {
            // 1. Collect ranked candidate official music video IDs (OMV) so if #1 is blocked/unavailable, #2/#3 succeed
            var candidates = await FindCandidateMusicVideosAsync(track, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return null;
            }

            foreach (var cand in candidates.Take(3))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? playableStreamUri = await ResolvePlayableVideoStreamAsync(
                    cand.VideoId,
                    forceLocalDownload,
                    cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(playableStreamUri))
                {
                    continue;
                }

                string thumbnailUri = !string.IsNullOrWhiteSpace(cand.ThumbnailUri)
                    ? cand.ThumbnailUri
                    : $"https://i.ytimg.com/vi/{cand.VideoId}/hqdefault.jpg";

                var result = new ResolvedMusicVideoStream(
                    VideoId: cand.VideoId,
                    StreamUri: playableStreamUri,
                    Title: string.IsNullOrWhiteSpace(cand.Title) ? track.Title : cand.Title,
                    Artist: string.IsNullOrWhiteSpace(cand.Artist) ? track.Artist : cand.Artist,
                    ThumbnailUri: thumbnailUri,
                    Duration: cand.Duration.TotalSeconds > 10 ? cand.Duration : track.Duration);

                var expiry = playableStreamUri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? DateTimeOffset.UtcNow.AddHours(3)
                    : DateTimeOffset.UtcNow.AddDays(7);

                _resolvedMusicVideoCache.Set(cacheKey, new CachedMusicVideoEntry(result, expiry));
                track.OfficialVideoId = cand.VideoId;
                return result;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve music video counterpart for '{Title}' - '{Artist}'.", track.Title, track.Artist);
            return null;
        }
    }

    private async Task<List<VideoSearchCandidate>> FindCandidateMusicVideosAsync(
        TrackItem track,
        CancellationToken cancellationToken)
    {
        var results = new List<VideoSearchCandidate>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        void TryAdd(string? rawId, string title, string artist, string thumb, TimeSpan dur)
        {
            if (string.IsNullOrWhiteSpace(rawId))
            {
                return;
            }

            string clean = ExtractCleanVideoId(rawId);
            if (string.IsNullOrWhiteSpace(clean) || !seenIds.Add(clean))
            {
                return;
            }

            results.Add(new VideoSearchCandidate(clean, title, artist, thumb, dur));
        }

        string cleanTrackVid = !string.IsNullOrWhiteSpace(track.YouTubeVideoId)
            ? ExtractCleanVideoId(track.YouTubeVideoId)
            : string.Empty;

        // 1. Track is itself an Official Video item
        if (track.IsOfficialVideoItem && !string.IsNullOrWhiteSpace(cleanTrackVid))
        {
            TryAdd(cleanTrackVid, track.Title, track.Artist, track.CoverArtUri, track.Duration);
        }

        // 2. Track already carries a pre-linked OfficialVideoId
        if (!string.IsNullOrWhiteSpace(track.OfficialVideoId))
        {
            TryAdd(track.OfficialVideoId, track.Title, track.Artist, track.CoverArtUri, track.Duration);
        }

        // 3. Reverse ATV counterpart map (OMV -> ATV)
        if (!string.IsNullOrWhiteSpace(cleanTrackVid))
        {
            EnsureAtvCounterpartCacheLoaded();
            foreach (var kvp in _atvCounterpartCache.Snapshot())
            {
                if (string.Equals(kvp.Value, cleanTrackVid, StringComparison.Ordinal) &&
                    !string.Equals(kvp.Key, cleanTrackVid, StringComparison.Ordinal))
                {
                    TryAdd(kvp.Key, track.Title, track.Artist, track.CoverArtUri, track.Duration);
                    break;
                }
            }
        }

        // 4. Native YouTube Music /next counterpartRenderer lookup (exact Song <-> Video link when provided by YTM)
        if (results.Count == 0 && !string.IsNullOrWhiteSpace(cleanTrackVid))
        {
            string? counterpartVid = await TryFindCounterpartVideoIdViaNextAsync(cleanTrackVid, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(counterpartVid) &&
                !string.Equals(counterpartVid, cleanTrackVid, StringComparison.Ordinal))
            {
                TryAdd(counterpartVid, track.Title, track.Artist, $"https://i.ytimg.com/vi/{counterpartVid}/hqdefault.jpg", track.Duration);
            }
        }

        // 5. YouTube Music search with Videos filter ("official music video" + clean fallback)
        if (results.Count == 0)
        {
            string primaryArtist = RecentAlbumItem.ExtractPrimaryArtist(track.Artist);
            string cleanTitle = CleanTitleForAtvSearch(track.Title, primaryArtist);
            string effectiveTitle = string.IsNullOrWhiteSpace(cleanTitle) ? track.Title.Trim() : cleanTitle;

            string[] queries = string.IsNullOrWhiteSpace(primaryArtist)
                ? [$"{effectiveTitle} official music video", effectiveTitle]
                : [$"{primaryArtist} {effectiveTitle} official music video", $"{primaryArtist} {effectiveTitle} official video"];

            var videoCandidates = new List<TrackItem>();
            var searchSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string query in queries)
            {
                var searchPayload = new Dictionary<string, object?>
                {
                    ["context"] = BuildWebRemixContext(),
                    ["query"] = query,
                    ["params"] = YtMusicVideosFilterParams
                };

                using var searchDoc = await ExecuteInnerTubePostDocAsync(
                    YtMusicSearchEndpoint,
                    searchPayload,
                    includeAuthHeaders: true,
                    cancellationToken).ConfigureAwait(false);

                if (searchDoc is not null)
                {
                    CollectOfficialVideoRenderersRecursive(searchDoc.RootElement, primaryArtist, videoCandidates, searchSeen, 8);
                }

                if (videoCandidates.Count > 0)
                {
                    break;
                }
            }

            foreach (var ranked in RankMatchingVideoCandidates(track, videoCandidates).Take(3))
            {
                TryAdd(ranked.YouTubeVideoId, ranked.Title, ranked.Artist, ranked.CoverArtUri, ranked.Duration);
            }
        }

        // 6. Final fallback: track's own YouTube videoId (if present)
        if (!string.IsNullOrWhiteSpace(cleanTrackVid))
        {
            TryAdd(cleanTrackVid, track.Title, track.Artist, track.CoverArtUri, track.Duration);
        }

        return results;
    }

    private async Task<string?> TryFindCounterpartVideoIdViaNextAsync(
        string trackVideoId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(2200));

            var payload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["videoId"] = trackVideoId,
                ["isAudioOnly"] = true
            };

            using var doc = await ExecuteInnerTubePostDocAsync(
                YtMusicNextEndpoint,
                payload,
                includeAuthHeaders: true,
                timeoutCts.Token).ConfigureAwait(false);
            if (doc is null)
            {
                return null;
            }

            return FindCounterpartVideoIdRecursive(doc.RootElement, trackVideoId);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindCounterpartVideoIdRecursive(JsonElement element, string sourceVideoId)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("counterpart", out var counterpartEl))
            {
                string? vid = FindFirstPropertyString(counterpartEl, "videoId");
                if (!string.IsNullOrWhiteSpace(vid) && !string.Equals(vid, sourceVideoId, StringComparison.Ordinal))
                {
                    return vid;
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                var found = FindCounterpartVideoIdRecursive(prop.Value, sourceVideoId);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindCounterpartVideoIdRecursive(item, sourceVideoId);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static List<TrackItem> RankMatchingVideoCandidates(TrackItem track, IReadOnlyList<TrackItem> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        string cleanTargetArtist = CleanAuthorForAtvSearch(RecentAlbumItem.ExtractPrimaryArtist(track.Artist));
        string cleanTargetTitle = CleanTitleForAtvSearch(track.Title, cleanTargetArtist);
        string normTargetTitle = NormalizeMatchText(cleanTargetTitle);
        string normTargetArtist = NormalizeMatchText(cleanTargetArtist);

        var scored = new List<(TrackItem Item, int Score)>();

        for (int i = 0; i < candidates.Count; i++)
        {
            var cand = candidates[i];
            string rawTitleLower = (cand.Title ?? string.Empty).ToLowerInvariant();
            string normCandTitle = NormalizeMatchText(CleanTitleForAtvSearch(cand.Title ?? string.Empty, cleanTargetArtist));
            string normCandArtist = NormalizeMatchText(cand.Artist ?? string.Empty);

            int score = 0;
            bool titleMatched = false;

            if (!string.IsNullOrWhiteSpace(normTargetTitle) && !string.IsNullOrWhiteSpace(normCandTitle))
            {
                if (string.Equals(normCandTitle, normTargetTitle, StringComparison.Ordinal))
                {
                    score += 120;
                    titleMatched = true;
                }
                else if (normCandTitle.Contains(normTargetTitle, StringComparison.Ordinal) ||
                         normTargetTitle.Contains(normCandTitle, StringComparison.Ordinal))
                {
                    score += 80;
                    titleMatched = true;
                }
            }

            if (!titleMatched)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(normTargetArtist) && !string.IsNullOrWhiteSpace(normCandArtist))
            {
                if (string.Equals(normCandArtist, normTargetArtist, StringComparison.Ordinal))
                {
                    score += 80;
                }
                else if (normCandArtist.Contains(normTargetArtist, StringComparison.Ordinal) ||
                         normTargetArtist.Contains(normCandArtist, StringComparison.Ordinal))
                {
                    score += 55;
                }
                else
                {
                    score -= 35;
                }
            }

            // Reward genuine official music videos and popular artist uploads
            if (rawTitleLower.Contains("official music video") || rawTitleLower.Contains("official video"))
            {
                score += 30;
            }

            if (cand.PlayCount >= 1_000_000)
            {
                score += 30;
            }
            else if (cand.PlayCount >= 100_000)
            {
                score += 15;
            }

            // Penalize fan re-uploads, lyric/subtitled videos, covers, nightcore, reactions, and interviews
            if (rawTitleLower.Contains("sub español") || rawTitleLower.Contains("subtitulado") ||
                rawTitleLower.Contains("lyric") || rawTitleLower.Contains("letra") ||
                rawTitleLower.Contains("karaoke") || rawTitleLower.Contains("cover") ||
                rawTitleLower.Contains("nightcore") || rawTitleLower.Contains("slowed") ||
                rawTitleLower.Contains("sped up") || rawTitleLower.Contains("reaction") ||
                rawTitleLower.Contains("interview") || rawTitleLower.Contains("audio only"))
            {
                score -= 75;
            }

            score += Math.Max(0, 12 - (i * 2));
            scored.Add((cand, score));
        }

        if (scored.Count == 0)
        {
            return [candidates[0]];
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();
    }

    private static List<(PlayerClientSpec Spec, bool UseAuth)> BuildVideoPlayerSpecs(bool isSignedIn)
    {
        var baseSpecs = BuildStreamPlayerSpecs(isSignedIn);

        // Order ANDROID FIRST because it returns progressive non-DASH ftypmp42 MP4 streams (itag 18/22)
        // that Windows Media Foundation MediaPlayer opens, seeks, and plays instantaneously without 0xC00D3E84.
        // VISIONOS/IOS follow as fast H.264 adaptive sources for local neutralized MP4 caching when progressive is unavailable.
        static int GetVideoClientPriority(string clientName, bool useAuth) => clientName switch
        {
            "ANDROID" or "ANDROID_ANON" => 0,
            "VISIONOS" or "VISIONOS_ANON" => 1,
            "IOS" or "IOS_ANON" => 2,
            "WEB_REMIX" when useAuth => 3,
            "MWEB" when useAuth => 4,
            "TVHTML5" when useAuth => 5,
            "WEB" when useAuth => 6,
            "ANDROID_VR" or "ANDROID_VR_ANON" => 7,
            _ => 8
        };

        return baseSpecs
            .OrderBy(entry => GetVideoClientPriority(entry.Spec.ClientName, entry.UseAuth))
            .ToList();
    }

    private async Task<string?> ResolvePlayableVideoStreamAsync(
        string videoId,
        bool forceLocalDownload,
        CancellationToken cancellationToken)
    {
        string targetVideoPath = Path.Combine(_cacheDirectory, $"{videoId}_video.mp4");
        if (TryGetValidCachedFile(targetVideoPath, out string? existingPath))
        {
            return existingPath;
        }

        var videoLock = GetStripedLock($"video:{videoId}", StringComparer.OrdinalIgnoreCase);
        await videoLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetValidCachedFile(targetVideoPath, out existingPath))
            {
                return existingPath;
            }

            var specs = BuildVideoPlayerSpecs(IsAuthenticated);
            var anonPair = await GetVisitorDataAndCookiesAsync(cancellationToken).ConfigureAwait(false);
            bool refreshedVisitorOnce = false;
            ResolvedAudioStream? downloadProgressiveFallback = null;
            ResolvedAudioStream? downloadAdaptiveFallback = null;

            for (int i = 0; i < specs.Count; i++)
            {
                var (spec, useAuth) = specs[i];
                string? visitorData = useAuth
                    ? (!string.IsNullOrWhiteSpace(_sessionVisitorData) ? _sessionVisitorData : _cachedVisitorData)
                    : (anonPair?.VisitorData ?? _cachedVisitorData);
                string? anonCookies = useAuth
                    ? null
                    : (anonPair?.Cookies ?? YouTubeAuthHelper.DefaultSocsCookie);

                var resolved = await TryResolveDirectVideoStreamWithClientAsync(
                    videoId,
                    spec,
                    useAuth,
                    visitorData,
                    anonCookies,
                    cancellationToken).ConfigureAwait(false);

                // If the first unauthenticated native client hit a bot/visitor check, refresh visitorData once and retry it
                if (!useAuth &&
                    !refreshedVisitorOnce &&
                    resolved.ProgressiveMp4 is null &&
                    resolved.AdaptiveMp4 is null &&
                    resolved.BlockedByAuth)
                {
                    refreshedVisitorOnce = true;
                    _visitorDataExpiry = DateTimeOffset.MinValue;
                    anonPair = await GetVisitorDataAndCookiesAsync(cancellationToken).ConfigureAwait(false);
                    if (anonPair is not null)
                    {
                        resolved = await TryResolveDirectVideoStreamWithClientAsync(
                            videoId,
                            spec,
                            useAuth: false,
                            anonPair.Value.VisitorData,
                            anonPair.Value.Cookies,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                // Tier 1 (Instant): Headerless progressive non-DASH MP4 (ftypmp42/ftypisom, e.g. ANDROID itag 18/22).
                // IMPORTANT: Never pass remote un-neutralized ftypdash adaptive URLs directly to Windows MediaPlayer
                // because Windows Media Foundation fails with DecodingError 0xC00D3E84 or stalls on ftypdash edit lists!
                if (!forceLocalDownload &&
                    !spec.SendOriginOnDownload &&
                    resolved.ProgressiveMp4 is not null)
                {
                    if (await ProbeProgressiveVideoStreamPlayableAsync(resolved.ProgressiveMp4.Url, cancellationToken).ConfigureAwait(false))
                    {
                        TraceStream($"Video fast-path progressive MP4 OK ({spec.ClientName}) video={videoId}");
                        return resolved.ProgressiveMp4.Url;
                    }
                }

                downloadProgressiveFallback ??= resolved.ProgressiveMp4;
                downloadAdaptiveFallback ??= resolved.AdaptiveMp4;

                // When forceLocalDownload is requested, stop probing further clients as soon as we have a downloadable H.264 stream
                if (forceLocalDownload && (downloadProgressiveFallback is not null || downloadAdaptiveFallback is not null))
                {
                    break;
                }
            }

            // Tier 2 (Guaranteed local playback): Download compact MP4 to local cache and neutralize DASH edts boxes.
            // Both progressive MP4 and locally neutralized H.264 adaptive MP4 play & seek reliably in Windows MediaPlayer.
            var downloadCandidates = new List<ResolvedAudioStream>(2);
            if (downloadProgressiveFallback is not null) downloadCandidates.Add(downloadProgressiveFallback);
            if (downloadAdaptiveFallback is not null) downloadCandidates.Add(downloadAdaptiveFallback);

            foreach (var dlCandidate in downloadCandidates)
            {
                string? downloadedPath = await TryDownloadVideoStreamToCacheAsync(
                    videoId,
                    dlCandidate,
                    targetVideoPath,
                    cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(downloadedPath))
                {
                    TraceStream($"Video local cached+neutralized MP4 OK ({dlCandidate.ClientName}) video={videoId}");
                    return downloadedPath;
                }
            }

            // Tier 3 (Final fallback): YoutubeExplode muxed progressive MP4 or compact H.264 video-only MP4
            try
            {
                var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId, cancellationToken).ConfigureAwait(false);
                var muxedStreamInfo = manifest.GetMuxedStreams()
                    .Where(s => s.Container == Container.Mp4)
                    .OrderByDescending(s => ScoreVideoHeight(s.VideoQuality.MaxHeight))
                    .FirstOrDefault();

                if (!forceLocalDownload &&
                    muxedStreamInfo is not null &&
                    !string.IsNullOrWhiteSpace(muxedStreamInfo.Url) &&
                    await ProbeProgressiveVideoStreamPlayableAsync(muxedStreamInfo.Url, cancellationToken).ConfigureAwait(false))
                {
                    return muxedStreamInfo.Url;
                }

                IStreamInfo? fallbackDownloadInfo = (IStreamInfo?)muxedStreamInfo
                    ?? manifest.GetVideoOnlyStreams()
                        .Where(s => s.Container == Container.Mp4 && s.VideoCodec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(s => ScoreVideoHeight(s.VideoQuality.MaxHeight))
                        .FirstOrDefault();

                if (fallbackDownloadInfo is not null)
                {
                    string tempFallback = Path.Combine(_cacheDirectory, $"{videoId}_vid_{Guid.NewGuid():N}.part");
                    try
                    {
                        await _youtubeClient.Videos.Streams.DownloadAsync(fallbackDownloadInfo, tempFallback, cancellationToken: cancellationToken).ConfigureAwait(false);
                        if (File.Exists(tempFallback) && new FileInfo(tempFallback).Length > 80_000)
                        {
                            NeutralizeDashMp4EditList(tempFallback);
                            if (File.Exists(targetVideoPath))
                            {
                                File.Delete(targetVideoPath);
                            }

                            File.Move(tempFallback, targetVideoPath, overwrite: true);
                            return targetVideoPath;
                        }
                    }
                    finally
                    {
                        if (File.Exists(tempFallback))
                        {
                            try { File.Delete(tempFallback); } catch { }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "YoutubeExplode video fallback failed for '{VideoId}'.", videoId);
            }
        }
        finally
        {
            videoLock.Release();
        }

        return null;
    }

    private async Task<bool> ProbeProgressiveVideoStreamPlayableAsync(string streamUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));

            using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
            req.Headers.TryAddWithoutValidation("User-Agent", "NSPlayer/12.00.22621.0 WMFSDK/12.00.22621.0");

            using var resp = await _httpClient.SendAsync(
                req,
                HttpCompletionOption.ResponseContentRead,
                timeoutCts.Token).ConfigureAwait(false);

            int code = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode && code != 206)
            {
                return false;
            }

            string? mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType) &&
                mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Inspect the ISO-BMFF ftyp box: reject fragmented DASH containers (ftypdash) for remote streaming
            // because Windows Media Foundation fails with DecodingError 0xC00D3E84 on remote ftypdash edit lists.
            byte[] headBytes = await resp.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            if (headBytes.Length >= 16)
            {
                string headAscii = Encoding.ASCII.GetString(headBytes, 0, Math.Min(headBytes.Length, 32));
                if (headAscii.Contains("ftypdash", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return headBytes.Length >= 16;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> TryDownloadVideoStreamToCacheAsync(
        string videoId,
        ResolvedAudioStream resolvedVideo,
        string targetVideoPath,
        CancellationToken cancellationToken)
    {
        string tempFilePath = Path.Combine(_cacheDirectory, $"{videoId}_vid_{Guid.NewGuid():N}.part");
        try
        {
            string downloadUrl = BuildStreamDownloadUrl(resolvedVideo.Url, resolvedVideo.ContentLength);
            long downloaded = await DownloadUrlToFileAsync(downloadUrl, resolvedVideo, tempFilePath, cancellationToken)
                .ConfigureAwait(false);

            if (downloaded == -403 &&
                resolvedVideo.AllowChunkedRangeRetry &&
                !resolvedVideo.Url.Contains("range=", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }

                downloaded = await DownloadChunkedToFileAsync(resolvedVideo, tempFilePath, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (downloaded > 80_000)
            {
                NeutralizeDashMp4EditList(tempFilePath);
                if (File.Exists(targetVideoPath))
                {
                    File.Delete(targetVideoPath);
                }

                File.Move(tempFilePath, targetVideoPath, overwrite: true);
                return targetVideoPath;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fall through to next fallback
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { }
            }
        }

        return null;
    }

    private async Task<ResolvedVideoPlayerCandidate> TryResolveDirectVideoStreamWithClientAsync(
        string videoId,
        PlayerClientSpec spec,
        bool useAuth,
        string? visitorData,
        string? anonCookies,
        CancellationToken cancellationToken)
    {
        bool sawAuthBlock = false;
        try
        {
            int? sts = spec.SendPlaybackContext
                ? await _scriptResolver.GetSignatureTimestampAsync(cancellationToken).ConfigureAwait(false)
                : null;

            bool canDelegate = useAuth && IsAuthenticated &&
                (YouTubeAuthHelper.GetDelegatedPageId(_dataSyncId) is not null ||
                 YouTubeAuthHelper.GetOnBehalfOfUser(_dataSyncId) is not null);
            bool[] attemptPageIds = canDelegate ? [true, false] : [false];

            foreach (bool includeDelegation in attemptPageIds)
            {
                var clientObj = new Dictionary<string, object?>
                {
                    ["clientName"] = spec.ClientName.EndsWith("_ANON", StringComparison.Ordinal)
                        ? spec.ClientName[..^"_ANON".Length]
                        : spec.ClientName,
                    ["clientVersion"] = spec.ClientVersion,
                    ["hl"] = "en",
                    ["gl"] = "US"
                };
                if (!string.IsNullOrWhiteSpace(spec.DeviceMake)) clientObj["deviceMake"] = spec.DeviceMake;
                if (!string.IsNullOrWhiteSpace(spec.DeviceModel)) clientObj["deviceModel"] = spec.DeviceModel;
                if (!string.IsNullOrWhiteSpace(spec.OsName)) clientObj["osName"] = spec.OsName;
                if (!string.IsNullOrWhiteSpace(spec.OsVersion)) clientObj["osVersion"] = spec.OsVersion;
                if (spec.AndroidSdkVersion.HasValue) clientObj["androidSdkVersion"] = spec.AndroidSdkVersion.Value;
                if (!string.IsNullOrWhiteSpace(visitorData)) clientObj["visitorData"] = visitorData;
                if (spec.UtcOffsetMinutes.HasValue) clientObj["utcOffsetMinutes"] = spec.UtcOffsetMinutes.Value;

                var contextObj = new Dictionary<string, object?> { ["client"] = clientObj };
                if (useAuth && IsAuthenticated)
                {
                    var userObj = new Dictionary<string, object?> { ["lockedSafetyMode"] = false };
                    if (includeDelegation)
                    {
                        string? onBehalfOf = YouTubeAuthHelper.GetOnBehalfOfUser(_dataSyncId);
                        if (!string.IsNullOrWhiteSpace(onBehalfOf))
                        {
                            userObj["onBehalfOfUser"] = onBehalfOf;
                        }
                    }

                    contextObj["user"] = userObj;
                }

                var payload = new Dictionary<string, object?>
                {
                    ["context"] = contextObj,
                    ["videoId"] = videoId,
                    ["contentCheckOk"] = true,
                    ["racyCheckOk"] = true
                };

                if (sts.HasValue)
                {
                    payload["playbackContext"] = new Dictionary<string, object?>
                    {
                        ["contentPlaybackContext"] = new Dictionary<string, object?>
                        {
                            ["signatureTimestamp"] = sts.Value,
                            ["html5Preference"] = "HTML5_PREF_WANTS"
                        }
                    };
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, spec.EndpointUrl)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("User-Agent", spec.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                request.Headers.TryAddWithoutValidation("Origin", spec.Origin);
                request.Headers.TryAddWithoutValidation("Referer", spec.Referer);
                if (!string.IsNullOrWhiteSpace(spec.ClientId))
                {
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", spec.ClientId);
                }

                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", spec.ClientVersion);

                if (useAuth && IsAuthenticated)
                {
                    ApplyPlayerAuthHeaders(request, spec.Origin, includePageId: includeDelegation, fallbackVisitorData: visitorData);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(visitorData))
                    {
                        request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);
                    }

                    if (!string.IsNullOrWhiteSpace(anonCookies))
                    {
                        request.Headers.TryAddWithoutValidation("Cookie", anonCookies);
                    }
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (((int)response.StatusCode is 400 or 403) && includeDelegation)
                    {
                        continue;
                    }

                    return new ResolvedVideoPlayerCandidate(null, null, sawAuthBlock);
                }

                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = ParseInnerTubeJson(body);
                if (doc is null)
                {
                    return new ResolvedVideoPlayerCandidate(null, null, sawAuthBlock);
                }

                string playStatus = string.Empty;
                string playReason = string.Empty;
                if (doc.RootElement.TryGetProperty("playabilityStatus", out var playability))
                {
                    if (playability.TryGetProperty("status", out var stProp))
                    {
                        playStatus = stProp.GetString()?.Trim() ?? string.Empty;
                    }

                    if (playability.TryGetProperty("reason", out var rProp))
                    {
                        playReason = rProp.GetString()?.Trim() ?? string.Empty;
                    }
                }

                if (string.Equals(playStatus, "LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(playStatus, "AGE_VERIFICATION_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
                    playReason.Contains("Sign in", StringComparison.OrdinalIgnoreCase) ||
                    playReason.Contains("bot", StringComparison.OrdinalIgnoreCase))
                {
                    sawAuthBlock = true;
                }

                if (IsPlayabilityBlocked(playStatus))
                {
                    if (includeDelegation)
                    {
                        continue;
                    }

                    return new ResolvedVideoPlayerCandidate(null, null, sawAuthBlock);
                }

                if (!doc.RootElement.TryGetProperty("streamingData", out var streamingData))
                {
                    return new ResolvedVideoPlayerCandidate(null, null, sawAuthBlock);
                }

                var (progressiveCandidate, adaptiveCandidate) = PickBestVideoMp4Candidates(streamingData);

                string? downloadCookie = spec.SendOriginOnDownload
                    ? (useAuth && IsAuthenticated ? YouTubeAuthHelper.EnsureConsentCookie(_cookieHeader) : anonCookies)
                    : null;

                async Task<ResolvedAudioStream?> MaterializeCandidateAsync(AudioFormatCandidate? cand)
                {
                    if (cand is null)
                    {
                        return null;
                    }

                    bool hadSig = !string.IsNullOrWhiteSpace(cand.Signature);
                    string? resolvedUrl = hadSig || spec.DecipherNChallenge
                        ? await _scriptResolver.TransformStreamUrlAsync(cand.Url, cand.Sp, cand.Signature, cancellationToken).ConfigureAwait(false)
                        : cand.Url;

                    if (string.IsNullOrWhiteSpace(resolvedUrl))
                    {
                        return null;
                    }

                    return new ResolvedAudioStream(
                        Url: resolvedUrl,
                        ClientName: spec.ClientName,
                        UserAgent: spec.UserAgent,
                        Origin: spec.SendOriginOnDownload ? spec.Origin : null,
                        Referer: spec.SendOriginOnDownload ? spec.Referer : null,
                        Cookie: downloadCookie,
                        ContentLength: cand.ContentLength,
                        AllowChunkedRangeRetry: spec.AllowChunkedRangeRetry,
                        ExpiresAt: DateTimeOffset.UtcNow.AddHours(4));
                }

                var resolvedProgressive = await MaterializeCandidateAsync(progressiveCandidate).ConfigureAwait(false);
                var resolvedAdaptive = await MaterializeCandidateAsync(adaptiveCandidate).ConfigureAwait(false);

                return new ResolvedVideoPlayerCandidate(resolvedProgressive, resolvedAdaptive, sawAuthBlock);
            }

            return new ResolvedVideoPlayerCandidate(null, null, sawAuthBlock);
        }
        catch
        {
            return new ResolvedVideoPlayerCandidate(null, null, sawAuthBlock);
        }
    }

    private static int ScoreVideoHeight(int height) => height switch
    {
        >= 450 and <= 720 => 100, // Crisp 480p-720p (includes 2.35:1 CinemaScope 544p HD!)
        >= 300 and < 450 => 92,   // Smooth 360p-449p (includes 2.35:1 CinemaScope 362p!)
        > 720 and <= 1080 => 78,  // Full HD 816p-1080p
        >= 200 and < 300 => 65,   // Fast 240p-272p
        > 1080 => 40,
        _ => 25
    };

    private static (AudioFormatCandidate? Progressive, AudioFormatCandidate? Adaptive) PickBestVideoMp4Candidates(JsonElement streamingData)
    {
        AudioFormatCandidate? bestMuxed = null;
        AudioFormatCandidate? bestAdaptive = null;

        // 1. Progressive muxed H.264 MP4 formats (itag 18 / 22, non-DASH ftypmp42)
        if (streamingData.TryGetProperty("formats", out var formats) &&
            formats.ValueKind == JsonValueKind.Array)
        {
            int bestScore = -1;
            foreach (var fmt in formats.EnumerateArray())
            {
                string mime = fmt.TryGetProperty("mimeType", out var mProp) ? (mProp.GetString() ?? string.Empty) : string.Empty;
                if (!mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var cand = ParseAudioFormatCandidate(fmt);
                if (cand is null)
                {
                    continue;
                }

                int height = fmt.TryGetProperty("height", out var hProp) && hProp.TryGetInt32(out int h) ? h : 360;
                int score = ScoreVideoHeight(height);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMuxed = cand;
                }
            }
        }

        // 2. Adaptive video/mp4 H.264 (avc1) — used for local neutralized MP4 caching when progressive is absent
        if (streamingData.TryGetProperty("adaptiveFormats", out var adaptiveFormats) &&
            adaptiveFormats.ValueKind == JsonValueKind.Array)
        {
            int bestScore = -1;
            foreach (var fmt in adaptiveFormats.EnumerateArray())
            {
                string mime = fmt.TryGetProperty("mimeType", out var mProp) ? (mProp.GetString() ?? string.Empty) : string.Empty;
                if (!mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase) ||
                    !mime.Contains("avc1", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var cand = ParseAudioFormatCandidate(fmt);
                if (cand is null)
                {
                    continue;
                }

                int height = fmt.TryGetProperty("height", out var hProp) && hProp.TryGetInt32(out int h) ? h : 360;
                int score = ScoreVideoHeight(height);
                if (cand.ContentLength is > 0 and <= 30_000_000)
                {
                    score += 8;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAdaptive = cand;
                }
            }
        }

        return (bestMuxed, bestAdaptive);
    }

    [GeneratedRegex(@"\b(19\d{2}|20\d{2})[-/](0?[1-9]|1[0-2])\b")]
    private static partial Regex IsoYearMonthRegex();

    [GeneratedRegex(@"\b(19\d{2}|20\d{2})-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])\b")]
    private static partial Regex IsoYearMonthDayRegex();

    [GeneratedRegex(@"[A-Za-zÁÉÍÓÚáéíóúñÑ]+")]
    private static partial Regex WordTokenRegex();
}
