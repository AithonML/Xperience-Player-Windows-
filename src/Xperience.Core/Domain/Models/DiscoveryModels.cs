using System.Globalization;
using System.Text;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;

namespace Xperience.Core.Domain.Models;

/// <summary>
/// Represents the rich Artist Detail view model powered exclusively by YouTube Music (plus Local catalog merge)
/// and optional Apple Music custom artist title wordmark artwork.
/// </summary>
public sealed class ArtistDetailProfile
{
    /// <summary>
    /// Gets the artist's display name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the official high-resolution square/circular artist image URI obtained from YouTube Music.
    /// </summary>
    public string AvatarUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the ultra-wide high-resolution artist hero banner image URI obtained from YouTube Music's immersive header.
    /// </summary>
    public string BannerImageUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the optional custom artist title logo/wordmark transparent PNG URI obtained from Apple Music (<c>artistLogo</c>).
    /// </summary>
    public string CustomTitleLogoUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the optional 16:9 looping Apple Music artist header motion video stream URI or cached local file path.
    /// </summary>
    public string HeaderVideoUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the optional seed hex background color reported by Apple Music editorial artwork.
    /// </summary>
    public string KeyColorHex { get; init; } = string.Empty;

    /// <summary>
    /// Gets the subscriber count text from YouTube Music (e.g., "3,14 M").
    /// </summary>
    public string SubscribersCountText { get; init; } = string.Empty;

    /// <summary>
    /// Gets the monthly listeners text from YouTube Music (e.g., "2,88 M usuarios mensuales").
    /// </summary>
    public string MonthlyListenersText { get; init; } = string.Empty;

    /// <summary>
    /// Gets the artist biography/summary obtained from YouTube Music.
    /// </summary>
    public string Biography { get; init; } = string.Empty;

    /// <summary>
    /// Gets the subtitle displaying monthly listeners or YouTube Music artist statistics.
    /// </summary>
    public string MetadataSubtitle { get; init; } = string.Empty;

    /// <summary>
    /// Gets the top most listened tracks for this artist ordered by YouTube Music play counts (plus local matches).
    /// </summary>
    public IReadOnlyList<TrackItem> TopTracks { get; init; } = [];

    /// <summary>
    /// Gets the combined studio albums discography merging YouTube Music albums and Local albums by this artist.
    /// </summary>
    public IReadOnlyList<AlbumItem> CombinedAlbums { get; init; } = [];

    /// <summary>
    /// Gets the Singles and EPs by this artist from YouTube Music.
    /// </summary>
    public IReadOnlyList<AlbumItem> SinglesAndEps { get; init; } = [];

    /// <summary>
    /// Gets the artist playlists and "Featured on" playlists from YouTube Music.
    /// </summary>
    public IReadOnlyList<AlbumItem> ArtistPlaylists { get; init; } = [];

    /// <summary>
    /// Gets related artists ("Puede que también te guste") from YouTube Music.
    /// </summary>
    public IReadOnlyList<ArtistItem> SimilarArtists { get; init; } = [];

    /// <summary>
    /// Gets the artist's featured latest release (Single or Album) determined by release month and year.
    /// </summary>
    public AlbumItem? LatestRelease { get; init; }

    /// <summary>
    /// Gets the badge label for the artist's latest release ("SENCILLO" or "ÁLBUM").
    /// </summary>
    public string LatestReleaseTypeBadge { get; init; } = string.Empty;

    /// <summary>
    /// Gets the formatted release date/month text for the artist's latest release (e.g., "Marzo 2025").
    /// </summary>
    public string LatestReleaseDateText { get; init; } = string.Empty;

