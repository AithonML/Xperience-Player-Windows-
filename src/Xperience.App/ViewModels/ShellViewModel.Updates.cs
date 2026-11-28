using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Xperience.App.Services;
using Xperience.Core.Domain.Models;

namespace Xperience.App.ViewModels;

/// <summary>
/// Event payload emitted when the update modal dialog should be presented to the user.
/// </summary>
public sealed class UpdateDialogRequestEventArgs(UpdateReleaseInfo release, bool isManualRequest) : EventArgs
{
    public UpdateReleaseInfo Release { get; } = release;
    public bool IsManualRequest { get; } = isManualRequest;
}

public sealed partial class ShellViewModel
{
    private bool _isHydratingUpdateSettings;
    private bool _startupUpdateCheckTriggered;
    private UpdateCheckResult? _lastManualUpdateCheckResult;

    /// <summary>
    /// Raised when a newer verified-checksum release is available and the update dialog should be shown.
    /// </summary>
    public event EventHandler<UpdateDialogRequestEventArgs>? UpdateDialogRequested;

    /// <summary>
    /// Gets the formatted current installed application version (e.g., <c>v0.2.0.0</c>),
    /// dynamically resolved from the MSIX package identity or compiled assembly version.
    /// </summary>
    public string CurrentAppVersionDisplay { get; } = WindowsInstallerLauncher.GetCompiledAppVersionDisplay();

    /// <summary>
    /// Gets the Windows architecture display badge (e.g., <c>Windows x64</c>, <c>Windows ARM64</c>).
    /// </summary>
    public string CurrentAppArchitectureDisplay { get; } = WindowsInstallerLauncher.GetSystemArchitectureDisplay();

    /// <summary>
    /// Gets the localized "On" text for the auto-update <see cref="Microsoft.UI.Xaml.Controls.ToggleSwitch"/>.
    /// </summary>
    public string UpdatesToggleOnLabel => GetLocalizedUpdateString("Settings_UpdatesToggleOn", "On");

    /// <summary>
    /// Gets the localized "Off" text for the auto-update <see cref="Microsoft.UI.Xaml.Controls.ToggleSwitch"/>.
    /// </summary>
    public string UpdatesToggleOffLabel => GetLocalizedUpdateString("Settings_UpdatesToggleOff", "Off");

