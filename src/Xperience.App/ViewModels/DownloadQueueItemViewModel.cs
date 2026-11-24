using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Xperience.Core.Domain.Entities;

namespace Xperience.App.ViewModels;

/// <summary>
/// Reactive ViewModel representing an individual active or completed track download
/// in the persistent offline queue.
/// </summary>
public sealed partial class DownloadQueueItemViewModel : ObservableObject
{
    public DownloadQueueItemViewModel(TrackItem track)
    {
        Track = track;
        Title = track.Title;
        Artist = track.Artist;
        Album = !string.IsNullOrWhiteSpace(track.Album) ? track.Album : "Single";
        CoverArtUri = track.CoverArtUri;
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public TrackItem Track { get; }

    public string Title { get; }

    public string Artist { get; }

    public string Album { get; }

    public string CoverArtUri { get; }

    public const string FallbackTransparentCoverUri = "ms-appx:///Assets/Covers/transparent_pixel.png";

    public string SafeCoverArtUri => !string.IsNullOrWhiteSpace(CoverArtUri) ? CoverArtUri : FallbackTransparentCoverUri;

    public double CoverArtOpacity => string.IsNullOrWhiteSpace(CoverArtUri) ? 0.0 : 1.0;

    public double DefaultCoverIconOpacity => string.IsNullOrWhiteSpace(CoverArtUri) ? 1.0 : 0.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedPercent))]
    public partial double ProgressPercent { get; set; }

    public string FormattedPercent => $"{Math.Round(ProgressPercent)}%";

    [ObservableProperty]
    public partial string StageStatus { get; set; } = "Starting download...";

    [ObservableProperty]
    public partial string ProviderBadge { get; set; } = "FLAC Lossless";

    [ObservableProperty]
    public partial string VersionBadge { get; set; } = "Original Studio";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressBarVisibility))]
    [NotifyPropertyChangedFor(nameof(StatusSuccessVisibility))]
    [NotifyPropertyChangedFor(nameof(StatusFailedVisibility))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    public partial bool IsDownloading { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressBarVisibility))]
    [NotifyPropertyChangedFor(nameof(StatusSuccessVisibility))]
    [NotifyPropertyChangedFor(nameof(StatusFailedVisibility))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    public partial bool IsCompleted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressBarVisibility))]
    [NotifyPropertyChangedFor(nameof(StatusSuccessVisibility))]
    [NotifyPropertyChangedFor(nameof(StatusFailedVisibility))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    public partial bool IsFailed { get; set; }

    public bool IsActive => IsDownloading && !IsCompleted && !IsFailed;

    public Visibility ProgressBarVisibility => IsDownloading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StatusSuccessVisibility => IsCompleted ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StatusFailedVisibility => IsFailed ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? DownloadedAudioFilePath { get; set; }

    [ObservableProperty]
    public partial string? DownloadedCanvasFilePath { get; set; }

    [ObservableProperty]
    public partial string? DownloadedLrcFilePath { get; set; }
}