    /// <summary>
    /// Gets the official music videos ("Videos oficiales") for this artist.
    /// </summary>
    public IReadOnlyList<TrackItem> OfficialVideos { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether a featured latest release is available for this artist.
    /// </summary>
    public bool HasLatestRelease => LatestRelease is not null && !string.IsNullOrWhiteSpace(LatestRelease.Title);

    /// <summary>
    /// Gets a value indicating whether official music videos are available for this artist.
    /// </summary>
    public bool HasOfficialVideos => OfficialVideos is { Count: > 0 };

    /// <summary>
    /// Gets the title of the featured latest release for XAML binding.
    /// </summary>
    public string LatestReleaseTitle => LatestRelease?.Title ?? string.Empty;

    /// <summary>
    /// Gets the subtitle of the featured latest release for XAML binding.
    /// </summary>
    public string LatestReleaseSubtitle => LatestRelease?.DisplaySubtitle ?? string.Empty;

    /// <summary>
    /// Gets the safe cover art URI of the featured latest release for XAML binding.
    /// </summary>
    public string LatestReleaseSafeCoverUri => LatestRelease?.SafeCoverArtUri ?? TrackItem.FallbackTransparentCoverUri;

    /// <summary>
    /// Gets the cover art opacity of the featured latest release for XAML binding.
    /// </summary>
    public double LatestReleaseCoverOpacity => LatestRelease?.CoverArtOpacity ?? 0.0;

    /// <summary>
    /// Gets the fallback icon opacity of the featured latest release for XAML binding.
    /// </summary>
    public double LatestReleaseDefaultIconOpacity => LatestRelease?.DefaultCoverIconOpacity ?? 1.0;

    /// <summary>
    /// Gets the resolved release type badge text ("SINGLE" or "ALBUM").
    /// </summary>
    public string LatestReleaseResolvedTypeBadge => !string.IsNullOrWhiteSpace(LatestReleaseTypeBadge)
        ? LatestReleaseTypeBadge
        : (LatestRelease?.ReleaseTypeBadgeText ?? "RELEASE");

    /// <summary>
    /// Gets the resolved release date/month text (e.g., "March 2025").
    /// </summary>
    public string LatestReleaseResolvedDateBadge => !string.IsNullOrWhiteSpace(LatestReleaseDateText)
        ? LatestReleaseDateText
        : (!string.IsNullOrWhiteSpace(LatestRelease?.ReleaseDateText)
            ? LatestRelease!.ReleaseDateText
            : (LatestRelease?.ReleaseYear > 1900 ? LatestRelease.ReleaseYear.ToString(CultureInfo.InvariantCulture) : string.Empty));

    /// <summary>
    /// Gets the composite eyebrow header badge for the featured latest release section.
    /// </summary>
    public string LatestReleaseEyebrowBadge => !string.IsNullOrWhiteSpace(LatestReleaseResolvedDateBadge)
        ? $"LATEST RELEASE • {LatestReleaseResolvedTypeBadge} • {LatestReleaseResolvedDateBadge.ToUpperInvariant()}"
        : $"LATEST RELEASE • {LatestReleaseResolvedTypeBadge}";

    /// <summary>
    /// Gets a value indicating whether a real artist photo is available.
    /// </summary>
    public bool HasAvatar =>
        !string.IsNullOrWhiteSpace(AvatarUri) &&
        !AvatarUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !AvatarUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a value indicating whether a wide hero banner image is available.
    /// </summary>
    public bool HasBanner =>
        !string.IsNullOrWhiteSpace(BannerImageUri) &&
        !BannerImageUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !BannerImageUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a safe URI for XAML ImageBrush/Image bindings for the square/circle avatar.
    /// </summary>
    public string SafeAvatarUri => HasAvatar ? TrackItem.NormalizeCoverUri(AvatarUri) : TrackItem.FallbackTransparentCoverUri;

    /// <summary>
    /// Gets a safe URI for XAML ImageBrush/Image bindings for the wide hero banner (falling back to <see cref="SafeAvatarUri"/>).
    /// </summary>
    public string SafeBannerUri => HasBanner ? TrackItem.NormalizeCoverUri(BannerImageUri) : SafeAvatarUri;

    /// <summary>
    /// Gets a value indicating whether an Apple Music custom artist title logo/wordmark is available.
    /// </summary>
    public bool HasCustomTitleLogo =>
        !string.IsNullOrWhiteSpace(CustomTitleLogoUri) &&
        !CustomTitleLogoUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a safe URI for XAML Image binding of the custom artist title wordmark.
    /// </summary>
    public string SafeCustomTitleLogoUri => HasCustomTitleLogo ? CustomTitleLogoUri : TrackItem.FallbackTransparentCoverUri;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the custom artist title wordmark image.
    /// </summary>
    public double CustomTitleLogoOpacity => HasCustomTitleLogo ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the plain text artist title (hidden when custom wordmark image is active).
    /// </summary>
    public double TextTitleOpacity => HasCustomTitleLogo ? 0.0 : 1.0;

    /// <summary>
    /// Gets the raw preferred hero image URI (banner if available, otherwise avatar).
    /// </summary>
    public string HeroImageUri => HasBanner ? BannerImageUri : AvatarUri;

    /// <summary>
    /// Gets the formatted subscriber pill text (e.g., "Subscribers 3.89M").
    /// </summary>
    public string DisplaySubscribersPillText => !string.IsNullOrWhiteSpace(SubscribersCountText)
        ? (SubscribersCountText.Contains("Subscri", StringComparison.OrdinalIgnoreCase)
            ? SubscribersCountText
            : $"Subscribers {SubscribersCountText}")
        : "Subscribers";

    /// <summary>
    /// Gets the monthly listeners or artist metadata subtitle text.
    /// </summary>
    public string DisplayMonthlyListenersText => !string.IsNullOrWhiteSpace(MonthlyListenersText)
        ? MonthlyListenersText
        : MetadataSubtitle;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the artist photo/banner layer.
    /// </summary>
    public double AvatarOpacity => (HasBanner || HasAvatar) ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the fallback minimalist artist icon layer.
    /// </summary>
    public double DefaultIconOpacity => (HasBanner || HasAvatar) ? 0.0 : 1.0;
}

/// <summary>
/// Represents the rich Album Detail view model with metadata, full playable tracklist,
/// Apple Music Canvas animated cover support, and recommended/other albums at the bottom.
/// </summary>
public sealed class AlbumDetailProfile
{
    /// <summary>
    /// Gets the album identifier.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the album title.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the primary album artist name.
    /// </summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>
    /// Gets the album cover artwork URI.
    /// </summary>
    public string CoverArtUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the album release year.
    /// </summary>
    public int ReleaseYear { get; init; }

    /// <summary>
    /// Gets the concise metadata line displayed below the artist name (e.g., "2026 • 18 songs • 52 min").
    /// </summary>
    public string MetadataSubtitle { get; init; } = string.Empty;

    /// <summary>
    /// Gets the editorial album description obtained directly from YouTube Music.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets the formatted album information and metadata summary.
    /// </summary>
    public string AlbumInformation { get; init; } = string.Empty;

    /// <summary>
    /// Gets the ordered list of tracks belonging to this album.
    /// </summary>
    public IReadOnlyList<TrackItem> Tracks { get; init; } = [];

    /// <summary>
    /// Gets alternate versions of this album (Deluxe, Remastered, Clean/Explicit, Standard) from YouTube Music.
    /// </summary>
    public IReadOnlyList<AlbumItem> OtherVersions { get; init; } = [];

    /// <summary>
    /// Gets other albums and EPs by the same artist ("More by [Artist]") from YouTube Music.
    /// </summary>
    public IReadOnlyList<AlbumItem> OtherAlbums { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether an editorial album description is available.
    /// </summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>
    /// Gets the formatted metadata line ("2026 • 18 songs • 52 min").
    /// </summary>
    public string DisplayMetadataLine
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(MetadataSubtitle))
            {
                return MetadataSubtitle;
            }

            int totalMinutes = Math.Max(1, (int)Math.Round(Tracks.Sum(t => t.Duration.TotalMinutes)));
            string songWord = Tracks.Count == 1 ? "1 song" : $"{Tracks.Count} songs";
            return ReleaseYear > 1900
                ? $"{ReleaseYear} • {songWord} • {totalMinutes} min"
                : $"{songWord} • {totalMinutes} min";
        }
    }

    /// <summary>
    /// Gets a value indicating whether a real cover art image is available.
    /// </summary>
    public bool HasCoverArt =>
        !string.IsNullOrWhiteSpace(CoverArtUri) &&
        !CoverArtUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !CoverArtUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a safe URI for XAML ImageBrush/Image bindings.
    /// </summary>
    public string SafeCoverArtUri => HasCoverArt ? TrackItem.NormalizeCoverUri(CoverArtUri) : TrackItem.FallbackTransparentCoverUri;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the cover art layer.
    /// </summary>
    public double CoverArtOpacity => HasCoverArt ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the fallback minimalist cover icon layer.
    /// </summary>
    public double DefaultIconOpacity => HasCoverArt ? 0.0 : 1.0;
}

/// <summary>
/// Categorizes the editorial card treatments inside the Home "Top picks for you" carousel.
/// </summary>
public enum TopPickKind
{
    PersonalStation = 0,
    NewRelease = 1,
    FeaturedPlaylist = 2,
    ArtistSpotlight = 3
}

