using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;
using Xperience.Core.Services;

namespace Xperience.App.ViewModels;

/// <summary>
/// Selectable presentation wrapper for a YouTube Music library playlist inside the Import Playlists modal.
/// </summary>
public sealed partial class YouTubePlaylistImportItemViewModel : ObservableObject
{
    private readonly Action? _onSelectionChanged;

    public YouTubeMusicLibraryPlaylist Source { get; }

    public string PlaylistId => Source.PlaylistId;

    public string Title => Source.Title;

    public string Subtitle => Source.DisplaySubtitle;

    public string ArtworkUri => Source.ArtworkUri;

    public string SafeArtworkUri => Source.SafeArtworkUri;

    public bool HasArtwork => Source.HasArtwork;

    public double ArtworkOpacity => Source.ArtworkOpacity;

    public double FallbackIconOpacity => Source.FallbackIconOpacity;

    public bool IsAlreadyImported { get; }

    public Visibility AlreadyImportedBadgeVisibility =>
        IsAlreadyImported ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged?.Invoke();

    public YouTubePlaylistImportItemViewModel(
        YouTubeMusicLibraryPlaylist source,
        bool isAlreadyImported,
        bool isSelected,
        Action? onSelectionChanged = null)
    {
        Source = source;
        IsAlreadyImported = isAlreadyImported;
        IsSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }
}

public sealed partial class ShellViewModel
{
    private CancellationTokenSource? _ytmLibraryImportLoadCts;
    private UserPlaylist? _pendingDeletePlaylist;

