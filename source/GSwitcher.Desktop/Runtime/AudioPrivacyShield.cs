using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GSwitcher.Services
{
    public sealed class AudioPrivacyShield : IDisposable
    {
        private readonly Action<string, bool> _pushStatus;
        private readonly object _syncRoot = new object();
        private IMMDeviceEnumerator _enumerator;
        private AudioNotificationClient _notificationClient;
        private bool _running;
        private bool _disposed;
        private DateTime _lastMuteUtc = DateTime.MinValue;

        public AudioPrivacyShield(Action<string, bool> pushStatus)
        {
            _pushStatus = pushStatus;
        }

        public void Start()
        {
            lock (_syncRoot)
            {
                if (_disposed || _running)
                {
                    return;
                }

                try
                {
                    _enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
                    _notificationClient = new AudioNotificationClient(this);
                    _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
                    _running = true;
                    AppLogger.Write("Audio privacy shield started.");
                }
                catch (Exception ex)
                {
                    AppLogger.Write("Audio privacy shield start failed: " + ex);
                    SafePushStatus("Audio privacy shield could not start.", true);
                    CleanupLocked();
                }
            }
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                if (!_running)
                {
                    return;
                }

                CleanupLocked();
                AppLogger.Write("Audio privacy shield stopped.");
            }
        }

        private void OnDefaultPlaybackChanged(EDataFlow flow, ERole role, string defaultDeviceId)
        {
            if (_disposed || !_running || flow != EDataFlow.Render)
            {
                return;
            }

            if (role != ERole.Console && role != ERole.Multimedia)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    GuardDefaultPlayback(defaultDeviceId);
                }
                catch (Exception ex)
                {
                    AppLogger.Write("Audio privacy shield endpoint handling failed: " + ex);
                }
            });
        }

        private void GuardDefaultPlayback(string deviceId)
        {
            if (!SettingsManager.Current.AudioPrivacyShield)
            {
                return;
            }

            IMMDevice device = null;
            try
            {
                lock (_syncRoot)
                {
                    if (_enumerator == null)
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(deviceId))
                    {
                        _enumerator.GetDevice(deviceId, out device);
                    }
                    else
                    {
                        _enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device);
                    }
                }

                var name = GetFriendlyName(device);
                if (!LooksLikeInternalSpeakers(name))
                {
                    AppLogger.Write("Audio privacy shield ignored endpoint: " + (name ?? "<unknown>"));
                    return;
                }

                if ((DateTime.UtcNow - _lastMuteUtc).TotalSeconds < 3)
                {
                    return;
                }

                MuteDevice(device);
                _lastMuteUtc = DateTime.UtcNow;
                var message = "Audio privacy shield muted internal speakers after output changed.";
                AppLogger.Write(message + " Endpoint=" + (name ?? "<unknown>"));
                SafePushStatus(message, false);
            }
            finally
            {
                ReleaseCom(device);
            }
        }

        private static string GetFriendlyName(IMMDevice device)
        {
            if (device == null)
            {
                return string.Empty;
            }

            IPropertyStore store = null;
            try
            {
                device.OpenPropertyStore(0, out store);
                if (store == null)
                {
                    return string.Empty;
                }

                var key = PropertyKeys.DeviceFriendlyName;
                PropVariant value;
                store.GetValue(ref key, out value);
                try
                {
                    return value.GetString();
                }
                finally
                {
                    value.Clear();
                }
            }
            finally
            {
                ReleaseCom(store);
            }
        }

        private static bool LooksLikeInternalSpeakers(string name)
        {
            name = (name ?? string.Empty).ToLowerInvariant();
            if (name.Length == 0)
            {
                return false;
            }

            if (name.Contains("headphone") ||
                name.Contains("headset") ||
                name.Contains("bluetooth") ||
                name.Contains("hdmi") ||
                name.Contains("display") ||
                name.Contains("monitor") ||
                name.Contains("nvidia"))
            {
                return false;
            }

            return name.Contains("speaker") ||
                   name.Contains("speakers") ||
                   name.Contains("realtek") ||
                   name.Contains("intel smart sound") ||
                   name.Contains("sst");
        }

        private static void MuteDevice(IMMDevice device)
        {
            if (device == null)
            {
                return;
            }

            object endpoint = null;
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, ClsCtx.InprocServer, IntPtr.Zero, out endpoint);
                var volume = endpoint as IAudioEndpointVolume;
                if (volume == null)
                {
                    return;
                }

                var context = Guid.Empty;
                volume.SetMute(true, ref context);
            }
            finally
            {
                ReleaseCom(endpoint);
            }
        }

        private void SafePushStatus(string message, bool isError)
        {
            try
            {
                if (_pushStatus != null)
                {
                    _pushStatus(message, isError);
                }
            }
            catch
            {
            }
        }

        private void CleanupLocked()
        {
            try
            {
                if (_enumerator != null && _notificationClient != null)
                {
                    _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Audio privacy shield unregister failed: " + ex.Message);
            }

            _running = false;
            _notificationClient = null;
            ReleaseCom(_enumerator);
            _enumerator = null;
        }

        private static void ReleaseCom(object value)
        {
            if (value == null)
            {
                return;
            }

            try
            {
                if (Marshal.IsComObject(value))
                {
                    Marshal.ReleaseComObject(value);
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                CleanupLocked();
            }
        }

        private sealed class AudioNotificationClient : IMMNotificationClient
        {
            private readonly AudioPrivacyShield _owner;

            public AudioNotificationClient(AudioPrivacyShield owner)
            {
                _owner = owner;
            }

            public void OnDeviceStateChanged(string deviceId, uint newState)
            {
            }

            public void OnDeviceAdded(string deviceId)
            {
            }

            public void OnDeviceRemoved(string deviceId)
            {
            }

            public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string defaultDeviceId)
            {
                if (_owner != null)
                {
                    _owner.OnDefaultPlaybackChanged(flow, role, defaultDeviceId);
                }
            }

            public void OnPropertyValueChanged(string deviceId, PropertyKey key)
            {
            }
        }

        private static class PropertyKeys
        {
            public static readonly PropertyKey DeviceFriendlyName = new PropertyKey
            {
                FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                PropertyId = 14
            };
        }

        private enum EDataFlow
        {
            Render = 0,
            Capture = 1,
            All = 2
        }

        private enum ERole
        {
            Console = 0,
            Multimedia = 1,
            Communications = 2
        }

        [Flags]
        private enum ClsCtx : uint
        {
            InprocServer = 0x1
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private sealed class MMDeviceEnumerator
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out object devices);
            void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            void RegisterEndpointNotificationCallback(IMMNotificationClient client);
            void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
        }

        [ComImport]
        [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMNotificationClient
        {
            void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
            void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
            void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            void Activate(ref Guid iid, ClsCtx clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
            void OpenPropertyStore(int access, out IPropertyStore properties);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetState(out uint state);
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint propertyCount);
            void GetAt(uint propertyIndex, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            void RegisterControlChangeNotify(IntPtr client);
            void UnregisterControlChangeNotify(IntPtr client);
            void GetChannelCount(out uint channelCount);
            void SetMasterVolumeLevel(float level, ref Guid eventContext);
            void SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            void GetMasterVolumeLevel(out float level);
            void GetMasterVolumeLevelScalar(out float level);
            void SetChannelVolumeLevel(uint channelNumber, float level, ref Guid eventContext);
            void SetChannelVolumeLevelScalar(uint channelNumber, float level, ref Guid eventContext);
            void GetChannelVolumeLevel(uint channelNumber, out float level);
            void GetChannelVolumeLevelScalar(uint channelNumber, out float level);
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
            void GetVolumeStepInfo(out uint step, out uint stepCount);
            void VolumeStepUp(ref Guid eventContext);
            void VolumeStepDown(ref Guid eventContext);
            void QueryHardwareSupport(out uint hardwareSupportMask);
            void GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public int PropertyId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            private ushort vt;
            private ushort reserved1;
            private ushort reserved2;
            private ushort reserved3;
            private IntPtr pointerValue;
            private int intValue;

            public string GetString()
            {
                if (vt == 31 && pointerValue != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(pointerValue);
                }

                return string.Empty;
            }

            public void Clear()
            {
                PropVariantClear(ref this);
            }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant propVariant);
    }
}
