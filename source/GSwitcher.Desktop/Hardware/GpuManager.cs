using System;
using System.Collections.Generic;
using System.Threading;
using GSwitcher.Models;

namespace GSwitcher.Backend
{
    public static class GpuManager
    {
        private static readonly object SyncRoot = new object();
        private const string NativeMuxControlPath = "awcc-mux";
        private const string LockedMuxControlPath = "awcc-mux-locked";
        private const string RejectedMuxControlPath = "awcc-mux-rejected";
        private const string NvidiaPanelControlPath = "nvidia-panel";
        private static IRouteController _routeController;
        private static bool _preferenceControlAvailable;

        public static void RefreshController()
        {
            lock (SyncRoot)
            {
                _preferenceControlAvailable = NvidiaController.IsAvailable();

                var nativeController = new AwccMuxRouteController();
                if (nativeController.IsAvailable)
                {
                    _routeController = nativeController;
                    return;
                }

                if (nativeController.HasMuxInterface && !IsCurrentProcessElevated())
                {
                    _routeController = new BlockedAwccMuxRouteController(nativeController.NativeController);
                    return;
                }

                if (NvidiaPanelController.IsRouteAutomationAvailable())
                {
                    _routeController = new NvidiaPanelRouteController(
                        BuildNativeMuxFallbackNote(nativeController));
                    return;
                }

                if (nativeController.HasMuxInterface)
                {
                    _routeController = new BlockedAwccMuxRouteController(nativeController.NativeController);
                    return;
                }

                _routeController = new PreferenceOnlyRouteController();
                _preferenceControlAvailable = NvidiaController.IsAvailable();
            }
        }

        public static string CurrentControlPath
        {
            get
            {
                EnsureController();
                var routePath = _routeController.ControlPath;
                if (routePath != "unavailable")
                {
                    return routePath;
                }

                return _preferenceControlAvailable ? NvidiaController.ControlPath : "unavailable";
            }
        }

        public static string CurrentRouteControlPath
        {
            get
            {
                EnsureController();
                return _routeController.ControlPath;
            }
        }

        public static string CurrentPreferenceControlPath
        {
            get
            {
                EnsureController();
                return _preferenceControlAvailable ? NvidiaController.ControlPath : "unavailable";
            }
        }

        public static string[] GetAvailableModes()
        {
            var routeModes = GetAvailableRouteModes();
            return routeModes.Length > 0 ? routeModes : GetAvailablePreferenceModes();
        }

        public static string[] GetAvailableRouteModes()
        {
            EnsureController();
            return _routeController.GetAvailableModes();
        }

        public static string[] GetAvailablePreferenceModes()
        {
            EnsureController();
            return _preferenceControlAvailable ? NvidiaController.GetAvailableModes() : new string[0];
        }

        public static GpuRouteSnapshot DetectCurrentRouteSnapshot()
        {
            EnsureController();
            return _routeController.DetectSnapshot();
        }

        public static string DetectCurrentRoute()
        {
            return NormalizeRouteMode(DetectCurrentRouteSnapshot().Mode);
        }

        public static string DetectCurrentPreference()
        {
            EnsureController();
            return _preferenceControlAvailable ? NormalizeMode(NvidiaController.DetectPreference()) : "unknown";
        }

        public static string DetectActiveMode()
        {
            EnsureController();
            return GetAvailableRouteModes().Length > 0
                ? DetectCurrentRoute()
                : DetectCurrentPreference();
        }

        public static OperationResult Apply(string mode)
        {
            mode = NormalizeMode(mode);

            if (mode == "auto")
            {
                return ApplyPreference(mode);
            }

            if (GetAvailableRouteModes().Length > 0)
            {
                return ApplyRoute(mode);
            }

            return ApplyPreference(mode);
        }

        public static OperationResult ApplyRoute(string mode)
        {
            EnsureController();
            mode = NormalizeRouteMode(mode);

            if (!_routeController.SupportsMode(mode))
            {
                return OperationResult.Fail(
                    "Hardware GPU route control for " + DescribeMode(mode) + " is not available on this path.",
                    controlPath: _routeController.ControlPath,
                    requestedMode: mode,
                    warning: DescribeControlPath(_routeController.ControlPath));
            }

            return _routeController.Apply(mode);
        }

