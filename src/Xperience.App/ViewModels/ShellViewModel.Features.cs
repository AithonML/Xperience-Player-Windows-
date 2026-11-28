using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.App.ViewModels;

public sealed partial class ShellViewModel
{
    private CancellationTokenSource? _musicVideoLookupCts;
    private CancellationTokenSource? _fullscreenVideoErrorCts;
    private bool _pendingAutoSwitchToVideoMode;
    private bool _hasRetriedMusicVideoForCurrentTrack;
    private string? _resolvedMusicVideoTrackId;
    private OfficialMusicVideoMetadata? _currentOfficialVideoMetadata;
    private bool _isSwitchingAudioVideoVariant;

    /// <summary>
    /// Gets the artist's official music videos ("Videos oficiales") displayed on the Artist Detail screen.
    /// </summary>
    public ObservableCollection<TrackItem> ArtistOfficialVideos { get; } = [];

    /// <summary>
    /// Gets the auto-generated smart playlist suggestion cards ("Sugerencias para ti") displayed in Playlists and Home.
    /// These are NOT saved to the user's library unless the user explicitly clicks "Guardar en biblioteca".
    /// </summary>
    public ObservableCollection<PlaylistCardViewModel> SmartSuggestedPlaylistCards { get; } = [];

    /// <summary>
    /// Raised when the resolved music video stream URI or the active Audio/Video switcher mode changes.
    /// </summary>
    public event EventHandler<(string? StreamUri, bool IsVideoModeActive)>? MusicVideoStateChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullscreenAudioVideoSwitcherVisibility))]
    [NotifyPropertyChangedFor(nameof(SidebarNowPlayingTitleVisibility))]
    public partial bool IsCurrentTrackMusicVideoAvailable { get; set; }

    [ObservableProperty]
    public partial bool IsResolvingMusicVideo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullscreenSongMode))]
    [NotifyPropertyChangedFor(nameof(FullscreenSongTabHighlightOpacity))]
    [NotifyPropertyChangedFor(nameof(FullscreenVideoTabHighlightOpacity))]
    [NotifyPropertyChangedFor(nameof(FullscreenVideoLayerVisibility))]
    [NotifyPropertyChangedFor(nameof(FullscreenCoverOuterWidth))]
    [NotifyPropertyChangedFor(nameof(FullscreenCoverOuterHeight))]
    public partial bool IsFullscreenVideoMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullscreenVideoLayerVisibility))]
    public partial string? CurrentMusicVideoStreamUri { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullscreenVideoErrorVisibility))]
    public partial string FullscreenVideoErrorMessage { get; set; } = string.Empty;

    public Visibility FullscreenVideoErrorVisibility =>
        !string.IsNullOrWhiteSpace(FullscreenVideoErrorMessage) ? Visibility.Visible : Visibility.Collapsed;

    public bool IsFullscreenSongMode => !IsFullscreenVideoMode;

    public Visibility FullscreenAudioVideoSwitcherVisibility =>
        IsCurrentTrackMusicVideoAvailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SidebarNowPlayingTitleVisibility =>
        IsCurrentTrackMusicVideoAvailable ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FullscreenVideoLayerVisibility =>
        IsFullscreenVideoMode && !string.IsNullOrWhiteSpace(CurrentMusicVideoStreamUri)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public double FullscreenSongTabHighlightOpacity => IsFullscreenVideoMode ? 0.0 : 1.0;

    public double FullscreenVideoTabHighlightOpacity => IsFullscreenVideoMode ? 1.0 : 0.0;

    public double FullscreenCoverOuterWidth => IsFullscreenVideoMode ? 452.0 : 356.0;

    public double FullscreenCoverOuterHeight => IsFullscreenVideoMode ? 254.0 : 356.0;

    public Visibility HasArtistLatestReleaseVisibility =>
        CurrentArtistDetail?.HasLatestRelease == true ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HasArtistOfficialVideosVisibility =>
        ArtistOfficialVideos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HasSmartSuggestedPlaylistsVisibility =>
        SmartSuggestedPlaylistCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CurrentPlaylistSaveToLibraryVisibility =>
        CurrentPlaylistDetail is { IsSmartSuggestion: true, IsSavedToLibrary: false }
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility CurrentPlaylistSavedBadgeVisibility =>
        CurrentPlaylistDetail is { IsSmartSuggestion: true, IsSavedToLibrary: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility CurrentPlaylistDeleteButtonVisibility =>
        CurrentPlaylistDetail is { IsSmartSuggestion: true }
            ? Visibility.Collapsed
            : Visibility.Visible;

    public string CurrentPlaylistTypeAccentLabel =>
        CurrentPlaylistDetail is { IsSmartSuggestion: true }
            ? (!string.IsNullOrWhiteSpace(CurrentPlaylistDetail.SmartBadgeText)
                ? $"Suggested for You • {CurrentPlaylistDetail.SmartBadgeText}"
                : "Suggested for You")
            : "Playlist";

    [RelayCommand]
    public void DismissFullscreenVideoError()
    {
        CancelAndDisposeCts(ref _fullscreenVideoErrorCts);
        FullscreenVideoErrorMessage = string.Empty;
    }

    private void ShowFullscreenVideoError(string message)
    {
        var token = ReplaceCts(ref _fullscreenVideoErrorCts).Token;

        FullscreenVideoErrorMessage = message;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4.5), token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            FullscreenVideoErrorMessage = string.Empty;
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Dismissed manually or replaced by a newer notice
            }
        }, token);
    }

    [RelayCommand]
    public async Task SelectFullscreenSongModeAsync()
    {
        _pendingAutoSwitchToVideoMode = false;
        DismissFullscreenVideoError();
        if (!IsFullscreenVideoMode || _isSwitchingAudioVideoVariant)
        {
            return;
        }

        _isSwitchingAudioVideoVariant = true;
        try
        {
            var meta = _currentOfficialVideoMetadata;
            var track = CurrentTrack;
            await _audioPlayerService.SwitchTrackAudioVariantAsync(
                officialVideoId: meta?.OfficialVideoId,
                useVideoAudio: false,
                resolveTargetPosition: (_, _, videoPos) =>
                    meta is not null
                        ? meta.MapVideoToSongPosition(videoPos, track?.Duration)
                        : videoPos);

            IsFullscreenVideoMode = false;
            MusicVideoStateChanged?.Invoke(this, (CurrentMusicVideoStreamUri, false));
            UpdateActiveLyricForPosition(_audioPlayerService.Position, forceNotify: true);
        }
        finally
        {
            _isSwitchingAudioVideoVariant = false;
        }
    }

    [RelayCommand]
    public async Task SelectFullscreenVideoModeAsync()
    {
        DismissFullscreenVideoError();
        if (_isSwitchingAudioVideoVariant)
        {
            return;
        }

        if (IsResolvingMusicVideo)
        {
            _pendingAutoSwitchToVideoMode = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentMusicVideoStreamUri))
        {
            if (CurrentTrack is not null && CurrentTrack.Id != "empty_state_track")
            {
                _pendingAutoSwitchToVideoMode = true;
                await TriggerMusicVideoLookupForTrackAsync(CurrentTrack);
            }

            return;
        }

        if (!IsFullscreenVideoMode)
        {
            await ActivateVideoAudioAndVisualModeAsync(CurrentTrack, CurrentMusicVideoStreamUri);
        }
    }

    private async Task ActivateVideoAudioAndVisualModeAsync(TrackItem? track, string? videoStreamUri)
    {
        if (track is null || string.IsNullOrWhiteSpace(videoStreamUri) || _isSwitchingAudioVideoVariant)
        {
            return;
        }

        _isSwitchingAudioVideoVariant = true;
        try
        {
            var meta = _currentOfficialVideoMetadata;
            string? officialId = meta?.OfficialVideoId;
            if (string.IsNullOrWhiteSpace(officialId))
            {
                officialId = track.OfficialVideoId;
            }

            bool switched = await _audioPlayerService.SwitchTrackAudioVariantAsync(
                officialVideoId: officialId,
                useVideoAudio: true,
                resolveTargetPosition: (songPath, videoAudioPath, songPos) =>
                {
                    if (meta is not null && meta.Segments.Count == 0 &&
                        !string.IsNullOrWhiteSpace(songPath) &&
                        !string.IsNullOrWhiteSpace(videoAudioPath))
                    {
                        var computed = Services.Audio.AudioOffsetAligner.ComputeAlignmentSegments(songPath, videoAudioPath);
                        if (computed.Count > 0)
                        {
                            meta = meta with { Segments = computed };
                            _currentOfficialVideoMetadata = meta;
                        }
                    }

                    return meta is not null
                        ? meta.MapSongToVideoPosition(songPos)
                        : songPos;
                });

            if (!switched && !_audioPlayerService.IsVideoAudioModeActive)
            {
                ShowFullscreenVideoError("Could not switch audio to the official music video.");
                return;
            }

            IsFullscreenVideoMode = true;
            MusicVideoStateChanged?.Invoke(this, (videoStreamUri, true));
            UpdateActiveLyricForPosition(_audioPlayerService.Position, forceNotify: true);
        }
        finally
        {
            _isSwitchingAudioVideoVariant = false;
        }
    }

    internal async Task HandleMusicVideoPlaybackFailureAsync()
    {
        var track = CurrentTrack;
        if (track is null || track.Id == "empty_state_track")
        {
            IsFullscreenVideoMode = false;
            return;
        }

        if (_hasRetriedMusicVideoForCurrentTrack)
        {
            IsFullscreenVideoMode = false;
            ShowFullscreenVideoError("Could not play the official music video for this track.");
            return;
        }

        _hasRetriedMusicVideoForCurrentTrack = true;
        if (_youTubeMusicService is Services.YouTubeMusicService ytService)
        {
            ytService.InvalidateCachedMusicVideo(track);
        }

        _pendingAutoSwitchToVideoMode = true;
        await TriggerMusicVideoLookupForTrackAsync(track, forceLocalDownload: true);
    }

    [RelayCommand]
    public async Task OpenArtistLatestReleaseAsync()
    {
        var latest = CurrentArtistDetail?.LatestRelease;
        if (latest is null)
        {
            return;
        }

        await OpenAlbumDetailAsync(latest);
    }

    [RelayCommand]
    public async Task PlayArtistLatestReleaseAsync()
    {
        var latest = CurrentArtistDetail?.LatestRelease;
        if (latest is null)
        {
            return;
        }

        try
        {
            var detail = await _catalogService.GetAlbumDetailAsync(latest);
            if (detail.Tracks.Count > 0)
            {
                await PlayTrackFromListAsync(detail.Tracks[0], detail.Tracks);
                return;
            }
        }
        catch
        {
            // Fallback to opening Album Detail
        }

        await OpenAlbumDetailAsync(latest);
    }

    public async Task PlayArtistOfficialVideoAsync(TrackItem? videoTrack)
    {
        if (videoTrack is null)
        {
            return;
        }

        _pendingAutoSwitchToVideoMode = true;
        var list = ArtistOfficialVideos.Count > 0 ? ArtistOfficialVideos.ToList() : [videoTrack];
        await PlayTrackFromListAsync(videoTrack, list);
        if (!IsFullscreenNowPlayingOpen)
        {
            OpenFullscreenNowPlayingCommand.Execute(null);
        }
    }

    public async Task RefreshSmartSuggestedPlaylistsAsync(
        IReadOnlyList<UserPlaylist>? preloaded = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var suggestions = preloaded ?? await _catalogService.GetSmartPlaylistSuggestionsAsync(cancellationToken);
            SmartSuggestedPlaylistCards.Clear();
            foreach (var pl in suggestions)
            {
                SmartSuggestedPlaylistCards.Add(new PlaylistCardViewModel(pl));
            }

            OnPropertyChanged(nameof(HasSmartSuggestedPlaylistsVisibility));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh smart suggested playlists.");
        }
    }

    [RelayCommand]
    public async Task SaveSmartPlaylistToLibraryAsync(UserPlaylist? smartPlaylist)
    {
        var target = smartPlaylist ?? CurrentPlaylistDetail;
        if (target is null)
        {
            return;
        }

        var saved = await _catalogService.SaveSmartPlaylistToLibraryAsync(target);
        target.IsSavedToLibrary = true;
        _isPlaylistsDirty = true;

        await RefreshPlaylistsAsync(refreshSmartSuggestions: false);
        await RefreshSmartSuggestedPlaylistsAsync();

        if (ActiveSubView == WorkspaceSubView.PlaylistDetail &&
            CurrentPlaylistDetail is not null &&
            string.Equals(CurrentPlaylistDetail.Title, saved.Title, StringComparison.OrdinalIgnoreCase))
        {
            CurrentPlaylistDetail.IsSavedToLibrary = true;
            CurrentPlaylistCard = new PlaylistCardViewModel(CurrentPlaylistDetail);
            OnPropertyChanged(nameof(CurrentPlaylistSaveToLibraryVisibility));
            OnPropertyChanged(nameof(CurrentPlaylistSavedBadgeVisibility));
            OnPropertyChanged(nameof(CurrentPlaylistDeleteButtonVisibility));
            OnPropertyChanged(nameof(CurrentPlaylistTypeAccentLabel));
        }

        StatusMessage = $"Saved to your library: '{saved.Title}'";
    }

    private void ResetMusicVideoStateForTrackChange()
    {
        CancelAndDisposeCts(ref _musicVideoLookupCts);
        DismissFullscreenVideoError();
        _resolvedMusicVideoTrackId = null;
        _currentOfficialVideoMetadata = null;
        _hasRetriedMusicVideoForCurrentTrack = false;
        IsFullscreenVideoMode = false;
        IsCurrentTrackMusicVideoAvailable = false;
        CurrentMusicVideoStreamUri = null;
        IsResolvingMusicVideo = false;
        MusicVideoStateChanged?.Invoke(this, (null, false));
    }

    internal async Task CheckOfficialMusicVideoAvailabilityAsync(TrackItem track)
    {
        if (track is null || track.Id == "empty_state_track" || string.IsNullOrWhiteSpace(track.Title))
        {
            ResetMusicVideoStateForTrackChange();
            return;
        }

        if (_pendingAutoSwitchToVideoMode || track.IsOfficialVideoItem)
        {
            await TriggerMusicVideoLookupForTrackAsync(track);
            return;
        }

        ResetMusicVideoStateForTrackChange();
        var token = ReplaceCts(ref _musicVideoLookupCts).Token;

        try
        {
            await Task.Delay(180, token);
            var metadata = await Task.Run(
                () => _youTubeMusicService.ResolveOfficialMusicVideoMetadataAsync(track, token),
                token);

            if (token.IsCancellationRequested || CurrentTrack?.Id != track.Id)
            {
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || CurrentTrack?.Id != track.Id)
                {
                    return;
                }

                _currentOfficialVideoMetadata = metadata;
                IsCurrentTrackMusicVideoAvailable = metadata is not null;
                if (metadata is not null && !string.IsNullOrWhiteSpace(metadata.OfficialVideoId))
                {
                    track.OfficialVideoId = metadata.OfficialVideoId;
                }

                if (_pendingAutoSwitchToVideoMode && metadata is not null)
                {
                    _ = TriggerMusicVideoLookupForTrackAsync(track);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Official music video availability check skipped for '{Title}'.", track.Title);
        }
    }

    internal async Task TriggerMusicVideoLookupForTrackAsync(TrackItem track, bool forceLocalDownload = false)
    {
        bool wantsImmediateVideo = _pendingAutoSwitchToVideoMode || (track?.IsOfficialVideoItem == true);

        if (!wantsImmediateVideo && !forceLocalDownload)
        {
            await CheckOfficialMusicVideoAvailabilityAsync(track!);
            return;
        }

        bool autoSwitch = wantsImmediateVideo;
        _pendingAutoSwitchToVideoMode = false;
        if (!autoSwitch && !forceLocalDownload)
        {
            _hasRetriedMusicVideoForCurrentTrack = false;
        }

        DismissFullscreenVideoError();
        var token = ReplaceCts(ref _musicVideoLookupCts).Token;

        IsFullscreenVideoMode = false;
        CurrentMusicVideoStreamUri = null;
        IsResolvingMusicVideo = true;
        MusicVideoStateChanged?.Invoke(this, (null, false));

        if (track is null || track.Id == "empty_state_track" || string.IsNullOrWhiteSpace(track.Title))
        {
            _resolvedMusicVideoTrackId = null;
            IsCurrentTrackMusicVideoAvailable = false;
            IsResolvingMusicVideo = false;
            return;
        }

        _resolvedMusicVideoTrackId = track.Id;

        try
        {
            if (!autoSwitch && !forceLocalDownload)
            {
                await Task.Delay(150, token);
            }

            var resolvedVideo = await Task.Run(
                () => _youTubeMusicService is Services.YouTubeMusicService ytService
                    ? ytService.ResolveMusicVideoStreamAsync(track, forceLocalDownload, token)
                    : _youTubeMusicService.ResolveMusicVideoStreamAsync(track, token),
                token);

            if (token.IsCancellationRequested || CurrentTrack?.Id != track.Id)
            {
                return;
            }

            _dispatcherQueue.TryEnqueue(async () =>
            {
                IsResolvingMusicVideo = false;
                if (token.IsCancellationRequested || CurrentTrack?.Id != track.Id)
                {
                    return;
                }

                bool shouldSwitchNow = autoSwitch || _pendingAutoSwitchToVideoMode;
                _pendingAutoSwitchToVideoMode = false;

                if (resolvedVideo is not null && !string.IsNullOrWhiteSpace(resolvedVideo.StreamUri))
                {
                    _currentOfficialVideoMetadata ??= new OfficialMusicVideoMetadata(
                        SongVideoId: resolvedVideo.SongVideoId ?? track.YouTubeVideoId,
                        OfficialVideoId: resolvedVideo.VideoId,
                        Segments: resolvedVideo.Segments ?? []);
                    if (resolvedVideo.Segments is { Count: > 0 } && _currentOfficialVideoMetadata.Segments.Count == 0)
                    {
                        _currentOfficialVideoMetadata = _currentOfficialVideoMetadata with { Segments = resolvedVideo.Segments };
                    }

                    CurrentMusicVideoStreamUri = resolvedVideo.StreamUri;
                    IsCurrentTrackMusicVideoAvailable = true;
                    if (shouldSwitchNow)
                    {
                        await ActivateVideoAudioAndVisualModeAsync(track, resolvedVideo.StreamUri);
                    }
                    else
                    {
                        MusicVideoStateChanged?.Invoke(this, (resolvedVideo.StreamUri, false));
                    }
                }
                else
                {
                    IsCurrentTrackMusicVideoAvailable = false;
                    if (forceLocalDownload || shouldSwitchNow)
                    {
                        ShowFullscreenVideoError("Official music video is not available for this track.");
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Cancelled by next track change
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Music video lookup skipped for '{Title}'.", track.Title);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (string.Equals(_resolvedMusicVideoTrackId, track.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _resolvedMusicVideoTrackId = null;
                }

                IsResolvingMusicVideo = false;
            });
        }
    }
}
