using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using GSwitcher.Backend;
using GSwitcher.Hardware;
using GSwitcher.Models;
using GSwitcher.Services;
using GSwitcher.ViewModels;

namespace GSwitcher
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "Global\\GSwitcherDesktopApp";

        private Mutex _singleInstanceMutex;
        private NotifyIcon _notifyIcon;
        private MainWindow _mainWindow;
        private MainViewModel _mainViewModel;
        private AppState _state;
        private AppSettings _settings;
        private AutomationEngine _automationEngine;
        private int _backendCommandRunning;
        private int _deferredRefreshRunning;
        private int _thermalDiagnosticsRunning;
        private int _thermalDiagnosticsPending;

        private ToolStripMenuItem _openDashboardMenuItem;
        private ToolStripMenuItem _powerUltraMenuItem;
        private ToolStripMenuItem _powerDailyMenuItem;
        private ToolStripMenuItem _powerBatteryMenuItem;
        private ToolStripMenuItem _gpuRouteNvidiaMenuItem;
        private ToolStripMenuItem _gpuRouteHybridMenuItem;
        private ToolStripMenuItem _gpuAutoMenuItem;
        private ToolStripMenuItem _gpuNvidiaMenuItem;
        private ToolStripMenuItem _gpuIntegratedMenuItem;
        private ToolStripMenuItem _pinWindowMenuItem;
        private ToolStripMenuItem _installHelperMenuItem;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            RuntimeCleanupService.CleanupStaleRuntimeRoots();

            if (EphemeralElevationService.TryHandleEphemeralOperation(e.Args, ExecuteEphemeralOperation))
            {
                AppLogger.Write("Ephemeral elevated operation finished.");
                Shutdown(0);
                return;
            }

            bool createdNew;
            _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // Another instance is running. Send command to it and exit.
                // Note: This is a simple implementation. A more robust solution would use IPC.
                if (e.Args.Length > 0)
                {
                    // For now, we just exit. The running instance will handle user interaction.
                }
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (sender, args) =>
            {
                AppLogger.Write("Unhandled exception: " + args.Exception);
                args.Handled = true;

                if (_mainWindow != null)
                {
                    _mainWindow.PushStatus("Unexpected error. Check the app log for details.", true);
                }
            };

            _state = AppStateStore.Load();
            _settings = SettingsManager.Load();
            _state.Theme = _settings.Theme;
            _state.RequiresRestart = ShouldKeepRestartFlag(_state);
            if (!_state.RequiresRestart)
            {
                _state.GpuPendingRouteMode = "unknown";
            }

            _state.HelperInstalled = false;
            _state.EvidenceSessionPath = string.Empty;
            _state.LastEvidenceCapturePath = string.Empty;

            var powerHealth = RefreshHardwareSnapshot();
            if (!powerHealth.Success) _state.LastStatus = powerHealth.Message;
            else if (_state.RequiresRestart && _state.GpuRouteControlPath == "awcc-mux")
            {
                var targetRoute = GpuManager.DescribeMode(_state.GpuPendingRouteMode);
                _state.LastStatus = "Hardware route change to " + targetRoute + " is pending restart.";
            }
            else if (GpuManager.IsBlockedMuxControlPath(_state.GpuRouteControlPath))
                _state.LastStatus = "Native GPU routing is present but needs a one-time elevated action.";
            else if (!string.IsNullOrWhiteSpace(powerHealth.Warning)) _state.LastStatus = powerHealth.Warning;
            else _state.LastStatus = "Ready.";

            AppStateStore.Save(_state);

            _mainViewModel = new MainViewModel(_state, HandleUiMessage);
            _mainWindow = new MainWindow(_mainViewModel);
            MainWindow = _mainWindow;

            InitializeTrayIcon();
            RefreshMenuChecks();
            RefreshTrayText();

            AppLogger.Write("Application started.");
            AppLogger.Write(
                "Startup snapshot | power=" + _state.PowerMode +
                " | route=" + _state.GpuRouteMode +
                " | panel=" + _state.GpuPanelMode +
                " | pref=" + _state.GpuPreferenceMode +
                " | routePath=" + _state.GpuRouteControlPath +
                " | panelPath=" + _state.GpuPanelControlPath +
                " | prefPath=" + _state.GpuPreferenceControlPath +
                " | helperInstalled=" + _state.HelperInstalled +
                " | restartPending=" + _state.RequiresRestart +
                " | powerHealth=" + _state.PowerPlanHealth);
            
            // --- Command-Line Argument Handling ---
            if (e.Args.Length > 0)
            {
                var command = e.Args[0].ToLowerInvariant();
                if (command == "--apply" && e.Args.Length > 1)
                {
                    var mode = e.Args[1];
                    AppLogger.Write(string.Format("CLI: Applying mode '{0}' and exiting.", mode));
                    ApplyPowerMode(mode, false);
                    ShutdownApp();
                    return;
                }
                if (command == "--run-tuner" && e.Args.Length > 1)
                {
                    var modeToTune = e.Args[1];
                    AppLogger.Write(string.Format("CLI: Running auto-tuner for '{0}'.", modeToTune));
                    RunAutoTunerAndExit(modeToTune);
                    return;
                }
                if (command == "--diagnostics")
                {
                    RunDiagnosticsShutdownAsync();
                    return;
                }
                if (command == "--smoke-test")
                {
                    RunSmokeTestShutdownAsync();
                    return;
                }
            }
            // --- End Command-Line Handling ---

            StartAutomationEngine();

            if (HasArgument(e.Args, "--startup"))
            {
                AppLogger.Write("Startup tray mode initialized.");
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => { _mainWindow.ShowDashboard(); }));
        }

        private void RunAutoTunerAndExit(string mode)
        {
            PowerPlanProfile profileToTest;
            switch (mode.ToLowerInvariant())
            {
                case "tested-ultra":
                    profileToTest = PowerPlanProfile.CreateTestedUltraProfile();
                    break;
                case "tested-cool":
                    profileToTest = PowerPlanProfile.CreateTestedCoolProfile();
                    break;
                default:
                    AppLogger.Write(string.Format("CLI Error: Unknown profile '{0}' for auto-tuner.", mode));
                    ShutdownApp();
                    return;
            }

            // Apply the base profile first to ensure a consistent starting point
            var planGuid = PowerManager.GetGuidForMode(mode);
            if (string.IsNullOrEmpty(planGuid))
            {
                 AppLogger.Write(string.Format("CLI Error: Could not resolve GUID for mode '{0}'.", mode));
                 ShutdownApp();
                 return;
            }
            PowerManager.ApplyProfile(profileToTest, planGuid);


            CpuStressTestService.Start(profileToTest);

            Task.Run(async () =>
            {
                while (CpuStressTestService.IsActive)
                {
                    AppLogger.Write(string.Format("Auto-Tuner running: {0} ({1}%)", CpuStressTestService.CurrentPhase, CpuStressTestService.ProgressPercent));
                    await Task.Delay(5000); // Log progress every 5 seconds
                }

                AppLogger.Write("Auto-Tuner finished. Applying calibrated profile.");
                var calibratedProfile = CpuStressTestService.CalibratedProfile;
                if (calibratedProfile != null)
                {
                    // Persist the tuned profile
                    PowerManager.ApplyProfile(calibratedProfile, planGuid);
                    AppLogger.Write("Successfully applied calibrated profile. Shutting down.");
                }
                else
                {
                    AppLogger.Write("Calibration did not produce a profile. No changes applied. Shutting down.");
                }
                
                ShutdownApp();
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLogger.Write("Application shutting down.");

            try
            {
                SmartCoolingService.Shutdown();
            }
            catch (Exception ex)
            {
                AppLogger.Write("Shutdown cleanup failed (smart cooling): " + ex);
            }

            try
            {
                CpuStressTestService.Stop();
            }
            catch (Exception ex)
            {
                AppLogger.Write("Shutdown cleanup failed (CPU stress test): " + ex);
            }

            try
            {
                if (_automationEngine != null)
                {
                    _automationEngine.Dispose();
                    _automationEngine = null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Shutdown cleanup failed (automation): " + ex);
            }

            try
            {
                if (_mainWindow != null)
                {
                    _mainWindow.PrepareForShutdown();
                    _mainWindow = null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Shutdown cleanup failed (WebView2): " + ex);
            }

            try
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Shutdown cleanup failed (tray icon): " + ex);
            }

            try
            {
                if (_singleInstanceMutex != null)
                {
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Shutdown cleanup failed (mutex dispose): " + ex);
            }

            base.OnExit(e);

            RuntimeCleanupService.CleanupCurrentRuntimeRoot();
        }

        private static bool HasArgument(string[] args, string target)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static OperationResult ExecuteEphemeralOperation(string operation, string mode)
        {
            operation = (operation ?? string.Empty).Trim().ToLowerInvariant();

            switch (operation)
            {
                case "power":
                    return PowerManager.Apply(mode);
                case "route":
                    return GpuManager.ApplyRoute(mode);
                case "native-route":
                    return GpuManager.ApplyNativeRouteOnly(mode);
                case "preference":
                    return GpuManager.ApplyPreference(mode);
                default:
                    return OperationResult.Fail(
                        "Unknown elevated operation: " + operation + ".",
                        controlPath: "ephemeral-task",
                        requestedMode: mode);
            }
        }

        private async void RunSmokeTestShutdownAsync()
        {
            await Task.Delay(100);
            _mainWindow.ShowDashboard();
            await Task.Delay(2200);
            AppLogger.Write("Smoke test startup finished successfully.");
            ShutdownApp();
        }

        private async void RunDiagnosticsShutdownAsync()
        {
            try
            {
                AppLogger.Write("Diagnostics started.");

                // Re-snapshot to ensure latest provisioning + capability detection is in state.json and logs.
                var powerHealth = RefreshHardwareSnapshot();
                AppStateStore.Save(_state);
                AppLogger.Write(
                    "Diagnostics snapshot | power=" + _state.PowerMode +
                    " | route=" + _state.GpuRouteMode +
                    " | panel=" + _state.GpuPanelMode +
                    " | pref=" + _state.GpuPreferenceMode +
                    " | routePath=" + _state.GpuRouteControlPath +
                    " | panelPath=" + _state.GpuPanelControlPath +
                    " | prefPath=" + _state.GpuPreferenceControlPath +
                    " | restartPending=" + _state.RequiresRestart +
                    " | powerHealth=" + powerHealth.Message +
                    " | warning=" + (powerHealth.Warning ?? string.Empty));

                // Diagnostics observes the current configuration. Active power, GPU
                // route, and NVIDIA preference changes belong to explicit user actions.
                _mainWindow.UpdateState(_state);

                // Show/hide path (tray UX) quickly to verify WPF + WebView2 shell.
                _mainWindow.ShowDashboard();
                await Task.Delay(1200);
                _mainWindow.HideWindow();
                await Task.Delay(300);

                AppLogger.Write("Diagnostics completed.");
            }
            catch (Exception ex)
            {
                AppLogger.Write("Diagnostics failure: " + ex);
            }
            finally
            {
                try
                {
                    var logPath = AppLogger.LogFilePath;
                    if (File.Exists(logPath))
                    {
                        var targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gswitcher_diagnostics.log");
                        File.Copy(logPath, targetPath, true);
                    }
                }
                catch
                {
                }
                ShutdownApp();
            }
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = SystemIcons.Shield;
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "GSwitcher";
            _notifyIcon.MouseClick += NotifyIcon_MouseClick;

            var menu = new ContextMenuStrip();

            _openDashboardMenuItem = new ToolStripMenuItem("Open Dashboard", null, delegate { ToggleDashboard(); });
            _powerUltraMenuItem = new ToolStripMenuItem("Power: Ultra", null, delegate { QueuePowerMode("ultra"); });
            _powerDailyMenuItem = new ToolStripMenuItem("Power: Daily", null, delegate { QueuePowerMode("daily"); });
            _powerBatteryMenuItem = new ToolStripMenuItem("Power: Battery", null, delegate { QueuePowerMode("battery"); });
            _gpuRouteNvidiaMenuItem = new ToolStripMenuItem("GPU: NVIDIA", null, delegate { ShowGpuReadOnlyStatus(); });
            _gpuRouteHybridMenuItem = new ToolStripMenuItem("GPU: Intel", null, delegate { ShowGpuReadOnlyStatus(); });
            _gpuAutoMenuItem = new ToolStripMenuItem("Policy: Automatic", null, delegate { ApplyGpuPreferenceMode("auto", true); });
            _gpuNvidiaMenuItem = new ToolStripMenuItem("Policy: RTX Priority", null, delegate { ApplyGpuPreferenceMode("dgpu", true); });
            _gpuIntegratedMenuItem = new ToolStripMenuItem("Policy: Optimus", null, delegate { ApplyGpuPreferenceMode("hybrid", true); });
            _pinWindowMenuItem = new ToolStripMenuItem("Pin Window", null, delegate { TogglePinned(); });
            _installHelperMenuItem = new ToolStripMenuItem("Elevate Next Route", null, delegate { RelaunchElevated(); });

            menu.Items.Add(_openDashboardMenuItem);
            menu.Items.Add(_pinWindowMenuItem);
            menu.Items.Add(_installHelperMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_powerUltraMenuItem);
            menu.Items.Add(_powerDailyMenuItem);
            menu.Items.Add(_powerBatteryMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_gpuRouteNvidiaMenuItem);
            menu.Items.Add(_gpuRouteHybridMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_gpuAutoMenuItem);
            menu.Items.Add(_gpuNvidiaMenuItem);
            menu.Items.Add(_gpuIntegratedMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ShutdownApp(); });

            _notifyIcon.ContextMenuStrip = menu;
        }

        private void NotifyIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleDashboard();
            }
        }

        private void ToggleDashboard()
        {
            if (_mainWindow.IsVisible)
            {
                _mainWindow.HideWindow();
            }
            else
            {
                _mainWindow.ShowDashboard();
            }
        }

        private void ShutdownApp()
        {
            try
            {
                AppStateStore.Save(_state);
            }
            catch (Exception ex)
            {
                AppLogger.Write("State save failed during shutdown: " + ex);
            }

            if (_mainWindow != null)
            {
                _mainWindow.PrepareForShutdown();
                _mainWindow = null;
            }

            Shutdown();
        }

        private void RunBackendCommand(string label, Action action)
        {
            if (Interlocked.CompareExchange(ref _backendCommandRunning, 1, 0) != 0)
            {
                SetTransientStatus("Another hardware action is still running. Wait for it to finish before sending a new command.", true);
                return;
            }

            SetBusyStatus(label, true, label + " started.", false);

            Task.Run(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    AppLogger.Write("Backend command failed (" + label + "): " + ex);
                    SetTransientStatus(label + " failed. Check the volatile app log for details.", true);
                }
                finally
                {
                    Interlocked.Exchange(ref _backendCommandRunning, 0);
                    SetBusyStatus(null, false, null, false);
                }
            });
        }

        private void QueuePowerMode(string requestedMode)
        {
            var mode = PowerManager.NormalizeMode(requestedMode);
            if (mode == "other")
            {
                SetTransientStatus("Unknown power mode request.", true);
                return;
            }

            string blockMessage;
            if (_automationEngine != null && _automationEngine.IsPowerModeBlocked(mode, out blockMessage))
            {
                SetTransientStatus(blockMessage, true);
                return;
            }

            if (Interlocked.CompareExchange(ref _backendCommandRunning, 1, 0) != 0)
            {
                SetTransientStatus("Another hardware action is still running. Wait for it to finish before sending a new command.", true);
                return;
            }

            var label = "Power " + PowerManager.DescribeMode(mode);
            if (_state != null)
            {
                _state.IsBusy = true;
                _state.BusyLabel = label;
                _state.LastStatus = label + " requested.";
                _state.LastUpdatedUtc = DateTime.UtcNow;
                AppStateStore.Save(_state);
            }

            if (_mainWindow != null)
            {
                _mainWindow.UpdateState(_state);
                _mainWindow.PushStatus(label + " requested.", false);
            }

            Task.Run(() =>
            {
                try
                {
                    ApplyPowerMode(mode, false);
                }
                catch (Exception ex)
                {
                    AppLogger.Write("Backend command failed (" + label + "): " + ex);
                    SetTransientStatus(label + " failed. Check the volatile app log for details.", true);
                }
                finally
                {
                    Interlocked.Exchange(ref _backendCommandRunning, 0);
                    SetBusyStatus(null, false, null, false);
                }
            });
        }

        private void ScheduleDeferredHardwareRefresh(string reason)
        {
            if (Interlocked.CompareExchange(ref _deferredRefreshRunning, 1, 0) != 0)
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500);
                    var health = RefreshHardwareSnapshot();
                    if (!health.Success)
                    {
                        _state.LastStatus = reason + " background verification: " + health.Message;
                    }
                    _state.LastUpdatedUtc = DateTime.UtcNow;
                    AppStateStore.Save(_state);
                    AppLogger.Write("Deferred hardware refresh finished (" + reason + ").");

                    RefreshMenuChecks();
                    RefreshTrayText();
                    if (_mainWindow != null)
                    {
                        _mainWindow.UpdateState(_state);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Write("Deferred hardware refresh failed (" + reason + "): " + ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _deferredRefreshRunning, 0);
                }
            });
        }

        private void SetTransientStatus(string message, bool isError)
        {
            if (_state != null)
            {
                _state.LastStatus = message;
                _state.LastUpdatedUtc = DateTime.UtcNow;
                AppStateStore.Save(_state);
            }

            if (_mainWindow != null)
            {
                _mainWindow.UpdateState(_state);
                _mainWindow.PushStatus(message, isError);
            }
        }

        private void SetBusyStatus(string label, bool isBusy, string message, bool isError)
        {
            if (_state != null)
            {
                _state.IsBusy = isBusy;
                _state.BusyLabel = isBusy ? (label ?? "Working") : string.Empty;

                if (!string.IsNullOrWhiteSpace(message))
                {
                    _state.LastStatus = message;
                    _state.LastUpdatedUtc = DateTime.UtcNow;
                }

                AppStateStore.Save(_state);
            }

            if (_mainWindow != null)
            {
                _mainWindow.UpdateState(_state);

                if (!string.IsNullOrWhiteSpace(message))
                {
                    _mainWindow.PushStatus(message, isError);
                }
            }
        }

        private void HandleUiMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            AppLogger.Write("UI message: " + message);

            if (message.StartsWith("power:", StringComparison.OrdinalIgnoreCase))
            {
                var mode = message.Substring("power:".Length);
                QueuePowerMode(mode);
                return;
            }

            if (string.Equals(message, "processor:open", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(message, "processor:refresh", StringComparison.OrdinalIgnoreCase))
            {
                PushProcessorSettings();
                return;
            }
            if (string.Equals(message, "processor:snapshot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(message, "processor:export", StringComparison.OrdinalIgnoreCase))
            {
                SmartCoolingService.Stop();
                try
                {
                    var path = ProcessorPowerSettingsService.SnapshotActive(message.EndsWith("export", StringComparison.OrdinalIgnoreCase));
                    _mainWindow.PushStatus("Processor snapshot saved: " + path, false);
                    PushProcessorSettings();
                }
                catch (Exception ex) { _mainWindow.PushStatus("Snapshot failed: " + ex.Message, true); }
                return;
            }
            if (string.Equals(message, "processor:restore", StringComparison.OrdinalIgnoreCase))
            {
                SmartCoolingService.Stop();
                try { _mainWindow.PushStatus(ProcessorPowerSettingsService.RestoreLatest(), false); PushProcessorSettings(); }
                catch (Exception ex) { _mainWindow.PushStatus("Restore failed: " + ex.Message, true); }
                return;
            }
            if (message.StartsWith("processor:preset:", StringComparison.OrdinalIgnoreCase))
            {
                SmartCoolingService.Stop();
                try { _mainWindow.PushStatus(ProcessorPowerSettingsService.ApplyPreset(message.Substring("processor:preset:".Length)), false); PushProcessorSettings(); }
                catch (Exception ex) { _mainWindow.PushStatus("Preset failed: " + ex.Message, true); }
                return;
            }
            if (message.StartsWith("processor:set:", StringComparison.OrdinalIgnoreCase))
            {
                SmartCoolingService.Stop();
                var parts = message.Substring("processor:set:".Length).Split(':');
                try
                {
                    if (parts.Length != 3) throw new ArgumentException("Invalid processor setting request.");
                    _mainWindow.PushStatus(ProcessorPowerSettingsService.Set(parts[0], parts[1].ToLowerInvariant(), parts[2]), false);
                    PushProcessorSettings();
                }
                catch (Exception ex) { _mainWindow.PushStatus("Setting rejected: " + ex.Message, true); }
                return;
            }

            if (message.StartsWith("gpu:", StringComparison.OrdinalIgnoreCase))
            {
                ShowGpuReadOnlyStatus();
                return;
            }

            if (message.StartsWith("gpu-route:", StringComparison.OrdinalIgnoreCase))
            {
                ShowGpuReadOnlyStatus();
                return;
            }

            if (message.StartsWith("gpu-pref:", StringComparison.OrdinalIgnoreCase))
            {
                ShowGpuReadOnlyStatus();
                return;
            }

            if (message.StartsWith("theme:", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTheme(message.Substring("theme:".Length));
                return;
            }

            if (message.StartsWith("signature:", StringComparison.OrdinalIgnoreCase))
            {
                _settings = SettingsManager.UpdateSignaturePlacement(message.Substring("signature:".Length));
                _mainWindow.UpdateState(_state);
                return;
            }

            if (message.StartsWith("setting:", StringComparison.OrdinalIgnoreCase))
            {
                ApplySetting(message.Substring("setting:".Length));
                return;
            }

            if (string.Equals(message, "app:elevate", StringComparison.OrdinalIgnoreCase))
            {
                ExplainElevationFlow();
                return;
            }

            if (string.Equals(message, "app:open-nvidia-panel", StringComparison.OrdinalIgnoreCase))
            {
                RunBackendCommand("NVIDIA panel request", OpenNvidiaPanel);
                return;
            }

            if (string.Equals(message, "app:elevate-action", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(message, "app:install-helper", StringComparison.OrdinalIgnoreCase))
            {
                ExplainElevationFlow();
                return;
            }

            if (string.Equals(message, "window:pin-toggle", StringComparison.OrdinalIgnoreCase))
            {
                TogglePinned();
                return;
            }

            if (string.Equals(message, "window:drag-start", StringComparison.OrdinalIgnoreCase))
            {
                _mainWindow.BeginWindowDrag();
                return;
            }

            if (message.StartsWith("display:brightness:", StringComparison.OrdinalIgnoreCase))
            {
                var command = message.Substring("display:brightness:".Length).Trim();
                if (string.Equals(command, "max", StringComparison.OrdinalIgnoreCase))
                {
                    RunBackendCommand("Brightness max", delegate { SetBrightness(100, false); });
                }
                else
                {
                    int delta;
                    if (int.TryParse(command, out delta))
                    {
                        RunBackendCommand("Brightness adjustment", delegate { AdjustBrightness(delta, false); });
                    }
                }

                return;
            }

            if (string.Equals(message, "display:heal", StringComparison.OrdinalIgnoreCase))
            {
                RunBackendCommand("Display recovery", delegate { HealDisplayState(false); });
                return;
            }

            if (string.Equals(message, "window:hide", StringComparison.OrdinalIgnoreCase))
            {
                _mainWindow.HideWindow();
                return;
            }

            if (message.StartsWith("thermal:boost:", StringComparison.OrdinalIgnoreCase))
            {
                var valueStr = message.Substring("thermal:boost:".Length).Trim();
                int boostValue;
                if (int.TryParse(valueStr, out boostValue) && boostValue >= 0 && boostValue <= 6)
                {
                    RunBackendCommand("CPU Boost Mode", delegate
                    {
                        var result = ThermalDiagnosticsService.SetBoostMode(boostValue);
                        SetTransientStatus(result.Message, !result.Success);
                        LogOperation("thermal-boost", result);
                        PushThermalDiagnostics();
                    });
                }
                else
                {
                    SetTransientStatus("Invalid boost mode value.", true);
                }

                return;
            }

            if (message.StartsWith("thermal:maxstate:", StringComparison.OrdinalIgnoreCase))
            {
                var valueStr = message.Substring("thermal:maxstate:".Length).Trim();
                int stateValue;
                if (int.TryParse(valueStr, out stateValue) && stateValue >= 5 && stateValue <= 100)
                {
                    RunBackendCommand("Max Processor State", delegate
                    {
                        var result = ThermalDiagnosticsService.SetMaxProcessorState(stateValue);
                        SetTransientStatus(result.Message, !result.Success);
                        LogOperation("thermal-maxstate", result);
                        PushThermalDiagnostics();
                    });
                }
                else
                {
                    SetTransientStatus("Invalid max processor state value.", true);
                }

                return;
            }

            if (string.Equals(message, "thermal:diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                PushThermalDiagnostics();
                return;
            }

            if (message.StartsWith("thermal:smart:start:", StringComparison.OrdinalIgnoreCase))
            {
                var val = message.Substring("thermal:smart:start:".Length).Trim();
                SmartCoolingService.Start(val);
                return;
            }

            if (string.Equals(message, "thermal:smart:stop", StringComparison.OrdinalIgnoreCase))
            {
                SmartCoolingService.Stop();
                CpuStressTestService.Stop();
                SetTransientStatus("Automatic optimization stopped.", false);
                return;
            }

            if (message.StartsWith("thermal:smart:reset:", StringComparison.OrdinalIgnoreCase))
            {
                var val = message.Substring("thermal:smart:reset:".Length).Trim();
                SmartCoolingService.Reset(val);
                return;
            }

            if (message.StartsWith("thermal:stresstest:start:", StringComparison.OrdinalIgnoreCase))
            {
                var modeToTune = message.Substring("thermal:stresstest:start:".Length).Trim();
                Task.Run(() =>
                {
                    // Similar to the CLI handler, create the profile to test
                    PowerPlanProfile profileToTest;
                    switch (modeToTune.ToLowerInvariant())
                    {
                        case "tested-ultra":
                            profileToTest = PowerPlanProfile.CreateTestedUltraProfile();
                            break;
                        case "tested-cool":
                            profileToTest = PowerPlanProfile.CreateTestedCoolProfile();
                            break;
                        default:
                            AppLogger.Write(string.Format("UI Error: Unknown profile '{0}' for auto-tuner.", modeToTune));
                            SetTransientStatus("Invalid profile selected for auto-tuner.", true);
                            return;
                    }

                    var planGuid = PowerManager.GetGuidForMode(modeToTune);
                    if (string.IsNullOrEmpty(planGuid))
                    {
                         AppLogger.Write(string.Format("UI Error: Could not resolve GUID for mode '{0}'.", modeToTune));
                         SetTransientStatus("Could not resolve power plan GUID.", true);
                         return;
                    }
                    
                    // Stop any other background services that might interfere
                    SmartCoolingService.Stop();
                    // Start the test
                    CpuStressTestService.Start(profileToTest);

                    // Wait for the test to complete in a non-blocking way
                    while (CpuStressTestService.IsActive)
                    {
                        // The service itself pushes UI updates, so we just wait.
                        Task.Delay(1000).Wait();
                    }

                    AppLogger.Write("UI-initiated Auto-Tuner finished. Applying calibrated profile.");
                    var calibratedProfile = CpuStressTestService.CalibratedProfile;
                    if (calibratedProfile != null)
                    {
                        var result = PowerManager.ApplyProfile(calibratedProfile, planGuid);
                        SetTransientStatus(result.Message, !result.Success);
                        LogOperation("auto-tune-apply", result);
                        PushThermalDiagnostics(); // Push final state
                    }
                    else
                    {
                        AppLogger.Write("Calibration did not produce a profile. No changes applied.");
                        SetTransientStatus("Calibration failed to produce a new profile.", true);
                    }
                });
                return;
            }

            if (string.Equals(message, "thermal:stresstest:stop", StringComparison.OrdinalIgnoreCase))
            {
                Task.Run(() => CpuStressTestService.Stop());
                return;
            }

            if (message.StartsWith("thermal:fix:", StringComparison.OrdinalIgnoreCase))
            {
                var payload = message.Substring("thermal:fix:".Length).Trim();
                var split = payload.Split(':');
                var action = split[0];
                int pid = 0;
                if (split.Length > 1)
                {
                    int.TryParse(split[1], out pid);
                }

                RunBackendCommand("Thermal Root Cause Fix", delegate
                {
                    var result = ThermalDiagnosticsService.ExecuteThermalFix(action, pid);
                    SetTransientStatus(result.Message, !result.Success);
                    PushThermalDiagnostics();
                });
                return;
            }

            if (message.StartsWith("thermal:profile:set:", StringComparison.OrdinalIgnoreCase))
            {
                var valStr = message.Substring("thermal:profile:set:".Length).Trim();
                int profileId;
                if (int.TryParse(valStr, out profileId) || (valStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(valStr.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out profileId)))
                {
                    RunBackendCommand("Dell Thermal Profile", delegate
                    {
                        var result = ThermalDiagnosticsService.SetDellThermalProfile(profileId);
                        SetTransientStatus(result.Message, !result.Success);
                        LogOperation("thermal-profile", result);
                        PushThermalDiagnostics();
                    });
                }
                return;
            }
        }

        private void PushProcessorSettings()
        {
            try { _mainWindow.PushProcessorSettings(ProcessorPowerSettingsService.ReadActive()); }
            catch (Exception ex)
            {
                var message = "Processor settings unavailable: " + ex.Message;
                _mainWindow.PushStatus(message, true);
                _mainWindow.PushProcessorSettings(new { schemeName = "Unavailable", settings = new object[0], error = message });
            }
        }

        public void TriggerThermalDiagnosticsUpdate()
        {
            PushThermalDiagnostics();
        }

        private void PushThermalDiagnostics()
        {
            Interlocked.Exchange(ref _thermalDiagnosticsPending, 1);
            if (Interlocked.CompareExchange(ref _thermalDiagnosticsRunning, 1, 0) != 0)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    while (Interlocked.Exchange(ref _thermalDiagnosticsPending, 0) != 0)
                    {
                        var snapshot = ThermalDiagnosticsService.GetCpuSnapshot();
                        var processes = ThermalDiagnosticsService.GetTopProcesses(8);

                        snapshot["smartCooling"] = SmartCoolingService.GetServiceState();
                        snapshot["cpuStressTest"] = CpuStressTestService.GetServiceState();
                        snapshot["rca"] = ThermalDiagnosticsService.PerformRootCauseAnalysis();

                        if (_mainWindow != null)
                        {
                            _mainWindow.PushThermalData(snapshot, processes);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Write("PushThermalDiagnostics background query failed: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _thermalDiagnosticsRunning, 0);
                    if (Interlocked.CompareExchange(ref _thermalDiagnosticsPending, 0, 0) != 0)
                    {
                        PushThermalDiagnostics();
                    }
                }
            });
        }

        private void ApplyTheme(string theme)
        {
            theme = (theme ?? string.Empty).Trim().ToLowerInvariant();
            if (theme != "light")
            {
                theme = "dark";
            }

            _state.Theme = theme;
            _settings = SettingsManager.UpdateTheme(theme);
            _state.LastUpdatedUtc = DateTime.UtcNow;
            AppStateStore.Save(_state);
            _mainWindow.UpdateState(_state);
        }

        private void ApplySetting(string payload)
        {
            payload = (payload ?? string.Empty).Trim();
            var split = payload.Split(':');
            if (split.Length != 2)
            {
                return;
            }

            var key = split[0].Trim();
            bool enabled;
            if (!bool.TryParse(split[1].Trim(), out enabled))
            {
                return;
            }

            try
            {
                _settings = SettingsManager.Update(key, enabled);
                if (string.Equals(key, "smartCoolingAutomaticEnabled", StringComparison.OrdinalIgnoreCase) && !enabled)
                {
                    SmartCoolingService.Stop();
                    CpuStressTestService.Stop();
                }
                _state.LastStatus = key + " " + (enabled ? "enabled." : "disabled.");
                _state.LastUpdatedUtc = DateTime.UtcNow;
                AppStateStore.Save(_state);
                RefreshMenuChecks();
                RefreshTrayText();
                if (_automationEngine != null)
                {
                    _automationEngine.RefreshSettings();
                }

                _mainWindow.UpdateState(_state);
                _mainWindow.PushStatus(_state.LastStatus, false);
            }
            catch (Exception ex)
            {
                _settings = SettingsManager.Load();
                _state.LastStatus = "Setting update failed: " + ex.Message;
                _state.LastUpdatedUtc = DateTime.UtcNow;
                AppLogger.Write("Setting update failed: " + ex);
                _mainWindow.UpdateState(_state);
                _mainWindow.PushStatus(_state.LastStatus, true);
            }
        }

        private void StartAutomationEngine()
        {
            if (_automationEngine != null || _mainWindow == null)
            {
                return;
            }

            _automationEngine = new AutomationEngine(
                _mainWindow,
                delegate { return _state; },
                ApplyAutomationPowerMode,
                SetTransientStatus,
                PushEmergencyEcoState);
            _automationEngine.Start();
        }

        private bool ApplyAutomationPowerMode(string mode, string reason, bool allowElevation)
        {
            mode = PowerManager.NormalizeMode(mode);
            if (mode == "other")
            {
                return false;
            }

            string blockMessage;
            if (_automationEngine != null && _automationEngine.IsPowerModeBlocked(mode, out blockMessage))
            {
                SetTransientStatus(blockMessage, true);
                return false;
            }

            if (Interlocked.CompareExchange(ref _backendCommandRunning, 1, 0) != 0)
            {
                AppLogger.Write("Automation power request skipped because another backend command is running: " + reason);
                return false;
            }

            try
            {
                var result = PowerManager.ApplyFast(mode);
                if (!result.Success && allowElevation && !EphemeralElevationService.IsCurrentProcessElevated())
                {
                    result = EphemeralElevationService.RunElevated("power", mode);
                }

                _state.PowerMode = PowerManager.DetectActiveMode();
                _state.PowerPlanHealthState = !result.Success ? "error" : (result.Verified ? "healthy" : "checking");
                _state.PowerPlanHealth = result.Message;
                _state.LastStatus = reason + ": " + result.Message;
                _state.LastUpdatedUtc = DateTime.UtcNow;
                AppStateStore.Save(_state);
                LogOperation("automation-" + mode, result);

                if (_automationEngine != null)
                {
                    _automationEngine.NotifyPowerModeChanged(_state.PowerMode);
                }

                RefreshMenuChecks();
                RefreshTrayText();

                if (_mainWindow != null)
                {
                    _mainWindow.UpdateState(_state);
                    _mainWindow.PushStatus(_state.LastStatus, !result.Success);
                }

                ScheduleDeferredHardwareRefresh("automation-" + mode);
                return result.Success;
            }
            catch (Exception ex)
            {
                AppLogger.Write("Automation power request failed (" + reason + "): " + ex);
                SetTransientStatus(reason + " failed. Check the volatile app log for details.", true);
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _backendCommandRunning, 0);
            }
        }

        private void PushEmergencyEcoState()
        {
            if (_mainWindow != null)
            {
                _mainWindow.PushEmergencyEco();
            }
        }

        private void ShowGpuReadOnlyStatus()
        {
            RunBackendCommand("GPU status check", delegate
            {
                RefreshHardwareSnapshot();

                var route = string.Equals(_state.GpuRouteMode, "dgpu", StringComparison.OrdinalIgnoreCase)
                    ? "NVIDIA RTX is the detected display owner."
                    : (string.Equals(_state.GpuRouteMode, "hybrid", StringComparison.OrdinalIgnoreCase)
                        ? "Intel UHD is the detected display owner."
                        : "GPU display ownership is not verified yet.");

                _state.LastStatus = route + " GPU controls are read-only in this dashboard.";
                _state.LastUpdatedUtc = DateTime.UtcNow;
                AppStateStore.Save(_state);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshMenuChecks();
                    RefreshTrayText();
                    _mainWindow.UpdateState(_state);
                    _mainWindow.PushStatus(_state.LastStatus, false);
                }));
            });
        }

        private void TogglePinned()
        {
            _state.IsPinned = !_state.IsPinned;
            _state.LastStatus = _state.IsPinned
                ? "Window pinning is enabled. The dashboard will stay open when focus changes."
                : "Window pinning is disabled. The dashboard will auto-hide again when focus changes.";
            _state.LastUpdatedUtc = DateTime.UtcNow;
            AppStateStore.Save(_state);

            RefreshMenuChecks();
            RefreshTrayText();
            _mainWindow.ApplyPinnedState(_state.IsPinned);
            _mainWindow.UpdateState(_state);
            _mainWindow.PushStatus(_state.LastStatus, false);
        }

        private void ShowTrayBalloon(string title, string text, int timeout)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowTrayBalloon(title, text, timeout)));
                return;
            }

            if (_notifyIcon == null)
            {
                return;
            }

            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.ShowBalloonTip(timeout);
        }

        private void OpenNvidiaPanel()
        {
            var result = NvidiaPanelController.OpenControlPanel();
            _state.LastStatus = result.Message;
            _state.LastUpdatedUtc = DateTime.UtcNow;
            AppStateStore.Save(_state);
            LogOperation("nvidia-panel-open", result);

            RefreshMenuChecks();
            RefreshTrayText();
            _mainWindow.UpdateState(_state);
            _mainWindow.PushStatus(result.Message, !result.Success);

            ShowTrayBalloon(result.Success ? "NVIDIA Panel" : "Panel Error", result.Message, 2500);
        }

        private void ApplyPowerMode(string mode, bool showBalloon)
        {
            mode = PowerManager.NormalizeMode(mode);
            var result = PowerManager.ApplyFast(mode);
            if (!result.Success && !EphemeralElevationService.IsCurrentProcessElevated())
            {
                result = EphemeralElevationService.RunElevated("power", mode);
            }

            _state.PowerMode = PowerManager.DetectActiveMode();
            _state.PowerPlanHealthState = !result.Success ? "error" : (result.Verified ? "healthy" : "checking");
            _state.PowerPlanHealth = result.Message;
            _state.LastStatus = result.Message;
            _state.LastUpdatedUtc = DateTime.UtcNow;
            AppStateStore.Save(_state);
            LogOperation("power", result);

            if (_automationEngine != null)
            {
                _automationEngine.NotifyPowerModeChanged(_state.PowerMode);
            }

            RefreshMenuChecks();
            RefreshTrayText();
            _mainWindow.UpdateState(_state);
            _mainWindow.PushStatus(result.Message, !result.Success);

            if (showBalloon && _notifyIcon != null)
            {
                _notifyIcon.BalloonTipTitle = result.Success ? "Power Verified" : "Power Error";
                _notifyIcon.BalloonTipText = result.Message;
                _notifyIcon.ShowBalloonTip(2500);
            }

            ScheduleDeferredHardwareRefresh("power-" + mode);
        }

        private void ApplyGpuRouteMode(string mode, bool showBalloon)
        {
            mode = GpuManager.NormalizeRouteMode(mode);
            if (_state.RequiresRestart && _state.GpuPendingRouteMode != "unknown")
            {
                if (string.Equals(_state.GpuPendingRouteMode, mode, StringComparison.OrdinalIgnoreCase))
                {
                    var queuedMessage = "Hardware route change to " + GpuManager.DescribeMode(mode) +
                        " is already queued. Restart Windows to finish it.";
                    _state.LastStatus = queuedMessage;
                    _state.LastUpdatedUtc = DateTime.UtcNow;
                    AppStateStore.Save(_state);
                    RefreshMenuChecks();
                    RefreshTrayText();
                    _mainWindow.UpdateState(_state);
                    _mainWindow.PushStatus(queuedMessage, false);
                    return;
                }

                var pendingMessage = "A hardware route change to " +
                    GpuManager.DescribeMode(_state.GpuPendingRouteMode) +
                    " is already pending restart. Restart Windows before requesting another route.";
                _state.LastStatus = pendingMessage;
                _state.LastUpdatedUtc = DateTime.UtcNow;
                AppStateStore.Save(_state);
                RefreshMenuChecks();
                RefreshTrayText();
                _mainWindow.UpdateState(_state);
                _mainWindow.PushStatus(pendingMessage, true);
                return;
            }

            var displayContext = DisplayStateService.CaptureTransitionContext();
            var needsElevation = GpuManager.IsBlockedMuxControlPath(_state.GpuRouteControlPath) &&
                !EphemeralElevationService.IsCurrentProcessElevated();
            var result = needsElevation
                ? EphemeralElevationService.RunElevated("route", mode)
                : GpuManager.ApplyRoute(mode);

            var preferenceSyncNote = TrySynchronizePreferenceWithRoute(mode, result);
            RefreshHardwareSnapshot();
            var displayRecoveryNote = DisplayStateService.TryRecoverAfterRouteChange(mode, result, displayContext);
            if (!string.IsNullOrWhiteSpace(displayRecoveryNote))
            {
                RefreshHardwareSnapshot();
            }

            if (!string.IsNullOrWhiteSpace(preferenceSyncNote))
            {
                result.Message = result.Message + " " + preferenceSyncNote;
            }

            if (!string.IsNullOrWhiteSpace(displayRecoveryNote))
            {
                result.Message = result.Message + " " + displayRecoveryNote;
            }

            if (!result.Success && string.Equals(result.ControlPath, "awcc-mux-rejected", StringComparison.OrdinalIgnoreCase))
            {
                _state.GpuRouteControlPath = "awcc-mux-rejected";
                _state.AvailableGpuRouteModes = new string[0];
                _state.GpuRouteEvidence =
                    "An elevated Dell WMI probe still rejected the native MUX call on this BIOS. " +
                    "GSwitcher will not keep retrying this route path in the current run.";
                result.Message =
                    result.Message +
                    " GSwitcher disabled route buttons for this run because repeating the same elevated AWCC call is expected to fail again.";
            }
            else if (!result.Success && !string.IsNullOrWhiteSpace(result.Warning))
            {
                result.Message = result.Message + " " + result.Warning;
            }

            if (result.Success && result.RequiresRestart)
            {
                _state.RequiresRestart = true;
                _state.GpuPendingRouteMode = GpuManager.NormalizeRouteMode(result.RequestedMode);
            }
            else if (result.Success)
            {
                _state.RequiresRestart = false;
                _state.GpuPendingRouteMode = "unknown";
            }

            _state.LastStatus = result.Message;
            _state.LastUpdatedUtc = DateTime.UtcNow;
            AppStateStore.Save(_state);
            LogOperation("gpu-route", result);

            RefreshMenuChecks();
            RefreshTrayText();
            _mainWindow.UpdateState(_state);
            _mainWindow.PushStatus(result.Message, !result.Success);

            if (showBalloon && _notifyIcon != null)
            {
                _notifyIcon.BalloonTipTitle = result.Success
                    ? (result.RequiresRestart ? "Route Restart Required" : "Route Verified")
                    : "GPU Route Error";
                _notifyIcon.BalloonTipText = result.Message;
                _notifyIcon.ShowBalloonTip(3000);
            }

        }

        private void RelaunchElevated()
        {
            var result = TryRelaunchElevated(null);
            _state.LastStatus = result.Message;
            _state.LastUpdatedUtc = DateTime.UtcNow;
            AppStateStore.Save(_state);
            LogOperation("app-elevate", result);

            RefreshMenuChecks();
            RefreshTrayText();
            _mainWindow.UpdateState(_state);
            _mainWindow.PushStatus(result.Message, !result.Success);

            if (_notifyIcon != null)
            {
                _notifyIcon.BalloonTipTitle = result.Success ? "Relaunching" : "Elevation Needed";
                _notifyIcon.BalloonTipText = result.Message;
                _notifyIcon.ShowBalloonTip(3000);
            }

            if (result.Success)
            {
                Shutdown();
            }
        }

        private void ExplainElevationFlow()
        {
            const string message = "Choose Hybrid or Dedicated in the Route panel. GSwitcher will request UAC only for that hardware action, then remove the temporary task.";
            _state.LastStatus = message;
            _state.LastUpdatedUtc = DateTime.UtcNow;
            AppStateStore.Save(_state);
            AppLogger.Write("Elevation guidance shown.");

            RefreshMenuChecks();
            RefreshTrayText();
            _mainWindow.UpdateState(_state);
            _mainWindow.PushStatus(message, false);
        }

        private void ApplyGpuPreferenceMode(string mode, bool showBalloon)
        {
            mode = GpuManager.NormalizeMode(mode);
            var result = GpuManager.ApplyPreference(mode);

            RefreshHardwareSnapshot();
            _state.LastStatus = result.Message;
            _state.LastUpdatedUtc = DateTime.UtcNow;
            AppStateStore.Save(_state);
            LogOperation("gpu-pref", result);

            RefreshMenuChecks();
            RefreshTrayText();
            _mainWindow.UpdateState(_state);
            _mainWindow.PushStatus(result.Message, !result.Success);

            if (showBalloon && _notifyIcon != null)
            {
                _notifyIcon.BalloonTipTitle = result.Success ? "Preference Verified" : "Preference Error";
                _notifyIcon.BalloonTipText = result.Message;
                _notifyIcon.ShowBalloonTip(3000);
            }

        }

        private void AdjustBrightness(int delta, bool showBalloon)
        {
            var result = DisplayStateService.AdjustBrightness(delta);
            RefreshHardwareSnapshot();
            _state.LastStatus = result.Message;
            _state.LastUpdatedUtc = DateTime.UtcNow;
            AppStateStore.Save(_state);
            LogOperation("brightness-adjust", result);

            RefreshMenuChecks();
            RefreshTrayText();
            _mainWindow.UpdateState(_state);
            _mainWindow.PushStatus(result.Message, !result.Success);

            if (showBalloon && _notifyIcon != null)
            {
                _notifyIcon.BalloonTipTitle = result.Success ? "Brightness Restored" : "Brightness Error";
                _notifyIcon.BalloonTipText = result.Message;
                _notifyIcon.ShowBalloonTip(2500);
            }

        }

        private void SetBrightness(int level, bool showBalloon)
        {
            var result = DisplayStateService.SetBrightness(level);
            RefreshHardwareSnapshot();
            _state.LastStatus = result.Message;
            _state.LastUpdatedUtc = DateTime.UtcNow;
            AppStateStore.Save(_state);
            LogOperation("brightness-set", result);

            RefreshMenuChecks();
            RefreshTrayText();
            _mainWindow.UpdateState(_state);
            _mainWindow.PushStatus(result.Message, !result.Success);

            if (showBalloon && _notifyIcon != null)
            {
                _notifyIcon.BalloonTipTitle = result.Success ? "Brightness Restored" : "Brightness Error";
                _notifyIcon.BalloonTipText = result.Message;
                _notifyIcon.ShowBalloonTip(2500);
            }

        }

        private void HealDisplayState(bool showBalloon)
        {
            var context = DisplayStateService.CaptureTransitionContext();
            var recoveryResult = OperationResult.Ok(
                "Display recovery completed.",
                controlPath: "display-recovery",
                requestedMode: _state.GpuRouteMode,
                verifiedMode: _state.GpuRouteMode);
            var recoveryNote = DisplayStateService.TryRecoverAfterRouteChange(_state.GpuRouteMode, recoveryResult, context);

            RefreshHardwareSnapshot();

            var message = !string.IsNullOrWhiteSpace(recoveryNote)
                ? recoveryNote
                : _state.DisplayWarning;

            _state.LastStatus = message;
            _state.LastUpdatedUtc = DateTime.UtcNow;
            AppStateStore.Save(_state);
            LogOperation("display-heal", recoveryResult);

            RefreshMenuChecks();
            RefreshTrayText();
            _mainWindow.UpdateState(_state);
            _mainWindow.PushStatus(message, false);

            if (showBalloon && _notifyIcon != null)
            {
                _notifyIcon.BalloonTipTitle = "Display Recovery";
                _notifyIcon.BalloonTipText = message;
                _notifyIcon.ShowBalloonTip(2500);
            }

        }

        private string TrySynchronizePreferenceWithRoute(string routeMode, OperationResult routeResult)
        {
            if (routeResult == null || !routeResult.Success || !SupportsGpuPreferenceMode(routeMode))
            {
                return null;
            }

            var preferenceResult = GpuManager.ApplyPreference(routeMode);
            LogOperation("gpu-pref-sync", preferenceResult);

            if (preferenceResult.Success)
            {
                return "NVIDIA policy was also aligned to " + GpuManager.DescribePreferenceMode(routeMode) + ".";
            }

            return "NVIDIA policy did not fully align with the requested route.";
        }

        private void RefreshMenuChecks()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshMenuChecks));
                return;
            }

            if (_powerUltraMenuItem != null)
            {
                _powerUltraMenuItem.Checked = _state.PowerMode == "tested-ultra";
                _powerDailyMenuItem.Checked = _state.PowerMode == "daily-cool";
                _powerBatteryMenuItem.Checked = _state.PowerMode == "tested-eco";

                _gpuRouteNvidiaMenuItem.Visible = true;
                _gpuRouteHybridMenuItem.Visible = true;
                _gpuRouteNvidiaMenuItem.Enabled = false;
                _gpuRouteHybridMenuItem.Enabled = false;
                _gpuAutoMenuItem.Visible = true;
                _gpuNvidiaMenuItem.Visible = true;
                _gpuIntegratedMenuItem.Visible = true;
                var preferenceAvailable = _state.GpuPreferenceControlPath == "nvidia-drs";
                _gpuAutoMenuItem.Enabled = preferenceAvailable;
                _gpuNvidiaMenuItem.Enabled = preferenceAvailable;
                _gpuIntegratedMenuItem.Enabled = preferenceAvailable;
                _pinWindowMenuItem.Checked = _state.IsPinned;
                _installHelperMenuItem.Checked = EphemeralElevationService.IsCurrentProcessElevated();
                _installHelperMenuItem.Visible = false;
                _installHelperMenuItem.Text = EphemeralElevationService.IsCurrentProcessElevated()
                    ? "Elevated Session"
                    : "Elevate Action";
                _installHelperMenuItem.Enabled = !EphemeralElevationService.IsCurrentProcessElevated();

                _gpuRouteNvidiaMenuItem.Text = _state.GpuRouteMode == "dgpu"
                    ? "GPU: NVIDIA RTX (active)"
                    : "GPU: NVIDIA RTX (detected)";
                _gpuRouteHybridMenuItem.Text = _state.GpuRouteMode == "hybrid"
                    ? "GPU: Intel UHD (active)"
                    : "GPU: Intel UHD (detected)";

                _gpuRouteNvidiaMenuItem.Checked = _state.GpuRouteMode == "dgpu";
                _gpuRouteHybridMenuItem.Checked = _state.GpuRouteMode == "hybrid";
                _gpuAutoMenuItem.Checked = _state.GpuPreferenceMode == "auto";
                _gpuNvidiaMenuItem.Checked = _state.GpuPreferenceMode == "dgpu";
                _gpuIntegratedMenuItem.Checked = _state.GpuPreferenceMode == "hybrid";
            }
        }

        private void RefreshTrayText()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshTrayText));
                return;
            }

            if (_notifyIcon == null)
            {
                return;
            }

            var power = PowerManager.DescribeMode(_state.PowerMode);
            var route = _state.GpuRouteMode == "dgpu"
                ? "Route Dedicated"
                : (_state.GpuRouteMode == "hybrid" ? "Route Hybrid" : "Route Unknown");
            var pref = _state.GpuPreferenceMode == "dgpu"
                ? "Policy RTX"
                : (_state.GpuPreferenceMode == "hybrid" ? "Policy Optimus" : (_state.GpuPreferenceMode == "auto" ? "Policy Auto" : "Policy ?"));
            var text = string.Format("GSwitcher | {0} | {1} | {2}", power, route, pref);
            if (_state.RequiresRestart && _state.GpuPendingRouteMode != "unknown")
            {
                text = string.Format(
                    "GSwitcher | {0} -> {1} | Restart",
                    route,
                    _state.GpuPendingRouteMode == "dgpu" ? "Dedicated" : "Hybrid");
            }

            if (text.Length > 63)
            {
                text = text.Substring(0, 63);
            }

            _notifyIcon.Text = text;
        }

        private OperationResult RefreshHardwareSnapshot()
        {
            var powerHealth = PowerManager.EnsureRequiredPlans();
            _state.PowerPlanHealthState = !powerHealth.Success
                ? "error"
                : (!string.IsNullOrWhiteSpace(powerHealth.Warning) ? "self-healed" : "healthy");
            _state.PowerPlanHealth = !powerHealth.Success
                ? powerHealth.Message
                : (!string.IsNullOrWhiteSpace(powerHealth.Warning) ? powerHealth.Warning : powerHealth.Message);
            _state.HelperInstalled = false;
            _state.EvidenceSessionPath = string.Empty;
            _state.LastEvidenceCapturePath = string.Empty;

            _state.PowerMode = PowerManager.DetectActiveMode();

            GpuManager.RefreshController();
            _state.GpuRouteControlPath = GpuManager.CurrentRouteControlPath;
            _state.GpuPreferenceControlPath = GpuManager.CurrentPreferenceControlPath;
            _state.AvailableGpuRouteModes = GpuManager.GetAvailableRouteModes();
            _state.AvailableGpuPreferenceModes = GpuManager.GetAvailablePreferenceModes();

            var routeSnapshot = GpuManager.DetectCurrentRouteSnapshot();
            _state.GpuRouteMode = routeSnapshot.Mode;
            _state.GpuRouteSource = routeSnapshot.Source;
            _state.GpuRouteEvidence = routeSnapshot.Evidence;

            var panelSnapshot = NvidiaPanelController.DetectSnapshot();
            _state.GpuPanelMode = panelSnapshot.Mode;
            _state.GpuPanelControlPath = panelSnapshot.ControlPath;
            _state.GpuPanelEvidence = panelSnapshot.Evidence;

            var detectedPreferenceMode = GpuManager.DetectCurrentPreference();
            _state.GpuPreferenceMode = !string.IsNullOrWhiteSpace(detectedPreferenceMode)
                ? detectedPreferenceMode
                : "unknown";

            if (_state.RequiresRestart && _state.GpuPendingRouteMode != "unknown" &&
                string.Equals(_state.GpuRouteMode, _state.GpuPendingRouteMode, StringComparison.OrdinalIgnoreCase))
            {
                _state.RequiresRestart = false;
                _state.GpuPendingRouteMode = "unknown";
            }

            if (_state.GpuRouteControlPath == "unavailable")
            {
                _state.RequiresRestart = false;
                _state.GpuPendingRouteMode = "unknown";
            }

            _state.GpuMode = _state.GpuRouteMode != "unknown" ? _state.GpuRouteMode : _state.GpuPreferenceMode;
            _state.GpuControlPath = _state.GpuRouteControlPath != "unavailable"
                ? _state.GpuRouteControlPath
                : _state.GpuPreferenceControlPath;
            _state.AvailableGpuModes = _state.AvailableGpuRouteModes.Length > 0
                ? _state.AvailableGpuRouteModes
                : _state.AvailableGpuPreferenceModes;

            DisplayStateService.Refresh(_state);

            return powerHealth;
        }

        private bool SupportsGpuMode(string mode)
        {
            return SupportsGpuRouteMode(mode) || SupportsGpuPreferenceMode(mode);
        }

        private bool SupportsGpuRouteMode(string mode)
        {
            if (_state.AvailableGpuRouteModes == null)
            {
                return false;
            }

            for (var index = 0; index < _state.AvailableGpuRouteModes.Length; index++)
            {
                if (string.Equals(_state.AvailableGpuRouteModes[index], mode, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool SupportsGpuPreferenceMode(string mode)
        {
            if (_state.AvailableGpuPreferenceModes == null)
            {
                return false;
            }

            for (var index = 0; index < _state.AvailableGpuPreferenceModes.Length; index++)
            {
                if (string.Equals(_state.AvailableGpuPreferenceModes[index], mode, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldKeepRestartFlag(AppState state)
        {
            if (state == null || !state.RequiresRestart)
            {
                return false;
            }

            var bootTimeUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCountMilliseconds());
            return state.LastUpdatedUtc >= bootTimeUtc;
        }

        private static ulong GetTickCountMilliseconds()
        {
            return NativeMethods.GetTickCount64();
        }

        private static void LogOperation(string category, OperationResult result)
        {
            if (result == null)
            {
                return;
            }

            AppLogger.Write(
                "Operation " + category +
                " | success=" + result.Success +
                " | controlPath=" + result.ControlPath +
                " | requested=" + result.RequestedMode +
                " | verified=" + result.VerifiedMode +
                " | restart=" + result.RequiresRestart +
                " | message=" + result.Message +
                    " | detail=" + (result.ErrorDetail ?? string.Empty));
        }

        private static OperationResult TryRelaunchElevated(string arguments)
        {
            try
            {
                var exePath = GetCurrentExecutablePath();
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    return OperationResult.Fail(
                        "GSwitcher could not find its own executable path for an elevated relaunch.",
                        controlPath: "process-relaunch");
                }

                var escapedArguments = string.IsNullOrWhiteSpace(arguments) ? string.Empty : arguments.Trim();
                var compatLayer = Environment.GetEnvironmentVariable("__COMPAT_LAYER");
                if (!string.IsNullOrWhiteSpace(compatLayer) &&
                    compatLayer.IndexOf("RunAsInvoker", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var quotedExePath = exePath.Replace("'", "''");
                    var quotedArguments = escapedArguments.Replace("'", "''");
                    var command = "$env:__COMPAT_LAYER=''; Start-Process -FilePath '" +
                        quotedExePath +
                        "' -Verb RunAs" +
                        (string.IsNullOrWhiteSpace(quotedArguments) ? string.Empty : (" -ArgumentList '" + quotedArguments + "'"));
                    var encodedCommand = Convert.ToBase64String(
                        System.Text.Encoding.Unicode.GetBytes(command));
                    var powershellInfo = new ProcessStartInfo
                    {
                        FileName = GetWindowsPowerShellPath(),
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    Process.Start(powershellInfo);
                }
                else
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = escapedArguments,
                        UseShellExecute = true,
                        Verb = "runas"
                    };

                    Process.Start(startInfo);
                }

                return OperationResult.Ok(
                    string.IsNullOrWhiteSpace(escapedArguments)
                        ? "Relaunching GSwitcher with administrator access."
                        : "Launching elevated action.",
                    "process-relaunch",
                    verified: true);
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                {
                    return OperationResult.Fail(
                        "Administrator access was canceled. Native GPU routing remains locked.",
                        ex.ToString(),
                        "process-relaunch");
                }

                return OperationResult.Fail(
                    "GSwitcher could not relaunch with administrator access.",
                    ex.ToString(),
                    "process-relaunch");
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(
                    "GSwitcher could not relaunch with administrator access.",
                    ex.ToString(),
                    "process-relaunch");
            }
        }

        private static string GetWindowsPowerShellPath()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");

            return File.Exists(path) ? path : "powershell.exe";
        }

        private static string GetCurrentExecutablePath()
        {
            using (var currentProcess = Process.GetCurrentProcess())
            {
                return currentProcess.MainModule != null
                    ? currentProcess.MainModule.FileName
                    : null;
            }
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll")]
            public static extern ulong GetTickCount64();
        }
    }
}