        public static OperationResult ApplyNativeRouteOnly(string mode)
        {
            mode = NormalizeRouteMode(mode);
            var nativeController = new AwccMuxRouteController();

            if (nativeController.IsAvailable)
            {
                return nativeController.Apply(mode);
            }

            if (nativeController.HasMuxInterface)
            {
                return nativeController.Apply(mode);
            }

            return OperationResult.Fail(
                "Native Dell MUX control is not available on this machine.",
                controlPath: "unavailable",
                requestedMode: mode);
        }

        public static OperationResult ApplyPreference(string mode)
        {
            EnsureController();
            mode = NormalizeMode(mode);

            if (!_preferenceControlAvailable)
            {
                return OperationResult.Fail(
                    "NVIDIA app preference control is not available on this machine right now.",
                    controlPath: "unavailable",
                    requestedMode: mode);
            }

            return NvidiaController.ApplyPreference(mode);
        }

        public static string NormalizeMode(string mode)
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

        public static string NormalizeRouteMode(string mode)
        {
            mode = NormalizeMode(mode);
            return mode == "dgpu" || mode == "hybrid" ? mode : "unknown";
        }

        public static string DescribeMode(string mode)
        {
            switch (NormalizeRouteMode(mode))
            {
                case "dgpu":
                    return "Dedicated Route";
                case "hybrid":
                    return "Hybrid Route";
                default:
                    return "Unknown";
            }
        }

        public static string DescribePreferenceMode(string mode)
        {
            switch (NormalizeMode(mode))
            {
                case "dgpu":
                    return "RTX Priority";
                case "hybrid":
                    return "Optimus";
                case "auto":
                    return "Automatic";
                default:
                    return "Unknown";
            }
        }

        public static string DescribeControlPath(string controlPath)
        {
            controlPath = (controlPath ?? string.Empty).Trim().ToLowerInvariant();
            switch (controlPath)
            {
                case NativeMuxControlPath:
                    return "Native Dell/AWCC MUX control";
                case LockedMuxControlPath:
                    return "Native Dell/AWCC MUX detected but blocked";
                case RejectedMuxControlPath:
                    return "Native Dell/AWCC MUX rejected by firmware or driver";
                case NvidiaPanelControlPath:
                    return "NVIDIA display-mode control";
                case "nvidia-drs":
                    return "NVIDIA app preference control";
                default:
                    return "GPU control unavailable";
            }
        }

        public static bool IsNativeMuxControlPath(string controlPath)
        {
            controlPath = (controlPath ?? string.Empty).Trim().ToLowerInvariant();
            return controlPath == NativeMuxControlPath ||
                   controlPath == LockedMuxControlPath ||
                   controlPath == RejectedMuxControlPath;
        }

        public static bool IsBlockedMuxControlPath(string controlPath)
        {
            controlPath = (controlPath ?? string.Empty).Trim().ToLowerInvariant();
            return controlPath == LockedMuxControlPath;
        }

        private static void EnsureController()
        {
            if (_routeController == null)
            {
                RefreshController();
            }
        }

        private static bool IsCurrentProcessElevated()
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }

        private static string BuildNativeMuxFallbackNote(AwccMuxRouteController nativeController)
        {
            if (nativeController == null || !nativeController.HasMuxInterface)
            {
                return null;
            }

            var probeError = nativeController.NativeController.ProbeError;
            if (IsCurrentProcessElevated())
            {
                return string.IsNullOrWhiteSpace(probeError)
                    ? "Dell AWCC MUX exists on this BIOS, but the elevated session still could not verify it. NVIDIA display mode is the active fallback path on this Windows installation."
                    : "Dell AWCC MUX exists on this BIOS, but the elevated session still could not verify it. NVIDIA display mode is the active fallback path on this Windows installation. Native probe: " + probeError;
            }

            return string.IsNullOrWhiteSpace(probeError)
                ? "Dell AWCC MUX exists on this BIOS, but this session is not elevated enough to use it directly."
                : "Dell AWCC MUX exists on this BIOS, but this session is not elevated enough to use it directly. Native probe: " + probeError;
        }