    [ObservableProperty]
    public partial bool AutoCheckUpdatesOnStartup { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckUpdatesButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(CheckUpdatesLoadingVisibility))]
    public partial bool IsCheckingForUpdates { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateCheckFeedbackVisibility))]
    public partial string UpdateCheckFeedbackText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUpdateCheckFeedbackError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableUpdateActionVisibility))]
    public partial UpdateReleaseInfo? AvailableUpdateRelease { get; set; }

    public bool IsCheckUpdatesButtonEnabled => !IsCheckingForUpdates;

    public Visibility CheckUpdatesLoadingVisibility =>
        IsCheckingForUpdates ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UpdateCheckFeedbackVisibility =>
        !string.IsNullOrWhiteSpace(UpdateCheckFeedbackText) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AvailableUpdateActionVisibility =>
        AvailableUpdateRelease is not null ? Visibility.Visible : Visibility.Collapsed;

    partial void OnAutoCheckUpdatesOnStartupChanged(bool value)
    {
        if (!_isHydratingUpdateSettings)
        {
            _ = _storageService.SaveAsync(
                UpdateSettings.StorageKey,
                new UpdateSettings { AutoCheckUpdatesOnStartup = value });
        }
    }

    /// <summary>
    /// Hydrates persisted update preferences (defaulting <see cref="AutoCheckUpdatesOnStartup"/> to <c>true</c>)
    /// and cleans up any stale temporary update downloads in the background.
    /// </summary>
    private async Task HydrateUpdateSettingsAsync()
    {
        _isHydratingUpdateSettings = true;
        try
        {
            var settings = await _storageService.LoadAsync(
                UpdateSettings.StorageKey,
                defaultValue: new UpdateSettings());

            AutoCheckUpdatesOnStartup = settings?.AutoCheckUpdatesOnStartup ?? true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load UpdateSettings; defaulting AutoCheckUpdatesOnStartup to true.");
            AutoCheckUpdatesOnStartup = true;
        }
        finally
        {
            _isHydratingUpdateSettings = false;
        }

        _ = Task.Run(() => _appUpdateService.CleanupStaleDownloads());
        RefreshUpdateLocalizedText();
    }

    /// <summary>
    /// Launches a non-blocking, silent background update check on app startup if enabled.
    /// Network and transient errors are swallowed silently without interrupting the user.
    /// </summary>
    private void TriggerSilentStartupUpdateCheck()
    {
        if (!AutoCheckUpdatesOnStartup || _startupUpdateCheckTriggered)
        {
            return;
        }

        _startupUpdateCheckTriggered = true;

        _ = Task.Run(async () =>
        {
            try
            {
                // Yield startup priority to catalog load and splash entrance
                await Task.WhenAny(InitialHomeReadyTask, Task.Delay(3000)).ConfigureAwait(false);
                await Task.Delay(600).ConfigureAwait(false);

                if (!AutoCheckUpdatesOnStartup)
                {
                    return;
                }

                var result = await _appUpdateService.CheckForUpdatesAsync().ConfigureAwait(false);
                if (result.Status == UpdateCheckStatus.UpdateAvailable && result.AvailableUpdate is { } release)
                {
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        _lastManualUpdateCheckResult = result;
                        AvailableUpdateRelease = release;
                        IsUpdateCheckFeedbackError = false;
                        UpdateCheckFeedbackText = FormatLocalizedUpdateString(
                            "Update_StatusAvailable",
                            "Update available: {0} (installed: {1})",
                            release.Version.DisplayTag,
                            CurrentAppVersionDisplay);

                        UpdateDialogRequested?.Invoke(
                            this,
                            new UpdateDialogRequestEventArgs(release, isManualRequest: false));
                    });
                }
            }
            catch (Exception ex)
            {
                // Silent failure on startup per requirements
                _logger.LogDebug(ex, "Silent startup update check skipped due to transient error.");
            }
        });
    }

    /// <summary>
    /// Performs a user-initiated check for updates against GitHub Releases and surfaces feedback or the update dialog.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesManuallyAsync()
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = true;
        IsUpdateCheckFeedbackError = false;
        UpdateCheckFeedbackText = GetLocalizedUpdateString("Update_StatusChecking", "Checking for updates...");

        try
        {
            var result = await _appUpdateService.CheckForUpdatesAsync();
            _lastManualUpdateCheckResult = result;
            ApplyUpdateCheckResultFeedback(result);

            if (result.Status == UpdateCheckStatus.UpdateAvailable && result.AvailableUpdate is { } release)
            {
                AvailableUpdateRelease = release;
                UpdateDialogRequested?.Invoke(
                    this,
                    new UpdateDialogRequestEventArgs(release, isManualRequest: true));
            }
            else if (result.Status is UpdateCheckStatus.UpToDate or UpdateCheckStatus.NoPublishedReleases)
            {
                AvailableUpdateRelease = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual update check failed unexpectedly.");
            IsUpdateCheckFeedbackError = true;
            UpdateCheckFeedbackText = FormatLocalizedUpdateString(
                "Update_ErrorGeneric",
                "Unable to check for updates: {0}",
                ex.Message);
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    /// <summary>
    /// Re-opens the update modal dialog when a newer release has already been discovered.
    /// </summary>
    [RelayCommand]
    private void OpenAvailableUpdateDialog()
    {
        if (AvailableUpdateRelease is { } release)
        {
            UpdateDialogRequested?.Invoke(
                this,
                new UpdateDialogRequestEventArgs(release, isManualRequest: true));
        }
    }

    /// <summary>
    /// Streams the release installer to <c>%TEMP%</c>, verifies its SHA-256 digest in streaming fashion,
    /// and if valid launches the native installer and cleanly shuts down the application.
    /// </summary>
    internal async Task<(bool Succeeded, bool IsCancelled, string? ErrorMessage)> DownloadVerifyAndLaunchUpdateAsync(
        UpdateReleaseInfo release,
        Action<UpdateDownloadProgress, string> onProgress,
        Action? beforeWindowClose,
        CancellationToken cancellationToken)
    {
        var progressAdapter = new Progress<UpdateDownloadProgress>(snapshot =>
        {
            string statusText = snapshot.Phase switch
            {
                UpdateDownloadPhase.CleaningTemp => GetLocalizedUpdateString("Update_StatusCleaning", "Preparing secure download..."),
                UpdateDownloadPhase.Downloading => snapshot.IsIndeterminate
                    ? GetLocalizedUpdateString("Update_StatusDownloadingIndeterminate", "Downloading update...")
                    : FormatLocalizedUpdateString("Update_StatusDownloading", "Downloading update ({0:0}%)...", snapshot.ProgressPercent),
                UpdateDownloadPhase.VerifyingIntegrity => GetLocalizedUpdateString("Update_StatusVerifying", "Verifying SHA-256 integrity..."),
                UpdateDownloadPhase.ReadyToInstall => GetLocalizedUpdateString("Update_StatusLaunching", "Integrity verified. Launching installer and closing app..."),
                _ => string.Empty
            };

            onProgress(snapshot, statusText);
        });

        var downloadResult = await _appUpdateService.DownloadAndVerifyUpdateAsync(
            release,
            progressAdapter,
            cancellationToken);

        if (!downloadResult.Succeeded)
        {
            if (downloadResult.IsCancelled)
            {
                return (false, true, null);
            }

            string errorMessage = downloadResult.IsIntegrityMismatch
                ? GetLocalizedUpdateString(
                    "Update_ErrorIntegrityMismatch",
                    "SHA-256 integrity verification failed: the downloaded file did not match the official checksum and was immediately deleted for your safety.")
                : FormatLocalizedUpdateString(
                    "Update_ErrorDownloadFailed",
                    "Failed to download the update: {0}",
                    downloadResult.ErrorMessage ?? "Unknown error");

            return (false, false, errorMessage);
        }

        if (string.IsNullOrWhiteSpace(downloadResult.VerifiedFilePath))
        {
            return (false, false, GetLocalizedUpdateString(
                "Update_ErrorIntegrityMismatch",
                "SHA-256 integrity verification failed."));
        }

        onProgress(
            new UpdateDownloadProgress(UpdateDownloadPhase.ReadyToInstall, 0, null, 100, false),
            GetLocalizedUpdateString("Update_StatusLaunching", "Integrity verified. Launching installer and closing app..."));

        var launchResult = _installerLauncher.LaunchVerifiedInstallerAndShutdown(
            downloadResult.VerifiedFilePath,
            beforeWindowClose);

        if (!launchResult.Launched)
        {
            string launchError = launchResult.WasUacCancelled
                ? GetLocalizedUpdateString("Update_ErrorUacCancelled", "Installation was cancelled at the Windows permission (UAC) prompt.")
                : FormatLocalizedUpdateString(
                    "Update_ErrorLaunchFailed",
                    "Could not launch the verified installer: {0}",
                    launchResult.ErrorMessage ?? "Unknown error");

            return (false, false, launchError);
        }

        return (true, false, null);
    }

    /// <summary>
    /// Resolves a localized update UI string via <see cref="Core.Abstractions.ILocalizationService"/> with safe fallback.
    /// </summary>
    internal string GetLocalizedUpdateString(string key, string fallback)
    {
        string value = _localizationService.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    internal string FormatLocalizedUpdateString(string key, string fallbackFormat, params object[] args)
    {
        string template = GetLocalizedUpdateString(key, fallbackFormat);
        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentUICulture, template, args);
        }
        catch (FormatException)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, fallbackFormat, args);
        }
    }

    private void ApplyUpdateCheckResultFeedback(UpdateCheckResult result)
    {
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable when result.AvailableUpdate is not null:
                IsUpdateCheckFeedbackError = false;
                UpdateCheckFeedbackText = FormatLocalizedUpdateString(
                    "Update_StatusAvailable",
                    "Update available: {0} (installed: {1})",
                    result.AvailableUpdate.Version.DisplayTag,
                    CurrentAppVersionDisplay);
                break;

            case UpdateCheckStatus.UpToDate:
                IsUpdateCheckFeedbackError = false;
                UpdateCheckFeedbackText = FormatLocalizedUpdateString(
                    "Update_StatusUpToDate",
                    "You are already running the latest version ({0}).",
                    CurrentAppVersionDisplay);
                break;

            case UpdateCheckStatus.NoPublishedReleases:
                IsUpdateCheckFeedbackError = false;
                UpdateCheckFeedbackText = FormatLocalizedUpdateString(
                    "Update_StatusNoReleases",
                    "No published releases were found on GitHub yet (current: {0}).",
                    CurrentAppVersionDisplay);
                break;

            case UpdateCheckStatus.NoCompatibleAsset:
                IsUpdateCheckFeedbackError = true;
                UpdateCheckFeedbackText = FormatLocalizedUpdateString(
                    "Update_ErrorNoCompatibleAsset",
                    "Release {0} was found, but it does not contain a Windows installer compatible with this architecture.",
                    result.LatestVersion?.DisplayTag ?? string.Empty);
                break;

            case UpdateCheckStatus.MissingChecksum:
                IsUpdateCheckFeedbackError = true;
                UpdateCheckFeedbackText = GetLocalizedUpdateString(
                    "Update_ErrorMissingChecksum",
                    "Update blocked for security: no valid SHA-256 checksum was provided for this installer.");
                break;

            case UpdateCheckStatus.NetworkError:
                IsUpdateCheckFeedbackError = true;
                UpdateCheckFeedbackText = GetLocalizedUpdateString(
                    "Update_ErrorNetwork",
                    "Could not reach GitHub Releases. Please check your internet connection and try again.");
                break;

            default:
                IsUpdateCheckFeedbackError = true;
                UpdateCheckFeedbackText = FormatLocalizedUpdateString(
                    "Update_ErrorGeneric",
                    "Unable to check for updates: {0}",
                    result.ErrorMessage ?? "Unknown error");
                break;
        }
    }

    private void RefreshUpdateLocalizedText()
    {
        OnPropertyChanged(nameof(UpdatesToggleOnLabel));
        OnPropertyChanged(nameof(UpdatesToggleOffLabel));

        if (!IsCheckingForUpdates && _lastManualUpdateCheckResult is not null)
        {
            ApplyUpdateCheckResultFeedback(_lastManualUpdateCheckResult);
        }
    }
}
