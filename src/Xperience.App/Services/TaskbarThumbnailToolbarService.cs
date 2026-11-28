using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Xperience.App.ViewModels;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Playback;

namespace Xperience.App.Services;

/// <summary>
/// Native Windows Taskbar hover preview thumbnail toolbar (<c>ITaskbarList3</c>) providing
/// symmetric playback controls: <c>[Like] [Previous] [Play/Pause] [Next] [Shuffle]</c>.
/// <para>
/// Subclasses the WinUI 3 <see cref="MainWindow"/> HWND via <c>Comctl32.SetWindowSubclass</c>
/// to handle <c>TaskbarButtonCreated</c> (including Windows Explorer restarts),
/// <c>WM_COMMAND</c> (<c>THBN_CLICKED</c>), DPI changes, and system taskbar light/dark theme changes.
/// </para>
/// </summary>
public sealed class TaskbarThumbnailToolbarService : IDisposable
{
    private const uint WmDestroy = 0x0002;
    private const uint WmSettingChange = 0x001A;
    private const uint WmCommand = 0x0111;
    private const uint WmDpiChanged = 0x02E0;
    private const uint ThbnClicked = 0x1800;
    private const int SmCxSmIcon = 49;
    private static readonly UIntPtr SubclassId = new(0x58505442); // "XPTB"
    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11D0-958A-006097C9A090");

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ShellViewModel _viewModel;
    private readonly IAudioPlayerService? _audioPlayerService;
    private readonly ILogger? _logger;
    private readonly uint _taskbarButtonCreatedMsg;

    // Strong reference prevents GC collection while HWND is subclassed.
    private readonly SubclassProc _subclassProc;
    private readonly Dictionary<TaskbarIconKind, IntPtr> _iconCache = new();

    private ITaskbarList3? _taskbarList;
    private IReadOnlyList<TaskbarButtonDescriptor>? _lastAppliedButtons;
    private bool _isSubclassed;
    private bool _buttonsAdded;
    private bool _lastLightTaskbar;
    private int _lastIconSize;
    private int _syncQueued;
    private int _disposed;

