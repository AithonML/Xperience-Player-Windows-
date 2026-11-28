using Xperience.Core.Domain.Enums;

namespace Xperience.Core.Playback;

/// <summary>
/// Represents a parsed audio format entry from YouTube InnerTube <c>adaptiveFormats</c>/<c>formats</c>.
/// </summary>
public sealed record YouTubeAudioFormatDescriptor(
    string Url,
    string Sp,
    string Signature,
    string MimeType,
    string Codec,
    int BitrateBps,
    int SampleRateHz,
    int Channels,
    long? ContentLength);

/// <summary>
/// Pure selector that picks the optimal YouTube Music audio stream for a requested <see cref="StreamingAudioQuality"/> tier
/// and generates quality-scoped cache file keys.
/// </summary>
public static class StreamingQualitySelector
{
    public static int GetTargetMaxBitrateBps(StreamingAudioQuality quality) =>
        quality switch
        {
            StreamingAudioQuality.Low64 => 72_000,       // ~48-64 kbps tier
            StreamingAudioQuality.Regular128 => 136_000, // ~128 kbps tier
            StreamingAudioQuality.High160 => int.MaxValue,
            StreamingAudioQuality.FlacLossless => int.MaxValue,
            _ => int.MaxValue
        };

    public static string GetCacheTierSuffix(StreamingAudioQuality quality) =>
        quality switch
        {
            StreamingAudioQuality.Low64 => "low64",
            StreamingAudioQuality.Regular128 => "reg128",
            StreamingAudioQuality.High160 => "high160",
            StreamingAudioQuality.FlacLossless => "flac",
            _ => "high160"
        };

    public static string ExtractCodecLabel(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return "AAC";
        }

        if (mimeType.Contains("opus", StringComparison.OrdinalIgnoreCase))
        {
            return "Opus";
        }

        if (mimeType.Contains("mp4a", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Contains("audio/mp4", StringComparison.OrdinalIgnoreCase))
        {
            return "AAC";
        }

        if (mimeType.Contains("flac", StringComparison.OrdinalIgnoreCase))
        {
            return "FLAC";
        }

        if (mimeType.Contains("vorbis", StringComparison.OrdinalIgnoreCase))
        {
            return "Vorbis";
        }

        if (mimeType.Contains("webm", StringComparison.OrdinalIgnoreCase))
        {
            return "WebM Audio";
        }

        return "AAC";
    }

    /// <summary>
    /// Selects the best audio format matching <paramref name="requestedQuality"/>, preferring MP4/AAC containers
    /// (best Windows MediaFoundation / AudioGraph compatibility) while respecting bitrate ceilings for Low/Regular tiers.
    /// </summary>
    public static YouTubeAudioFormatDescriptor? SelectBestFormat(
        IReadOnlyList<YouTubeAudioFormatDescriptor> candidates,
        StreamingAudioQuality requestedQuality)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        int maxBps = GetTargetMaxBitrateBps(requestedQuality);
        bool preferMp4(YouTubeAudioFormatDescriptor c) =>
            c.MimeType.StartsWith("audio/mp4", StringComparison.OrdinalIgnoreCase) ||
            c.MimeType.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase);

        if (requestedQuality is StreamingAudioQuality.High160 or StreamingAudioQuality.FlacLossless)
        {
            return candidates
                .OrderByDescending(c => preferMp4(c) ? 1 : 0)
                .ThenByDescending(c => c.BitrateBps)
                .FirstOrDefault();
        }

        // For Low (64k) and Regular (128k), prefer the highest bitrate that stays within the ceiling,
        // preferring MP4/AAC when available. If all formats exceed the ceiling, pick the lowest bitrate available.
        var withinCeiling = candidates.Where(c => c.BitrateBps > 0 && c.BitrateBps <= maxBps).ToList();
        if (withinCeiling.Count > 0)
        {
            return withinCeiling
                .OrderByDescending(c => preferMp4(c) ? 1 : 0)
                .ThenByDescending(c => c.BitrateBps)
                .First();
        }

        return candidates
            .OrderBy(c => c.BitrateBps > 0 ? c.BitrateBps : int.MaxValue)
            .ThenByDescending(c => preferMp4(c) ? 1 : 0)
            .First();
    }
}
