using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using GSwitcher.Backend;
using GSwitcher.Models;

namespace GSwitcher.Services
{
    public sealed class AutomationEngine : IDisposable
    {
        private const int IdleThresholdMilliseconds = 10 * 60 * 1000;
        private const int WakeThresholdMilliseconds = 5000;
        private const int IdleCheckIntervalMilliseconds = 10000;
        private const int HotkeyId = 0x4753;
        private const int TelemetryHotkeyId = 0x4754;
        private const int WmHotkey = 0x0312;
        private const int WmPowerBroadcast = 0x0218;
        private const int PbtPowerSettingChange = 0x8013;
        private const int DeviceNotifyWindowHandle = 0;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint VkSpace = 0x20;
        private const uint VkT = 0x54;
        private static readonly Guid LidSwitchStateChange =
            new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

        private readonly MainWindow _owner;
        private readonly Func<AppState> _getState;
        private readonly Func<string, string, bool, bool> _applyPowerMode;
        private readonly Action<string, bool> _pushStatus;
        private readonly Action _pushEmergencyEco;

        private System.Threading.Timer _idleTimer;
        private HotkeyMessageWindow _messageWindow;
        private IntPtr _hotkeyHwnd;
        private bool _hotkeyRegistered;
        private bool _telemetryHotkeyRegistered;
        private IntPtr _lidNotificationHandle;
        private bool _disposed;
        private string _preAfkState;
        private string _preSuspendState;
        private bool _emergencyEcoApplied;
        private bool? _lidClosed;
        private int _automationActionRunning;
        private OsdWindow _osdWindow;
        private TelemetryHudWindow _telemetryHudWindow;
        private AudioPrivacyShield _audioPrivacyShield;
        private readonly NetworkFocusService _networkFocusService = new NetworkFocusService();

        public AutomationEngine(
            MainWindow owner,
            Func<AppState> getState,
            Func<string, string, bool, bool> applyPowerMode,
            Action<string, bool> pushStatus,
            Action pushEmergencyEco)
        {
            _owner = owner;
            _getState = getState;
            _applyPowerMode = applyPowerMode;
            _pushStatus = pushStatus;
            _pushEmergencyEco = pushEmergencyEco;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint CbSize;
            public uint DwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LastInputInfo plii);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PowerBroadcastSetting
        {
            public Guid PowerSetting;
            public int DataLength;
            public int Data;
        }

        public void Start()
        {
            if (_disposed || _idleTimer != null)
            {
                return;
            }

            _idleTimer = new System.Threading.Timer(OnIdleTimer, null, IdleCheckIntervalMilliseconds, IdleCheckIntervalMilliseconds);
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            RefreshSettings();
            CheckBatteryBleedOut();
            AppLogger.Write("Automation engine started.");
        }

        public void RefreshSettings()
        {
            if (_disposed || _owner == null)
            {
                return;
            }

            if (!_owner.Dispatcher.CheckAccess())
            {
                _owner.Dispatcher.BeginInvoke(new Action(RefreshSettings));
                return;
            }

            RefreshHotkeyRegistration();
            RefreshAudioPrivacyShield();
            RefreshLidNotification();
            RefreshNetworkFocus();
        }

