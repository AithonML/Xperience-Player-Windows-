using Xperience.Core.Domain.Entities;

namespace Xperience.Core.Domain.Models;

/// <summary>
/// Represents a playlist discovered in the signed-in user's YouTube Music library
/// (<c>FEmusic_liked_playlists</c>) ready for local import into Xperience Player.
/// </summary>
public sealed record YouTubeMusicLibraryPlaylist(
    string PlaylistId,
    string BrowseId,
    string Title,
    string Subtitle = "",
    string ArtworkUri = "",
    string Author = "",
    string TrackCountText = "")
{
    /// <summary>
    /// Gets a value indicating whether this playlist has a real cover image URI.
    /// </summary>
    public bool HasArtwork =>
        !string.IsNullOrWhiteSpace(ArtworkUri) &&
        !ArtworkUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !ArtworkUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a safe cover URI for XAML image bindings (never empty or invalid).
    /// </summary>
    public string SafeArtworkUri => HasArtwork
        ? TrackItem.NormalizeCoverUri(ArtworkUri)
        : TrackItem.FallbackTransparentCoverUri;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the artwork layer.
    /// </summary>
    public double ArtworkOpacity => HasArtwork ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the fallback music icon layer.
    /// </summary>
    public double FallbackIconOpacity => HasArtwork ? 0.0 : 1.0;

    /// <summary>
    /// Gets a concise display subtitle combining author and track count when available.
    /// </summary>
    public string DisplaySubtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Subtitle))
            {
                return Subtitle.Trim();
            }

            if (!string.IsNullOrWhiteSpace(Author) && !string.IsNullOrWhiteSpace(TrackCountText))
            {
                return $"{Author.Trim()} • {TrackCountText.Trim()}";
            }

            if (!string.IsNullOrWhiteSpace(TrackCountText))
            {
                return TrackCountText.Trim();
            }

            if (!string.IsNullOrWhiteSpace(Author))
            {
                return Author.Trim();
            }

            return "YouTube Music";
        }
    }
}
