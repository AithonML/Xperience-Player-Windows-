using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Xperience.App.ViewModels;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Views;

public sealed partial class ShellPage
{
    private bool _isUpdatesFeatureInitialized;
    private bool _isSplashCompleted;
    private bool _isUpdateDialogOpen;
    private UpdateReleaseInfo? _pendingStartupUpdateRelease;

    private void InitializeUpdateFeatures()
    {
        if (_isUpdatesFeatureInitialized)
        {
            return;
        }

        _isUpdatesFeatureInitialized = true;
        ViewModel.UpdateDialogRequested += OnUpdateDialogRequested;
    }

    /// <summary>
    /// Called by <see cref="MainWindow"/> once the startup splash overlay finishes exiting,
    /// ensuring any queued startup update dialog appears cleanly without overlapping the splash screen.
    /// </summary>
    public void NotifySplashCompleted()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _isSplashCompleted = true;
            if (_pendingStartupUpdateRelease is { } pending && !_isUpdateDialogOpen)
            {
                _pendingStartupUpdateRelease = null;
                _ = ShowUpdateAvailableDialogAsync(pending);
            }
        });
    }

    /// <summary>
    /// Returns the semantic foreground brush for inline update check feedback text in Settings.
    /// </summary>
    public Brush GetUpdateFeedbackForegroundBrush(bool isError)
    {
        if (isError)
        {
            return new SolidColorBrush(Color.FromArgb(255, 244, 63, 94));
        }

        return ResolveAppBrush("TokenPrimaryAccentBrush", Color.FromArgb(255, 45, 212, 191));
    }

    private void OnUpdateDialogRequested(object? sender, UpdateDialogRequestEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isUpdateDialogOpen)
            {
                return;
            }

            if (!e.IsManualRequest && !_isSplashCompleted)
            {
                _pendingStartupUpdateRelease = e.Release;
                return;
            }

            _ = ShowUpdateAvailableDialogAsync(e.Release);
        });
    }

    private async Task ShowUpdateAvailableDialogAsync(UpdateReleaseInfo release)
    {
        if (_isUpdateDialogOpen || XamlRoot is null)
        {
            return;
        }

        _isUpdateDialogOpen = true;
        CancellationTokenSource? downloadCts = null;
        bool isDownloadInProgress = false;

        try
        {
            var primaryTextBrush = ResolveAppBrush("TokenPrimaryTextBrush", Colors.White);
            var secondaryTextBrush = ResolveAppBrush("TokenSecondaryTextBrush", Color.FromArgb(255, 176, 182, 194));
            var accentBrush = ResolveAppBrush("TokenPrimaryAccentBrush", Color.FromArgb(255, 45, 212, 191));
            var accentBadgeBg = ResolveAppBrush("TokenAccentBadgeBackgroundBrush", Color.FromArgb(54, 45, 212, 191));
            var accentBadgeBorder = ResolveAppBrush("TokenAccentBadgeBorderBrush", Color.FromArgb(140, 45, 212, 191));
            bool isDark = !ViewModel.IsLightMode;

            var cardSurfaceBrush = new SolidColorBrush(
                isDark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(190, 255, 255, 255));
            var cardBorderBrush = new SolidColorBrush(
                isDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));

            // 1. Header Strip: Version transition pill + installer asset details
            var versionTransitionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            versionTransitionPanel.Children.Add(new Border
            {
                Background = accentBadgeBg,
                BorderBrush = accentBadgeBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Child = new TextBlock
                {
                    Text = $"{ViewModel.CurrentAppVersionDisplay}  →  {release.Version.DisplayTag}",
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = accentBrush
                }
            });

            string assetMeta = release.SelectedAsset.Size > 0
                ? $"{release.SelectedAsset.Name}  •  {FormatFileSize(release.SelectedAsset.Size)}"
                : release.SelectedAsset.Name;

            var headerDetails = new StackPanel { Spacing = 6 };
            headerDetails.Children.Add(versionTransitionPanel);
            headerDetails.Children.Add(new TextBlock
            {
                Text = assetMeta,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                Foreground = secondaryTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            // 2. Scrollable Changelog Box
            string changelogBody = string.IsNullOrWhiteSpace(release.ReleaseNotes)
                ? ViewModel.GetLocalizedUpdateString(
                    "Update_EmptyChangelog",
                    "This release includes performance improvements, stability enhancements, and bug fixes.")
                : release.ReleaseNotes;

            var changelogHeader = new TextBlock
            {
                Text = ViewModel.GetLocalizedUpdateString("Update_ChangelogHeader", "What's new in this release"),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = primaryTextBrush
            };

            var changelogTextBlock = new TextBlock
            {
                Text = changelogBody,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12.5,
                Foreground = secondaryTextBrush,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };

            var changelogScrollViewer = new ScrollViewer
            {
                MaxHeight = 230,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = changelogTextBlock
            };

            var changelogBorder = new Border
            {
                Background = cardSurfaceBrush,
                BorderBrush = cardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Child = changelogScrollViewer
            };

            // 3. Download & SHA-256 Verification Progress Section (visible once user clicks Update Now)
            var statusTextBlock = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = primaryTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var percentTextBlock = new TextBlock
            {
                Text = "0%",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = accentBrush,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var progressHeaderGrid = new Grid();
            progressHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progressHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(statusTextBlock, 0);
            Grid.SetColumn(percentTextBlock, 1);
            progressHeaderGrid.Children.Add(statusTextBlock);
            progressHeaderGrid.Children.Add(percentTextBlock);

            var downloadProgressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 6,
                Foreground = accentBrush
            };

            var progressPanel = new StackPanel
            {
                Spacing = 8,
                Visibility = Visibility.Collapsed
            };
            progressPanel.Children.Add(progressHeaderGrid);
            progressPanel.Children.Add(downloadProgressBar);

            // 4. Integrity / Error Banner (visible if download, SHA-256 verification, or launch fails)
            var errorTextBlock = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 251, 113, 133)),
                TextWrapping = TextWrapping.Wrap
            };

            var errorGrid = new Grid { ColumnSpacing = 10 };
            errorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            errorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var errorIcon = new FontIcon
            {
                Glyph = "\uE783",
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 244, 63, 94)),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0)
            };
            Grid.SetColumn(errorIcon, 0);
            Grid.SetColumn(errorTextBlock, 1);
            errorGrid.Children.Add(errorIcon);
            errorGrid.Children.Add(errorTextBlock);

            var errorBanner = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(30, 244, 63, 94)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(110, 244, 63, 94)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Visibility = Visibility.Collapsed,
                Child = errorGrid
            };

            var contentRoot = new StackPanel
            {
                Spacing = 14,
                MinWidth = 440,
                MaxWidth = 520
            };
            contentRoot.Children.Add(headerDetails);
            contentRoot.Children.Add(changelogHeader);
            contentRoot.Children.Add(changelogBorder);
            contentRoot.Children.Add(progressPanel);
            contentRoot.Children.Add(errorBanner);

            // Custom Dialog Title with App Accent Icon
            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleRow.Children.Add(new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = accentBadgeBg,
                BorderBrush = accentBadgeBorder,
                BorderThickness = new Thickness(1),
                Child = new FontIcon
                {
                    Glyph = "\uE777",
                    FontSize = 15,
                    Foreground = accentBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = ViewModel.GetLocalizedUpdateString("Update_DialogTitle", "New update available"),
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = primaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            var dialog = new ContentDialog
            {
                Title = titleRow,
                Content = contentRoot,
                PrimaryButtonText = ViewModel.GetLocalizedUpdateString("Update_NowButton", "Update now"),
                CloseButtonText = ViewModel.GetLocalizedUpdateString("Update_LaterButton", "Later"),
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light,
                XamlRoot = XamlRoot
            };

            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentBtnStyleObj) &&
                accentBtnStyleObj is Style accentBtnStyle)
            {
                dialog.PrimaryButtonStyle = accentBtnStyle;
            }

            dialog.PrimaryButtonClick += (senderDialog, args) =>
            {
                // Keep dialog open so live streaming progress, SHA-256 verification, and any integrity error remain visible
                args.Cancel = true;
                if (isDownloadInProgress)
                {
                    return;
                }

                isDownloadInProgress = true;
                senderDialog.IsPrimaryButtonEnabled = false;
                errorBanner.Visibility = Visibility.Collapsed;
                progressPanel.Visibility = Visibility.Visible;
                downloadProgressBar.IsIndeterminate = true;
                downloadProgressBar.Value = 0;
                percentTextBlock.Text = "0%";
                statusTextBlock.Text = ViewModel.GetLocalizedUpdateString("Update_StatusCleaning", "Preparing secure download...");

                downloadCts?.Dispose();
                downloadCts = new CancellationTokenSource();
                var ct = downloadCts.Token;

                _ = RunDialogUpdateFlowAsync(
                    senderDialog,
                    release,
                    progressPanel,
                    downloadProgressBar,
                    statusTextBlock,
                    percentTextBlock,
                    errorBanner,
                    errorTextBlock,
                    () => isDownloadInProgress = false,
                    ct);
            };

            dialog.Closing += (_, _) =>
            {
                // If user clicks "Later" / closes during download, cancel immediately so .part is deleted
                if (isDownloadInProgress && downloadCts is { IsCancellationRequested: false })
                {
                    try
                    {
                        downloadCts.Cancel();
                    }
                    catch
                    {
                        // Ignore cancellation races
                    }
                }
            };

            await dialog.ShowAsync();
        }
        finally
        {
            downloadCts?.Cancel();
            downloadCts?.Dispose();
            _isUpdateDialogOpen = false;
        }
    }

    private async Task RunDialogUpdateFlowAsync(
        ContentDialog dialog,
        UpdateReleaseInfo release,
        StackPanel progressPanel,
        ProgressBar downloadProgressBar,
        TextBlock statusTextBlock,
        TextBlock percentTextBlock,
        Border errorBanner,
        TextBlock errorTextBlock,
        Action onFinished,
        CancellationToken cancellationToken)
    {
        try
        {
            var (succeeded, isCancelled, errorMessage) = await ViewModel.DownloadVerifyAndLaunchUpdateAsync(
                release,
                onProgress: (snapshot, statusText) =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        statusTextBlock.Text = statusText;
                        downloadProgressBar.IsIndeterminate = snapshot.IsIndeterminate;
                        downloadProgressBar.Value = snapshot.ProgressPercent;
                        percentTextBlock.Text = snapshot.IsIndeterminate
                            ? (snapshot.Phase == UpdateDownloadPhase.VerifyingIntegrity ? "SHA-256" : "...")
                            : $"{snapshot.ProgressPercent:0}%";
                    });
                },
                beforeWindowClose: PrepareShellForUpdateShutdown,
                cancellationToken: cancellationToken);

            if (succeeded)
            {
                try
                {
                    dialog.Hide();
                }
                catch
                {
                    // App may already be closing
                }

                return;
            }

            if (isCancelled)
            {
                return;
            }

            progressPanel.Visibility = Visibility.Collapsed;
            errorTextBlock.Text = errorMessage ?? ViewModel.GetLocalizedUpdateString(
                "Update_ErrorIntegrityMismatch",
                "SHA-256 integrity verification failed.");
            errorBanner.Visibility = Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = true;
        }
        catch (Exception ex)
        {
            progressPanel.Visibility = Visibility.Collapsed;
            errorTextBlock.Text = ex.Message;
            errorBanner.Visibility = Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = true;
        }
        finally
        {
            onFinished();
        }
    }

    private void PrepareShellForUpdateShutdown()
    {
        try
        {
            _searchSpringTimer.Stop();
            _searchPlaceholderTimer.Stop();
            _auroraAnimTimer.Stop();
            _artistSkeletonShimmerTimer?.Stop();
            _albumSkeletonShimmerTimer?.Stop();
            _playlistRelatedShimmerTimer?.Stop();
            _likedRecsShimmerTimer?.Stop();
            StopNowPlayingSidebarTimers();
            _canvasLoopWatchdogTimer?.Stop();
            _artistVideoRetentionTimer?.Stop();
            _fullscreenMusicVideoPlayer?.Pause();
        }
        catch
        {
            // Non-critical shutdown cleanup
        }
    }

    private static Brush ResolveAppBrush(string resourceKey, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }

    private static string FormatFileSize(long bytes)
    {
        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;

        if (bytes >= gb)
        {
            return $"{bytes / gb:0.00} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:0.0} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:0} KB";
        }

        return $"{bytes} B";
    }
}
