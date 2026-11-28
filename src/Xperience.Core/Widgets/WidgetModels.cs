using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xperience.Core.Widgets;

/// <summary>
/// Stable widget definition identifiers registered in <c>Package.appxmanifest</c>
/// and handled by the Windows 11 Widgets Board provider.
/// </summary>
public static class WidgetDefinitions
{
    public const string MiniPlayer = "XperienceMiniPlayer";
    public const string RecommendedPlaylists = "XperienceRecommendedPlaylists";
    public const string MusicHub = "XperienceMusicHub";

    public static readonly IReadOnlyList<string> All =
    [
        MiniPlayer,
        RecommendedPlaylists,
        MusicHub
    ];
}

/// <summary>
/// Action verbs emitted by Adaptive Card <c>Action.Execute</c> buttons across all Xperience widgets.
/// </summary>
public static class WidgetActionVerbs
{
    public const string PlayPause = "play_pause";
    public const string Previous = "previous";
    public const string Next = "next";
    public const string ToggleLike = "toggle_like";
    public const string RefreshRecommendations = "refresh_recommendations";
    public const string PlayPlaylist = "play_playlist";
    public const string OpenAmbient = "open_ambient";
    public const string OpenApp = "open_app";
}

/// <summary>
/// Origin of a playlist or quick-mix card displayed inside Xperience widgets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetPlaylistSourceType
{
    UserPlaylist,
    SmartPlaylist,
    MadeForYou,
    RadioStation,
    LikedSongs,
    RecentlyPlayed,
    Fallback
}

/// <summary>
/// Lightweight, process-safe snapshot of current playback state persisted for Windows 11 widgets.
/// </summary>
public sealed record WidgetPlaybackSnapshot
{
    public string? TrackId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public string? ArtworkUri { get; init; }
    public bool IsPlaying { get; init; }
    public bool IsLiked { get; init; }
    public long PositionMs { get; init; }
    public long DurationMs { get; init; }
    public int QueueSize { get; init; }
    public long UpdatedAtUtcMs { get; init; }

    [JsonIgnore]
    public bool HasTrack =>
        !string.IsNullOrWhiteSpace(TrackId) &&
        !string.Equals(TrackId, "empty_state_track", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(Title);

    [JsonIgnore]
    public long SafePositionMs => Math.Max(0L, PositionMs);

    [JsonIgnore]
    public long SafeDurationMs => Math.Max(0L, DurationMs);

    [JsonIgnore]
    public int ProgressPercent
    {
        get
        {
            if (!HasTrack || SafeDurationMs <= 0L)
            {
                return 0;
            }

            long clamped = Math.Clamp(SafePositionMs, 0L, SafeDurationMs);
            return (int)Math.Clamp((clamped * 100L) / SafeDurationMs, 0L, 100L);
        }
    }

    [JsonIgnore]
    public string FormattedPosition => WidgetTimeFormatter.FormatMs(SafePositionMs);

    [JsonIgnore]
    public string FormattedDuration => WidgetTimeFormatter.FormatMs(SafeDurationMs);
}

/// <summary>
/// Lightweight playlist or quick-mix recommendation item rendered by widgets.
/// </summary>
public sealed record WidgetPlaylistItem
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public int TrackCount { get; init; }
    public string? ArtworkUri { get; init; }
    public WidgetPlaylistSourceType SourceType { get; init; } = WidgetPlaylistSourceType.UserPlaylist;
    public string? PlaylistId { get; init; }
    public string? BrowseId { get; init; }
    public string? SeedVideoId { get; init; }
    public string? Params { get; init; }

    [JsonIgnore]
    public string DisplaySubtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Subtitle))
            {
                return Subtitle.Trim();
            }

            if (TrackCount > 0)
            {
                return TrackCount == 1 ? "1 song" : $"{TrackCount} songs";
            }

            return SourceType switch
            {
                WidgetPlaylistSourceType.LikedSongs => "Liked",
                WidgetPlaylistSourceType.SmartPlaylist => "Smart Mix",
                WidgetPlaylistSourceType.RadioStation => "Quick Mix",
                WidgetPlaylistSourceType.RecentlyPlayed => "Recent",
                _ => "For You"
            };
        }
    }
}

/// <summary>
/// Full state snapshot shared between Xperience Music Player and the Windows 11 Widget Provider.
/// </summary>
public sealed record WidgetStateSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public WidgetPlaybackSnapshot Playback { get; init; } = new();
    public IReadOnlyList<WidgetPlaylistItem> Recommendations { get; init; } = [];
    public int LikedSongsCount { get; init; }
    public long RecommendationsUpdatedAtUtcMs { get; init; }
    public int RotationOffset { get; init; }
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
}

/// <summary>
/// Serializable IPC command sent from the Windows 11 Widget Provider to the running Xperience app
/// (or passed via CLI launch argument when the app is not currently running).
/// </summary>
public sealed record WidgetCommandMessage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Verb { get; init; } = string.Empty;
    public string? PlaylistId { get; init; }
    public string? PlaylistTitle { get; init; }
    public WidgetPlaylistSourceType? SourceType { get; init; }
    public string? YtmPlaylistId { get; init; }
    public string? BrowseId { get; init; }
    public string? SeedVideoId { get; init; }
    public string? Params { get; init; }
    public long TimestampUtcMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static WidgetCommandMessage? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WidgetCommandMessage>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public string ToLaunchArgumentValue()
    {
        var bytes = Encoding.UTF8.GetBytes(ToJson());
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static WidgetCommandMessage? FromLaunchArgumentValue(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        try
        {
            string base64 = encoded.Trim().Replace('-', '+').Replace('_', '/');
            int padding = (4 - (base64.Length % 4)) % 4;
            if (padding > 0)
            {
                base64 = base64.PadRight(base64.Length + padding, '=');
            }

            var bytes = Convert.FromBase64String(base64);
            return FromJson(Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Compact duration formatter (`m:ss` or `h:mm:ss`) for widget progress labels.
/// </summary>
public static class WidgetTimeFormatter
{
    public static string FormatMs(long durationMs)
    {
        long safeMs = Math.Max(0L, durationMs);
        long totalSeconds = safeMs / 1000L;
        long hours = totalSeconds / 3600L;
        long minutes = (totalSeconds % 3600L) / 60L;
        long seconds = totalSeconds % 60L;

        return hours > 0L
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:D2}", hours, minutes, seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}", minutes, seconds);
    }
}
