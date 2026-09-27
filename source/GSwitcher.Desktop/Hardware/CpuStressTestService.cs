using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GSwitcher.Backend;
using GSwitcher.Models;
using GSwitcher.Services;

namespace GSwitcher.Hardware
{
    public static class CpuStressTestService
    {
        private static readonly object SyncLock = new object();
        private static CancellationTokenSource _testCts;
        private static CancellationTokenSource _workerCts;
        private static Task _mainTestTask;
        private static readonly List<Task> WorkerTasks = new List<Task>();

        public static bool IsActive { get; private set; }
        public static PowerPlanProfile ActiveProfile { get; private set; }
        public static PowerPlanProfile CalibratedProfile { get; private set; }
        public static string CurrentPhase { get; private set; }
        public static int ElapsedTimeSeconds { get; private set; }
        public static int ProgressPercent { get; private set; }
        public static int CurrentTemp { get; private set; }
        public static int CurrentClock { get; private set; }
        public static int PeakTemperature { get; private set; }
        public static int InitialTemperature { get; private set; }
        public static double MaxSpikeDelta { get; private set; }
        public static string CalibrationReport { get; private set; }

        static CpuStressTestService()
        {
            ResetState();
        }

        private static void ResetState()
        {
            IsActive = false;
            ActiveProfile = null;
            CalibratedProfile = null;
            CurrentPhase = "Idle";
            ElapsedTimeSeconds = 0;
            ProgressPercent = 0;
            CurrentTemp = 0;
            CurrentClock = 0;
            PeakTemperature = 0;
            InitialTemperature = 0;
            MaxSpikeDelta = 0.0;
            CalibrationReport = "No calibration run yet.";
        }

