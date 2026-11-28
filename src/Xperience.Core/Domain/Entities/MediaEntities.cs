using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Domain.Entities;

/// <summary>
/// Represents a single time-synchronized lyric line for a music track, including optional
/// syllable/character-level segments for progressive letter-by-letter karaoke rendering.
/// </summary>
/// <param name="Timestamp">The time offset from the start of the track when this line becomes active.</param>
/// <param name="Text">The localized or original lyric text.</param>
/// <param name="IsSynchronized">Indicates whether the line originates from a time-synced source.</param>
/// <param name="EndTimestamp">Optional end timestamp of the lyric line.</param>
/// <param name="Syllables">Optional word/syllable/character timing segments for progressive intra-line highlight.</param>
public sealed record LyricLine(
    TimeSpan Timestamp,
    string Text,
    bool IsSynchronized = true,
    TimeSpan? EndTimestamp = null,
    IReadOnlyList<LyricSyllableSegment>? Syllables = null)
{
    /// <summary>
    /// Gets the formatted timestamp string (m:ss).
    /// </summary>
    [JsonIgnore]
    public string FormattedTimestamp => $"{(int)Timestamp.TotalMinutes}:{Timestamp.Seconds:D2}";

    /// <summary>
    /// Gets a value indicating whether this line contains sub-line word/character timing segments.
    /// </summary>
    [JsonIgnore]
    public bool HasProgressiveSegments => Syllables is { Count: > 0 };
}

/// <summary>
/// Represents an immutable or domain-level music track entity supporting both Local files and YouTube Music streams.
/// </summary>
public sealed class TrackItem : INotifyPropertyChanged
{
    private bool _isLiked;
    private bool _isCurrentlyPlaying;
    private string? _cachedRawCoverUri;
    private string? _cachedSafeCoverUri;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Fallback transparent 1x1 asset URI used when a track has no cover art so ImageBrush never fails URI binding.
    /// </summary>
    public const string FallbackTransparentCoverUri = "ms-appx:///Assets/Covers/transparent_pixel.png";

    /// <summary>
    /// Gets the unique identifier for the track.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the display title of the song.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the primary performing artist name.
    /// </summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>
    /// Gets the album title to which the song belongs.
    /// </summary>
    public string Album { get; init; } = string.Empty;