/// <summary>
/// Represents a rich editorial suggestion card inside the Home "Top picks for you" carousel.
/// </summary>
public sealed record TopPickItem(
    string Id,
    string Eyebrow,
    string Title,
    string Subtitle,
    string ArtworkUri,
    IReadOnlyList<string> CollageArtworks,
    TopPickKind Kind,
    string? BrowseId = null,
    string? PlaylistId = null,
    string? VideoId = null,
    string? ArtistName = null,
    string? AlbumTitle = null,
    TrackItem? SeedTrack = null,
    string AmbientHexColor = "#FF182232")
{
    public bool HasArtwork =>
        !string.IsNullOrWhiteSpace(ArtworkUri) &&
        !ArtworkUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !ArtworkUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    public string SafeArtworkUri => HasArtwork
        ? TrackItem.NormalizeCoverUri(ArtworkUri)
        : TrackItem.FallbackTransparentCoverUri;

    public bool IsPersonalStation => Kind == TopPickKind.PersonalStation;
    public bool IsNewRelease => Kind == TopPickKind.NewRelease;
    public bool IsFeaturedPlaylist => Kind == TopPickKind.FeaturedPlaylist;
    public bool IsArtistSpotlight => Kind == TopPickKind.ArtistSpotlight;

    public bool IsCollage => Kind == TopPickKind.FeaturedPlaylist && CollageArtworks.Count >= 4;
    public bool IsSingleCover => !IsCollage && !IsArtistSpotlight && HasArtwork;

    public double SingleCoverOpacity => IsSingleCover ? 1.0 : 0.0;
    public double CollageOpacity => IsCollage ? 1.0 : 0.0;
    public double ArtistSpotlightOpacity => IsArtistSpotlight ? 1.0 : 0.0;
    public double PersonalStationRingOpacity => IsPersonalStation ? 1.0 : 0.0;
    public double BadgeOpacity => Kind == TopPickKind.NewRelease ? 0.0 : 1.0;

    public string CollageArt0 => GetCollageUri(0);
    public string CollageArt1 => GetCollageUri(1);
    public string CollageArt2 => GetCollageUri(2);
    public string CollageArt3 => GetCollageUri(3);

    private string GetCollageUri(int index)
    {
        if (CollageArtworks.Count == 0)
        {
            return TrackItem.FallbackTransparentCoverUri;
        }

        string raw = CollageArtworks[index % CollageArtworks.Count];
        return string.IsNullOrWhiteSpace(raw)
            ? TrackItem.FallbackTransparentCoverUri
            : TrackItem.NormalizeCoverUri(raw);
    }

    public string BadgeText => Kind switch
    {
        TopPickKind.PersonalStation => "Mix",
        TopPickKind.NewRelease => "Music",
        TopPickKind.FeaturedPlaylist => "Playlist",
        TopPickKind.ArtistSpotlight => "Spotlight",
        _ => "Pick"
    };

    public string BadgeGlyph => Kind switch
    {
        TopPickKind.PersonalStation => "\uE90B",
        TopPickKind.NewRelease => "\uEC4F",
        TopPickKind.FeaturedPlaylist => "\uE8FD",
        TopPickKind.ArtistSpotlight => "\uE77B",
        _ => "\uE734"
    };
}

/// <summary>
/// Represents a "More like: [Album]" section containing up to 15 similar album recommendations.
/// </summary>
public sealed record MoreLikeAlbumSectionData(
    string SeedAlbumTitle,
    string SeedArtist,
    string SeedCoverUri,
    AlbumItem? SeedAlbum,
    IReadOnlyList<AlbumItem> Albums)
{
    public string HeaderTitle => $"More like: {SeedAlbumTitle}";

    public string SafeSeedCoverUri => string.IsNullOrWhiteSpace(SeedCoverUri)
        ? TrackItem.FallbackTransparentCoverUri
        : TrackItem.NormalizeCoverUri(SeedCoverUri);
}

/// <summary>
/// Represents a recommended playlist in the "Playlists made for you" section.
/// </summary>
public sealed record RecommendedPlaylistItem(
    string Id,
    string Title,
    string Subtitle,
    string ArtworkUri,
    string PlaylistId,
    string? BrowseId = null,
    string? Author = null,
    string? TrackCountText = null)
{
    public bool HasArtwork =>
        !string.IsNullOrWhiteSpace(ArtworkUri) &&
        !ArtworkUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !ArtworkUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    public string SafeArtworkUri => HasArtwork
        ? TrackItem.NormalizeCoverUri(ArtworkUri)
        : TrackItem.FallbackTransparentCoverUri;

    public double ArtworkOpacity => HasArtwork ? 1.0 : 0.0;
    public double DefaultIconOpacity => HasArtwork ? 0.0 : 1.0;
}

/// <summary>
/// Represents a recommended radio station or track-seeded mix in the "Radio Stations" section.
/// </summary>
public sealed record RadioStationItem(
    string Id,
    string Title,
    string Subtitle,
    string ArtworkUri,
    string PlaylistId,
    string? SeedVideoId = null,
    string? Params = null)
{
    public bool HasArtwork =>
        !string.IsNullOrWhiteSpace(ArtworkUri) &&
        !ArtworkUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !ArtworkUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    public string SafeArtworkUri => HasArtwork
        ? TrackItem.NormalizeCoverUri(ArtworkUri)
        : TrackItem.FallbackTransparentCoverUri;

    public double ArtworkOpacity => HasArtwork ? 1.0 : 0.0;
    public double DefaultIconOpacity => HasArtwork ? 0.0 : 1.0;
}

/// <summary>
/// Represents a recent music release (album or single) in the "New Releases" section.
/// </summary>
public sealed record NewReleaseItem(
    string Id,
    string Title,
    string Artist,
    string Subtitle,
    string ArtworkUri,
    bool IsAlbum,
    string? BrowseId = null,
    string? VideoId = null,
    string? Year = null)
{
    public bool HasArtwork =>
        !string.IsNullOrWhiteSpace(ArtworkUri) &&
        !ArtworkUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !ArtworkUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    public string SafeArtworkUri => HasArtwork
        ? TrackItem.NormalizeCoverUri(ArtworkUri)
        : TrackItem.FallbackTransparentCoverUri;

    public double ArtworkOpacity => HasArtwork ? 1.0 : 0.0;
    public double DefaultIconOpacity => HasArtwork ? 0.0 : 1.0;
    public string TypeBadgeText => IsAlbum ? "Album" : "Single";
}

