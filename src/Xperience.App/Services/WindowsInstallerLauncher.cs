using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;
using Xperience.Core.Updates;

namespace Xperience.App.Services;

/// <summary>
/// Result of attempting to launch a SHA-256-verified Windows update installer.
/// </summary>
public sealed record InstallerLaunchResult(
    bool Launched,
    bool WasUacCancelled = false,
    string? ErrorMessage = null);

/// <summary>
/// Launches a verified Windows installer (<c>.exe</c>, <c>.msix</c>, or <c>.msixbundle</c>)
/// and cleanly shuts down the active WinUI 3 application instance so file replacement proceeds without locks.
/// </summary>
public sealed class WindowsInstallerLauncher
{
    private const int ErrorCancelledWin32Code = 1223;

    private readonly IAudioPlayerService _audioPlayerService;
    private readonly ILogger<WindowsInstallerLauncher> _logger;
    private readonly string _allowedUpdatesDirectory;

    /// <summary>
    /// Initializes a new instance of <see cref="WindowsInstallerLauncher"/>.
    /// </summary>
    public WindowsInstallerLauncher(
        IAudioPlayerService audioPlayerService,
        ILogger<WindowsInstallerLauncher> logger,
        string? allowedUpdatesDirectory = null)
    {
        _audioPlayerService = audioPlayerService;
        _logger = logger;
        _allowedUpdatesDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(allowedUpdatesDirectory)
                ? GitHubAppUpdateService.GetDefaultUpdatesDirectory()
                : allowedUpdatesDirectory);