    /// <summary>
    /// Selectable playlists fetched from the signed-in user's YouTube Music library.
    /// </summary>
    public ObservableCollection<YouTubePlaylistImportItemViewModel> YouTubeLibraryImportItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YouTubePlaylistImportModalVisibility))]
    public partial bool IsYouTubePlaylistImportModalOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportYouTubeLoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(HasYouTubeLibraryImportItemsVisibility))]
    [NotifyPropertyChangedFor(nameof(ImportYouTubeSelectionToolbarVisibility))]
    [NotifyPropertyChangedFor(nameof(EmptyYouTubeLibraryImportVisibility))]
    [NotifyPropertyChangedFor(nameof(CanImportSelectedYouTubePlaylists))]
    public partial bool IsLoadingYouTubeLibraryPlaylists { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportYouTubeProgressVisibility))]
    [NotifyPropertyChangedFor(nameof(CanImportSelectedYouTubePlaylists))]
    [NotifyPropertyChangedFor(nameof(CanDismissYouTubePlaylistImportModal))]
    public partial bool IsImportingYouTubePlaylists { get; set; }

    [ObservableProperty]
    public partial string YouTubePlaylistImportStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportYouTubeErrorVisibility))]
    [NotifyPropertyChangedFor(nameof(EmptyYouTubeLibraryImportVisibility))]
    public partial string YouTubePlaylistImportErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportYouTubeProgressVisibility))]
    public partial string YouTubePlaylistImportProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportYouTubePlaylistsButtonLabel))]
    [NotifyPropertyChangedFor(nameof(CanImportSelectedYouTubePlaylists))]
    public partial int SelectedYouTubeImportCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshCurrentYouTubePlaylist))]
    public partial bool IsRefreshingCurrentYouTubePlaylist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeletePlaylistConfirmationVisibility))]
    public partial bool IsDeletePlaylistConfirmationOpen { get; set; }

    [ObservableProperty]
    public partial string PendingDeletePlaylistTitle { get; set; } = "Delete Playlist";

    [ObservableProperty]
    public partial string PendingDeletePlaylistMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PendingDeletePlaylistConfirmLabel { get; set; } = "Delete Playlist";

    public Visibility YouTubePlaylistImportModalVisibility =>
        IsYouTubePlaylistImportModalOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeletePlaylistConfirmationVisibility =>
        IsDeletePlaylistConfirmationOpen ? Visibility.Visible : Visibility.Collapsed;

    public string ImportYouTubePlaylistsButtonLabel =>
        SelectedYouTubeImportCount > 0 ? $"Import ({SelectedYouTubeImportCount})" : "Import";

    public bool CanImportSelectedYouTubePlaylists =>
        !IsLoadingYouTubeLibraryPlaylists && !IsImportingYouTubePlaylists && SelectedYouTubeImportCount > 0;

    public bool CanDismissYouTubePlaylistImportModal => !IsImportingYouTubePlaylists;

    public bool CanRefreshCurrentYouTubePlaylist => !IsRefreshingCurrentYouTubePlaylist;

    public Visibility ImportYouTubeLoadingVisibility =>
        IsLoadingYouTubeLibraryPlaylists ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ImportYouTubeErrorVisibility =>
        !string.IsNullOrWhiteSpace(YouTubePlaylistImportErrorText) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ImportYouTubeProgressVisibility =>
        IsImportingYouTubePlaylists || !string.IsNullOrWhiteSpace(YouTubePlaylistImportProgressText)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility HasYouTubeLibraryImportItemsVisibility =>
        !IsLoadingYouTubeLibraryPlaylists && YouTubeLibraryImportItems.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ImportYouTubeSelectionToolbarVisibility =>
        !IsLoadingYouTubeLibraryPlaylists && YouTubeLibraryImportItems.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility EmptyYouTubeLibraryImportVisibility =>
        !IsLoadingYouTubeLibraryPlaylists &&
        YouTubeLibraryImportItems.Count == 0 &&
        string.IsNullOrWhiteSpace(YouTubePlaylistImportErrorText)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility CurrentPlaylistYouTubeBadgeVisibility =>
        CurrentPlaylistDetail is { IsFromYouTubeMusic: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility CurrentPlaylistRefreshButtonVisibility =>
        CurrentPlaylistDetail is { IsFromYouTubeMusic: true } && IsYouTubeMusicAuthenticated
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string CurrentPlaylistDeleteButtonTooltip =>
        CurrentPlaylistDetail is { IsFromYouTubeMusic: true }
            ? "Remove from App"
            : "Delete Playlist";

    [RelayCommand]
    private async Task OpenYouTubePlaylistImportModalAsync()
    {
        if (!_youTubeMusicService.IsAuthenticated)
        {
            StatusMessage = "Sign in to YouTube Music first to import your playlists.";
            return;
        }

        IsYouTubePlaylistImportModalOpen = true;
        YouTubePlaylistImportErrorText = string.Empty;
        YouTubePlaylistImportProgressText = string.Empty;
        YouTubePlaylistImportStatusText = "Loading your YouTube Music playlists...";
        YouTubeLibraryImportItems.Clear();
        SelectedYouTubeImportCount = 0;
        IsLoadingYouTubeLibraryPlaylists = true;
        NotifyYouTubeImportListStateChanged();

        var ct = ReplaceCts(ref _ytmLibraryImportLoadCts).Token;

        try
        {
            await EnsurePlaylistsHydratedAsync(ct, includeSmartSuggestions: false);
            var libraryPlaylists = await _youTubeMusicService.FetchUserLibraryPlaylistsAsync(ct);

            if (ct.IsCancellationRequested || !IsYouTubePlaylistImportModalOpen)
            {
                return;
            }

            YouTubeLibraryImportItems.Clear();
            foreach (var pl in libraryPlaylists)
            {
                bool alreadyImported = PlaylistImportHelper.IsAlreadyImported(Playlists, pl.PlaylistId);
                YouTubeLibraryImportItems.Add(new YouTubePlaylistImportItemViewModel(
                    pl,
                    isAlreadyImported: alreadyImported,
                    isSelected: !alreadyImported,
                    onSelectionChanged: RecalculateSelectedYouTubeImportCount));
            }

            RecalculateSelectedYouTubeImportCount();
            YouTubePlaylistImportStatusText = YouTubeLibraryImportItems.Count switch
            {
                0 => "No playlists were found in your YouTube Music library.",
                1 => "Found 1 playlist in your YouTube Music library.",
                _ => $"Found {YouTubeLibraryImportItems.Count} playlists in your YouTube Music library."
            };
        }
        catch (OperationCanceledException)
        {
            // Cancelled when modal closed or reloaded
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load YouTube Music library playlists.");
            YouTubePlaylistImportErrorText =
                "Could not load your YouTube Music playlists. Check your connection or reconnect your account.";
            YouTubePlaylistImportStatusText = string.Empty;
        }
        finally
        {
            IsLoadingYouTubeLibraryPlaylists = false;
            NotifyYouTubeImportListStateChanged();
        }
    }

    [RelayCommand]
    private void CloseYouTubePlaylistImportModal()
    {
        if (IsImportingYouTubePlaylists)
        {
            return;
        }

        CancelAndDisposeCts(ref _ytmLibraryImportLoadCts);
        IsYouTubePlaylistImportModalOpen = false;
    }

    public void ToggleYouTubeImportItemSelection(YouTubePlaylistImportItemViewModel? item)
    {
        if (item is null || IsImportingYouTubePlaylists)
        {
            return;
        }

        item.IsSelected = !item.IsSelected;
        RecalculateSelectedYouTubeImportCount();
    }

    [RelayCommand]
    private void SelectAllYouTubeImportPlaylists()
    {
        if (IsImportingYouTubePlaylists)
        {
            return;
        }

        foreach (var item in YouTubeLibraryImportItems)
        {
            item.IsSelected = true;
        }

        RecalculateSelectedYouTubeImportCount();
    }

    [RelayCommand]
    private void ClearYouTubeImportSelection()
    {
        if (IsImportingYouTubePlaylists)
        {
            return;
        }

        foreach (var item in YouTubeLibraryImportItems)
        {
            item.IsSelected = false;
        }

        RecalculateSelectedYouTubeImportCount();
    }

    [RelayCommand]
    private async Task ImportSelectedYouTubePlaylistsAsync()
    {
        if (IsImportingYouTubePlaylists)
        {
            return;
        }

        var selected = YouTubeLibraryImportItems.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        IsImportingYouTubePlaylists = true;
        YouTubePlaylistImportErrorText = string.Empty;

        int importedCount = 0;
        int totalTracksImported = 0;
        int failedCount = 0;

        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var item = selected[i];
                YouTubePlaylistImportProgressText = $"Importing {i + 1} of {selected.Count}: {item.Title}...";

                try
                {
                    var (tracks, coverUri, description) =
                        await _youTubeMusicService.FetchUserPlaylistTracksAsync(item.Source);

                    string resolvedCover = !string.IsNullOrWhiteSpace(coverUri)
                        ? coverUri
                        : item.ArtworkUri;

                    await _catalogService.ImportYouTubeMusicPlaylistAsync(
                        item.Source,
                        tracks,
                        accountLabel: _youTubeMusicService.ConnectedAccountLabel,
                        description: description,
                        coverUri: resolvedCover);

                    importedCount++;
                    totalTracksImported += tracks.Count;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to import YouTube Music playlist '{Title}' ({Id}).", item.Title, item.PlaylistId);
                    failedCount++;
                }
            }

            _isPlaylistsDirty = true;
            await RefreshPlaylistsAsync(refreshSmartSuggestions: false);

            if (ActiveSubView == WorkspaceSubView.PlaylistDetail &&
                CurrentPlaylistDetail is { IsFromYouTubeMusic: true } current)
            {
                var refreshedCurrent = Playlists.FirstOrDefault(p =>
                    string.Equals(p.Id, current.Id, StringComparison.OrdinalIgnoreCase));
                if (refreshedCurrent is not null)
                {
                    await OpenPlaylistDetailAsync(refreshedCurrent);
                }
            }
        }
        finally
        {
            IsImportingYouTubePlaylists = false;
            YouTubePlaylistImportProgressText = string.Empty;
        }

        if (importedCount > 0)
        {
            IsYouTubePlaylistImportModalOpen = false;
            string playlistWord = importedCount == 1 ? "playlist" : "playlists";
            string trackWord = totalTracksImported == 1 ? "track" : "tracks";
            StatusMessage = failedCount == 0
                ? $"Imported {importedCount} YouTube Music {playlistWord} ({totalTracksImported} {trackWord})."
                : $"Imported {importedCount} YouTube Music {playlistWord} ({failedCount} failed).";
        }
        else
        {
            YouTubePlaylistImportErrorText = "Could not import the selected playlists. Please try again.";
        }
    }

    [RelayCommand]
    private async Task RefreshCurrentYouTubePlaylistAsync()
    {
        if (CurrentPlaylistDetail is not { IsFromYouTubeMusic: true } current ||
            !IsYouTubeMusicAuthenticated ||
            IsRefreshingCurrentYouTubePlaylist)
        {
            return;
        }

        IsRefreshingCurrentYouTubePlaylist = true;
        StatusMessage = $"Refreshing '{current.Title}' from YouTube Music...";

        try
        {
            var source = new YouTubeMusicLibraryPlaylist(
                PlaylistId: current.ExternalPlaylistId,
                BrowseId: current.ExternalBrowseId,
                Title: current.Title,
                Subtitle: current.Description,
                ArtworkUri: current.CustomCoverImagePath);

            var (tracks, coverUri, description) = await _youTubeMusicService.FetchUserPlaylistTracksAsync(source);
            var updated = await _catalogService.ImportYouTubeMusicPlaylistAsync(
                source,
                tracks,
                accountLabel: _youTubeMusicService.ConnectedAccountLabel,
                description: description,
                coverUri: coverUri);

            _isPlaylistsDirty = true;
            await RefreshPlaylistsAsync(refreshSmartSuggestions: false);

            if (ActiveSubView == WorkspaceSubView.PlaylistDetail &&
                CurrentPlaylistDetail is not null &&
                string.Equals(CurrentPlaylistDetail.Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                await OpenPlaylistDetailAsync(updated);
            }

            string trackWord = updated.Tracks.Count == 1 ? "track" : "tracks";
            StatusMessage = $"Refreshed '{updated.Title}' ({updated.Tracks.Count} {trackWord}) from YouTube Music.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh YouTube Music playlist '{Title}'.", current.Title);
            StatusMessage = $"Could not refresh '{current.Title}' from YouTube Music.";
        }
        finally
        {
            IsRefreshingCurrentYouTubePlaylist = false;
        }
    }

    [RelayCommand]
    private void RequestDeleteCurrentPlaylist()
    {
        if (CurrentPlaylistDetail is null ||
            string.IsNullOrWhiteSpace(CurrentPlaylistDetail.Id) ||
            CurrentPlaylistDetail.IsSmartSuggestion)
        {
            return;
        }

        RequestDeletePlaylistCard(CurrentPlaylistDetail);
    }

    public void RequestDeletePlaylistCard(UserPlaylist? playlist)
    {
        if (playlist is null ||
            string.IsNullOrWhiteSpace(playlist.Id) ||
            playlist.IsSmartSuggestion)
        {
            return;
        }

        _pendingDeletePlaylist = playlist;

        if (playlist.IsFromYouTubeMusic)
        {
            PendingDeletePlaylistTitle = "Remove Playlist from App";
            PendingDeletePlaylistMessage =
                $"Remove \"{playlist.Title}\" from Xperience Player? This only removes it from the app and won't affect your YouTube Music library.";
            PendingDeletePlaylistConfirmLabel = "Remove from App";
        }
        else
        {
            PendingDeletePlaylistTitle = "Delete Playlist";
            PendingDeletePlaylistMessage =
                $"Delete \"{playlist.Title}\" from your playlists? This action cannot be undone.";
            PendingDeletePlaylistConfirmLabel = "Delete Playlist";
        }

        IsDeletePlaylistConfirmationOpen = true;
    }

    [RelayCommand]
    private void CancelDeletePlaylist()
    {
        _pendingDeletePlaylist = null;
        IsDeletePlaylistConfirmationOpen = false;
    }

    [RelayCommand]
    private async Task ConfirmDeletePlaylistAsync()
    {
        var target = _pendingDeletePlaylist;
        _pendingDeletePlaylist = null;
        IsDeletePlaylistConfirmationOpen = false;

        if (target is null || string.IsNullOrWhiteSpace(target.Id))
        {
            return;
        }

        string playlistId = target.Id;
        string title = target.Title;
        bool isFromYouTubeMusic = target.IsFromYouTubeMusic;

        bool wasOpenInDetail = ActiveSubView == WorkspaceSubView.PlaylistDetail &&
                               CurrentPlaylistDetail is not null &&
                               string.Equals(CurrentPlaylistDetail.Id, playlistId, StringComparison.OrdinalIgnoreCase);

        if (wasOpenInDetail)
        {
            CancelAndDisposeCts(ref _playlistRelatedLoadCts);
            _playlistRelatedCache.Remove(playlistId);
            PlaylistRelatedTracks.Clear();
            IsPlaylistRelatedLoading = false;
            OnPropertyChanged(nameof(PlaylistRelatedSkeletonVisibility));
            OnPropertyChanged(nameof(HasPlaylistRelatedVisibility));
            OnPropertyChanged(nameof(PlaylistRelatedSectionVisibility));
        }

        // Strictly local deletion: never touches YouTube Music APIs or the user's YouTube Music account.
        await _catalogService.DeletePlaylistAsync(playlistId);
        _isPlaylistsDirty = true;
        await RefreshPlaylistsAsync(refreshSmartSuggestions: false);

        if (wasOpenInDetail)
        {
            ClearPlaylistDetailState();
            ActiveSubView = WorkspaceSubView.MainSection;
            CurrentSection = NavigationSection.Playlists;
        }

        StatusMessage = isFromYouTubeMusic
            ? $"Removed '{title}' from Xperience Player (your YouTube Music library was not changed)."
            : $"Deleted playlist '{title}'.";
    }

    private void RecalculateSelectedYouTubeImportCount()
    {
        int count = 0;
        for (int i = 0; i < YouTubeLibraryImportItems.Count; i++)
        {
            if (YouTubeLibraryImportItems[i].IsSelected)
            {
                count++;
            }
        }

        SelectedYouTubeImportCount = count;
    }

    private void NotifyYouTubeImportListStateChanged()
    {
        OnPropertyChanged(nameof(HasYouTubeLibraryImportItemsVisibility));
        OnPropertyChanged(nameof(ImportYouTubeSelectionToolbarVisibility));
        OnPropertyChanged(nameof(EmptyYouTubeLibraryImportVisibility));
        OnPropertyChanged(nameof(CanImportSelectedYouTubePlaylists));
    }
}