/// <summary>
/// Represents raw parsed shelves returned by InnerTube's <c>FEmusic_home</c> browse endpoint.
/// </summary>
public sealed record YtmHomeFeedResult(
    IReadOnlyList<TrackItem> PersonalizedTracks,
    IReadOnlyList<AlbumItem> PersonalizedAlbums,
    IReadOnlyList<RecommendedPlaylistItem> PlaylistsMadeForYou,
    IReadOnlyList<RadioStationItem> RadioStations,
    IReadOnlyList<NewReleaseItem> NewReleases,
    IReadOnlyList<TopPickItem> FeaturedTopPicks,
    bool IsPersonalized);

/// <summary>
/// Represents a deduplicated recently played album (or standalone single release)
/// shown in the Home "Recently Played" irregular mosaic.
/// </summary>
public sealed class RecentAlbumItem
{
    /// <summary>
    /// Gets the deterministic identifier for this recent album entry.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the canonical normalized album deduplication key (normalized album + primary artist,
    /// or normalized title + primary artist for standalone singles).
    /// </summary>
    public string AlbumKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the display title of the album (or track title for standalone singles).
    /// </summary>
    public string AlbumTitle { get; init; } = string.Empty;

    /// <summary>
    /// Gets the primary artist name for display.
    /// </summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>
    /// Gets the album cover artwork URI.
    /// </summary>
    public string CoverArtUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the optional YouTube Music InnerTube browseId (e.g., <c>MPREb_...</c>).
    /// </summary>
    public string BrowseId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the release year of the album when known.
    /// </summary>
    public int ReleaseYear { get; init; }

    /// <summary>
    /// Gets the media source type (Local or Online).
    /// </summary>
    public MediaSourceType SourceType { get; init; } = MediaSourceType.Online;

    /// <summary>
    /// Gets a value indicating whether this entry represents a standalone single without a distinct album title.
    /// </summary>
    public bool IsSingleRelease { get; init; }

    /// <summary>
    /// Gets the UTC timestamp of the most recent playback event for this album.
    /// </summary>
    public DateTimeOffset LastPlayedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the most recently played track belonging to this album.
    /// </summary>
    public TrackItem? SeedTrack { get; init; }

    /// <summary>
    /// Gets a value indicating whether this album entry has a real cover art image.
    /// </summary>
    public bool HasCoverArt =>
        !string.IsNullOrWhiteSpace(CoverArtUri) &&
        !CoverArtUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !CoverArtUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a safe URI for XAML Image / ImageBrush bindings.
    /// </summary>
    public string SafeCoverArtUri => HasCoverArt
        ? TrackItem.NormalizeCoverUri(CoverArtUri)
        : TrackItem.FallbackTransparentCoverUri;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the cover art layer.
    /// </summary>
    public double CoverArtOpacity => HasCoverArt ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the fallback minimalist icon layer.
    /// </summary>
    public double DefaultCoverIconOpacity => HasCoverArt ? 0.0 : 1.0;

    /// <summary>
    /// Converts this recent album entry into an <see cref="AlbumItem"/> suitable for opening Album Detail.
    /// </summary>
    public AlbumItem ToAlbumItem() => new()
    {
        Id = !string.IsNullOrWhiteSpace(BrowseId) ? $"ytm_alb_{BrowseId}" : Id,
        Title = AlbumTitle,
        Artist = Artist,
        ReleaseYear = ReleaseYear,
        CoverArtUri = CoverArtUri,
        BrowseId = BrowseId,
        SourceType = SourceType,
        Subtitle = IsSingleRelease
            ? (ReleaseYear > 1900 ? $"Single • {ReleaseYear}" : "Single")
            : (ReleaseYear > 1900 ? $"Album • {ReleaseYear}" : "Album")
    };