        private void OnIdleTimer(object state)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // Thermal Autopilot: Proactively protect hardware if temp spikes above 80°C
                try
                {
                    int currentTemp = ThermalDiagnosticsService.GetSystemTemperature();
                    if (currentTemp >= 80 &&
                        SettingsManager.Current.SmartCoolingAutomaticEnabled &&
                        !SmartCoolingService.IsSmartCoolingEnabled)
                    {
                        AppLogger.Write(string.Format("AutomationEngine: CPU Temperature is critically high ({0}°C). Activating Smart Cooling Autopilot (Medium profile)...", currentTemp));
                        if (_pushStatus != null)
                        {
                            _pushStatus(string.Format("Autopilot: High CPU temp ({0}°C) detected. Activating Smart Cooling...", currentTemp), true);
                        }
                        SmartCoolingService.Start("medium");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Write("Thermal Autopilot failed: " + ex.Message);
                }

                if (!SettingsManager.Current.AfkDownclock)
                {
                    _preAfkState = null;
                    return;
                }

                var idleMilliseconds = GetIdleMilliseconds();
                if (idleMilliseconds < 0)
                {
                    return;
                }

                if (idleMilliseconds >= IdleThresholdMilliseconds)
                {
                    var currentMode = PowerManager.NormalizeMode(PowerManager.DetectActiveMode());
                    if (currentMode == "tested-ultra" && _preAfkState == null)
                    {
                        _preAfkState = "ultra";
                        SwitchPower("daily", "AFK downclock", allowElevation: false);
                    }

                    return;
                }

                if (idleMilliseconds <= WakeThresholdMilliseconds && _preAfkState == "ultra")
                {
                    if (SettingsManager.Current.AfkDownclock)
                    {
                        if (SwitchPower("ultra", "AFK restore", allowElevation: false))
                        {
                            _preAfkState = null;
                        }
                    }
                    else
                    {
                        _preAfkState = null;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("AFK automation failed: " + ex);
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (e.Mode == PowerModes.Suspend)
                {
                    if (SettingsManager.Current.StandbyHygiene)
                    {
                        var activeMode = PowerManager.NormalizeMode(PowerManager.DetectActiveMode());
                        _preSuspendState = activeMode == "other" ? null : activeMode;
                        SwitchPower("battery", "Standby hygiene", allowElevation: false);
                    }

                    return;
                }

                if (e.Mode == PowerModes.Resume)
                {
                    if (SettingsManager.Current.StandbyHygiene && !string.IsNullOrWhiteSpace(_preSuspendState))
                    {
                        var restoreMode = _preSuspendState;
                        _preSuspendState = null;
                        SwitchPower(restoreMode, "Standby restore", allowElevation: false);
                    }

                    CheckBatteryBleedOut();
                    return;
                }

                if (e.Mode == PowerModes.StatusChange)
                {
                    CheckBatteryBleedOut();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Power-mode automation failed: " + ex);
            }
        }

        private void CheckBatteryBleedOut()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (!SettingsManager.Current.BatteryBleedOut)
                {
                    _emergencyEcoApplied = false;
                    return;
                }

                var powerStatus = SystemInformation.PowerStatus;
                if (powerStatus.PowerLineStatus != PowerLineStatus.Offline)
                {
                    _emergencyEcoApplied = false;
                    return;
                }

                var batteryPercent = powerStatus.BatteryLifePercent;
                if (batteryPercent < 0)
                {
                    return;
                }

                if (batteryPercent <= 0.15f && !_emergencyEcoApplied)
                {
                    _emergencyEcoApplied = true;
                    if (_pushEmergencyEco != null)
                    {
                        _pushEmergencyEco();
                    }

                    SwitchPower("battery", "Emergency battery guard", allowElevation: false);
                    return;
                }

                if (batteryPercent > 0.18f)
                {
                    _emergencyEcoApplied = false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Battery automation failed: " + ex);
            }
        }

        private void RefreshHotkeyRegistration()
        {
            var settings = SettingsManager.Current;
            var enabled = settings.QuickSwitchOsd;

            if (enabled && !_hotkeyRegistered)
            {
                EnsureMessageHook();
                if (_hotkeyHwnd == IntPtr.Zero)
                {
                    return;
                }

                _hotkeyRegistered = RegisterHotKey(_hotkeyHwnd, HotkeyId, ModControl | ModShift, VkSpace);
                if (!_hotkeyRegistered)
                {
                    AppLogger.Write("Quick OSD hotkey registration failed. Win32 error=" + Marshal.GetLastWin32Error());
                    if (_pushStatus != null)
                    {
                        _pushStatus("Quick OSD hotkey could not be registered.", true);
                    }
                }
                else
                {
                    AppLogger.Write("Quick OSD hotkey registered: Ctrl+Shift+Space.");
                }
            }
            else if (!enabled && _hotkeyRegistered)
            {
                UnregisterHotKey(_hotkeyHwnd, HotkeyId);
                _hotkeyRegistered = false;
                AppLogger.Write("Quick OSD hotkey unregistered.");
            }

            if (!_telemetryHotkeyRegistered)
            {
                EnsureMessageHook();
                if (_hotkeyHwnd == IntPtr.Zero)
                {
                    return;
                }

                _telemetryHotkeyRegistered = RegisterHotKey(_hotkeyHwnd, TelemetryHotkeyId, ModAlt | ModShift, VkT);
                if (!_telemetryHotkeyRegistered)
                {
                    AppLogger.Write("Phantom HUD hotkey registration failed. Win32 error=" + Marshal.GetLastWin32Error());
                    if (_pushStatus != null)
                    {
                        _pushStatus("Phantom HUD hotkey Alt+Shift+T could not be registered.", true);
                    }
                }
                else
                {
                    AppLogger.Write("Phantom HUD hotkey registered: Alt+Shift+T.");
                }
            }

            if (!settings.TelemetryHud)
            {
                CloseTelemetryHud();
            }
        }

        private void RefreshAudioPrivacyShield()
        {
            var enabled = SettingsManager.Current.AudioPrivacyShield;
            if (enabled)
            {
                if (_audioPrivacyShield == null)
                {
                    _audioPrivacyShield = new AudioPrivacyShield(_pushStatus);
                }

                _audioPrivacyShield.Start();
                return;
            }

            if (_audioPrivacyShield != null)
            {
                _audioPrivacyShield.Stop();
            }
        }

        private void RefreshNetworkFocus()
        {
            var settings = SettingsManager.Current;
            var state = _getState == null ? null : _getState();
            var mode = state == null ? PowerManager.DetectActiveMode() : state.PowerMode;
            _networkFocusService.Refresh(mode, settings.BitsServiceHook, _pushStatus);
        }

        public void NotifyPowerModeChanged(string mode)
        {
            if (_disposed)
            {
                return;
            }

            mode = PowerManager.NormalizeMode(mode);
            _networkFocusService.Refresh(mode, SettingsManager.Current.BitsServiceHook, _pushStatus);

            if (mode == "tested-ultra" && IsLidClosedUltraBlocked())
            {
                SwitchPower("daily", "Lid failsafe", allowElevation: false);
            }
        }

        public bool IsPowerModeBlocked(string mode, out string message)
        {
            mode = PowerManager.NormalizeMode(mode);
            if (mode == "tested-ultra" && IsLidClosedUltraBlocked())
            {
                message = "Lid closed. Ultra mode is locked to reduce display heat risk.";
                return true;
            }

            message = string.Empty;
            return false;
        }

        private bool IsLidClosedUltraBlocked()
        {
            return SettingsManager.Current.LidClosedFailsafe && _lidClosed.HasValue && _lidClosed.Value;
        }

        private void RefreshLidNotification()
        {
            var enabled = SettingsManager.Current.LidClosedFailsafe;
            if (enabled && _lidNotificationHandle == IntPtr.Zero)
            {
                EnsureMessageHook();
                if (_hotkeyHwnd == IntPtr.Zero)
                {
                    return;
                }

                var guid = LidSwitchStateChange;
                _lidNotificationHandle = RegisterPowerSettingNotification(_hotkeyHwnd, ref guid, DeviceNotifyWindowHandle);
                if (_lidNotificationHandle == IntPtr.Zero)
                {
                    AppLogger.Write("Lid failsafe notification registration failed. Win32 error=" + Marshal.GetLastWin32Error());
                    if (_pushStatus != null)
                    {
                        _pushStatus("Lid failsafe could not subscribe to Windows lid events.", true);
                    }
                }
                else
                {
                    AppLogger.Write("Lid failsafe notification registered.");
                }
                return;
            }

            if (!enabled && _lidNotificationHandle != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_lidNotificationHandle);
                _lidNotificationHandle = IntPtr.Zero;
                _lidClosed = null;
                AppLogger.Write("Lid failsafe notification unregistered.");
            }
        }

