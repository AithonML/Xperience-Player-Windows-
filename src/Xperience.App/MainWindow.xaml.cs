using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using Xperience.App.Controls;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App;

/// <summary>
/// Persisted main window geometry and presentation state across application launches.
/// </summary>
public sealed record WindowBoundsState(
    int X,
    int Y,
    int Width,
    int Height,
    bool IsMaximized,
    bool HasPosition);

/// <summary>
/// Main application window configured with Windows 11 Mica backdrop, custom frameless titlebar integration,
/// official application icon, persistent window bounds, and the Aurora + progressive logo Splash Screen overlay.
/// </summary>
public sealed partial class MainWindow : Window
{
    internal const string WindowBoundsSettingKey = "MainWindowBounds_v1";

    private readonly IThemeService? _themeService;
    private readonly IStorageService? _storageService;
    private readonly CancellationTokenSource _splashCts = new();
    private AnimatedSplashControl? _splashOverlay;
    private CancellationTokenSource? _boundsSaveDebounceCts;
    private AppWindow? _appWindow;
    private RectInt32 _restoredBounds;
    private bool _hasRestoredBounds;
    private bool _isMaximized;
    private bool _isApplyingInitialBounds;
    private bool _isClosed;
    private bool _splashStarted;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    /// <param name="initialBounds">Optional persisted window bounds preloaded before window activation.</param>
    public MainWindow(WindowBoundsState? initialBounds = null)
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            _themeService = app.Services.GetService<IThemeService>();
            _storageService = app.Services.GetService<IStorageService>();
        }

        ConfigureWindowAppearance(initialBounds);
        InitializeSplashThemeAndSync();

        Closed += OnWindowClosed;
    }

    private void ConfigureWindowAppearance(WindowBoundsState? initialBounds)
    {
        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
            }

            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow is not null)
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "xperience_icon_round.ico");
                if (File.Exists(iconPath))
                {
                    _appWindow.SetIcon(iconPath);
                }

                ApplyInitialWindowBounds(_appWindow, initialBounds);
                _appWindow.Changed += OnAppWindowChanged;

                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    var titleBar = _appWindow.TitleBar;
                    titleBar.ExtendsContentIntoTitleBar = true;
                    titleBar.ButtonBackgroundColor = Colors.Transparent;
                    titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                    UpdateTitleBarButtonForeground(_themeService?.CurrentTheme.IsDark ?? true);
                }
            }
        }
        catch
        {
            // Fallback to standard window chrome if customization is unsupported
        }
    }

    private void ApplyInitialWindowBounds(AppWindow appWindow, WindowBoundsState? saved)
    {
        _isApplyingInitialBounds = true;
        try
        {
            RectInt32 targetRect;
            bool shouldMaximize = false;

            if (saved is { Width: >= 400, Height: >= 300 })
            {
                var probeRect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
                var displayArea = saved.HasPosition
                    ? DisplayArea.GetFromRect(probeRect, DisplayAreaFallback.Nearest)
                    : DisplayArea.Primary;

                var workArea = displayArea?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);
                int minW = Math.Min(800, workArea.Width);
                int minH = Math.Min(540, workArea.Height);
                int width = Math.Clamp(saved.Width, minW, workArea.Width);
                int height = Math.Clamp(saved.Height, minH, workArea.Height);

                int x;
                int y;
                if (saved.HasPosition)
                {
                    x = Math.Clamp(saved.X, workArea.X, workArea.X + Math.Max(0, workArea.Width - width));
                    y = Math.Clamp(saved.Y, workArea.Y, workArea.Y + Math.Max(0, workArea.Height - height));
                }
                else
                {
                    x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
                    y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
                }

                targetRect = new RectInt32(x, y, width, height);
                shouldMaximize = saved.IsMaximized;
            }
            else
            {
                var workArea = DisplayArea.Primary?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);
                int width = Math.Clamp((int)Math.Round(workArea.Width * 0.78), Math.Min(1100, workArea.Width), Math.Min(1480, workArea.Width));
                int height = Math.Clamp((int)Math.Round(workArea.Height * 0.82), Math.Min(680, workArea.Height), Math.Min(920, workArea.Height));
                int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
                int y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
                targetRect = new RectInt32(x, y, width, height);
            }

            _restoredBounds = targetRect;
            _hasRestoredBounds = true;
            _isMaximized = shouldMaximize;

            appWindow.MoveAndResize(targetRect);

            if (shouldMaximize && appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() => _isApplyingInitialBounds = false);
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isClosed || _isApplyingInitialBounds)
        {
            return;
        }

        if (!args.DidSizeChange && !args.DidPositionChange && !args.DidPresenterChange)
        {
            return;
        }

        CaptureCurrentWindowBounds();
        ScheduleDebouncedBoundsSave();
    }

    private void CaptureCurrentWindowBounds()
    {
        if (_appWindow is null)
        {
            return;
        }

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Minimized)
            {
                return;
            }

            _isMaximized = presenter.State == OverlappedPresenterState.Maximized;
            if (presenter.State == OverlappedPresenterState.Restored)
            {
                var size = _appWindow.Size;
                var pos = _appWindow.Position;
                if (size.Width >= 400 && size.Height >= 300)
                {
                    _restoredBounds = new RectInt32(pos.X, pos.Y, size.Width, size.Height);
                    _hasRestoredBounds = true;
                }
            }
        }
        else
        {
            var size = _appWindow.Size;
            var pos = _appWindow.Position;
            if (size.Width >= 400 && size.Height >= 300)
            {
                _restoredBounds = new RectInt32(pos.X, pos.Y, size.Width, size.Height);
                _hasRestoredBounds = true;
            }
        }
    }

    private WindowBoundsState? BuildCurrentBoundsState()
    {
        if (!_hasRestoredBounds || _restoredBounds.Width < 400 || _restoredBounds.Height < 300)
        {
            return null;
        }

        return new WindowBoundsState(
            X: _restoredBounds.X,
            Y: _restoredBounds.Y,
            Width: _restoredBounds.Width,
            Height: _restoredBounds.Height,
            IsMaximized: _isMaximized,
            HasPosition: true);
    }

    private void ScheduleDebouncedBoundsSave()
    {
        if (_storageService is null)
        {
            return;
        }

        _boundsSaveDebounceCts?.Cancel();
        _boundsSaveDebounceCts?.Dispose();
        _boundsSaveDebounceCts = new CancellationTokenSource();
        var token = _boundsSaveDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested && BuildCurrentBoundsState() is { } state)
                {
                    await _storageService.SaveAsync(WindowBoundsSettingKey, state, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by newer resize/move event
            }
            catch
            {
                // Non-critical persistence error
            }
        }, token);
    }

    private void FlushBoundsOnClose()
    {
        if (_storageService is null)
        {
            return;
        }

        _boundsSaveDebounceCts?.Cancel();
        _boundsSaveDebounceCts?.Dispose();
        _boundsSaveDebounceCts = null;

        CaptureCurrentWindowBounds();
        if (BuildCurrentBoundsState() is { } state)
        {
            try
            {
                Task.Run(() => _storageService.SaveAsync(WindowBoundsSettingKey, state))
                    .Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore persistence timeout during shutdown
            }
        }
    }

    private void InitializeSplashThemeAndSync()
    {
        bool isDark = _themeService?.CurrentTheme.IsDark ?? true;
        WindowRootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;

        _splashOverlay = new AnimatedSplashControl();
        Canvas.SetZIndex(_splashOverlay, 100);
        _splashOverlay.SetDarkTheme(isDark);
        WindowRootGrid.Children.Add(_splashOverlay);

        if (_themeService is not null)
        {
            _themeService.ThemeChanged += OnThemeServiceChanged;
        }

        WindowRootGrid.Loaded += OnWindowRootGridLoaded;
    }

    private void OnThemeServiceChanged(object? sender, ThemePaletteDefinition palette)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTitleBarButtonForeground(palette.IsDark);
            if (_splashOverlay is { } splash && WindowRootGrid.Children.Contains(splash))
            {
                splash.SetDarkTheme(palette.IsDark);
            }
        });
    }

    private void UpdateTitleBarButtonForeground(bool isDark)
    {
        try
        {
            if (_appWindow is not null && AppWindowTitleBar.IsCustomizationSupported())
            {
                _appWindow.TitleBar.ButtonForegroundColor = isDark
                    ? Colors.White
                    : Color.FromArgb(255, 14, 17, 26);
            }
        }
        catch
        {
            // Ignore if titlebar customization is unavailable
        }
    }

    private async void OnWindowRootGridLoaded(object sender, RoutedEventArgs e)
    {
        if (_splashStarted)
        {
            return;
        }

        _splashStarted = true;
        await RunSplashWithHomeSyncAsync(_splashCts.Token);
    }

    private async Task RunSplashWithHomeSyncAsync(CancellationToken ct)
    {
        var splash = _splashOverlay;
        try
        {
            if (splash is not null)
            {
                // 1. Run minimum entrance choreography (~1330 ms) while Home loads in parallel underneath
                await splash.PlayEntranceAsync(ct);
            }

            // 2. Wait for Home initial catalog + discovery feed with a 2400 ms safety cap
            Task homeReadyTask = MainShellPage.ViewModel.InitialHomeReadyTask;
            if (!homeReadyTask.IsCompleted)
            {
                await Task.WhenAny(homeReadyTask, Task.Delay(2400, ct));
            }

            // 3. Brief settle beat + smooth fade-out & scale-up (480 ms)
            await Task.Delay(120, ct);
            if (splash is not null)
            {
                await splash.PlayExitFadeOutAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed during splash
        }
        finally
        {
            WindowRootGrid.Loaded -= OnWindowRootGridLoaded;
            if (_splashOverlay is not null)
            {
                _splashOverlay.Shutdown();
                WindowRootGrid.Children.Remove(_splashOverlay);
                _splashOverlay = null;
            }

            try
            {
                CanvasDevice.GetSharedDevice().Trim();
            }
            catch
            {
                // Ignore trim errors if GPU device is unavailable
            }

            if (_themeService is not null)
            {
                _themeService.ThemeChanged -= OnThemeServiceChanged;
            }

            MainShellPage.NotifySplashCompleted();
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        FlushBoundsOnClose();

        if (_appWindow is not null)
        {
            _appWindow.Changed -= OnAppWindowChanged;
        }

        WindowRootGrid.Loaded -= OnWindowRootGridLoaded;
        _splashCts.Cancel();
        if (_splashOverlay is not null)
        {
            _splashOverlay.Shutdown();
            WindowRootGrid.Children.Remove(_splashOverlay);
            _splashOverlay = null;
        }

        if (_themeService is not null)
        {
            _themeService.ThemeChanged -= OnThemeServiceChanged;
        }
    }
}
