using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using GSwitcher.Models;
using GSwitcher.Services;
using System.Threading.Tasks;

namespace GSwitcher.Backend
{
    public static class ThermalDiagnosticsService
    {
        private static readonly string PowerCfgPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "powercfg.exe");

        private const string ProcessorSubgroupGuid = "54533251-82be-4824-96c1-47b60b740d00";
        private const string PerfBoostModeGuid = "be337238-0d82-4146-a960-4f3749d470c7";
        private const string ProcThrottleMaxGuid = "bc5038f7-23e0-4960-96da-33abaf5935ec";

        public static string GetActiveSchemeGuid()
        {
            try
            {
                var result = RunPowerCfg("/getactivescheme");
                var match = Regex.Match(
                    result.StandardOutput ?? string.Empty,
                    @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
                return match.Success ? match.Groups[1].Value : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string GetActiveSchemeName()
        {
            try
            {
                var result = RunPowerCfg("/getactivescheme");
                var text = result.StandardOutput ?? string.Empty;
                var match = Regex.Match(text, @"\((.+)\)");
                return match.Success ? match.Groups[1].Value.Trim() : "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        public static int GetBoostMode()
        {
            try
            {
                var schemeGuid = GetActiveSchemeGuid();
                if (string.IsNullOrWhiteSpace(schemeGuid))
                {
                    return -1;
                }

                var result = RunPowerCfg(string.Format("/q {0} {1} {2}", schemeGuid, ProcessorSubgroupGuid, PerfBoostModeGuid));
                var text = result.StandardOutput ?? string.Empty;
                var match = Regex.Match(text, @"Current AC Power Setting Index:\s+0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return Convert.ToInt32(match.Groups[1].Value, 16);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("GetBoostMode failed: " + ex.Message);
            }

            return -1;
        }

        public static string DescribeBoostMode(int value)
        {
            switch (value)
            {
                case 0: return "Disabled";
                case 1: return "Enabled";
                case 2: return "Aggressive";
                case 3: return "Efficient Enabled";
                case 4: return "Efficient Aggressive";
                case 5: return "Aggressive At Guaranteed";
                case 6: return "Efficient Aggressive At Guaranteed";
                default: return value < 0 ? "Unknown" : "Custom (" + value + ")";
            }
        }

        public static OperationResult SetBoostMode(int value)
        {
            try
            {
                var schemeGuid = GetActiveSchemeGuid();
                if (string.IsNullOrWhiteSpace(schemeGuid))
                {
                    return OperationResult.Fail("Could not detect active power scheme.", controlPath: "thermal");
                }

                var acResult = RunPowerCfg(string.Format(
                    "/setacvalueindex {0} {1} {2} {3}",
                    schemeGuid, ProcessorSubgroupGuid, PerfBoostModeGuid, value));

                var dcResult = RunPowerCfg(string.Format(
                    "/setdcvalueindex {0} {1} {2} {3}",
                    schemeGuid, ProcessorSubgroupGuid, PerfBoostModeGuid, value));

                RunPowerCfg("/setactive " + schemeGuid);

                if (acResult.ExitCode != 0 || dcResult.ExitCode != 0)
                {
                    return OperationResult.Fail(
                        "Boost mode change may have failed. AC exit=" + acResult.ExitCode + " DC exit=" + dcResult.ExitCode,
                        controlPath: "thermal",
                        requestedMode: "boost-" + value);
                }

                var verified = GetBoostMode();
                var label = DescribeBoostMode(value);

                return OperationResult.Ok(
                    "CPU Boost Mode set to " + label + ".",
                    "thermal",
                    requestedMode: "boost-" + value,
                    verifiedMode: "boost-" + verified,
                    verified: verified == value);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(
                    "Boost mode change failed: " + ex.Message,
                    ex.ToString(),
                    "thermal",
                    "boost-" + value);
            }
        }

        public static int GetMaxProcessorState()
        {
            try
            {
                var schemeGuid = GetActiveSchemeGuid();
                if (string.IsNullOrWhiteSpace(schemeGuid))
                {
                    return -1;
                }

                var result = RunPowerCfg(string.Format("/q {0} {1} {2}", schemeGuid, ProcessorSubgroupGuid, ProcThrottleMaxGuid));
                var text = result.StandardOutput ?? string.Empty;
                var match = Regex.Match(text, @"Current AC Power Setting Index:\s+0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return Convert.ToInt32(match.Groups[1].Value, 16);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("GetMaxProcessorState failed: " + ex.Message);
            }

            return -1;
        }

        public static OperationResult SetMaxProcessorState(int percent)
        {
            if (percent < 5 || percent > 100)
            {
                return OperationResult.Fail("Max processor state must be between 5 and 100.", controlPath: "thermal");
            }

            try
            {
                var schemeGuid = GetActiveSchemeGuid();
                if (string.IsNullOrWhiteSpace(schemeGuid))
                {
                    return OperationResult.Fail("Could not detect active power scheme.", controlPath: "thermal");
                }

                var acResult = RunPowerCfg(string.Format(
                    "/setacvalueindex {0} {1} {2} {3}",
                    schemeGuid, ProcessorSubgroupGuid, ProcThrottleMaxGuid, percent));

                var dcResult = RunPowerCfg(string.Format(
                    "/setdcvalueindex {0} {1} {2} {3}",
                    schemeGuid, ProcessorSubgroupGuid, ProcThrottleMaxGuid, percent));

                RunPowerCfg("/setactive " + schemeGuid);

                if (acResult.ExitCode != 0 || dcResult.ExitCode != 0)
                {
                    return OperationResult.Fail(
                        "Max processor state change may have failed.",
                        controlPath: "thermal",
                        requestedMode: "maxstate-" + percent);
                }

                var verified = GetMaxProcessorState();
                var boostNote = percent < 100
                    ? " Turbo Boost is effectively disabled at the OS level."
                    : " Full Turbo Boost is allowed.";

                return OperationResult.Ok(
                    "Max Processor State set to " + percent + "%." + boostNote,
                    "thermal",
                    requestedMode: "maxstate-" + percent,
                    verifiedMode: "maxstate-" + verified,
                    verified: verified == percent);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(
                    "Max processor state change failed: " + ex.Message,
                    ex.ToString(),
                    "thermal",
                    "maxstate-" + percent);
            }
        }

        public static string DescribeDellProfile(int value)
        {
            switch (value)
            {
                case 0xA0: return "Balanced";
                case 0xA1: return "Balanced Perf";
                case 0xA2: return "Cool";
                case 0xA3: return "Quiet";
                case 0xA4: return "Performance";
                case 0xA5: return "Low Power";
                case 0xAB: return "G-Mode";
                default: return value <= 0 ? "Unknown" : "Profile 0x" + value.ToString("X");
            }
        }

        private static string _cachedCpuName = null;
        private static int _cachedBaseClock = 0;
        private static int _cachedCores = 0;
        private static int _cachedThreads = 0;

        private static List<Dictionary<string, object>> _cachedProcesses = new List<Dictionary<string, object>>();
        private static DateTime _lastProcessQuery = DateTime.MinValue;
        private static readonly object ProcessCacheLock = new object();
        private static bool _isQueryingProcesses = false;

        private static void EnsureCpuStaticDetails()
        {
            if (_cachedCpuName != null) return;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, MaxClockSpeed, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementObject obj in collection)
                    {
                        _cachedCpuName = (obj["Name"] ?? "Unknown CPU").ToString().Trim();
                        _cachedBaseClock = Convert.ToInt32(obj["MaxClockSpeed"] ?? 0);
                        _cachedCores = Convert.ToInt32(obj["NumberOfCores"] ?? 0);
                        _cachedThreads = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? 0);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Failed to query static CPU details: " + ex.Message);
                _cachedCpuName = "Unknown CPU";
            }
        }

        private static ManagementObject GetAwccWmiInstance(ManagementScope scope)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM AWCCWmiMethodFunction")))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementObject instance in collection)
                    {
                        return instance; // caller must dispose
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("GetAwccWmiInstance failed: " + ex.Message);
            }
            return null;
        }

        public static int GetSystemTemperature()
        {
            try
            {
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate
                };
                var scope = new ManagementScope(@"\\.\root\wmi", options);
                scope.Connect();
                using (var instance = GetAwccWmiInstance(scope))
                {
                    if (instance != null)
                    {
                        return GetSystemTemperature(instance);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("GetSystemTemperature failed: " + ex.Message);
            }
            return 0;
        }

        public static int GetSystemTemperature(ManagementObject instance)
        {
            int maxTemp = 0;
            for (int id = 1; id <= 6; id++)
            {
                try
                {
                    uint arg2 = (uint)((id << 8) | 4);
                    using (var input = instance.GetMethodParameters("Thermal_Information"))
                    {
                        input["arg2"] = Convert.ToInt32(arg2);
                        using (var output = instance.InvokeMethod("Thermal_Information", input, null))
                        {
                            if (output != null && output["argr"] != null)
                            {
                                long rawVal = Convert.ToInt64(output["argr"]);
                                if (rawVal != 0xFFFFFFFF && rawVal != -1)
                                {
                                    int val = (int)rawVal;
                                    if (val >= 20 && val <= 115)
                                    {
                                        if (val > maxTemp)
                                        {
                                            maxTemp = val;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Write(string.Format("GetSystemTemperature for sensor {0} failed: {1}", id, ex.Message));
                }
            }
            return maxTemp;
        }

        public static List<int> GetFanSpeeds()
        {
            try
            {
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate
                };
                var scope = new ManagementScope(@"\\.\root\wmi", options);
                scope.Connect();
                using (var instance = GetAwccWmiInstance(scope))
                {
                    if (instance != null)
                    {
                        return GetFanSpeeds(instance);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("GetFanSpeeds failed: " + ex.Message);
            }
            return new List<int>();
        }

        public static List<int> GetFanSpeeds(ManagementObject instance)
        {
            var fanSpeeds = new List<int>();
            for (int id = 1; id <= 4; id++)
            {
                try
                {
                    uint arg2 = (uint)((id << 8) | 5);
                    using (var input = instance.GetMethodParameters("Thermal_Information"))
                    {
                        input["arg2"] = Convert.ToInt32(arg2);
                        using (var output = instance.InvokeMethod("Thermal_Information", input, null))
                        {
                            if (output != null && output["argr"] != null)
                            {
                                long rawVal = Convert.ToInt64(output["argr"]);
                                if (rawVal != 0xFFFFFFFF && rawVal != -1)
                                {
                                    int val = (int)rawVal;
                                    if (val >= 0 && val <= 10000)
                                    {
                                        fanSpeeds.Add(val);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Write(string.Format("GetFanSpeeds for fan {0} failed: {1}", id, ex.Message));
                }
            }
            return fanSpeeds;
        }

        public static int GetDellThermalProfile()
        {
            try
            {
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate
                };
                var scope = new ManagementScope(@"\\.\root\wmi", options);
                scope.Connect();
                using (var instance = GetAwccWmiInstance(scope))
                {
                    if (instance != null)
                    {
                        return GetDellThermalProfile(instance);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("GetDellThermalProfile failed: " + ex.Message);
            }
            return -1;
        }

        public static int GetDellThermalProfile(ManagementObject instance)
        {
            try
            {
                using (var input = instance.GetMethodParameters("Thermal_Information"))
                {
                    input["arg2"] = 11;
                    using (var output = instance.InvokeMethod("Thermal_Information", input, null))
                    {
                        if (output != null && output["argr"] != null)
                        {
                            long rawRet = Convert.ToInt64(output["argr"]);
                            if (rawRet != -1 && rawRet != 0xFFFFFFFF && rawRet != 0xFFFF)
                            {
                                return (int)(rawRet & 0xFF);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("GetDellThermalProfile failed: " + ex.Message);
            }
            return -1;
        }

        public static OperationResult SetDellThermalProfile(int profileId)
        {
            try
            {
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate
                };
                var scope = new ManagementScope(@"\\.\root\wmi", options);
                scope.Connect();

                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM AWCCWmiMethodFunction")))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject instance in instances)
                    {
                        using (instance)
                        {
                            uint arg2 = (uint)((profileId << 8) | 1);
                            using (var input = instance.GetMethodParameters("Thermal_Control"))
                            {
                                input["arg2"] = Convert.ToInt32(arg2);
                                using (var output = instance.InvokeMethod("Thermal_Control", input, null))
                                {
                                    if (output == null || output["argr"] == null)
                                    {
                                        return OperationResult.Fail("SetDellThermalProfile: No output returned.", controlPath: "thermal");
                                    }
                                    long rawRet = Convert.ToInt64(output["argr"]);
                                    int ret = (int)rawRet;
                                    if (ret == 0 || ret == 1 || ret == -1)
                                    {
                                        return OperationResult.Ok(
                                            "Dell Thermal Profile set to " + DescribeDellProfile(profileId) + ".",
                                            "thermal",
                                            requestedMode: "profile-" + profileId,
                                            verifiedMode: "profile-" + profileId,
                                            verified: true);
                                    }
                                    return OperationResult.Fail("Dell Thermal Control returned failure code: " + ret, controlPath: "thermal");
                                }
                            }
                        }
                    }
                }
                return OperationResult.Fail("AWCCWmiMethodFunction WMI class not found.", controlPath: "thermal");
            }
            catch (Exception ex)
            {
                return OperationResult.Fail("SetDellThermalProfile failed: " + ex.Message, ex.ToString(), "thermal");
            }
        }

        private static PerformanceCounter _cpuFreqCounter = null;
        public static int GetCurrentClockSpeed()
        {
            try
            {
                if (_cpuFreqCounter == null)
                {
                    _cpuFreqCounter = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
                    _cpuFreqCounter.NextValue();
                }
                return (int)_cpuFreqCounter.NextValue();
            }
            catch (Exception ex)
            {
                AppLogger.Write("GetCurrentClockSpeed failed: " + ex.Message);
                return _cachedBaseClock;
            }
        }

        public static Dictionary<string, object> GetCpuSnapshot()
        {
            var snapshot = new Dictionary<string, object>();

            EnsureCpuStaticDetails();
            snapshot["cpuName"] = _cachedCpuName;
            snapshot["baseClock"] = _cachedBaseClock;
            snapshot["currentClock"] = GetCurrentClockSpeed();
            snapshot["cores"] = _cachedCores;
            snapshot["threads"] = _cachedThreads;

            snapshot["schemeName"] = GetActiveSchemeName();
            snapshot["schemeGuid"] = GetActiveSchemeGuid();

            var boostMode = GetBoostMode();
            snapshot["boostMode"] = boostMode;
            snapshot["boostLabel"] = DescribeBoostMode(boostMode);

            var maxState = GetMaxProcessorState();
            snapshot["maxProcessorState"] = maxState;

            try
            {
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate
                };
                var scope = new ManagementScope(@"\\.\root\wmi", options);
                scope.Connect();
                using (var instance = GetAwccWmiInstance(scope))
                {
                    if (instance != null)
                    {
                        snapshot["cpuTemp"] = GetSystemTemperature(instance);
                        
                        var dellProfile = GetDellThermalProfile(instance);
                        snapshot["dellThermalProfile"] = dellProfile;
                        snapshot["dellProfileLabel"] = DescribeDellProfile(dellProfile);
                        
                        snapshot["fanSpeeds"] = GetFanSpeeds(instance);
                    }
                    else
                    {
                        snapshot["cpuTemp"] = 0;
                        snapshot["dellThermalProfile"] = -1;
                        snapshot["dellProfileLabel"] = "Unknown";
                        snapshot["fanSpeeds"] = new List<int>();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("GetCpuSnapshot WMI aggregation failed: " + ex.Message);
                snapshot["cpuTemp"] = 0;
                snapshot["dellThermalProfile"] = -1;
                snapshot["dellProfileLabel"] = "Unknown";
                snapshot["fanSpeeds"] = new List<int>();
            }

            return snapshot;
        }

        public static List<Dictionary<string, object>> GetTopProcesses(int count)
        {
            lock (ProcessCacheLock)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastProcessQuery > TimeSpan.FromSeconds(10) || _cachedProcesses.Count == 0) && !_isQueryingProcesses)
                {
                    _isQueryingProcesses = true;
                    Task.Run(() =>
                    {
                        try
                        {
                            var fresh = FetchTopProcessesFromWmi();
                            lock (ProcessCacheLock)
                            {
                                _cachedProcesses = fresh;
                                _lastProcessQuery = DateTime.UtcNow;
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Write("Background top processes query failed: " + ex.Message);
                        }
                        finally
                        {
                            lock (ProcessCacheLock)
                            {
                                _isQueryingProcesses = false;
                            }
                        }
                    });
                }

                var result = new List<Dictionary<string, object>>();
                int limit = Math.Min(count, _cachedProcesses.Count);
                for (int i = 0; i < limit; i++)
                {
                    result.Add(new Dictionary<string, object>(_cachedProcesses[i]));
                }
                return result;
            }
        }

        private static List<Dictionary<string, object>> FetchTopProcessesFromWmi()
        {
            var results = new List<Dictionary<string, object>>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PercentProcessorTime, IDProcess FROM Win32_PerfFormattedData_PerfProc_Process " +
                    "WHERE Name != '_Total' AND Name != 'Idle'"))
                using (var collection = searcher.Get())
                {
                    var allProcesses = new List<KeyValuePair<long, Dictionary<string, object>>>();

                    foreach (ManagementObject obj in collection)
                    {
                        var cpuPercent = Convert.ToInt64(obj["PercentProcessorTime"] ?? 0);
                        if (cpuPercent <= 0)
                        {
                            continue;
                        }

                        var entry = new Dictionary<string, object>
                        {
                            { "name", CleanProcessName((obj["Name"] ?? "").ToString()) },
                            { "cpu", cpuPercent },
                            { "pid", Convert.ToInt32(obj["IDProcess"] ?? 0) }
                        };

                        allProcesses.Add(new KeyValuePair<long, Dictionary<string, object>>(cpuPercent, entry));
                    }

                    allProcesses.Sort((a, b) => b.Key.CompareTo(a.Key));

                    for (var i = 0; i < Math.Min(12, allProcesses.Count); i++)
                    {
                        results.Add(allProcesses[i].Value);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("FetchTopProcessesFromWmi failed: " + ex.Message);
            }
            return results;
        }

        public static Dictionary<string, object> PerformRootCauseAnalysis()
        {
            var analysis = new Dictionary<string, object>
            {
                { "wmiHogging", false },
                { "wmiCpu", 0L },
                { "dellHogging", false },
                { "dellCpu", 0L },
                { "windowsUpdateHogging", false },
                { "windowsUpdateCpu", 0L },
                { "rogueHogging", false },
                { "rogueProcessName", "" },
                { "rogueProcessPid", 0 },
                { "rogueProcessCpu", 0L },
                { "hasIssues", false }
            };

            var suggestions = new List<string>();
            List<Dictionary<string, object>> procs;
            lock (ProcessCacheLock)
            {
                procs = new List<Dictionary<string, object>>(_cachedProcesses);
            }

            foreach (var proc in procs)
            {
                var name = (proc["name"] ?? "").ToString().ToLower();
                var cpu = Convert.ToInt64(proc["cpu"]);
                var pid = Convert.ToInt32(proc["pid"]);

                if (name.Contains("wmiprvse"))
                {
                    analysis["wmiCpu"] = cpu;
                    if (cpu >= 5)
                    {
                        analysis["wmiHogging"] = true;
                        analysis["hasIssues"] = true;
                        suggestions.Add("WMI Provider Host (wmiprvse.exe) is consuming " + cpu + "% CPU. This usually happens due to background monitoring loops. Restarting the WMI service will reset this loop.");
                    }
                }
                else if (name.Contains("awcc") || name.Contains("dellhardwaresupport") || name.Contains("alienware"))
                {
                    analysis["dellCpu"] = (long)analysis["dellCpu"] + cpu;
                    if ((long)analysis["dellCpu"] >= 5)
                    {
                        analysis["dellHogging"] = true;
                        analysis["hasIssues"] = true;
                    }
                }
                else if (name.Contains("tiworker") || name.Contains("trustedinstaller") || name.Contains("msmpeng") || name.Contains("mscorsvw"))
                {
                    analysis["windowsUpdateCpu"] = (long)analysis["windowsUpdateCpu"] + cpu;
                    if ((long)analysis["windowsUpdateCpu"] >= 8)
                    {
                        analysis["windowsUpdateHogging"] = true;
                        analysis["hasIssues"] = true;
                    }
                }
                else if (cpu >= 10 && name != "gswitcher" && name != "explorer" && name != "dwm" && name != "system" && name != "idle")
                {
                    if (cpu > (long)analysis["rogueProcessCpu"])
                    {
                        analysis["rogueHogging"] = true;
                        analysis["rogueProcessName"] = proc["name"].ToString();
                        analysis["rogueProcessPid"] = pid;
                        analysis["rogueProcessCpu"] = cpu;
                        analysis["hasIssues"] = true;
                    }
                }
            }

            if ((bool)analysis["dellHogging"])
            {
                suggestions.Add("Dell/Alienware background services are consuming " + analysis["dellCpu"] + "% CPU. Restarting Dell thermal services can reset this loop.");
            }
            if ((bool)analysis["windowsUpdateHogging"])
            {
                suggestions.Add("Windows Update, Installer, or Defender (" + analysis["windowsUpdateCpu"] + "% CPU) is running in the background. It is recommended to pause updates or let them finish.");
            }
            if ((bool)analysis["rogueHogging"])
            {
                suggestions.Add("Rogue background process '" + analysis["rogueProcessName"] + "' (PID " + analysis["rogueProcessPid"] + ") is consuming " + analysis["rogueProcessCpu"] + "% CPU. You can restrict its CPU cores or terminate it.");
            }

            analysis["suggestions"] = suggestions;
            return analysis;
        }

        public static OperationResult ExecuteThermalFix(string action, int pid)
        {
            action = (action ?? "").Trim().ToLower();
            AppLogger.Write("ExecuteThermalFix requested: " + action + " for pid " + pid);

            try
            {
                if (action == "restart-wmi")
                {
                    var result = RunProcess("cmd.exe", "/c net stop winmgmt /y && net start winmgmt");
                    if (result.ExitCode == 0)
                    {
                        return OperationResult.Ok("Successfully restarted Windows Management Instrumentation service.", "thermal-fix");
                    }
                    return OperationResult.Fail("Failed to restart WMI service. Exit code: " + result.ExitCode, controlPath: "thermal-fix");
                }
                else if (action == "restart-dell")
                {
                    var stopRes = RunProcess("cmd.exe", "/c net stop AWCCService");
                    var startRes = RunProcess("cmd.exe", "/c net start AWCCService");
                    return OperationResult.Ok("Dell thermal services restart commanded.", "thermal-fix");
                }
                else if (action == "terminate-process" && pid > 0)
                {
                    using (var proc = Process.GetProcessById(pid))
                    {
                        var name = proc.ProcessName;
                        proc.Kill();
                        return OperationResult.Ok("Successfully terminated process '" + name + "' (PID " + pid + ").", "thermal-fix");
                    }
                }
                else if (action == "restrict-process" && pid > 0)
                {
                    using (var proc = Process.GetProcessById(pid))
                    {
                        proc.ProcessorAffinity = (IntPtr)1;
                        proc.PriorityClass = ProcessPriorityClass.Idle;
                        return OperationResult.Ok("Successfully restricted process '" + proc.ProcessName + "' (PID " + pid + ") to Core 0 with Idle priority.", "thermal-fix");
                    }
                }
                
                return OperationResult.Fail("Unknown thermal fix action: " + action, controlPath: "thermal-fix");
            }
            catch (Exception ex)
            {
                AppLogger.Write("ExecuteThermalFix failed for " + action + ": " + ex.Message);
                return OperationResult.Fail("Thermal fix failed: " + ex.Message, ex.ToString(), "thermal-fix");
            }
        }

        private static string CleanProcessName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Unknown";
            }

            var hashIndex = name.LastIndexOf('#');
            if (hashIndex > 0)
            {
                name = name.Substring(0, hashIndex);
            }

            return name.Trim();
        }

        private static ProcessResult RunPowerCfg(string arguments)
        {
            if (string.IsNullOrWhiteSpace(PowerCfgPath) || !File.Exists(PowerCfgPath))
            {
                return new ProcessResult
                {
                    ExitCode = 2,
                    StandardOutput = string.Empty,
                    ErrorOutput = "powercfg.exe was not found."
                };
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = PowerCfgPath,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                var standardOutput = process.StandardOutput.ReadToEnd();
                var errorOutput = process.StandardError.ReadToEnd();
                process.WaitForExit();

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = standardOutput,
                    ErrorOutput = errorOutput
                };
            }
        }

        private static ProcessResult RunProcess(string fileName, string arguments)
        {
            if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
            {
                return new ProcessResult
                {
                    ExitCode = 2,
                    StandardOutput = string.Empty,
                    ErrorOutput = "Required executable was not found: " + (fileName ?? "<null>")
                };
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                var standardOutput = process.StandardOutput.ReadToEnd();
                var errorOutput = process.StandardError.ReadToEnd();
                process.WaitForExit();

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = standardOutput,
                    ErrorOutput = errorOutput
                };
            }
        }

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; }
            public string ErrorOutput { get; set; }
        }
    }
}