    /// <summary>
    /// Initializes and attaches the Windows Taskbar thumbnail toolbar to <paramref name="hwnd"/>.
    /// </summary>
    public TaskbarThumbnailToolbarService(
        IntPtr hwnd,
        DispatcherQueue dispatcherQueue,
        ShellViewModel viewModel,
        IAudioPlayerService? audioPlayerService = null,
        ILogger? logger = null)
    {
        _hwnd = hwnd;
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _audioPlayerService = audioPlayerService;
        _logger = logger;

        _taskbarButtonCreatedMsg = RegisterWindowMessage("TaskbarButtonCreated");
        _subclassProc = WindowSubclassProc;

        if (_hwnd != IntPtr.Zero)
        {
            _isSubclassed = SetWindowSubclass(_hwnd, _subclassProc, SubclassId, IntPtr.Zero);
            if (!_isSubclassed)
            {
                _logger?.LogWarning("Failed to install Comctl32 window subclass for taskbar thumbnail toolbar.");
            }
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        if (_audioPlayerService is not null)
        {
            _audioPlayerService.CurrentTrackChanged += OnAudioCurrentTrackChanged;
            _audioPlayerService.IsPlayingChanged += OnAudioIsPlayingChanged;
        }
    }

    /// <summary>
    /// Defensive initialization fallback invoked after the window finishes loading in case
    /// <c>TaskbarButtonCreated</c> was broadcast before the subclass was attached.
    /// </summary>
    public void EnsureInitialized()
    {
        if (Volatile.Read(ref _disposed) != 0 || _buttonsAdded || _hwnd == IntPtr.Zero)
        {
            return;
        }

        InitializeAndAddButtons();
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        IntPtr dwRefData)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            if (_taskbarButtonCreatedMsg != 0 && uMsg == _taskbarButtonCreatedMsg)
            {
                // Explorer creates or recreates the taskbar button (including after Explorer.exe restart).
                _buttonsAdded = false;
                _lastAppliedButtons = null;
                InitializeAndAddButtons();
            }
            else if (uMsg == WmCommand)
            {
                ulong wp = unchecked((ulong)wParam.ToInt64());
                uint hiWord = (uint)((wp >> 16) & 0xFFFF);
                uint loWord = (uint)(wp & 0xFFFF);

                if (hiWord == ThbnClicked && TaskbarToolbarStateCalculator.TryMapButtonId(loWord, out var buttonId))
                {
                    _dispatcherQueue.TryEnqueue(() => DispatchButtonAction(buttonId));
                    return IntPtr.Zero;
                }
            }
            else if (uMsg == WmDpiChanged || uMsg == WmSettingChange)
            {
                _dispatcherQueue.TryEnqueue(RefreshIconsIfNeeded);
            }
            else if (uMsg == WmDestroy)
            {
                Dispose();
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void InitializeAndAddButtons()
    {
        if (Volatile.Read(ref _disposed) != 0 || _hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!EnsureTaskbarListCreated())
            {
                return;
            }

            EnsureIconCacheUpToDate(forceRebuild: false);
            var descriptors = BuildCurrentDescriptors();
            var nativeButtons = BuildNativeButtons(descriptors);

            int hr = _taskbarList!.ThumbBarAddButtons(_hwnd, (uint)nativeButtons.Length, nativeButtons);
            if (hr >= 0)
            {
                _buttonsAdded = true;
                _lastAppliedButtons = descriptors;
            }
            else
            {
                _logger?.LogDebug("ThumbBarAddButtons returned HRESULT 0x{HResult:X8}.", hr);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to initialize Windows taskbar thumbnail toolbar.");
        }
    }

    private bool EnsureTaskbarListCreated()
    {
        if (_taskbarList is not null)
        {
            return true;
        }

        var comType = Type.GetTypeFromCLSID(ClsidTaskbarList);
        if (comType is null)
        {
            return false;
        }

        if (Activator.CreateInstance(comType) is not ITaskbarList3 instance)
        {
            return false;
        }

        int hr = instance.HrInit();
        if (hr < 0)
        {
            try { Marshal.ReleaseComObject(instance); } catch { }
            return false;
        }

        _taskbarList = instance;
        return true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (e.PropertyName is nameof(ShellViewModel.CurrentTrack)
            or nameof(ShellViewModel.IsPlaying)
            or nameof(ShellViewModel.IsCurrentTrackLiked)
            or nameof(ShellViewModel.IsShuffleEnabled))
        {
            ScheduleToolbarSync();
        }
    }

    private void OnAudioCurrentTrackChanged(object? sender, TrackItem? track) => ScheduleToolbarSync();

    private void OnAudioIsPlayingChanged(object? sender, bool isPlaying) => ScheduleToolbarSync();

    private void ScheduleToolbarSync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _syncQueued, 1) == 0)
        {
            if (!_dispatcherQueue.TryEnqueue(SyncToolbarButtonsOnUiThread))
            {
                Interlocked.Exchange(ref _syncQueued, 0);
            }
        }
    }

    private void SyncToolbarButtonsOnUiThread()
    {
        Interlocked.Exchange(ref _syncQueued, 0);

        if (Volatile.Read(ref _disposed) != 0 || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_buttonsAdded)
        {
            InitializeAndAddButtons();
            return;
        }

        var descriptors = BuildCurrentDescriptors();
        if (TaskbarToolbarStateCalculator.AreEqual(_lastAppliedButtons, descriptors))
        {
            return;
        }

        PushButtonUpdate(descriptors);
    }

    private void RefreshIconsIfNeeded()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_buttonsAdded || _hwnd == IntPtr.Zero)
        {
            return;
        }

        int targetSize = GetTargetIconSize();
        bool isLightTaskbar = IsSystemTaskbarLightTheme();
        if (targetSize == _lastIconSize && isLightTaskbar == _lastLightTaskbar)
        {
            return;
        }

        EnsureIconCacheUpToDate(forceRebuild: true);
        var descriptors = BuildCurrentDescriptors();
        PushButtonUpdate(descriptors);
    }

    private void PushButtonUpdate(IReadOnlyList<TaskbarButtonDescriptor> descriptors)
    {
        if (_taskbarList is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            EnsureIconCacheUpToDate(forceRebuild: false);
            var nativeButtons = BuildNativeButtons(descriptors);
            int hr = _taskbarList.ThumbBarUpdateButtons(_hwnd, (uint)nativeButtons.Length, nativeButtons);
            if (hr >= 0)
            {
                _lastAppliedButtons = descriptors;
            }
            else
            {
                _logger?.LogDebug("ThumbBarUpdateButtons returned HRESULT 0x{HResult:X8}.", hr);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to update Windows taskbar thumbnail toolbar buttons.");
        }
    }

    private TaskbarToolbarSnapshot CaptureSnapshot()
    {
        var track = _viewModel.CurrentTrack;
        bool hasTrack = TaskbarToolbarStateCalculator.HasLoadedTrack(track?.Id)
            || TaskbarToolbarStateCalculator.HasLoadedTrack(_audioPlayerService?.CurrentTrack?.Id);

        bool isPlaying = _viewModel.IsPlaying || (_audioPlayerService?.IsPlaying ?? false);
        bool isLiked = _viewModel.IsCurrentTrackLiked;
        bool isShuffle = _viewModel.IsShuffleEnabled;

        return new TaskbarToolbarSnapshot(
            HasLoadedTrack: hasTrack,
            IsPlaying: isPlaying,
            IsCurrentTrackLiked: isLiked,
            IsShuffleEnabled: isShuffle);
    }

    private IReadOnlyList<TaskbarButtonDescriptor> BuildCurrentDescriptors() =>
        TaskbarToolbarStateCalculator.BuildButtons(CaptureSnapshot());

    private ThumbButton[] BuildNativeButtons(IReadOnlyList<TaskbarButtonDescriptor> descriptors)
    {
        var buttons = new ThumbButton[descriptors.Count];
        for (int i = 0; i < descriptors.Count; i++)
        {
            var desc = descriptors[i];
            _iconCache.TryGetValue(desc.Icon, out IntPtr hIcon);

            buttons[i] = new ThumbButton
            {
                dwMask = ThumbButtonMask.Icon | ThumbButtonMask.Tooltip | ThumbButtonMask.Flags,
                iId = (uint)desc.Id,
                iBitmap = 0,
                hIcon = hIcon,
                szTip = desc.Tooltip,
                dwFlags = desc.IsEnabled ? ThumbButtonFlags.Enabled : ThumbButtonFlags.Disabled
            };
        }

        return buttons;
    }

    private void DispatchButtonAction(TaskbarButtonId buttonId)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            switch (buttonId)
            {
                case TaskbarButtonId.Like:
                    if (_viewModel.ToggleLikeCurrentTrackCommand.CanExecute(null))
                    {
                        _viewModel.ToggleLikeCurrentTrackCommand.Execute(null);
                    }
                    break;

                case TaskbarButtonId.Previous:
                    if (_viewModel.PreviousTrackCommand.CanExecute(null))
                    {
                        _viewModel.PreviousTrackCommand.Execute(null);
                    }
                    break;

                case TaskbarButtonId.PlayPause:
                    if (_viewModel.TogglePlayPauseCommand.CanExecute(null))
                    {
                        _viewModel.TogglePlayPauseCommand.Execute(null);
                    }
                    break;

                case TaskbarButtonId.Next:
                    if (_viewModel.NextTrackCommand.CanExecute(null))
                    {
                        _viewModel.NextTrackCommand.Execute(null);
                    }
                    break;

                case TaskbarButtonId.Shuffle:
                    if (_viewModel.ToggleShuffleCommand.CanExecute(null))
                    {
                        _viewModel.ToggleShuffleCommand.Execute(null);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error executing taskbar thumbnail toolbar action {ButtonId}.", buttonId);
        }
    }

    private void EnsureIconCacheUpToDate(bool forceRebuild)
    {
        int iconSize = GetTargetIconSize();
        bool isLightTaskbar = IsSystemTaskbarLightTheme();

        if (!forceRebuild && _iconCache.Count > 0 && _lastIconSize == iconSize && _lastLightTaskbar == isLightTaskbar)
        {
            return;
        }

        DestroyCachedIcons();
        _lastIconSize = iconSize;
        _lastLightTaskbar = isLightTaskbar;

        foreach (TaskbarIconKind kind in Enum.GetValues<TaskbarIconKind>())
        {
            IntPtr hIcon = TaskbarVectorIconRenderer.CreateIcon(kind, iconSize, isLightTaskbar);
            if (hIcon != IntPtr.Zero)
            {
                _iconCache[kind] = hIcon;
            }
        }
    }

    private int GetTargetIconSize()
    {
        try
        {
            uint dpi = _hwnd != IntPtr.Zero ? GetDpiForWindow(_hwnd) : 96u;
            if (dpi == 0)
            {
                dpi = 96u;
            }

            int smIcon = GetSystemMetricsForDpi(SmCxSmIcon, dpi);
            if (smIcon >= 16 && smIcon <= 64)
            {
                return smIcon;
            }

            return Math.Clamp((int)Math.Round(16.0 * dpi / 96.0), 16, 48);
        }
        catch
        {
            return 20;
        }
    }

    private static bool IsSystemTaskbarLightTheme()
    {
        try
        {
            uint size = sizeof(int);
            int status = RegGetValueW(
                HkeyCurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme",
                RrfRtRegDword,
                out _,
                out int value,
                ref size);

            if (status == 0)
            {
                return value != 0;
            }
        }
        catch
        {
            // Default to dark Windows 11 taskbar icon palette
        }

        return false;
    }

    private void DestroyCachedIcons()
    {
        foreach (IntPtr hIcon in _iconCache.Values)
        {
            if (hIcon != IntPtr.Zero)
            {
                DestroyIcon(hIcon);
            }
        }

        _iconCache.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_audioPlayerService is not null)
        {
            _audioPlayerService.CurrentTrackChanged -= OnAudioCurrentTrackChanged;
            _audioPlayerService.IsPlayingChanged -= OnAudioIsPlayingChanged;
        }

        if (_isSubclassed && _hwnd != IntPtr.Zero)
        {
            _isSubclassed = false;
            try
            {
                RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
            }
            catch
            {
                // Ignore if window is already destroyed
            }
        }

        DestroyCachedIcons();

        if (_taskbarList is not null)
        {
            try
            {
                Marshal.ReleaseComObject(_taskbarList);
            }
            catch
            {
                // Ignore COM teardown errors during shutdown
            }

            _taskbarList = null;
        }
    }

    #region COM & Win32 Interop

    [Flags]
    private enum ThumbButtonMask : uint
    {
        Bitmap = 0x0001,
        Icon = 0x0002,
        Tooltip = 0x0004,
        Flags = 0x0008
    }

    [Flags]
    private enum ThumbButtonFlags : uint
    {
        Enabled = 0x0000,
        Disabled = 0x0001,
        DismissOnClick = 0x0002,
        NoBackground = 0x0004,
        Hidden = 0x0008,
        NonInteractive = 0x0010
    }

    // Matches ShObjIdl_core.h (#include <pshpack8.h>) on both x64 and x86.
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct ThumbButton
    {
        public ThumbButtonMask dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;

        public ThumbButtonFlags dwFlags;
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        [PreserveSig] int SetProgressState(IntPtr hwnd, int tbpFlags);
        [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        [PreserveSig] int UnregisterTab(IntPtr hwndTab);
        [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButton[] pButtons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButton[] pButtons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
    }

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly IntPtr HkeyCurrentUser = new(unchecked((int)0x80000001));
    private const uint RrfRtRegDword = 0x00000010;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegGetValueW(
        IntPtr hkey,
        string lpSubKey,
        string lpValue,
        uint dwFlags,
        out uint pdwType,
        out int pvData,
        ref uint pcbData);

    #endregion

    #region DPI-Aware Supersampled Vector HICON Renderer

    /// <summary>
    /// Renders crisp 4x4 supersampled anti-aliased 32-bit ARGB Win32 <c>HICON</c>s directly via
    /// <c>CreateDIBSection</c> + <c>CreateIconIndirect</c> with zero external <c>.ico</c> or GDI+ dependencies.
    /// </summary>
    private static class TaskbarVectorIconRenderer
    {
        private const uint DibRgbColors = 0;
        private const uint BiRgb = 0;

        public static IntPtr CreateIcon(TaskbarIconKind kind, int size, bool isLightTaskbar)
        {
            size = Math.Clamp(size, 16, 64);

            // Monochrome foreground adapts cleanly to Windows 11 dark/light taskbar surfaces.
            (byte r, byte g, byte b) primary = isLightTaskbar
                ? ((byte)0x1F, (byte)0x1F, (byte)0x1F)
                : ((byte)0xF3, (byte)0xF3, (byte)0xF3);

            (byte r, byte g, byte b) color = kind switch
            {
                TaskbarIconKind.LikeOn => ((byte)0xFF, (byte)0x4D, (byte)0x6D), // Vibrant rose heart
                TaskbarIconKind.ShuffleOn => isLightTaskbar
                    ? ((byte)0x4F, (byte)0x46, (byte)0xE5) // Rich indigo on light taskbar
                    : ((byte)0x7C, (byte)0x90, (byte)0xFF), // Crisp luminous indigo on dark taskbar
                _ => primary
            };

            byte[] bgraPixels = RenderPixels(kind, size, color.r, color.g, color.b);
            return CreateIconFromBgra(bgraPixels, size);
        }

        private static byte[] RenderPixels(TaskbarIconKind kind, int size, byte r, byte g, byte b)
        {
            var pixels = new byte[size * size * 4];
            const int samples = 4;
            const float invSamples = 1f / samples;
            const float invTotalSamples = 1f / (samples * samples);
            float aaRadius = 0.75f / size;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float coverage = 0f;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        float ny = (y + (sy + 0.5f) * invSamples) / size;
                        for (int sx = 0; sx < samples; sx++)
                        {
                            float nx = (x + (sx + 0.5f) * invSamples) / size;
                            float sdf = EvaluateGlyphSdf(kind, nx, ny);
                            coverage += SmoothCoverage(sdf, aaRadius);
                        }
                    }

                    float alpha = Math.Clamp(coverage * invTotalSamples, 0f, 1f);
                    int idx = (y * size + x) * 4;
                    pixels[idx + 0] = b;
                    pixels[idx + 1] = g;
                    pixels[idx + 2] = r;
                    pixels[idx + 3] = (byte)Math.Round(alpha * 255f);
                }
            }

            return pixels;
        }

        private static float SmoothCoverage(float signedDistance, float aaRadius)
        {
            if (signedDistance <= -aaRadius)
            {
                return 1f;
            }

            if (signedDistance >= aaRadius)
            {
                return 0f;
            }

            float t = (aaRadius - signedDistance) / (2f * aaRadius);
            return t * t * (3f - 2f * t);
        }

        private static float EvaluateGlyphSdf(TaskbarIconKind kind, float x, float y) =>
            kind switch
            {
                TaskbarIconKind.Play => SdfPlay(x, y),
                TaskbarIconKind.Pause => SdfPause(x, y),
                TaskbarIconKind.Previous => SdfPrevious(x, y),
                TaskbarIconKind.Next => SdfNext(x, y),
                TaskbarIconKind.LikeOn => SdfHeart(x, y, outlineOnly: false),
                TaskbarIconKind.LikeOff => SdfHeart(x, y, outlineOnly: true),
                TaskbarIconKind.ShuffleOff => SdfShuffle(x, y, showActiveDot: false),
                TaskbarIconKind.ShuffleOn => SdfShuffle(x, y, showActiveDot: true),
                _ => 1f
            };

        private static float SdfPlay(float x, float y)
        {
            // Optically centered right-pointing triangle with subtle corner rounding
            return SdfTriangle(x, y, 0.30f, 0.20f, 0.30f, 0.80f, 0.80f, 0.50f) - 0.025f;
        }

        private static float SdfPause(float x, float y)
        {
            float leftBar = SdfRoundedBox(x - 0.34f, y - 0.50f, 0.095f, 0.30f, 0.035f);
            float rightBar = SdfRoundedBox(x - 0.66f, y - 0.50f, 0.095f, 0.30f, 0.035f);
            return MathF.Min(leftBar, rightBar);
        }

        private static float SdfPrevious(float x, float y)
        {
            float bar = SdfRoundedBox(x - 0.25f, y - 0.50f, 0.055f, 0.27f, 0.025f);
            float tri = SdfTriangle(x, y, 0.77f, 0.23f, 0.77f, 0.77f, 0.35f, 0.50f) - 0.02f;
            return MathF.Min(bar, tri);
        }

        private static float SdfNext(float x, float y)
        {
            float tri = SdfTriangle(x, y, 0.23f, 0.23f, 0.23f, 0.77f, 0.65f, 0.50f) - 0.02f;
            float bar = SdfRoundedBox(x - 0.75f, y - 0.50f, 0.055f, 0.27f, 0.025f);
            return MathF.Min(tri, bar);
        }

        private static float SdfHeart(float x, float y, bool outlineOnly)
        {
            // Classic symmetric heart SDF mapped into [0..1] icon space
            float px = MathF.Abs(x - 0.50f) * 1.48f;
            float py = (0.79f - y) * 1.48f;

            float d;
            if (py + px > 1.0f)
            {
                float dx = px - 0.25f;
                float dy = py - 0.75f;
                d = MathF.Sqrt(dx * dx + dy * dy) - (MathF.Sqrt(2f) / 4f);
            }
            else
            {
                float d1x = px;
                float d1y = py - 1.0f;
                float d1 = d1x * d1x + d1y * d1y;

                float m = 0.5f * MathF.Max(px + py, 0f);
                float d2x = px - m;
                float d2y = py - m;
                float d2 = d2x * d2x + d2y * d2y;

                d = MathF.Sqrt(MathF.Min(d1, d2)) * MathF.Sign(px - py);
            }

            d /= 1.48f;
            return outlineOnly ? MathF.Abs(d + 0.035f) - 0.042f : d;
        }

        private static float SdfShuffle(float x, float y, bool showActiveDot)
        {
            const float stroke = 0.042f;

            // Upper-left -> lower-right crossing path
            float s1 = SdfSegment(x, y, 0.16f, 0.30f, 0.34f, 0.30f) - stroke;
            float s2 = SdfSegment(x, y, 0.34f, 0.30f, 0.62f, 0.64f) - stroke;
            float s3 = SdfSegment(x, y, 0.62f, 0.64f, 0.79f, 0.64f) - stroke;

            // Lower-left -> upper-right crossing path
            float s4 = SdfSegment(x, y, 0.16f, 0.64f, 0.34f, 0.64f) - stroke;
            float s5 = SdfSegment(x, y, 0.34f, 0.64f, 0.62f, 0.30f) - stroke;
            float s6 = SdfSegment(x, y, 0.62f, 0.30f, 0.79f, 0.30f) - stroke;

            // Two crisp arrowheads on the right
            float topArrow = SdfTriangle(x, y, 0.72f, 0.19f, 0.72f, 0.41f, 0.87f, 0.30f) - 0.008f;
            float botArrow = SdfTriangle(x, y, 0.72f, 0.53f, 0.72f, 0.75f, 0.87f, 0.64f) - 0.008f;

            float d = MathF.Min(MathF.Min(MathF.Min(s1, s2), MathF.Min(s3, s4)),
                                MathF.Min(MathF.Min(s5, s6), MathF.Min(topArrow, botArrow)));

            if (showActiveDot)
            {
                // Small centered active-state indicator dot beneath the shuffle glyph
                float dx = x - 0.50f;
                float dy = y - 0.86f;
                float dot = MathF.Sqrt(dx * dx + dy * dy) - 0.055f;
                d = MathF.Min(d, dot);
            }

            return d;
        }

        private static float SdfRoundedBox(float px, float py, float bx, float by, float r)
        {
            float qx = MathF.Abs(px) - bx + r;
            float qy = MathF.Abs(py) - by + r;
            float outLen = MathF.Sqrt(MathF.Max(qx, 0f) * MathF.Max(qx, 0f) + MathF.Max(qy, 0f) * MathF.Max(qy, 0f));
            return outLen + MathF.Min(MathF.Max(qx, qy), 0f) - r;
        }

        private static float SdfSegment(float px, float py, float ax, float ay, float bx, float by)
        {
            float pax = px - ax;
            float pay = py - ay;
            float bax = bx - ax;
            float bay = by - ay;
            float denom = bax * bax + bay * bay;
            float h = denom > 1e-6f ? Math.Clamp((pax * bax + pay * bay) / denom, 0f, 1f) : 0f;
            float dx = pax - bax * h;
            float dy = pay - bay * h;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private static float SdfTriangle(
            float px, float py,
            float ax, float ay,
            float bx, float by,
            float cx, float cy)
        {
            float d0 = SdfSegment(px, py, ax, ay, bx, by);
            float d1 = SdfSegment(px, py, bx, by, cx, cy);
            float d2 = SdfSegment(px, py, cx, cy, ax, ay);
            float dist = MathF.Min(d0, MathF.Min(d1, d2));

            float c0 = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
            float c1 = (cx - bx) * (py - by) - (cy - by) * (px - bx);
            float c2 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);
            bool inside = (c0 >= 0f && c1 >= 0f && c2 >= 0f) || (c0 <= 0f && c1 <= 0f && c2 <= 0f);
            return inside ? -dist : dist;
        }

        private static IntPtr CreateIconFromBgra(byte[] bgraPixels, int size)
        {
            var bmi = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = size,
                biHeight = -size, // Top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
                biSizeImage = (uint)bgraPixels.Length
            };

            IntPtr hbmColor = CreateDIBSection(IntPtr.Zero, ref bmi, DibRgbColors, out IntPtr bitsPtr, IntPtr.Zero, 0);
            if (hbmColor == IntPtr.Zero || bitsPtr == IntPtr.Zero)
            {
                if (hbmColor != IntPtr.Zero)
                {
                    DeleteObject(hbmColor);
                }
                return IntPtr.Zero;
            }

            IntPtr hbmMask = IntPtr.Zero;
            try
            {
                Marshal.Copy(bgraPixels, 0, bitsPtr, bgraPixels.Length);
                hbmMask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
                if (hbmMask == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                var iconInfo = new IconInfo
                {
                    fIcon = true,
                    xHotspot = 0,
                    yHotspot = 0,
                    hbmMask = hbmMask,
                    hbmColor = hbmColor
                };

                return CreateIconIndirect(ref iconInfo);
            }
            finally
            {
                // CreateIconIndirect copies both bitmaps; immediately release temporary GDI handles.
                if (hbmMask != IntPtr.Zero)
                {
                    DeleteObject(hbmMask);
                }

                DeleteObject(hbmColor);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BitmapInfoHeader pbmi,
            uint iUsage,
            out IntPtr ppvBits,
            IntPtr hSection,
            uint dwOffset);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, IntPtr lpvBits);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect(ref IconInfo piconinfo);
    }

    #endregion
}