    /// <summary>
    /// Determines whether another <see cref="RecentAlbumItem"/> represents the same album or standalone single.
    /// </summary>
    public bool MatchesAlbum(RecentAlbumItem? other)
    {
        if (other is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(AlbumKey) &&
            !string.IsNullOrWhiteSpace(other.AlbumKey) &&
            string.Equals(AlbumKey, other.AlbumKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(BrowseId) &&
            !string.IsNullOrWhiteSpace(other.BrowseId) &&
            string.Equals(BrowseId, other.BrowseId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (SeedTrack is not null &&
            other.SeedTrack is not null &&
            !string.IsNullOrWhiteSpace(SeedTrack.Id) &&
            string.Equals(SeedTrack.Id, other.SeedTrack.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string titleA = NormalizeIdentityToken(AlbumTitle);
        string titleB = NormalizeIdentityToken(other.AlbumTitle);
        if (string.IsNullOrWhiteSpace(titleA) || !string.Equals(titleA, titleB, StringComparison.Ordinal))
        {
            return false;
        }

        string primaryArtistA = ExtractPrimaryArtist(Artist);
        string primaryArtistB = ExtractPrimaryArtist(other.Artist);
        if (IsGenericArtistName(primaryArtistA) || IsGenericArtistName(primaryArtistB))
        {
            return true;
        }

        return string.Equals(
            NormalizeIdentityToken(primaryArtistA),
            NormalizeIdentityToken(primaryArtistB),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Merges a newly played occurrence of this album with an existing entry, moving recency forward
    /// while preserving the best available artwork, browseId, release year, and artist metadata.
    /// </summary>
    public RecentAlbumItem MergeWithNewer(RecentAlbumItem newer)
    {
        string bestCover = newer.HasCoverArt
            ? newer.CoverArtUri
            : (HasCoverArt ? CoverArtUri : newer.CoverArtUri);
        string bestBrowseId = !string.IsNullOrWhiteSpace(newer.BrowseId) ? newer.BrowseId : BrowseId;
        int bestYear = newer.ReleaseYear > 1900 ? newer.ReleaseYear : ReleaseYear;
        string bestArtist = !IsGenericArtistName(newer.Artist)
            ? newer.Artist
            : (!IsGenericArtistName(Artist) ? Artist : newer.Artist);
        string bestTitle = !string.IsNullOrWhiteSpace(newer.AlbumTitle) ? newer.AlbumTitle : AlbumTitle;
        string bestKey = !string.IsNullOrWhiteSpace(newer.AlbumKey) ? newer.AlbumKey : AlbumKey;

        return new RecentAlbumItem
        {
            Id = !string.IsNullOrWhiteSpace(newer.Id) ? newer.Id : Id,
            AlbumKey = bestKey,
            AlbumTitle = bestTitle,
            Artist = bestArtist,
            CoverArtUri = bestCover,
            BrowseId = bestBrowseId,
            ReleaseYear = bestYear,
            SourceType = newer.SourceType,
            IsSingleRelease = newer.IsSingleRelease && IsSingleRelease,
            LastPlayedAt = newer.LastPlayedAt >= LastPlayedAt ? newer.LastPlayedAt : DateTimeOffset.UtcNow,
            SeedTrack = newer.SeedTrack ?? SeedTrack
        };
    }

    /// <summary>
    /// Creates a canonical <see cref="RecentAlbumItem"/> from a played <see cref="TrackItem"/>.
    /// Standalone singles with blank or generic album labels use the track title + primary artist as their release identity.
    /// </summary>
    public static RecentAlbumItem FromTrack(TrackItem track, string? browseId = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        bool isGenericAlbum = IsGenericAlbumTitle(track.Album);
        string resolvedTitle = isGenericAlbum
            ? track.Title.Trim()
            : track.Album.Trim();
        if (string.IsNullOrWhiteSpace(resolvedTitle))
        {
            resolvedTitle = track.Title.Trim();
        }

        string primaryArtist = ExtractPrimaryArtist(track.Artist);
        string displayArtist = !string.IsNullOrWhiteSpace(primaryArtist)
            ? primaryArtist
            : (!string.IsNullOrWhiteSpace(track.Artist) ? track.Artist.Trim() : "Unknown Artist");

        string normTitle = NormalizeIdentityToken(resolvedTitle);
        string normArtist = IsGenericArtistName(primaryArtist) ? "*" : NormalizeIdentityToken(primaryArtist);
        string albumKey = $"{(isGenericAlbum ? "sng" : "alb")}::{normTitle}::{normArtist}";

        string cleanBrowseId = browseId?.Trim() ?? string.Empty;
        string id = !string.IsNullOrWhiteSpace(cleanBrowseId)
            ? $"recent_alb_{cleanBrowseId}"
            : $"recent_alb_{ComputeStableKeyHash(albumKey)}";

        return new RecentAlbumItem
        {
            Id = id,
            AlbumKey = albumKey,
            AlbumTitle = resolvedTitle,
            Artist = displayArtist,
            CoverArtUri = track.HasCoverArt ? track.CoverArtUri : string.Empty,
            BrowseId = cleanBrowseId,
            ReleaseYear = track.ReleaseYear > 1900 ? track.ReleaseYear : 0,
            SourceType = track.SourceType,
            IsSingleRelease = isGenericAlbum,
            LastPlayedAt = DateTimeOffset.UtcNow,
            SeedTrack = track
        };
    }

    /// <summary>
    /// Determines whether an album title is blank or a generic non-album placeholder label.
    /// </summary>
    public static bool IsGenericAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
        {
            return true;
        }

        string trimmed = albumTitle.Trim();
        return trimmed.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Single", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("YouTube Music", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("YouTube Music For You", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Recommended Mix", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Radio Mix", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Streaming", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Local Album", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("views", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("plays", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("reproducciones", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("visualizaciones", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether an artist name is blank or a generic placeholder label.
    /// </summary>
    public static bool IsGenericArtistName(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return true;
        }

        string trimmed = artist.Trim();
        return trimmed.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Featured Artist", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("YouTube Artist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the primary performing artist from a potentially multi-artist string (e.g., "MARINA, Absolutely" -> "MARINA").
    /// </summary>
    public static string ExtractPrimaryArtist(string? artistField)
    {
        if (string.IsNullOrWhiteSpace(artistField))
        {
            return string.Empty;
        }

        var split = artistField.Split([',', ';', '/'], 2, StringSplitOptions.TrimEntries);
        return string.IsNullOrWhiteSpace(split[0]) ? artistField.Trim() : split[0];
    }

    /// <summary>
    /// Normalizes a title or artist string for case/whitespace-insensitive album identity matching.
    /// </summary>
    public static string NormalizeIdentityToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string trimmed = text.Trim();
        var sb = new StringBuilder(trimmed.Length);
        bool prevSpace = false;
        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
                prevSpace = false;
            }
        }

        return sb.ToString();
    }

    private static string ComputeStableKeyHash(string input)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in input)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }

            return hash.ToString("x16");
        }
    }
}

/// <summary>
/// Encapsulates the personalized Home discovery screen feed
/// based on the user's recently played music and recommendations.
/// </summary>
public sealed record HomeDiscoveryFeed(
    string PersonalizedSubtitle,
    IReadOnlyList<TrackItem> RecentlyPlayedTracks,
    IReadOnlyList<TrackItem> RecommendedTracks,
    IReadOnlyList<string> GenreTags,
    string SelectedGenreTag,
    IReadOnlyList<TrackItem> GenreTracks,
    IReadOnlyList<ArtistItem> RelatedArtists,
    IReadOnlyList<AlbumItem> RecommendedAlbums,
    IReadOnlyList<TopPickItem>? TopPicks = null,
    IReadOnlyList<MoreLikeAlbumSectionData>? MoreLikeAlbumSections = null,
    IReadOnlyList<RecommendedPlaylistItem>? PlaylistsMadeForYou = null,
    IReadOnlyList<RadioStationItem>? RadioStations = null,
    IReadOnlyList<NewReleaseItem>? NewReleases = null,
    bool IsPersonalized = false,
    IReadOnlyList<RecentAlbumItem>? RecentlyPlayedAlbums = null,
    IReadOnlyList<UserPlaylist>? SmartSuggestedPlaylists = null);

