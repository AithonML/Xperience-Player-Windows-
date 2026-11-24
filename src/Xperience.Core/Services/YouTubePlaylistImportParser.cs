using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Services;

/// <summary>
/// Pure <see cref="System.Text.Json"/> parser for YouTube Music InnerTube library playlists
/// (<c>FEmusic_liked_playlists</c>), continuation tokens, and playlist track browse pages (<c>VL...</c>).
/// Zero UI or platform dependencies so it is fully unit-testable in <c>Xperience.Core.Tests</c>.
/// </summary>
public static partial class YouTubePlaylistImportParser
{
    private static readonly string[] ContinuationContainerKeys =
    [
        "musicPlaylistShelfRenderer",
        "musicPlaylistShelfContinuation",
        "gridRenderer",
        "gridContinuation",
        "musicShelfRenderer",
        "musicShelfContinuation",
        "sectionListRenderer",
        "sectionListContinuation"
    ];

    private static readonly HashSet<string> GenericPlaylistLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "playlist",
        "auto playlist",
        "lista de reproducción",
        "lista automática",
        "reproducción",
        "album",
        "single",
        "ep",
        "song",
        "video",
        "private",
        "public",
        "unlisted",
        "privada",
        "pública",
        "oculta"
    };

    /// <summary>
    /// Strips a leading <c>"VL"</c> prefix (if present) and trims whitespace from a YouTube Music playlist identifier.
    /// </summary>
    public static string NormalizePlaylistId(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return string.Empty;
        }

        string trimmed = rawId.Trim();
        if (trimmed.Length > 2 && trimmed.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[2..];
        }

        return trimmed;
    }

    /// <summary>
    /// Produces a canonical InnerTube browseId (prefixed with <c>"VL"</c> for playlist IDs).
    /// </summary>
    public static string NormalizeBrowseId(string? browseId, string? playlistId)
    {
        if (!string.IsNullOrWhiteSpace(browseId))
        {
            string trimmed = browseId.Trim();
            if (trimmed.Length > 2 && trimmed.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
            {
                return $"VL{trimmed[2..]}";
            }

            if (trimmed.StartsWith("FE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("MP", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            return $"VL{trimmed}";
        }

        string normalizedPlaylistId = NormalizePlaylistId(playlistId);
        return string.IsNullOrWhiteSpace(normalizedPlaylistId)
            ? string.Empty
            : $"VL{normalizedPlaylistId}";
    }

    /// <summary>
    /// Parses a YouTube Music <c>FEmusic_liked_playlists</c> browse response (or its continuation page)
    /// into a deduplicated list of user playlists plus an optional continuation token.
    /// </summary>
    public static (IReadOnlyList<YouTubeMusicLibraryPlaylist> Playlists, string? ContinuationToken)
        ParseLibraryPlaylistsResponse(JsonElement root)
    {
        var playlists = new List<YouTubeMusicLibraryPlaylist>();
        var seenPlaylistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectLibraryPlaylistsRecursive(root, playlists, seenPlaylistIds);
        string? continuationToken = ExtractContinuationToken(root);

        return (playlists, continuationToken);
    }

    /// <summary>
    /// Extracts the next InnerTube pagination continuation token from grid, shelf, playlist shelf,
    /// section list, or <c>continuationItemRenderer</c> nodes anywhere in the response.
    /// </summary>
    public static string? ExtractContinuationToken(JsonElement root)
    {
        return ExtractContinuationTokenCore(root);
    }

    /// <summary>
    /// Parses a YouTube Music playlist browse page (initial or continuation) into ordered deduplicated tracks,
    /// an optional next continuation token, and optional header cover art and description.
    /// Promotes studio ATV video IDs from Track Credits (<c>MPTC</c> + 11-char ID) when available while
    /// retaining OMV/regular video items so user playlist tracks are never silently dropped.
    /// </summary>
    public static (IReadOnlyList<TrackItem> Tracks, string? ContinuationToken, string? HeaderCoverUri, string? HeaderDescription)
        ParsePlaylistTracksPage(JsonElement root)
    {
        ExtractPlaylistHeaderMetadata(root, out string? headerCoverUri, out string? headerDescription);

        var tracks = new List<TrackItem>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Prefer dedicated playlist shelf nodes when present so trailing "Suggestions" shelves are not imported.
        var playlistShelves = new List<JsonElement>();
        CollectElementsByPropertyName(root, "musicPlaylistShelfRenderer", playlistShelves);
        CollectElementsByPropertyName(root, "musicPlaylistShelfContinuation", playlistShelves);

        string? continuationToken = null;

        if (playlistShelves.Count > 0)
        {
            foreach (var shelf in playlistShelves)
            {
                CollectResponsiveTracksRecursive(shelf, tracks, seenIds);
                continuationToken ??= ExtractContinuationTokenCore(shelf);
            }
        }
        else if (TryCollectAppendContinuationItems(root, tracks, seenIds, out string? appendToken))
        {
            continuationToken = appendToken;
        }
        else if (root.TryGetProperty("contents", out var contents) &&
                 contents.TryGetProperty("twoColumnBrowseResultsRenderer", out var twoCol) &&
                 twoCol.TryGetProperty("secondaryContents", out var secondary))
        {
            CollectResponsiveTracksRecursive(secondary, tracks, seenIds);
            continuationToken = ExtractContinuationTokenCore(secondary);
        }
        else
        {
            CollectResponsiveTracksRecursive(root, tracks, seenIds);
        }

        if (string.IsNullOrWhiteSpace(continuationToken) && playlistShelves.Count == 0)
        {
            continuationToken = ExtractContinuationTokenCore(root);
        }

        return (tracks, continuationToken, headerCoverUri, headerDescription);
    }

    private static bool TryCollectAppendContinuationItems(
        JsonElement root,
        List<TrackItem> tracks,
        HashSet<string> seenIds,
        out string? continuationToken)
    {
        continuationToken = null;
        if (!root.TryGetProperty("onResponseReceivedActions", out var actions) ||
            actions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        bool foundAction = false;
        foreach (var action in actions.EnumerateArray())
        {
            if (action.TryGetProperty("appendContinuationItemsAction", out var append) &&
                append.TryGetProperty("continuationItems", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foundAction = true;
                CollectResponsiveTracksRecursive(items, tracks, seenIds);
                continuationToken ??= ExtractContinuationTokenCore(items);
            }
        }

        return foundAction;
    }

    private static void CollectLibraryPlaylistsRecursive(
        JsonElement element,
        List<YouTubeMusicLibraryPlaylist> results,
        HashSet<string> seenPlaylistIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicTwoRowItemRenderer", out var twoRow) &&
                twoRow.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseLibraryPlaylistFromTwoRow(twoRow);
                if (parsed is not null && seenPlaylistIds.Add(parsed.PlaylistId))
                {
                    results.Add(parsed);
                }

                return;
            }

            if (element.TryGetProperty("musicResponsiveListItemRenderer", out var listItem) &&
                listItem.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseLibraryPlaylistFromResponsiveItem(listItem);
                if (parsed is not null && seenPlaylistIds.Add(parsed.PlaylistId))
                {
                    results.Add(parsed);
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    CollectLibraryPlaylistsRecursive(prop.Value, results, seenPlaylistIds);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectLibraryPlaylistsRecursive(item, results, seenPlaylistIds);
            }
        }
    }

    private static YouTubeMusicLibraryPlaylist? ParseLibraryPlaylistFromTwoRow(JsonElement renderer)
    {
        if (IsActionTileRenderer(renderer))
        {
            return null;
        }

        string title = renderer.TryGetProperty("title", out var titleEl)
            ? ExtractRunsText(titleEl).Trim()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        ExtractPlaylistNavigation(renderer, out string browseId, out string pageType);
        if (!IsAcceptedPlaylistNavigation(browseId, pageType))
        {
            return null;
        }

        string normalizedPlaylistId = NormalizePlaylistId(browseId);
        if (string.IsNullOrWhiteSpace(normalizedPlaylistId) || IsSkippedPlaylistId(normalizedPlaylistId))
        {
            return null;
        }

        string canonicalBrowseId = NormalizeBrowseId(browseId, normalizedPlaylistId);

        string subtitle = string.Empty;
        string author = string.Empty;
        string trackCountText = string.Empty;
        if (renderer.TryGetProperty("subtitle", out var subtitleEl))
        {
            subtitle = ExtractRunsText(subtitleEl).Trim();
            ParseSubtitleAuthorAndTrackCount(subtitleEl, out author, out trackCountText);
        }

        string artworkUri = ExtractBestThumbnailUrl(renderer, "thumbnailRenderer");

        return new YouTubeMusicLibraryPlaylist(
            PlaylistId: normalizedPlaylistId,
            BrowseId: canonicalBrowseId,
            Title: title,
            Subtitle: subtitle,
            ArtworkUri: artworkUri,
            Author: author,
            TrackCountText: trackCountText);
    }

    private static YouTubeMusicLibraryPlaylist? ParseLibraryPlaylistFromResponsiveItem(JsonElement renderer)
    {
        if (IsActionTileRenderer(renderer))
        {
            return null;
        }

        string title = string.Empty;
        string subtitle = string.Empty;
        string author = string.Empty;
        string trackCountText = string.Empty;

        if (renderer.TryGetProperty("flexColumns", out var flexColumns) &&
            flexColumns.ValueKind == JsonValueKind.Array)
        {
            int colCount = flexColumns.GetArrayLength();
            if (colCount > 0 && TryGetFlexColumnTextElement(flexColumns[0], out var titleTextEl))
            {
                title = ExtractRunsText(titleTextEl).Trim();
            }

            if (colCount > 1 && TryGetFlexColumnTextElement(flexColumns[1], out var subtitleTextEl))
            {
                subtitle = ExtractRunsText(subtitleTextEl).Trim();
                ParseSubtitleAuthorAndTrackCount(subtitleTextEl, out author, out trackCountText);
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        ExtractPlaylistNavigation(renderer, out string browseId, out string pageType);
        if (!IsAcceptedPlaylistNavigation(browseId, pageType))
        {
            return null;
        }

        string normalizedPlaylistId = NormalizePlaylistId(browseId);
        if (string.IsNullOrWhiteSpace(normalizedPlaylistId) || IsSkippedPlaylistId(normalizedPlaylistId))
        {
            return null;
        }

        string canonicalBrowseId = NormalizeBrowseId(browseId, normalizedPlaylistId);
        string artworkUri = ExtractBestThumbnailUrl(renderer, "thumbnail");

        return new YouTubeMusicLibraryPlaylist(
            PlaylistId: normalizedPlaylistId,
            BrowseId: canonicalBrowseId,
            Title: title,
            Subtitle: subtitle,
            ArtworkUri: artworkUri,
            Author: author,
            TrackCountText: trackCountText);
    }

    private static bool IsActionTileRenderer(JsonElement renderer)
    {
        if (renderer.TryGetProperty("navigationEndpoint", out var navEp) &&
            navEp.ValueKind == JsonValueKind.Object)
        {
            if (navEp.TryGetProperty("createPlaylistServiceEndpoint", out _) ||
                navEp.TryGetProperty("playlistEditorEndpoint", out _) ||
                navEp.TryGetProperty("addToPlaylistServiceEndpoint", out _) ||
                navEp.TryGetProperty("modalEndpoint", out _) ||
                navEp.TryGetProperty("playlistEditEndpoint", out _))
            {
                return true;
            }
        }

        return false;
    }

    private static void ExtractPlaylistNavigation(JsonElement renderer, out string browseId, out string pageType)
    {
        browseId = string.Empty;
        pageType = string.Empty;

        // 1. Direct navigationEndpoint.browseEndpoint
        if (renderer.TryGetProperty("navigationEndpoint", out var navEp) &&
            TryExtractBrowseEndpoint(navEp, out string directBrowseId, out string directPageType))
        {
            browseId = directBrowseId;
            pageType = directPageType;
            return;
        }

        // 2. title.runs[*].navigationEndpoint.browseEndpoint
        if (renderer.TryGetProperty("title", out var titleEl) &&
            TryExtractBrowseEndpointFromRuns(titleEl, out string titleBrowseId, out string titlePageType))
        {
            browseId = titleBrowseId;
            pageType = titlePageType;
            return;
        }

        // 3. flexColumns[0] runs navigationEndpoint.browseEndpoint
        if (renderer.TryGetProperty("flexColumns", out var flexCols) &&
            flexCols.ValueKind == JsonValueKind.Array &&
            flexCols.GetArrayLength() > 0 &&
            TryGetFlexColumnTextElement(flexCols[0], out var flexTextEl) &&
            TryExtractBrowseEndpointFromRuns(flexTextEl, out string flexBrowseId, out string flexPageType))
        {
            browseId = flexBrowseId;
            pageType = flexPageType;
            return;
        }

        // 4. thumbnailOverlay / overlay playNavigationEndpoint.watchPlaylistEndpoint.playlistId
        if (TryExtractOverlayWatchPlaylistId(renderer, out string overlayPlaylistId))
        {
            browseId = NormalizeBrowseId(null, overlayPlaylistId);
            return;
        }
    }

    private static bool TryExtractBrowseEndpoint(JsonElement navEp, out string browseId, out string pageType)
    {
        browseId = string.Empty;
        pageType = string.Empty;

        if (navEp.ValueKind != JsonValueKind.Object ||
            !navEp.TryGetProperty("browseEndpoint", out var be) ||
            be.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (be.TryGetProperty("browseId", out var bidProp) && bidProp.ValueKind == JsonValueKind.String)
        {
            browseId = bidProp.GetString()?.Trim() ?? string.Empty;
        }

        if (be.TryGetProperty("browseEndpointContextSupportedConfigs", out var cfg) &&
            cfg.ValueKind == JsonValueKind.Object &&
            cfg.TryGetProperty("browseEndpointContextMusicConfig", out var mcfg) &&
            mcfg.ValueKind == JsonValueKind.Object &&
            mcfg.TryGetProperty("pageType", out var pt) &&
            pt.ValueKind == JsonValueKind.String)
        {
            pageType = pt.GetString()?.Trim() ?? string.Empty;
        }

        return !string.IsNullOrWhiteSpace(browseId);
    }

    private static bool TryExtractBrowseEndpointFromRuns(JsonElement textContainer, out string browseId, out string pageType)
    {
        browseId = string.Empty;
        pageType = string.Empty;

        if (textContainer.ValueKind != JsonValueKind.Object ||
            !textContainer.TryGetProperty("runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("navigationEndpoint", out var runNav) &&
                TryExtractBrowseEndpoint(runNav, out browseId, out pageType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractOverlayWatchPlaylistId(JsonElement renderer, out string playlistId)
    {
        playlistId = string.Empty;

        JsonElement overlay = default;
        if (renderer.TryGetProperty("thumbnailOverlay", out var thumbOverlay))
        {
            overlay = thumbOverlay;
        }
        else if (renderer.TryGetProperty("overlay", out var itemOverlay))
        {
            overlay = itemOverlay;
        }
        else
        {
            return false;
        }

        if (overlay.ValueKind == JsonValueKind.Object &&
            overlay.TryGetProperty("musicItemThumbnailOverlayRenderer", out var overlayRenderer) &&
            overlayRenderer.TryGetProperty("content", out var content) &&
            content.TryGetProperty("musicPlayButtonRenderer", out var playBtn) &&
            playBtn.TryGetProperty("playNavigationEndpoint", out var playNav))
        {
            if (playNav.TryGetProperty("watchPlaylistEndpoint", out var wpe) &&
                wpe.TryGetProperty("playlistId", out var plProp) &&
                plProp.ValueKind == JsonValueKind.String)
            {
                string? raw = plProp.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    playlistId = raw;
                    return true;
                }
            }

            if (playNav.TryGetProperty("watchEndpoint", out var we) &&
                we.TryGetProperty("playlistId", out var wplProp) &&
                wplProp.ValueKind == JsonValueKind.String)
            {
                string? raw = wplProp.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    playlistId = raw;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsAcceptedPlaylistNavigation(string browseId, string pageType)
    {
        if (string.IsNullOrWhiteSpace(browseId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pageType) &&
            pageType.Contains("PODCAST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(browseId, "VLSE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(browseId, "SE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (browseId.StartsWith("FE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(pageType, "MUSIC_PAGE_TYPE_PLAYLIST", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return browseId.StartsWith("VL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSkippedPlaylistId(string normalizedPlaylistId) =>
        string.Equals(normalizedPlaylistId, "SE", StringComparison.OrdinalIgnoreCase);

    private static void ParseSubtitleAuthorAndTrackCount(
        JsonElement subtitleElement,
        out string author,
        out string trackCountText)
    {
        author = string.Empty;
        trackCountText = string.Empty;

        var parts = ExtractNonSeparatorRunTexts(subtitleElement);
        foreach (string part in parts)
        {
            if (string.IsNullOrWhiteSpace(trackCountText) && IsTrackCountText(part))
            {
                trackCountText = part;
                continue;
            }

            if (string.IsNullOrWhiteSpace(author) &&
                !GenericPlaylistLabels.Contains(part) &&
                !IsTrackCountText(part) &&
                !IsDurationText(part))
            {
                author = part;
            }
        }
    }

    private static bool IsTrackCountText(string text) =>
        TrackCountRegex().IsMatch(text);

    private static string? ExtractContinuationTokenCore(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // Direct continuationItemRenderer check
            if (element.TryGetProperty("continuationItemRenderer", out var contItem) &&
                contItem.ValueKind == JsonValueKind.Object)
            {
                string? itemToken = ExtractTokenFromContinuationItemRenderer(contItem);
                if (!string.IsNullOrWhiteSpace(itemToken))
                {
                    return itemToken;
                }
            }

            // Direct continuations array on the current node
            if (element.TryGetProperty("continuations", out var directConts))
            {
                string? directToken = ExtractTokenFromContinuationsArray(directConts);
                if (!string.IsNullOrWhiteSpace(directToken))
                {
                    return directToken;
                }
            }

            // Prioritized known InnerTube continuation containers
            foreach (string key in ContinuationContainerKeys)
            {
                if (element.TryGetProperty(key, out var container) &&
                    container.ValueKind == JsonValueKind.Object)
                {
                    if (container.TryGetProperty("continuations", out var conts))
                    {
                        string? tok = ExtractTokenFromContinuationsArray(conts);
                        if (!string.IsNullOrWhiteSpace(tok))
                        {
                            return tok;
                        }
                    }

                    string? nested = ExtractContinuationTokenCore(container);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            // Walk remaining properties (tabs, contents, continuationContents, onResponseReceivedActions, items, etc.)
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name is "continuations" or "continuationItemRenderer")
                {
                    continue;
                }

                if (Array.IndexOf(ContinuationContainerKeys, prop.Name) >= 0)
                {
                    continue;
                }

                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    string? found = ExtractContinuationTokenCore(prop.Value);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                string? found = ExtractContinuationTokenCore(item);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? ExtractTokenFromContinuationsArray(JsonElement continuations)
    {
        if (continuations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in continuations.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object &&
                entry.TryGetProperty("nextContinuationData", out var ncd) &&
                ncd.ValueKind == JsonValueKind.Object &&
                ncd.TryGetProperty("continuation", out var tokProp) &&
                tokProp.ValueKind == JsonValueKind.String)
            {
                string? token = tokProp.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }
        }

        return null;
    }

    private static string? ExtractTokenFromContinuationItemRenderer(JsonElement contItem)
    {
        if (!contItem.TryGetProperty("continuationEndpoint", out var endpoint) ||
            endpoint.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (endpoint.TryGetProperty("continuationCommand", out var cmd) &&
            cmd.ValueKind == JsonValueKind.Object &&
            cmd.TryGetProperty("token", out var tokenProp) &&
            tokenProp.ValueKind == JsonValueKind.String)
        {
            string? token = tokenProp.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        if (endpoint.TryGetProperty("commandExecutorCommand", out var exec) &&
            exec.ValueKind == JsonValueKind.Object &&
            exec.TryGetProperty("commands", out var commands) &&
            commands.ValueKind == JsonValueKind.Array)
        {
            foreach (var subCmd in commands.EnumerateArray())
            {
                if (subCmd.ValueKind == JsonValueKind.Object &&
                    subCmd.TryGetProperty("continuationCommand", out var innerCmd) &&
                    innerCmd.TryGetProperty("token", out var innerTok) &&
                    innerTok.ValueKind == JsonValueKind.String)
                {
                    string? token = innerTok.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        return token;
                    }
                }
            }
        }

        return null;
    }

    private static void ExtractPlaylistHeaderMetadata(
        JsonElement root,
        out string? headerCoverUri,
        out string? headerDescription)
    {
        headerCoverUri = null;
        headerDescription = null;

        ExtractHeaderRecursive(root, ref headerCoverUri, ref headerDescription);
    }

    private static void ExtractHeaderRecursive(
        JsonElement element,
        ref string? headerCoverUri,
        ref string? headerDescription)
    {
        if (!string.IsNullOrWhiteSpace(headerCoverUri) && !string.IsNullOrWhiteSpace(headerDescription))
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicEditablePlaylistDetailHeaderRenderer", out var editableHeader) &&
                editableHeader.ValueKind == JsonValueKind.Object)
            {
                ExtractHeaderRecursive(editableHeader, ref headerCoverUri, ref headerDescription);
            }

            if (element.TryGetProperty("musicResponsiveHeaderRenderer", out var respHeader) &&
                respHeader.ValueKind == JsonValueKind.Object)
            {
                TryPopulateFromHeaderRenderer(respHeader, ref headerCoverUri, ref headerDescription);
            }

            if (element.TryGetProperty("musicDetailHeaderRenderer", out var detailHeader) &&
                detailHeader.ValueKind == JsonValueKind.Object)
            {
                TryPopulateFromHeaderRenderer(detailHeader, ref headerCoverUri, ref headerDescription);
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name is "musicPlaylistShelfRenderer" or "musicShelfRenderer" or "musicResponsiveListItemRenderer")
                {
                    continue;
                }

                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    ExtractHeaderRecursive(prop.Value, ref headerCoverUri, ref headerDescription);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractHeaderRecursive(item, ref headerCoverUri, ref headerDescription);
            }
        }
    }

    private static void TryPopulateFromHeaderRenderer(
        JsonElement headerRenderer,
        ref string? headerCoverUri,
        ref string? headerDescription)
    {
        if (string.IsNullOrWhiteSpace(headerCoverUri))
        {
            string thumb = ExtractBestThumbnailUrl(headerRenderer, "thumbnail");
            if (!string.IsNullOrWhiteSpace(thumb))
            {
                headerCoverUri = thumb;
            }
        }

        if (string.IsNullOrWhiteSpace(headerDescription) &&
            headerRenderer.TryGetProperty("description", out var descEl))
        {
            string desc = string.Empty;
            if (descEl.ValueKind == JsonValueKind.Object &&
                descEl.TryGetProperty("musicDescriptionShelfRenderer", out var descShelf) &&
                descShelf.TryGetProperty("description", out var innerDesc))
            {
                desc = ExtractRunsText(innerDesc).Trim();
            }
            else
            {
                desc = ExtractRunsText(descEl).Trim();
            }

            if (!string.IsNullOrWhiteSpace(desc))
            {
                headerDescription = desc;
            }
        }
    }

    private static void CollectElementsByPropertyName(
        JsonElement element,
        string propertyName,
        List<JsonElement> matches)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out var found) &&
                found.ValueKind == JsonValueKind.Object)
            {
                matches.Add(found);
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    CollectElementsByPropertyName(prop.Value, propertyName, matches);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectElementsByPropertyName(item, propertyName, matches);
            }
        }
    }

    private static void CollectResponsiveTracksRecursive(
        JsonElement element,
        List<TrackItem> tracks,
        HashSet<string> seenIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicResponsiveListItemRenderer", out var renderer) &&
                renderer.ValueKind == JsonValueKind.Object)
            {
                var track = ParsePlaylistTrackFromResponsiveItem(renderer);
                if (track is not null && seenIds.Add(track.Id))
                {
                    tracks.Add(track);
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    CollectResponsiveTracksRecursive(prop.Value, tracks, seenIds);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectResponsiveTracksRecursive(item, tracks, seenIds);
            }
        }
    }

    private static TrackItem? ParsePlaylistTrackFromResponsiveItem(JsonElement renderer)
    {
        // Promote canonical ATV video ID from Track Credits ("MPTC" + 11-char ID) when available,
        // otherwise keep the playlist item's own videoId (retaining OMV/UGC/regular video items!).
        string? videoId = ExtractPreferredVideoId(renderer);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        string title = string.Empty;
        var artistNames = new List<string>();
        string fallbackArtist = string.Empty;
        string album = string.Empty;
        string fallbackAlbum = string.Empty;
        TimeSpan duration = TimeSpan.Zero;

        if (renderer.TryGetProperty("flexColumns", out var flexColumns) &&
            flexColumns.ValueKind == JsonValueKind.Array)
        {
            int colCount = flexColumns.GetArrayLength();
            if (colCount > 0 && TryGetFlexColumnTextElement(flexColumns[0], out var col0Text))
            {
                title = ExtractRunsText(col0Text).Trim();
            }

            for (int colIndex = 1; colIndex < colCount; colIndex++)
            {
                if (!TryGetFlexColumnTextElement(flexColumns[colIndex], out var colText) ||
                    !colText.TryGetProperty("runs", out var runs) ||
                    runs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var run in runs.EnumerateArray())
                {
                    string runText = run.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String
                        ? tProp.GetString()?.Trim() ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(runText) || IsSeparatorToken(runText))
                    {
                        continue;
                    }

                    if (duration == TimeSpan.Zero && TryParseDuration(runText, out var parsedDuration))
                    {
                        duration = parsedDuration;
                        continue;
                    }

                    if (IsViewsOrPlaysText(runText))
                    {
                        continue;
                    }

                    string runPageType = string.Empty;
                    if (run.TryGetProperty("navigationEndpoint", out var runNav))
                    {
                        TryExtractBrowseEndpoint(runNav, out _, out runPageType);
                    }

                    if (string.Equals(runPageType, "MUSIC_PAGE_TYPE_ARTIST", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(runPageType, "MUSIC_PAGE_TYPE_USER_CHANNEL", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!artistNames.Contains(runText, StringComparer.OrdinalIgnoreCase))
                        {
                            artistNames.Add(runText);
                        }
                    }
                    else if (string.Equals(runPageType, "MUSIC_PAGE_TYPE_ALBUM", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(album))
                        {
                            album = runText;
                        }
                    }
                    else if (colIndex == 1 && string.IsNullOrWhiteSpace(fallbackArtist) && !GenericPlaylistLabels.Contains(runText))
                    {
                        fallbackArtist = runText;
                    }
                    else if (colIndex >= 2 && string.IsNullOrWhiteSpace(fallbackAlbum) && !GenericPlaylistLabels.Contains(runText))
                    {
                        fallbackAlbum = runText;
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Fixed columns typically carry the track duration (e.g. "3:42").
        if (duration == TimeSpan.Zero &&
            renderer.TryGetProperty("fixedColumns", out var fixedColumns) &&
            fixedColumns.ValueKind == JsonValueKind.Array)
        {
            foreach (var fixedCol in fixedColumns.EnumerateArray())
            {
                if (fixedCol.TryGetProperty("musicResponsiveListItemFixedColumnRenderer", out var fixedRenderer) &&
                    fixedRenderer.TryGetProperty("text", out var fixedText))
                {
                    string text = ExtractRunsText(fixedText).Trim();
                    if (TryParseDuration(text, out var parsedDuration))
                    {
                        duration = parsedDuration;
                        break;
                    }
                }
            }
        }

        string resolvedArtist = artistNames.Count > 0
            ? string.Join(", ", artistNames)
            : (!string.IsNullOrWhiteSpace(fallbackArtist) ? fallbackArtist : "Unknown Artist");

        string resolvedAlbum = !string.IsNullOrWhiteSpace(album) ? album : fallbackAlbum;
        string coverArtUri = ExtractBestThumbnailUrl(renderer, "thumbnail");
        bool isExplicit = HasExplicitBadge(renderer);

        return new TrackItem
        {
            Id = $"ytm_{videoId}",
            Title = title,
            Artist = resolvedArtist,
            Album = resolvedAlbum,
            Duration = duration,
            CoverArtUri = coverArtUri,
            AudioSourceUri = $"ytmusic://{videoId}",
            YouTubeVideoId = videoId,
            SourceType = MediaSourceType.Online,
            QualityBadge = "YT MUSIC",
            IsExplicit = isExplicit
        };
    }

    private static string? ExtractPreferredVideoId(JsonElement renderer)
    {
        // 1. Promote ATV studio audio ID from Track Credits ("MPTC" + 11-char ID) when available.
        string? atvFromCredits = ExtractTrackCreditsAtvVideoId(renderer);
        if (!string.IsNullOrWhiteSpace(atvFromCredits))
        {
            return atvFromCredits;
        }

        // 2. playlistItemData.videoId
        if (renderer.TryGetProperty("playlistItemData", out var pid) &&
            pid.ValueKind == JsonValueKind.Object &&
            pid.TryGetProperty("videoId", out var pidVid) &&
            pidVid.ValueKind == JsonValueKind.String)
        {
            string? id = pidVid.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        // 3. overlay playNavigationEndpoint.watchEndpoint.videoId
        if (renderer.TryGetProperty("overlay", out var overlay) &&
            overlay.ValueKind == JsonValueKind.Object &&
            overlay.TryGetProperty("musicItemThumbnailOverlayRenderer", out var ovr) &&
            ovr.TryGetProperty("content", out var content) &&
            content.TryGetProperty("musicPlayButtonRenderer", out var playBtn) &&
            playBtn.TryGetProperty("playNavigationEndpoint", out var playNav) &&
            playNav.TryGetProperty("watchEndpoint", out var watchEp) &&
            watchEp.TryGetProperty("videoId", out var ovVid) &&
            ovVid.ValueKind == JsonValueKind.String)
        {
            string? id = ovVid.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        // 4. First videoId anywhere in renderer (e.g. flexColumns[0] watchEndpoint)
        return FindFirstStringProperty(renderer, "videoId");
    }

    private static string? ExtractTrackCreditsAtvVideoId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("browseId", out var bidProp) &&
                bidProp.ValueKind == JsonValueKind.String)
            {
                string? raw = bidProp.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var match = TrackCreditsAtvRegex().Match(raw);
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    string? found = ExtractTrackCreditsAtvVideoId(prop.Value);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                string? found = ExtractTrackCreditsAtvVideoId(item);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? FindFirstStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                string? val = prop.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return val;
                }
            }

            foreach (var child in element.EnumerateObject())
            {
                if (child.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    string? found = FindFirstStringProperty(child.Value, propertyName);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                string? found = FindFirstStringProperty(item, propertyName);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static bool TryGetFlexColumnTextElement(JsonElement flexColumn, out JsonElement textElement)
    {
        if (flexColumn.ValueKind == JsonValueKind.Object &&
            flexColumn.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var colRenderer) &&
            colRenderer.ValueKind == JsonValueKind.Object &&
            colRenderer.TryGetProperty("text", out textElement) &&
            textElement.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        textElement = default;
        return false;
    }

    private static string ExtractRunsText(JsonElement textElement)
    {
        if (textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString() ?? string.Empty;
        }

        if (textElement.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (textElement.TryGetProperty("simpleText", out var simple) &&
            simple.ValueKind == JsonValueKind.String)
        {
            return simple.GetString() ?? string.Empty;
        }

        if (!textElement.TryGetProperty("runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var run in runs.EnumerateArray())
        {
            if (run.ValueKind == JsonValueKind.Object &&
                run.TryGetProperty("text", out var t) &&
                t.ValueKind == JsonValueKind.String)
            {
                sb.Append(t.GetString());
            }
        }

        return sb.ToString();
    }

    private static List<string> ExtractNonSeparatorRunTexts(JsonElement textElement)
    {
        var result = new List<string>();
        if (textElement.ValueKind == JsonValueKind.Object &&
            textElement.TryGetProperty("runs", out var runs) &&
            runs.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in runs.EnumerateArray())
            {
                if (run.ValueKind == JsonValueKind.Object &&
                    run.TryGetProperty("text", out var t) &&
                    t.ValueKind == JsonValueKind.String)
                {
                    string text = t.GetString()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text) && !IsSeparatorToken(text))
                    {
                        result.Add(text);
                    }
                }
            }
        }
        else
        {
            string plain = ExtractRunsText(textElement).Trim();
            if (!string.IsNullOrWhiteSpace(plain))
            {
                foreach (string segment in plain.Split(['•', '·'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!string.IsNullOrWhiteSpace(segment))
                    {
                        result.Add(segment);
                    }
                }
            }
        }

        return result;
    }

    private static string ExtractBestThumbnailUrl(JsonElement renderer, string thumbnailContainerKey)
    {
        if (!renderer.TryGetProperty(thumbnailContainerKey, out var thumbContainer) ||
            thumbContainer.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        JsonElement thumbnailsArray = default;
        if (thumbContainer.TryGetProperty("musicThumbnailRenderer", out var mtr) &&
            mtr.TryGetProperty("thumbnail", out var mtrThumb) &&
            mtrThumb.TryGetProperty("thumbnails", out var mtrArr) &&
            mtrArr.ValueKind == JsonValueKind.Array)
        {
            thumbnailsArray = mtrArr;
        }
        else if (thumbContainer.TryGetProperty("croppedSquareThumbnailRenderer", out var csr) &&
                 csr.TryGetProperty("thumbnail", out var csrThumb) &&
                 csrThumb.TryGetProperty("thumbnails", out var csrArr) &&
                 csrArr.ValueKind == JsonValueKind.Array)
        {
            thumbnailsArray = csrArr;
        }
        else if (thumbContainer.TryGetProperty("thumbnails", out var directArr) &&
                 directArr.ValueKind == JsonValueKind.Array)
        {
            thumbnailsArray = directArr;
        }

        if (thumbnailsArray.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        string bestUrl = string.Empty;
        long bestArea = -1;
        foreach (var item in thumbnailsArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("url", out var urlProp) ||
                urlProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? url = urlProp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            long width = item.TryGetProperty("width", out var wProp) && wProp.TryGetInt64(out long w) ? w : 0;
            long height = item.TryGetProperty("height", out var hProp) && hProp.TryGetInt64(out long h) ? h : 0;
            long area = width > 0 && height > 0 ? width * height : Math.Max(width, height);

            if (area >= bestArea)
            {
                bestArea = area;
                bestUrl = url;
            }
        }

        if (bestUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{bestUrl}";
        }

        return bestUrl;
    }

    private static bool HasExplicitBadge(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("badges", out var badges) ||
            badges.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var badge in badges.EnumerateArray())
        {
            if (badge.ValueKind != JsonValueKind.Object ||
                !badge.TryGetProperty("musicInlineBadgeRenderer", out var inline))
            {
                continue;
            }

            if (inline.TryGetProperty("icon", out var icon) &&
                icon.TryGetProperty("iconType", out var iconType) &&
                iconType.ValueKind == JsonValueKind.String &&
                string.Equals(iconType.GetString(), "MUSIC_EXPLICIT_BADGE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (inline.TryGetProperty("accessibilityData", out var acc) &&
                acc.TryGetProperty("accessibilityData", out var accInner) &&
                accInner.TryGetProperty("label", out var label) &&
                label.ValueKind == JsonValueKind.String &&
                label.GetString()?.Contains("Explicit", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSeparatorToken(string text) =>
        text is "•" or "·" or "-" or "–" or "—" or "," or "&" or "|";

    private static bool IsViewsOrPlaysText(string text) =>
        ViewsOrPlaysRegex().IsMatch(text);

    private static bool IsDurationText(string text) =>
        DurationRegex().IsMatch(text);

    private static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text) || !DurationRegex().IsMatch(text))
        {
            return false;
        }

        string[] parts = text.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
        {
            duration = new TimeSpan(0, m, s);
            return true;
        }

        if (parts.Length == 3 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int min) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec))
        {
            duration = new TimeSpan(h, min, sec);
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^MPTC([a-zA-Z0-9_\-]{11})$")]
    private static partial Regex TrackCreditsAtvRegex();

    [GeneratedRegex(@"^\d{1,2}:\d{2}(:\d{2})?$")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"\b\d[\d.,]*\s*(songs?|tracks?|videos?|items?|canciones?|canci[oó]n|pistas?|elementos?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TrackCountRegex();

    [GeneratedRegex(@"\b(views?|plays?|reproducciones?|visualizaciones?|vistas?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ViewsOrPlaysRegex();
}
