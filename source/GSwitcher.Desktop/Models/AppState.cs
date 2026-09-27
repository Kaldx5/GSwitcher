using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace GSwitcher.Models
{
    [DataContract]
    public sealed class AppState
    {
        [DataMember]
        public string Theme { get; set; }

        [DataMember]
        public string PowerMode { get; set; }

        [DataMember]
        public string GpuMode { get; set; }

        [DataMember]
        public string GpuRouteMode { get; set; }

        [DataMember]
        public string GpuPreferenceMode { get; set; }

        [DataMember]
        public string GpuPanelMode { get; set; }

        [DataMember]
        public string LastStatus { get; set; }

        [DataMember]
        public bool IsBusy { get; set; }

        [DataMember]
        public string BusyLabel { get; set; }

        [DataMember]
        public DateTime LastUpdatedUtc { get; set; }

        [DataMember]
        public string PowerPlanHealth { get; set; }

        [DataMember]
        public string PowerPlanHealthState { get; set; }

        [DataMember]
        public string GpuControlPath { get; set; }

        [DataMember]
        public string GpuRouteControlPath { get; set; }

        [DataMember]
        public string GpuPreferenceControlPath { get; set; }

        [DataMember]
        public string GpuPanelControlPath { get; set; }

        [DataMember]
        public string GpuRouteSource { get; set; }

        [DataMember]
        public string GpuRouteEvidence { get; set; }

        [DataMember]
        public string GpuPanelEvidence { get; set; }

        [DataMember]
        public string GpuPendingRouteMode { get; set; }

        [DataMember]
        public bool RequiresRestart { get; set; }

        [DataMember]
        public string[] AvailableGpuModes { get; set; }

        [DataMember]
        public string[] AvailableGpuRouteModes { get; set; }

        [DataMember]
        public string[] AvailableGpuPreferenceModes { get; set; }

        [DataMember]
        public bool IsPinned { get; set; }

        [DataMember]
        public bool HelperInstalled { get; set; }

        [DataMember]
        public string EvidenceSessionPath { get; set; }

        [DataMember]
        public string LastEvidenceCapturePath { get; set; }

        [DataMember]
        public bool BrightnessSupported { get; set; }

        [DataMember]
        public int BrightnessLevel { get; set; }

        [DataMember]
        public string DisplayWarning { get; set; }

        [DataMember]
        public bool AutoTuningEnabled { get; set; }

        [DataMember]
        public int TunedPCoreMaxFreq_Ultra { get; set; }

        [DataMember]
        public int TunedECoreMaxFreq_Ultra { get; set; }

        [DataMember]
        public int TunedEpp_Ultra { get; set; }

        [DataMember]
        public int TunedPCoreMaxFreq_Cool { get; set; }

        [DataMember]
        public int TunedECoreMaxFreq_Cool { get; set; }

        [DataMember]
        public int TunedEpp_Cool { get; set; }

        public static AppState CreateDefault()
        {
            return new AppState
            {
                Theme = "dark",
                PowerMode = "other",
                GpuMode = "unknown",
                GpuRouteMode = "unknown",
                GpuPreferenceMode = "unknown",
                GpuPanelMode = "unknown",
                LastStatus = "Ready.",
                IsBusy = false,
                BusyLabel = string.Empty,
                LastUpdatedUtc = DateTime.UtcNow,
                PowerPlanHealth = "Checking power plans...",
                PowerPlanHealthState = "checking",
                GpuControlPath = "unavailable",
                GpuRouteControlPath = "unavailable",
                GpuPreferenceControlPath = "unavailable",
                GpuPanelControlPath = "unavailable",
                GpuRouteSource = "unknown",
                GpuRouteEvidence = "Waiting for GPU route verification...",
                GpuPanelEvidence = "Waiting for NVIDIA display-mode verification...",
                GpuPendingRouteMode = "unknown",
                RequiresRestart = false,
                AvailableGpuModes = new string[0],
                AvailableGpuRouteModes = new string[0],
                AvailableGpuPreferenceModes = new string[0],
                IsPinned = false,
                HelperInstalled = false,
                EvidenceSessionPath = string.Empty,
                LastEvidenceCapturePath = string.Empty,
                BrightnessSupported = false,
                BrightnessLevel = -1,
                DisplayWarning = "Display recovery status is waiting for more evidence.",
                AutoTuningEnabled = false,
                TunedPCoreMaxFreq_Ultra = 0,
                TunedECoreMaxFreq_Ultra = 0,
                TunedEpp_Ultra = 0,
                TunedPCoreMaxFreq_Cool = 0,
                TunedECoreMaxFreq_Cool = 0,
                TunedEpp_Cool = 0
            };
        }

        public static AppState Normalize(AppState state)
        {
            if (state == null)
            {
                return CreateDefault();
            }

            state.Theme = NormalizeTheme(state.Theme);
            state.PowerMode = NormalizePowerMode(state.PowerMode);
            state.GpuMode = NormalizeGpuMode(state.GpuMode);
            state.GpuControlPath = NormalizeControlPath(state.GpuControlPath);
            state.PowerPlanHealthState = NormalizePowerPlanHealthState(state.PowerPlanHealthState);
            state.GpuRouteMode = NormalizeGpuRouteMode(state.GpuRouteMode);
            state.GpuPreferenceMode = NormalizeGpuPreferenceMode(state.GpuPreferenceMode);
            state.GpuPanelMode = NormalizeGpuPanelMode(state.GpuPanelMode);
            state.GpuRouteControlPath = NormalizeGpuRouteControlPath(state.GpuRouteControlPath);
            state.GpuPreferenceControlPath = NormalizeGpuPreferenceControlPath(state.GpuPreferenceControlPath);
            state.GpuPanelControlPath = NormalizeGpuPanelControlPath(state.GpuPanelControlPath);
            state.GpuRouteSource = NormalizeGpuRouteSource(state.GpuRouteSource);
            state.GpuPendingRouteMode = NormalizeGpuRouteMode(state.GpuPendingRouteMode);
            state.AvailableGpuRouteModes = NormalizeAvailableGpuRouteModes(state.AvailableGpuRouteModes);
            state.AvailableGpuPreferenceModes = NormalizeAvailableGpuPreferenceModes(state.AvailableGpuPreferenceModes);
            state.EvidenceSessionPath = state.EvidenceSessionPath ?? string.Empty;
            state.LastEvidenceCapturePath = state.LastEvidenceCapturePath ?? string.Empty;
            state.BrightnessLevel = NormalizeBrightnessLevel(state.BrightnessLevel);
            state.DisplayWarning = string.IsNullOrWhiteSpace(state.DisplayWarning)
                ? "Display recovery status is waiting for more evidence."
                : state.DisplayWarning.Trim();

            state.TunedPCoreMaxFreq_Ultra = Math.Max(0, state.TunedPCoreMaxFreq_Ultra);
            state.TunedECoreMaxFreq_Ultra = Math.Max(0, state.TunedECoreMaxFreq_Ultra);
            state.TunedEpp_Ultra = Math.Max(0, state.TunedEpp_Ultra);
            state.TunedPCoreMaxFreq_Cool = Math.Max(0, state.TunedPCoreMaxFreq_Cool);
            state.TunedECoreMaxFreq_Cool = Math.Max(0, state.TunedECoreMaxFreq_Cool);
            state.TunedEpp_Cool = Math.Max(0, state.TunedEpp_Cool);

            if (string.IsNullOrWhiteSpace(state.LastStatus))
            {
                state.LastStatus = "Ready.";
            }

            state.BusyLabel = state.IsBusy && !string.IsNullOrWhiteSpace(state.BusyLabel)
                ? state.BusyLabel.Trim()
                : string.Empty;

            if (state.LastUpdatedUtc == default(DateTime))
            {
                state.LastUpdatedUtc = DateTime.UtcNow;
            }

            if (string.IsNullOrWhiteSpace(state.PowerPlanHealth))
            {
                state.PowerPlanHealth = "Checking power plans...";
            }

            if (state.GpuRouteControlPath == "unavailable" && state.GpuControlPath == "awcc-mux")
            {
                state.GpuRouteControlPath = "awcc-mux";
            }
            else if (state.GpuRouteControlPath == "unavailable" && state.GpuControlPath == "awcc-mux-locked")
            {
                state.GpuRouteControlPath = "awcc-mux-locked";
            }
            else if (state.GpuRouteControlPath == "unavailable" && state.GpuControlPath == "nvidia-panel")
            {
                state.GpuRouteControlPath = "nvidia-panel";
            }

            if (state.GpuPreferenceControlPath == "unavailable" && state.GpuControlPath == "nvidia-drs")
            {
                state.GpuPreferenceControlPath = "nvidia-drs";
            }

            if (state.GpuPanelControlPath == "unavailable" && state.GpuControlPath == "nvidia-panel")
            {
                state.GpuPanelControlPath = "nvidia-panel";
            }

            if (state.GpuRouteMode == "unknown")
            {
                var legacyRouteMode = NormalizeGpuRouteMode(state.GpuMode);
                if (legacyRouteMode != "unknown")
                {
                    state.GpuRouteMode = legacyRouteMode;
                }
            }

            if (state.GpuPreferenceMode == "unknown" && state.GpuPreferenceControlPath == "nvidia-drs")
            {
                state.GpuPreferenceMode = NormalizeGpuPreferenceMode(state.GpuMode);
            }

            if (string.IsNullOrWhiteSpace(state.GpuRouteEvidence))
            {
                state.GpuRouteEvidence = "Waiting for GPU route verification...";
            }

            if (string.IsNullOrWhiteSpace(state.GpuPanelEvidence))
            {
                state.GpuPanelEvidence = "Waiting for NVIDIA display-mode verification...";
            }

            if (state.AvailableGpuRouteModes.Length == 0 &&
                (state.GpuRouteControlPath == "awcc-mux" ||
                 state.GpuRouteControlPath == "awcc-mux-locked" ||
                 state.GpuRouteControlPath == "nvidia-panel"))
            {
                state.AvailableGpuRouteModes = new[] { "dgpu", "hybrid" };
            }

            if (state.AvailableGpuPreferenceModes.Length == 0 && state.GpuPreferenceControlPath == "nvidia-drs")
            {
                state.AvailableGpuPreferenceModes = new[] { "auto", "dgpu", "hybrid" };
            }

            if (state.RequiresRestart && state.GpuPendingRouteMode == "unknown")
            {
                state.GpuPendingRouteMode = state.GpuRouteMode;
            }

            state.GpuMode = state.GpuRouteMode != "unknown" ? state.GpuRouteMode : state.GpuPreferenceMode;
            state.GpuControlPath = state.GpuRouteControlPath != "unavailable"
                ? state.GpuRouteControlPath
                : state.GpuPreferenceControlPath;
            state.AvailableGpuModes = state.AvailableGpuRouteModes.Length > 0
                ? state.AvailableGpuRouteModes
                : state.AvailableGpuPreferenceModes;

            return state;
        }

        public AppState Clone()
        {
            return new AppState
            {
                Theme = Theme,
                PowerMode = PowerMode,
                GpuMode = GpuMode,
                GpuRouteMode = GpuRouteMode,
                GpuPreferenceMode = GpuPreferenceMode,
                GpuPanelMode = GpuPanelMode,
                LastStatus = LastStatus,
                IsBusy = IsBusy,
                BusyLabel = BusyLabel,
                LastUpdatedUtc = LastUpdatedUtc,
                PowerPlanHealth = PowerPlanHealth,
                PowerPlanHealthState = PowerPlanHealthState,
                GpuControlPath = GpuControlPath,
                GpuRouteControlPath = GpuRouteControlPath,
                GpuPreferenceControlPath = GpuPreferenceControlPath,
                GpuPanelControlPath = GpuPanelControlPath,
                GpuRouteSource = GpuRouteSource,
                GpuRouteEvidence = GpuRouteEvidence,
                GpuPanelEvidence = GpuPanelEvidence,
                GpuPendingRouteMode = GpuPendingRouteMode,
                RequiresRestart = RequiresRestart,
                AvailableGpuModes = CloneArray(AvailableGpuModes),
                AvailableGpuRouteModes = CloneArray(AvailableGpuRouteModes),
                AvailableGpuPreferenceModes = CloneArray(AvailableGpuPreferenceModes),
                IsPinned = IsPinned,
                HelperInstalled = HelperInstalled,
                EvidenceSessionPath = EvidenceSessionPath,
                LastEvidenceCapturePath = LastEvidenceCapturePath,
                BrightnessSupported = BrightnessSupported,
                BrightnessLevel = BrightnessLevel,
                DisplayWarning = DisplayWarning,
                AutoTuningEnabled = AutoTuningEnabled,
                TunedPCoreMaxFreq_Ultra = TunedPCoreMaxFreq_Ultra,
                TunedECoreMaxFreq_Ultra = TunedECoreMaxFreq_Ultra,
                TunedEpp_Ultra = TunedEpp_Ultra,
                TunedPCoreMaxFreq_Cool = TunedPCoreMaxFreq_Cool,
                TunedECoreMaxFreq_Cool = TunedECoreMaxFreq_Cool,
                TunedEpp_Cool = TunedEpp_Cool
            };
        }

        private static string NormalizeTheme(string theme)
        {
            return string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        }

        private static string NormalizePowerMode(string mode)
        {
            mode = (mode ?? string.Empty).Trim().ToLowerInvariant();

            switch (mode)
            {
                case "ultra":
                case "tested-ultra":
                    return "tested-ultra";
                case "daily":
                case "daily-cool":
                    return "daily-cool";
                case "battery":
                case "eco":
                case "tested-eco":
                    return "tested-eco";
                case "tested-cool":
                    return "tested-cool";
                default:
                    return "other";
            }
        }

        private static string NormalizeGpuMode(string mode)
        {
            mode = (mode ?? string.Empty).Trim().ToLowerInvariant();

            switch (mode)
            {
                case "nvidia":
                case "rtx":
                case "discrete":
                case "dgpu":
                    return "dgpu";
                case "integrated":
                case "intel":
                case "hybrid":
                    return "hybrid";
                case "auto":
                    return "auto";
                default:
                    return "unknown";
            }
        }

        private static string NormalizeGpuRouteMode(string mode)
        {
            mode = NormalizeGpuMode(mode);
            return mode == "dgpu" || mode == "hybrid" ? mode : "unknown";
        }

        private static string NormalizeGpuPreferenceMode(string mode)
        {
            return NormalizeGpuMode(mode);
        }

        private static string NormalizeGpuPanelMode(string mode)
        {
            return NormalizeGpuMode(mode);
        }

        private static string NormalizeControlPath(string controlPath)
        {
            controlPath = (controlPath ?? string.Empty).Trim().ToLowerInvariant();

            switch (controlPath)
            {
                case "awcc-mux":
                case "awcc-mux-locked":
                case "awcc-mux-rejected":
                case "nvidia-panel":
                case "nvidia-drs":
                    return controlPath;
                default:
                    return "unavailable";
            }
        }

        private static string NormalizeGpuRouteControlPath(string controlPath)
        {
            controlPath = (controlPath ?? string.Empty).Trim().ToLowerInvariant();

            switch (controlPath)
            {
                case "awcc-mux":
                case "awcc-mux-locked":
                case "awcc-mux-rejected":
                case "nvidia-panel":
                    return controlPath;
                default:
                    return "unavailable";
            }
        }

        private static string NormalizeGpuPreferenceControlPath(string controlPath)
        {
            controlPath = (controlPath ?? string.Empty).Trim().ToLowerInvariant();
            return controlPath == "nvidia-drs" ? controlPath : "unavailable";
        }

        private static string NormalizeGpuPanelControlPath(string controlPath)
        {
            controlPath = (controlPath ?? string.Empty).Trim().ToLowerInvariant();
            return controlPath == "nvidia-panel" ? controlPath : "unavailable";
        }

        private static string NormalizeGpuRouteSource(string source)
        {
            source = (source ?? string.Empty).Trim().ToLowerInvariant();

            switch (source)
            {
                case "awcc-query":
                case "display-topology":
                case "nvidia-panel-status":
                case "nvidia-panel-selection":
                    return source;
                default:
                    return "unknown";
            }
        }

        private static string NormalizePowerPlanHealthState(string healthState)
        {
            healthState = (healthState ?? string.Empty).Trim().ToLowerInvariant();

            switch (healthState)
            {
                case "healthy":
                case "self-healed":
                case "error":
                    return healthState;
                default:
                    return "checking";
            }
        }

        private static int NormalizeBrightnessLevel(int brightnessLevel)
        {
            if (brightnessLevel < 0)
            {
                return -1;
            }

            if (brightnessLevel > 100)
            {
                return 100;
            }

            return brightnessLevel;
        }

        private static string[] NormalizeAvailableGpuModes(string[] modes)
        {
            return NormalizeAvailableGpuPreferenceModes(modes);
        }

        private static string[] NormalizeAvailableGpuRouteModes(string[] modes)
        {
            if (modes == null || modes.Length == 0)
            {
                return new string[0];
            }

            var normalized = new List<string>();
            for (var index = 0; index < modes.Length; index++)
            {
                var mode = NormalizeGpuRouteMode(modes[index]);
                if (mode == "unknown")
                {
                    continue;
                }

                if (!normalized.Contains(mode))
                {
                    normalized.Add(mode);
                }
            }

            return normalized.ToArray();
        }

        private static string[] NormalizeAvailableGpuPreferenceModes(string[] modes)
        {
            if (modes == null || modes.Length == 0)
            {
                return new string[0];
            }

            var normalized = new List<string>();
            for (var index = 0; index < modes.Length; index++)
            {
                var mode = NormalizeGpuPreferenceMode(modes[index]);
                if (mode == "unknown")
                {
                    continue;
                }

                if (!normalized.Contains(mode))
                {
                    normalized.Add(mode);
                }
            }

            return normalized.ToArray();
        }

        private static string[] CloneArray(string[] items)
        {
            if (items == null || items.Length == 0)
            {
                return new string[0];
            }

            var clone = new string[items.Length];
            Array.Copy(items, clone, items.Length);
            return clone;
        }
    }
}