    /// <summary>
    /// Gets the total duration of the track.
    /// </summary>
    public TimeSpan Duration { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets the URI pointing to the album or track artwork (file:///, https://, or empty when no cover exists).
    /// </summary>
    public string CoverArtUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the URI or local file path for audio playback (or ytmusic://videoId for on-demand YouTube Music streaming).
    /// </summary>
    public string AudioSourceUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the optional YouTube Music videoId when this track originates from YouTube Music.
    /// </summary>
    public string YouTubeVideoId { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional official music video (OMV) YouTube videoId associated with this song.
    /// </summary>
    public string OfficialVideoId { get; set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether this item represents an official music video card (e.g., on the Artist screen).
    /// </summary>
    public bool IsOfficialVideoItem { get; init; }

    /// <summary>
    /// Gets the view count or artist subtitle for official music video cards.
    /// </summary>
    [JsonIgnore]
    public string VideoViewsSubtitle => !string.IsNullOrWhiteSpace(PlayCountText)
        ? PlayCountText
        : (!string.IsNullOrWhiteSpace(Artist) ? Artist : "Official Video");

    /// <summary>
    /// Gets the origin source of the track (Local vs Online).
    /// </summary>
    public MediaSourceType SourceType { get; init; } = MediaSourceType.Local;

    /// <summary>
    /// Gets or sets a value indicating whether the user has marked this track as liked.
    /// </summary>
    public bool IsLiked
    {
        get => _isLiked;
        set
        {
            if (_isLiked != value)
            {
                _isLiked = value;
                NotifyPropertyChanged(nameof(IsLiked));
                NotifyPropertyChanged(nameof(LikeGlyph));
                NotifyPropertyChanged(nameof(LikeIconOpacity));
            }
        }
    }

    /// <summary>
    /// Gets or sets a runtime value indicating whether this track is the active playing track in the audio player.
    /// </summary>
    [JsonIgnore]
    public bool IsCurrentlyPlaying
    {
        get => _isCurrentlyPlaying;
        set
        {
            if (_isCurrentlyPlaying != value)
            {
                _isCurrentlyPlaying = value;
                NotifyPropertyChanged(nameof(IsCurrentlyPlaying));
                NotifyPropertyChanged(nameof(PlayingIndicatorOpacity));
                NotifyPropertyChanged(nameof(TrackNumberOpacity));
                NotifyPropertyChanged(nameof(ActiveRowHighlightOpacity));
                NotifyPropertyChanged(nameof(ActiveTitleOpacity));
                NotifyPropertyChanged(nameof(InactiveTitleOpacity));
            }
        }
    }

    /// <summary>
    /// Gets the formatted track number string for album tracklists ("1", "2", ...).
    /// </summary>
    [JsonIgnore]
    public string DisplayTrackNumber => TrackNumber > 0 ? TrackNumber.ToString() : "•";

    /// <summary>
    /// Gets the opacity (1.0 when playing, 0.0 when inactive) for the equalizer playing indicator on album rows.
    /// </summary>
    [JsonIgnore]
    public double PlayingIndicatorOpacity => _isCurrentlyPlaying ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity (0.0 when playing, 1.0 when inactive) for the track index number on album rows.
    /// </summary>
    [JsonIgnore]
    public double TrackNumberOpacity => _isCurrentlyPlaying ? 0.0 : 1.0;

    /// <summary>
    /// Gets the opacity (1.0 when playing, 0.0 when inactive) for the illuminated active row box background.
    /// </summary>
    [JsonIgnore]
    public double ActiveRowHighlightOpacity => _isCurrentlyPlaying ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity (1.0 when playing, 0.0 when inactive) for the accent-highlighted song title.
    /// </summary>
    [JsonIgnore]
    public double ActiveTitleOpacity => _isCurrentlyPlaying ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity (0.0 when playing, 1.0 when inactive) for the default primary ink song title.
    /// </summary>
    [JsonIgnore]
    public double InactiveTitleOpacity => _isCurrentlyPlaying ? 0.0 : 1.0;

    /// <summary>
    /// Gets a value indicating whether this track is marked as explicit content.
    /// </summary>
    public bool IsExplicit { get; init; }

    /// <summary>
    /// Gets the opacity (1.0 when explicit, 0.0 otherwise) for compact [E] explicit badges.
    /// </summary>
    [JsonIgnore]
    public double ExplicitBadgeOpacity => IsExplicit ? 1.0 : 0.0;

    /// <summary>
    /// Gets the Unix timestamp in milliseconds when this track was added to the library
    /// (file modified/created time for local files; persistent first-seen timestamp for online tracks).
    /// </summary>
    public long DateAddedMs { get; init; }

    /// <summary>
    /// Gets the audio bitrate or quality badge (e.g., "FLAC 24-bit", "YT MUSIC AAC", or "320 kbps").
    /// </summary>
    public string QualityBadge { get; init; } = "FLAC";

    /// <summary>
    /// Gets the musical genre extracted from the audio file metadata.
    /// </summary>
    public string Genre { get; init; } = string.Empty;

    /// <summary>
    /// Gets the release year extracted from the audio file metadata.
    /// </summary>
    public int ReleaseYear { get; init; }

    /// <summary>
    /// Gets the track number on the album extracted from the audio file metadata.
    /// </summary>
    public uint TrackNumber { get; init; }

    /// <summary>
    /// Gets the formatted play count string from YouTube Music (e.g., "60 M reproducciones").
    /// </summary>
    public string PlayCountText { get; init; } = string.Empty;

    /// <summary>
    /// Gets the approximate numeric play count parsed from YouTube Music for ranking top tracks.
    /// </summary>
    public long PlayCount { get; init; }

    /// <summary>
    /// Gets the play count string when available, or falls back to the album/artist name.
    /// </summary>
    [JsonIgnore]
    public string DisplayPlayCountOrArtist => !string.IsNullOrWhiteSpace(PlayCountText) ? PlayCountText : Artist;

    /// <summary>
    /// Gets or sets the name of the lyrics provider that supplied the active lyrics for this track.
    /// </summary>
    [JsonIgnore]
    public string LyricsProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection of synchronized lyric lines for this track.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<LyricLine> Lyrics { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether an online track also has a downloaded offline file in the local library.
    /// </summary>
    public bool IsDownloadedOffline { get; set; }

    /// <summary>
    /// Gets or sets the local offline file path when an online track has been downloaded locally.
    /// </summary>
    public string LocalOfflineFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether this track is stored locally or has a downloaded offline copy.
    /// </summary>
    [JsonIgnore]
    public bool IsDownloadedOrLocal =>
        SourceType == MediaSourceType.Local ||
        IsDownloadedOffline ||
        !string.IsNullOrWhiteSpace(LocalOfflineFilePath) ||
        (!string.IsNullOrWhiteSpace(AudioSourceUri) &&
         !AudioSourceUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
         !AudioSourceUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
         !AudioSourceUri.StartsWith("ytmusic://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the downloaded/local indicator icon on song rows.
    /// </summary>
    [JsonIgnore]
    public double DownloadedBadgeOpacity => IsDownloadedOrLocal ? 1.0 : 0.0;

    /// <summary>
    /// Gets a value indicating whether this track has a real album cover image (not empty and not legacy placeholder).
    /// </summary>
    [JsonIgnore]
    public bool HasCoverArt =>
        !string.IsNullOrWhiteSpace(CoverArtUri) &&
        !CoverArtUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !CoverArtUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a safe URI for XAML ImageBrush bindings so empty covers never trigger URI format exceptions.
    /// Cached per raw <see cref="CoverArtUri"/> string to avoid synchronous filesystem checks on every binding read.
    /// </summary>
    [JsonIgnore]
    public string SafeCoverArtUri
    {
        get
        {
            if (!HasCoverArt)
            {
                return FallbackTransparentCoverUri;
            }

            string raw = CoverArtUri;
            if (string.Equals(_cachedRawCoverUri, raw, StringComparison.Ordinal) && _cachedSafeCoverUri is not null)
            {
                return _cachedSafeCoverUri;
            }

            string normalized = NormalizeCoverUri(raw);
            _cachedRawCoverUri = raw;
            _cachedSafeCoverUri = normalized;
            return normalized;
        }
    }

    internal static string NormalizeCoverUri(string rawUri)
    {
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            return FallbackTransparentCoverUri;
        }

        string trimmed = rawUri.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{trimmed}";
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ms-appx://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var fileUri) &&
                fileUri.IsFile &&
                File.Exists(fileUri.LocalPath))
            {
                return fileUri.AbsoluteUri;
            }

            return FallbackTransparentCoverUri;
        }

        if (File.Exists(trimmed))
        {
            try
            {
                return new Uri(Path.GetFullPath(trimmed)).AbsoluteUri;
            }
            catch
            {
                // Fallback to transparent pixel if Uri conversion fails
            }
        }

        return FallbackTransparentCoverUri;
    }

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the custom cover art layer.
    /// </summary>
    [JsonIgnore]
    public double CoverArtOpacity => HasCoverArt ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the minimalist dynamic default cover icon layer.
    /// </summary>
    [JsonIgnore]
    public double DefaultCoverIconOpacity => HasCoverArt ? 0.0 : 1.0;

    /// <summary>
    /// Gets the formatted duration string (m:ss).
    /// </summary>
    [JsonIgnore]
    public string FormattedDuration => $"{(int)Duration.TotalMinutes}:{Duration.Seconds:D2}";

    /// <summary>
    /// Gets the composite subtitle combining Artist and Album ("Artist - Album").
    /// </summary>
    [JsonIgnore]
    public string ArtistAlbumSubtitle => string.IsNullOrWhiteSpace(Album) ? Artist : $"{Artist} - {Album}";

    /// <summary>
    /// Gets the bullet-separated subtitle combining Artist and Album ("Artist • Album") matching Android Library rows,
    /// omitting blank or generic "Unknown Album" labels.
    /// </summary>
    [JsonIgnore]
    public string ArtistAlbumDotSubtitle
    {
        get
        {
            string trimmedArtist = Artist?.Trim() ?? string.Empty;
            string trimmedAlbum = Album?.Trim() ?? string.Empty;
            bool hasAlbum = !string.IsNullOrWhiteSpace(trimmedAlbum) &&
                            !trimmedAlbum.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase) &&
                            !trimmedAlbum.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(trimmedArtist))
            {
                return hasAlbum ? trimmedAlbum : string.Empty;
            }

            return hasAlbum ? $"{trimmedArtist} • {trimmedAlbum}" : trimmedArtist;
        }
    }

    /// <summary>
    /// Gets the Segoe Fluent glyph for the track's favorite/like state.
    /// </summary>
    [JsonIgnore]
    public string LikeGlyph => IsLiked ? "\uEB52" : "\uEB51";

    /// <summary>
    /// Gets the foreground opacity for the track's favorite/like button icon.
    /// </summary>
    [JsonIgnore]
    public double LikeIconOpacity => IsLiked ? 1.0 : 0.72;
}

/// <summary>
/// Represents a music album entity in the catalog.
/// </summary>
public sealed class AlbumItem
{
    private string? _cachedRawCoverUri;
    private string? _cachedSafeCoverUri;
    /// <summary>
    /// Gets the unique identifier for the album.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the album title.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the album artist name.
    /// </summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>
    /// Gets the release year of the album.
    /// </summary>
    public int ReleaseYear { get; init; }

    /// <summary>
    /// Gets the number of tracks in this album (exact for local albums, release-appropriate count for online albums).
    /// </summary>
    public int TrackCount { get; init; }

    /// <summary>
    /// Gets a value indicating whether this album has a positive track count suitable for badge display.
    /// </summary>
    [JsonIgnore]
    public bool HasTrackCount => TrackCount > 0;

    /// <summary>
    /// Gets the opacity (1.0 when <see cref="TrackCount"/> &gt; 0, else 0.0) for the top-right track count pill badge.
    /// </summary>
    [JsonIgnore]
    public double TrackCountBadgeOpacity => TrackCount > 0 ? 1.0 : 0.0;

    /// <summary>
    /// Gets the formatted track count badge label ("1 track" or "{N} tracks", empty when 0).
    /// </summary>
    [JsonIgnore]
    public string TrackCountBadgeText => TrackCount switch
    {
        <= 0 => string.Empty,
        1 => "1 track",
        _ => $"{TrackCount} tracks"
    };

    /// <summary>
    /// Gets the URI to the album cover artwork.
    /// </summary>
    public string CoverArtUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the source type of the album.
    /// </summary>
    public MediaSourceType SourceType { get; init; } = MediaSourceType.Local;

    /// <summary>
    /// Gets the optional YouTube Music InnerTube browseId (e.g., "MPREb_..." for albums/singles or "VL..." for playlists).
    /// </summary>
    public string BrowseId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the optional descriptive subtitle (e.g., "2023", "Single • 2026", or "Lista de reproducción • YouTube Music").
    /// </summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>
    /// Gets the release month (1..12) when known, or 0 when only the release year is available.
    /// </summary>
    public int ReleaseMonth { get; init; }

    /// <summary>
    /// Gets the formatted release date/month label (e.g., "Marzo 2025" or "2025").
    /// </summary>
    public string ReleaseDateText { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether this release is explicitly marked as a Single or EP.
    /// </summary>
    public bool IsSingleOrEp { get; init; }

    /// <summary>
    /// Gets a value indicating whether this item represents a Single or EP based on explicit flag, ID prefix, or subtitle.
    /// </summary>
    [JsonIgnore]
    public bool IsSingleReleaseType =>
        IsSingleOrEp ||
        Id.StartsWith("ytm_single_", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(Subtitle) &&
         (Subtitle.Contains("Single", StringComparison.OrdinalIgnoreCase) ||
          Subtitle.Contains("Sencillo", StringComparison.OrdinalIgnoreCase) ||
          Subtitle.Contains("EP", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Gets the release type badge label ("SINGLE" or "ALBUM").
    /// </summary>
    [JsonIgnore]
    public string ReleaseTypeBadgeText => IsSingleReleaseType ? "SINGLE" : "ALBUM";

    /// <summary>
    /// Gets the subtitle for display on album/single/playlist cards.
    /// </summary>
    [JsonIgnore]
    public string DisplaySubtitle => !string.IsNullOrWhiteSpace(Subtitle)
        ? Subtitle
        : (ReleaseYear > 0 ? $"{ReleaseYear} • {Artist}" : Artist);

    /// <summary>
    /// Gets a value indicating whether this album has a real cover art image.
    /// </summary>
    [JsonIgnore]
    public bool HasCoverArt =>
        !string.IsNullOrWhiteSpace(CoverArtUri) &&
        !CoverArtUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !CoverArtUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a safe URI for XAML ImageBrush bindings.
    /// Cached per raw <see cref="CoverArtUri"/> string to avoid repeated filesystem checks.
    /// </summary>
    [JsonIgnore]
    public string SafeCoverArtUri
    {
        get
        {
            if (!HasCoverArt)
            {
                return TrackItem.FallbackTransparentCoverUri;
            }

            string raw = CoverArtUri;
            if (string.Equals(_cachedRawCoverUri, raw, StringComparison.Ordinal) && _cachedSafeCoverUri is not null)
            {
                return _cachedSafeCoverUri;
            }

            string normalized = TrackItem.NormalizeCoverUri(raw);
            _cachedRawCoverUri = raw;
            _cachedSafeCoverUri = normalized;
            return normalized;
        }
    }

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the custom cover art layer.
    /// </summary>
    [JsonIgnore]
    public double CoverArtOpacity => HasCoverArt ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the minimalist dynamic default cover icon layer.
    /// </summary>
    [JsonIgnore]
    public double DefaultCoverIconOpacity => HasCoverArt ? 0.0 : 1.0;

    /// <summary>
    /// Gets a value indicating whether a valid release year is present for card badge display.
    /// </summary>
    [JsonIgnore]
    public bool HasReleaseYear => ReleaseYear > 1900;

    /// <summary>
    /// Gets the formatted release year text for album cards.
    /// </summary>
    [JsonIgnore]
    public string ReleaseYearText => ReleaseYear > 1900 ? ReleaseYear.ToString() : string.Empty;

    /// <summary>
    /// Gets the card subtitle prefixed with Album type ("Album • Artist").
    /// </summary>
    [JsonIgnore]
    public string AlbumArtistSubtitle => string.IsNullOrWhiteSpace(Artist) ? "Album" : $"Album • {Artist}";

    /// <summary>
    /// Gets the subtitle for "Other Versions" album cards (e.g., "2024 • Album • Kim Petras").
    /// </summary>
    [JsonIgnore]
    public string OtherVersionSubtitle
    {
        get
        {
            string typeLabel = "Album";
            if (!string.IsNullOrWhiteSpace(Subtitle))
            {
                if (Subtitle.Contains("Single", StringComparison.OrdinalIgnoreCase) ||
                    Subtitle.Contains("Sencillo", StringComparison.OrdinalIgnoreCase))
                {
                    typeLabel = "Single";
                }
                else if (Subtitle.Contains("EP", StringComparison.OrdinalIgnoreCase))
                {
                    typeLabel = "EP";
                }
            }

            if (ReleaseYear > 1900 && !string.IsNullOrWhiteSpace(Artist))
            {
                return $"{ReleaseYear} • {typeLabel} • {Artist}";
            }

            if (!string.IsNullOrWhiteSpace(Artist))
            {
                return $"{typeLabel} • {Artist}";
            }

            return ReleaseYear > 1900 ? $"{ReleaseYear} • {typeLabel}" : typeLabel;
        }
    }

    /// <summary>
    /// Gets the subtitle for "More by [Artist]" album cards (e.g., "2020 • Kim Petras").
    /// </summary>
    [JsonIgnore]
    public string YearArtistSubtitle
    {
        get
        {
            if (ReleaseYear > 1900 && !string.IsNullOrWhiteSpace(Artist))
            {
                return $"{ReleaseYear} • {Artist}";
            }

            return DisplaySubtitle;
        }
    }

    /// <summary>
    /// Gets the Library album card subtitle formatted as "Artist • Year" (or "Artist" when year is unavailable).
    /// </summary>
    [JsonIgnore]
    public string ArtistYearSubtitle
    {
        get
        {
            string trimmedArtist = Artist?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(trimmedArtist) && ReleaseYear > 1900)
            {
                return $"{trimmedArtist} • {ReleaseYear}";
            }

            if (!string.IsNullOrWhiteSpace(trimmedArtist))
            {
                return trimmedArtist;
            }

            return ReleaseYear > 1900 ? ReleaseYear.ToString() : DisplaySubtitle;
        }
    }
}

/// <summary>
/// Represents a recording artist entity in the catalog.
/// </summary>
public sealed class ArtistItem
{
    private string? _cachedRawAvatarUri;
    private string? _cachedSafeAvatarUri;

    /// <summary>
    /// Gets the unique identifier for the artist.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the optional YouTube Music InnerTube browseId (e.g. <c>UC...</c>) for exact artist navigation.
    /// </summary>
    public string BrowseId { get; init; } = string.Empty;

    /// <summary>
    /// Extracts a valid YouTube channel browseId (<c>UC...</c>) from <see cref="BrowseId"/> or <see cref="Id"/>.
    /// </summary>
    public string? ExtractChannelBrowseId()
    {
        static string? NormalizeCandidate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string clean = raw.Trim();
            if (clean.StartsWith("ytm_sim_", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[8..];
            }
            else if (clean.StartsWith("ytm_artist_", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[11..];
            }

            if (clean.StartsWith("MPLAUC", StringComparison.Ordinal))
            {
                clean = clean[4..];
            }

            return clean.StartsWith("UC", StringComparison.Ordinal) && clean.Length >= 10 ? clean : null;
        }

        return NormalizeCandidate(BrowseId) ?? NormalizeCandidate(Id);
    }

    /// <summary>
    /// Gets the artist's display name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the URI to the circular artist portrait image.
    /// </summary>
    public string AvatarUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the genre or short descriptor for the artist.
    /// </summary>
    public string Genre { get; init; } = string.Empty;

    /// <summary>
    /// Gets the YouTube Music monthly audience / subscriber subtitle when available (e.g., "65.4M monthly audience").
    /// </summary>
    public string MonthlyAudienceText { get; init; } = string.Empty;

    /// <summary>
    /// Gets the count of local library tracks attributed to this artist.
    /// </summary>
    public int TrackCount { get; init; }

    /// <summary>
    /// Gets the count of local library albums attributed to this artist.
    /// </summary>
    public int AlbumCount { get; init; }

    /// <summary>
    /// Gets a value indicating whether the artist is featured in the "Artists You May Like" discovery rail.
    /// </summary>
    public bool IsRecommended { get; init; }

    /// <summary>
    /// Gets the source type associated with this artist entry.
    /// </summary>
    public MediaSourceType SourceType { get; init; } = MediaSourceType.All;

    /// <summary>
    /// Gets the initial uppercase character of the artist's name for circular avatar fallback badges.
    /// </summary>
    [JsonIgnore]
    public string InitialLetter
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                return "#";
            }

            foreach (char ch in Name.Trim())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    return char.ToUpperInvariant(ch).ToString();
                }
            }

            return "#";
        }
    }

    /// <summary>
    /// Gets the clean display subtitle for Library artist cards:
    /// monthly audience for online artists, song/album count for local artists, or genre fallback.
    /// </summary>
    [JsonIgnore]
    public string DisplaySubtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(MonthlyAudienceText))
            {
                return MonthlyAudienceText.Trim();
            }

            if (SourceType == MediaSourceType.Local && TrackCount > 0)
            {
                string songsPart = TrackCount == 1 ? "1 song" : $"{TrackCount} songs";
                if (AlbumCount > 0)
                {
                    string albumsPart = AlbumCount == 1 ? "1 album" : $"{AlbumCount} albums";
                    return $"{songsPart} • {albumsPart}";
                }

                return songsPart;
            }

            if (!string.IsNullOrWhiteSpace(Genre))
            {
                return Genre.Trim();
            }

            return SourceType == MediaSourceType.Online ? "YouTube Music" : "Local Library";
        }
    }

    /// <summary>
    /// Gets a value indicating whether this artist has a real avatar image.
    /// </summary>
    [JsonIgnore]
    public bool HasCoverArt =>
        !string.IsNullOrWhiteSpace(AvatarUri) &&
        !AvatarUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase) &&
        !AvatarUri.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a safe URI for XAML ImageBrush bindings.
    /// Cached per raw <see cref="AvatarUri"/> string to avoid repeated filesystem checks.
    /// </summary>
    [JsonIgnore]
    public string SafeAvatarUri
    {
        get
        {
            if (!HasCoverArt)
            {
                return TrackItem.FallbackTransparentCoverUri;
            }

            string raw = AvatarUri;
            if (string.Equals(_cachedRawAvatarUri, raw, StringComparison.Ordinal) && _cachedSafeAvatarUri is not null)
            {
                return _cachedSafeAvatarUri;
            }

            string normalized = TrackItem.NormalizeCoverUri(raw);
            _cachedRawAvatarUri = raw;
            _cachedSafeAvatarUri = normalized;
            return normalized;
        }
    }

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the custom avatar image layer.
    /// </summary>
    [JsonIgnore]
    public double CoverArtOpacity => HasCoverArt ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity (1.0 or 0.0) for the minimalist dynamic default artist icon layer.
    /// </summary>
    [JsonIgnore]
    public double DefaultCoverIconOpacity => HasCoverArt ? 0.0 : 1.0;
}

