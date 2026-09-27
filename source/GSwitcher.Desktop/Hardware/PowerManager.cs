using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using GSwitcher.Models;
using GSwitcher.Services;

namespace GSwitcher.Backend
{
    public static class PowerManager
    {
        private const string ControlPath = "powercfg";
        private static readonly string PowerCfgPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "powercfg.exe");

        // Standard Windows Power Plan GUIDs
        private const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

        // Processor power management subgroup and setting GUIDs
        private const string ProcessorSubgroupGuid = "54533251-82be-4824-96c1-47b60b740d00";
        private const string PerfBoostModeGuid = "be337238-0d82-4146-a960-4f3749d470c7"; // Boost Mode
        private const string ProcThrottleMinGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c"; // Min Processor State
        private const string PerfEppGuid = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";      // P-Core EPP
        private const string PerfEpp1Guid = "36687f9e-e3a5-4dbf-b1dc-15eb381c6864";     // E-Core EPP
        private const string PerfIncPolGuid = "465e1f50-b610-473a-ab58-00d1077dc418";   // Performance increase policy
        private const string MaxFreqGuid = "75b0ae3f-bce0-45a7-8c89-c9611c25e100";      // P-Core Max Freq
        private const string MaxFreq1Guid = "75b0ae3f-bce0-45a7-8c89-c9611c25e101";     // E-Core Max Freq
        
        // "Tested Power Plan" GUIDs
        private const string TestedUltraGuid = "d1676300-e139-4070-c410-b0057cabbedd"; // Dell G16 Ultimate
        private const string TestedCoolGuid = "9387c14e-e380-40e8-fa47-54261569b5c2";
        private const string TestedEcoGuid = "a1d8213b-15f1-43e5-839b-e85c57a2f428";

        private static readonly Dictionary<string, string> PlanGuids = new Dictionary<string, string>
        {
            { "tested-ultra", "56d20d10-0638-4356-a915-78ad7abedad3" }, // Ultra Performance
            { "daily-cool", "7477293b-38bb-49c5-8447-201c61b1b287" }, // Daily Driver (Cool & Quiet)
            { "tested-cool", TestedCoolGuid },
            { "tested-eco", "4dcb9003-9345-4ef6-87f2-54f1144c2d6c" } // Max Battery (Extreme Throttle)
        };

        private static readonly Dictionary<string, string> LegacyPlanGuids = new Dictionary<string, string>
        {
            { "tested-ultra", TestedUltraGuid },
            { "daily-cool", "9343ff3a-3212-4d17-b33f-ffa606bac492" },
            { "tested-cool", TestedCoolGuid },
            { "tested-eco", TestedEcoGuid }
        };

        public static string NormalizeMode(string mode)
        {
            mode = (mode ?? string.Empty).Trim().ToLowerInvariant();

            switch (mode)
            {
                // Map legacy names to new tested plans for backward compatibility
                case "ultra":
                    return "tested-ultra";
                case "cool":
                    return "tested-cool";
                case "daily":
                case "daily-cool":
                    return "daily-cool";
                case "eco":
                case "battery":
                    return "tested-eco";

                // Allow direct access to new plans
                case "tested-ultra":
                case "tested-cool":
                case "tested-eco":
                    return mode;
                    
                default:
                    return "other";
            }
        }

        public static OperationResult Apply(string mode)
        {
            mode = NormalizeMode(mode);
            if (!PlanGuids.ContainsKey(mode))
            {
                return OperationResult.Fail("Unknown power mode.", controlPath: ControlPath, requestedMode: mode);
            }

            var planGuid = GetGuidForMode(mode);
            if (planGuid == null)
            {
                return OperationResult.Fail(
                    "The " + DescribeMode(mode) + " power plan is not installed.",
                    "Neither the current nor the legacy plan GUID was found.",
                    ControlPath,
                    mode,
                    verifiedMode: DetectActiveMode());
            }

            // Switching plans must not rewrite a user's existing processor settings.
            var result = RunPowerCfg("/setactive " + planGuid);
            if (result.ExitCode != 0)
            {
                return OperationResult.Fail("Power plan switch failed: " + CleanError(result.ErrorOutput), result.ErrorOutput, ControlPath, mode, DetectActiveMode());
            }

            var verifiedMode = DetectActiveMode();
            if (verifiedMode != mode)
            {
                return OperationResult.Fail("Power plan change could not be verified.", "Expected " + mode + " but detected " + verifiedMode + ".", ControlPath, mode, verifiedMode);
            }
            return OperationResult.Ok("Power mode verified: " + DescribeMode(mode) + ".", ControlPath, mode, verifiedMode);
        }

