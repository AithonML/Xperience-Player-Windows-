using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Xperience.App.Services;
using Xperience.App.Services.Audio;
using Xperience.App.Services.Plugins;
using Xperience.App.ViewModels;
using Xperience.App.Widgets;
using Xperience.Core.Abstractions;
using Xperience.Core.Updates;
using Xperience.Core.Widgets;

namespace Xperience.App;

/// <summary>
/// Enterprise WinUI 3 Application entrypoint managing the Dependency Injection container,
/// centralized exception logging, and lifecycle orchestration.
/// </summary>
public partial class App : Application
{
    private readonly ILogger<App> _logger;

    /// <summary>
    /// Gets the unified application <see cref="IServiceProvider"/> container.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// Gets the active main window instance.
    /// </summary>
    public static Window? MainWindowInstance { get; private set; }

    /// <summary>
    /// Gets or sets an optional widget command passed via CLI arguments when launched from a Windows 11 widget.
    /// </summary>
    public static WidgetCommandMessage? PendingWidgetLaunchAction { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="App"/> class.
    /// </summary>
    public App()
    {
        Services = ConfigureServices();
        _logger = Services.GetRequiredService<ILogger<App>>();

        InitializeComponent();
        RegisterCentralizedExceptionHandlers();
    }

    /// <inheritdoc />
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XperienceMusicPlayer");
        System.IO.Directory.CreateDirectory(logDir);
        var traceLog = System.IO.Path.Combine(logDir, "startup_trace.log");
        try
        {
            System.IO.File.WriteAllText(traceLog, $"[{DateTime.Now:O}] OnLaunched started\r\n");
            _logger.LogInformation("Launching Xperience Music Player (WinUI 3 + Win2D Liquid Glass)...");

            // Initialize any registered modular plugins asynchronously
            var plugins = Services.GetServices<IModulePlugin>();
            foreach (var plugin in plugins)
            {
                try
                {
                    await plugin.InitializeAsync(Services);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Plugin '{PluginId}' failed to initialize.", plugin.Id);
                }
            }

            // Initialize theme, DSP engine & load saved window bounds early so MainWindow opens at the exact theme and size on frame 0
            await Services.GetRequiredService<IThemeService>().InitializeAsync();
            await Services.GetRequiredService<IAudioDspController>().InitializeAsync();
            await Services.GetRequiredService<IDataUsageService>().InitializeAsync();
            var storageService = Services.GetRequiredService<IStorageService>();
            var savedWindowBounds = await storageService.LoadAsync<WindowBoundsState>(MainWindow.WindowBoundsSettingKey);

            System.IO.File.AppendAllText(traceLog, $"[{DateTime.Now:O}] Creating MainWindow...\r\n");
            MainWindowInstance = new MainWindow(savedWindowBounds);
            System.IO.File.AppendAllText(traceLog, $"[{DateTime.Now:O}] Activating MainWindow...\r\n");
            MainWindowInstance.Activate();
            System.IO.File.AppendAllText(traceLog, $"[{DateTime.Now:O}] MainWindow Activated successfully!\r\n");

            // Initialize Windows 11 Widget state bridge & local IPC listener (non-fatal if unpackaged)
            try
            {
                var widgetBridge = Services.GetRequiredService<WidgetStateBridge>();
                var launchAction = PendingWidgetLaunchAction;
                PendingWidgetLaunchAction = null;
                await widgetBridge.InitializeAsync(MainWindowInstance.DispatcherQueue, launchAction);
            }
            catch (Exception widgetEx)
            {
                _logger.LogWarning(widgetEx, "Windows 11 WidgetStateBridge initialization failed non-fatally.");
            }
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(traceLog, $"[{DateTime.Now:O}] FATAL OnLaunched Exception: {ex}\r\n");
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 1. Logging Infrastructure
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
        });

        // 2. Core Abstraction Services (Singletons)
        services.AddSingleton<IStorageService, StorageService>();
        services.AddSingleton<IDataUsageService, DataUsageService>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IYouTubeMusicService, YouTubeMusicService>();
        services.AddSingleton<ILyricsProviderService, LyricsProviderService>();
        services.AddSingleton<ICanvasArtworkService, AppleMusicCanvasService>();
        services.AddSingleton<IMediaCatalogService, MediaCatalogService>();
        services.AddSingleton<IMusicDownloadService, MornyeMusicDownloadService>();
        services.AddSingleton<NightcoreDspEngine>();
        services.AddSingleton<IAudioDspController, AudioDspController>();
        services.AddSingleton<IAudioPlayerService, AudioPlayerService>();
        services.AddSingleton<ISleepTimerService, SleepTimerService>();
        services.AddSingleton<WindowsInstallerLauncher>();
        services.AddSingleton<IAppUpdateService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<GitHubAppUpdateService>>();
            string updatesDir = GitHubAppUpdateService.GetDefaultUpdatesDirectory();
            try
            {
                System.IO.Directory.CreateDirectory(updatesDir);
                WindowsAccountSessionStore.ApplyUserOnlyDirectoryAcl(updatesDir);
            }
            catch
            {
                // Best-effort directory ACL hardening
            }

            return new GitHubAppUpdateService(
                currentVersion: WindowsInstallerLauncher.GetCompiledAppVersion(),
                log: msg => logger.LogInformation("{UpdateMessage}", msg),
                updatesDirectory: updatesDir,
                isPackagedApp: WindowsInstallerLauncher.IsPackagedIdentity());
        });

        // 3. Modular Plugins, Widgets & ViewModels
        services.AddSingleton<IModulePlugin, SpatialAudioEnhancerPlugin>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<WidgetStateBridge>();

        return services.BuildServiceProvider();
    }

    private void RegisterCentralizedExceptionHandlers()
    {
        UnhandledException += (sender, e) =>
        {
            _logger.LogCritical(e.Exception, "Unhandled UI thread exception intercepted: {Message}", e.Message);
            try
            {
                var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XperienceMusicPlayer");
                System.IO.Directory.CreateDirectory(logDir);
                var crashLogPath = System.IO.Path.Combine(logDir, "crash.log");
                string entry = $"[{DateTime.Now:O}] UI Exception: {e.Message}\r\n{e.Exception}\r\n\r\n";
                const long maxCrashLogBytes = 512 * 1024;
                if (System.IO.File.Exists(crashLogPath) &&
                    new System.IO.FileInfo(crashLogPath).Length >= maxCrashLogBytes)
                {
                    System.IO.File.WriteAllText(crashLogPath, entry);
                }
                else
                {
                    System.IO.File.AppendAllText(crashLogPath, entry);
                }
            }
            catch { }
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            _logger.LogError(e.Exception, "Unobserved background task exception intercepted.");
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                _logger.LogCritical(ex, "Fatal AppDomain unhandled exception.");
            }
        };
    }
}