        private interface IRouteController
        {
            string ControlPath { get; }
            bool IsAvailable { get; }
            string[] GetAvailableModes();
            bool SupportsMode(string mode);
            GpuRouteSnapshot DetectSnapshot();
            OperationResult Apply(string mode);
        }

        private sealed class AwccMuxRouteController : IRouteController
        {
            private readonly AwccMuxController _controller = new AwccMuxController();

            public AwccMuxController NativeController
            {
                get { return _controller; }
            }

            public bool HasMuxInterface
            {
                get { return _controller.HasMuxInterface; }
            }

            public string ControlPath
            {
                get { return NativeMuxControlPath; }
            }

            public bool IsAvailable
            {
                get { return _controller.IsAvailable; }
            }

            public string[] GetAvailableModes()
            {
                return _controller.GetAvailableModes();
            }

            public bool SupportsMode(string mode)
            {
                mode = NormalizeRouteMode(mode);
                return mode == "dgpu" || mode == "hybrid";
            }

            public GpuRouteSnapshot DetectSnapshot()
            {
                var mode = NormalizeRouteMode(_controller.DetectMode());
                if (mode != "unknown")
                {
                    return GpuRouteSnapshot.Create(
                        mode,
                        "awcc-query",
                        "Verified through the native Dell AWCC MUX state.");
                }

                var topologySnapshot = GpuRouteDetector.Detect();
                if (topologySnapshot.Mode != "unknown")
                {
                    return topologySnapshot;
                }

                return GpuRouteSnapshot.Unknown(
                    "Dell AWCC MUX is available, but the current route could not be verified.");
            }

            public OperationResult Apply(string mode)
            {
                return _controller.Apply(mode);
            }
        }

        private sealed class BlockedAwccMuxRouteController : IRouteController
        {
            private readonly AwccMuxController _controller;

            public BlockedAwccMuxRouteController(AwccMuxController controller)
            {
                _controller = controller ?? new AwccMuxController();
            }

            public string ControlPath
            {
                get { return LockedMuxControlPath; }
            }

            public bool IsAvailable
            {
                get { return true; }
            }

            public string[] GetAvailableModes()
            {
                return _controller.GetAvailableModes();
            }

            public bool SupportsMode(string mode)
            {
                mode = NormalizeRouteMode(mode);
                return mode == "dgpu" || mode == "hybrid";
            }

            public GpuRouteSnapshot DetectSnapshot()
            {
                var snapshot = GpuRouteDetector.Detect();
                if (snapshot.Mode != "unknown")
                {
                    return snapshot;
                }

                return GpuRouteSnapshot.Unknown(
                    "Native Dell MUX exists, but this session is not elevated and Windows could not verify the current route.");
            }

            public OperationResult Apply(string mode)
            {
                mode = NormalizeRouteMode(mode);
                var elevated = IsCurrentProcessElevated();

                if (elevated)
                {
                    return OperationResult.Fail(
                        "Native Dell MUX for " + DescribeMode(mode) +
                        " is present, but Dell WMI rejected the route call even with administrator access.",
                        _controller.ProbeError,
                        RejectedMuxControlPath,
                        mode,
                        warning: "This BIOS/driver path exposes the MUX interface but does not accept the AWCC WMI route call. No software route fallback will be reported as live display ownership unless Windows topology verifies it.");
                }

                return OperationResult.Fail(
                    "Native Dell MUX for " + DescribeMode(mode) +
                    " is present, but this session cannot access it. Relaunch GSwitcher normally and accept the UAC prompt.",
                    _controller.ProbeError,
                    ControlPath,
                    mode,
                    warning: "Native Dell MUX was detected, so GSwitcher will not silently downgrade this machine to preference-only RTX switching.");
            }

            private static bool IsCurrentProcessElevated()
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
        }

        private sealed class NvidiaPanelRouteController : IRouteController
        {
            private const int LiveRouteVerifyTimeoutMs = 20000;
            private const int LiveRouteVerifyStepMs = 400;
            private readonly string _nativeMuxFallbackNote;

