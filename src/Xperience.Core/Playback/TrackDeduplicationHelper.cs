using Xperience.Core.Domain.Entities;

namespace Xperience.Core.Playback;

/// <summary>
/// Normalization and deduplication helpers shared by <see cref="PlaybackQueueController"/>,
/// <see cref="ProgressiveQueueManager"/>, and YouTube Music radio recommendation fetchers.
/// </summary>
public static class TrackDeduplicationHelper
{
    private static readonly char[] PrimaryArtistDelimiters = [',', ';', '/', '&'];

    /// <summary>
    /// Extracts the primary performing artist name by splitting on <c>,</c>, <c>;</c>, <c>/</c>, and <c>&amp;</c>
    /// and returning the first non-empty trimmed segment (matching Android's <c>primaryArtistName</c>).
    /// </summary>
    public static string PrimaryArtistName(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        string[] segments = artist.Split(
            PrimaryArtistDelimiters,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return segments.Length > 0 ? segments[0] : artist.Trim();
    }

    /// <summary>
    /// Normalizes a track or YouTube identifier by trimming whitespace, stripping any leading <c>ytm_</c> prefix,
    /// and lowercasing with invariant culture.
    /// </summary>
    public static string NormalizeTrackId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        string trimmed = id.Trim();
        if (trimmed.StartsWith("ytm_", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[4..].Trim();
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Strips any leading <c>ytm_</c> prefix from an identifier while preserving original character casing.
    /// </summary>
    public static string StripYtmPrefix(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        string trimmed = id.Trim();
        return trimmed.StartsWith("ytm_", StringComparison.OrdinalIgnoreCase)
            ? trimmed[4..].Trim()
            : trimmed;
    }

    /// <summary>
    /// Extracts a YouTube videoId from a <c>ytmusic://</c> URI or a YouTube HTTP(S) URL, or returns <c>null</c>
    /// if the input does not point to a YouTube video resource.
    /// </summary>
    public static string? ExtractYouTubeVideoIdFromUri(string? uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath))
        {
            return null;
        }

        string trimmed = uriOrPath.Trim();

        if (trimmed.StartsWith("ytmusic://", StringComparison.OrdinalIgnoreCase))
        {
            string rest = trimmed["ytmusic://".Length..].Trim('/');
            int sep = rest.IndexOfAny(['?', '#', '/', '&']);
            string candidate = sep >= 0 ? rest[..sep] : rest;
            candidate = StripYtmPrefix(candidate);
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        string host = uri.Host;
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            string seg = uri.AbsolutePath.Trim('/');
            int slash = seg.IndexOf('/');
            string candidate = slash >= 0 ? seg[..slash] : seg;
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
        }

        if (host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            // Check ?v=<videoId> query parameter
            string query = uri.Query;
            if (!string.IsNullOrEmpty(query))
            {
                string q = query.TrimStart('?');
                foreach (string pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = pair[..eq];
                    if (key.Equals("v", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = Uri.UnescapeDataString(pair[(eq + 1)..]).Trim();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            return val;
                        }
                    }
                }
            }

            // Check /embed/<id>, /shorts/<id>, /v/<id>
            string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 &&
                (segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase) ||
                 segments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase) ||
                 segments[0].Equals("v", StringComparison.OrdinalIgnoreCase)))
            {
                string candidate = segments[1].Trim();
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts all normalized (trimmed, <c>ytm_</c>-stripped, lowercased) identifiers associated with <paramref name="track"/>,
    /// including <see cref="TrackItem.Id"/>, <see cref="TrackItem.YouTubeVideoId"/>, and any videoId embedded in
    /// <see cref="TrackItem.AudioSourceUri"/>.
    /// </summary>
    public static HashSet<string> ExtractNormalizedTrackIds(TrackItem? track)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (track is null)
        {
            return ids;
        }

        string normId = NormalizeTrackId(track.Id);
        if (!string.IsNullOrEmpty(normId))
        {
            ids.Add(normId);
        }

        string normVideoId = NormalizeTrackId(track.YouTubeVideoId);
        if (!string.IsNullOrEmpty(normVideoId))
        {
            ids.Add(normVideoId);
        }

        string? uriVideoId = ExtractYouTubeVideoIdFromUri(track.AudioSourceUri);
        string normUriVideoId = NormalizeTrackId(uriVideoId);
        if (!string.IsNullOrEmpty(normUriVideoId))
        {
            ids.Add(normUriVideoId);
        }

        return ids;
    }

    /// <summary>
    /// Builds the exact title + artist deduplication key (<c>"{title}||{artist}"</c>, trimmed and lowercased)
    /// used by <see cref="PlaybackQueueController"/>.
    /// </summary>
    public static string BuildTitleArtistKey(string? title, string? artist)
    {
        string normalizedTitle = (title ?? string.Empty).Trim().ToLowerInvariant();
        string normalizedArtist = (artist ?? string.Empty).Trim().ToLowerInvariant();
        return $"{normalizedTitle}||{normalizedArtist}";
    }

    /// <summary>
    /// Builds the title + primary-artist deduplication key (<c>"{title}::{primaryArtist}"</c>, trimmed and lowercased)
    /// used by remote similar-track fetchers.
    /// </summary>
    public static string BuildTitlePrimaryArtistKey(string? title, string? artist)
    {
        string normalizedTitle = (title ?? string.Empty).Trim().ToLowerInvariant();
        string normalizedPrimaryArtist = PrimaryArtistName(artist).ToLowerInvariant();
        return $"{normalizedTitle}::{normalizedPrimaryArtist}";
    }

    /// <summary>
    /// Populates <paramref name="destination"/> with all raw, <c>ytm_</c>-stripped, and <c>ytm_</c>-prefixed
    /// identifiers associated with <paramref name="track"/> (ID, YouTubeVideoId, and URI videoId).
    /// </summary>
    internal static void AddExclusionFormsForTrack(TrackItem track, HashSet<string> destination)
    {
        AddIdForms(track.Id, destination);
        AddIdForms(track.YouTubeVideoId, destination);

        string? uriVideoId = ExtractYouTubeVideoIdFromUri(track.AudioSourceUri);
        if (!string.IsNullOrWhiteSpace(uriVideoId))
        {
            AddIdForms(uriVideoId, destination);
        }
    }

    private static void AddIdForms(string? rawId, HashSet<string> destination)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return;
        }

        string trimmed = rawId.Trim();
        destination.Add(trimmed);

        string stripped = StripYtmPrefix(trimmed);
        if (!string.IsNullOrEmpty(stripped))
        {
            destination.Add(stripped);
            destination.Add($"ytm_{stripped}");
        }
    }
}
