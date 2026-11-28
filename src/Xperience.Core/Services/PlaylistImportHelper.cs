using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Services;

/// <summary>
/// Pure helper for importing and refreshing YouTube Music playlists inside the local
/// <see cref="UserPlaylist"/> collection without duplicating entries or overwriting custom local covers.
/// </summary>
public static class PlaylistImportHelper
{
    /// <summary>
    /// Inserts or updates an imported YouTube Music playlist inside <paramref name="existing"/>.
    /// Matches by normalized <see cref="UserPlaylist.ExternalPlaylistId"/> (case-insensitive).
    /// </summary>
    public static UserPlaylist UpsertYouTubeMusicPlaylist(
        List<UserPlaylist> existing,
        YouTubeMusicLibraryPlaylist sourcePlaylist,
        IReadOnlyList<TrackItem> tracks,
        string? accountLabel = null,
        string? description = null,
        string? fallbackCoverUri = null,
        DateTimeOffset? importedAt = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(sourcePlaylist);

        string externalPlaylistId = YouTubePlaylistImportParser.NormalizePlaylistId(
            !string.IsNullOrWhiteSpace(sourcePlaylist.PlaylistId)
                ? sourcePlaylist.PlaylistId
                : sourcePlaylist.BrowseId);
        string externalBrowseId = YouTubePlaylistImportParser.NormalizeBrowseId(
            sourcePlaylist.BrowseId,
            externalPlaylistId);

        string resolvedTitle = !string.IsNullOrWhiteSpace(sourcePlaylist.Title)
            ? sourcePlaylist.Title.Trim()
            : "YouTube Music Playlist";

        string resolvedDescription = !string.IsNullOrWhiteSpace(description)
            ? description.Trim()
            : sourcePlaylist.Subtitle.Trim();

        string importedCover = !string.IsNullOrWhiteSpace(fallbackCoverUri)
            ? fallbackCoverUri.Trim()
            : sourcePlaylist.ArtworkUri.Trim();

        DateTimeOffset now = importedAt ?? DateTimeOffset.UtcNow;
        string importedAtIso = now.ToString("O");

        var deduplicatedTracks = DeduplicateTracks(tracks);

        UserPlaylist? match = !string.IsNullOrWhiteSpace(externalPlaylistId)
            ? existing.FirstOrDefault(p =>
                p is not null &&
                string.Equals(
                    YouTubePlaylistImportParser.NormalizePlaylistId(p.ExternalPlaylistId),
                    externalPlaylistId,
                    StringComparison.OrdinalIgnoreCase))
            : null;

        if (match is not null)
        {
            match.Title = resolvedTitle;

            if (!string.IsNullOrWhiteSpace(resolvedDescription) || string.IsNullOrWhiteSpace(match.Description))
            {
                match.Description = resolvedDescription;
            }

            if (!IsLocalCustomCoverPath(match.CustomCoverImagePath) && !string.IsNullOrWhiteSpace(importedCover))
            {
                match.CustomCoverImagePath = importedCover;
            }

            match.Tracks = deduplicatedTracks;
            match.Source = UserPlaylist.YouTubeMusicSource;
            match.ExternalPlaylistId = externalPlaylistId;
            match.ExternalBrowseId = externalBrowseId;
            if (!string.IsNullOrWhiteSpace(accountLabel))
            {
                match.SourceAccountLabel = accountLabel.Trim();
            }

            match.ImportedAt = importedAtIso;
            return match;
        }

        var created = new UserPlaylist
        {
            Id = $"pl_{Guid.NewGuid():N}",
            Title = resolvedTitle,
            Description = resolvedDescription,
            CustomCoverImagePath = importedCover,
            CreatedAt = now,
            Tracks = deduplicatedTracks,
            Source = UserPlaylist.YouTubeMusicSource,
            ExternalPlaylistId = externalPlaylistId,
            ExternalBrowseId = externalBrowseId,
            SourceAccountLabel = accountLabel?.Trim() ?? string.Empty,
            ImportedAt = importedAtIso
        };

        existing.Insert(0, created);
        return created;
    }

    /// <summary>
    /// Returns <c>true</c> if a playlist matching <paramref name="playlistId"/> (with or without leading <c>"VL"</c>)
    /// is already present in <paramref name="existing"/>.
    /// </summary>
    public static bool IsAlreadyImported(IEnumerable<UserPlaylist> existing, string? playlistId)
    {
        if (existing is null || string.IsNullOrWhiteSpace(playlistId))
        {
            return false;
        }

        string normalizedTarget = YouTubePlaylistImportParser.NormalizePlaylistId(playlistId);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return false;
        }

        foreach (var playlist in existing)
        {
            if (playlist is null || string.IsNullOrWhiteSpace(playlist.ExternalPlaylistId))
            {
                continue;
            }

            string candidate = YouTubePlaylistImportParser.NormalizePlaylistId(playlist.ExternalPlaylistId);
            if (string.Equals(candidate, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<TrackItem> DeduplicateTracks(IReadOnlyList<TrackItem>? tracks)
    {
        var result = new List<TrackItem>();
        if (tracks is null || tracks.Count == 0)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            if (track is null || string.IsNullOrWhiteSpace(track.Id))
            {
                continue;
            }

            if (seen.Add(track.Id))
            {
                result.Add(track);
            }
        }

        return result;
    }

    private static bool IsLocalCustomCoverPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string trimmed = path.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.Contains("/CoverCache/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("\\CoverCache\\", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
