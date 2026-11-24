using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Xperience.App.Services;

/// <summary>
/// Converts fragmented Apple Music Canvas / Artist Header MP4 files (fMP4: <c>ftyp + moov(mvex) + moof/mdat...</c>
/// with no <c>sidx</c>/<c>mfra</c>, zero <c>mvhd</c> duration, and non-zero <c>tfdt</c> priming offsets) into
/// standard fast-start flat ISO-BMFF MP4 files (<c>ftyp + moov(stts/ctts/stss/stsc/stsz/stco) + mdat</c>).
/// Windows Media Foundation (<c>Windows.Media.Playback.MediaPlayer</c>) loops flat MP4s with exact
/// <c>NaturalDuration</c>, <c>CanSeek == true</c>, and zero EOF freezes.
/// </summary>
public static class Mp4CanvasNormalizer
{
    private readonly record struct SampleRecord(
        uint Duration,
        uint Size,
        int CompositionOffset,
        bool IsSync,
        long SourceByteOffset);

    private readonly record struct ChunkRecord(
        int SampleCount,
        long OutputDataOffset);

    /// <summary>
    /// Checks whether an MP4 file on disk is a fragmented MP4 (contains top-level <c>moof</c> boxes).
    /// </summary>
    public static bool IsFragmentedMp4(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[8];
            Span<byte> ext = stackalloc byte[8];
            long pos = 0;
            int boxesScanned = 0;
            while (pos + 8 <= fs.Length && boxesScanned < 32)
            {
                fs.Position = pos;
                if (fs.Read(header) < 8)
                {
                    break;
                }

                ulong size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                if (size == 1 && pos + 16 <= fs.Length)
                {
                    if (fs.Read(ext) < 8)
                    {
                        break;
                    }

                    size = BinaryPrimitives.ReadUInt64BigEndian(ext);
                }
                else if (size == 0)
                {
                    size = (ulong)(fs.Length - pos);
                }

                if (size < 8)
                {
                    break;
                }

                if (header[4] == (byte)'m' &&
                    header[5] == (byte)'o' &&
                    header[6] == (byte)'o' &&
                    header[7] == (byte)'f')
                {
                    return true;
                }

                pos += (long)size;
                boxesScanned++;
            }
        }
        catch
        {
            // Ignore IO probe errors
        }

