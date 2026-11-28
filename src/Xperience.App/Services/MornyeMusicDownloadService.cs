using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Windows.Media.Transcoding;
using Windows.Storage;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;
using Xperience.Core.Services;

namespace Xperience.App.Services;

/// <summary>
/// Implements multi-quality track downloading inspired by <c>mornyeapp/Mornye-Python</c> and <c>mornyeapp/Mornye-Desktop</c>.
/// Combines Tidal, Qobuz, and Deezer lossless metadata/stream resolution with YouTube Music audio extraction,
/// hardware-accelerated FLAC/MP3 encoding, TagLibSharp metadata + HD artwork embedding, synchronized <c>.lrc</c> lyrics,
/// and offline Apple Music Canvas (<c>.canvas.mp4</c>) sidecar preservation.
/// </summary>
public sealed class MornyeMusicDownloadService : IMusicDownloadService
{
    private const string PersistedOptionsKey = "DownloadMetadataOptions_v1";
    private const string MornyeRegistryGistUrl = "https://gist.githubusercontent.com/BartolomeoRusso9/ef9fdbbc894818aea89d25a8d99f8c77/raw";
    private const string TidalApiBaseUrl = "https://api.tidal.com/v1";
    private static readonly string[] TidalPublicTokens = ["CzET4vdadNUFQ5JU", "49YxDN9a2aFV6RTG"];
    private const string QobuzApiBaseUrl = "https://www.qobuz.com/api.json/0.2";
    private const string QobuzPublicAppId = "735532640";
    private const string DeezerApiBaseUrl = "https://api.deezer.com";

    private static readonly Regex NonWordRegex = new(@"\W+", RegexOptions.Compiled);
    private static readonly Regex TitleNoiseRegex = new(
        @"\s*[\(\[][^\)\]]*(?:official|audio|video|lyric|lyrics|visualizer|remaster|remastered|deluxe|bonus|hd|hq|4k|explicit|clean)[^\)\]]*[\)\]]\s*|\s+-\s+(?:[^-]*\b(?:remaster|remastered|mono|stereo|deluxe|bonus|version)\b.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingCreditRegex = new(
        @"\s*[\(\[]?\b(?:feat|ft|featuring|with)\.?\s+[^\)\]]+[\)\]]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DifferentRecordingRegex = new(
        @"\b(live|remix|mix|edit|instrumental|acoustic|demo|karaoke|a cappella|acapella|reprise|radio edit|extended|slowed|sped up|sped|nightcore|8d|clean version|clean edit|censored|tribute|backing track|cover)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArtistSplitRegex = new(
        @"\s+(?:feat\.?|ft\.?|featuring|with|vs\.?|and|x)\s+|[,;&/]|\s+\+\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IStorageService _storageService;
    private readonly IYouTubeMusicService _youTubeMusicService;
    private readonly ILyricsProviderService _lyricsProviderService;
    private readonly ICanvasArtworkService _canvasArtworkService;
    private readonly IMediaCatalogService _mediaCatalogService;
    private readonly IDataUsageService _dataUsageService;
    private readonly ILogger<MornyeMusicDownloadService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _flacStreamCacheDir;
    private bool _isInitialized;

    /// <inheritdoc />
    public DownloadMetadataOptions Options { get; private set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MornyeMusicDownloadService"/> class.
    /// </summary>
    public MornyeMusicDownloadService(
        IStorageService storageService,
        IYouTubeMusicService youTubeMusicService,
        ILyricsProviderService lyricsProviderService,
        ICanvasArtworkService canvasArtworkService,
        IMediaCatalogService mediaCatalogService,
        IDataUsageService dataUsageService,
        ILogger<MornyeMusicDownloadService> logger)
    {
        _storageService = storageService;
        _youTubeMusicService = youTubeMusicService;
        _lyricsProviderService = lyricsProviderService;
        _canvasArtworkService = canvasArtworkService;
        _mediaCatalogService = mediaCatalogService;
        _dataUsageService = dataUsageService;
        _logger = logger;

        _flacStreamCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XperienceMusicPlayer",
            "FlacStreamCache");
        Directory.CreateDirectory(_flacStreamCacheDir);

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");

        string defaultMusicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrWhiteSpace(defaultMusicFolder))
        {
            defaultMusicFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XperienceMusicPlayer");
        }

        Options.DownloadDirectory = Path.Combine(defaultMusicFolder, "Xperience Downloads");
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        try
        {
            var saved = await _storageService.LoadAsync<DownloadMetadataOptions>(
                PersistedOptionsKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);

            if (saved is not null)
            {
                if (string.IsNullOrWhiteSpace(saved.DownloadDirectory))
                {
                    saved.DownloadDirectory = Options.DownloadDirectory;
                }

                Options = saved;
            }

            Directory.CreateDirectory(Options.DownloadDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted download options; using defaults.");
        }

        _isInitialized = true;
    }

    /// <inheritdoc />
    public async Task SaveOptionsAsync(DownloadMetadataOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DownloadDirectory))
        {
            options.DownloadDirectory = Options.DownloadDirectory;
        }

