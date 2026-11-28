using System.Runtime.InteropServices;

namespace Xperience.App.Services.Audio;

/// <summary>
/// Lightweight native Windows Media Foundation (<c>IMFSourceReader</c>) audio decoder that streams
/// any Windows-supported audio format (.m4a/AAC, .mp3, .flac, .wav, .ogg, .wma, or HTTP/HTTPS URL)
/// as interleaved 32-bit IEEE float stereo PCM at the target <c>AudioGraph</c> sample rate,
/// with sample-accurate seeking and zero external NuGet dependencies.
/// </summary>
internal sealed class MediaFoundationPcmDecoder : IDisposable
{
    private const uint MF_VERSION = 0x00020070;
    private const uint MF_SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;
    private const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    private const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    private const uint MF_SOURCE_READERF_ERROR = 0x00000001;
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
    private const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x00000020;

    private static readonly object s_mfInitLock = new();
    private static int s_mfRefCount;

    private readonly object _readerLock = new();
    private IMFSourceReader? _reader;
    private float[] _leftoverBuffer = [];
    private int _leftoverOffsetFloats;
    private int _leftoverCountFloats;
    private float[] _scratchCopyBuffer = new float[16384];
    private int _decodedChannels = 2;
    private bool _reachedEof;
    private int _disposed;

    /// <summary>
    /// Gets the negotiated output sample rate in Hz.
    /// </summary>
    public int SampleRate { get; private set; }

    /// <summary>
    /// Gets the output channel count (always normalized to 2 stereo channels by <see cref="ReadStereoFrames"/>).
    /// </summary>
    public int Channels => 2;

    /// <summary>
    /// Gets the total duration reported by the media container, if available.
    /// </summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the underlying Media Foundation reader has reached end-of-stream.
    /// </summary>
    public bool ReachedEndOfStream => _reachedEof && _leftoverCountFloats <= 0;

    private MediaFoundationPcmDecoder()
    {
    }

    /// <summary>
    /// Opens an audio file path or URI and configures Windows Media Foundation to decode directly to
    /// 32-bit IEEE float PCM at <paramref name="targetSampleRate"/> Hz.
    /// </summary>
    public static MediaFoundationPcmDecoder? TryOpen(string pathOrUrl, int targetSampleRate, out string? errorReason)
    {
        errorReason = null;
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            errorReason = "Empty path or URL.";
            return null;
        }

        EnsureMfStarted();

        var decoder = new MediaFoundationPcmDecoder();
        try
        {
            int hr = MfNative.MFCreateSourceReaderFromURL(pathOrUrl, IntPtr.Zero, out var reader);
            if (hr < 0 || reader is null)
            {
                errorReason = $"MFCreateSourceReaderFromURL failed (0x{hr:X8}).";
                decoder.Dispose();
                return null;
            }

            decoder._reader = reader;

            reader.SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, false);
            hr = reader.SetStreamSelection(MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);
            if (hr < 0)
            {
                errorReason = $"SetStreamSelection failed (0x{hr:X8}).";
                decoder.Dispose();
                return null;
            }

            // Request 32-bit IEEE float stereo PCM at the exact AudioGraph sample rate
            hr = MfNative.MFCreateMediaType(out var requestedType);
            if (hr < 0 || requestedType is null)
            {
                errorReason = $"MFCreateMediaType failed (0x{hr:X8}).";
                decoder.Dispose();
                return null;
            }

