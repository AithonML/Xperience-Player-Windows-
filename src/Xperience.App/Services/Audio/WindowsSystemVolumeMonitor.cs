using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Xperience.App.Services.Audio;

/// <summary>
/// Monitors the Windows system default multimedia audio endpoint master volume and mute state
/// via Core Audio (<c>IMMDeviceEnumerator</c> + <c>IAudioEndpointVolume</c> + <c>IMMNotificationClient</c>)
/// with zero polling and automatic re-binding when the default playback device changes.
/// </summary>
public sealed class WindowsSystemVolumeMonitor : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private IMMDeviceEnumerator? _deviceEnumerator;
    private IMMDevice? _currentDevice;
    private IAudioEndpointVolume? _endpointVolume;
    private VolumeCallbackImpl? _volumeCallback;
    private DeviceNotificationImpl? _deviceCallback;
    private int _disposed;

    public event EventHandler<(float MasterVolumeScalar, bool IsMuted)>? SystemVolumeChanged;

    public float CurrentMasterVolumeScalar { get; private set; } = 1.0f;
    public bool IsSystemMuted { get; private set; }

    public WindowsSystemVolumeMonitor(ILogger logger)
    {
        _logger = logger;
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(CoreAudioGuids.MMDeviceEnumeratorClsid);
            if (enumeratorType is null)
            {
                return;
            }

            _deviceEnumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumeratorType);
            if (_deviceEnumerator is null)
            {
                return;
            }

            _deviceCallback = new DeviceNotificationImpl(this);
            _ = _deviceEnumerator.RegisterEndpointNotificationCallback(_deviceCallback);
            AttachToDefaultEndpointLocked();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows Core Audio system volume monitor unavailable.");
        }
    }

    private void AttachToDefaultEndpointLocked()
    {
        DetachCurrentEndpointLocked();

        if (_deviceEnumerator is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            int hr = _deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            if (hr < 0 || device is null)
            {
                return;
            }

            _currentDevice = device;
            Guid iid = CoreAudioGuids.IAudioEndpointVolumeIid;
            hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object activatedObj);
            if (hr < 0 || activatedObj is not IAudioEndpointVolume endpointVol)
            {
                return;
            }

            _endpointVolume = endpointVol;
            if (endpointVol.GetMasterVolumeLevelScalar(out float vol) >= 0)
            {
                CurrentMasterVolumeScalar = Math.Clamp(vol, 0f, 1f);
            }

            if (endpointVol.GetMute(out bool muted) >= 0)
            {
                IsSystemMuted = muted;
            }

            _volumeCallback = new VolumeCallbackImpl(this);
            _ = endpointVol.RegisterControlChangeNotify(_volumeCallback);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed attaching to default Windows audio endpoint.");
        }
    }

    private void DetachCurrentEndpointLocked()
    {
        if (_endpointVolume is not null)
        {
            try
            {
                if (_volumeCallback is not null)
                {
                    _ = _endpointVolume.UnregisterControlChangeNotify(_volumeCallback);
                }
            }
            catch { }

            try { Marshal.ReleaseComObject(_endpointVolume); } catch { }
            _endpointVolume = null;
            _volumeCallback = null;
        }

        if (_currentDevice is not null)
        {
            try { Marshal.ReleaseComObject(_currentDevice); } catch { }
            _currentDevice = null;
        }
    }

    private void HandleVolumeNotify(float masterVolumeScalar, bool isMuted)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        CurrentMasterVolumeScalar = Math.Clamp(masterVolumeScalar, 0f, 1f);
        IsSystemMuted = isMuted;
        SystemVolumeChanged?.Invoke(this, (CurrentMasterVolumeScalar, IsSystemMuted));
    }

    private void HandleDefaultDeviceChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            float vol;
            bool muted;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                AttachToDefaultEndpointLocked();
                vol = CurrentMasterVolumeScalar;
                muted = IsSystemMuted;
            }

            SystemVolumeChanged?.Invoke(this, (vol, muted));
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            DetachCurrentEndpointLocked();

            if (_deviceEnumerator is not null)
            {
                try
                {
                    if (_deviceCallback is not null)
                    {
                        _ = _deviceEnumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
                    }
                }
                catch { }

                try { Marshal.ReleaseComObject(_deviceEnumerator); } catch { }
                _deviceEnumerator = null;
                _deviceCallback = null;
            }
        }
    }

    private const uint ClsCtxAll = 23;

    private static class CoreAudioGuids
    {
        public static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IAudioEndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    }

    private enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioVolumeNotificationData
    {
        public Guid EventContext;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Muted;
        public float MasterVolume;
        public uint Channels;
        public float FirstChannelVolume;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr pNotifyData);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, uint dwNewState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, IntPtr key);
    }

    private sealed class VolumeCallbackImpl(WindowsSystemVolumeMonitor owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr pNotifyData)
        {
            if (pNotifyData != IntPtr.Zero)
            {
                var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(pNotifyData);
                owner.HandleVolumeNotify(data.MasterVolume, data.Muted);
            }

            return 0;
        }
    }

    private sealed class DeviceNotificationImpl(WindowsSystemVolumeMonitor owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) => 0;
        public int OnDeviceAdded(string pwstrDeviceId) => 0;
        public int OnDeviceRemoved(string pwstrDeviceId) => 0;
        public int OnPropertyValueChanged(string pwstrDeviceId, IntPtr key) => 0;

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId)
        {
            if (flow == EDataFlow.eRender && role == ERole.eMultimedia)
            {
                owner.HandleDefaultDeviceChanged();
            }

            return 0;
        }
    }
}
