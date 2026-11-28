using Xperience.Core.Domain.Enums;
using Xperience.Core.Playback;
using Xunit;

namespace Xperience.Core.Tests.Playback;

public sealed class StreamingQualitySelectorTests
{
    private static readonly IReadOnlyList<YouTubeAudioFormatDescriptor> SampleFormats =
    [
        new("https://cdn/low-opus", "", "", "audio/webm; codecs=\"opus\"", "Opus", 48_000, 48000, 2, 1_200_000),
        new("https://cdn/low-aac", "", "", "audio/mp4; codecs=\"mp4a.40.2\"", "AAC", 64_000, 44100, 2, 1_600_000),
        new("https://cdn/reg-aac", "", "", "audio/mp4; codecs=\"mp4a.40.2\"", "AAC", 128_000, 44100, 2, 3_200_000),
        new("https://cdn/high-opus", "", "", "audio/webm; codecs=\"opus\"", "Opus", 160_000, 48000, 2, 4_000_000),
        new("https://cdn/high-aac", "", "", "audio/mp4; codecs=\"mp4a.40.2\"", "AAC", 256_000, 44100, 2, 6_400_000)
    ];

    [Fact]
    public void SelectBestFormat_Low64_PicksClosestFormatWithin64kCeiling()
    {
        var picked = StreamingQualitySelector.SelectBestFormat(SampleFormats, StreamingAudioQuality.Low64);
        Assert.NotNull(picked);
        Assert.Equal("https://cdn/low-aac", picked!.Url);
        Assert.Equal(64_000, picked.BitrateBps);
    }

    [Fact]
    public void SelectBestFormat_Regular128_Picks128kFormat()
    {
        var picked = StreamingQualitySelector.SelectBestFormat(SampleFormats, StreamingAudioQuality.Regular128);
        Assert.NotNull(picked);
        Assert.Equal("https://cdn/reg-aac", picked!.Url);
        Assert.Equal(128_000, picked.BitrateBps);
    }

    [Fact]
    public void SelectBestFormat_High160_PicksHighestBitrateMp4()
    {
        var picked = StreamingQualitySelector.SelectBestFormat(SampleFormats, StreamingAudioQuality.High160);
        Assert.NotNull(picked);
        Assert.Equal("https://cdn/high-aac", picked!.Url);
        Assert.Equal(256_000, picked.BitrateBps);
    }

    [Fact]
    public void SelectBestFormat_WhenAllFormatsExceedLowCeiling_FallsBackToLowestBitrate()
    {
        var highOnly = new List<YouTubeAudioFormatDescriptor>
        {
            new("https://cdn/128", "", "", "audio/mp4", "AAC", 128_000, 44100, 2, 3_000_000),
            new("https://cdn/256", "", "", "audio/mp4", "AAC", 256_000, 44100, 2, 6_000_000)
        };

        var picked = StreamingQualitySelector.SelectBestFormat(highOnly, StreamingAudioQuality.Low64);
        Assert.NotNull(picked);
        Assert.Equal(128_000, picked!.BitrateBps);
    }

    [Theory]
    [InlineData(StreamingAudioQuality.Low64, "low64")]
    [InlineData(StreamingAudioQuality.Regular128, "reg128")]
    [InlineData(StreamingAudioQuality.High160, "high160")]
    [InlineData(StreamingAudioQuality.FlacLossless, "flac")]
    public void GetCacheTierSuffix_ReturnsDistinctKeyPerTier(StreamingAudioQuality quality, string expected)
    {
        Assert.Equal(expected, StreamingQualitySelector.GetCacheTierSuffix(quality));
    }
}