        Options = options;
        try
        {
            Directory.CreateDirectory(Options.DownloadDirectory);
            await _storageService.SaveAsync(PersistedOptionsKey, Options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist download options.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackDownloadCandidate>> SearchDownloadCandidatesAsync(
        TrackItem track,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        string expectedTitle = track.Title ?? string.Empty;
        string expectedArtist = track.Artist ?? string.Empty;
        string expectedAlbum = track.Album ?? string.Empty;
        double expectedDurationSec = track.Duration.TotalSeconds;
        string searchQuery = $"{expectedArtist} {expectedTitle}".Trim();

        var tidalCandidatesTask = SearchTidalCandidatesAsync(searchQuery, expectedTitle, expectedArtist, expectedAlbum, expectedDurationSec, cancellationToken);
        var deezerCandidatesTask = SearchDeezerCandidatesAsync(searchQuery, expectedTitle, expectedArtist, expectedAlbum, expectedDurationSec, cancellationToken);
        var gdStudioCandidatesTask = SearchGdStudioCandidatesAsync(searchQuery, expectedTitle, expectedArtist, expectedAlbum, expectedDurationSec, cancellationToken);
        var ytCandidatesTask = SearchYouTubeCandidatesAsync(track, searchQuery, expectedTitle, expectedArtist, expectedAlbum, expectedDurationSec, cancellationToken);

        await Task.WhenAll(tidalCandidatesTask, deezerCandidatesTask, gdStudioCandidatesTask, ytCandidatesTask).ConfigureAwait(false);

        var tidalList = await tidalCandidatesTask.ConfigureAwait(false);
        var deezerList = await deezerCandidatesTask.ConfigureAwait(false);
        var gdList = await gdStudioCandidatesTask.ConfigureAwait(false);
        var ytList = await ytCandidatesTask.ConfigureAwait(false);

        var merged = new List<TrackDownloadCandidate>();

        // 1. Build Primary Mornye Enriched Candidate combining best Tidal/Deezer Lossless metadata + verified audio stream
        var bestTidal = tidalList.FirstOrDefault(c => !HasVariantConflict(expectedTitle, c.Title) && TitlesMatchStrict(expectedTitle, c.Title));
        var bestDeezer = deezerList.FirstOrDefault(c => !HasVariantConflict(expectedTitle, c.Title) && TitlesMatchStrict(expectedTitle, c.Title));
        var bestGd = gdList.FirstOrDefault(c => !HasVariantConflict(expectedTitle, c.Title) && TitlesMatchStrict(expectedTitle, c.Title));
        var bestYt = ytList.FirstOrDefault(c => !HasVariantConflict(expectedTitle, c.Title) && TitlesMatchStrict(expectedTitle, c.Title)) ?? ytList.FirstOrDefault();

        string primaryTitle = bestTidal?.Title ?? bestDeezer?.Title ?? track.Title;
        string primaryArtist = bestTidal?.Artist ?? bestDeezer?.Artist ?? track.Artist;
        string primaryAlbum = !string.IsNullOrWhiteSpace(bestTidal?.Album)
            ? bestTidal!.Album
            : (!string.IsNullOrWhiteSpace(bestDeezer?.Album) ? bestDeezer!.Album : (string.IsNullOrWhiteSpace(track.Album) ? track.Title : track.Album));
        string primaryIsrc = !string.IsNullOrWhiteSpace(bestTidal?.Isrc)
            ? bestTidal!.Isrc
            : (bestDeezer?.Isrc ?? string.Empty);
        string primaryCover = !string.IsNullOrWhiteSpace(bestTidal?.CoverArtUri)
            ? bestTidal!.CoverArtUri
            : (!string.IsNullOrWhiteSpace(bestDeezer?.CoverArtUri) ? bestDeezer!.CoverArtUri : track.CoverArtUri);
        TimeSpan primaryDuration = bestTidal?.Duration.TotalSeconds > 0
            ? bestTidal.Duration
            : (bestDeezer?.Duration.TotalSeconds > 0 ? bestDeezer.Duration : track.Duration);

        var providerParts = new List<string>();
        if (bestTidal is not null) providerParts.Add("Tidal Hi-Fi");
        if (bestDeezer is not null) providerParts.Add("Deezer");
        if (bestGd is not null) providerParts.Add("GDStudio FLAC");
        if (providerParts.Count == 0) providerParts.Add("YouTube Music");

        int bestScore = Math.Max(
            bestTidal?.MatchScorePercent ?? 0,
            Math.Max(bestDeezer?.MatchScorePercent ?? 0, Math.Max(bestGd?.MatchScorePercent ?? 0, 96)));

        merged.Add(new TrackDownloadCandidate
        {
            Title = primaryTitle,
            Artist = primaryArtist,
            Album = primaryAlbum,
            Duration = primaryDuration,
            CoverArtUri = primaryCover,
            Isrc = primaryIsrc,
            Copyright = bestTidal?.Copyright ?? string.Empty,
            ReplayGain = bestTidal?.ReplayGain ?? string.Empty,
            PeakAmplitude = bestTidal?.PeakAmplitude ?? string.Empty,
            Genre = bestDeezer?.Genre ?? (string.IsNullOrWhiteSpace(track.Genre) ? "Pop" : track.Genre),
            ProviderLabel = string.Join(" + ", providerParts),
            QualityBadge = bestGd is not null ? "Direct 24-bit/16-bit FLAC" : "FLAC 16-bit Lossless",
            VersionBadge = DetectVersionBadge(primaryTitle),
            MatchScorePercent = Math.Clamp(bestScore, 92, 100),
            IsRecommended = true,
            YouTubeVideoId = !string.IsNullOrWhiteSpace(track.YouTubeVideoId) ? track.YouTubeVideoId : bestYt?.YouTubeVideoId,
            TidalTrackId = bestTidal?.TidalTrackId,
            DeezerTrackId = bestDeezer?.DeezerTrackId,
            GdStudioSource = bestGd?.GdStudioSource,
            GdStudioUrlId = bestGd?.GdStudioUrlId,
            ReleaseYear = bestDeezer?.ReleaseYear ?? (track.ReleaseYear > 0 ? track.ReleaseYear : DateTime.Now.Year),
            TrackNumber = bestTidal?.TrackNumber ?? bestDeezer?.TrackNumber ?? (track.TrackNumber > 0 ? track.TrackNumber : 1u),
            DiscNumber = bestTidal?.DiscNumber ?? bestDeezer?.DiscNumber ?? 1u
        });

        // 2. Append Distinct Alternative Versions / Suggestions from Tidal, Deezer, GDStudio, and YouTube Music
        var seenSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"{FoldText(primaryTitle)}|{FoldText(primaryAlbum)}|{(int)Math.Round(primaryDuration.TotalSeconds / 4.0)}"
        };

        foreach (var cand in tidalList.Concat(deezerList).Concat(gdList).Concat(ytList).OrderByDescending(c => c.MatchScorePercent))
        {
            string sig = $"{FoldText(cand.Title)}|{FoldText(cand.Album)}|{(int)Math.Round(cand.Duration.TotalSeconds / 4.0)}|{cand.ProviderLabel}";
            string coreSig = $"{FoldText(cand.Title)}|{FoldText(cand.Album)}|{(int)Math.Round(cand.Duration.TotalSeconds / 4.0)}";

            if (seenSignatures.Contains(sig))
            {
                continue;
            }

            // Include if it's an alternative version/album/duration OR from a distinct provider source
            if (!seenSignatures.Contains(coreSig) || merged.Count < 6)
            {
                var candidateToAdd = cand;
                candidateToAdd.IsRecommended = false;
                if (string.IsNullOrWhiteSpace(cand.YouTubeVideoId))
                {
                    // Pair with matching YouTube video ID if it's a metadata-only provider entry
                    var matchingYt = ytList.FirstOrDefault(y => TitlesMatchStrict(cand.Title, y.Title)) ?? bestYt;
                    candidateToAdd = new TrackDownloadCandidate
                    {
                        Id = cand.Id,
                        Title = cand.Title,
                        Artist = cand.Artist,
                        Album = cand.Album,
                        Duration = cand.Duration,
                        CoverArtUri = string.IsNullOrWhiteSpace(cand.CoverArtUri) ? primaryCover : cand.CoverArtUri,
                        Isrc = cand.Isrc,
                        Copyright = cand.Copyright,
                        ReplayGain = cand.ReplayGain,
                        PeakAmplitude = cand.PeakAmplitude,
                        Genre = cand.Genre,
                        ProviderLabel = cand.ProviderLabel,
                        QualityBadge = cand.QualityBadge,
                        VersionBadge = cand.VersionBadge,
                        MatchScorePercent = cand.MatchScorePercent,
                        IsRecommended = false,
                        YouTubeVideoId = matchingYt?.YouTubeVideoId ?? track.YouTubeVideoId,
                        TidalTrackId = cand.TidalTrackId,
                        DeezerTrackId = cand.DeezerTrackId,
                        GdStudioSource = cand.GdStudioSource,
                        GdStudioUrlId = cand.GdStudioUrlId,
                        ReleaseYear = cand.ReleaseYear,
                        TrackNumber = cand.TrackNumber,
                        DiscNumber = cand.DiscNumber
                    };
                }

                seenSignatures.Add(sig);
                seenSignatures.Add(coreSig);
                merged.Add(candidateToAdd);

                if (merged.Count >= 8)
                {
                    break;
                }
            }
        }

        return merged;
    }

    /// <inheritdoc />
    public async Task<TrackDownloadResult> DownloadTrackAsync(
        TrackItem track,
        DownloadAudioFormat? formatOverride = null,
        IProgress<string>? progressStatus = null,
        CancellationToken cancellationToken = default,
        TrackDownloadCandidate? selectedCandidate = null,
        IProgress<TrackDownloadProgressReport>? detailedProgress = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        void ReportStage(double percent, string status, string badgeText = "")
        {
            progressStatus?.Report(status);
            detailedProgress?.Report(new TrackDownloadProgressReport(percent, status, badgeText));
        }

        DownloadAudioFormat targetFormat = formatOverride ?? Options.DefaultFormat;
        string downloadDir = Options.DownloadDirectory;
        Directory.CreateDirectory(downloadDir);

        string effectiveArtistName = !string.IsNullOrWhiteSpace(selectedCandidate?.Artist) ? selectedCandidate!.Artist : track.Artist;
        string effectiveTitleName = !string.IsNullOrWhiteSpace(selectedCandidate?.Title) ? selectedCandidate!.Title : track.Title;

        string safeArtist = SanitizeFileName(string.IsNullOrWhiteSpace(effectiveArtistName) ? "Unknown Artist" : effectiveArtistName);
        string safeTitle = SanitizeFileName(string.IsNullOrWhiteSpace(effectiveTitleName) ? "Track" : effectiveTitleName);
        string baseFileName = $"{safeArtist} - {safeTitle}";

        string targetExt = targetFormat switch
        {
            DownloadAudioFormat.FlacLossless => ".flac",
            DownloadAudioFormat.Mp3MaxQuality => ".mp3",
            _ => ".m4a"
        };

        string finalAudioPath = Path.Combine(downloadDir, $"{baseFileName}{targetExt}");
        string? lrcSidecarPath = null;
        string? canvasSidecarPath = null;

        try
        {
            // STEP 1: Enrich Metadata via Mornye Pipeline (Tidal + Qobuz + Deezer) in parallel
            ReportStage(8.0, $"Verifying track identity & ISRC on Mornye (Tidal / Deezer) for '{effectiveTitleName}'...", "MORNYE METADATA");
            var enrichedMeta = await ResolveMornyeMetadataAsync(track, selectedCandidate, cancellationToken).ConfigureAwait(false);

            // STEP 2: Resolve or Download Audio Stream
            string providerSummary = enrichedMeta.ProviderSource;
            bool directFlacDownloaded = false;

            if (targetFormat == DownloadAudioFormat.FlacLossless)
            {
                ReportStage(22.0, $"Searching for verified FLAC Lossless stream for '{enrichedMeta.Title}'...", "FLAC LOSSLESS");
                directFlacDownloaded = await TryDownloadDirectLosslessFlacAsync(
                    enrichedMeta,
                    finalAudioPath,
                    cancellationToken).ConfigureAwait(false);

                if (directFlacDownloaded)
                {
                    providerSummary = $"{enrichedMeta.ProviderSource} Direct Lossless FLAC";
                    ReportStage(68.0, $"Direct FLAC Lossless stream verified ({baseFileName}.flac)...", "FLAC LOSSLESS");
                }
            }

            if (!directFlacDownloaded)
            {
                ReportStage(34.0, $"Downloading verified master audio stream for '{enrichedMeta.Title}'...", providerSummary);
                string? sourceAudioFile = await ResolveSourceAudioFileAsync(track, enrichedMeta, selectedCandidate, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sourceAudioFile) || !File.Exists(sourceAudioFile))
                {
                    ReportStage(0.0, $"Error: Could not resolve stream for '{enrichedMeta.Title}'.", "ERROR");
                    return new TrackDownloadResult(
                        Success: false,
                        AudioFilePath: string.Empty,
                        LrcFilePath: null,
                        CanvasVideoFilePath: null,
                        FormatBadge: targetExt.TrimStart('.').ToUpperInvariant(),
                        ProviderSummary: "None",
                        ErrorMessage: "Could not resolve audio stream from YouTube Music or Mornye providers.");
                }

                if (targetFormat == DownloadAudioFormat.FlacLossless)
                {
                    ReportStage(56.0, $"Encoding FLAC Lossless ({baseFileName}.flac)...", "FLAC LOSSLESS");
                    await TranscodeAudioFileAsync(sourceAudioFile, finalAudioPath, isFlac: true, cancellationToken).ConfigureAwait(false);
                    providerSummary = $"Mornye Verified ({enrichedMeta.ProviderSource}) + Lossless FLAC";
                }
                else if (targetFormat == DownloadAudioFormat.Mp3MaxQuality)
                {
                    ReportStage(56.0, $"Encoding MP3 320 kbps ({baseFileName}.mp3)...", "MP3 320K");
                    await TranscodeAudioFileAsync(sourceAudioFile, finalAudioPath, isFlac: false, cancellationToken).ConfigureAwait(false);
                    providerSummary = $"YouTube Music 320kbps MP3 + {enrichedMeta.ProviderSource} Tags";
                }
                else
                {
                    ReportStage(58.0, $"Saving M4A container ({baseFileName}.m4a)...", "M4A AAC");
                    File.Copy(sourceAudioFile, finalAudioPath, overwrite: true);
                    providerSummary = $"YouTube Music M4A + {enrichedMeta.ProviderSource} Tags";
                }
            }

            // STEP 3: Fetch Synchronized / Static Lyrics if enabled
            IReadOnlyList<LyricLine> resolvedLyrics = track.Lyrics;
            if (Options.IncludeSynchronizedLyrics || Options.IncludeStaticLyrics)
            {
                ReportStage(74.0, "Downloading synchronized lyrics (.lrc)...", providerSummary);
                try
                {
                    var lookupTrack = new TrackItem
                    {
                        Id = track.Id,
                        Title = enrichedMeta.Title,
                        Artist = enrichedMeta.Artist,
                        Album = enrichedMeta.Album,
                        Duration = enrichedMeta.DurationSeconds > 0 ? TimeSpan.FromSeconds(enrichedMeta.DurationSeconds) : track.Duration
                    };

                    var fetchedResult = await _lyricsProviderService.FetchOnlineLyricsAsync(
                        lookupTrack,
                        cancellationToken).ConfigureAwait(false);

                    if (fetchedResult?.Lines is { Count: > 0 })
                    {
                        resolvedLyrics = fetchedResult.Lines;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Lyrics fetch fallback used during download.");
                }

                if (Options.IncludeSynchronizedLyrics && resolvedLyrics.Count > 0)
                {
                    lrcSidecarPath = Path.Combine(downloadDir, $"{baseFileName}.lrc");
                    string lrcContent = FormatAsLrcFileContent(enrichedMeta, resolvedLyrics);
                    await File.WriteAllTextAsync(lrcSidecarPath, lrcContent, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                }
            }

            // STEP 4: Download High-Resolution Cover Art bytes if enabled
            byte[]? coverBytes = null;
            string coverMimeType = "image/jpeg";
            if (Options.IncludeHighResCoverArt)
            {
                ReportStage(84.0, "Embedding HD 1280x1280 cover art...", providerSummary);
                (coverBytes, coverMimeType) = await DownloadCoverArtBytesAsync(enrichedMeta.CoverArtUri, track.CoverArtUri, cancellationToken).ConfigureAwait(false);
            }

            // STEP 5: Apply TagLibSharp Metadata & Embedded Lyrics / Artwork according to user toggles
            ReportStage(90.0, "Writing ID3v2 / Vorbis / ISRC metadata tags...", providerSummary);
            ApplyMetadataTagsToFile(finalAudioPath, enrichedMeta, resolvedLyrics, coverBytes, coverMimeType);

            // STEP 6: Download Apple Music Canvas (.canvas.mp4 sidecar) for Offline Mode if enabled
            if (Options.IncludeAppleCanvasOffline)
            {
                ReportStage(95.0, "Checking Apple Music animated Canvas...", providerSummary);
                try
                {
                    var canvasResult = await _canvasArtworkService.GetCanvasArtworkAsync(
                        enrichedMeta.Title,
                        enrichedMeta.Artist,
                        enrichedMeta.Album,
                        cancellationToken).ConfigureAwait(false);

                    if (canvasResult is not null && !string.IsNullOrWhiteSpace(canvasResult.VideoUri))
                    {
                        string targetCanvasPath = Path.Combine(downloadDir, $"{baseFileName}.canvas.mp4");
                        if (File.Exists(canvasResult.VideoUri))
                        {
                            File.Copy(canvasResult.VideoUri, targetCanvasPath, overwrite: true);
                            canvasSidecarPath = targetCanvasPath;
                        }
                        else if (canvasResult.VideoUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            using var resp = await _httpClient.GetAsync(canvasResult.VideoUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                            if (resp.IsSuccessStatusCode)
                            {
                                await using var fs = new FileStream(targetCanvasPath, FileMode.Create, FileAccess.Write, FileShare.None);
                                await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                                canvasSidecarPath = targetCanvasPath;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Optional Apple Music Canvas download skipped for '{Title}'.", track.Title);
                }
            }

            // STEP 7: Import into Local Library Catalog so it appears immediately in Library
            ReportStage(98.0, "Importing song into your local library...", providerSummary);
            await _mediaCatalogService.ImportLocalFilesAsync([finalAudioPath], cancellationToken).ConfigureAwait(false);
            await _dataUsageService.MarkTrackDownloadedAsync(track, cancellationToken).ConfigureAwait(false);

            string badge = targetFormat switch
            {
                DownloadAudioFormat.FlacLossless => "FLAC LOSSLESS",
                DownloadAudioFormat.Mp3MaxQuality => "MP3 320K",
                _ => "M4A AAC"
            };

            ReportStage(100.0, $"Downloaded '{enrichedMeta.Title}' ({badge})!", badge);
            return new TrackDownloadResult(
                Success: true,
                AudioFilePath: finalAudioPath,
                LrcFilePath: lrcSidecarPath,
                CanvasVideoFilePath: canvasSidecarPath,
                FormatBadge: badge,
                ProviderSummary: providerSummary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Track download failed for '{Title}'.", track.Title);
            return new TrackDownloadResult(
                Success: false,
                AudioFilePath: finalAudioPath,
                LrcFilePath: lrcSidecarPath,
                CanvasVideoFilePath: canvasSidecarPath,
                FormatBadge: targetExt.TrimStart('.').ToUpperInvariant(),
                ProviderSummary: "Error",
                ErrorMessage: ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ResolvedPlaybackStream?> ResolveDirectFlacStreamAsync(
        TrackItem track,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(_flacStreamCacheDir);
            string cacheKeySeed = !string.IsNullOrWhiteSpace(track.YouTubeVideoId)
                ? track.YouTubeVideoId.Trim()
                : $"{track.Artist}|{track.Title}";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKeySeed.ToLowerInvariant())))[..16].ToLowerInvariant();
            string cachedFlacPath = Path.Combine(_flacStreamCacheDir, $"{hash}_flac.flac");

            // 1. Instant verified FLAC cache hit
            if (File.Exists(cachedFlacPath) && HasValidFlacMagicHeader(cachedFlacPath))
            {
                return BuildResolvedFlacStreamFromFile(
                    cachedFlacPath,
                    PlaybackSourceKind.FlacGdStudio,
                    "FLAC Lossless (Cached)",
                    isrc: string.Empty,
                    downloadedNetworkBytes: 0);
            }

            // 2. Resolve Mornye metadata & direct unencrypted FLAC stream (GDStudio / Tidal / Qobuz)
            var enrichedMeta = await ResolveMornyeMetadataAsync(track, selectedCandidate: null, cancellationToken).ConfigureAwait(false);
            string tempFlacPath = Path.Combine(_flacStreamCacheDir, $"{hash}_{Guid.NewGuid():N}.part");
            try
            {
                bool downloaded = await TryDownloadDirectLosslessFlacAsync(enrichedMeta, tempFlacPath, cancellationToken).ConfigureAwait(false);
                if (!downloaded || !File.Exists(tempFlacPath) || !HasValidFlacMagicHeader(tempFlacPath))
                {
                    return null;
                }

                File.Move(tempFlacPath, cachedFlacPath, overwrite: true);
                long fileSize = new FileInfo(cachedFlacPath).Length;

                PlaybackSourceKind sourceKind = PlaybackSourceKind.FlacGdStudio;
                if (enrichedMeta.ProviderSource.Contains("Tidal", StringComparison.OrdinalIgnoreCase) &&
                    !enrichedMeta.ProviderSource.Contains("NETEASE", StringComparison.OrdinalIgnoreCase))
                {
                    sourceKind = PlaybackSourceKind.FlacTidal;
                }
                else if (enrichedMeta.ProviderSource.Contains("Qobuz", StringComparison.OrdinalIgnoreCase) &&
                         !enrichedMeta.ProviderSource.Contains("NETEASE", StringComparison.OrdinalIgnoreCase))
                {
                    sourceKind = PlaybackSourceKind.FlacQobuz;
                }

                string providerLabel = string.IsNullOrWhiteSpace(enrichedMeta.ProviderSource)
                    ? "Mornye FLAC Lossless"
                    : enrichedMeta.ProviderSource;

                return BuildResolvedFlacStreamFromFile(
                    cachedFlacPath,
                    sourceKind,
                    providerLabel,
                    isrc: enrichedMeta.Isrc,
                    downloadedNetworkBytes: fileSize);
            }
            finally
            {
                if (File.Exists(tempFlacPath))
                {
                    try { File.Delete(tempFlacPath); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Direct FLAC streaming resolution failed for '{Title}'; will fall back to YouTube Music.", track.Title);
            return null;
        }
    }

    private static ResolvedPlaybackStream BuildResolvedFlacStreamFromFile(
        string flacPath,
        PlaybackSourceKind sourceKind,
        string providerLabel,
        string isrc,
        long downloadedNetworkBytes)
    {
        long fileSize = 0;
        int bitrateKbps = 0;
        int sampleRateHz = 44100;
        int channels = 2;
        int bitDepth = 16;

        try
        {
            if (File.Exists(flacPath))
            {
                fileSize = new FileInfo(flacPath).Length;
                using var tagFile = TagLib.File.Create(flacPath);
                var props = tagFile.Properties;
                if (props is not null)
                {
                    if (props.AudioBitrate > 0) bitrateKbps = props.AudioBitrate;
                    if (props.AudioSampleRate > 0) sampleRateHz = props.AudioSampleRate;
                    if (props.AudioChannels > 0) channels = props.AudioChannels;
                    if (props.BitsPerSample > 0) bitDepth = props.BitsPerSample;
                }
            }
        }
        catch
        {
            // Keep safe FLAC defaults if TagLibSharp fails
        }

        return new ResolvedPlaybackStream(
            PlaybackUri: flacPath,
            SourceKind: sourceKind,
            ProviderLabel: providerLabel,
            RequestedQuality: StreamingAudioQuality.FlacLossless,
            ActualQuality: StreamingAudioQuality.FlacLossless,
            IsFlacFallback: false,
            FallbackReason: null,
            Codec: "FLAC",
            BitrateKbps: bitrateKbps,
            SampleRateHz: sampleRateHz,
            ChannelCount: channels,
            BitDepth: bitDepth,
            FileSizeBytes: fileSize,
            Isrc: isrc,
            DownloadedNetworkBytes: downloadedNetworkBytes);
    }

    private sealed class EnrichedMornyeMetadata
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string AlbumArtist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public uint TrackNumber { get; set; } = 1;
        public uint DiscNumber { get; set; } = 1;
        public int ReleaseYear { get; set; } = DateTime.Now.Year;
        public string Genre { get; set; } = "Pop";
        public string Isrc { get; set; } = string.Empty;
        public string Copyright { get; set; } = string.Empty;
        public string ReplayGain { get; set; } = string.Empty;
        public string PeakAmplitude { get; set; } = string.Empty;
        public string CoverArtUri { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
        public string? TidalTrackId { get; set; }
        public string? QobuzTrackId { get; set; }
        public string? DeezerTrackId { get; set; }
        public string? GdStudioSource { get; set; }
        public string? GdStudioUrlId { get; set; }
        public string ProviderSource { get; set; } = "YouTube Music";
    }

    /// <summary>
    /// Queries Tidal, Qobuz, and Deezer public APIs (mirroring <c>mornyeapp/Mornye-Python</c>)
    /// with strict title/artist/duration identity verification to resolve canonical ISRC,
    /// 1280x1280 HD Cover Art, Track/Disc Number, Copyright, and ReplayGain.
    /// </summary>
    private async Task<EnrichedMornyeMetadata> ResolveMornyeMetadataAsync(
        TrackItem track,
        TrackDownloadCandidate? selectedCandidate,
        CancellationToken cancellationToken)
    {
        string targetTitle = !string.IsNullOrWhiteSpace(selectedCandidate?.Title) ? selectedCandidate!.Title : track.Title;
        string targetArtist = !string.IsNullOrWhiteSpace(selectedCandidate?.Artist) ? selectedCandidate!.Artist : track.Artist;
        string targetAlbum = !string.IsNullOrWhiteSpace(selectedCandidate?.Album) ? selectedCandidate!.Album : (string.IsNullOrWhiteSpace(track.Album) ? targetTitle : track.Album);
        double targetDurationSec = selectedCandidate?.Duration.TotalSeconds > 0
            ? selectedCandidate.Duration.TotalSeconds
            : track.Duration.TotalSeconds;

        var meta = new EnrichedMornyeMetadata
        {
            Title = targetTitle,
            Artist = targetArtist,
            AlbumArtist = targetArtist.Split([',', '&'], 2, StringSplitOptions.TrimEntries)[0],
            Album = targetAlbum,
            TrackNumber = selectedCandidate?.TrackNumber > 0 ? selectedCandidate.TrackNumber : (track.TrackNumber > 0 ? track.TrackNumber : 1u),
            DiscNumber = selectedCandidate?.DiscNumber > 0 ? selectedCandidate.DiscNumber : 1u,
            ReleaseYear = selectedCandidate?.ReleaseYear > 0 ? selectedCandidate.ReleaseYear : (track.ReleaseYear > 0 ? track.ReleaseYear : DateTime.Now.Year),
            Genre = !string.IsNullOrWhiteSpace(selectedCandidate?.Genre) ? selectedCandidate!.Genre : (string.IsNullOrWhiteSpace(track.Genre) ? "Pop" : track.Genre),
            CoverArtUri = !string.IsNullOrWhiteSpace(selectedCandidate?.CoverArtUri) ? selectedCandidate!.CoverArtUri : track.CoverArtUri,
            Isrc = selectedCandidate?.Isrc ?? string.Empty,
            Copyright = selectedCandidate?.Copyright ?? string.Empty,
            ReplayGain = selectedCandidate?.ReplayGain ?? string.Empty,
            PeakAmplitude = selectedCandidate?.PeakAmplitude ?? string.Empty,
            DurationSeconds = targetDurationSec,
            TidalTrackId = selectedCandidate?.TidalTrackId,
            QobuzTrackId = selectedCandidate?.QobuzTrackId,
            DeezerTrackId = selectedCandidate?.DeezerTrackId,
            GdStudioSource = selectedCandidate?.GdStudioSource,
            GdStudioUrlId = selectedCandidate?.GdStudioUrlId
        };

        string query = $"{targetArtist} {targetTitle}".Trim();
        var providers = new List<string>();

        var tidalTask = TryEnrichFromTidalAsync(query, meta, cancellationToken);
        var qobuzTask = TryEnrichFromQobuzAsync(query, meta, cancellationToken);
        var deezerTask = TryEnrichFromDeezerAsync(query, meta, cancellationToken);

        await Task.WhenAll(tidalTask, qobuzTask, deezerTask).ConfigureAwait(false);

        if (await tidalTask.ConfigureAwait(false))
        {
            providers.Add("Tidal");
        }

        if (await qobuzTask.ConfigureAwait(false))
        {
            providers.Add("Qobuz");
        }

        if (await deezerTask.ConfigureAwait(false))
        {
            providers.Add("Deezer");
        }

        if (providers.Count > 0)
        {
            meta.ProviderSource = string.Join(" + ", providers);
        }
        else if (!string.IsNullOrWhiteSpace(selectedCandidate?.ProviderLabel))
        {
            meta.ProviderSource = selectedCandidate!.ProviderLabel;
        }

        return meta;
    }

    private async Task<bool> TryEnrichFromTidalAsync(
        string query,
        EnrichedMornyeMetadata meta,
        CancellationToken cancellationToken)
    {
        foreach (string token in TidalPublicTokens)
        {
            try
            {
                string url = $"{TidalApiBaseUrl}/search/tracks?query={Uri.EscapeDataString(query)}&limit=8&countryCode=US";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("x-tidal-token", token);

                using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array ||
                    items.GetArrayLength() == 0)
                {
                    continue;
                }

                JsonElement? bestItem = null;
                double bestScore = 0.0;

                foreach (var item in items.EnumerateArray())
                {
                    string itemTitle = ExtractTidalTrackTitle(item);
                    string itemArtist = ExtractTidalArtistNames(item);
                    string itemAlbum = item.TryGetProperty("album", out var aObj) && aObj.ValueKind == JsonValueKind.Object && aObj.TryGetProperty("title", out var atProp)
                        ? (atProp.GetString() ?? string.Empty)
                        : string.Empty;
                    double itemDurationSec = item.TryGetProperty("duration", out var durProp) && durProp.TryGetDouble(out double dSec) ? dSec : 0;

                    // Strict Mornye Identity Guard: Never enrich from a different song or conflicting version
                    if (!TitlesMatchStrict(meta.Title, itemTitle) ||
                        !ArtistsMatchStrict(meta.Artist, itemArtist) ||
                        HasVariantConflict(meta.Title, itemTitle))
                    {
                        continue;
                    }

                    double score = ScoreTrackCandidate(meta.Title, meta.Artist, meta.Album, meta.DurationSeconds, itemTitle, itemArtist, itemAlbum, itemDurationSec, requireExactVariant: true);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestItem = item;
                    }
                }

                if (bestItem is null || bestScore < 0.72)
                {
                    continue;
                }

                var matched = bestItem.Value;
                if (matched.TryGetProperty("id", out var idProp))
                {
                    meta.TidalTrackId = idProp.ToString();
                }

                if (matched.TryGetProperty("isrc", out var isrcProp) && !string.IsNullOrWhiteSpace(isrcProp.GetString()))
                {
                    meta.Isrc = isrcProp.GetString()!;
                }

                if (matched.TryGetProperty("duration", out var dProp) && dProp.TryGetDouble(out double matchedDur) && matchedDur > 0 && meta.DurationSeconds <= 0)
                {
                    meta.DurationSeconds = matchedDur;
                }

                if (matched.TryGetProperty("copyright", out var copyProp) && !string.IsNullOrWhiteSpace(copyProp.GetString()))
                {
                    meta.Copyright = copyProp.GetString()!;
                }

                if (matched.TryGetProperty("trackNumber", out var tnProp) && tnProp.TryGetUInt32(out uint tn) && tn > 0)
                {
                    meta.TrackNumber = tn;
                }

                if (matched.TryGetProperty("volumeNumber", out var vnProp) && vnProp.TryGetUInt32(out uint vn) && vn > 0)
                {
                    meta.DiscNumber = vn;
                }

                if (matched.TryGetProperty("replayGain", out var rgProp) && rgProp.TryGetDouble(out double rg))
                {
                    meta.ReplayGain = $"{rg:F2} dB";
                }

                if (matched.TryGetProperty("peak", out var pkProp) && pkProp.TryGetDouble(out double pk))
                {
                    meta.PeakAmplitude = $"{pk:F6}";
                }

                if (matched.TryGetProperty("album", out var albObj) && albObj.ValueKind == JsonValueKind.Object)
                {
                    if (albObj.TryGetProperty("title", out var albTitle) && !string.IsNullOrWhiteSpace(albTitle.GetString()))
                    {
                        meta.Album = albTitle.GetString()!;
                    }

                    if (albObj.TryGetProperty("cover", out var coverProp) && !string.IsNullOrWhiteSpace(coverProp.GetString()))
                    {
                        string coverUuid = coverProp.GetString()!.Replace('-', '/');
                        meta.CoverArtUri = $"https://resources.tidal.com/images/{coverUuid}/1280x1280.jpg";
                    }
                }

                return true;
            }
            catch
            {
                // Try next token
            }
        }

        return false;
    }

    private async Task<bool> TryEnrichFromQobuzAsync(
        string query,
        EnrichedMornyeMetadata meta,
        CancellationToken cancellationToken)
    {
        try
        {
            string url = $"{QobuzApiBaseUrl}/track/search?query={Uri.EscapeDataString(query)}&limit=6";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-App-Id", QobuzPublicAppId);

            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracksObj) ||
                !tracksObj.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0)
            {
                return false;
            }

            JsonElement? bestItem = null;
            double bestScore = 0.0;

            foreach (var item in items.EnumerateArray())
            {
                string itemTitle = item.TryGetProperty("title", out var tProp) ? (tProp.GetString() ?? string.Empty) : string.Empty;
                string itemArtist = item.TryGetProperty("performer", out var pObj) && pObj.ValueKind == JsonValueKind.Object && pObj.TryGetProperty("name", out var pnProp)
                    ? (pnProp.GetString() ?? string.Empty)
                    : string.Empty;
                string itemAlbum = item.TryGetProperty("album", out var aObj) && aObj.ValueKind == JsonValueKind.Object && aObj.TryGetProperty("title", out var atProp)
                    ? (atProp.GetString() ?? string.Empty)
                    : string.Empty;
                double itemDurationSec = item.TryGetProperty("duration", out var dProp) && dProp.TryGetDouble(out double dSec) ? dSec : 0;

                if (!TitlesMatchStrict(meta.Title, itemTitle) ||
                    !ArtistsMatchStrict(meta.Artist, itemArtist) ||
                    HasVariantConflict(meta.Title, itemTitle))
                {
                    continue;
                }

                double score = ScoreTrackCandidate(meta.Title, meta.Artist, meta.Album, meta.DurationSeconds, itemTitle, itemArtist, itemAlbum, itemDurationSec, requireExactVariant: true);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestItem = item;
                }
            }

            if (bestItem is null || bestScore < 0.72)
            {
                return false;
            }

            var matched = bestItem.Value;
            if (matched.TryGetProperty("id", out var idProp))
            {
                meta.QobuzTrackId = idProp.ToString();
            }

            if (string.IsNullOrWhiteSpace(meta.Isrc) &&
                matched.TryGetProperty("isrc", out var isrcProp) &&
                !string.IsNullOrWhiteSpace(isrcProp.GetString()))
            {
                meta.Isrc = isrcProp.GetString()!;
            }

            if (string.IsNullOrWhiteSpace(meta.Copyright) &&
                matched.TryGetProperty("copyright", out var copyProp) &&
                !string.IsNullOrWhiteSpace(copyProp.GetString()))
            {
                meta.Copyright = copyProp.GetString()!;
            }

            if (matched.TryGetProperty("album", out var albObj) && albObj.ValueKind == JsonValueKind.Object)
            {
                if (albObj.TryGetProperty("genre", out var genreObj) &&
                    genreObj.ValueKind == JsonValueKind.Object &&
                    genreObj.TryGetProperty("name", out var genreName) &&
                    !string.IsNullOrWhiteSpace(genreName.GetString()))
                {
                    meta.Genre = genreName.GetString()!;
                }

                if (albObj.TryGetProperty("released_at", out var relProp) && relProp.TryGetInt64(out long unixSec) && unixSec > 0)
                {
                    meta.ReleaseYear = DateTimeOffset.FromUnixTimeSeconds(unixSec).Year;
                }

                if (string.IsNullOrWhiteSpace(meta.CoverArtUri) &&
                    albObj.TryGetProperty("image", out var imgObj) &&
                    imgObj.ValueKind == JsonValueKind.Object &&
                    imgObj.TryGetProperty("large", out var largeProp) &&
                    !string.IsNullOrWhiteSpace(largeProp.GetString()))
                {
                    meta.CoverArtUri = largeProp.GetString()!;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryEnrichFromDeezerAsync(
        string query,
        EnrichedMornyeMetadata meta,
        CancellationToken cancellationToken)
    {
        try
        {
            string url = $"{DeezerApiBaseUrl}/search?q={Uri.EscapeDataString(query)}&limit=6";
            using var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataArr) ||
                dataArr.ValueKind != JsonValueKind.Array ||
                dataArr.GetArrayLength() == 0)
            {
                return false;
            }

            JsonElement? bestItem = null;
            double bestScore = 0.0;

            foreach (var item in dataArr.EnumerateArray())
            {
                string itemTitle = item.TryGetProperty("title", out var tProp) ? (tProp.GetString() ?? string.Empty) : string.Empty;
                string itemArtist = item.TryGetProperty("artist", out var aObj) && aObj.ValueKind == JsonValueKind.Object && aObj.TryGetProperty("name", out var anProp)
                    ? (anProp.GetString() ?? string.Empty)
                    : string.Empty;
                string itemAlbum = item.TryGetProperty("album", out var alObj) && alObj.ValueKind == JsonValueKind.Object && alObj.TryGetProperty("title", out var altProp)
                    ? (altProp.GetString() ?? string.Empty)
                    : string.Empty;
                double itemDurationSec = item.TryGetProperty("duration", out var dProp) && dProp.TryGetDouble(out double dSec) ? dSec : 0;

                if (!TitlesMatchStrict(meta.Title, itemTitle) ||
                    !ArtistsMatchStrict(meta.Artist, itemArtist) ||
                    HasVariantConflict(meta.Title, itemTitle))
                {
                    continue;
                }

                double score = ScoreTrackCandidate(meta.Title, meta.Artist, meta.Album, meta.DurationSeconds, itemTitle, itemArtist, itemAlbum, itemDurationSec, requireExactVariant: true);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestItem = item;
                }
            }

            if (bestItem is null || bestScore < 0.72)
            {
                return false;
            }

            var matched = bestItem.Value;
            if (!matched.TryGetProperty("id", out var idProp))
            {
                return false;
            }

            string dzId = idProp.ToString();
            meta.DeezerTrackId = dzId;

            if (matched.TryGetProperty("duration", out var durProp) && durProp.TryGetDouble(out double dzDur) && dzDur > 0 && meta.DurationSeconds <= 0)
            {
                meta.DurationSeconds = dzDur;
            }

            if (matched.TryGetProperty("album", out var albObj) && albObj.ValueKind == JsonValueKind.Object)
            {
                if (string.IsNullOrWhiteSpace(meta.Album) || meta.Album == meta.Title)
                {
                    if (albObj.TryGetProperty("title", out var albTitleProp) && !string.IsNullOrWhiteSpace(albTitleProp.GetString()))
                    {
                        meta.Album = albTitleProp.GetString()!;
                    }
                }

                if (string.IsNullOrWhiteSpace(meta.CoverArtUri) &&
                    albObj.TryGetProperty("cover_xl", out var xlProp) &&
                    !string.IsNullOrWhiteSpace(xlProp.GetString()))
                {
                    meta.CoverArtUri = xlProp.GetString()!;
                }
            }

            // Query full Deezer track detail for ISRC, release_date, track_position, disk_number
            using var trackResp = await _httpClient.GetAsync($"{DeezerApiBaseUrl}/track/{dzId}", cancellationToken).ConfigureAwait(false);
            if (trackResp.IsSuccessStatusCode)
            {
                string trackJson = await trackResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var trackDoc = JsonDocument.Parse(trackJson);
                var root = trackDoc.RootElement;

                if (string.IsNullOrWhiteSpace(meta.Isrc) &&
                    root.TryGetProperty("isrc", out var isrcProp) &&
                    !string.IsNullOrWhiteSpace(isrcProp.GetString()))
                {
                    meta.Isrc = isrcProp.GetString()!;
                }

                if (root.TryGetProperty("track_position", out var tpProp) && tpProp.TryGetUInt32(out uint tp) && tp > 0)
                {
                    meta.TrackNumber = tp;
                }

                if (root.TryGetProperty("disk_number", out var dnProp) && dnProp.TryGetUInt32(out uint dn) && dn > 0)
                {
                    meta.DiscNumber = dn;
                }

                if (root.TryGetProperty("release_date", out var rdProp) &&
                    !string.IsNullOrWhiteSpace(rdProp.GetString()) &&
                    rdProp.GetString()!.Length >= 4 &&
                    int.TryParse(rdProp.GetString()![..4], out int yr) &&
                    yr > 1900)
                {
                    meta.ReleaseYear = yr;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to download a genuine, unencrypted Lossless/Hi-Res FLAC stream (<c>fLaC</c> magic header, 900-1650+ kbps)
    /// via Mornye's decrypted community registry (<c>gdstudio</c> Lossless FLAC API, Qobuz, and Tidal endpoints),
    /// strictly enforcing Mornye's track identity and duration checks so a wrong track is never accepted.
    /// </summary>
    private async Task<bool> TryDownloadDirectLosslessFlacAsync(
        EnrichedMornyeMetadata meta,
        string outputFlacPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var registryJson = await FetchDecryptedMornyeRegistryAsync(cancellationToken).ConfigureAwait(false);
            string gdstudioApiUrl = "https://music-api.gdstudio.xyz/api.php";

            if (!string.IsNullOrWhiteSpace(registryJson))
            {
                try
                {
                    using var regDoc = JsonDocument.Parse(registryJson!);
                    if (regDoc.RootElement.TryGetProperty("gdstudio", out var gdProp) &&
                        gdProp.ValueKind == JsonValueKind.String)
                    {
                        string? candidateGdUrl = gdProp.GetString();
                        if (YouTubeSecurityPolicy.IsSafePublicHttpsUrl(candidateGdUrl))
                        {
                            gdstudioApiUrl = candidateGdUrl!;
                        }
                    }
                }
                catch
                {
                    // Fallback to default gdstudio endpoint
                }
            }

            // 1. Primary Mornye Lossless FLAC Provider (gdstudio 999 / 740 lossless FLAC stream) with strict title/artist/duration verification
            if (await TryDownloadFromGdStudioLosslessAsync(gdstudioApiUrl, meta, outputFlacPath, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            // 2. Try Tidal direct playbackinfopostpaywall manifest (verifying fLaC magic header + duration)
            if (!string.IsNullOrWhiteSpace(meta.TidalTrackId))
            {
                foreach (string token in TidalPublicTokens)
                {
                    string playbackUrl =
                        $"{TidalApiBaseUrl}/tracks/{meta.TidalTrackId}/playbackinfopostpaywall?audioquality=LOSSLESS&playbackmode=STREAM&assetpresentation=FULL&countryCode=US";
                    using var req = new HttpRequestMessage(HttpMethod.Get, playbackUrl);
                    req.Headers.TryAddWithoutValidation("x-tidal-token", token);

                    using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("manifest", out var manifestProp) &&
                            !string.IsNullOrWhiteSpace(manifestProp.GetString()))
                        {
                            string decodedManifest = Encoding.UTF8.GetString(Convert.FromBase64String(manifestProp.GetString()!));
                            using var manDoc = JsonDocument.Parse(decodedManifest);
                            if (manDoc.RootElement.TryGetProperty("urls", out var urlsArr) &&
                                urlsArr.ValueKind == JsonValueKind.Array &&
                                urlsArr.GetArrayLength() > 0)
                            {
                                string? streamUrl = urlsArr[0].GetString();
                                if (YouTubeSecurityPolicy.IsSafePublicHttpsUrl(streamUrl) &&
                                    await TryDownloadFileVerifiedAsync(streamUrl!, outputFlacPath, requireFlacHeader: true, meta.DurationSeconds, cancellationToken).ConfigureAwait(false))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            // 3. Try Mornye decrypted registry Qobuz community endpoints
            if (!string.IsNullOrWhiteSpace(registryJson))
            {
                using var regDoc = JsonDocument.Parse(registryJson!);
                if (!string.IsNullOrWhiteSpace(meta.QobuzTrackId) &&
                    regDoc.RootElement.TryGetProperty("qobuz", out var qobuzArr) &&
                    qobuzArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var endpointEl in qobuzArr.EnumerateArray())
                    {
                        string? baseUrl = endpointEl.GetString();
                        if (!YouTubeSecurityPolicy.IsSafePublicHttpsUrl(baseUrl))
                        {
                            continue;
                        }

                        string reqUrl = $"{baseUrl!.TrimEnd('/')}/stream?track_id={meta.QobuzTrackId}&quality=27";
                        using var resp = await _httpClient.GetAsync(reqUrl, cancellationToken).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                            using var sDoc = JsonDocument.Parse(body);
                            string? flacUrl = sDoc.RootElement.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                            if (YouTubeSecurityPolicy.IsSafePublicHttpsUrl(flacUrl) &&
                                await TryDownloadFileVerifiedAsync(flacUrl!, outputFlacPath, requireFlacHeader: true, meta.DurationSeconds, cancellationToken).ConfigureAwait(false))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Direct lossless endpoint fallback triggered.");
        }

        return false;
    }

    private async Task<bool> TryDownloadFromGdStudioLosslessAsync(
        string gdstudioApiUrl,
        EnrichedMornyeMetadata meta,
        string outputFlacPath,
        CancellationToken cancellationToken)
    {
        if (!YouTubeSecurityPolicy.IsSafePublicHttpsUrl(gdstudioApiUrl))
        {
            return false;
        }

        // If user explicitly picked a verified GDStudio candidate, try it first
        if (!string.IsNullOrWhiteSpace(meta.GdStudioSource) && !string.IsNullOrWhiteSpace(meta.GdStudioUrlId))
        {
            foreach (int br in new[] { 999, 740 })
            {
                string trackUrl = $"{gdstudioApiUrl}?types=url&source={meta.GdStudioSource}&id={Uri.EscapeDataString(meta.GdStudioUrlId)}&br={br}";
                using var urlResp = await _httpClient.GetAsync(trackUrl, cancellationToken).ConfigureAwait(false);
                if (!urlResp.IsSuccessStatusCode)
                {
                    continue;
                }

                string urlJson = await urlResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var urlDoc = JsonDocument.Parse(urlJson);
                string? streamUrl = urlDoc.RootElement.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                if (YouTubeSecurityPolicy.IsSafePublicHttpsUrl(streamUrl) &&
                    await TryDownloadFileVerifiedAsync(streamUrl!, outputFlacPath, requireFlacHeader: true, meta.DurationSeconds, cancellationToken).ConfigureAwait(false))
                {
                    meta.ProviderSource = $"Mornye Lossless ({meta.GdStudioSource.ToUpperInvariant()} 24-bit/16-bit FLAC)";
                    return true;
                }
            }
        }

        string searchQuery = $"{meta.Artist} {meta.Title}".Trim();
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return false;
        }

        foreach (string source in new[] { "netease" })
        {
            try
            {
                string searchUrl = $"{gdstudioApiUrl}?types=search&source={source}&name={Uri.EscapeDataString(searchQuery)}&count=10";
                using var searchResp = await _httpClient.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
                if (!searchResp.IsSuccessStatusCode)
                {
                    continue;
                }

                string searchJson = await searchResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var searchDoc = JsonDocument.Parse(searchJson);
                if (searchDoc.RootElement.ValueKind != JsonValueKind.Array || searchDoc.RootElement.GetArrayLength() == 0)
                {
                    continue;
                }

                var candidates = new List<(string UrlId, double Score)>();

                foreach (var item in searchDoc.RootElement.EnumerateArray())
                {
                    string? urlId = item.TryGetProperty("url_id", out var uidProp)
                        ? uidProp.ToString()
                        : (item.TryGetProperty("id", out var idProp) ? idProp.ToString() : null);

                    if (string.IsNullOrWhiteSpace(urlId))
                    {
                        continue;
                    }

                    string itemName = item.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? string.Empty) : string.Empty;
                    string itemAlbum = item.TryGetProperty("album", out var alProp) ? (alProp.GetString() ?? string.Empty) : string.Empty;
                    string itemArtist = string.Empty;
                    if (item.TryGetProperty("artist", out var aProp))
                    {
                        if (aProp.ValueKind == JsonValueKind.Array)
                        {
                            itemArtist = string.Join(", ", aProp.EnumerateArray().Select(x => x.GetString() ?? string.Empty));
                        }
                        else if (aProp.ValueKind == JsonValueKind.String)
                        {
                            itemArtist = aProp.GetString() ?? string.Empty;
                        }
                    }

                    // CRITICAL MORNYE IDENTITY GUARD:
                    // Never accept a track whose title or artist does not strictly match the requested song!
                    // This prevents "BRAT" by Chrissy Chlapecka from ever being downloaded when asking for "Cassiopeia".
                    if (!TitlesMatchStrict(meta.Title, itemName) ||
                        !ArtistsMatchStrict(meta.Artist, itemArtist) ||
                        HasVariantConflict(meta.Title, itemName))
                    {
                        continue;
                    }

                    double score = ScoreTrackCandidate(
                        meta.Title,
                        meta.Artist,
                        meta.Album,
                        meta.DurationSeconds,
                        itemName,
                        itemArtist,
                        itemAlbum,
                        candDurationSec: 0,
                        requireExactVariant: true);

                    if (score >= 0.80)
                    {
                        candidates.Add((urlId!, score));
                    }
                }

                foreach (var candidate in candidates.OrderByDescending(c => c.Score).Take(2))
                {
                    foreach (int br in new[] { 999, 740 })
                    {
                        string trackUrl = $"{gdstudioApiUrl}?types=url&source={source}&id={Uri.EscapeDataString(candidate.UrlId)}&br={br}";
                        using var urlResp = await _httpClient.GetAsync(trackUrl, cancellationToken).ConfigureAwait(false);
                        if (!urlResp.IsSuccessStatusCode)
                        {
                            continue;
                        }

                        string urlJson = await urlResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        using var urlDoc = JsonDocument.Parse(urlJson);
                        string? streamUrl = urlDoc.RootElement.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                        if (string.IsNullOrWhiteSpace(streamUrl))
                        {
                            continue;
                        }

                        if (await TryDownloadFileVerifiedAsync(streamUrl!, outputFlacPath, requireFlacHeader: true, meta.DurationSeconds, cancellationToken).ConfigureAwait(false))
                        {
                            meta.ProviderSource = $"Mornye Lossless ({source.ToUpperInvariant()} 24-bit/16-bit FLAC)";
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Continue to next source
            }
        }

        return false;
    }

    private async Task<List<TrackDownloadCandidate>> SearchTidalCandidatesAsync(
        string query,
        string expectedTitle,
        string expectedArtist,
        string expectedAlbum,
        double expectedDurationSec,
        CancellationToken cancellationToken)
    {
        var results = new List<TrackDownloadCandidate>();
        foreach (string token in TidalPublicTokens)
        {
            try
            {
                string url = $"{TidalApiBaseUrl}/search/tracks?query={Uri.EscapeDataString(query)}&limit=8&countryCode=US";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("x-tidal-token", token);

                using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in items.EnumerateArray())
                {
                    string itemTitle = ExtractTidalTrackTitle(item);
                    string itemArtist = ExtractTidalArtistNames(item);
                    if (ComputeTitleRatio(expectedTitle, itemTitle) < 0.52 || !ArtistsMatchStrict(expectedArtist, itemArtist))
                    {
                        continue;
                    }

                    string itemAlbum = string.Empty;
                    string coverUri = string.Empty;
                    if (item.TryGetProperty("album", out var albObj) && albObj.ValueKind == JsonValueKind.Object)
                    {
                        if (albObj.TryGetProperty("title", out var atProp))
                        {
                            itemAlbum = atProp.GetString() ?? string.Empty;
                        }

                        if (albObj.TryGetProperty("cover", out var covProp) && !string.IsNullOrWhiteSpace(covProp.GetString()))
                        {
                            coverUri = $"https://resources.tidal.com/images/{covProp.GetString()!.Replace('-', '/')}/640x640.jpg";
                        }
                    }

                    double durSec = item.TryGetProperty("duration", out var dProp) && dProp.TryGetDouble(out double d) ? d : 0;
                    double score = ScoreTrackCandidate(expectedTitle, expectedArtist, expectedAlbum, expectedDurationSec, itemTitle, itemArtist, itemAlbum, durSec, requireExactVariant: false);
                    if (score < 0.55)
                    {
                        continue;
                    }

                    string qualityBadge = "FLAC 16-bit/44.1kHz";
                    if (item.TryGetProperty("audioQuality", out var aqProp) &&
                        string.Equals(aqProp.GetString(), "HI_RES_LOSSLESS", StringComparison.OrdinalIgnoreCase))
                    {
                        qualityBadge = "Hi-Res FLAC 24-bit";
                    }

                    results.Add(new TrackDownloadCandidate
                    {
                        Title = itemTitle,
                        Artist = itemArtist,
                        Album = itemAlbum,
                        Duration = durSec > 0 ? TimeSpan.FromSeconds(durSec) : TimeSpan.Zero,
                        CoverArtUri = coverUri,
                        Isrc = item.TryGetProperty("isrc", out var isrcProp) ? (isrcProp.GetString() ?? string.Empty) : string.Empty,
                        Copyright = item.TryGetProperty("copyright", out var copyProp) ? (copyProp.GetString() ?? string.Empty) : string.Empty,
                        ReplayGain = item.TryGetProperty("replayGain", out var rgProp) && rgProp.TryGetDouble(out double rg) ? $"{rg:F2} dB" : string.Empty,
                        PeakAmplitude = item.TryGetProperty("peak", out var pkProp) && pkProp.TryGetDouble(out double pk) ? $"{pk:F6}" : string.Empty,
                        ProviderLabel = "Tidal Hi-Fi",
                        QualityBadge = qualityBadge,
                        VersionBadge = DetectVersionBadge(itemTitle),
                        MatchScorePercent = (int)Math.Round(Math.Clamp(score * 100.0, 55.0, 99.0)),
                        TidalTrackId = item.TryGetProperty("id", out var idProp) ? idProp.ToString() : null,
                        TrackNumber = item.TryGetProperty("trackNumber", out var tnProp) && tnProp.TryGetUInt32(out uint tn) && tn > 0 ? tn : 1u,
                        DiscNumber = item.TryGetProperty("volumeNumber", out var vnProp) && vnProp.TryGetUInt32(out uint vn) && vn > 0 ? vn : 1u
                    });
                }

                if (results.Count > 0)
                {
                    break;
                }
            }
            catch
            {
                // Try next token
            }
        }

        return results;
    }

    private async Task<List<TrackDownloadCandidate>> SearchDeezerCandidatesAsync(
        string query,
        string expectedTitle,
        string expectedArtist,
        string expectedAlbum,
        double expectedDurationSec,
        CancellationToken cancellationToken)
    {
        var results = new List<TrackDownloadCandidate>();
        try
        {
            string url = $"{DeezerApiBaseUrl}/search?q={Uri.EscapeDataString(query)}&limit=8";
            using var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return results;
            }

            string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in dataArr.EnumerateArray())
            {
                string itemTitle = item.TryGetProperty("title", out var tProp) ? (tProp.GetString() ?? string.Empty) : string.Empty;
                string itemArtist = item.TryGetProperty("artist", out var aObj) && aObj.ValueKind == JsonValueKind.Object && aObj.TryGetProperty("name", out var anProp)
                    ? (anProp.GetString() ?? string.Empty)
                    : string.Empty;

                if (ComputeTitleRatio(expectedTitle, itemTitle) < 0.52 || !ArtistsMatchStrict(expectedArtist, itemArtist))
                {
                    continue;
                }

                string itemAlbum = string.Empty;
                string coverUri = string.Empty;
                if (item.TryGetProperty("album", out var alObj) && alObj.ValueKind == JsonValueKind.Object)
                {
                    if (alObj.TryGetProperty("title", out var altProp))
                    {
                        itemAlbum = altProp.GetString() ?? string.Empty;
                    }

                    if (alObj.TryGetProperty("cover_xl", out var xlProp) && !string.IsNullOrWhiteSpace(xlProp.GetString()))
                    {
                        coverUri = xlProp.GetString()!;
                    }
                }

                double durSec = item.TryGetProperty("duration", out var dProp) && dProp.TryGetDouble(out double d) ? d : 0;
                double score = ScoreTrackCandidate(expectedTitle, expectedArtist, expectedAlbum, expectedDurationSec, itemTitle, itemArtist, itemAlbum, durSec, requireExactVariant: false);
                if (score < 0.55)
                {
                    continue;
                }

                results.Add(new TrackDownloadCandidate
                {
                    Title = itemTitle,
                    Artist = itemArtist,
                    Album = itemAlbum,
                    Duration = durSec > 0 ? TimeSpan.FromSeconds(durSec) : TimeSpan.Zero,
                    CoverArtUri = coverUri,
                    ProviderLabel = "Deezer Hi-Fi",
                    QualityBadge = "FLAC 16-bit Lossless",
                    VersionBadge = DetectVersionBadge(itemTitle),
                    MatchScorePercent = (int)Math.Round(Math.Clamp(score * 100.0, 55.0, 99.0)),
                    DeezerTrackId = item.TryGetProperty("id", out var idProp) ? idProp.ToString() : null
                });
            }
        }
        catch
        {
            // Ignore
        }

        return results;
    }

    private async Task<List<TrackDownloadCandidate>> SearchGdStudioCandidatesAsync(
        string query,
        string expectedTitle,
        string expectedArtist,
        string expectedAlbum,
        double expectedDurationSec,
        CancellationToken cancellationToken)
    {
        var results = new List<TrackDownloadCandidate>();
        try
        {
            string searchUrl = $"https://music-api.gdstudio.xyz/api.php?types=search&source=netease&name={Uri.EscapeDataString(query)}&count=8";
            using var resp = await _httpClient.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return results;
            }

            string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string? urlId = item.TryGetProperty("url_id", out var uidProp)
                    ? uidProp.ToString()
                    : (item.TryGetProperty("id", out var idProp) ? idProp.ToString() : null);

                if (string.IsNullOrWhiteSpace(urlId))
                {
                    continue;
                }

                string itemName = item.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? string.Empty) : string.Empty;
                string itemAlbum = item.TryGetProperty("album", out var alProp) ? (alProp.GetString() ?? string.Empty) : string.Empty;
                string itemArtist = string.Empty;
                if (item.TryGetProperty("artist", out var aProp))
                {
                    if (aProp.ValueKind == JsonValueKind.Array)
                    {
                        itemArtist = string.Join(", ", aProp.EnumerateArray().Select(x => x.GetString() ?? string.Empty));
                    }
                    else if (aProp.ValueKind == JsonValueKind.String)
                    {
                        itemArtist = aProp.GetString() ?? string.Empty;
                    }
                }

                // Only include GDStudio candidates that genuinely match the requested song title and artist!
                if (ComputeTitleRatio(expectedTitle, itemName) < 0.58 || !ArtistsMatchStrict(expectedArtist, itemArtist))
                {
                    continue;
                }

                double score = ScoreTrackCandidate(expectedTitle, expectedArtist, expectedAlbum, expectedDurationSec, itemName, itemArtist, itemAlbum, 0, requireExactVariant: false);
                if (score < 0.60)
                {
                    continue;
                }

                results.Add(new TrackDownloadCandidate
                {
                    Title = itemName,
                    Artist = itemArtist,
                    Album = itemAlbum,
                    Duration = expectedDurationSec > 0 ? TimeSpan.FromSeconds(expectedDurationSec) : TimeSpan.Zero,
                    ProviderLabel = "GDStudio Direct FLAC",
                    QualityBadge = "Direct 24-bit/16-bit FLAC",
                    VersionBadge = DetectVersionBadge(itemName),
                    MatchScorePercent = (int)Math.Round(Math.Clamp(score * 100.0, 60.0, 99.0)),
                    GdStudioSource = "netease",
                    GdStudioUrlId = urlId
                });
            }
        }
        catch
        {
            // Ignore
        }

        return results;
    }

    private async Task<List<TrackDownloadCandidate>> SearchYouTubeCandidatesAsync(
        TrackItem originalTrack,
        string query,
        string expectedTitle,
        string expectedArtist,
        string expectedAlbum,
        double expectedDurationSec,
        CancellationToken cancellationToken)
    {
        var results = new List<TrackDownloadCandidate>();
        try
        {
            var search = await _youTubeMusicService.SearchCatalogAsync(query, cancellationToken).ConfigureAwait(false);
            foreach (var song in search.Songs.Take(8))
            {
                if (ComputeTitleRatio(expectedTitle, song.Title) < 0.48 && !ArtistsMatchStrict(expectedArtist, song.Artist))
                {
                    continue;
                }

                double score = ScoreTrackCandidate(
                    expectedTitle,
                    expectedArtist,
                    expectedAlbum,
                    expectedDurationSec,
                    song.Title,
                    song.Artist,
                    song.Album,
                    song.Duration.TotalSeconds,
                    requireExactVariant: false);

                if (score < 0.50)
                {
                    continue;
                }

                results.Add(new TrackDownloadCandidate
                {
                    Title = song.Title,
                    Artist = song.Artist,
                    Album = string.IsNullOrWhiteSpace(song.Album) ? expectedAlbum : song.Album,
                    Duration = song.Duration,
                    CoverArtUri = string.IsNullOrWhiteSpace(song.CoverArtUri) ? originalTrack.CoverArtUri : song.CoverArtUri,
                    ProviderLabel = "YouTube Music + Mornye",
                    QualityBadge = "Master Audio Stream",
                    VersionBadge = DetectVersionBadge(song.Title),
                    MatchScorePercent = (int)Math.Round(Math.Clamp(score * 100.0, 52.0, 98.0)),
                    YouTubeVideoId = song.YouTubeVideoId
                });
            }
        }
        catch
        {
            // Ignore
        }

        return results;
    }

    private async Task<string?> FetchDecryptedMornyeRegistryAsync(CancellationToken cancellationToken)
    {
        try
        {
            string rawBase64 = (await _httpClient.GetStringAsync(MornyeRegistryGistUrl, cancellationToken).ConfigureAwait(false)).Trim();
            string padded = rawBase64.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }

            byte[] raw = Convert.FromBase64String(padded);
            if (raw.Length < 12 + 16)
            {
                return null;
            }

            byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes("spotiflac:community:url:v1"));

            // Support both raw [12B nonce][ciphertext][16B tag] (Mornye-Python v1) and 1-byte version-prefixed payloads
            foreach (int offset in new[] { 0, 1 })
            {
                if (raw.Length <= offset + 12 + 16)
                {
                    continue;
                }

                byte[] nonce = raw[offset..(offset + 12)];
                byte[] tag = raw[^16..];
                byte[] ciphertext = raw[(offset + 12)..^16];

                foreach (string aadText in new[] { "spotiflac|community|url|v1", "mornye|community|url|v1" })
                {
                    try
                    {
                        byte[] plain = new byte[ciphertext.Length];
                        using var aes = new AesGcm(key, 16);
                        aes.Decrypt(nonce, ciphertext, tag, plain, Encoding.UTF8.GetBytes(aadText));
                        return Encoding.UTF8.GetString(plain);
                    }
                    catch
                    {
                        // Try next AAD / offset
                    }
                }
            }
        }
        catch
        {
            // Ignore registry fetch error
        }

        return null;
    }

    private async Task<bool> TryDownloadFileVerifiedAsync(
        string url,
        string destinationPath,
        bool requireFlacHeader,
        double expectedDurationSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            string tempPath = destinationPath + ".tmp.flac";
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            var fi = new FileInfo(tempPath);
            long minBytes = requireFlacHeader ? 1_500_000 : 250_000;
            if (fi.Length > minBytes)
            {
                if (requireFlacHeader && !HasValidFlacMagicHeader(tempPath))
                {
                    File.Delete(tempPath);
                    return false;
                }

                // Mornye Duration Guard: verify actual decoded audio duration matches expected track duration (±12s)
                // and is not a 30-second preview clip.
                if (!ValidateDownloadedAudioDuration(tempPath, expectedDurationSeconds))
                {
                    File.Delete(tempPath);
                    return false;
                }

                File.Move(tempPath, destinationPath, overwrite: true);
                return true;
            }

            File.Delete(tempPath);
        }
        catch
        {
            // Ignore
        }

        return false;
    }

    private static bool ValidateDownloadedAudioDuration(string filePath, double expectedDurationSeconds)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            double actualSec = tagFile.Properties.Duration.TotalSeconds;
            if (actualSec <= 0)
            {
                return true;
            }

            // Reject 30-second preview clips when full song is expected
            if (actualSec <= 35.0 && expectedDurationSeconds > 45.0)
            {
                return false;
            }

            if (expectedDurationSeconds > 20.0 && Math.Abs(expectedDurationSeconds - actualSec) > 12.0)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    private static bool HasValidFlacMagicHeader(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 4)
            {
                return false;
            }

            Span<byte> magic = stackalloc byte[4];
            int read = fs.Read(magic);
            // 'fLaC' = 0x66, 0x4C, 0x61, 0x43
            return read == 4 && magic[0] == 0x66 && magic[1] == 0x4C && magic[2] == 0x61 && magic[3] == 0x43;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> ResolveSourceAudioFileAsync(
        TrackItem track,
        EnrichedMornyeMetadata enrichedMeta,
        TrackDownloadCandidate? selectedCandidate,
        CancellationToken cancellationToken)
    {
        if (track.SourceType == MediaSourceType.Local &&
            !string.IsNullOrWhiteSpace(track.AudioSourceUri) &&
            File.Exists(track.AudioSourceUri))
        {
            return track.AudioSourceUri;
        }

        string videoId = selectedCandidate?.YouTubeVideoId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(videoId))
        {
            videoId = track.YouTubeVideoId;
        }

        if (string.IsNullOrWhiteSpace(videoId) &&
            track.AudioSourceUri.StartsWith("ytmusic://", StringComparison.OrdinalIgnoreCase))
        {
            videoId = track.AudioSourceUri["ytmusic://".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(videoId))
        {
            string query = $"{enrichedMeta.Artist} {enrichedMeta.Title}".Trim();
            var search = await _youTubeMusicService.SearchCatalogAsync(query, cancellationToken).ConfigureAwait(false);

            // Pick the best matching YouTube Music track using Mornye's strict scorer rather than blindly taking FirstOrDefault()
            TrackItem? bestMatch = null;
            double bestScore = 0.0;
            foreach (var cand in search.Songs)
            {
                double s = ScoreTrackCandidate(
                    enrichedMeta.Title,
                    enrichedMeta.Artist,
                    enrichedMeta.Album,
                    enrichedMeta.DurationSeconds,
                    cand.Title,
                    cand.Artist,
                    cand.Album,
                    cand.Duration.TotalSeconds,
                    requireExactVariant: false);

                if (s > bestScore)
                {
                    bestScore = s;
                    bestMatch = cand;
                }
            }

            videoId = (bestMatch ?? search.Songs.FirstOrDefault())?.YouTubeVideoId ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        return await _youTubeMusicService.ResolveAudioStreamUriAsync(videoId, cancellationToken).ConfigureAwait(false);
    }

    // =========================================================================
    // MORNYE TRACK IDENTITY & SCORING ENGINE (Ported from mornye/core/text_match.py)
    // =========================================================================

    private static string FoldText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value
            .Replace("&", " and ", StringComparison.Ordinal)
            .Replace("ø", "o", StringComparison.OrdinalIgnoreCase)
            .Replace("æ", "ae", StringComparison.OrdinalIgnoreCase)
            .Replace("ß", "ss", StringComparison.OrdinalIgnoreCase)
            .Normalize(NormalizationForm.FormKD)
            .ToLowerInvariant();

        var sb = new StringBuilder(normalized.Length);
        foreach (char ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return NonWordRegex.Replace(sb.ToString(), " ").Trim();
    }

    private static string StripTitleDecorations(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string text = value.Trim();
        for (int i = 0; i < 4; i++)
        {
            string stripped = TrailingCreditRegex.Replace(TitleNoiseRegex.Replace(text, ""), "").Trim();
            if (string.IsNullOrWhiteSpace(stripped) || stripped == text)
            {
                break;
            }

            text = stripped;
        }

        return text;
    }

    private static bool HasVariantConflict(string? expectedTitle, string? candidateTitle)
    {
        bool expectedMarked = DifferentRecordingRegex.IsMatch(expectedTitle ?? string.Empty);
        bool candidateMarked = DifferentRecordingRegex.IsMatch(candidateTitle ?? string.Empty);
        return expectedMarked != candidateMarked;
    }

    private static string DetectVersionBadge(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Original Studio";
        }

        var match = DifferentRecordingRegex.Match(title);
        if (match.Success)
        {
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Value.ToLowerInvariant());
        }

        if (title.Contains("remaster", StringComparison.OrdinalIgnoreCase))
        {
            return "Remastered";
        }

        if (title.Contains("deluxe", StringComparison.OrdinalIgnoreCase))
        {
            return "Deluxe Edition";
        }

        return "Original Studio";
    }

    private static double ComputeStringSimilarity(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return 0.0;
        }

        if (left == right)
        {
            return 1.0;
        }

        int n = left.Length;
        int m = right.Length;
        int[,] dp = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++) dp[i, 0] = i;
        for (int j = 0; j <= m; j++) dp[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }

        int maxLen = Math.Max(n, m);
        return maxLen == 0 ? 1.0 : 1.0 - ((double)dp[n, m] / maxLen);
    }

    private static double ComputeTitleRatio(string? expectedTitle, string? candidateTitle)
    {
        string leftFolded = FoldText(expectedTitle);
        string rightFolded = FoldText(candidateTitle);
        if (string.IsNullOrEmpty(leftFolded) || string.IsNullOrEmpty(rightFolded))
        {
            return 0.0;
        }

        if (leftFolded == rightFolded)
        {
            return 1.0;
        }

        double plain = ComputeStringSimilarity(leftFolded, rightFolded);
        string leftStripped = FoldText(StripTitleDecorations(expectedTitle));
        string rightStripped = FoldText(StripTitleDecorations(candidateTitle));
        double stripped = ComputeStringSimilarity(leftStripped, rightStripped);
        return Math.Max(plain, stripped);
    }

    private static bool TitlesMatchStrict(string? expectedTitle, string? candidateTitle)
    {
        string expectedFolded = FoldText(expectedTitle);
        string foundFolded = FoldText(candidateTitle);
        if (string.IsNullOrEmpty(expectedFolded) || string.IsNullOrEmpty(foundFolded))
        {
            return false;
        }

        if (expectedFolded == foundFolded)
        {
            return true;
        }

        string expectedCore = FoldText(StripTitleDecorations(expectedTitle));
        string foundCore = FoldText(StripTitleDecorations(candidateTitle));
        if (!string.IsNullOrEmpty(expectedCore) && !string.IsNullOrEmpty(foundCore) && expectedCore == foundCore)
        {
            return true;
        }

        return ComputeTitleRatio(expectedTitle, candidateTitle) >= 0.86;
    }

    private static List<string> SplitArtists(string? artistCredit)
    {
        if (string.IsNullOrWhiteSpace(artistCredit))
        {
            return [];
        }

        return ArtistSplitRegex
            .Split(artistCredit)
            .Select(FoldText)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static bool ArtistsMatchStrict(string? expectedArtist, string? foundArtist)
    {
        string expectedFolded = FoldText(expectedArtist);
        string foundFolded = FoldText(foundArtist);
        if (string.IsNullOrEmpty(expectedFolded) || string.IsNullOrEmpty(foundFolded))
        {
            return false;
        }

        if (expectedFolded == foundFolded)
        {
            return true;
        }

        var expectedParts = SplitArtists(expectedArtist);
        var foundParts = SplitArtists(foundArtist);

        foreach (string one in expectedParts)
        {
            foreach (string other in foundParts)
            {
                if (one == other)
                {
                    return true;
                }

                // Containment only when the shorter artist name has >= 2 words or >= 6 chars
                string shorter = one.Length <= other.Length ? one : other;
                string longer = one.Length <= other.Length ? other : one;
                if ((shorter.Contains(' ') || shorter.Length >= 6) && longer.Contains(shorter, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static double ComputeArtistRatio(string? expectedArtist, string? foundArtist)
    {
        if (ArtistsMatchStrict(expectedArtist, foundArtist))
        {
            return 1.0;
        }

        double best = ComputeTitleRatio(expectedArtist, foundArtist);
        foreach (string one in SplitArtists(expectedArtist))
        {
            foreach (string other in SplitArtists(foundArtist))
            {
                best = Math.Max(best, ComputeStringSimilarity(one, other));
            }
        }

        return best;
    }

    private static double ScoreTrackCandidate(
        string expectedTitle,
        string expectedArtist,
        string expectedAlbum,
        double expectedDurationSec,
        string candTitle,
        string candArtist,
        string candAlbum,
        double candDurationSec,
        bool requireExactVariant)
    {
        double titleScore = ComputeTitleRatio(expectedTitle, candTitle);
        double artistScore = !string.IsNullOrWhiteSpace(expectedArtist)
            ? ComputeArtistRatio(expectedArtist, candArtist)
            : 1.0;

        double score = !string.IsNullOrWhiteSpace(expectedArtist)
            ? (0.62 * titleScore + 0.38 * artistScore)
            : titleScore;

        // Mornye TITLE_FLOOR: an unrelated song by the same artist (e.g. "BRAT" vs "Cassiopeia")
        // is heavily penalized so it can never pass any threshold.
        if (titleScore < 0.58)
        {
            score *= 0.25;
        }

        if (requireExactVariant && HasVariantConflict(expectedTitle, candTitle))
        {
            score *= 0.50;
        }
        else if (HasVariantConflict(expectedTitle, candTitle))
        {
            score *= 0.84;
        }

        if (!string.IsNullOrWhiteSpace(expectedAlbum) && !string.IsNullOrWhiteSpace(candAlbum))
        {
            score = Math.Min(1.0, score + 0.05 * ComputeTitleRatio(expectedAlbum, candAlbum));
        }

        if (expectedDurationSec > 20.0 && candDurationSec > 20.0)
        {
            double delta = Math.Abs(expectedDurationSec - candDurationSec);
            if (delta <= 3.5)
            {
                score = Math.Min(1.0, score + 0.05);
            }
            else if (delta > 10.0)
            {
                score *= 0.68;
            }
        }

        return Math.Round(Math.Clamp(score, 0.0, 1.0), 4);
    }

    private static string ExtractTidalTrackTitle(JsonElement item)
    {
        string title = item.TryGetProperty("title", out var tProp) ? (tProp.GetString() ?? string.Empty).Trim() : string.Empty;
        string version = item.TryGetProperty("version", out var vProp) && vProp.ValueKind == JsonValueKind.String
            ? (vProp.GetString() ?? string.Empty).Trim()
            : string.Empty;

        if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(version) &&
            !title.Contains(version, StringComparison.OrdinalIgnoreCase))
        {
            return $"{title} ({version})";
        }

        return title;
    }

    private static string ExtractTidalArtistNames(JsonElement item)
    {
        if (item.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var a in arr.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var nProp) && !string.IsNullOrWhiteSpace(nProp.GetString()))
                {
                    names.Add(nProp.GetString()!);
                }
            }

            if (names.Count > 0)
            {
                return string.Join(", ", names);
            }
        }

        if (item.TryGetProperty("artist", out var aObj) && aObj.ValueKind == JsonValueKind.Object &&
            aObj.TryGetProperty("name", out var nameProp))
        {
            return nameProp.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static async Task TranscodeAudioFileAsync(
        string inputFilePath,
        string outputFilePath,
        bool isFlac,
        CancellationToken cancellationToken)
    {
        if (File.Exists(outputFilePath))
        {
            File.Delete(outputFilePath);
        }

        string? outDir = Path.GetDirectoryName(outputFilePath);
        string outFileName = Path.GetFileName(outputFilePath);
        if (string.IsNullOrWhiteSpace(outDir))
        {
            File.Copy(inputFilePath, outputFilePath, overwrite: true);
            return;
        }

        var sourceFile = await StorageFile.GetFileFromPathAsync(inputFilePath);
        var destFolder = await StorageFolder.GetFolderFromPathAsync(outDir);
        var destFile = await destFolder.CreateFileAsync(outFileName, CreationCollisionOption.ReplaceExisting);

        var profile = isFlac
            ? MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High)
            : MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High);

        var transcoder = new MediaTranscoder
        {
            AlwaysReencode = true,
            HardwareAccelerationEnabled = true
        };

        var prep = await transcoder.PrepareFileTranscodeAsync(sourceFile, destFile, profile);
        if (prep.CanTranscode)
        {
            await prep.TranscodeAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (!isFlac || HasValidFlacMagicHeader(outputFilePath))
            {
                return;
            }
        }

        // Fallback: If MediaTranscoder cannot directly read a DASH fragmented M4A container,
        // use Windows AudioGraph offline file transcoding so we ALWAYS produce a genuine multi-megabyte
        // FLAC (starting with 'fLaC' magic header) or 320kbps MP3, and NEVER copy a 128kbps .m4a as .flac!
        bool audioGraphSuccess = await TryTranscodeViaAudioGraphAsync(sourceFile, destFile, profile, cancellationToken).ConfigureAwait(false);
        if (!audioGraphSuccess && !isFlac)
        {
            File.Copy(inputFilePath, outputFilePath, overwrite: true);
        }
    }

    private static async Task<bool> TryTranscodeViaAudioGraphAsync(
        StorageFile sourceFile,
        StorageFile destFile,
        MediaEncodingProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.SystemDefault
            };

            var createResult = await AudioGraph.CreateAsync(settings).AsTask(cancellationToken).ConfigureAwait(false);
            if (createResult.Status != AudioGraphCreationStatus.Success)
            {
                return false;
            }

            using var graph = createResult.Graph;
            var inputResult = await graph.CreateFileInputNodeAsync(sourceFile).AsTask(cancellationToken).ConfigureAwait(false);
            if (inputResult.Status != AudioFileNodeCreationStatus.Success)
            {
                return false;
            }

            var outputResult = await graph.CreateFileOutputNodeAsync(destFile, profile).AsTask(cancellationToken).ConfigureAwait(false);
            if (outputResult.Status != AudioFileNodeCreationStatus.Success)
            {
                return false;
            }

            using var fileInput = inputResult.FileInputNode;
            using var fileOutput = outputResult.FileOutputNode;
            fileInput.AddOutgoingConnection(fileOutput);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fileInput.FileCompleted += (_, _) => tcs.TrySetResult(true);

            graph.Start();
            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                await tcs.Task.ConfigureAwait(false);
            }

            graph.Stop();
            var finalizeStatus = await fileOutput.FinalizeAsync().AsTask(cancellationToken).ConfigureAwait(false);
            return finalizeStatus == TranscodeFailureReason.None;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(byte[]? Bytes, string MimeType)> DownloadCoverArtBytesAsync(
        string primaryCoverUri,
        string fallbackCoverUri,
        CancellationToken cancellationToken)
    {
        foreach (string uri in new[] { primaryCoverUri, fallbackCoverUri })
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                continue;
            }

            try
            {
                if (File.Exists(uri))
                {
                    byte[] localBytes = await File.ReadAllBytesAsync(uri, cancellationToken).ConfigureAwait(false);
                    string mime = uri.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                    return (localBytes, mime);
                }

                if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    using var resp = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                        if (bytes.Length > 512)
                        {
                            string mime = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                            return (bytes, mime);
                        }
                    }
                }
            }
            catch
            {
                // Try next cover URI
            }
        }

        return (null, "image/jpeg");
    }

    private void ApplyMetadataTagsToFile(
        string filePath,
        EnrichedMornyeMetadata meta,
        IReadOnlyList<LyricLine> lyrics,
        byte[]? coverBytes,
        string coverMimeType)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var tag = tagFile.Tag;

            if (Options.IncludeTitleAndArtist)
            {
                tag.Title = meta.Title;
                tag.Performers = [meta.Artist];
                tag.AlbumArtists = [meta.AlbumArtist];
            }

            if (Options.IncludeAlbumAndTrackNumber)
            {
                tag.Album = meta.Album;
                tag.Track = meta.TrackNumber;
                tag.Disc = meta.DiscNumber;
            }

            if (Options.IncludeGenreAndReleaseYear)
            {
                tag.Genres = [meta.Genre];
                if (meta.ReleaseYear > 0)
                {
                    tag.Year = (uint)meta.ReleaseYear;
                }
            }

            var commentParts = new List<string>();
            if (Options.IncludeIsrcAndCopyright)
            {
                if (!string.IsNullOrWhiteSpace(meta.Copyright))
                {
                    tag.Copyright = meta.Copyright;
                }

                if (!string.IsNullOrWhiteSpace(meta.Isrc))
                {
                    commentParts.Add($"ISRC: {meta.Isrc}");
                }
            }

            if (Options.IncludeReplayGain && !string.IsNullOrWhiteSpace(meta.ReplayGain))
            {
                commentParts.Add($"ReplayGain: {meta.ReplayGain} (Peak: {meta.PeakAmplitude})");
            }

            if (commentParts.Count > 0)
            {
                tag.Comment = string.Join(" | ", commentParts);
            }

            if (lyrics.Count > 0)
            {
                if (Options.IncludeSynchronizedLyrics && lyrics.Any(l => l.IsSynchronized))
                {
                    tag.Lyrics = FormatAsLrcFileContent(meta, lyrics);
                }
                else if (Options.IncludeStaticLyrics)
                {
                    tag.Lyrics = string.Join(Environment.NewLine, lyrics.Select(l => l.Text));
                }
            }

            if (Options.IncludeHighResCoverArt && coverBytes is { Length: > 0 })
            {
                var pic = new TagLib.Picture(new TagLib.ByteVector(coverBytes))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = coverMimeType,
                    Description = "Cover"
                };
                tag.Pictures = [pic];
            }

            tagFile.Save();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TagLibSharp could not write all metadata tags to '{File}'.", filePath);
        }
    }

    private static string FormatAsLrcFileContent(EnrichedMornyeMetadata meta, IReadOnlyList<LyricLine> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[ti:{meta.Title}]");
        sb.AppendLine($"[ar:{meta.Artist}]");
        sb.AppendLine($"[al:{meta.Album}]");
        if (!string.IsNullOrWhiteSpace(meta.Isrc))
        {
            sb.AppendLine($"[id:{meta.Isrc}]");
        }

        sb.AppendLine();

        foreach (var line in lines)
        {
            int totalMins = (int)line.Timestamp.TotalMinutes;
            int secs = line.Timestamp.Seconds;
            int hundredths = line.Timestamp.Milliseconds / 10;
            sb.AppendLine($"[{totalMins:D2}:{secs:D2}.{hundredths:D2}]{line.Text}");
        }

        return sb.ToString();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (Array.IndexOf(invalid, c) < 0)
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }

        return sb.ToString().Trim();
    }
}
