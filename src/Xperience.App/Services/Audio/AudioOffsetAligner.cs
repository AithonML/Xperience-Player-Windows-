using System.Buffers;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Services.Audio;

/// <summary>
/// Computes exact timeline alignment between a studio Song audio file (ATV) and an Official Music Video
/// audio file (OMV) using 50 Hz onset-flux normalized cross-correlation, recovering intro/outro skit offsets
/// (e.g. Song 0:12 -> Video 0:23) even when YouTube's <c>segmentMap</c> is omitted from unauthenticated responses.
/// </summary>
internal static class AudioOffsetAligner
{
    private const int AnalysisSampleRate = 8000;
    private const int HopFrames = 160; // 20 ms per hop (50 Hz)
    private const int MaxSongSeconds = 55;
    private const int MaxVideoSeconds = 110;
    private const int MinLagSeconds = -15;
    private const int MaxLagSeconds = 75;
    private const double MinCorrelationConfidence = 0.42;

    public static IReadOnlyList<MusicVideoTimeSegment> ComputeAlignmentSegments(
        string? songAudioPath,
        string? videoAudioPath)
    {
        if (string.IsNullOrWhiteSpace(songAudioPath) ||
            string.IsNullOrWhiteSpace(videoAudioPath) ||
            !File.Exists(songAudioPath) ||
            !File.Exists(videoAudioPath))
        {
            return [];
        }

        try
        {
            if (!TryExtractOnsetEnvelope(songAudioPath, MaxSongSeconds, out float[] songFlux, out TimeSpan songDuration) ||
                !TryExtractOnsetEnvelope(videoAudioPath, MaxVideoSeconds, out float[] videoFlux, out TimeSpan videoDuration))
            {
                return [];
            }

            if (songFlux.Length < 250 || videoFlux.Length < 250)
            {
                return [];
            }

            // Pick a 25s template window starting right after any brief leading silence in the studio Song
            float peak = 0f;
            for (int i = 0; i < songFlux.Length; i++)
            {
                if (songFlux[i] > peak)
                {
                    peak = songFlux[i];
                }
            }

            if (peak <= 1e-5f)
            {
                return [];
            }

            int templateStartHop = 50; // 1.0s
            float onsetGate = peak * 0.08f;
            for (int i = 0; i < Math.Min(songFlux.Length - 250, 750); i++)
            {
                if (songFlux[i] >= onsetGate)
                {
                    templateStartHop = Math.Max(0, i - 10);
                    break;
                }
            }

            int templateLength = Math.Min(1250, songFlux.Length - templateStartHop); // up to 25s
            if (templateLength < 200)
            {
                return [];
            }

            double templateEnergy = 0.0;
            for (int k = 0; k < templateLength; k++)
            {
                double v = songFlux[templateStartHop + k];
                templateEnergy += v * v;
            }

            if (templateEnergy <= 1e-6)
            {
                return [];
            }

            int minLagHops = MinLagSeconds * 50;
            int maxLagHops = MaxLagSeconds * 50;
            double bestCorr = 0.0;
            int bestLagHops = 0;

            for (int lag = minLagHops; lag <= maxLagHops; lag++)
            {
                int videoStart = templateStartHop + lag;
                if (videoStart < 0 || videoStart + templateLength > videoFlux.Length)
                {
                    continue;
                }

                double dot = 0.0;
                double videoEnergy = 0.0;
                for (int k = 0; k < templateLength; k++)
                {
                    double sv = songFlux[templateStartHop + k];
                    double vv = videoFlux[videoStart + k];
                    dot += sv * vv;
                    videoEnergy += vv * vv;
                }

                if (videoEnergy <= 1e-6)
                {
                    continue;
                }

                double corr = dot / Math.Sqrt(templateEnergy * videoEnergy);
                if (corr > bestCorr)
                {
                    bestCorr = corr;
                    bestLagHops = lag;
                }
            }

            if (bestCorr < MinCorrelationConfidence)
            {
                return [];
            }

            long offsetMs = bestLagHops * 20L;
            if (Math.Abs(offsetMs) < 250L)
            {
                return [];
            }

            long songDurMs = songDuration > TimeSpan.Zero ? (long)songDuration.TotalMilliseconds : 240_000L;
            long videoDurMs = videoDuration > TimeSpan.Zero ? (long)videoDuration.TotalMilliseconds : 240_000L;

            if (offsetMs > 0)
            {
                long segDuration = Math.Max(1000L, Math.Min(songDurMs, videoDurMs - offsetMs));
                return [new MusicVideoTimeSegment(SongStartMs: 0L, VideoStartMs: offsetMs, DurationMs: segDuration)];
            }

            long songLeadMs = -offsetMs;
            long negSegDuration = Math.Max(1000L, Math.Min(videoDurMs, songDurMs - songLeadMs));
            return [new MusicVideoTimeSegment(SongStartMs: songLeadMs, VideoStartMs: 0L, DurationMs: negSegDuration)];
        }
        catch
        {
            return [];
        }
    }

    private static bool TryExtractOnsetEnvelope(
        string path,
        int maxSeconds,
        out float[] flux,
        out TimeSpan duration)
    {
        flux = [];
        duration = TimeSpan.Zero;

        using var decoder = MediaFoundationPcmDecoder.TryOpen(path, AnalysisSampleRate, out _);
        if (decoder is null)
        {
            return false;
        }

        duration = decoder.Duration;
        int sr = Math.Max(8000, decoder.SampleRate);
        int hopFrames = Math.Max(80, sr / 50); // 20 ms hop
        int maxHops = maxSeconds * 50;
        var rmsList = new List<float>(maxHops);

        float[] buf = ArrayPool<float>.Shared.Rent(hopFrames * 2);
        try
        {
            for (int h = 0; h < maxHops; h++)
            {
                int read = decoder.ReadStereoFrames(buf, 0, hopFrames, out bool eof);
                if (read > 0)
                {
                    double sumSq = 0.0;
                    for (int f = 0; f < read; f++)
                    {
                        float mono = 0.5f * (buf[f * 2] + buf[(f * 2) + 1]);
                        sumSq += mono * mono;
                    }

                    rmsList.Add((float)Math.Sqrt(sumSq / read));
                }

                if (eof || read == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buf);
        }

        if (rmsList.Count < 10)
        {
            return false;
        }

        flux = new float[rmsList.Count];
        float prev = rmsList[0];
        for (int i = 1; i < rmsList.Count; i++)
        {
            float curr = rmsList[i];
            flux[i] = MathF.Max(0f, curr - prev);
            prev = curr;
        }

        return true;
    }
}