/// <summary>
/// Represents a user-created playlist with custom cover art or an automatic 2x2 collage of the first 4 distinct song covers.
/// </summary>
public sealed class UserPlaylist
{
    private string? _cachedRawCustomCoverPath;
    private string? _cachedSafeCustomCoverUri;
    private List<TrackItem>? _cachedCollageTracksRef;
    private int _cachedCollageTracksCount = -1;
    private int _cachedCollageScannedCount;
    private int _cachedCollagePrefixSig;
    private IReadOnlyList<string>? _cachedCollageCovers;

    /// <summary>
    /// Gets or sets the unique playlist identifier.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the display title of the playlist.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional description of the playlist.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional local file path or URI to a custom playlist cover image.
    /// </summary>
    public string CustomCoverImagePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the creation timestamp.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the ordered tracks inside the playlist.
    /// </summary>
    public List<TrackItem> Tracks { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether this playlist is an auto-generated smart suggestion
    /// that has not yet been saved into the user's permanent library unless explicitly saved.
    /// </summary>
    public bool IsSmartSuggestion { get; set; }

    /// <summary>
    /// Gets or sets the badge text shown on auto-generated smart playlist cards (e.g. "Top 20 • 7 días", "Selección de 25").
    /// </summary>
    public string SmartBadgeText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Segoe Fluent icon glyph for the smart playlist badge.
    /// </summary>
    public string SmartCategoryGlyph { get; set; } = "\uE734";

    /// <summary>
    /// Gets or sets a runtime value indicating whether this smart playlist has already been saved into the user's library.
    /// </summary>
    [JsonIgnore]
    public bool IsSavedToLibrary { get; set; }

    /// <summary>
    /// Gets the label for the explicit "Save to Library" action button on smart suggested playlists.
    /// </summary>
    [JsonIgnore]
    public string SaveButtonLabel => IsSavedToLibrary ? "Saved" : "Save to Library";

    /// <summary>
    /// Gets the Segoe Fluent glyph for the explicit "Save to Library" action button on smart suggested playlists.
    /// </summary>
    [JsonIgnore]
    public string SaveButtonGlyph => IsSavedToLibrary ? "\uE73E" : "\uE710";

    /// <summary>
    /// Gets a value indicating whether a custom cover photo was selected by the user.
    /// </summary>
    [JsonIgnore]
    public bool HasCustomCover =>
        !string.IsNullOrWhiteSpace(CustomCoverImagePath) &&
        !CustomCoverImagePath.EndsWith("transparent_pixel.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a safe URI for the custom cover image.
    /// Cached per raw <see cref="CustomCoverImagePath"/> string to avoid repeated filesystem checks.
    /// </summary>
    [JsonIgnore]
    public string SafeCustomCoverUri
    {
        get
        {
            if (!HasCustomCover)
            {
                return TrackItem.FallbackTransparentCoverUri;
            }

            string raw = CustomCoverImagePath;
            if (string.Equals(_cachedRawCustomCoverPath, raw, StringComparison.Ordinal) && _cachedSafeCustomCoverUri is not null)
            {
                return _cachedSafeCustomCoverUri;
            }

            string normalized = TrackItem.NormalizeCoverUri(raw);
            _cachedRawCustomCoverPath = raw;
            _cachedSafeCustomCoverUri = normalized;
            return normalized;
        }
    }

    /// <summary>
    /// Gets the distinct cover art URIs (up to 4) from the tracks in this playlist, skipping duplicate album covers.
    /// Cached until <see cref="Tracks"/> is replaced or mutated so binding reads avoid repeated allocations.
    /// </summary>
    public IReadOnlyList<string> GetDistinctCollageCovers()
    {
        var tracks = Tracks;
        if (tracks is null || tracks.Count == 0)
        {
            _cachedCollageTracksRef = tracks;
            _cachedCollageTracksCount = 0;
            _cachedCollageScannedCount = 0;
            _cachedCollagePrefixSig = 0;
            _cachedCollageCovers = Array.Empty<string>();
            return _cachedCollageCovers;
        }

        if (_cachedCollageCovers is not null &&
            ReferenceEquals(tracks, _cachedCollageTracksRef) &&
            tracks.Count == _cachedCollageTracksCount &&
            ComputeTracksPrefixSignature(tracks, _cachedCollageScannedCount) == _cachedCollagePrefixSig)
        {
            return _cachedCollageCovers;
        }

        var distinct = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int scanned = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            scanned = i + 1;
            var track = tracks[i];
            if (track is null || !track.HasCoverArt)
            {
                continue;
            }

            var safeUri = track.SafeCoverArtUri;
            var albumKey = string.IsNullOrWhiteSpace(track.Album)
                ? safeUri
                : $"{track.Artist.Trim()}|{track.Album.Trim()}";

            if (!seen.Add(safeUri) || !seenAlbums.Add(albumKey))
            {
                continue;
            }

            distinct.Add(safeUri);
            if (distinct.Count >= 4)
            {
                break;
            }
        }

        _cachedCollageTracksRef = tracks;
        _cachedCollageTracksCount = tracks.Count;
        _cachedCollageScannedCount = scanned;
        _cachedCollagePrefixSig = ComputeTracksPrefixSignature(tracks, scanned);
        _cachedCollageCovers = distinct;
        return distinct;
    }

    private static int ComputeTracksPrefixSignature(List<TrackItem> tracks, int prefixCount)
    {
        int count = Math.Min(tracks.Count, prefixCount);
        int sig = tracks.Count;
        for (int i = 0; i < count; i++)
        {
            sig = unchecked((sig * 397) ^ RuntimeHelpers.GetHashCode(tracks[i]));
        }

        return sig;
    }

    /// <summary>
    /// Gets the opacity for the single custom cover image layer.
    /// </summary>
    [JsonIgnore]
    public double CustomCoverOpacity => HasCustomCover ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity for the 2x2 collage grid when no custom cover is set and at least 1 song has cover art.
    /// </summary>
    [JsonIgnore]
    public double CollageGridOpacity => !HasCustomCover && GetDistinctCollageCovers().Count > 0 ? 1.0 : 0.0;

    /// <summary>
    /// Gets the opacity for the fallback default icon when neither custom cover nor track covers exist.
    /// </summary>
    [JsonIgnore]
    public double DefaultCoverIconOpacity => !HasCustomCover && GetDistinctCollageCovers().Count == 0 ? 1.0 : 0.0;

    /// <summary>
    /// Gets the top-left collage cover URI.
    /// </summary>
    [JsonIgnore]
    public string CollageCover1 => GetCollageSlotUri(0);

    /// <summary>
    /// Gets the top-right collage cover URI.
    /// </summary>
    [JsonIgnore]
    public string CollageCover2 => GetCollageSlotUri(1);

    /// <summary>
    /// Gets the bottom-left collage cover URI.
    /// </summary>
    [JsonIgnore]
    public string CollageCover3 => GetCollageSlotUri(2);

    /// <summary>
    /// Gets the bottom-right collage cover URI.
    /// </summary>
    [JsonIgnore]
    public string CollageCover4 => GetCollageSlotUri(3);

    /// <summary>
    /// Gets a formatted track count subtitle.
    /// </summary>
    [JsonIgnore]
    public string TrackCountSubtitle => Tracks.Count == 1 ? "1 song" : $"{Tracks.Count} songs";

    /// <summary>
    /// Gets a summary line combining description and song count.
    /// </summary>
    [JsonIgnore]
    public string SummarySubtitle => string.IsNullOrWhiteSpace(Description)
        ? TrackCountSubtitle
        : $"{TrackCountSubtitle} • {Description}";

    private string GetCollageSlotUri(int slotIndex)
    {
        var covers = GetDistinctCollageCovers();
        if (covers.Count == 0)
        {
            return TrackItem.FallbackTransparentCoverUri;
        }

        if (slotIndex < covers.Count)
        {
            return covers[slotIndex];
        }

        return covers[slotIndex % covers.Count];
    }
}