        private void EnsureMessageHook()
        {
            if (_messageWindow != null)
            {
                return;
            }

            _messageWindow = new HotkeyMessageWindow(HandleHotkeyMessage, HandlePowerBroadcastMessage);
            _hotkeyHwnd = _messageWindow.Handle;
            AppLogger.Write("Automation message sink created: hwnd=" + _hotkeyHwnd + ".");
        }

        private bool HandleHotkeyMessage(int id)
        {
            if (id == HotkeyId)
            {
                if (SettingsManager.Current.QuickSwitchOsd)
                {
                    ShowQuickSwitchOsd();
                }

                return true;
            }

            if (id == TelemetryHotkeyId)
            {
                if (!SettingsManager.Current.TelemetryHud)
                {
                    SettingsManager.Update("telemetryHud", true);
                }

                ToggleTelemetryHud();
                return true;
            }

            return false;
        }

        private bool HandlePowerBroadcastMessage(IntPtr wParam, IntPtr lParam)
        {
            if (wParam.ToInt32() != PbtPowerSettingChange)
            {
                return false;
            }

            HandlePowerBroadcastSetting(lParam);
            return true;
        }

        private void HandlePowerBroadcastSetting(IntPtr lParam)
        {
            if (lParam == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var setting = (PowerBroadcastSetting)Marshal.PtrToStructure(lParam, typeof(PowerBroadcastSetting));
                if (setting.PowerSetting != LidSwitchStateChange || setting.DataLength < 4)
                {
                    return;
                }

                _lidClosed = setting.Data == 0;
                AppLogger.Write("Lid failsafe event: lid " + (_lidClosed.Value ? "closed" : "open") + ".");

                if (_lidClosed.Value && IsLidClosedUltraBlocked())
                {
                    var activeMode = PowerManager.NormalizeMode(PowerManager.DetectActiveMode());
                    if (activeMode == "tested-ultra")
                    {
                        if (_pushStatus != null)
                        {
                            _pushStatus("Lid closed. Ultra mode is being reduced to Daily.", true);
                        }

                        SwitchPower("daily", "Lid failsafe", allowElevation: false);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Lid failsafe event handling failed: " + ex.Message);
            }
        }

        private void ShowQuickSwitchOsd()
        {
            if (_disposed || _owner == null)
            {
                return;
            }

            if (!_owner.Dispatcher.CheckAccess())
            {
                _owner.Dispatcher.BeginInvoke(new Action(ShowQuickSwitchOsd));
                return;
            }

            if (!SettingsManager.Current.QuickSwitchOsd)
            {
                return;
            }

            if (_osdWindow != null && _osdWindow.IsVisible)
            {
                _osdWindow.Activate();
                return;
            }

            var currentMode = PowerManager.NormalizeMode(PowerManager.DetectActiveMode());
            _osdWindow = new OsdWindow(currentMode);
            _osdWindow.ModeAccepted += delegate(string mode)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    if (SettingsManager.Current.QuickSwitchOsd)
                    {
                        SwitchPower(mode, "Quick OSD", allowElevation: true);
                    }
                });
            };
            _osdWindow.Closed += delegate { _osdWindow = null; };
            _osdWindow.Show();
            _osdWindow.Activate();
        }