        return false;
    }

    /// <summary>
    /// Ensures a local MP4 file (such as an offline <c>.canvas.mp4</c> sidecar or legacy cached fMP4)
    /// is available as a flat fast-start MP4 suitable for gapless hardware looping in WinRT <c>MediaPlayer</c>.
    /// Returns the original path immediately if it is already flat; otherwise flattens into <paramref name="cacheDirectory"/>
    /// keyed by source path + file size + last write time.
    /// </summary>
    public static string EnsureNormalizedLocalVideoFile(string sourcePath, string cacheDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return sourcePath;
            }

            if (!IsFragmentedMp4(sourcePath))
            {
                return sourcePath;
            }

            var info = new FileInfo(sourcePath);
            string fingerprint = $"{Path.GetFullPath(sourcePath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..24].ToLowerInvariant();
            Directory.CreateDirectory(cacheDirectory);
            string normalizedPath = Path.Combine(cacheDirectory, $"canvas_v2_local_{hash}.mp4");

            if (File.Exists(normalizedPath) && new FileInfo(normalizedPath).Length > 16_384)
            {
                return normalizedPath;
            }

            if (TryFlattenFragmentedMp4(sourcePath, normalizedPath) &&
                File.Exists(normalizedPath) &&
                new FileInfo(normalizedPath).Length > 16_384)
            {
                return normalizedPath;
            }
        }
        catch
        {
            // Fall back to sourcePath on IO errors
        }

        return sourcePath;
    }

    /// <summary>
    /// Defragments an Apple Music fMP4 file into a standard flat fast-start MP4 at <paramref name="outputPath"/>
    /// by streaming <c>mdat</c> sample payloads directly between <see cref="FileStream"/> instances without
    /// buffering the entire video file on the Large Object Heap.
    /// Returns <c>true</c> if the file was successfully flattened; returns <c>false</c> if the input is
    /// already a flat MP4 or cannot be flattened safely.
    /// </summary>
    public static bool TryFlattenFragmentedMp4(string inputPath, string outputPath)
    {
        string tempOut = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var inputFs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long totalFileLength = inputFs.Length;
            if (totalFileLength < 64)
            {
                return false;
            }

            // 1. Scan top-level boxes via FileStream without loading mdat into RAM
            long moovFileStart = -1;
            int moovHeaderLen = 8;
            int moovBoxLen = 0;
            var moofRanges = new List<(long FileStart, int HeaderLen, int BoxLen)>();

            Span<byte> hdr = stackalloc byte[8];
            Span<byte> ext = stackalloc byte[8];
            long pos = 0;
            while (pos + 8 <= totalFileLength)
            {
                inputFs.Position = pos;
                if (!TryReadExact(inputFs, hdr))
                {
                    break;
                }

                uint size32 = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
                int boxHdrLen = 8;
                long boxSize;
                if (size32 == 1)
                {
                    if (pos + 16 > totalFileLength || !TryReadExact(inputFs, ext))
                    {
                        break;
                    }

                    ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(ext);
                    if (size64 < 16 || size64 > (ulong)(totalFileLength - pos))
                    {
                        break;
                    }

                    boxHdrLen = 16;
                    boxSize = (long)size64;
                }
                else if (size32 == 0)
                {
                    boxSize = totalFileLength - pos;
                }
                else if (size32 < 8)
                {
                    break;
                }
                else
                {
                    boxSize = size32;
                }

                long boxEnd = Math.Min(totalFileLength, pos + boxSize);
                long actualLen = boxEnd - pos;

                if (hdr[4] == (byte)'m' && hdr[5] == (byte)'o' && hdr[6] == (byte)'o' && hdr[7] == (byte)'v')
                {
                    if (moovFileStart < 0 && actualLen is >= 8 and <= 16 * 1024 * 1024)
                    {
                        moovFileStart = pos;
                        moovHeaderLen = boxHdrLen;
                        moovBoxLen = (int)actualLen;
                    }
                }
                else if (hdr[4] == (byte)'m' && hdr[5] == (byte)'o' && hdr[6] == (byte)'o' && hdr[7] == (byte)'f')
                {
                    if (actualLen is >= 8 and <= 16 * 1024 * 1024)
                    {
                        moofRanges.Add((pos, boxHdrLen, (int)actualLen));
                    }
                }

                pos = boxEnd;
            }

            if (moovFileStart < 0 || moofRanges.Count == 0)
            {
                return false;
            }

            // 2. Read ONLY the small moov metadata box and parse video track + trex defaults
            byte[] moovBuf = new byte[moovBoxLen];
            inputFs.Position = moovFileStart;
            if (!TryReadExact(inputFs, moovBuf.AsSpan(0, moovBoxLen)))
            {
                return false;
            }

            if (!TryParseMoovMetadata(
                moovBuf,
                moovHeaderLen,
                moovBoxLen,
                out uint videoTrackId,
                out uint mediaTimescale,
                out ushort mdhdLanguage,
                out byte[]? tkhdBytes,
                out byte[]? hdlrBytes,
                out byte[]? vmhdBytes,
                out byte[]? dinfBytes,
                out byte[]? stsdBytes,
                out uint trexDefaultDuration,
                out uint trexDefaultSize,
                out uint trexDefaultFlags))
            {
                return false;
            }

            // 3. Parse each small moof metadata box using a reusable rented buffer
            var samples = new List<SampleRecord>(512);
            var chunkSampleCounts = new List<int>(moofRanges.Count);
            long totalPayloadBytes = 0;
            ulong totalDurationTicks = 0;
            bool hasNonZeroCto = false;
            int minCto = 0;

            int maxMoofLen = 0;
            foreach (var r in moofRanges)
            {
                if (r.BoxLen > maxMoofLen)
                {
                    maxMoofLen = r.BoxLen;
                }
            }

            byte[] moofBuf = ArrayPool<byte>.Shared.Rent(Math.Max(4096, maxMoofLen));
            try
            {
                foreach (var (moofFileStart, moofHdrLen, moofLen) in moofRanges)
                {
                    inputFs.Position = moofFileStart;
                    if (!TryReadExact(inputFs, moofBuf.AsSpan(0, moofLen)))
                    {
                        return false;
                    }

                    int p = moofHdrLen;
                    while (p + 8 <= moofLen)
                    {
                        if (!TryReadBoxHeader(moofBuf, p, moofLen, out string tType, out int tHdr, out long tSize))
                        {
                            break;
                        }

                        int tEnd = (int)Math.Min(moofLen, p + tSize);
                        if (tType == "traf")
                        {
                            ParseTrafSamples(
                                moofBuf,
                                moofFileStart,
                                p + tHdr,
                                tEnd,
                                totalFileLength,
                                videoTrackId,
                                trexDefaultDuration,
                                trexDefaultSize,
                                trexDefaultFlags,
                                samples,
                                chunkSampleCounts,
                                ref totalPayloadBytes,
                                ref totalDurationTicks,
                                ref hasNonZeroCto,
                                ref minCto);
                        }

                        p = tEnd;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(moofBuf);
            }

            // 4. Build compact flat ftyp + moov + mdat header prefix
            byte[]? headerPrefix = BuildFlatHeaderPrefix(
                samples,
                chunkSampleCounts,
                totalPayloadBytes,
                totalDurationTicks,
                hasNonZeroCto,
                minCto,
                mediaTimescale,
                mdhdLanguage,
                tkhdBytes!,
                hdlrBytes!,
                vmhdBytes,
                dinfBytes,
                stsdBytes!);

            if (headerPrefix is null)
            {
                return false;
            }

            // 5. Stream flat header + mdat sample runs directly to output FileStream (zero full-video LOH allocation)
            byte[] copyBuf = ArrayPool<byte>.Shared.Rent(65_536);
            try
            {
                using (var outputFs = new FileStream(tempOut, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65_536))
                {
                    outputFs.Write(headerPrefix, 0, headerPrefix.Length);

                    int i = 0;
                    while (i < samples.Count)
                    {
                        long runStart = samples[i].SourceByteOffset;
                        long runLength = samples[i].Size;
                        i++;

                        // Coalesce contiguous sample ranges into a single sequential stream copy
                        while (i < samples.Count && samples[i].SourceByteOffset == runStart + runLength)
                        {
                            runLength += samples[i].Size;
                            i++;
                        }

                        inputFs.Position = runStart;
                        long remaining = runLength;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(remaining, copyBuf.Length);
                            int read = inputFs.Read(copyBuf, 0, toRead);
                            if (read <= 0)
                            {
                                return false;
                            }

                            outputFs.Write(copyBuf, 0, read);
                            remaining -= read;
                        }
                    }

                    outputFs.Flush();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(copyBuf);
            }

            File.Move(tempOut, outputPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (File.Exists(tempOut))
            {
                try { File.Delete(tempOut); } catch { }
            }
        }
    }

    private static bool TryReadExact(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = stream.Read(destination[total..]);
            if (n <= 0)
            {
                return false;
            }

            total += n;
        }

        return true;
    }

    /// <summary>
    /// Flattens an in-memory fMP4 byte array into a standard flat fast-start MP4 byte array.
    /// </summary>
    public static bool TryFlattenFragmentedMp4Bytes(byte[] src, out byte[]? output)
    {
        output = null;
        if (src.Length < 64)
        {
            return false;
        }

        // 1. Scan top-level boxes
        int moovStart = -1, moovHdrLen = 8, moovEnd = -1;
        var moofRanges = new List<(int Start, int HeaderLen, int End)>();

        int pos = 0;
        while (pos + 8 <= src.Length)
        {
            if (!TryReadBoxHeader(src, pos, src.Length, out string type, out int headerLen, out long boxSize))
            {
                break;
            }

            int boxEnd = (int)Math.Min(src.Length, pos + boxSize);
            if (type == "moov" && moovStart < 0)
            {
                moovStart = pos;
                moovHdrLen = headerLen;
                moovEnd = boxEnd;
            }
            else if (type == "moof")
            {
                moofRanges.Add((pos, headerLen, boxEnd));
            }

            pos = boxEnd;
        }

        if (moovStart < 0 || moofRanges.Count == 0)
        {
            // Already a flat MP4 or invalid
            return false;
        }

        // 2. Parse moov: extract video track metadata (tkhd, mdhd, hdlr, vmhd, dinf, stsd) and trex defaults
        if (!TryParseMoovMetadata(
            src,
            moovStart + moovHdrLen,
            moovEnd,
            out uint videoTrackId,
            out uint mediaTimescale,
            out ushort mdhdLanguage,
            out byte[]? tkhdBytes,
            out byte[]? hdlrBytes,
            out byte[]? vmhdBytes,
            out byte[]? dinfBytes,
            out byte[]? stsdBytes,
            out uint trexDefaultDuration,
            out uint trexDefaultSize,
            out uint trexDefaultFlags))
        {
            return false;
        }

        // 3. Parse every moof -> traf -> tfhd + trun and gather all video samples
        var samples = new List<SampleRecord>(512);
        var chunkSampleCounts = new List<int>(moofRanges.Count);
        long totalPayloadBytes = 0;
        ulong totalDurationTicks = 0;
        bool hasNonZeroCto = false;
        int minCto = 0;

        foreach (var (moofStart, moofHdrLen, moofEnd) in moofRanges)
        {
            int p = moofStart + moofHdrLen;
            while (p + 8 <= moofEnd)
            {
                if (!TryReadBoxHeader(src, p, moofEnd, out string tType, out int tHdr, out long tSize))
                {
                    break;
                }

                int tEnd = (int)Math.Min(moofEnd, p + tSize);
                if (tType == "traf")
                {
                    ParseTrafSamples(
                        src,
                        moofStart,
                        p + tHdr,
                        tEnd,
                        src.Length,
                        videoTrackId,
                        trexDefaultDuration,
                        trexDefaultSize,
                        trexDefaultFlags,
                        samples,
                        chunkSampleCounts,
                        ref totalPayloadBytes,
                        ref totalDurationTicks,
                        ref hasNonZeroCto,
                        ref minCto);
                }

                p = tEnd;
            }
        }

        // 4. Build standard flat MP4 header prefix (ftyp + moov + mdat header)
        byte[]? headerPrefix = BuildFlatHeaderPrefix(
            samples,
            chunkSampleCounts,
            totalPayloadBytes,
            totalDurationTicks,
            hasNonZeroCto,
            minCto,
            mediaTimescale,
            mdhdLanguage,
            tkhdBytes!,
            hdlrBytes!,
            vmhdBytes,
            dinfBytes,
            stsdBytes!);

        if (headerPrefix is null)
        {
            return false;
        }

        long totalFileLength = headerPrefix.Length + totalPayloadBytes;
        byte[] result = new byte[totalFileLength];
        Buffer.BlockCopy(headerPrefix, 0, result, 0, headerPrefix.Length);
        int w = headerPrefix.Length;

        foreach (var s in samples)
        {
            Buffer.BlockCopy(src, (int)s.SourceByteOffset, result, w, (int)s.Size);
            w += (int)s.Size;
        }

        output = result;
        return true;
    }

    private static bool TryParseMoovMetadata(
        byte[] src,
        int moovContentStart,
        int moovEnd,
        out uint videoTrackId,
        out uint mediaTimescale,
        out ushort mdhdLanguage,
        out byte[]? tkhdBytes,
        out byte[]? hdlrBytes,
        out byte[]? vmhdBytes,
        out byte[]? dinfBytes,
        out byte[]? stsdBytes,
        out uint trexDefaultDuration,
        out uint trexDefaultSize,
        out uint trexDefaultFlags)
    {
        videoTrackId = 0;
        mediaTimescale = 0;
        mdhdLanguage = 0x55C4; // 'und'
        tkhdBytes = null;
        hdlrBytes = null;
        vmhdBytes = null;
        dinfBytes = null;
        stsdBytes = null;
        trexDefaultDuration = 0;
        trexDefaultSize = 0;
        trexDefaultFlags = 0;

        int moovPos = moovContentStart;
        while (moovPos + 8 <= moovEnd)
        {
            if (!TryReadBoxHeader(src, moovPos, moovEnd, out string childType, out int childHdr, out long childSize))
            {
                break;
            }

            int childEnd = (int)Math.Min(moovEnd, moovPos + childSize);

            if (childType == "trak" && stsdBytes is null)
            {
                TryParseVideoTrack(
                    src,
                    moovPos + childHdr,
                    childEnd,
                    out uint parsedTrackId,
                    out uint parsedTimescale,
                    out ushort parsedLang,
                    out byte[]? parsedTkhd,
                    out byte[]? parsedHdlr,
                    out byte[]? parsedVmhd,
                    out byte[]? parsedDinf,
                    out byte[]? parsedStsd);

                if (parsedStsd is not null && parsedTkhd is not null && parsedHdlr is not null && parsedTimescale > 0)
                {
                    videoTrackId = parsedTrackId;
                    mediaTimescale = parsedTimescale;
                    mdhdLanguage = parsedLang;
                    tkhdBytes = parsedTkhd;
                    hdlrBytes = parsedHdlr;
                    vmhdBytes = parsedVmhd;
                    dinfBytes = parsedDinf;
                    stsdBytes = parsedStsd;
                }
            }
            else if (childType == "mvex")
            {
                int mvexPos = moovPos + childHdr;
                while (mvexPos + 8 <= childEnd)
                {
                    if (!TryReadBoxHeader(src, mvexPos, childEnd, out string mType, out int mHdr, out long mSize))
                    {
                        break;
                    }

                    int mEnd = (int)Math.Min(childEnd, mvexPos + mSize);
                    if (mType == "trex" && mEnd - (mvexPos + mHdr) >= 24)
                    {
                        int p = mvexPos + mHdr + 4; // skip version+flags
                        uint tId = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(p, 4));
                        if (videoTrackId == 0 || tId == videoTrackId)
                        {
                            trexDefaultDuration = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(p + 8, 4));
                            trexDefaultSize = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(p + 12, 4));
                            trexDefaultFlags = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(p + 16, 4));
                        }
                    }

                    mvexPos = mEnd;
                }
            }

            moovPos = childEnd;
        }

        return stsdBytes is not null && tkhdBytes is not null && hdlrBytes is not null && mediaTimescale > 0;
    }

    private static byte[]? BuildFlatHeaderPrefix(
        List<SampleRecord> samples,
        List<int> chunkSampleCounts,
        long totalPayloadBytes,
        ulong totalDurationTicks,
        bool hasNonZeroCto,
        int minCto,
        uint mediaTimescale,
        ushort mdhdLanguage,
        byte[] tkhdBytes,
        byte[] hdlrBytes,
        byte[]? vmhdBytes,
        byte[]? dinfBytes,
        byte[] stsdBytes)
    {
        if (samples.Count == 0 || totalPayloadBytes <= 0 || totalDurationTicks == 0)
        {
            return null;
        }

        if (totalDurationTicks > uint.MaxValue)
        {
            // Extremely long clip would need v1 mvhd/tkhd/mdhd; leave untouched
            return null;
        }

        uint finalDuration = (uint)totalDurationTicks;

        byte[] ftypBox = BuildFtypBox();
        byte[] mvhdBox = BuildMvhdBox(mediaTimescale, finalDuration);
        byte[] patchedTkhdBox = PatchTkhdDuration(tkhdBytes, finalDuration);
        byte[] mdhdBox = BuildMdhdBox(mediaTimescale, finalDuration, mdhdLanguage);
        byte[] vmhdBox = vmhdBytes ?? BuildDefaultVmhdBox();
        byte[] dinfBox = dinfBytes ?? BuildDefaultDinfBox();

        byte[] sttsBox = BuildSttsBox(samples);
        byte[]? cttsBox = hasNonZeroCto ? BuildCttsBox(samples, minCto) : null;
        byte[]? stssBox = BuildStssBox(samples);
        byte[] stscBox = BuildStscBox(chunkSampleCounts);
        byte[] stszBox = BuildStszBox(samples);

        int stcoSize = 8 + 4 + 4 + (chunkSampleCounts.Count * 4);
        int stblContentSize =
            stsdBytes.Length +
            sttsBox.Length +
            (cttsBox?.Length ?? 0) +
            (stssBox?.Length ?? 0) +
            stscBox.Length +
            stszBox.Length +
            stcoSize;
        int stblSize = 8 + stblContentSize;
        int minfSize = 8 + vmhdBox.Length + dinfBox.Length + stblSize;
        int mdiaSize = 8 + mdhdBox.Length + hdlrBytes.Length + minfSize;
        int trakSize = 8 + patchedTkhdBox.Length + mdiaSize;
        int moovSize = 8 + mvhdBox.Length + trakSize;

        long mdatStart = ftypBox.Length + moovSize;
        long mdatDataStart = mdatStart + 8;
        if (mdatDataStart + totalPayloadBytes > int.MaxValue - 16)
        {
            return null;
        }

        var chunks = new List<ChunkRecord>(chunkSampleCounts.Count);
        long runningOffset = mdatDataStart;
        int sampleCursor = 0;
        foreach (int countInChunk in chunkSampleCounts)
        {
            chunks.Add(new ChunkRecord(countInChunk, runningOffset));
            for (int i = 0; i < countInChunk; i++)
            {
                runningOffset += samples[sampleCursor++].Size;
            }
        }

        byte[] stcoBox = BuildStcoBox(chunks);

        byte[] headerPrefix = new byte[mdatDataStart];
        int w = 0;

        // ftyp
        Buffer.BlockCopy(ftypBox, 0, headerPrefix, w, ftypBox.Length);
        w += ftypBox.Length;

        // moov header
        WriteBoxHeader(headerPrefix, ref w, moovSize, "moov");
        Buffer.BlockCopy(mvhdBox, 0, headerPrefix, w, mvhdBox.Length);
        w += mvhdBox.Length;

        // trak header
        WriteBoxHeader(headerPrefix, ref w, trakSize, "trak");
        Buffer.BlockCopy(patchedTkhdBox, 0, headerPrefix, w, patchedTkhdBox.Length);
        w += patchedTkhdBox.Length;

        // mdia header
        WriteBoxHeader(headerPrefix, ref w, mdiaSize, "mdia");
        Buffer.BlockCopy(mdhdBox, 0, headerPrefix, w, mdhdBox.Length);
        w += mdhdBox.Length;
        Buffer.BlockCopy(hdlrBytes, 0, headerPrefix, w, hdlrBytes.Length);
        w += hdlrBytes.Length;

        // minf header
        WriteBoxHeader(headerPrefix, ref w, minfSize, "minf");
        Buffer.BlockCopy(vmhdBox, 0, headerPrefix, w, vmhdBox.Length);
        w += vmhdBox.Length;
        Buffer.BlockCopy(dinfBox, 0, headerPrefix, w, dinfBox.Length);
        w += dinfBox.Length;

        // stbl header
        WriteBoxHeader(headerPrefix, ref w, stblSize, "stbl");
        Buffer.BlockCopy(stsdBytes, 0, headerPrefix, w, stsdBytes.Length);
        w += stsdBytes.Length;
        Buffer.BlockCopy(sttsBox, 0, headerPrefix, w, sttsBox.Length);
        w += sttsBox.Length;
        if (cttsBox is not null)
        {
            Buffer.BlockCopy(cttsBox, 0, headerPrefix, w, cttsBox.Length);
            w += cttsBox.Length;
        }

        if (stssBox is not null)
        {
            Buffer.BlockCopy(stssBox, 0, headerPrefix, w, stssBox.Length);
            w += stssBox.Length;
        }

        Buffer.BlockCopy(stscBox, 0, headerPrefix, w, stscBox.Length);
        w += stscBox.Length;
        Buffer.BlockCopy(stszBox, 0, headerPrefix, w, stszBox.Length);
        w += stszBox.Length;
        Buffer.BlockCopy(stcoBox, 0, headerPrefix, w, stcoBox.Length);
        w += stcoBox.Length;

        // mdat box header (8 bytes)
        WriteBoxHeader(headerPrefix, ref w, (int)(8 + totalPayloadBytes), "mdat");
        return headerPrefix;
    }

    private static void TryParseVideoTrack(
        byte[] src,
        int start,
        int end,
        out uint trackId,
        out uint timescale,
        out ushort language,
        out byte[]? tkhd,
        out byte[]? hdlr,
        out byte[]? vmhd,
        out byte[]? dinf,
        out byte[]? stsd)
    {
        trackId = 0;
        timescale = 0;
        language = 0x55C4;
        tkhd = null;
        hdlr = null;
        vmhd = null;
        dinf = null;
        stsd = null;

        bool isVideo = false;
        int pos = start;
        while (pos + 8 <= end)
        {
            if (!TryReadBoxHeader(src, pos, end, out string type, out int hdr, out long size))
            {
                break;
            }

            int boxEnd = (int)Math.Min(end, pos + size);
            if (type == "tkhd")
            {
                tkhd = src[pos..boxEnd];
                int payload = pos + hdr;
                if (boxEnd - payload >= 24)
                {
                    byte ver = src[payload];
                    int trackIdOff = ver == 1 ? payload + 20 : payload + 12;
                    if (trackIdOff + 4 <= boxEnd)
                    {
                        trackId = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(trackIdOff, 4));
                    }
                }
            }
            else if (type == "mdia")
            {
                int mPos = pos + hdr;
                while (mPos + 8 <= boxEnd)
                {
                    if (!TryReadBoxHeader(src, mPos, boxEnd, out string mType, out int mHdr, out long mSize))
                    {
                        break;
                    }

                    int mEnd = (int)Math.Min(boxEnd, mPos + mSize);
                    if (mType == "mdhd")
                    {
                        int payload = mPos + mHdr;
                        if (mEnd - payload >= 24)
                        {
                            byte ver = src[payload];
                            if (ver == 1 && mEnd - payload >= 36)
                            {
                                timescale = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(payload + 20, 4));
                                language = BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(payload + 32, 2));
                            }
                            else if (ver == 0)
                            {
                                timescale = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(payload + 12, 4));
                                language = BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(payload + 20, 2));
                            }
                        }
                    }
                    else if (mType == "hdlr")
                    {
                        int payload = mPos + mHdr;
                        if (mEnd - payload >= 12)
                        {
                            string handlerType = Encoding.ASCII.GetString(src, payload + 8, 4);
                            if (handlerType == "vide")
                            {
                                isVideo = true;
                                hdlr = src[mPos..mEnd];
                            }
                        }
                    }
                    else if (mType == "minf")
                    {
                        int minfPos = mPos + mHdr;
                        while (minfPos + 8 <= mEnd)
                        {
                            if (!TryReadBoxHeader(src, minfPos, mEnd, out string iType, out int iHdr, out long iSize))
                            {
                                break;
                            }

                            int iEnd = (int)Math.Min(mEnd, minfPos + iSize);
                            if (iType == "vmhd")
                            {
                                vmhd = src[minfPos..iEnd];
                            }
                            else if (iType == "dinf")
                            {
                                dinf = src[minfPos..iEnd];
                            }
                            else if (iType == "stbl")
                            {
                                int stblPos = minfPos + iHdr;
                                while (stblPos + 8 <= iEnd)
                                {
                                    if (!TryReadBoxHeader(src, stblPos, iEnd, out string sType, out _, out long sSize))
                                    {
                                        break;
                                    }

                                    int sEnd = (int)Math.Min(iEnd, stblPos + sSize);
                                    if (sType == "stsd")
                                    {
                                        stsd = src[stblPos..sEnd];
                                    }

                                    stblPos = sEnd;
                                }
                            }

                            minfPos = iEnd;
                        }
                    }

                    mPos = mEnd;
                }
            }

            pos = boxEnd;
        }

        if (!isVideo)
        {
            stsd = null;
        }
    }

    private static void ParseTrafSamples(
        byte[] src,
        long moofStart,
        int trafStart,
        int trafEnd,
        long totalSourceLength,
        uint targetTrackId,
        uint trexDefaultDuration,
        uint trexDefaultSize,
        uint trexDefaultFlags,
        List<SampleRecord> samples,
        List<int> chunkSampleCounts,
        ref long totalPayloadBytes,
        ref ulong totalDurationTicks,
        ref bool hasNonZeroCto,
        ref int minCto)
    {
        uint trafTrackId = targetTrackId;
        long baseDataOffset = moofStart;
        uint defaultDur = trexDefaultDuration;
        uint defaultSize = trexDefaultSize;
        uint defaultFlags = trexDefaultFlags;
        var trunRanges = new List<(int PayloadStart, int End)>();

        int pos = trafStart;
        while (pos + 8 <= trafEnd)
        {
            if (!TryReadBoxHeader(src, pos, trafEnd, out string type, out int hdr, out long size))
            {
                break;
            }

            int boxEnd = (int)Math.Min(trafEnd, pos + size);
            int payload = pos + hdr;

            if (type == "tfhd" && boxEnd - payload >= 8)
            {
                uint tfFlags = (uint)((src[payload + 1] << 16) | (src[payload + 2] << 8) | src[payload + 3]);
                trafTrackId = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(payload + 4, 4));
                int cur = payload + 8;

                if ((tfFlags & 0x000001) != 0 && cur + 8 <= boxEnd)
                {
                    baseDataOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(src.AsSpan(cur, 8));
                    cur += 8;
                }

                if ((tfFlags & 0x000002) != 0 && cur + 4 <= boxEnd)
                {
                    cur += 4; // sample_description_index
                }

                if ((tfFlags & 0x000008) != 0 && cur + 4 <= boxEnd)
                {
                    defaultDur = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(cur, 4));
                    cur += 4;
                }

                if ((tfFlags & 0x000010) != 0 && cur + 4 <= boxEnd)
                {
                    defaultSize = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(cur, 4));
                    cur += 4;
                }

                if ((tfFlags & 0x000020) != 0 && cur + 4 <= boxEnd)
                {
                    defaultFlags = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(cur, 4));
                }
            }
            else if (type == "trun")
            {
                trunRanges.Add((payload, boxEnd));
            }

            pos = boxEnd;
        }

        if (targetTrackId != 0 && trafTrackId != targetTrackId)
        {
            return;
        }

        long runningDataOffset = baseDataOffset;
        foreach (var (payload, boxEnd) in trunRanges)
        {
            if (boxEnd - payload < 8)
            {
                continue;
            }

            byte version = src[payload];
            uint trFlags = (uint)((src[payload + 1] << 16) | (src[payload + 2] << 8) | src[payload + 3]);
            uint sampleCount = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(payload + 4, 4));
            if (sampleCount == 0 || sampleCount > 200_000)
            {
                continue;
            }

            int cur = payload + 8;
            long dataOffset = runningDataOffset;
            if ((trFlags & 0x000001) != 0 && cur + 4 <= boxEnd)
            {
                int relOffset = BinaryPrimitives.ReadInt32BigEndian(src.AsSpan(cur, 4));
                dataOffset = baseDataOffset + relOffset;
                cur += 4;
            }

            bool hasFirstSampleFlags = (trFlags & 0x000004) != 0;
            uint firstSampleFlags = defaultFlags;
            if (hasFirstSampleFlags && cur + 4 <= boxEnd)
            {
                firstSampleFlags = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(cur, 4));
                cur += 4;
            }

            bool hasDur = (trFlags & 0x000100) != 0;
            bool hasSize = (trFlags & 0x000200) != 0;
            bool hasFlags = (trFlags & 0x000400) != 0;
            bool hasCto = (trFlags & 0x000800) != 0;

            int addedInRun = 0;
            long sampleBytePos = dataOffset;

            for (uint i = 0; i < sampleCount; i++)
            {
                uint sDur = defaultDur;
                uint sSize = defaultSize;
                uint sFlags = (i == 0 && hasFirstSampleFlags) ? firstSampleFlags : defaultFlags;
                int sCto = 0;

                if (hasDur)
                {
                    if (cur + 4 > boxEnd) break;
                    sDur = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(cur, 4));
                    cur += 4;
                }

                if (hasSize)
                {
                    if (cur + 4 > boxEnd) break;
                    sSize = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(cur, 4));
                    cur += 4;
                }

                if (hasFlags)
                {
                    if (cur + 4 > boxEnd) break;
                    sFlags = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(cur, 4));
                    cur += 4;
                }

                if (hasCto)
                {
                    if (cur + 4 > boxEnd) break;
                    sCto = version == 0
                        ? (int)BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(cur, 4))
                        : BinaryPrimitives.ReadInt32BigEndian(src.AsSpan(cur, 4));
                    cur += 4;
                }

                if (sDur == 0)
                {
                    sDur = 1;
                }

                if (sampleBytePos < 0 || sampleBytePos + sSize > totalSourceLength)
                {
                    break;
                }

                bool isSync = (sFlags & 0x00010000) == 0;
                if (sCto != 0)
                {
                    hasNonZeroCto = true;
                    if (sCto < minCto)
                    {
                        minCto = sCto;
                    }
                }

                samples.Add(new SampleRecord(sDur, sSize, sCto, isSync, sampleBytePos));
                sampleBytePos += sSize;
                totalPayloadBytes += sSize;
                totalDurationTicks += sDur;
                addedInRun++;
            }

            if (addedInRun > 0)
            {
                chunkSampleCounts.Add(addedInRun);
                runningDataOffset = sampleBytePos;
            }
        }
    }

    private static bool TryReadBoxHeader(
        byte[] data,
        int offset,
        int limit,
        out string type,
        out int headerLen,
        out long boxSize)
    {
        type = string.Empty;
        headerLen = 8;
        boxSize = 0;

        if (offset < 0 || offset + 8 > limit)
        {
            return false;
        }

        uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        type = Encoding.ASCII.GetString(data, offset + 4, 4);

        if (size32 == 1)
        {
            if (offset + 16 > limit)
            {
                return false;
            }

            ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
            if (size64 < 16 || size64 > (ulong)(limit - offset))
            {
                return false;
            }

            headerLen = 16;
            boxSize = (long)size64;
            return true;
        }

        if (size32 == 0)
        {
            boxSize = limit - offset;
            return boxSize >= 8;
        }

        if (size32 < 8)
        {
            return false;
        }

        boxSize = size32;
        return true;
    }

    private static void WriteBoxHeader(byte[] dest, ref int offset, int size, string type)
    {
        BinaryPrimitives.WriteUInt32BigEndian(dest.AsSpan(offset, 4), (uint)size);
        dest[offset + 4] = (byte)type[0];
        dest[offset + 5] = (byte)type[1];
        dest[offset + 6] = (byte)type[2];
        dest[offset + 7] = (byte)type[3];
        offset += 8;
    }

    private static byte[] BuildFtypBox()
    {
        byte[] buf = new byte[32];
        int w = 0;
        WriteBoxHeader(buf, ref w, buf.Length, "ftyp");
        Encoding.ASCII.GetBytes("isom", buf.AsSpan(w, 4)); w += 4;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), 0x00000200); w += 4;
        Encoding.ASCII.GetBytes("isom", buf.AsSpan(w, 4)); w += 4;
        Encoding.ASCII.GetBytes("iso2", buf.AsSpan(w, 4)); w += 4;
        Encoding.ASCII.GetBytes("avc1", buf.AsSpan(w, 4)); w += 4;
        Encoding.ASCII.GetBytes("mp41", buf.AsSpan(w, 4));
        return buf;
    }

    private static byte[] BuildMvhdBox(uint timescale, uint duration)
    {
        byte[] buf = new byte[108];
        int w = 0;
        WriteBoxHeader(buf, ref w, buf.Length, "mvhd");
        // version=0, flags=0 (4), creation=0 (4), modification=0 (4) -> w + 12
        w += 12;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), timescale); w += 4;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), duration); w += 4;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), 0x00010000); w += 4; // rate = 1.0
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(w, 2), 0x0100); w += 2; // volume = 1.0
        w += 10; // reserved
        // unity 3x3 matrix
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), 0x00010000); w += 16;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), 0x00010000); w += 16;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), 0x40000000); w += 4;
        w += 24; // pre_defined
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), 2); // next_track_ID
        return buf;
    }

    private static byte[] PatchTkhdDuration(byte[] originalTkhd, uint duration)
    {
        byte[] clone = (byte[])originalTkhd.Clone();
        if (clone.Length >= 32)
        {
            byte ver = clone[8];
            // Ensure track enabled + in movie + in preview flags (0x000003)
            clone[11] = (byte)(clone[11] | 0x03);
            if (ver == 1 && clone.Length >= 44)
            {
                BinaryPrimitives.WriteUInt64BigEndian(clone.AsSpan(36, 8), duration);
            }
            else if (ver == 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(clone.AsSpan(28, 4), duration);
            }
        }

        return clone;
    }

    private static byte[] BuildMdhdBox(uint timescale, uint duration, ushort language)
    {
        byte[] buf = new byte[32];
        int w = 0;
        WriteBoxHeader(buf, ref w, buf.Length, "mdhd");
        w += 12; // version(1)+flags(3)+creation(4)+modification(4)
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), timescale); w += 4;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), duration); w += 4;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(w, 2), language);
        return buf;
    }

    private static byte[] BuildDefaultVmhdBox()
    {
        byte[] buf = new byte[20];
        int w = 0;
        WriteBoxHeader(buf, ref w, buf.Length, "vmhd");
        buf[11] = 1; // flags = 1
        return buf;
    }

    private static byte[] BuildDefaultDinfBox()
    {
        byte[] buf = new byte[36];
        int w = 0;
        WriteBoxHeader(buf, ref w, 36, "dinf");
        WriteBoxHeader(buf, ref w, 28, "dref");
        w += 4; // ver+flags
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), 1); w += 4;
        WriteBoxHeader(buf, ref w, 12, "url ");
        buf[w + 3] = 1; // self-contained flag
        return buf;
    }

    private static byte[] BuildSttsBox(List<SampleRecord> samples)
    {
        var runs = new List<(uint Count, uint Delta)>();
        uint curDelta = samples[0].Duration;
        uint curCount = 1;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].Duration == curDelta)
            {
                curCount++;
            }
            else
            {
                runs.Add((curCount, curDelta));
                curDelta = samples[i].Duration;
                curCount = 1;
            }
        }

        runs.Add((curCount, curDelta));

        int size = 8 + 4 + 4 + (runs.Count * 8);
        byte[] buf = new byte[size];
        int w = 0;
        WriteBoxHeader(buf, ref w, size, "stts");
        w += 4; // ver+flags
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), (uint)runs.Count); w += 4;
        foreach (var (count, delta) in runs)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), count); w += 4;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), delta); w += 4;
        }

        return buf;
    }

    private static byte[] BuildCttsBox(List<SampleRecord> samples, int minCto)
    {
        // Use ctts version 0 with non-negative bias if minCto < 0 OR version 1 signed directly.
        // Shifting by -minCto produces universally compatible ctts v0 (unsigned offsets >= 0) across all Windows Media Foundation builds.
        int bias = minCto < 0 ? -minCto : 0;

        var runs = new List<(uint Count, uint Offset)>();
        uint curVal = (uint)(samples[0].CompositionOffset + bias);
        uint curCount = 1;
        for (int i = 1; i < samples.Count; i++)
        {
            uint val = (uint)(samples[i].CompositionOffset + bias);
            if (val == curVal)
            {
                curCount++;
            }
            else
            {
                runs.Add((curCount, curVal));
                curVal = val;
                curCount = 1;
            }
        }

        runs.Add((curCount, curVal));

        int size = 8 + 4 + 4 + (runs.Count * 8);
        byte[] buf = new byte[size];
        int w = 0;
        WriteBoxHeader(buf, ref w, size, "ctts");
        w += 4; // version=0, flags=0
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), (uint)runs.Count); w += 4;
        foreach (var (count, off) in runs)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), count); w += 4;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), off); w += 4;
        }

        return buf;
    }

    private static byte[]? BuildStssBox(List<SampleRecord> samples)
    {
        var syncIndices = new List<uint>();
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].IsSync)
            {
                syncIndices.Add((uint)(i + 1)); // 1-based
            }
        }

        if (syncIndices.Count == 0 || syncIndices.Count == samples.Count)
        {
            return null;
        }

        int size = 8 + 4 + 4 + (syncIndices.Count * 4);
        byte[] buf = new byte[size];
        int w = 0;
        WriteBoxHeader(buf, ref w, size, "stss");
        w += 4; // ver+flags
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), (uint)syncIndices.Count); w += 4;
        foreach (uint idx in syncIndices)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), idx); w += 4;
        }

        return buf;
    }

    private static byte[] BuildStscBox(List<int> chunkSampleCounts)
    {
        // Compress consecutive chunks with identical sample counts
        var entries = new List<(uint FirstChunk, uint SamplesPerChunk)>();
        int prevCount = -1;
        for (int i = 0; i < chunkSampleCounts.Count; i++)
        {
            int count = chunkSampleCounts[i];
            if (count != prevCount)
            {
                entries.Add(((uint)(i + 1), (uint)count));
                prevCount = count;
            }
        }

        int size = 8 + 4 + 4 + (entries.Count * 12);
        byte[] buf = new byte[size];
        int w = 0;
        WriteBoxHeader(buf, ref w, size, "stsc");
        w += 4; // ver+flags
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), (uint)entries.Count); w += 4;
        foreach (var (firstChunk, spc) in entries)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), firstChunk); w += 4;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), spc); w += 4;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), 1); w += 4; // sample_description_index
        }

        return buf;
    }

    private static byte[] BuildStszBox(List<SampleRecord> samples)
    {
        int size = 8 + 4 + 4 + 4 + (samples.Count * 4);
        byte[] buf = new byte[size];
        int w = 0;
        WriteBoxHeader(buf, ref w, size, "stsz");
        w += 4; // ver+flags
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), 0); w += 4; // variable sample_size
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), (uint)samples.Count); w += 4;
        foreach (var s in samples)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), s.Size); w += 4;
        }

        return buf;
    }

    private static byte[] BuildStcoBox(List<ChunkRecord> chunks)
    {
        int size = 8 + 4 + 4 + (chunks.Count * 4);
        byte[] buf = new byte[size];
        int w = 0;
        WriteBoxHeader(buf, ref w, size, "stco");
        w += 4; // ver+flags
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), (uint)chunks.Count); w += 4;
        foreach (var chunk in chunks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(w, 4), (uint)chunk.OutputDataOffset); w += 4;
        }

        return buf;
    }
}
