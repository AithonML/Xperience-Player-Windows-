using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Services.Audio;

/// <summary>
/// Production Music Sleep Timer ("Temporizador de música") service supporting timed countdown presets
/// (5m, 15m, 30m, 45m, 60m, Custom), "Fin de la canción" (End of current track), live 1-second visual badge
/// updates, and a smooth 3-second exponential volume fade-out before pausing playback.
/// </summary>
public sealed class SleepTimerService : ISleepTimerService
{
    private const double FadeOutWindowSeconds = 3.2;

    private readonly IAudioPlayerService _audioPlayerService;
    private readonly ILogger<SleepTimerService> _logger;
    private readonly object _syncRoot = new();
    private readonly Timer _countdownTimer;

    private SleepTimerState _currentState = SleepTimerState.Inactive;
    private long _timerStartTimestamp;
    private TimeSpan _targetDuration = TimeSpan.Zero;
    private double _preFadeMasterVolume = 0.85;
    private bool _capturedPreFadeVolume;
    private string? _armedTrackId;
    private int _lastPublishedWholeSeconds = -1;
    private int _disposed;

    /// <inheritdoc />
    public event EventHandler<SleepTimerState>? StateChanged;

    /// <inheritdoc />
    public SleepTimerState CurrentState
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentState;
            }
        }
    }

    public SleepTimerService(
        IAudioPlayerService audioPlayerService,
        ILogger<SleepTimerService> logger)
    {
        _audioPlayerService = audioPlayerService;
        _logger = logger;
        _countdownTimer = new Timer(OnCountdownTick, null, Timeout.Infinite, Timeout.Infinite);
        _audioPlayerService.CurrentTrackChanged += OnAudioCurrentTrackChanged;
        _audioPlayerService.PositionChanged += OnAudioPositionChanged;
    }

    /// <inheritdoc />
    public void StartPreset(SleepTimerPresetMode mode)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (mode == SleepTimerPresetMode.Off)
        {
            Cancel();
            return;
        }

        if (mode == SleepTimerPresetMode.EndOfTrack)
        {
            StartEndOfTrackTimer();
            return;
        }

        int minutes = mode switch
        {
            SleepTimerPresetMode.Minutes5 => 5,
            SleepTimerPresetMode.Minutes15 => 15,
            SleepTimerPresetMode.Minutes30 => 30,
            SleepTimerPresetMode.Minutes45 => 45,
            SleepTimerPresetMode.Minutes60 => 60,
            _ => 15
        };

        StartCountdownInternal(TimeSpan.FromMinutes(minutes), mode);
    }

    /// <inheritdoc />
    public void StartCustomMinutes(int minutes)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        int clamped = Math.Clamp(minutes, 1, 360);
        StartCountdownInternal(TimeSpan.FromMinutes(clamped), SleepTimerPresetMode.Custom);
    }

    /// <inheritdoc />
    public void AddMinutes(int extraMinutes)
    {
        if (Volatile.Read(ref _disposed) != 0 || extraMinutes <= 0)
        {
            return;
        }

        SleepTimerState snapshot;
        lock (_syncRoot)
        {
            if (!_currentState.IsActive || _currentState.StopAtEndOfCurrentTrack)
            {
                StartCountdownInternal(TimeSpan.FromMinutes(extraMinutes), SleepTimerPresetMode.Custom);
                return;
            }

            RestoreVolumeIfFadingLocked();
            var added = TimeSpan.FromMinutes(extraMinutes);
            var elapsed = Stopwatch.GetElapsedTime(_timerStartTimestamp);
            var remaining = (_targetDuration - elapsed) + added;
            if (remaining > TimeSpan.FromHours(6))
            {
                remaining = TimeSpan.FromHours(6);
            }

            _timerStartTimestamp = Stopwatch.GetTimestamp();
            _targetDuration = remaining;
            _lastPublishedWholeSeconds = (int)Math.Ceiling(remaining.TotalSeconds);

            _currentState = _currentState with
            {
                RemainingTime = remaining,
                TotalDuration = _currentState.TotalDuration + added,
                IsFadingOut = false
            };
            snapshot = _currentState;
        }

        StateChanged?.Invoke(this, snapshot);
    }

    /// <inheritdoc />
    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        SleepTimerState snapshot;
        lock (_syncRoot)
        {
            SetTimerInterval(Timeout.Infinite, Timeout.Infinite);
            RestoreVolumeIfFadingLocked();
            _targetDuration = TimeSpan.Zero;
            _armedTrackId = null;
            _lastPublishedWholeSeconds = -1;
            _currentState = SleepTimerState.Inactive;
            snapshot = _currentState;
        }

        _logger.LogInformation("Music Sleep Timer cancelled.");
        StateChanged?.Invoke(this, snapshot);
    }

    private void StartEndOfTrackTimer()
    {
        SleepTimerState snapshot;
        lock (_syncRoot)
        {
            RestoreVolumeIfFadingLocked();
            SetTimerInterval(Timeout.Infinite, Timeout.Infinite);

            var currentTrack = _audioPlayerService.CurrentTrack;
            _armedTrackId = currentTrack?.Id ?? currentTrack?.Title ?? "__active_track__";
            var remaining = _audioPlayerService.Duration > _audioPlayerService.Position
                ? _audioPlayerService.Duration - _audioPlayerService.Position
                : TimeSpan.Zero;

            _currentState = new SleepTimerState
            {
                IsActive = true,
                Mode = SleepTimerPresetMode.EndOfTrack,
                RemainingTime = remaining,
                TotalDuration = _audioPlayerService.Duration,
                StopAtEndOfCurrentTrack = true,
                IsFadingOut = false
            };
            snapshot = _currentState;
        }

        _logger.LogInformation("Music Sleep Timer armed for End of Current Track.");
        StateChanged?.Invoke(this, snapshot);
    }

    private void StartCountdownInternal(TimeSpan duration, SleepTimerPresetMode mode)
    {
        SleepTimerState snapshot;
        lock (_syncRoot)
        {
            RestoreVolumeIfFadingLocked();
            _timerStartTimestamp = Stopwatch.GetTimestamp();
            _targetDuration = duration;
            _armedTrackId = null;
            _lastPublishedWholeSeconds = (int)Math.Ceiling(duration.TotalSeconds);

            _currentState = new SleepTimerState
            {
                IsActive = true,
                Mode = mode,
                RemainingTime = duration,
                TotalDuration = duration,
                StopAtEndOfCurrentTrack = false,
                IsFadingOut = false
            };
            snapshot = _currentState;
            SetTimerInterval(200, 200);
        }

        _logger.LogInformation("Music Sleep Timer started: {Mode} ({Minutes:0.0} min).", mode, duration.TotalMinutes);
        StateChanged?.Invoke(this, snapshot);
    }

    private void OnCountdownTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        SleepTimerState? updatedToPublish = null;
        bool expired = false;

        lock (_syncRoot)
        {
            if (!_currentState.IsActive || _currentState.StopAtEndOfCurrentTrack)
            {
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(_timerStartTimestamp);
            var remaining = _targetDuration - elapsed;

            if (remaining <= TimeSpan.Zero)
            {
                SetTimerInterval(Timeout.Infinite, Timeout.Infinite);
                expired = true;
                _currentState = SleepTimerState.Inactive;
                updatedToPublish = _currentState;
            }
            else
            {
                double remSec = remaining.TotalSeconds;
                bool fading = remSec <= FadeOutWindowSeconds;

                if (fading && _audioPlayerService.IsPlaying)
                {
                    if (!_capturedPreFadeVolume)
                    {
                        _preFadeMasterVolume = Math.Clamp(_audioPlayerService.Volume, 0.05, 1.0);
                        _capturedPreFadeVolume = true;
                    }

                    // Smooth exponential fade-out curve over final 3.2 seconds
                    double norm = Math.Clamp(remSec / FadeOutWindowSeconds, 0.0, 1.0);
                    double fadeGain = Math.Pow(norm, 1.65);
                    _audioPlayerService.Volume = Math.Clamp(_preFadeMasterVolume * fadeGain, 0.01, 1.0);
                }

                int wholeSecs = (int)Math.Ceiling(remSec);
                if (wholeSecs != _lastPublishedWholeSeconds || fading != _currentState.IsFadingOut)
                {
                    _lastPublishedWholeSeconds = wholeSecs;
                    _currentState = _currentState with
                    {
                        RemainingTime = remaining,
                        IsFadingOut = fading
                    };
                    updatedToPublish = _currentState;
                }
            }
        }

        if (expired)
        {
            _ = CompleteTimerAndPausePlaybackAsync();
        }

        if (updatedToPublish is not null)
        {
            StateChanged?.Invoke(this, updatedToPublish);
        }
    }

    private void OnAudioPositionChanged(object? sender, PlaybackPositionEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        SleepTimerState? updatedToPublish = null;
        lock (_syncRoot)
        {
            if (!_currentState.IsActive || !_currentState.StopAtEndOfCurrentTrack)
            {
                return;
            }

            var remaining = e.Duration > e.Position ? e.Duration - e.Position : TimeSpan.Zero;
            int wholeSecs = (int)Math.Ceiling(remaining.TotalSeconds);
            if (wholeSecs != _lastPublishedWholeSeconds)
            {
                _lastPublishedWholeSeconds = wholeSecs;
                _currentState = _currentState with
                {
                    RemainingTime = remaining,
                    TotalDuration = e.Duration
                };
                updatedToPublish = _currentState;
            }
        }

        if (updatedToPublish is not null)
        {
            StateChanged?.Invoke(this, updatedToPublish);
        }
    }

    private void OnAudioCurrentTrackChanged(object? sender, TrackItem? newTrack)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        bool shouldStopAtBoundary = false;
        SleepTimerState? deactivatedSnapshot = null;

        lock (_syncRoot)
        {
            if (!_currentState.IsActive || !_currentState.StopAtEndOfCurrentTrack)
            {
                return;
            }

            string? newTrackKey = newTrack?.Id ?? newTrack?.Title;
            if (!string.IsNullOrWhiteSpace(_armedTrackId) &&
                !string.Equals(_armedTrackId, newTrackKey, StringComparison.OrdinalIgnoreCase))
            {
                shouldStopAtBoundary = true;
                _armedTrackId = null;
                _lastPublishedWholeSeconds = -1;
                _currentState = SleepTimerState.Inactive;
                deactivatedSnapshot = _currentState;
            }
        }

        if (shouldStopAtBoundary)
        {
            _ = CompleteTimerAndPausePlaybackAsync();
        }

        if (deactivatedSnapshot is not null)
        {
            StateChanged?.Invoke(this, deactivatedSnapshot);
        }
    }

    private async Task CompleteTimerAndPausePlaybackAsync()
    {
        try
        {
            if (_audioPlayerService.IsPlaying)
            {
                await _audioPlayerService.TogglePlayPauseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error pausing playback at sleep timer expiration.");
        }
        finally
        {
            lock (_syncRoot)
            {
                RestoreVolumeIfFadingLocked();
            }
            _logger.LogInformation("Music Sleep Timer expired; playback paused cleanly.");
        }
    }

    private void RestoreVolumeIfFadingLocked()
    {
        if (_capturedPreFadeVolume)
        {
            try
            {
                _audioPlayerService.Volume = Math.Clamp(_preFadeMasterVolume, 0.05, 1.0);
            }
            catch
            {
                // Ignore transient volume restore error
            }
            finally
            {
                _capturedPreFadeVolume = false;
            }
        }
    }

    private void SetTimerInterval(int dueTimeMs, int periodMs)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _countdownTimer.Change(dueTimeMs, periodMs);
        }
        catch (ObjectDisposedException)
        {
            // Ignore concurrent disposal
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _audioPlayerService.CurrentTrackChanged -= OnAudioCurrentTrackChanged;
        _audioPlayerService.PositionChanged -= OnAudioPositionChanged;
        _countdownTimer.Dispose();

        lock (_syncRoot)
        {
            RestoreVolumeIfFadingLocked();
        }
    }
}