        private void ToggleTelemetryHud()
        {
            if (_disposed || _owner == null)
            {
                return;
            }

            if (!_owner.Dispatcher.CheckAccess())
            {
                _owner.Dispatcher.BeginInvoke(new Action(ToggleTelemetryHud));
                return;
            }

            if (_telemetryHudWindow != null && _telemetryHudWindow.IsVisible)
            {
                CloseTelemetryHud();
                return;
            }

            _telemetryHudWindow = new TelemetryHudWindow(delegate
            {
                var state = _getState == null ? null : _getState();
                return state == null ? PowerManager.DetectActiveMode() : state.PowerMode;
            });
            _telemetryHudWindow.Closed += delegate { _telemetryHudWindow = null; };
            _telemetryHudWindow.Show();
            AppLogger.Write("Phantom HUD shown.");
        }

        private void CloseTelemetryHud()
        {
            try
            {
                if (_telemetryHudWindow != null)
                {
                    _telemetryHudWindow.Close();
                    _telemetryHudWindow = null;
                    AppLogger.Write("Phantom HUD hidden.");
                }
            }
            catch
            {
            }
        }

        private bool SwitchPower(string mode, string reason, bool allowElevation)
        {
            if (Interlocked.CompareExchange(ref _automationActionRunning, 1, 0) != 0)
            {
                return false;
            }

            try
            {
                if (_applyPowerMode == null)
                {
                    return false;
                }

                return _applyPowerMode(mode, reason, allowElevation);
            }
            catch (Exception ex)
            {
                AppLogger.Write("Automation power switch failed (" + reason + "): " + ex);
                if (_pushStatus != null)
                {
                    _pushStatus(reason + " failed. Check the app log.", true);
                }

                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _automationActionRunning, 0);
            }
        }

        private static int GetIdleMilliseconds()
        {
            var info = new LastInputInfo();
            info.CbSize = (uint)Marshal.SizeOf(typeof(LastInputInfo));

            if (!GetLastInputInfo(ref info))
            {
                return -1;
            }

            unchecked
            {
                var elapsed = (uint)Environment.TickCount - info.DwTime;
                return elapsed > int.MaxValue ? int.MaxValue : (int)elapsed;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch
            {
            }

            try
            {
                if (_idleTimer != null)
                {
                    _idleTimer.Dispose();
                    _idleTimer = null;
                }
            }
            catch
            {
            }

            try
            {
                if (_hotkeyRegistered)
                {
                    UnregisterHotKey(_hotkeyHwnd, HotkeyId);
                    _hotkeyRegistered = false;
                }

                if (_telemetryHotkeyRegistered)
                {
                    UnregisterHotKey(_hotkeyHwnd, TelemetryHotkeyId);
                    _telemetryHotkeyRegistered = false;
                }

                if (_lidNotificationHandle != IntPtr.Zero)
                {
                    UnregisterPowerSettingNotification(_lidNotificationHandle);
                    _lidNotificationHandle = IntPtr.Zero;
                }

                if (_messageWindow != null)
                {
                    _messageWindow.Dispose();
                    _messageWindow = null;
                    _hotkeyHwnd = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Automation hotkey cleanup failed: " + ex.Message);
            }

            try
            {
                _networkFocusService.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Write("Network focus cleanup failed: " + ex.Message);
            }

            try
            {
                if (_audioPrivacyShield != null)
                {
                    _audioPrivacyShield.Dispose();
                    _audioPrivacyShield = null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Audio privacy shield cleanup failed: " + ex.Message);
            }

            try
            {
                CloseTelemetryHud();
            }
            catch
            {
            }

            try
            {
                if (_osdWindow != null)
                {
                    _osdWindow.Close();
                    _osdWindow = null;
                }
            }
            catch
            {
            }
        }

        private sealed class HotkeyMessageWindow : NativeWindow, IDisposable
        {
            private readonly Func<int, bool> _handleHotkey;
            private readonly Func<IntPtr, IntPtr, bool> _handlePowerBroadcast;
            private bool _disposed;

            public HotkeyMessageWindow(Func<int, bool> handleHotkey, Func<IntPtr, IntPtr, bool> handlePowerBroadcast)
            {
                _handleHotkey = handleHotkey;
                _handlePowerBroadcast = handlePowerBroadcast;
                CreateHandle(new CreateParams
                {
                    Caption = "GSwitcherAutomationMessageSink"
                });
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmHotkey && _handleHotkey != null && _handleHotkey(m.WParam.ToInt32()))
                {
                    return;
                }

                if (m.Msg == WmPowerBroadcast && _handlePowerBroadcast != null && _handlePowerBroadcast(m.WParam, m.LParam))
                {
                    return;
                }

                base.WndProc(ref m);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                DestroyHandle();
            }
        }
    }
}