            try
            {
                int sr = Math.Clamp(targetSampleRate, 8000, 192000);
                requestedType.SetGUID(MfNative.MF_MT_MAJOR_TYPE, MfNative.MFMediaType_Audio);
                requestedType.SetGUID(MfNative.MF_MT_SUBTYPE, MfNative.MFAudioFormat_Float);
                requestedType.SetUINT32(MfNative.MF_MT_AUDIO_NUM_CHANNELS, 2);
                requestedType.SetUINT32(MfNative.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sr);
                requestedType.SetUINT32(MfNative.MF_MT_AUDIO_BITS_PER_SAMPLE, 32);
                requestedType.SetUINT32(MfNative.MF_MT_AUDIO_BLOCK_ALIGNMENT, 8);
                requestedType.SetUINT32(MfNative.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(sr * 8));

                hr = reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, requestedType);
                if (hr < 0)
                {
                    // Fallback: request float PCM without forcing sample rate / channel count, then read negotiated type
                    Marshal.ReleaseComObject(requestedType);
                    requestedType = null;
                    hr = MfNative.MFCreateMediaType(out requestedType);
                    if (hr < 0 || requestedType is null)
                    {
                        errorReason = $"MFCreateMediaType(fallback) failed (0x{hr:X8}).";
                        decoder.Dispose();
                        return null;
                    }

                    requestedType.SetGUID(MfNative.MF_MT_MAJOR_TYPE, MfNative.MFMediaType_Audio);
                    requestedType.SetGUID(MfNative.MF_MT_SUBTYPE, MfNative.MFAudioFormat_Float);
                    hr = reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, requestedType);
                    if (hr < 0)
                    {
                        errorReason = $"SetCurrentMediaType(Float) failed (0x{hr:X8}).";
                        decoder.Dispose();
                        return null;
                    }
                }
            }
            finally
            {
                if (requestedType is not null)
                {
                    Marshal.ReleaseComObject(requestedType);
                }
            }

            decoder.RefreshNegotiatedMediaTypeLocked();
            decoder.ReadDurationLocked();
            return decoder;
        }
        catch (Exception ex)
        {
            errorReason = $"{ex.GetType().Name}: {ex.Message}";
            decoder.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Gets the last HRESULT returned by <see cref="Seek(TimeSpan)"/> for diagnostic logging.
    /// </summary>
    public int LastSeekHResult { get; private set; }

    /// <summary>
    /// Gets the exact decoded sample timestamp landed on after the most recent <see cref="Seek(TimeSpan)"/>.
    /// </summary>
    public TimeSpan LastSeekActualPosition { get; private set; } = TimeSpan.Zero;

    /// <summary>
    /// Seeks the decoder to <paramref name="position"/> and clears any leftover decoded chunk.
    /// Automatically marshals onto an MTA thread when invoked from an STA UI thread so the
    /// Media Foundation <c>IMFSourceReader</c> COM RCW never fails with <c>E_NOINTERFACE</c>.
    /// </summary>
    public bool Seek(TimeSpan position)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return Task.Run(() => SeekCore(position)).GetAwaiter().GetResult();
        }

        return SeekCore(position);
    }

    private bool SeekCore(TimeSpan position)
    {
        lock (_readerLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _reader is null)
            {
                LastSeekHResult = unchecked((int)0x80070005);
                return false;
            }

            _leftoverOffsetFloats = 0;
            _leftoverCountFloats = 0;
            _reachedEof = false;

            long targetTicks = Math.Max(0L, position.Ticks);
            if (Duration > TimeSpan.Zero && targetTicks > Duration.Ticks)
            {
                targetTicks = Duration.Ticks;
            }

            try
            {
                // Flush any buffered MFT frames before repositioning; ignore non-fatal flush errors
                _ = _reader.Flush(MF_SOURCE_READER_FIRST_AUDIO_STREAM);

                Guid timeFormat = Guid.Empty;
                var seekVariant = new PropVariant
                {
                    vt = 20, // VT_I8 (100-nanosecond units)
                    hVal = targetTicks
                };
                int hr = _reader.SetCurrentPosition(in timeFormat, ref seekVariant);

                // Some MP4/MP3 splitters reject seeking strictly at Duration.Ticks; retry slightly before EOF
                long nearEndMarginTicks = TimeSpan.FromMilliseconds(250).Ticks;
                if (hr < 0 && Duration.Ticks > nearEndMarginTicks && targetTicks > Duration.Ticks - nearEndMarginTicks)
                {
                    targetTicks = Math.Max(0L, Duration.Ticks - nearEndMarginTicks);
                    seekVariant.hVal = targetTicks;
                    hr = _reader.SetCurrentPosition(in timeFormat, ref seekVariant);
                }

                LastSeekHResult = hr;
                if (hr < 0)
                {
                    return false;
                }

                LastSeekActualPosition = TimeSpan.FromTicks(targetTicks);

                // Fragmented DASH .m4a and WebM/Opus cues seek to the sync point AT OR BEFORE targetTicks (often 1-4s earlier).
                // Decode and discard pre-roll samples up to targetTicks so seeking is sample-accurate.
                if (targetTicks > 0)
                {
                    long maxPrerollWindowTicks = TimeSpan.FromSeconds(15).Ticks;
                    int emptyReads = 0;
                    for (int iter = 0; iter < 512 && !_reachedEof; iter++)
                    {
                        int readHr;
                        uint streamFlags;
                        long samplePtsTicks;
                        IMFSample? sample;
                        try
                        {
                            readHr = _reader.ReadSample(
                                MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                                0,
                                out _,
                                out streamFlags,
                                out samplePtsTicks,
                                out sample);
                        }
                        catch
                        {
                            _reachedEof = true;
                            break;
                        }

                        if (readHr < 0 || (streamFlags & MF_SOURCE_READERF_ERROR) != 0)
                        {
                            if (sample is not null)
                            {
                                Marshal.ReleaseComObject(sample);
                            }
                            _reachedEof = true;
                            break;
                        }

                        if ((streamFlags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                        {
                            RefreshNegotiatedMediaTypeLocked();
                        }

                        if ((streamFlags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                        {
                            _reachedEof = true;
                        }

                        if (sample is null)
                        {
                            if (_reachedEof || ++emptyReads > 16)
                            {
                                break;
                            }
                            continue;
                        }

                        emptyReads = 0;
                        int decodedFrames = 0;
                        try
                        {
                            if (!TryDecodeSampleToStereoLocked(sample, out decodedFrames))
                            {
                                continue;
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(sample);
                        }

                        if (decodedFrames <= 0)
                        {
                            continue;
                        }

                        int sr = Math.Max(8000, SampleRate);
                        if (samplePtsTicks >= 0 &&
                            samplePtsTicks < targetTicks &&
                            (targetTicks - samplePtsTicks) <= maxPrerollWindowTicks)
                        {
                            long deltaTicks = targetTicks - samplePtsTicks;
                            int skipFrames = (int)Math.Round(deltaTicks * (double)sr / TimeSpan.TicksPerSecond);
                            if (skipFrames >= decodedFrames)
                            {
                                LastSeekActualPosition = TimeSpan.FromTicks(
                                    samplePtsTicks + (long)Math.Round(decodedFrames * (double)TimeSpan.TicksPerSecond / sr));
                                continue;
                            }

                            int keepFrames = decodedFrames - Math.Max(0, skipFrames);
                            StoreLeftoverFramesLocked(_scratchCopyBuffer, Math.Max(0, skipFrames), keepFrames);
                            LastSeekActualPosition = TimeSpan.FromTicks(
                                samplePtsTicks + (long)Math.Round(Math.Max(0, skipFrames) * (double)TimeSpan.TicksPerSecond / sr));
                            break;
                        }

                        StoreLeftoverFramesLocked(_scratchCopyBuffer, 0, decodedFrames);
                        if (samplePtsTicks >= 0)
                        {
                            LastSeekActualPosition = TimeSpan.FromTicks(samplePtsTicks);
                        }
                        break;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                LastSeekHResult = ex.HResult;
                return false;
            }
        }
    }

    private void StoreLeftoverFramesLocked(float[] stereoInterleaved, int startFrame, int frameCount)
    {
        if (frameCount <= 0)
        {
            return;
        }

        int floatCount = frameCount * 2;
        if (_leftoverBuffer.Length < floatCount)
        {
            _leftoverBuffer = new float[Math.Max(floatCount, 8192)];
        }

        Array.Copy(stereoInterleaved, startFrame * 2, _leftoverBuffer, 0, floatCount);
        _leftoverOffsetFloats = 0;
        _leftoverCountFloats = floatCount;
    }

    private bool TryDecodeSampleToStereoLocked(IMFSample sample, out int stereoFrames)
    {
        stereoFrames = 0;
        IMFMediaBuffer? mediaBuffer = null;
        try
        {
            int hr = sample.ConvertToContiguousBuffer(out mediaBuffer);
            if (hr < 0 || mediaBuffer is null)
            {
                return false;
            }

            hr = mediaBuffer.Lock(out IntPtr pData, out _, out uint currentLengthBytes);
            if (hr < 0 || currentLengthBytes < sizeof(float))
            {
                return false;
            }

            try
            {
                int rawFloatCount = (int)(currentLengthBytes / sizeof(float));
                int srcCh = Math.Max(1, _decodedChannels);
                int sampleFrames = rawFloatCount / srcCh;
                if (sampleFrames <= 0)
                {
                    return false;
                }

                int requiredBufferFloats = Math.Max(rawFloatCount, sampleFrames * 2);
                if (_scratchCopyBuffer.Length < requiredBufferFloats)
                {
                    _scratchCopyBuffer = new float[Math.Max(requiredBufferFloats, _scratchCopyBuffer.Length * 2)];
                }

                Marshal.Copy(pData, _scratchCopyBuffer, 0, rawFloatCount);

                if (srcCh == 1)
                {
                    for (int f = sampleFrames - 1; f >= 0; f--)
                    {
                        float mono = _scratchCopyBuffer[f];
                        _scratchCopyBuffer[f * 2] = mono;
                        _scratchCopyBuffer[(f * 2) + 1] = mono;
                    }
                }
                else if (srcCh > 2)
                {
                    for (int f = 0; f < sampleFrames; f++)
                    {
                        int baseSrc = f * srcCh;
                        _scratchCopyBuffer[f * 2] = _scratchCopyBuffer[baseSrc];
                        _scratchCopyBuffer[(f * 2) + 1] = _scratchCopyBuffer[baseSrc + 1];
                    }
                }

                stereoFrames = sampleFrames;
                return true;
            }
            finally
            {
                mediaBuffer.Unlock();
            }
        }
        finally
        {
            if (mediaBuffer is not null)
            {
                Marshal.ReleaseComObject(mediaBuffer);
            }
        }
    }

    /// <summary>
    /// Reads up to <paramref name="maxFrames"/> interleaved stereo float frames (2 floats per frame)
    /// into <paramref name="destination"/>.
    /// </summary>
    public int ReadStereoFrames(float[] destination, int destFloatOffset, int maxFrames, out bool reachedEof)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            bool eofResult = false;
            int frames = Task.Run(() => ReadStereoFramesCore(destination, destFloatOffset, maxFrames, out eofResult))
                .GetAwaiter()
                .GetResult();
            reachedEof = eofResult;
            return frames;
        }

        return ReadStereoFramesCore(destination, destFloatOffset, maxFrames, out reachedEof);
    }

    private int ReadStereoFramesCore(float[] destination, int destFloatOffset, int maxFrames, out bool reachedEof)
    {
        reachedEof = false;
        if (maxFrames <= 0)
        {
            return 0;
        }

        lock (_readerLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _reader is null)
            {
                reachedEof = true;
                return 0;
            }

            int framesWritten = 0;

            // 1. Drain leftover floats from previous decoded IMFSample
            if (_leftoverCountFloats > 0)
            {
                int availFrames = _leftoverCountFloats / 2;
                int toCopyFrames = Math.Min(maxFrames, availFrames);
                int toCopyFloats = toCopyFrames * 2;
                Array.Copy(_leftoverBuffer, _leftoverOffsetFloats, destination, destFloatOffset, toCopyFloats);
                _leftoverOffsetFloats += toCopyFloats;
                _leftoverCountFloats -= toCopyFloats;
                framesWritten += toCopyFrames;
            }

            // 2. Pull fresh IMFSamples until requested frame count is satisfied or EOF is reached
            int consecutiveEmptyReads = 0;
            while (framesWritten < maxFrames && !_reachedEof)
            {
                int hr;
                uint streamFlags;
                IMFSample? sample;
                try
                {
                    hr = _reader.ReadSample(
                        MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                        0,
                        out _,
                        out streamFlags,
                        out _,
                        out sample);
                }
                catch
                {
                    _reachedEof = true;
                    break;
                }

                if (hr < 0 || (streamFlags & MF_SOURCE_READERF_ERROR) != 0)
                {
                    if (sample is not null)
                    {
                        Marshal.ReleaseComObject(sample);
                    }

                    _reachedEof = true;
                    break;
                }

                if ((streamFlags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                {
                    RefreshNegotiatedMediaTypeLocked();
                }

                if ((streamFlags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    _reachedEof = true;
                }

                if (sample is null)
                {
                    if (_reachedEof || ++consecutiveEmptyReads > 16)
                    {
                        if (consecutiveEmptyReads > 16)
                        {
                            _reachedEof = true;
                        }

                        break;
                    }

                    continue;
                }

                consecutiveEmptyReads = 0;

                IMFMediaBuffer? mediaBuffer = null;
                try
                {
                    hr = sample.ConvertToContiguousBuffer(out mediaBuffer);
                    if (hr < 0 || mediaBuffer is null)
                    {
                        continue;
                    }

                    hr = mediaBuffer.Lock(out IntPtr pData, out _, out uint currentLengthBytes);
                    if (hr < 0 || currentLengthBytes < sizeof(float))
                    {
                        continue;
                    }

                    try
                    {
                        int rawFloatCount = (int)(currentLengthBytes / sizeof(float));
                        if (_scratchCopyBuffer.Length < rawFloatCount)
                        {
                            _scratchCopyBuffer = new float[Math.Max(rawFloatCount, _scratchCopyBuffer.Length * 2)];
                        }

                        Marshal.Copy(pData, _scratchCopyBuffer, 0, rawFloatCount);

                        int srcCh = Math.Max(1, _decodedChannels);
                        int sampleFrames = rawFloatCount / srcCh;
                        int stereoFloatsNeeded = sampleFrames * 2;

                        int framesRemaining = maxFrames - framesWritten;
                        int directFrames = Math.Min(framesRemaining, sampleFrames);
                        int outIdx = destFloatOffset + (framesWritten * 2);

                        if (srcCh == 2)
                        {
                            Array.Copy(_scratchCopyBuffer, 0, destination, outIdx, directFrames * 2);
                        }
                        else if (srcCh == 1)
                        {
                            for (int f = 0; f < directFrames; f++)
                            {
                                float mono = _scratchCopyBuffer[f];
                                destination[outIdx + (f * 2)] = mono;
                                destination[outIdx + (f * 2) + 1] = mono;
                            }
                        }
                        else
                        {
                            // Downmix multi-channel (e.g., 5.1) front L/R cleanly
                            for (int f = 0; f < directFrames; f++)
                            {
                                int baseSrc = f * srcCh;
                                destination[outIdx + (f * 2)] = _scratchCopyBuffer[baseSrc];
                                destination[outIdx + (f * 2) + 1] = _scratchCopyBuffer[baseSrc + 1];
                            }
                        }

                        framesWritten += directFrames;

                        // Store remaining frames in leftover buffer
                        int leftoverFrames = sampleFrames - directFrames;
                        if (leftoverFrames > 0)
                        {
                            int leftoverFloats = leftoverFrames * 2;
                            if (_leftoverBuffer.Length < leftoverFloats)
                            {
                                _leftoverBuffer = new float[Math.Max(leftoverFloats, 8192)];
                            }

                            if (srcCh == 2)
                            {
                                Array.Copy(_scratchCopyBuffer, directFrames * 2, _leftoverBuffer, 0, leftoverFloats);
                            }
                            else if (srcCh == 1)
                            {
                                for (int f = 0; f < leftoverFrames; f++)
                                {
                                    float mono = _scratchCopyBuffer[directFrames + f];
                                    _leftoverBuffer[f * 2] = mono;
                                    _leftoverBuffer[(f * 2) + 1] = mono;
                                }
                            }
                            else
                            {
                                for (int f = 0; f < leftoverFrames; f++)
                                {
                                    int baseSrc = (directFrames + f) * srcCh;
                                    _leftoverBuffer[f * 2] = _scratchCopyBuffer[baseSrc];
                                    _leftoverBuffer[(f * 2) + 1] = _scratchCopyBuffer[baseSrc + 1];
                                }
                            }

                            _leftoverOffsetFloats = 0;
                            _leftoverCountFloats = leftoverFloats;
                        }
                    }
                    finally
                    {
                        mediaBuffer.Unlock();
                    }
                }
                finally
                {
                    if (mediaBuffer is not null)
                    {
                        Marshal.ReleaseComObject(mediaBuffer);
                    }

                    Marshal.ReleaseComObject(sample);
                }
            }

            reachedEof = _reachedEof && _leftoverCountFloats <= 0;
            return framesWritten;
        }
    }

    private void RefreshNegotiatedMediaTypeLocked()
    {
        if (_reader is null)
        {
            return;
        }

        if (_reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, out var actualType) >= 0 && actualType is not null)
        {
            try
            {
                Guid srKey = MfNative.MF_MT_AUDIO_SAMPLES_PER_SECOND;
                Guid chKey = MfNative.MF_MT_AUDIO_NUM_CHANNELS;

                if (actualType.GetUINT32(ref srKey, out uint sr) >= 0 && sr >= 8000)
                {
                    SampleRate = (int)sr;
                }
                else
                {
                    SampleRate = 48000;
                }

                if (actualType.GetUINT32(ref chKey, out uint ch) >= 0 && ch >= 1)
                {
                    _decodedChannels = (int)ch;
                }
                else
                {
                    _decodedChannels = 2;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(actualType);
            }
        }
    }

    private void ReadDurationLocked()
    {
        if (_reader is null)
        {
            return;
        }

        try
        {
            int hr = _reader.GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MfNative.MF_PD_DURATION, out PropVariant pv);
            if (hr >= 0)
            {
                long ticks = pv.vt == 21 ? (long)pv.uhVal : pv.vt == 20 ? pv.hVal : 0L;
                if (ticks > 0)
                {
                    Duration = TimeSpan.FromTicks(ticks);
                }
            }
        }
        catch
        {
            Duration = TimeSpan.Zero;
        }
    }

    private static void EnsureMfStarted()
    {
        lock (s_mfInitLock)
        {
            if (s_mfRefCount == 0)
            {
                MfNative.MFStartup(MF_VERSION, 0);
            }
            s_mfRefCount++;
        }
    }

    private static void ReleaseMfRef()
    {
        lock (s_mfInitLock)
        {
            if (s_mfRefCount > 0)
            {
                s_mfRefCount--;
                if (s_mfRefCount == 0)
                {
                    MfNative.MFShutdown();
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_readerLock)
        {
            if (_reader is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(_reader);
                }
                catch
                {
                    // Ignore COM release race during shutdown
                }
                finally
                {
                    _reader = null;
                }
            }

            _leftoverBuffer = [];
            _leftoverCountFloats = 0;
        }

        ReleaseMfRef();
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public long hVal;
        [FieldOffset(8)] public ulong uhVal;
    }

    private static class MfNative
    {
        public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00aa00389b71");
        public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(uint version, uint dwFlags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern int MFCreateSourceReaderFromURL(string pwszURL, IntPtr pAttributes, out IMFSourceReader ppSourceReader);
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
        [PreserveSig] int Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
        [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr Value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
        [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(in Guid guidKey, byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] new int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] new int CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
        [PreserveSig] new int Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
        [PreserveSig] new int GetUINT32(ref Guid guidKey, out uint punValue);
        [PreserveSig] new int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] new int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] new int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] new int GetStringLength(ref Guid guidKey, out uint pcchLength);
        [PreserveSig] new int GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] new int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] new int GetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] new int SetItem(ref Guid guidKey, IntPtr Value);
        [PreserveSig] new int DeleteItem(ref Guid guidKey);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(in Guid guidKey, uint unValue);
        [PreserveSig] new int SetUINT64(in Guid guidKey, ulong unValue);
        [PreserveSig] new int SetDouble(in Guid guidKey, double fValue);
        [PreserveSig] new int SetGUID(in Guid guidKey, in Guid guidValue);
        [PreserveSig] new int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] new int SetBlob(in Guid guidKey, byte[] pBuf, uint cbBufSize);
        [PreserveSig] new int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out uint pcItems);
        [PreserveSig] new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] new int CopyAllItems(IMFAttributes pDest);
        [PreserveSig] int GetMajorType(out Guid pguidMajorType);
        [PreserveSig] int IsCompressedFormat(out bool pfCompressed);
        [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
        [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
        [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
        [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid guidKey, IntPtr pValue);
        [PreserveSig] new int GetItemType(ref Guid guidKey, out int pType);
        [PreserveSig] new int CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
        [PreserveSig] new int Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
        [PreserveSig] new int GetUINT32(ref Guid guidKey, out uint punValue);
        [PreserveSig] new int GetUINT64(ref Guid guidKey, out ulong punValue);
        [PreserveSig] new int GetDouble(ref Guid guidKey, out double pfValue);
        [PreserveSig] new int GetGUID(ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] new int GetStringLength(ref Guid guidKey, out uint pcchLength);
        [PreserveSig] new int GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] new int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] new int GetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] new int SetItem(ref Guid guidKey, IntPtr Value);
        [PreserveSig] new int DeleteItem(ref Guid guidKey);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(in Guid guidKey, uint unValue);
        [PreserveSig] new int SetUINT64(in Guid guidKey, ulong unValue);
        [PreserveSig] new int SetDouble(in Guid guidKey, double fValue);
        [PreserveSig] new int SetGUID(in Guid guidKey, in Guid guidValue);
        [PreserveSig] new int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] new int SetBlob(in Guid guidKey, byte[] pBuf, uint cbBufSize);
        [PreserveSig] new int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out uint pcItems);
        [PreserveSig] new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] new int CopyAllItems(IMFAttributes pDest);
        [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
        [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
        [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
        [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
        [PreserveSig] int SetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
        [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType ppMediaType);
        [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);
        [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
        [PreserveSig] int SetCurrentPosition(in Guid guidTimeFormat, ref PropVariant varPosition);
        [PreserveSig] int ReadSample(uint dwStreamIndex, uint dwControlFlags, out uint pdwActualStreamIndex, out uint pdwStreamFlags, out long pllTimestamp, out IMFSample ppSample);
        [PreserveSig] int Flush(uint dwStreamIndex);
        [PreserveSig] int GetServiceForStream(uint dwStreamIndex, in Guid guidService, in Guid riid, out IntPtr ppvObject);
        [PreserveSig] int GetPresentationAttribute(uint dwStreamIndex, in Guid guidAttribute, out PropVariant pvarAttribute);
    }
}
