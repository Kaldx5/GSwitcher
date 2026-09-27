using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GSwitcher.Models;
using GSwitcher.Services;

namespace GSwitcher.Backend
{
    public static class SmartCoolingService
    {
        private static readonly object SyncLock = new object();
        private static CancellationTokenSource _cts;
        private static Task _loopTask;
        private static int _generation;
        private static bool _shuttingDown;

        public static bool IsActive { get; private set; }
        public static string ActiveProfile { get; private set; }
        public static int TargetTemperature { get; private set; }
        public static int CurrentTemperature { get; private set; }
        public static string OptimizationStatus { get; private set; }
        public static bool StableStateReached { get; private set; }
        public static bool IsSmartCoolingEnabled { get; private set; }

        static SmartCoolingService()
        {
            IsActive = false;
            ActiveProfile = "none";
            TargetTemperature = 0;
            CurrentTemperature = 0;
            OptimizationStatus = "Idle";
            StableStateReached = false;
            IsSmartCoolingEnabled = false;
        }

        private static void TriggerUiUpdate()
        {
            try
            {
                if (System.Windows.Application.Current != null)
                {
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var app = System.Windows.Application.Current as GSwitcher.App;
                        if (app != null)
                        {
                            app.TriggerThermalDiagnosticsUpdate();
                        }
                    }));
                }
            }
            catch
            {
                // Ignore if app is shutting down
            }
        }

        public static void Start(string profile, bool forceReoptimize = false)
        {
            lock (SyncLock)
            {
                if (_shuttingDown)
                {
                    return;
                }

                StopCore(false);
                var generation = ++_generation;
                var settings = SettingsManager.Current;
                settings.SmartCoolingAutomaticEnabled = true;
                SettingsManager.Save(settings);

                ActiveProfile = (profile ?? "medium").ToLower();
                StableStateReached = false;
                IsSmartCoolingEnabled = true;
                IsActive = true;

                if (ActiveProfile == "simple")
                {
                    TargetTemperature = 50;
                }
                else if (ActiveProfile == "high")
                {
                    TargetTemperature = 75;
                }
                else
                {
                    ActiveProfile = "medium";
                    TargetTemperature = 60;
                }

                bool hasSaved = false;
                int savedBoost = 1;
                int savedMaxState = 100;
                int savedDellProfile = 0xA0;

                if (ActiveProfile == "simple")
                {
                    hasSaved = settings.SmartSimple_Saved;
                    savedBoost = settings.SmartSimple_Boost;
                    savedMaxState = settings.SmartSimple_MaxState;
                    savedDellProfile = settings.SmartSimple_DellProfile;
                }
                else if (ActiveProfile == "medium")
                {
                    hasSaved = settings.SmartMedium_Saved;
                    savedBoost = settings.SmartMedium_Boost;
                    savedMaxState = settings.SmartMedium_MaxState;
                    savedDellProfile = settings.SmartMedium_DellProfile;
                }
                else if (ActiveProfile == "high")
                {
                    hasSaved = settings.SmartHigh_Saved;
                    savedBoost = settings.SmartHigh_Boost;
                    savedMaxState = settings.SmartHigh_MaxState;
                    savedDellProfile = settings.SmartHigh_DellProfile;
                }

                if (hasSaved && !forceReoptimize)
                {
                    // Apply cached profile directly but keep watchdog loop running in stable mode
                    OptimizationStatus = "Loading Saved Profile...";
                    TriggerUiUpdate();
                    ApplyProfileSettings(savedBoost, savedMaxState, savedDellProfile);
                    StableStateReached = true;
                    IsActive = false; // It's in stable mode, not active regulating
                    OptimizationStatus = string.Format("Stable - Loaded Saved Profile (Boost: {0}, Limit: {1}%, Fan: {2})",
                        ThermalDiagnosticsService.DescribeBoostMode(savedBoost),
                        savedMaxState,
                        ThermalDiagnosticsService.DescribeDellProfile(savedDellProfile));
                    AppLogger.Write(string.Format("SmartCooling: Applied cached profile for '{0}': Boost={1}, MaxState={2}, DellProfile=0x{3:X}",
                        ActiveProfile, savedBoost, savedMaxState, savedDellProfile));
                }
                else
                {
                    // Start fresh: Reset optimization status and set initial fans
                    OptimizationStatus = "Optimizing: Querying system temperature...";
                    int initialDellProfile = (ActiveProfile == "high") ? 0xA4 : (ActiveProfile == "medium" ? 0xA0 : 0xA3); // Performance, Balanced, Quiet
                    ThermalDiagnosticsService.SetDellThermalProfile(initialDellProfile);
                }

                // Start background watchdog loop
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                TriggerUiUpdate();

                _loopTask = Task.Run(async () =>
                {
                    try
                    {
                        await RunWatchdogLoop(token, generation);
                    }
                    catch (Exception ex)
                    {
                        if (token.IsCancellationRequested || !IsCurrentGeneration(generation))
                        {
                            return;
                        }
                        AppLogger.Write("SmartCooling: Watchdog loop crash: " + ex.Message);
                        lock (SyncLock)
                        {
                            OptimizationStatus = "Error: " + ex.Message;
                            IsActive = false;
                            IsSmartCoolingEnabled = false;
                        }
                        TriggerUiUpdate();
                    }
                }, token);
            }
        }

        public static void Stop()
        {
            lock (SyncLock)
            {
                StopCore(true);
            }
        }

        public static void Shutdown()
        {
            lock (SyncLock)
            {
                _shuttingDown = true;
                StopCore(false);
            }
        }

        private static void StopCore(bool disableAutomatic)
        {
            ++_generation;
            if (_cts != null)
            {
                _cts.Cancel();
                // Do not dispose here: a canceled loop may still be observing the token.
                _cts = null;
            }

            IsActive = false;
            IsSmartCoolingEnabled = false;
            StableStateReached = false;
            ActiveProfile = "none";
            TargetTemperature = 0;
            _loopTask = null;
            if (disableAutomatic)
            {
                var settings = SettingsManager.Current;
                settings.SmartCoolingAutomaticEnabled = false;
                SettingsManager.Save(settings);
                OptimizationStatus = "Automatic optimization disabled";
            }
            else if (!_shuttingDown)
            {
                OptimizationStatus = "Stopped";
            }
            TriggerUiUpdate();
        }

        private static bool IsCurrentGeneration(int generation)
        {
            lock (SyncLock)
            {
                return generation == _generation && !_shuttingDown;
            }
        }

        public static void Reset(string profile)
        {
            lock (SyncLock)
            {
                var settings = SettingsManager.Current;
                profile = (profile ?? "medium").ToLower();

                if (profile == "simple")
                {
                    settings.SmartSimple_Saved = false;
                }
                else if (profile == "medium")
                {
                    settings.SmartMedium_Saved = false;
                }
                else if (profile == "high")
                {
                    settings.SmartHigh_Saved = false;
                }

                SettingsManager.Save(settings);
                AppLogger.Write("SmartCooling: Reset saved settings for profile: " + profile);
                Start(profile, true);
            }
        }

        public static Dictionary<string, object> GetServiceState()
        {
            lock (SyncLock)
            {
                return new Dictionary<string, object>
                {
                    { "isActive", IsActive },
                    { "activeProfile", ActiveProfile },
                    { "targetTemperature", TargetTemperature },
                    { "currentTemperature", CurrentTemperature },
                    { "optimizationStatus", OptimizationStatus },
                    { "stableStateReached", StableStateReached },
                    { "isEnabled", IsSmartCoolingEnabled }
                };
            }
        }

        private static async Task RunWatchdogLoop(CancellationToken token, int generation)
        {
            int overTargetCount = 0;
            int underTargetCount = 0;
            int spikeCount = 0;
            int coolStateCount = 0;

            double emaTemp = -1;
            double emaDerivative = 0;

            // Start by tracking initial config
            int currentBoost = ThermalDiagnosticsService.GetBoostMode();
            int currentMaxState = ThermalDiagnosticsService.GetMaxProcessorState();
            int currentDellProfile = ThermalDiagnosticsService.GetDellThermalProfile();

            if (currentBoost < 0) currentBoost = 1; // Default to enabled
            if (currentMaxState < 0) currentMaxState = 100;
            if (currentDellProfile < 0) currentDellProfile = (ActiveProfile == "high") ? 0xA4 : (ActiveProfile == "medium" ? 0xA0 : 0xA3);

            AppLogger.Write(string.Format("SmartCooling Watchdog Initialized: Profile={0}, Target={1}°C, Boost={2}, MaxState={3}%, FanProfile=0x{4:X}",
                ActiveProfile, TargetTemperature, currentBoost, currentMaxState, currentDellProfile));

            while (!token.IsCancellationRequested && IsSmartCoolingEnabled && IsCurrentGeneration(generation))
            {
                int temp = ThermalDiagnosticsService.GetSystemTemperature();
                lock (SyncLock)
                {
                    CurrentTemperature = temp;
                }

                if (temp == 0)
                {
                    // Fallback to CPU usage check
                    lock (SyncLock)
                    {
                        OptimizationStatus = "Optimizing: Waiting for temperature sensors (using CPU load analyzer)...";
                    }
                    TriggerUiUpdate();

                    var processes = ThermalDiagnosticsService.GetTopProcesses(3);
                    long totalCpu = 0;
                    foreach (var proc in processes)
                    {
                        if (proc.ContainsKey("cpu"))
                        {
                            totalCpu += Convert.ToInt64(proc["cpu"]);
                        }
                    }

                    if (totalCpu > 15) // High CPU load proxy
                    {
                        if (currentBoost != 0)
                        {
                            ThermalDiagnosticsService.SetBoostMode(0);
                            currentBoost = 0;
                            lock (SyncLock)
                            {
                                OptimizationStatus = "Optimizing: High CPU load proxy. Disabled Turbo Boost.";
                            }
                            TriggerUiUpdate();
                        }
                        else if (currentMaxState > 85)
                        {
                            currentMaxState -= 5;
                            ThermalDiagnosticsService.SetMaxProcessorState(currentMaxState);
                            lock (SyncLock)
                            {
                                OptimizationStatus = "Optimizing: High CPU load proxy. Capping CPU Max State to " + currentMaxState + "%.";
                            }
                            TriggerUiUpdate();
                        }
                    }
                    else
                    {
                        // Safe load
                        underTargetCount++;
                        if (underTargetCount >= 3)
                        {
                            if (!IsCurrentGeneration(generation)) return;
                            SaveStableState(currentBoost, currentMaxState, currentDellProfile);
                            lock (SyncLock)
                            {
                                StableStateReached = true;
                                IsActive = false;
                            }
                            TriggerUiUpdate();
                        }
                    }
                }
                else
                {
                    // EMA and PID calculation
                    if (emaTemp < 0) emaTemp = temp;
                    else
                    {
                        double prevEma = emaTemp;
                        emaTemp = (temp * 0.4) + (emaTemp * 0.6); // Fast responsive EMA
                        emaDerivative = emaTemp - prevEma;
                    }
                    
                    double projectedTemp = emaTemp + (emaDerivative * 3.0); // Project 15s ahead (3 ticks of 5s)

                    string trendLabel = "Stable";
                    if (emaDerivative > 0.5) trendLabel = "Rising Fast";
                    else if (emaDerivative > 0.1) trendLabel = "Rising";
                    else if (emaDerivative < -0.5) trendLabel = "Cooling Fast";
                    else if (emaDerivative < -0.1) trendLabel = "Cooling";

                    bool isStableMode;
                    lock (SyncLock)
                    {
                        isStableMode = StableStateReached;
                    }

                    if (isStableMode)
                    {
                        // ─── WATCHDOG ENFORCEMENT MODE ───
                        if (temp > TargetTemperature + 2 || projectedTemp > TargetTemperature + 5)
                        {
                            // Temperature spiked above target threshold or is projected to spike
                            spikeCount++;
                            coolStateCount = 0;
                            lock (SyncLock)
                            {
                                OptimizationStatus = string.Format("Enforcing - Temp spike: {0}°C (Trend: {1} {2:+0.0;-0.0}°C/tick). Reacting in {3}s...",
                                    temp, trendLabel, emaDerivative, (2 - spikeCount) * 5);
                            }
                            TriggerUiUpdate();

                            if (spikeCount >= 2) // Above target limit for 10s (faster reaction)
                            {
                                spikeCount = 0;
                                lock (SyncLock)
                                {
                                    StableStateReached = false;
                                    IsActive = true; // Return to active regulating
                                }
                                AppLogger.Write(string.Format("SmartCooling: Watchdog triggered due to temp spike ({0}°C, Proj: {1:F1}°C). Re-regulating...", temp, projectedTemp));
                                TriggerUiUpdate();

                                overTargetCount = 1;
                                underTargetCount = 0;
                            }
                        }
                        else if (temp <= TargetTemperature - 8)
                        {
                            // Safely cool! Check if we can relax settings to restore performance
                            coolStateCount++;
                            spikeCount = 0;
                            if (coolStateCount >= 4) // Cool for 20 seconds
                            {
                                coolStateCount = 0;
                                bool relaxed = false;

                                // 1. Relax Max Processor State first
                                if (currentMaxState < 100)
                                {
                                    currentMaxState += 5;
                                    if (currentMaxState > 100) currentMaxState = 100;
                                    ThermalDiagnosticsService.SetMaxProcessorState(currentMaxState);
                                    relaxed = true;
                                    lock (SyncLock)
                                    {
                                        OptimizationStatus = string.Format("Stable (Cool: {0}°C) - Relaxing CPU limit to {1}%", temp, currentMaxState);
                                    }
                                    TriggerUiUpdate();
                                    AppLogger.Write(string.Format("SmartCooling: Relaxed CPU MaxState to {0}% due to cool temp ({1}°C).", currentMaxState, temp));
                                }
                                // 2. Enable Turbo Boost if MaxState is at 100%
                                else if (currentBoost == 0)
                                {
                                    ThermalDiagnosticsService.SetBoostMode(1);
                                    currentBoost = 1;
                                    relaxed = true;
                                    lock (SyncLock)
                                    {
                                        OptimizationStatus = string.Format("Stable (Cool: {0}°C) - Re-enabling CPU Boost", temp);
                                    }
                                    TriggerUiUpdate();
                                    AppLogger.Write(string.Format("SmartCooling: Re-enabled Turbo Boost due to cool temp ({0}°C).", temp));
                                }

                                if (relaxed)
                                {
                                    // Update our saved profile with these relaxed parameters as they are currently stable!
                                    if (!IsCurrentGeneration(generation)) return;
                                    SaveStableState(currentBoost, currentMaxState, currentDellProfile);
                                    // Remain in stable mode
                                    lock (SyncLock)
                                    {
                                        StableStateReached = true;
                                        IsActive = false;
                                    }
                                    TriggerUiUpdate();
                                }
                            }
                        }
                        else
                        {
                            // Temp is in a normal stable range
                            spikeCount = 0;
                            coolStateCount = 0;
                            lock (SyncLock)
                            {
                                OptimizationStatus = string.Format("Stable - Target Reached & Saved! (Boost: {0}, Limit: {1}%, Fan: {2})",
                                    ThermalDiagnosticsService.DescribeBoostMode(currentBoost),
                                    currentMaxState,
                                    ThermalDiagnosticsService.DescribeDellProfile(currentDellProfile));
                            }
                            TriggerUiUpdate();
                        }
                    }
                    else
                    {
                        // ─── ACTIVE REGULATION MODE ───
                        lock (SyncLock)
                        {
                            OptimizationStatus = string.Format("Optimizing: Temperature is {0}°C (Target: <{1}°C, Proj: {2:F1}°C)", 
                                temp, TargetTemperature, projectedTemp);
                        }
                        TriggerUiUpdate();

                        if (temp > TargetTemperature || projectedTemp > TargetTemperature + 3)
                        {
                            underTargetCount = 0;
                            overTargetCount++;

                            if (overTargetCount >= 1) // Immediate reaction on high spikes
                            {
                                overTargetCount = 0;

                                int diff = temp - TargetTemperature;
                                double projDiff = projectedTemp - TargetTemperature;

                                // Step 1: Force Cool/Quiet/Performance thermal profiles dynamically (NEVER G-mode)
                                int targetProfile = currentDellProfile;
                                if (temp >= 75 || diff >= 15 || emaDerivative >= 1.0)
                                {
                                    targetProfile = 0xA4; // Performance
                                }
                                else if (temp >= 65 || diff >= 5)
                                {
                                    targetProfile = 0xA2; // Cool
                                }
                                else
                                {
                                    targetProfile = 0xA0; // Balanced
                                }

                                if (currentDellProfile != targetProfile)
                                {
                                    var res = ThermalDiagnosticsService.SetDellThermalProfile(targetProfile);
                                    if (res.Success)
                                    {
                                        currentDellProfile = targetProfile;
                                    }
                                    AppLogger.Write(string.Format("SmartCooling: Dynamic Fan Switch: Set to {0} due to temp={1}°C.", 
                                        ThermalDiagnosticsService.DescribeDellProfile(currentDellProfile), temp));
                                }

                                // Step 2: Disable CPU Turbo Boost
                                if (currentBoost != 0)
                                {
                                    ThermalDiagnosticsService.SetBoostMode(0);
                                    currentBoost = 0;
                                    lock (SyncLock)
                                    {
                                        OptimizationStatus = "Optimizing: Disabling CPU Turbo Boost to lower thermal output.";
                                    }
                                    AppLogger.Write("SmartCooling: Disabled CPU Turbo Boost to lower thermals.");
                                    TriggerUiUpdate();
                                }
                                // Step 3: Proportional Capping of Max Processor State (fast convergence via PID)
                                else if (currentMaxState > 50) // Allow dropping lower than before (50% instead of 70%) to aggressively contain heat
                                {
                                    int step = 3;
                                    if (projDiff >= 20 || diff >= 20) step = 15;
                                    else if (projDiff >= 10 || diff >= 10) step = 10;
                                    else if (projDiff >= 5 || diff >= 5) step = 5;

                                    currentMaxState -= step;
                                    if (currentMaxState < 50) currentMaxState = 50;

                                    ThermalDiagnosticsService.SetMaxProcessorState(currentMaxState);
                                    lock (SyncLock)
                                    {
                                        OptimizationStatus = string.Format("Optimizing: Capping Max CPU State to {0}% (PID Drop {1}%) to enforce cooling.", currentMaxState, step);
                                    }
                                    AppLogger.Write(string.Format("SmartCooling: Dropped Max CPU State to {0}% (step={1}) due to diff={2}°C, projDiff={3:F1}.", currentMaxState, step, diff, projDiff));
                                    TriggerUiUpdate();
                                }
                                // Keep optimization within power and thermal controls. Changing
                                // another process's priority or affinity persists after we exit.
                                else
                                {
                                    lock (SyncLock)
                                    {
                                        OptimizationStatus = "Optimizing: Capped to minimum power state. Waiting for thermal dissipation...";
                                    }
                                    TriggerUiUpdate();
                                }
                            }
                        }
                        else
                        {
                            // Temp is within target!
                            overTargetCount = 0;
                            underTargetCount++;

                            lock (SyncLock)
                            {
                                OptimizationStatus = string.Format("Stabilizing: Temperature is {0}°C (Target: <{1}°C). Stabilizing for {2}s...", temp, TargetTemperature, (4 - underTargetCount) * 5);
                            }
                            TriggerUiUpdate();

                            if (underTargetCount >= 4) // Stable for 20s
                            {
                                // Relax fans to Balanced (0xA0) or Quiet (0xA3) if stable under target
                                int relaxedProfile = (ActiveProfile == "simple") ? 0xA3 : 0xA0;
                                if (currentDellProfile != relaxedProfile)
                                {
                                    var res = ThermalDiagnosticsService.SetDellThermalProfile(relaxedProfile);
                                    if (res.Success)
                                    {
                                        currentDellProfile = relaxedProfile;
                                    }
                                }

                                if (!IsCurrentGeneration(generation)) return;
                                SaveStableState(currentBoost, currentMaxState, currentDellProfile);
                                lock (SyncLock)
                                {
                                    StableStateReached = true;
                                    IsActive = false;
                                }
                                TriggerUiUpdate();
                                underTargetCount = 0;
                            }
                        }
                    }
                }

                await Task.Delay(5000, token);
            }
        }

        private static void SaveStableState(int boost, int maxState, int dellProfile)
        {
            var settings = SettingsManager.Current;

            if (ActiveProfile == "simple")
            {
                settings.SmartSimple_Saved = true;
                settings.SmartSimple_Boost = boost;
                settings.SmartSimple_MaxState = maxState;
                settings.SmartSimple_DellProfile = dellProfile;
            }
            else if (ActiveProfile == "medium")
            {
                settings.SmartMedium_Saved = true;
                settings.SmartMedium_Boost = boost;
                settings.SmartMedium_MaxState = maxState;
                settings.SmartMedium_DellProfile = dellProfile;
            }
            else if (ActiveProfile == "high")
            {
                settings.SmartHigh_Saved = true;
                settings.SmartHigh_Boost = boost;
                settings.SmartHigh_MaxState = maxState;
                settings.SmartHigh_DellProfile = dellProfile;
            }

            SettingsManager.Save(settings);

            lock (SyncLock)
            {
                OptimizationStatus = string.Format("Stable - Target Reached & Saved! (Boost: {0}, Limit: {1}%, Fan: {2})",
                    ThermalDiagnosticsService.DescribeBoostMode(boost),
                    maxState,
                    ThermalDiagnosticsService.DescribeDellProfile(dellProfile));
            }

            AppLogger.Write(string.Format("SmartCooling: Saved stable state for '{0}': Boost={1}, MaxState={2}, DellProfile=0x{3:X}",
                ActiveProfile, boost, maxState, dellProfile));
        }

        private static void ApplyProfileSettings(int boost, int maxState, int dellProfile)
        {
            try
            {
                ThermalDiagnosticsService.SetDellThermalProfile(dellProfile);
                ThermalDiagnosticsService.SetBoostMode(boost);
                ThermalDiagnosticsService.SetMaxProcessorState(maxState);
            }
            catch (Exception ex)
            {
                AppLogger.Write("SmartCooling: Failed to apply profile settings: " + ex.Message);
            }
        }
    }
}
