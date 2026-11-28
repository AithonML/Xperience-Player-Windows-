using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.App.ViewModels;

/// <summary>
/// Dedicated partial class for Apple Music Canvas orchestration, same-album seamless reuse,
/// canonical canvas identity matching, and zero-stall offline/online local video normalization.
/// </summary>
public sealed partial class ShellViewModel
{
    private static readonly HashSet<string> GenericCanvasAlbumTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "single",
        "ep",
        "album",
        "unknown album",
        "unknown",
        "local album",
        "youtube music",
        "youtube music for you",
        "streaming",
        "radio mix",
        "recommended mix"
    };

    private static readonly HashSet<string> CanvasEditionModifierTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "deluxe", "edition", "remaster", "remastered", "version", "single", "ep",
        "expanded", "explicit", "clean", "anniversary", "bonus", "track", "tracks",
        "special", "rare", "n", "and", "collector", "collectors", "complete",
        "standard", "encore", "platinum", "gold", "tour", "digital", "original"
    };

    private string? _activeCanvasAlbumBaseKey;
    private string? _activeCanvasPrimaryArtistKey;
    private string? _activeCanvasCoverUri;
    private string? _activeCanvasCanonicalKey;
    private string? _activeCanvasAppleAlbumId;
    private string? _activeCanvasTrackId;

    private void ClearActiveTrackCanvasCore()
    {
        bool hadCanvas = !string.IsNullOrWhiteSpace(CurrentCanvasVideoUri) ||
                         !string.IsNullOrWhiteSpace(_activeCanvasCanonicalKey);

        _activeCanvasAlbumBaseKey = null;
        _activeCanvasPrimaryArtistKey = null;
        _activeCanvasCoverUri = null;
        _activeCanvasCanonicalKey = null;
        _activeCanvasAppleAlbumId = null;
        _activeCanvasTrackId = null;

        CurrentCanvasVideoUri = null;
        CurrentCanvasProviderBadge = string.Empty;

        if (hadCanvas)
        {
            CanvasVideoChanged?.Invoke(this, null);
        }
    }

    /// <summary>
    /// Called during optimistic track switching so switching to a DIFFERENT album immediately restores
    /// the new album's static cover (&lt;16ms), while switching within the SAME album keeps the
    /// currently playing Apple Canvas looping continuously with zero interruption or flash.
    /// </summary>
    private void PrepareCanvasForIncomingTrackSelection(TrackItem track)
    {
        if (!IsAppleMusicCanvasEnabled || track.Id == "empty_state_track")
        {
            CancelAndDisposeCts(ref _canvasLookupCts);
            ClearActiveTrackCanvasCore();
            return;
        }

        if (IsSameAlbumForCanvasReuse(track))
        {
            // Same album (or exact same cached Canvas URL/ID) -> keep Canvas running seamlessly!
            _activeCanvasTrackId = track.Id;
            return;
        }

        // Different album -> cancel previous lookup and restore static cover immediately
        CancelAndDisposeCts(ref _canvasLookupCts);
        ClearActiveTrackCanvasCore();
    }

    private void HandleIsAppleMusicCanvasEnabledChangedCore(bool value)
    {
        if (!value)
        {
            CancelAndDisposeCts(ref _canvasLookupCts);
            ClearActiveTrackCanvasCore();

            CancelAndDisposeCts(ref _albumCanvasLookupCts);
            CurrentAlbumDetailCanvasUri = null;
            AlbumDetailCanvasVideoChanged?.Invoke(this, null);
        }
        else
        {
            if (CurrentTrack is not null && CurrentTrack.Id != "empty_state_track")
            {
                _ = TriggerCanvasLookupForTrackCoreAsync(CurrentTrack);
            }

            if (ActiveSubView == WorkspaceSubView.AlbumDetail && CurrentAlbumDetail is not null)
            {
                _ = TriggerAlbumDetailCanvasLookupCoreAsync(CurrentAlbumDetail);
            }
        }
    }

    private async Task TriggerCanvasLookupForTrackCoreAsync(TrackItem track)
    {
        if (!IsAppleMusicCanvasEnabled || track.Id == "empty_state_track")
        {
            CancelAndDisposeCts(ref _canvasLookupCts);
            ClearActiveTrackCanvasCore();
            return;
        }

        // 1. Same-Album Reuse: if the active playing Canvas already belongs to this track's album
        //    (or resolves to the exact same Apple Canvas URL / Album Canvas ID), keep it playing
        //    continuously with ZERO stop, clear, reload, or re-animation!
        if (IsSameAlbumForCanvasReuse(track))
        {
            CancelAndDisposeCts(ref _canvasLookupCts);
            _activeCanvasTrackId = track.Id;
            UpdateActiveCanvasTrackMetadata(track);
            return;
        }

        var token = ReplaceCts(ref _canvasLookupCts).Token;

        // 2. Synchronous zero-I/O peek against in-memory & disk-persisted album Canvas cache
        var peeked = _canvasArtworkService.PeekCanvasArtwork(track.Title, track.Artist, track.Album);
        if (peeked is not null && !string.IsNullOrWhiteSpace(peeked.VideoUri))
        {
            if (IsSameResolvedCanvasAsActive(peeked))
            {
                _activeCanvasTrackId = track.Id;
                UpdateActiveCanvasTrackMetadata(track, peeked);
                return;
            }

            ApplyResolvedTrackCanvas(track, peeked);
            return;
        }

        // Different album with no synchronous cache hit -> clear previous album's canvas immediately
        // so the new album's static cover stays cleanly visible while the new canvas resolves.
        ClearActiveTrackCanvasCore();

        try
        {
            // 3. Fast Offline Check: downloaded/local track with companion .canvas.mp4 sidecar on disk
            if (track.SourceType == MediaSourceType.Local &&
                !string.IsNullOrWhiteSpace(track.AudioSourceUri) &&
                File.Exists(track.AudioSourceUri))
            {
                string? dir = Path.GetDirectoryName(track.AudioSourceUri);
                string baseName = Path.GetFileNameWithoutExtension(track.AudioSourceUri);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    string offlineCanvasSidecar = Path.Combine(dir, $"{baseName}.canvas.mp4");
                    string offlineMp4Sidecar = Path.Combine(dir, $"{baseName}.mp4");
                    string? foundOfflineCanvas = File.Exists(offlineCanvasSidecar)
                        ? offlineCanvasSidecar
                        : (File.Exists(offlineMp4Sidecar) && !string.Equals(offlineMp4Sidecar, track.AudioSourceUri, StringComparison.OrdinalIgnoreCase)
                            ? offlineMp4Sidecar
                            : null);

                    if (!string.IsNullOrWhiteSpace(foundOfflineCanvas))
                    {
                        // Ensure legacy fragmented offline .canvas.mp4 sidecars are normalized to flat MP4 for gapless looping
                        string? loopableOfflinePath = await _canvasArtworkService
                            .EnsureLocalLoopableVideoAsync(foundOfflineCanvas, token)
                            .ConfigureAwait(false);
                        string resolvedOfflinePath = !string.IsNullOrWhiteSpace(loopableOfflinePath)
                            ? loopableOfflinePath!
                            : foundOfflineCanvas;

                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        var offlineResult = new CanvasArtworkResult(
                            VideoUri: resolvedOfflinePath,
                            ProviderName: "Offline Apple Canvas",
                            CanonicalCanvasKey: Path.GetFullPath(resolvedOfflinePath));

                        _dispatcherQueue.TryEnqueue(() =>
                        {
                            if (!token.IsCancellationRequested &&
                                CurrentTrack?.Id == track.Id &&
                                IsAppleMusicCanvasEnabled)
                            {
                                ApplyResolvedTrackCanvas(track, offlineResult);
                            }
                        });
                        return;
                    }
                }
            }

            // 4. Brief debounce for rapid track skipping without delaying Canvas startup
            await Task.Delay(80, token).ConfigureAwait(false);

            var result = await Task.Run(
                () => _canvasArtworkService.GetCanvasArtworkAsync(
                    track.Title,
                    track.Artist,
                    track.Album,
                    token),
                token).ConfigureAwait(false);

            if (token.IsCancellationRequested || CurrentTrack?.Id != track.Id)
            {
                return;
            }

            if (result is not null && !string.IsNullOrWhiteSpace(result.VideoUri))
            {
                RecordExternalCanvasDataUsage(track, result.VideoUri, result.ProviderName);
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (!token.IsCancellationRequested &&
                        CurrentTrack?.Id == track.Id &&
                        IsAppleMusicCanvasEnabled)
                    {
                        ApplyResolvedTrackCanvas(track, result);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by next track change
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Apple Music Canvas lookup skipped for track {Title}.", track.Title);
        }
    }

    private void ApplyResolvedTrackCanvas(TrackItem track, CanvasArtworkResult result)
    {
        if (IsSameResolvedCanvasAsActive(result))
        {
            _activeCanvasTrackId = track.Id;
            UpdateActiveCanvasTrackMetadata(track, result);
            CurrentCanvasProviderBadge = result.ProviderName;
            return;
        }

        _activeCanvasTrackId = track.Id;
        UpdateActiveCanvasTrackMetadata(track, result);
        CurrentCanvasProviderBadge = result.ProviderName;
        CurrentCanvasVideoUri = result.VideoUri;
        CanvasVideoChanged?.Invoke(this, result.VideoUri);
    }

    private void UpdateActiveCanvasTrackMetadata(TrackItem track, CanvasArtworkResult? result = null)
    {
        if (IsSpecificRealAlbumTitle(track.Album))
        {
            string baseAlbum = ExtractCanonicalBaseAlbum(track.Album);
            if (!string.IsNullOrWhiteSpace(baseAlbum))
            {
                _activeCanvasAlbumBaseKey = baseAlbum;
            }
        }

        string primaryArtist = NormalizeCanvasToken(ExtractPrimaryArtistName(track.Artist));
        if (!string.IsNullOrWhiteSpace(primaryArtist))
        {
            _activeCanvasPrimaryArtistKey = primaryArtist;
        }

        if (!string.IsNullOrWhiteSpace(track.CoverArtUri))
        {
            _activeCanvasCoverUri = NormalizeCoverUriForComparison(track.CoverArtUri);
        }

        if (result is not null)
        {
            if (!string.IsNullOrWhiteSpace(result.CanonicalCanvasKey))
            {
                _activeCanvasCanonicalKey = result.CanonicalCanvasKey;
            }

            if (!string.IsNullOrWhiteSpace(result.AppleAlbumId))
            {
                _activeCanvasAppleAlbumId = result.AppleAlbumId;
            }
        }
    }

    private bool IsSameAlbumForCanvasReuse(TrackItem incomingTrack)
    {
        if (!IsAppleMusicCanvasEnabled || string.IsNullOrWhiteSpace(CurrentCanvasVideoUri))
        {
            return false;
        }

        // Exact same track re-selected
        if (!string.IsNullOrWhiteSpace(_activeCanvasTrackId) &&
            string.Equals(_activeCanvasTrackId, incomingTrack.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check album-title + artist/cover identity when both belong to a specific real album
        if (!string.IsNullOrWhiteSpace(_activeCanvasAlbumBaseKey) &&
            IsSpecificRealAlbumTitle(incomingTrack.Album))
        {
            string incomingAlbumBase = ExtractCanonicalBaseAlbum(incomingTrack.Album);
            if (!string.IsNullOrWhiteSpace(incomingAlbumBase) &&
                AreFaithfulSameAlbum(_activeCanvasAlbumBaseKey, incomingAlbumBase))
            {
                string incomingArtist = NormalizeCanvasToken(ExtractPrimaryArtistName(incomingTrack.Artist));
                bool artistMatches =
                    string.IsNullOrWhiteSpace(_activeCanvasPrimaryArtistKey) ||
                    string.IsNullOrWhiteSpace(incomingArtist) ||
                    string.Equals(_activeCanvasPrimaryArtistKey, incomingArtist, StringComparison.OrdinalIgnoreCase) ||
                    _activeCanvasPrimaryArtistKey.Contains(incomingArtist, StringComparison.OrdinalIgnoreCase) ||
                    incomingArtist.Contains(_activeCanvasPrimaryArtistKey, StringComparison.OrdinalIgnoreCase);

                string incomingCover = NormalizeCoverUriForComparison(incomingTrack.CoverArtUri);
                bool coverMatches =
                    !string.IsNullOrWhiteSpace(_activeCanvasCoverUri) &&
                    !string.IsNullOrWhiteSpace(incomingCover) &&
                    string.Equals(_activeCanvasCoverUri, incomingCover, StringComparison.OrdinalIgnoreCase);

                if (artistMatches || coverMatches)
                {
                    return true;
                }
            }
        }

        // Check synchronous cache peek for canonical Canvas URL / Apple Album ID match
        var peeked = _canvasArtworkService.PeekCanvasArtwork(incomingTrack.Title, incomingTrack.Artist, incomingTrack.Album);
        if (peeked is not null && IsSameResolvedCanvasAsActive(peeked))
        {
            return true;
        }

        return false;
    }

    private bool IsSameResolvedCanvasAsActive(CanvasArtworkResult candidate)
    {
        if (string.IsNullOrWhiteSpace(CurrentCanvasVideoUri))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(candidate.VideoUri) &&
            string.Equals(CurrentCanvasVideoUri, candidate.VideoUri, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_activeCanvasCanonicalKey) &&
            !string.IsNullOrWhiteSpace(candidate.CanonicalCanvasKey) &&
            string.Equals(_activeCanvasCanonicalKey, candidate.CanonicalCanvasKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_activeCanvasAppleAlbumId) &&
            !string.IsNullOrWhiteSpace(candidate.AppleAlbumId) &&
            string.Equals(_activeCanvasAppleAlbumId, candidate.AppleAlbumId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private async Task TriggerAlbumDetailCanvasLookupCoreAsync(AlbumDetailProfile albumDetail)
    {
        var token = ReplaceCts(ref _albumCanvasLookupCts).Token;

        if (!IsAppleMusicCanvasEnabled || string.IsNullOrWhiteSpace(albumDetail.Title))
        {
            return;
        }

        // Instant 0ms hit if this album's flat MP4 Canvas is already cached locally
        var peeked = _canvasArtworkService.PeekCanvasArtwork(albumDetail.Title, albumDetail.Artist, albumDetail.Title);
        if (peeked is not null && !string.IsNullOrWhiteSpace(peeked.VideoUri))
        {
            if (ActiveSubView == WorkspaceSubView.AlbumDetail && IsAppleMusicCanvasEnabled)
            {
                if (!string.Equals(CurrentAlbumDetailCanvasUri, peeked.VideoUri, StringComparison.OrdinalIgnoreCase))
                {
                    CurrentAlbumDetailCanvasUri = peeked.VideoUri;
                    AlbumDetailCanvasVideoChanged?.Invoke(this, peeked.VideoUri);
                }
            }

            return;
        }

        try
        {
            await Task.Delay(80, token).ConfigureAwait(false);

            var result = await Task.Run(
                () => _canvasArtworkService.GetCanvasArtworkAsync(
                    albumDetail.Title,
                    albumDetail.Artist,
                    albumDetail.Title,
                    token),
                token).ConfigureAwait(false);

            if (token.IsCancellationRequested || ActiveSubView != WorkspaceSubView.AlbumDetail)
            {
                return;
            }

            if (result is not null && !string.IsNullOrWhiteSpace(result.VideoUri))
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (!token.IsCancellationRequested &&
                        ActiveSubView == WorkspaceSubView.AlbumDetail &&
                        IsAppleMusicCanvasEnabled &&
                        string.Equals(CurrentAlbumDetail?.Title, albumDetail.Title, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.Equals(CurrentAlbumDetailCanvasUri, result.VideoUri, StringComparison.OrdinalIgnoreCase))
                        {
                            CurrentAlbumDetailCanvasUri = result.VideoUri;
                            AlbumDetailCanvasVideoChanged?.Invoke(this, result.VideoUri);
                        }
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled on navigation
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Album detail Apple Music Canvas lookup skipped for {Album}.", albumDetail.Title);
        }
    }

    private static bool IsSpecificRealAlbumTitle(string? album)
    {
        if (string.IsNullOrWhiteSpace(album))
        {
            return false;
        }

        return !GenericCanvasAlbumTitles.Contains(album.Trim());
    }

    private static string ExtractCanonicalBaseAlbum(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string s = raw.Trim();
        if (s.EndsWith(" - Single", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^9].Trim();
        }
        else if (s.EndsWith(" - EP", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^5].Trim();
        }

        int parenIdx = s.IndexOf('(');
        if (parenIdx > 1)
        {
            s = s[..parenIdx];
        }

        int bracketIdx = s.IndexOf('[');
        if (bracketIdx > 1)
        {
            s = s[..bracketIdx];
        }

        int dashIdx = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 1)
        {
            s = s[..dashIdx];
        }

        return NormalizeCanvasToken(s);
    }

    private static bool AreFaithfulSameAlbum(string baseA, string baseB)
    {
        if (string.IsNullOrWhiteSpace(baseA) || string.IsNullOrWhiteSpace(baseB))
        {
            return false;
        }

        if (string.Equals(baseA, baseB, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string shorter = baseA.Length <= baseB.Length ? baseA : baseB;
        string longer = baseA.Length <= baseB.Length ? baseB : baseA;
        if (shorter.Length >= 3 && longer.StartsWith(shorter + " ", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = longer[(shorter.Length + 1)..].Trim();
            string[] extraTokens = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (extraTokens.Length > 0 && extraTokens.All(t => CanvasEditionModifierTokens.Contains(t)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractPrimaryArtistName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string s = raw.Trim();
        if (s.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^8].Trim();
        }

        string[] parts = s.Split([',', '&', ';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts[0] : s;
    }

    private static string NormalizeCanvasToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string lower = value.ToLowerInvariant().Replace('$', 's');
        string alnum = CanvasNonAlnumRegex().Replace(lower, " ");
        return CanvasSpacesRegex().Replace(alnum, " ").Trim();
    }

    private static string NormalizeCoverUriForComparison(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return string.Empty;
        }

        string trimmed = uri.Trim();
        int eqIdx = trimmed.IndexOf("=w", StringComparison.OrdinalIgnoreCase);
        if (eqIdx > 10)
        {
            return trimmed[..eqIdx];
        }

        return trimmed;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}\s]")]
    private static partial Regex CanvasNonAlnumRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CanvasSpacesRegex();
}