        try
        {
            Directory.CreateDirectory(_allowedUpdatesDirectory);
            WindowsAccountSessionStore.ApplyUserOnlyDirectoryAcl(_allowedUpdatesDirectory);
        }
        catch
        {
            // Best-effort directory preparation
        }
    }

    /// <summary>
    /// Resolves the compiled semantic version of the running Windows application from
    /// MSIX <see cref="Package.Current"/> (when packaged), <see cref="AssemblyInformationalVersionAttribute"/>,
    /// or <see cref="AssemblyName.Version"/>.
    /// </summary>
    public static SemVersion GetCompiledAppVersion()
    {
        if (TryGetPackagedVersion(out var packagedVersion) && packagedVersion is not null)
        {
            return packagedVersion;
        }

        var assembly = typeof(App).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip +commit metadata if injected by SDK SourceLink
            string withoutMetadata = informational.Split('+', 2)[0];
            if (SemVerComparer.TryParse(withoutMetadata, out var parsedInfo) && parsedInfo is not null)
            {
                return parsedInfo;
            }
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is not null)
        {
            return new SemVersion(
                Math.Max(0, assemblyVersion.Major),
                Math.Max(0, assemblyVersion.Minor),
                Math.Max(0, assemblyVersion.Build));
        }

        return new SemVersion(0, 2, 0);
    }

    /// <summary>
    /// Resolves the formatted display version of the running Windows application (e.g., <c>v0.2.0.0</c>)
    /// from MSIX <see cref="Package.Current"/> (when packaged), <see cref="AssemblyInformationalVersionAttribute"/>,
    /// or <see cref="AssemblyName.Version"/>.
    /// </summary>
    public static string GetCompiledAppVersionDisplay()
    {
        try
        {
            var pkgVer = Package.Current.Id.Version;
            return $"v{pkgVer.Major}.{pkgVer.Minor}.{pkgVer.Build}.{pkgVer.Revision}";
        }
        catch
        {
            // Unpackaged deployment: fall back to compiled assembly metadata
        }

        var assembly = typeof(App).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            string withoutMetadata = informational.Split('+', 2)[0].Trim();
            if (!string.IsNullOrEmpty(withoutMetadata))
            {
                return withoutMetadata.StartsWith('v') || withoutMetadata.StartsWith('V')
                    ? $"v{withoutMetadata[1..]}"
                    : $"v{withoutMetadata}";
            }
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is not null)
        {
            return $"v{Math.Max(0, assemblyVersion.Major)}.{Math.Max(0, assemblyVersion.Minor)}.{Math.Max(0, assemblyVersion.Build)}.{Math.Max(0, assemblyVersion.Revision)}";
        }

        return GetCompiledAppVersion().DisplayTag;
    }

    /// <summary>
    /// Detects whether the current process is running with MSIX package identity.
    /// Returns <c>false</c> for unpackaged WinUI 3 (<c>WindowsPackageType=None</c>) deployments.
    /// </summary>
    public static bool IsPackagedIdentity()
    {
        return TryGetPackagedVersion(out _);
    }

    /// <summary>
    /// Formats a concise Windows architecture badge label for Settings UI (e.g., <c>Windows x64</c>, <c>Windows ARM64</c>).
    /// </summary>
    public static string GetSystemArchitectureDisplay()
    {
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "Windows ARM64",
            Architecture.X64 => "Windows x64",
            Architecture.X86 => "Windows x86",
            var other => $"Windows {other}"
        };
    }

    /// <summary>
    /// Launches the SHA-256-verified installer file via native Windows ShellExecute (preserving UAC elevation
    /// and App Installer handling) and cleanly shuts down the current app instance to release file locks.
    /// </summary>
    public InstallerLaunchResult LaunchVerifiedInstallerAndShutdown(
        string verifiedInstallerPath,
        Action? beforeWindowClose = null,
        string? expectedSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(verifiedInstallerPath))
        {
            return new InstallerLaunchResult(false, ErrorMessage: "Installer path is empty.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(verifiedInstallerPath);
        }
        catch (Exception ex)
        {
            return new InstallerLaunchResult(false, ErrorMessage: ex.Message);
        }

        // Defense-in-depth: only launch supported Windows installers staged inside our updates directory
        string rootWithSep = _allowedUpdatesDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _allowedUpdatesDirectory
            : _allowedUpdatesDirectory + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ||
            !WindowsReleaseAssetSelector.IsSupportedInstallerExtension(fullPath) ||
            !File.Exists(fullPath))
        {
            _logger.LogError("Refused to launch unverified or out-of-scope installer path: {Path}", fullPath);
            return new InstallerLaunchResult(false, ErrorMessage: "Verified installer file could not be located.");
        }

        // TOCTOU mitigation: re-verify SHA-256 immediately before Process.Start when expected digest is supplied
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            if (!Sha256ChecksumParser.TryNormalizeSha256(expectedSha256, out string normalizedExpected) ||
                !VerifyFileSha256Sync(fullPath, normalizedExpected))
            {
                _logger.LogError("Pre-launch SHA-256 re-verification failed for installer: {Path}", fullPath);
                StreamingFileVerifier.TryDeleteFile(fullPath);
                return new InstallerLaunchResult(
                    false,
                    ErrorMessage: "Installer integrity verification failed immediately before launch.");
            }
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fullPath,
                WorkingDirectory = Path.GetDirectoryName(fullPath) ?? _allowedUpdatesDirectory,
                UseShellExecute = true
            };

            _logger.LogInformation("Launching verified Windows update installer: {Path}", fullPath);
            using var startedProcess = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledWin32Code)
        {
            _logger.LogInformation("User cancelled UAC elevation prompt for update installer.");
            return new InstallerLaunchResult(false, WasUacCancelled: true, ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch verified update installer at {Path}.", fullPath);
            return new InstallerLaunchResult(false, ErrorMessage: ex.Message);
        }

        PerformCleanShutdown(beforeWindowClose);
        return new InstallerLaunchResult(true);
    }

    private static bool VerifyFileSha256Sync(string filePath, string normalizedExpectedHex)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: false);
            byte[] computedBytes = SHA256.HashData(stream);
            byte[] expectedBytes = Convert.FromHexString(normalizedExpectedHex);
            return CryptographicOperations.FixedTimeEquals(computedBytes, expectedBytes);
        }
        catch
        {
            return false;
        }
    }

    private void PerformCleanShutdown(Action? beforeWindowClose)
    {
        _logger.LogInformation("Initiating clean application shutdown for update installation...");

        try
        {
            beforeWindowClose?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Non-critical error during pre-shutdown view cleanup.");
        }

        // Release active audio devices, MediaFoundation streams, and local audio file handles
        try
        {
            if (_audioPlayerService is IDisposable disposableAudio)
            {
                disposableAudio.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Non-critical error while disposing audio player during update shutdown.");
        }

        // Close MainWindow (triggers OnWindowClosed -> flushes window bounds & settings) and exit WinUI 3 loop
        try
        {
            if (App.MainWindowInstance is { } mainWindow)
            {
                mainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        mainWindow.Close();
                    }
                    catch { }

                    try
                    {
                        Application.Current?.Exit();
                    }
                    catch { }
                });
            }
            else
            {
                Application.Current?.Exit();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error requesting WinUI Application.Exit().");
        }

        // Fallback safety net: ensure process terminates cleanly if native WinUI message loop lingers
        // so the installer can overwrite binaries without hitting file locks
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            Environment.Exit(0);
        });
    }

    private static bool TryGetPackagedVersion(out SemVersion? version)
    {
        version = null;
        try
        {
            var pkgVer = Package.Current.Id.Version;
            version = new SemVersion(pkgVer.Major, pkgVer.Minor, pkgVer.Build);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