        private static void TriggerUiUpdate()
        {
            try
            {
                if (System.Windows.Application.Current != null)
                {
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var app = System.Windows.Application.Current as App;
                        if (app != null)
                        {
                            app.TriggerThermalDiagnosticsUpdate();
                        }
                    }));
                }
            }
            catch
            {
                // Ignore during shutdown
            }
        }

        public static void Start(PowerPlanProfile profileToTest)
        {
            lock (SyncLock)
            {
                if (IsActive)
                {
                    Stop();
                }

                ResetState();
                IsActive = true;
                ActiveProfile = profileToTest.Clone();
                CalibrationReport = "Starting stress test and auto-tuning calibration...";

                _testCts = new CancellationTokenSource();
                var token = _testCts.Token;

                _mainTestTask = Task.Run(async () =>
                {
                    try
                    {
                        await RunStressTestLoop(token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation is the normal path when the user stops or restarts a test.
                        if (!token.IsCancellationRequested)
                        {
                            lock (SyncLock)
                            {
                                IsActive = false;
                                CurrentPhase = "Error";
                                CalibrationReport = "Test canceled unexpectedly.";
                            }
                            TriggerUiUpdate();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Write("CpuStressTestService: Stress test crash: " + ex.Message);
                        lock (SyncLock)
                        {
                            IsActive = false;
                            CurrentPhase = "Error";
                            CalibrationReport = "Test failed: " + ex.Message;
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
                StopStressWorkers();

                if (_testCts != null)
                {
                    _testCts.Cancel();
                    _testCts.Dispose();
                    _testCts = null;
                }

                IsActive = false;
                CurrentPhase = "Stopped";
                CalibratedProfile = null;
                CalibrationReport = "Stress test cancelled by user.";
                TriggerUiUpdate();
            }
        }

        private static async Task RunStressTestLoop(CancellationToken token)
        {
            const int totalDuration = 100; // Total seconds for all phases
            var tempHistory = new List<int>();
            int lastTemp = 0;

            InitialTemperature = ThermalDiagnosticsService.GetSystemTemperature();
            PeakTemperature = InitialTemperature;

            for (int sec = 0; sec <= totalDuration; sec++)
            {
                if (token.IsCancellationRequested) break;

                // 1. Manage Workloads based on Phase
                if (sec == 0)
                {
                    CurrentPhase = "Phase 1/4: Idle Baseline";
                    StopStressWorkers();
                }
                else if (sec == 10)
                {
                    CurrentPhase = "Phase 2/4: Single-Core Load";
                    StartStressWorkers(1);
                }
                else if (sec == 35)
                {
                    CurrentPhase = "Phase 3/4: All-Core Load";
                    StartStressWorkers(Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2)));
                }
                else if (sec == 70)
                {
                    CurrentPhase = "Phase 4/4: Spike Test";
                }
                else if (sec == 90)
                {
                    CurrentPhase = "Cool-down & Calibration";
                    StopStressWorkers();
                }

                // Stepped load cycling for spike test
                if (sec >= 70 && sec < 90)
                {
                    if ((sec - 70) % 4 < 2) StartStressWorkers(Math.Min(4, Environment.ProcessorCount));
                    else StopStressWorkers();
                }

                // 2. Measure real-time metrics
                int temp = ThermalDiagnosticsService.GetSystemTemperature();
                int clock = ThermalDiagnosticsService.GetCurrentClockSpeed();

                lock (SyncLock)
                {
                    ElapsedTimeSeconds = sec;
                    ProgressPercent = (sec * 100) / totalDuration;
                    CurrentTemp = temp;
                    CurrentClock = clock;

                    if (temp > PeakTemperature) PeakTemperature = temp;
                    if (lastTemp > 0)
                    {
                        double delta = temp - lastTemp;
                        if (delta > MaxSpikeDelta) MaxSpikeDelta = delta;
                    }
                    lastTemp = temp;
                    tempHistory.Add(temp);
                }

                TriggerUiUpdate();
                await Task.Delay(1000, token);
            }

            if (token.IsCancellationRequested) return;

            // 3. Calibrate based on test results
            var calibrationLog = new List<string>();
            var calibrated = Calibrate(ActiveProfile, PeakTemperature, MaxSpikeDelta, calibrationLog);
            var report = GenerateCalibrationReport(ActiveProfile, calibrated, PeakTemperature, MaxSpikeDelta, calibrationLog);

            lock (SyncLock)
            {
                CalibratedProfile = calibrated;
                CalibrationReport = report;
                IsActive = false;
                CurrentPhase = "Completed";
            }
            TriggerUiUpdate();
        }

        private static void StartStressWorkers(int threadCount)
        {
            lock (SyncLock)
            {
                if (_workerCts != null)
                {
                    if (WorkerTasks.Count == threadCount) return;
                    StopStressWorkers();
                }

                _workerCts = new CancellationTokenSource();
                var token = _workerCts.Token;

                for (int i = 0; i < threadCount; i++)
                {
                    WorkerTasks.Add(Task.Factory.StartNew(
                        MathWorkerMethod, token, token, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                }
            }
        }

        private static void StopStressWorkers()
        {
            Task[] workers;
            CancellationTokenSource workerCts;
            lock (SyncLock)
            {
                workerCts = _workerCts;
                workers = WorkerTasks.ToArray();
                WorkerTasks.Clear();
                _workerCts = null;
                if (workerCts != null)
                {
                    workerCts.Cancel();
                }
            }

            if (workers.Length > 0)
            {
                try
                {
                    Task.WaitAll(workers, 1000);
                }
                catch (AggregateException)
                {
                    // A worker may fault while a process is shutting down; it must not
                    // prevent the next test from starting.
                }
            }

            if (workerCts != null)
            {
                workerCts.Dispose();
            }
        }

        private static void MathWorkerMethod(object state)
        {
            var token = (CancellationToken)state;
            double val = 1.0;
            while (!token.IsCancellationRequested)
            {
                for (int i = 0; i < 5000; i++) val = Math.Sin(val) + Math.Cos(val);
                Thread.Sleep(1);
            }
        }

        private static PowerPlanProfile Calibrate(PowerPlanProfile initial, int peakTemp, double maxSpike, List<string> log)
        {
            var calibrated = initial.Clone();
            int targetTemp = initial.Id.Contains("cool") ? 82 : 88;

            // 1. Analyze Peak Temperature -> Adjust Frequency Caps
            if (peakTemp > targetTemp)
            {
                int diff = peakTemp - targetTemp;
                int pCoreReduction = (diff / 2) * 75; // 75MHz reduction per 2°C over target
                int eCoreReduction = pCoreReduction / 2;

                log.Add(string.Format("Peak temp ({0}°C) > target ({1}°C). Reducing frequency.", peakTemp, targetTemp));
                calibrated.MaxPCoreFrequency -= pCoreReduction;
                calibrated.MaxECoreFrequency -= eCoreReduction;
            }

            // 2. Analyze Thermal Spikes -> Adjust Boost & EPP
            if (maxSpike >= 8.0 && calibrated.BoostMode == PowerPlanProfileBoostMode.Aggressive)
            {
                log.Add(string.Format("High thermal spike ({0:F1}°C/s). Downgrading boost from Aggressive.", maxSpike));
                calibrated.BoostMode = PowerPlanProfileBoostMode.EfficientAggressive;
                calibrated.Epp += 10;
            }
            else if (maxSpike >= 6.0)
            {
                log.Add(string.Format("Moderate thermal spike ({0:F1}°C/s). Increasing EPP to slow ramp-up.", maxSpike));
                calibrated.Epp += 15;
            }
            if (calibrated.Epp > 100) calibrated.Epp = 100;


            // 3. Final Guard: Enforce safety floors and user-defined absolute ceilings
            log.Add("Applying safety guards and user-defined hard caps.");
            const int pCoreSafetyFloor = 3200;
            const int eCoreSafetyFloor = 2400;

            if (calibrated.MaxPCoreFrequency < pCoreSafetyFloor) calibrated.MaxPCoreFrequency = pCoreSafetyFloor;
            if (calibrated.MaxECoreFrequency < eCoreSafetyFloor) calibrated.MaxECoreFrequency = eCoreSafetyFloor;
            
            // User's absolute hard cap for the Ultra profile
            if (initial.Id == "tested-ultra")
            {
                if (calibrated.MaxPCoreFrequency > 4100)
                {
                    log.Add("P-Core frequency capped at 4100 MHz (user request).");
                    calibrated.MaxPCoreFrequency = 4100;
                }
                if (calibrated.MaxECoreFrequency > 3200)
                {
                    log.Add("E-Core frequency capped at 3200 MHz (user request).");
                    calibrated.MaxECoreFrequency = 3200;
                }
            }

            return calibrated;
        }

        private static string GenerateCalibrationReport(PowerPlanProfile initial, PowerPlanProfile calibrated, int peakTemp, double maxSpike, List<string> log)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Calibration Analysis Complete!");
            sb.AppendLine("---------------------------------------");
            sb.AppendLine(string.Format("Peak Temperature: {0}°C", peakTemp));
            sb.AppendLine(string.Format("Max Temperature Spike: {0:F1}°C/sec", maxSpike));
            sb.AppendLine("---------------------------------------");
            sb.AppendLine("Recommended Setting Adjustments:");

            string pCoreChange = initial.MaxPCoreFrequency != calibrated.MaxPCoreFrequency
                ? string.Format("from {0} MHz to {1} MHz", initial.MaxPCoreFrequency, calibrated.MaxPCoreFrequency) : "Unchanged";
            sb.AppendLine(string.Format("• P-Core Cap: {0}", pCoreChange));

            string eCoreChange = initial.MaxECoreFrequency != calibrated.MaxECoreFrequency
                ? string.Format("from {0} MHz to {1} MHz", initial.MaxECoreFrequency, calibrated.MaxECoreFrequency) : "Unchanged";
            sb.AppendLine(string.Format("• E-Core Cap: {0}", eCoreChange));

            string boostChange = initial.BoostMode != calibrated.BoostMode
                ? string.Format("from {0} to {1}", initial.BoostMode, calibrated.BoostMode) : "Unchanged";
            sb.AppendLine(string.Format("• Boost Mode: {0}", boostChange));

            string eppChange = initial.Epp != calibrated.Epp
                ? string.Format("from {0} to {1}", initial.Epp, calibrated.Epp) : "Unchanged";
            sb.AppendLine(string.Format("• EPP Setting: {0}", eppChange));

            if (log.Any())
            {
                sb.AppendLine("---------------------------------------");
                sb.AppendLine("Reasoning:");
                foreach (var reason in log) sb.AppendLine(string.Format("• {0}", reason));
            }
            
            sb.AppendLine("---------------------------------------");
            sb.AppendLine("Apply these settings or re-run to fine-tune.");

            return sb.ToString();
        }

        public static Dictionary<string, object> GetServiceState()
        {
            lock (SyncLock)
            {
                return new Dictionary<string, object>
                {
                    { "isActive", IsActive },
                    { "activeProfileName", ActiveProfile != null ? ActiveProfile.Name : "None" },
                    { "currentPhase", CurrentPhase },
                    { "elapsedTimeSeconds", ElapsedTimeSeconds },
                    { "progressPercent", ProgressPercent },
                    { "currentTemp", CurrentTemp },
                    { "currentClock", CurrentClock },
                    { "peakTemperature", PeakTemperature },
                    { "initialTemperature", InitialTemperature },
                    { "maxSpikeDelta", MaxSpikeDelta },
                    { "calibrationReport", CalibrationReport },
                    { "calibratedPCoreMaxFreq", CalibratedProfile != null ? CalibratedProfile.MaxPCoreFrequency : null },
                    { "calibratedECoreMaxFreq", CalibratedProfile != null ? CalibratedProfile.MaxECoreFrequency : null },
                    { "calibratedEpp", CalibratedProfile != null ? (object)CalibratedProfile.Epp : null }
                };
            }
        }
    }
}