            public NvidiaPanelRouteController(string nativeMuxFallbackNote)
            {
                _nativeMuxFallbackNote = string.IsNullOrWhiteSpace(nativeMuxFallbackNote)
                    ? null
                    : nativeMuxFallbackNote.Trim();
            }

            public string ControlPath
            {
                get { return NvidiaPanelControlPath; }
            }

            public bool IsAvailable
            {
                get { return NvidiaPanelController.IsInstalled(); }
            }

            public string[] GetAvailableModes()
            {
                return IsAvailable ? new[] { "dgpu", "hybrid" } : new string[0];
            }

            public bool SupportsMode(string mode)
            {
                mode = NormalizeRouteMode(mode);
                return mode == "dgpu" || mode == "hybrid";
            }

            public GpuRouteSnapshot DetectSnapshot()
            {
                var liveSnapshot = GpuRouteDetector.Detect();
                var panelSnapshot = NvidiaPanelController.DetectSnapshot();

                if (liveSnapshot.Mode == "dgpu" || liveSnapshot.Mode == "hybrid")
                {
                    return GpuRouteSnapshot.Create(
                        liveSnapshot.Mode,
                        liveSnapshot.Source,
                        BuildEvidence(panelSnapshot, liveSnapshot));
                }

                if (panelSnapshot.StatusMode == "dgpu" || panelSnapshot.StatusMode == "hybrid")
                {
                    return GpuRouteSnapshot.Create(
                        panelSnapshot.StatusMode,
                        "nvidia-panel-status",
                        BuildEvidence(panelSnapshot, liveSnapshot));
                }

                if (panelSnapshot.Mode == "dgpu" || panelSnapshot.Mode == "hybrid")
                {
                    return GpuRouteSnapshot.Create(
                        panelSnapshot.Mode,
                        "nvidia-panel-selection",
                        BuildEvidence(panelSnapshot, liveSnapshot));
                }

                return GpuRouteSnapshot.Unknown(
                    BuildEvidence(panelSnapshot, liveSnapshot));
            }

            public OperationResult Apply(string mode)
            {
                mode = NormalizeRouteMode(mode);
                var result = ReconcileLiveRoute(
                    mode,
                    NvidiaPanelController.ApplyDisplayMode(mode),
                    null);

                if (!result.Success && mode == "hybrid")
                {
                    result = TryHybridRecoverySequence(mode, result);
                }
                else if (!result.Success && mode == "dgpu")
                {
                    result = TryDedicatedRecoverySequence(mode, result);
                }

                return AppendNativeMuxNote(result);
            }

            private OperationResult TryHybridRecoverySequence(string requestedMode, OperationResult initialResult)
            {
                requestedMode = NormalizeRouteMode(requestedMode);

                var autoRecovery = ReconcileLiveRoute(
                    requestedMode,
                    NvidiaPanelController.ApplyDisplayMode("auto"),
                    "Automatic Select");

                if (!autoRecovery.Success)
                {
                    string adapterRestartNote;
                    if (NvidiaPanelController.TryRestartNvidiaDisplayAdapter(out adapterRestartNote))
                    {
                        var adapterRecovery = ReconcileLiveRoute(
                            requestedMode,
                            NvidiaPanelController.ApplyDisplayMode("auto"),
                            "NVIDIA adapter restart");

                        adapterRecovery.Warning = CombineWarnings(
                            adapterRecovery.Warning,
                            adapterRestartNote);

                        if (!adapterRecovery.Success)
                        {
                            return adapterRecovery;
                        }

                        var postRestartLock = ReconcileLiveRoute(
                            requestedMode,
                            NvidiaPanelController.ApplyDisplayMode("hybrid"),
                            "Optimus lock");

                        if (postRestartLock.Success)
                        {
                            postRestartLock.Warning = CombineWarnings(
                                postRestartLock.Warning,
                                adapterRestartNote);
                            return postRestartLock;
                        }

                        adapterRecovery.Warning = CombineWarnings(
                            adapterRecovery.Warning,
                            "Automatic Select reached the integrated path after the adapter restart, but the final Optimus lock did not finish cleanly.");

                        return adapterRecovery;
                    }

                    autoRecovery.Warning = CombineWarnings(
                        autoRecovery.Warning,
                        adapterRestartNote);

                    return autoRecovery;
                }

                var lockResult = ReconcileLiveRoute(
                    requestedMode,
                    NvidiaPanelController.ApplyDisplayMode("hybrid"),
                    "Optimus lock");

                if (lockResult.Success)
                {
                    return lockResult;
                }

                autoRecovery.Warning = CombineWarnings(
                    autoRecovery.Warning,
                    "Automatic Select moved Windows back to the integrated route, but the final Optimus lock did not finish cleanly.");

                return autoRecovery;
            }