        public static OperationResult ApplyFast(string mode)
        {
            return Apply(mode);
        }

        public static OperationResult EnsureRequiredPlans()
        {
            try
            {
                foreach (var mode in new[] { "tested-ultra", "daily-cool", "tested-eco" })
                {
                    if (GetGuidForMode(mode) == null)
                    {
                        return OperationResult.Fail(
                            string.Format("Required plan for {0} is missing. No plan was imported or recreated.", mode),
                            controlPath: ControlPath);
                    }
                }
                
                return OperationResult.Ok(
                    "Healthy. Ultra, Daily, and Eco power plans are installed.", ControlPath);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(
                    "Power plan provisioning failed: " + ex.Message,
                    ex.ToString(),
                    ControlPath);
            }
        }

        public static OperationResult ApplyProfile(PowerPlanProfile profile, string planGuid)
        {
            var warnings = new List<string>();
            string error;

            if (!TrySetValue("setacvalueindex", planGuid, ProcessorSubgroupGuid, ProcThrottleMinGuid, profile.MinProcessorState, out error))
                warnings.Add(string.Format("AC Min P-Core State failed: {0}", error));
            if (!TrySetValue("setdcvalueindex", planGuid, ProcessorSubgroupGuid, ProcThrottleMinGuid, profile.MinProcessorState, out error))
                warnings.Add(string.Format("DC Min P-Core State failed: {0}", error));
            
            if (profile.MaxPCoreFrequency.HasValue && !TrySetValue("setacvalueindex", planGuid, ProcessorSubgroupGuid, MaxFreqGuid, profile.MaxPCoreFrequency.Value, out error))
                warnings.Add(string.Format("AC Max P-Core Freq failed: {0}", error));
            if (profile.MaxPCoreFrequency.HasValue && !TrySetValue("setdcvalueindex", planGuid, ProcessorSubgroupGuid, MaxFreqGuid, profile.MaxPCoreFrequency.Value, out error))
                warnings.Add(string.Format("DC Max P-Core Freq failed: {0}", error));

            if (profile.MaxECoreFrequency.HasValue && !TrySetValue("setacvalueindex", planGuid, ProcessorSubgroupGuid, MaxFreq1Guid, profile.MaxECoreFrequency.Value, out error))
                warnings.Add(string.Format("AC Max E-Core Freq failed: {0}", error));
            if (profile.MaxECoreFrequency.HasValue && !TrySetValue("setdcvalueindex", planGuid, ProcessorSubgroupGuid, MaxFreq1Guid, profile.MaxECoreFrequency.Value, out error))
                warnings.Add(string.Format("DC Max E-Core Freq failed: {0}", error));

            if (!TrySetValue("setacvalueindex", planGuid, ProcessorSubgroupGuid, PerfBoostModeGuid, (int)profile.BoostMode, out error))
                warnings.Add(string.Format("AC boost mode failed: {0}", error));
            if (!TrySetValue("setdcvalueindex", planGuid, ProcessorSubgroupGuid, PerfBoostModeGuid, (int)profile.BoostMode, out error))
                warnings.Add(string.Format("DC boost mode failed: {0}", error));

            if (!TrySetValue("setacvalueindex", planGuid, ProcessorSubgroupGuid, PerfIncPolGuid, 0, out error))
                warnings.Add(string.Format("AC performance increase policy failed: {0}", error));

            if (!TrySetValue("setacvalueindex", planGuid, ProcessorSubgroupGuid, PerfEppGuid, profile.Epp, out error))
                warnings.Add(string.Format("AC P-Core EPP failed: {0}", error));
            if (!TrySetValue("setdcvalueindex", planGuid, ProcessorSubgroupGuid, PerfEppGuid, profile.Epp, out error))
                warnings.Add(string.Format("DC P-Core EPP failed: {0}", error));
            
            if (!TrySetValue("setacvalueindex", planGuid, ProcessorSubgroupGuid, PerfEpp1Guid, Math.Min(profile.Epp + 10, 100), out error))
                warnings.Add(string.Format("AC E-Core EPP failed: {0}", error));
            if (!TrySetValue("setdcvalueindex", planGuid, ProcessorSubgroupGuid, PerfEpp1Guid, Math.Min(profile.Epp + 10, 100), out error))
                warnings.Add(string.Format("DC E-Core EPP failed: {0}", error));

            var result = RunPowerCfg("/setactive " + planGuid);
            if (result.ExitCode != 0)
            {
                return OperationResult.Fail("Power plan switch failed: " + CleanError(result.ErrorOutput), result.ErrorOutput, ControlPath, profile.Id);
            }

            var verifiedMode = DetectActiveMode();
            if (!string.Equals(verifiedMode, profile.Id, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Fail("Power plan change could not be verified.", string.Format("Expected {0} but detected {1}.", profile.Id, verifiedMode), ControlPath, profile.Id, verifiedMode, verified: false);
            }

            var message = "Power mode verified: " + profile.Name + ".";
            return warnings.Count > 0 
                ? OperationResult.Ok(message, ControlPath, profile.Id, verifiedMode, true, warning: string.Join(" | ", warnings)) 
                : OperationResult.Ok(message, ControlPath, profile.Id, verifiedMode, true);
        }

        public static string DetectActiveMode()
        {
            try
            {
                var activeGuid = GetActiveSchemeGuid();
                foreach (var planEntry in PlanGuids)
                {
                    if (string.Equals(activeGuid, planEntry.Value, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(activeGuid, LegacyPlanGuids[planEntry.Key], StringComparison.OrdinalIgnoreCase))
                    {
                        return planEntry.Key;
                    }
                }
            }
            catch {}
            return "other";
        }

        public static string GetActiveSchemeGuid()
        {
            try
            {
                var result = RunPowerCfg("/getactivescheme");
                var text = string.Join(" ", result.StandardOutput ?? string.Empty, result.ErrorOutput ?? string.Empty);
                var match = Regex.Match(text, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        public static string DescribeMode(string mode)
        {
            switch (NormalizeMode(mode))
            {
                case "tested-ultra": return "Tested Ultra";
                case "tested-cool": return "Tested Cool";
                case "daily-cool": return "GSwitcher Daily Cool v2";
                case "tested-eco": return "Tested Eco";
                default: return "Other";
            }
        }

        public static string GetGuidForMode(string mode)
        {
            var normalizedMode = NormalizeMode(mode);
            if (!PlanGuids.ContainsKey(normalizedMode)) return null;
            if (SchemeExists(PlanGuids[normalizedMode])) return PlanGuids[normalizedMode];
            return SchemeExists(LegacyPlanGuids[normalizedMode]) ? LegacyPlanGuids[normalizedMode] : null;
        }

        public static OperationResult RestoreActiveScheme(string schemeGuid)
        {
            if (!Regex.IsMatch(schemeGuid ?? string.Empty, @"^[0-9a-fA-F-]{36}$"))
            {
                return OperationResult.Fail("The original power scheme GUID is invalid.", controlPath: ControlPath);
            }

            var result = RunPowerCfg("/setactive " + schemeGuid);
            if (result.ExitCode != 0)
            {
                return OperationResult.Fail("Power scheme restore failed: " + CleanError(result.ErrorOutput), result.ErrorOutput, ControlPath);
            }

            return OperationResult.Ok("Original power scheme restored.", ControlPath, verifiedMode: DetectActiveMode());
        }

        private static bool TrySetValue(string command, string scheme, string subgroup, string setting, int value, out string error)
        {
            var result = RunPowerCfg(string.Format("/{0} {1} {2} {3} {4}", command, scheme, subgroup, setting, value));
            if (result.ExitCode == 0)
            {
                error = null;
                return true;
            }
            error = CleanError(result.ErrorOutput);
            return false;
        }
        
        private static string UnhideProcessorAttributes()
        {
            var guidsToUnhide = new[] { PerfBoostModeGuid, ProcThrottleMinGuid, PerfEppGuid, PerfEpp1Guid, MaxFreqGuid, MaxFreq1Guid };
            var warnings = new List<string>();

            foreach(var guid in guidsToUnhide)
            {
                var result = RunPowerCfg(string.Format("/attributes {0} {1} -ATTRIB_HIDE", ProcessorSubgroupGuid, guid));
                if (result.ExitCode != 0)
                {
                    warnings.Add(string.Format("Failed to unhide {0}", guid));
                }
            }
            return warnings.Count > 0 ? "Some power attributes could not be unhidden." : null;
        }

        private static bool SchemeExists(string schemeGuid)
        {
            if (string.IsNullOrWhiteSpace(schemeGuid)) return false;
            var result = RunPowerCfg("/query " + schemeGuid);
            return result.ExitCode == 0;
        }

        private static string CleanError(string text)
        {
            var cleaned = (text ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "Unknown error." : cleaned;
        }

        private static ProcessResult RunPowerCfg(string arguments)
        {
            return RunProcess(PowerCfgPath, arguments);
        }
        
        private static ProcessResult RunProcess(string fileName, string arguments)
        {
            if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
            {
                return new ProcessResult { ExitCode = -1, ErrorOutput = "Executable not found: " + (fileName ?? "<null>") };
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
                return new ProcessResult { ExitCode = process.ExitCode, StandardOutput = standardOutput, ErrorOutput = errorOutput };
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
