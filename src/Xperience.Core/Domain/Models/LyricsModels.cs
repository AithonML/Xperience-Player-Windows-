namespace Xperience.Core.Domain.Models;

/// <summary>
/// Identifies the supported online synchronized lyrics providers.
/// </summary>
public enum LyricsProviderId
{
    /// <summary>
    /// YouLyPlus (LyricsPlus v2 API) supporting word/syllable-by-syllable synchronization.
    /// </summary>
    YouLyPlus = 0,

    /// <summary>
    /// Paxsenix Lyrically API supporting Apple Music syllable/word and line synchronization.
    /// </summary>
    Paxsenix = 1,

    /// <summary>
    /// Better Lyrics TTML word/syllable-synchronized API.
    /// </summary>
    BetterLyrics = 2,

    /// <summary>
    /// LRCLIB community synchronized LRC database.
    /// </summary>
    LrcLib = 3,

    /// <summary>
    /// KuGou Music KRC/LRC synchronized lyrics service.
    /// </summary>
    KuGou = 4,

    /// <summary>
    /// SimpMusic community synchronized lyrics service (keyed by YouTube videoId or search).
    /// </summary>
    SimpMusic = 5
}

/// <summary>
/// Represents a single word, syllable, or character timing segment within a lyric line
/// for progressive letter-by-letter / syllable-by-syllable karaoke synchronization.
/// </summary>
public sealed record LyricSyllableSegment(
    TimeSpan StartTime,
    TimeSpan EndTime,
    string Text);

/// <summary>
/// Configurable priority and state entry for an online lyrics provider in the Settings Studio.
/// Note: Uses default property initializers rather than <c>required</c> so WinUI 3 XamlTypeInfo
/// can generate a parameterless activator for compiled <c>x:DataType</c> bindings.
/// </summary>
public sealed record LyricsProviderConfig
{
    /// <summary>
    /// Gets the unique provider identifier.
    /// </summary>
    public LyricsProviderId Id { get; init; } = LyricsProviderId.YouLyPlus;

    /// <summary>
    /// Gets the display name of the lyrics provider (e.g. "YouLyPlus", "Paxsenix", "Better Lyrics").
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Gets a short badge describing the synchronization capabilities of the provider.
    /// </summary>
    public string CapabilityBadge { get; init; } = "Word / Letter Sync";

    /// <summary>
    /// Gets or sets the 1-based priority rank in the provider resolution pipeline.
    /// </summary>
    public int PriorityOrder { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether this provider is enabled in the resolution pipeline.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets the formatted priority badge (e.g. "#1").
    /// </summary>
    public string PriorityLabel => $"#{PriorityOrder}";

    /// <summary>
    /// Gets the opacity of the row in Settings Studio based on enabled state.
    /// </summary>
    public double RowOpacity => IsEnabled ? 1.0 : 0.48;

    /// <summary>
    /// Gets the formatted status label ("Enabled" or "Off").
    /// </summary>
    public string StatusLabel => IsEnabled ? "Enabled" : "Off";
}

/// <summary>
/// Represents the outcome of a synchronous in-memory lyrics cache lookup.
/// </summary>
public enum LyricsCacheLookupState
{
    /// <summary>
    /// No fresh cache entry exists for this track under the active provider configuration.
    /// </summary>
    Miss = 0,

    /// <summary>
    /// A cached positive lyrics match is immediately available.
    /// </summary>
    Hit = 1,

    /// <summary>
    /// All enabled providers recently reported no lyrics for this track.
    /// </summary>
    NotFound = 2
}

/// <summary>
/// Encapsulates the resolved synchronized lyrics and the winning provider metadata.
/// </summary>
public sealed record LyricsResolutionResult(
    string ProviderName,
    bool IsWordOrCharSynced,
    IReadOnlyList<Entities.LyricLine> Lines);