            private OperationResult TryDedicatedRecoverySequence(string requestedMode, OperationResult initialResult)
            {
                requestedMode = NormalizeRouteMode(requestedMode);

                string adapterRestartNote;
                if (!NvidiaPanelController.TryRestartNvidiaDisplayAdapter(out adapterRestartNote))
                {
                    initialResult.Warning = CombineWarnings(
                        initialResult == null ? null : initialResult.Warning,
                        adapterRestartNote);
                    return initialResult;
                }

                var adapterRecovery = ReconcileLiveRoute(
                    requestedMode,
                    NvidiaPanelController.ApplyDisplayMode("dgpu"),
                    "NVIDIA adapter restart");

                adapterRecovery.Warning = CombineWarnings(
                    adapterRecovery.Warning,
                    adapterRestartNote);

                return adapterRecovery;
            }

            private OperationResult ReconcileLiveRoute(
                string requestedMode,
                OperationResult controllerResult,
                string recoveryMode)
            {
                requestedMode = NormalizeRouteMode(requestedMode);
                var liveSnapshot = WaitForLiveRoute(requestedMode);
                var panelSnapshot = NvidiaPanelController.DetectSnapshot(true);
                var evidence = BuildEvidence(panelSnapshot, liveSnapshot);

                if (liveSnapshot.Mode == requestedMode)
                {
                    var message = "Windows verified " + DescribeMode(requestedMode) +
                        " as the live route.";

                    if (!string.IsNullOrWhiteSpace(recoveryMode))
                    {
                        message += " Recovery step " + recoveryMode + " completed the switch.";
                    }
                    else if (controllerResult != null && !controllerResult.Success)
                    {
                        message += " NVIDIA Control Panel was inconsistent, but the active desktop path did switch.";
                    }
                    else
                    {
                        message += " NVIDIA display mode and Windows desktop ownership now agree.";
                    }

                    return OperationResult.Ok(
                        message,
                        ControlPath,
                        requestedMode: requestedMode,
                        verifiedMode: liveSnapshot.Mode,
                        warning: CombineWarnings(
                            controllerResult == null ? null : controllerResult.Warning,
                            string.IsNullOrWhiteSpace(recoveryMode)
                                ? null
                                : recoveryMode + " was used as a recovery path after the first hybrid request did not finish cleanly."));
                }

                if (liveSnapshot.Mode != "unknown")
                {
                    return OperationResult.Fail(
                        "NVIDIA display mode did not move the live route to " +
                        DescribeMode(requestedMode) + ". Windows still shows " +
                        DescribeMode(liveSnapshot.Mode) + ".",
                        evidence,
                        ControlPath,
                        requestedMode,
                        verifiedMode: liveSnapshot.Mode,
                        warning: CombineWarnings(
                            controllerResult == null ? null : controllerResult.Warning,
                            string.IsNullOrWhiteSpace(recoveryMode)
                                ? null
                                : recoveryMode + " also failed to move the live desktop path."));
                }

                if (controllerResult != null)
                {
                    controllerResult.Warning = CombineWarnings(
                        controllerResult.Warning,
                        "Windows could not independently prove the live route after the request.");
                    return controllerResult;
                }

                return OperationResult.Fail(
                    "NVIDIA display mode request did not finish cleanly and the live route could not be proven.",
                    evidence,
                    ControlPath,
                    requestedMode: requestedMode);
            }