/// <summary>
/// Represents an alphabetically (or chronologically) grouped bucket of tracks inside the Library directory tabs.
/// </summary>
public sealed record LibraryTrackGroup(
    string Letter,
    IReadOnlyList<TrackItem> Items,
    int StartIndex = 0)
{
    /// <summary>
    /// Gets the number of tracks inside this group.
    /// </summary>
    public int Count => Items.Count;

    /// <summary>
    /// Gets the formatted item count text displayed on the right side of the section divider.
    /// </summary>
    public string CountText => Items.Count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Represents an alphabetically grouped bucket of albums inside the Library Albums tab.
/// </summary>
public sealed record LibraryAlbumGroup(
    string Letter,
    IReadOnlyList<AlbumItem> Items)
{
    /// <summary>
    /// Gets the number of albums inside this group.
    /// </summary>
    public int Count => Items.Count;

    /// <summary>
    /// Gets the formatted item count text displayed on the right side of the section divider.
    /// </summary>
    public string CountText => Items.Count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Represents an alphabetically grouped bucket of artists inside the Library Artists tab.
/// </summary>
public sealed record LibraryArtistGroup(
    string Letter,
    IReadOnlyList<ArtistItem> Items)
{
    /// <summary>
    /// Gets the number of artists inside this group.
    /// </summary>
    public int Count => Items.Count;

    /// <summary>
    /// Gets the formatted item count text displayed on the right side of the section divider.
    /// </summary>
    public string CountText => Items.Count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Shared A–Z + '#' letter normalization and alphabetical/chronological grouping helper matching Android's Library directories.
/// </summary>
public static class LibraryAlphabetHelper
{
    /// <summary>
    /// The canonical 27 fast-scroll index rail buckets ordered A–Z followed by '#'.
    /// </summary>
    public static IReadOnlyList<string> AllLetters { get; } =
    [
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "#"
    ];

    /// <summary>
    /// Normalizes a display string for alphabetical bucketing and sorting using Unicode NFD decomposition,
    /// stripping combining diacritic marks (e.g., 'É' -> 'E', 'Ñ' -> 'N'), and uppercasing with invariant culture.
    /// </summary>
    public static string NormalizeSortKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string decomposed = raw.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolves the canonical bucket letter ('A'..'Z', or '#' for digits, symbols, or blank strings).
    /// </summary>
    public static string GetBucketLetter(string? raw)
    {
        string sortKey = NormalizeSortKey(raw);
        if (sortKey.Length > 0 && sortKey[0] is >= 'A' and <= 'Z')
        {
            return sortKey[0].ToString();
        }

        return "#";
    }

    /// <summary>
    /// Returns the sort ordinal (0..25 for 'A'..'Z', 26 for '#') for a bucket letter.
    /// </summary>
    public static int GetBucketOrder(string? letter)
    {
        if (!string.IsNullOrEmpty(letter) && letter.Length == 1 && letter[0] is >= 'A' and <= 'Z')
        {
            return letter[0] - 'A';
        }

        return 26;
    }

    /// <summary>
    /// Groups tracks into A–Z + '#' alphabetical sections sorted by normalized title,
    /// recording each group's flattened <see cref="LibraryTrackGroup.StartIndex"/> for continuous queue playback.
    /// </summary>
    public static IReadOnlyList<LibraryTrackGroup> GroupTracks(IEnumerable<TrackItem> tracks)
    {
        if (tracks is null)
        {
            return [];
        }

        var grouped = tracks
            .GroupBy(t => GetBucketLetter(t.Title), StringComparer.Ordinal)
            .OrderBy(g => GetBucketOrder(g.Key))
            .ToList();

        var result = new List<LibraryTrackGroup>(grouped.Count);
        int runningStart = 0;
        foreach (var g in grouped)
        {
            var orderedItems = g
                .OrderBy(t => NormalizeSortKey(t.Title), StringComparer.Ordinal)
                .ThenBy(t => t.Title.ToLowerInvariant(), StringComparer.Ordinal)
                .ToList();

            result.Add(new LibraryTrackGroup(g.Key, orderedItems, runningStart));
            runningStart += orderedItems.Count;
        }

        return result;
    }

    /// <summary>
    /// Groups albums into A–Z + '#' alphabetical sections sorted by normalized title.
    /// </summary>
    public static IReadOnlyList<LibraryAlbumGroup> GroupAlbums(IEnumerable<AlbumItem> albums)
    {
        if (albums is null)
        {
            return [];
        }

        return albums
            .GroupBy(a => GetBucketLetter(a.Title), StringComparer.Ordinal)
            .OrderBy(g => GetBucketOrder(g.Key))
            .Select(g => new LibraryAlbumGroup(
                g.Key,
                g.OrderBy(a => NormalizeSortKey(a.Title), StringComparer.Ordinal)
                 .ThenBy(a => a.Title.ToLowerInvariant(), StringComparer.Ordinal)
                 .ToList()))
            .ToList();
    }

    /// <summary>
    /// Groups artists into A–Z + '#' alphabetical sections sorted by normalized artist name.
    /// </summary>
    public static IReadOnlyList<LibraryArtistGroup> GroupArtists(IEnumerable<ArtistItem> artists)
    {
        if (artists is null)
        {
            return [];
        }

        return artists
            .GroupBy(a => GetBucketLetter(a.Name), StringComparer.Ordinal)
            .OrderBy(g => GetBucketOrder(g.Key))
            .Select(g => new LibraryArtistGroup(
                g.Key,
                g.OrderBy(a => NormalizeSortKey(a.Name), StringComparer.Ordinal)
                 .ThenBy(a => a.Name.ToLowerInvariant(), StringComparer.Ordinal)
                 .ToList()))
            .ToList();
    }

    /// <summary>
    /// Groups recently added tracks into chronological buckets matching Android's Recently Added timeline
    /// ("Last week", "Last month", "Last 3 months", "Last 6 months", "This year", descending calendar years, "Undated").
    /// </summary>
    public static IReadOnlyList<LibraryTrackGroup> GroupRecentlyAddedByPeriod(
        IEnumerable<TrackItem> tracks,
        DateTimeOffset? nowUtc = null)
    {
        if (tracks is null)
        {
            return [];
        }

        var referenceNow = nowUtc ?? DateTimeOffset.UtcNow;
        long nowMs = referenceNow.ToUnixTimeMilliseconds();
        long weekMs = 7L * 24 * 60 * 60 * 1000;
        long monthMs = 30L * 24 * 60 * 60 * 1000;
        long threeMonthsMs = 90L * 24 * 60 * 60 * 1000;
        long sixMonthsMs = 180L * 24 * 60 * 60 * 1000;

        var ordered = tracks
            .OrderByDescending(t => t.DateAddedMs)
            .ThenBy(t => t.Title.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        var buckets = new Dictionary<string, (int OrderKey, List<TrackItem> Items)>(StringComparer.Ordinal);

        foreach (var track in ordered)
        {
            string label;
            int orderKey;

            if (track.DateAddedMs <= 0)
            {
                label = "Undated";
                orderKey = int.MaxValue;
            }
            else
            {
                long ageMs = Math.Max(0, nowMs - track.DateAddedMs);
                var addedUtc = DateTimeOffset.FromUnixTimeMilliseconds(track.DateAddedMs);

                if (ageMs <= weekMs)
                {
                    label = "Last week";
                    orderKey = 0;
                }
                else if (ageMs <= monthMs)
                {
                    label = "Last month";
                    orderKey = 1;
                }
                else if (ageMs <= threeMonthsMs)
                {
                    label = "Last 3 months";
                    orderKey = 2;
                }
                else if (ageMs <= sixMonthsMs)
                {
                    label = "Last 6 months";
                    orderKey = 3;
                }
                else if (addedUtc.Year == referenceNow.Year)
                {
                    label = "This year";
                    orderKey = 4;
                }
                else
                {
                    label = addedUtc.Year.ToString(CultureInfo.InvariantCulture);
                    orderKey = 10000 - addedUtc.Year;
                }
            }

            if (!buckets.TryGetValue(label, out var bucket))
            {
                bucket = (orderKey, []);
                buckets[label] = bucket;
            }

            bucket.Items.Add(track);
        }

        var result = new List<LibraryTrackGroup>(buckets.Count);
        int runningStart = 0;
        foreach (var kvp in buckets.OrderBy(k => k.Value.OrderKey))
        {
            result.Add(new LibraryTrackGroup(kvp.Key, kvp.Value.Items, runningStart));
            runningStart += kvp.Value.Items.Count;
        }

        return result;
    }
}

/// <summary>
/// Represents a single YouTube Music Mood or Genre tile inside the Search explorer
/// (e.g., "Energize", "Chill", "Pop", "Rock") with its InnerTube browse parameters and accent stripe color.
/// </summary>
public sealed record MoodGenreCategoryItem(
    string Id,
    string Title,
    string BrowseId,
    string? Params = null,
    string StripeColorHex = "#FF6366F1",
    uint StripeColorArgb = 0xFF6366F1u)
{
    /// <summary>
    /// Gets the accent hex color string (alias for <see cref="StripeColorHex"/>).
    /// </summary>
    public string AccentColorHex => StripeColorHex;

    /// <summary>
    /// Formats an ARGB 32-bit unsigned integer into a canonical <c>#AARRGGBB</c> hex color string.
    /// </summary>
    public static string FormatArgbHex(uint argb) => $"#{argb:X8}";

    /// <summary>
    /// Normalizes a raw InnerTube <c>solid.leftStripeColor</c> signed/unsigned integer (e.g. <c>-11776769</c> -&gt; <c>0xFF4C4EFF</c>)
    /// into an opaque 32-bit ARGB color and <c>#AARRGGBB</c> hex string.
    /// </summary>
    public static (uint Argb, string Hex) NormalizeStripeColor(long rawColor)
    {
        if (rawColor == 0L)
        {
            return (0xFF6366F1u, "#FF6366F1");
        }

        uint unsigned32 = unchecked((uint)rawColor);
        if ((unsigned32 >> 24) == 0u)
        {
            unsigned32 |= 0xFF000000u;
        }

        return (unsigned32, FormatArgbHex(unsigned32));
    }
}

/// <summary>
/// Represents a named section in the YouTube Music Moods &amp; Genres explorer
/// (e.g., "For you", "Moods &amp; moments", "Genres") with its category items and badge count.
/// </summary>
public sealed record MoodGenreSection(
    string Title,
    IReadOnlyList<MoodGenreCategoryItem> Items)
{
    /// <summary>
    /// Gets the number of categories in this section.
    /// </summary>
    public int Count => Items.Count;

    /// <summary>
    /// Gets the formatted item count text displayed inside the section header pill badge.
    /// </summary>
    public string CountText => Items.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets a value indicating whether this section contains any categories.
    /// </summary>
    public bool HasItems => Items.Count > 0;
}

/// <summary>
/// Encapsulates the full YouTube Music Moods &amp; Genres feed returned by InnerTube <c>FEmusic_moods_and_genres</c>
/// (or a clean curated genre fallback when offline).
/// </summary>
public sealed record MoodsAndGenresFeed(
    IReadOnlyList<MoodGenreSection> Sections,
    bool IsFromYouTubeMusic = false)
{
    /// <summary>
    /// Gets a value indicating whether at least one section with items is present.
    /// </summary>
    public bool HasSections => Sections.Count > 0;

    /// <summary>
    /// Gets the total number of categories across all sections.
    /// </summary>
    public int TotalCategoriesCount => Sections.Sum(s => s.Count);
}

/// <summary>
/// Encapsulates curated playlists, featured albums, and songs returned when browsing
/// a selected YouTube Music Mood or Genre category.
/// </summary>
public sealed record MoodGenreDetailResult(
    string CategoryTitle,
    string BrowseId,
    string? Params,
    IReadOnlyList<RecommendedPlaylistItem> Playlists,
    IReadOnlyList<AlbumItem> Albums,
    IReadOnlyList<TrackItem> Songs,
    string StripeColorHex = "#FF6366F1")
{
    /// <summary>
    /// Gets a value indicating whether curated playlists were returned for this category.
    /// </summary>
    public bool HasPlaylists => Playlists.Count > 0;

    /// <summary>
    /// Gets a value indicating whether featured albums were returned for this category.
    /// </summary>
    public bool HasAlbums => Albums.Count > 0;

    /// <summary>
    /// Gets a value indicating whether playable songs were returned for this category.
    /// </summary>
    public bool HasSongs => Songs.Count > 0;

    /// <summary>
    /// Gets a value indicating whether any playlists, albums, or songs were found.
    /// </summary>
    public bool HasAnyContent => HasPlaylists || HasAlbums || HasSongs;
}

/// <summary>
/// Categorizes the featured "Top Result" card in dedicated Search results.
/// </summary>
public enum SearchTopResultKind
{
    Song = 0,
    Artist = 1,
    Album = 2,
    Playlist = 3
}

/// <summary>
/// Represents the single highlighted Top Result card shown at the top of categorized Search results.
/// </summary>
public sealed record SearchTopResultItem(
    SearchTopResultKind Kind,
    string Title,
    string Subtitle,
    string ArtworkUri,
    TrackItem? Track = null,
    ArtistItem? Artist = null,
    AlbumItem? Album = null,
    RecommendedPlaylistItem? Playlist = null)
{
    public bool IsSong => Kind == SearchTopResultKind.Song;
    public bool IsArtist => Kind == SearchTopResultKind.Artist;
    public bool IsAlbum => Kind == SearchTopResultKind.Album;
    public bool IsPlaylist => Kind == SearchTopResultKind.Playlist;

    public string KindBadgeText => Kind switch
    {
        SearchTopResultKind.Song => "Song",
        SearchTopResultKind.Artist => "Artist",
        SearchTopResultKind.Album => "Album",
        SearchTopResultKind.Playlist => "Playlist",
        _ => "Top Result"
    };

    public bool HasArtwork =>
        !string.IsNullOrWhiteSpace(ArtworkUri) &&
        !ArtworkUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !ArtworkUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    public string SafeArtworkUri => HasArtwork
        ? TrackItem.NormalizeCoverUri(ArtworkUri)
        : TrackItem.FallbackTransparentCoverUri;

    public double ArtworkOpacity => HasArtwork ? 1.0 : 0.0;
    public double DefaultIconOpacity => HasArtwork ? 0.0 : 1.0;
}

/// <summary>
/// Encapsulates rich categorized YouTube Music search results (Top Result, Songs, Albums, Artists, Playlists)
/// with WIC-warmed local artwork URIs ready for WinUI <c>&lt;Image&gt;</c> rendering.
/// </summary>
public sealed record YouTubeMusicSearchResult(
    string Query,
    SearchTopResultItem? TopResult,
    IReadOnlyList<TrackItem> Songs,
    IReadOnlyList<AlbumItem> Albums,
    IReadOnlyList<ArtistItem> Artists,
    IReadOnlyList<RecommendedPlaylistItem> Playlists)
{
    public bool HasTopResult => TopResult is not null;
    public bool HasSongs => Songs.Count > 0;
    public bool HasAlbums => Albums.Count > 0;
    public bool HasArtists => Artists.Count > 0;
    public bool HasPlaylists => Playlists.Count > 0;
    public bool HasAnyResults => HasTopResult || HasSongs || HasAlbums || HasArtists || HasPlaylists;

    public static YouTubeMusicSearchResult Empty(string query = "") =>
        new(query, null, [], [], [], []);
}

/// <summary>
/// Persisted playback history record for tracking user listening habits, play frequency,
/// timestamps (last 7 days vs all-time), artist affinity, and musical genres to power Smart Playlists.
/// </summary>
public sealed class PlaybackHistoryEntry
{
    public string TrackId { get; set; } = string.Empty;
    public TrackItem Track { get; set; } = new();
    public long LastPlayedTimestampMs { get; set; }
    public List<long> PlayTimestampsMs { get; set; } = [];
    public int TotalPlayCount { get; set; }
    public string Artist { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
}

/// <summary>
/// Represents an aligned musical segment between a studio audio track (ATV) and its official music video (OMV),
/// matching YouTube Music's <c>segmentMap</c> (e.g. skipping an intro skit at 0:00-0:11 in the video so 0:12 song = 0:23 video).
/// </summary>
public sealed record MusicVideoTimeSegment(
    long SongStartMs,
    long VideoStartMs,
    long DurationMs);

/// <summary>
/// Metadata identifying a track's authentic Official Music Video (<c>MUSIC_VIDEO_TYPE_OMV</c>) counterpart
/// and any timeline alignment segments between Song and Video.
/// </summary>
public sealed record OfficialMusicVideoMetadata(
    string SongVideoId,
    string OfficialVideoId,
    IReadOnlyList<MusicVideoTimeSegment> Segments)
{
    public TimeSpan MapSongToVideoPosition(TimeSpan songPosition, TimeSpan? videoDuration = null)
    {
        long songMs = Math.Max(0L, (long)Math.Round(songPosition.TotalMilliseconds));
        long mappedMs = songMs;

        if (Segments.Count > 0)
        {
            MusicVideoTimeSegment? active = null;
            for (int i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                if (songMs >= seg.SongStartMs && songMs <= seg.SongStartMs + seg.DurationMs)
                {
                    active = seg;
                    break;
                }

                if (songMs >= seg.SongStartMs)
                {
                    active = seg;
                }
            }

            active ??= Segments[0];
            mappedMs = active.VideoStartMs + Math.Max(0L, songMs - active.SongStartMs);
        }

        if (videoDuration is { } vd && vd > TimeSpan.Zero)
        {
            long maxMs = Math.Max(0L, (long)vd.TotalMilliseconds);
            mappedMs = Math.Clamp(mappedMs, 0L, maxMs);
        }

        return TimeSpan.FromMilliseconds(Math.Max(0L, mappedMs));
    }

    public TimeSpan MapVideoToSongPosition(TimeSpan videoPosition, TimeSpan? songDuration = null)
    {
        long videoMs = Math.Max(0L, (long)Math.Round(videoPosition.TotalMilliseconds));
        long mappedMs = videoMs;

        if (Segments.Count > 0)
        {
            var first = Segments[0];
            if (videoMs < first.VideoStartMs)
            {
                mappedMs = first.SongStartMs;
            }
            else
            {
                MusicVideoTimeSegment? containing = null;
                MusicVideoTimeSegment? previous = null;
                for (int i = 0; i < Segments.Count; i++)
                {
                    var seg = Segments[i];
                    if (videoMs >= seg.VideoStartMs && videoMs <= seg.VideoStartMs + seg.DurationMs)
                    {
                        containing = seg;
                        break;
                    }

                    if (videoMs > seg.VideoStartMs + seg.DurationMs)
                    {
                        previous = seg;
                    }
                }

                if (containing is not null)
                {
                    mappedMs = containing.SongStartMs + Math.Max(0L, videoMs - containing.VideoStartMs);
                }
                else if (previous is not null)
                {
                    mappedMs = previous.SongStartMs + previous.DurationMs;
                }
                else
                {
                    mappedMs = first.SongStartMs;
                }
            }
        }

        if (songDuration is { } sd && sd > TimeSpan.Zero)
        {
            long maxMs = Math.Max(0L, (long)sd.TotalMilliseconds);
            mappedMs = Math.Clamp(mappedMs, 0L, maxMs);
        }

        return TimeSpan.FromMilliseconds(Math.Max(0L, mappedMs));
    }
}

/// <summary>
/// Represents a resolved YouTube Music video counterpart (Official Music Video / OMV) for a song track,
/// used by the Fullscreen Audio / Video Switcher ("Canción | Video").
/// </summary>
public sealed record ResolvedMusicVideoStream(
    string VideoId,
    string StreamUri,
    string Title,
    string Artist,
    string ThumbnailUri,
    TimeSpan Duration,
    string? SongVideoId = null,
    IReadOnlyList<MusicVideoTimeSegment>? Segments = null);