            private GpuRouteSnapshot WaitForLiveRoute(string requestedMode)
            {
                requestedMode = NormalizeRouteMode(requestedMode);
                var deadlineUtc = DateTime.UtcNow.AddMilliseconds(LiveRouteVerifyTimeoutMs);
                GpuRouteSnapshot lastKnown = null;

                while (DateTime.UtcNow < deadlineUtc)
                {
                    var snapshot = GpuRouteDetector.Detect();
                    if (snapshot != null)
                    {
                        lastKnown = snapshot;
                        if (snapshot.Mode == requestedMode)
                        {
                            return snapshot;
                        }
                    }

                    Thread.Sleep(LiveRouteVerifyStepMs);
                }

                return lastKnown ?? GpuRouteSnapshot.Unknown(
                    "Windows did not report a live route during the verification window.");
            }

            private OperationResult AppendNativeMuxNote(OperationResult result)
            {
                if (result == null)
                {
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(_nativeMuxFallbackNote))
                {
                    result.Warning = CombineWarnings(
                        result.Warning,
                        _nativeMuxFallbackNote);
                }

                return result;
            }

            private string BuildEvidence(NvidiaPanelSnapshot panelSnapshot, GpuRouteSnapshot liveSnapshot)
            {
                var parts = new List<string>();

                if (liveSnapshot != null && !string.IsNullOrWhiteSpace(liveSnapshot.Evidence))
                {
                    parts.Add(liveSnapshot.Evidence);
                }

                if (panelSnapshot != null && !string.IsNullOrWhiteSpace(panelSnapshot.Evidence))
                {
                    parts.Add(panelSnapshot.Evidence);
                }

                if (panelSnapshot != null && liveSnapshot != null &&
                    liveSnapshot.Mode != "unknown" &&
                    panelSnapshot.StatusMode != "unknown" &&
                    panelSnapshot.StatusMode != liveSnapshot.Mode)
                {
                    parts.Add(
                        "NVIDIA Control Panel still reports " +
                        DescribeMode(panelSnapshot.StatusMode) +
                        " while Windows shows " +
                        DescribeMode(liveSnapshot.Mode) + ".");
                }

                if (panelSnapshot != null && panelSnapshot.Mode != "unknown")
                {
                    parts.Add("Panel selection: " + DescribePanelSelection(panelSnapshot.Mode) + ".");
                }

                if (!string.IsNullOrWhiteSpace(_nativeMuxFallbackNote))
                {
                    parts.Add("Native Dell MUX was detected but did not become the active control path.");
                }

                if (parts.Count == 0)
                {
                    return "NVIDIA display mode is available, but the current route could not be proven.";
                }

                return string.Join(" ", parts.ToArray());
            }

            private static string DescribePanelSelection(string mode)
            {
                mode = NormalizeMode(mode);
                switch (mode)
                {
                    case "auto":
                        return "Automatic Select";
                    case "dgpu":
                    case "hybrid":
                        return DescribeMode(mode);
                    default:
                        return "Unknown";
                }
            }

            private static string CombineWarnings(string first, string second)
            {
                var hasFirst = !string.IsNullOrWhiteSpace(first);
                var hasSecond = !string.IsNullOrWhiteSpace(second);

                if (hasFirst && hasSecond)
                {
                    return first.Trim() + " " + second.Trim();
                }

                if (hasFirst)
                {
                    return first.Trim();
                }

                if (hasSecond)
                {
                    return second.Trim();
                }

                return null;
            }
        }

        private sealed class PreferenceOnlyRouteController : IRouteController
        {
            public string ControlPath
            {
                get { return "unavailable"; }
            }

            public bool IsAvailable
            {
                get { return true; }
            }

            public string[] GetAvailableModes()
            {
                return new string[0];
            }

            public bool SupportsMode(string mode)
            {
                return false;
            }

            public GpuRouteSnapshot DetectSnapshot()
            {
                return GpuRouteDetector.Detect();
            }

            public OperationResult Apply(string mode)
            {
                mode = NormalizeRouteMode(mode);

                return OperationResult.Fail(
                    "This system does not expose native Dell MUX route control for " + DescribeMode(mode) +
                    ". Use NVIDIA app preference control instead.",
                    controlPath: ControlPath,
                    requestedMode: mode);
            }
        }
    }
}
