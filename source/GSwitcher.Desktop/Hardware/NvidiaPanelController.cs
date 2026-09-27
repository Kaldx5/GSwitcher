using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using GSwitcher.Models;

namespace GSwitcher.Backend
{
    internal sealed class NvidiaPanelSnapshot
    {
        public string Mode { get; private set; }
        public string StatusMode { get; private set; }
        public string StatusText { get; private set; }
        public string ControlPath { get; private set; }
        public string Evidence { get; private set; }
        public bool Probed { get; private set; }

        public static NvidiaPanelSnapshot Create(
            string mode,
            string statusMode,
            string statusText,
            string controlPath,
            string evidence,
            bool probed)
        {
            return new NvidiaPanelSnapshot
            {
                Mode = GpuManager.NormalizeMode(mode),
                StatusMode = GpuManager.NormalizeRouteMode(statusMode),
                StatusText = string.IsNullOrWhiteSpace(statusText) ? string.Empty : statusText.Trim(),
                ControlPath = string.IsNullOrWhiteSpace(controlPath) ? "unavailable" : controlPath.Trim().ToLowerInvariant(),
                Evidence = string.IsNullOrWhiteSpace(evidence)
                    ? "NVIDIA display-mode verification is waiting for more evidence."
                    : evidence.Trim(),
                Probed = probed
            };
        }

        public static NvidiaPanelSnapshot Unknown(string evidence)
        {
            return Create("unknown", "unknown", string.Empty, "unavailable", evidence, false);
        }
    }

    internal static class NvidiaPanelController
    {
        public const string ControlPath = "nvidia-panel";

        private const string PanelWindowTitle = "NVIDIA Control Panel";
        private const string ConfirmDialogTitle = "Apply Changes";
        private const string ControlPanelAppId =
            "shell:AppsFolder\\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel";

        private const int AutoModeId = 3792;
        private const int OptimusModeId = 3793;
        private const int DgpuModeId = 3794;
        private const int ApplyButtonId = 1021;
        private const int StatusLabelId = 3841;
        private const int StatusLabelAltId = 3842;
        private const int ConfirmYesId = 1;
        private const int ConfirmNoId = 2;
        private const int ConfirmCountdownId = 31102;
        private const int ConfirmBodyId = 31110;
        private const string TreeViewClassName = "SysTreeView32";
        private const string ManageDisplayModeNodeText = "Manage Display mode";

        private const int WaitForWindowMs = 20000;
        private const int WaitForReadyMs = 16000;
        private const int WaitForDialogMs = 12000;
        private const int WaitForVerifyMs = 24000;
        private const int WaitStepMs = 250;
        private const int SnapshotCacheSeconds = 8;
        private const int TreeNavigationRetryMs = 1500;
        private const int TreeNavigationSettleMs = 400;
        private const int TreeNodeTextOffsetX = 60;
        private const int TreeNodeMinimumOffsetX = 28;
        private const int TreeNodeClickMarginX = 20;

        private static readonly bool EnablePanelAutomation = false;
        private static readonly object SnapshotLock = new object();
        private static NvidiaPanelSnapshot _lastSnapshot;
        private static DateTime _lastSnapshotUtc = DateTime.MinValue;

        public static bool IsInstalled()
        {
            try
            {
                if (FindWindowByTitle(PanelWindowTitle) != IntPtr.Zero)
                {
                    return true;
                }

                if (NvidiaController.IsAvailable())
                {
                    return true;
                }

                var windowsApps = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WindowsApps");

                if (!Directory.Exists(windowsApps))
                {
                    return false;
                }

                var packages = Directory.GetDirectories(
                    windowsApps,
                    "NVIDIACorp.NVIDIAControlPanel_*",
                    SearchOption.TopDirectoryOnly);

                return packages != null && packages.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static string[] GetAvailableModes()
        {
            return IsRouteAutomationAvailable() ? new[] { "auto", "hybrid", "dgpu" } : new string[0];
        }

        public static bool IsRouteAutomationAvailable()
        {
            return EnablePanelAutomation && IsInstalled();
        }

        public static NvidiaPanelSnapshot DetectSnapshot(bool forceRefresh = false)
        {
            lock (SnapshotLock)
            {
                if (!forceRefresh &&
                    _lastSnapshot != null &&
                    (DateTime.UtcNow - _lastSnapshotUtc).TotalSeconds < SnapshotCacheSeconds)
                {
                    return _lastSnapshot;
                }
            }

            if (!IsInstalled())
            {
                return CacheSnapshot(NvidiaPanelSnapshot.Unknown(
                    "NVIDIA Control Panel is not installed on this machine."));
            }

            if (!EnablePanelAutomation)
            {
                return CacheSnapshot(NvidiaPanelSnapshot.Unknown(
                    "NVIDIA Control Panel is installed, but passive startup probing is disabled in the portable build because nvcplui.exe is unstable on this machine. Use NVIDIA DRS preference evidence or Windows display topology for route truth."));
            }

            PanelSession session = null;
            NativeMethods.POINT originalCursor;
            var hadCursor = NativeMethods.GetCursorPos(out originalCursor);

            try
            {
                session = OpenPanelSession(readOnlyProbe: true);
                if (session == null || session.WindowHandle == IntPtr.Zero)
                {
                    return CacheSnapshot(NvidiaPanelSnapshot.Unknown(
                        "NVIDIA Control Panel could not be opened for a display-mode probe."));
                }

                var state = WaitForDisplayModeSurface(session, WaitForReadyMs);
                if (state == null || !state.IsReady)
                {
                    return CacheSnapshot(NvidiaPanelSnapshot.Unknown(
                        "GSwitcher could not read the NVIDIA display-mode controls."));
                }

                var evidence = BuildEvidence(
                    state.SelectedMode,
                    state.StatusMode,
                    state.StatusText,
                    session.LaunchedByGSwitcher,
                    state.RawStatusText);

                return CacheSnapshot(NvidiaPanelSnapshot.Create(
                    state.SelectedMode,
                    state.StatusMode,
                    state.StatusText,
                    ControlPath,
                    evidence,
                    true));
            }
            catch (Exception ex)
            {
                return CacheSnapshot(NvidiaPanelSnapshot.Unknown(
                    "NVIDIA display-mode probe failed: " + ex.Message + "."));
            }
            finally
            {
                if (hadCursor)
                {
                    NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
                }

                CleanupPanelSession(session);
            }
        }

        public static OperationResult ApplyDisplayMode(string mode)
        {
            mode = GpuManager.NormalizeMode(mode);
            if (mode != "auto" && mode != "hybrid" && mode != "dgpu")
            {
                return OperationResult.Fail(
                    "Unknown NVIDIA display mode.",
                    controlPath: ControlPath,
                    requestedMode: mode);
            }

            if (!IsInstalled())
            {
                return OperationResult.Fail(
                    "NVIDIA Control Panel is not installed on this machine.",
                    controlPath: ControlPath,
                    requestedMode: mode);
            }

            if (!EnablePanelAutomation)
            {
                return OperationResult.Fail(
                    "NVIDIA Control Panel display-mode automation is disabled in the portable build because it can crash nvcplui.exe on this machine. Use GPU preference controls for DRS policy, and use AWCC/Windows topology for hardware route truth.",
                    controlPath: ControlPath,
                    requestedMode: mode);
            }

            string blockerMessage;
            if (!PreflightDisplaySwitch(mode, out blockerMessage))
            {
                return OperationResult.Fail(
                    blockerMessage,
                    controlPath: ControlPath,
                    requestedMode: mode);
            }

            PanelSession session = null;
            NativeMethods.POINT originalCursor;
            var hadCursor = NativeMethods.GetCursorPos(out originalCursor);

            try
            {
                session = OpenPanelSession(readOnlyProbe: false);
                if (session == null || session.WindowHandle == IntPtr.Zero)
                {
                    return OperationResult.Fail(
                        "NVIDIA Control Panel could not be opened for display-mode control.",
                        controlPath: ControlPath,
                        requestedMode: mode);
                }

                NativeMethods.ShowWindow(session.WindowHandle, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(session.WindowHandle);
                Thread.Sleep(240);

                var initialState = WaitForDisplayModeSurface(session, WaitForReadyMs);
                if (initialState == null || !initialState.IsReady)
                {
                    return OperationResult.Fail(
                        "GSwitcher could not read the NVIDIA display-mode controls.",
                        controlPath: ControlPath,
                        requestedMode: mode);
                }

                if (initialState.SelectedMode == mode && ModeAlreadyVerified(mode, initialState.StatusMode))
                {
                    var alreadyMessage = "NVIDIA display mode is already set to " +
                        DescribeMode(mode) + ". " + initialState.StatusText + ".";

                    var snapshot = NvidiaPanelSnapshot.Create(
                        initialState.SelectedMode,
                        initialState.StatusMode,
                        initialState.StatusText,
                        ControlPath,
                        BuildEvidence(
                            initialState.SelectedMode,
                            initialState.StatusMode,
                            initialState.StatusText,
                            session.LaunchedByGSwitcher,
                            initialState.RawStatusText),
                        true);

                    CacheSnapshot(snapshot);

                    return OperationResult.Ok(
                        alreadyMessage,
                        ControlPath,
                        requestedMode: mode,
                        verifiedMode: snapshot.Mode,
                        warning: blockerMessage);
                }

                var targetHandle = initialState.GetModeHandle(mode);
                if (targetHandle == IntPtr.Zero)
                {
                    return OperationResult.Fail(
                        "NVIDIA Control Panel did not expose the requested display-mode control.",
                        controlPath: ControlPath,
                        requestedMode: mode);
                }

                if (!ClickHandle(targetHandle))
                {
                    return OperationResult.Fail(
                        "GSwitcher could not select " + DescribeMode(mode) + " in NVIDIA Control Panel.",
                        controlPath: ControlPath,
                        requestedMode: mode);
                }

                var pendingState = WaitForPendingSelection(session, mode, WaitForReadyMs);
                if (pendingState == null || pendingState.ApplyHandle == IntPtr.Zero)
                {
                    var requestedRoute = GpuManager.NormalizeRouteMode(mode);
                    var currentRoute = GpuManager.NormalizeRouteMode(initialState.StatusMode);
                    if (initialState.SelectedMode == mode && currentRoute != "unknown" && currentRoute != requestedRoute)
                    {
                        var stuckMessage = "NVIDIA Control Panel is already selecting " +
                            DescribeMode(mode) + ", but the live status is still " +
                            DescribeStatusMode(initialState.StatusMode) + ".";

                        return OperationResult.Fail(
                            AppendGpuActivityNote(stuckMessage),
                            BuildEvidence(
                                initialState.SelectedMode,
                                initialState.StatusMode,
                                initialState.StatusText,
                                session.LaunchedByGSwitcher,
                                initialState.RawStatusText),
                            controlPath: ControlPath,
                            requestedMode: mode,
                            verifiedMode: initialState.StatusMode,
                            warning: "The requested route is selected in NVIDIA Control Panel, but the live display owner did not switch.");
                    }

                    return OperationResult.Fail(
                        "NVIDIA Control Panel did not stage the requested display-mode change.",
                        controlPath: ControlPath,
                        requestedMode: mode,
                        warning: AppendGpuActivityNote("A blocking fullscreen app or an unsupported display state may have rejected the switch."));
                }

                if (!ClickHandle(pendingState.ApplyHandle))
                {
                    return OperationResult.Fail(
                        "GSwitcher could not press Apply in NVIDIA Control Panel.",
                        controlPath: ControlPath,
                        requestedMode: mode);
                }

                var confirmHandle = WaitForWindowByTitle(ConfirmDialogTitle, WaitForDialogMs);
                if (confirmHandle == IntPtr.Zero)
                {
                    var silentFinalState = WaitForVerifiedState(session, mode, 6000);
                    if (silentFinalState != null)
                    {
                        var silentSnapshot = NvidiaPanelSnapshot.Create(
                            silentFinalState.SelectedMode,
                            silentFinalState.StatusMode,
                            silentFinalState.StatusText,
                            ControlPath,
                            BuildEvidence(
                                silentFinalState.SelectedMode,
                                silentFinalState.StatusMode,
                                silentFinalState.StatusText,
                                session.LaunchedByGSwitcher,
                                silentFinalState.RawStatusText),
                            true);

                        CacheSnapshot(silentSnapshot);

                        return ModeAlreadyVerified(mode, silentFinalState.StatusMode)
                            ? OperationResult.Ok(
                                "NVIDIA display mode verified without a visible confirmation dialog: " +
                                DescribeMode(mode) + ". " + silentFinalState.StatusText + ".",
                                ControlPath,
                                requestedMode: mode,
                                verifiedMode: silentFinalState.StatusMode,
                                warning: blockerMessage)
                            : OperationResult.Fail(
                                AppendGpuActivityNote(
                                    "NVIDIA display mode changed silently, but the live status is still " +
                                    DescribeStatusMode(silentFinalState.StatusMode) + "."),
                                silentSnapshot.Evidence,
                                ControlPath,
                                requestedMode: mode,
                                verifiedMode: silentFinalState.StatusMode,
                                warning: "The confirmation dialog never appeared, and the live display owner did not fully switch.");
                    }

                    return OperationResult.Fail(
                        "NVIDIA Control Panel never showed the display-change confirmation dialog.",
                        controlPath: ControlPath,
                        requestedMode: mode);
                }

                var dialogState = ReadDialogState(confirmHandle);
                if (dialogState == null || dialogState.YesHandle == IntPtr.Zero)
                {
                    return OperationResult.Fail(
                        "GSwitcher could not read the NVIDIA confirmation dialog.",
                        dialogState == null ? null : dialogState.ControlDump,
                        controlPath: ControlPath,
                        requestedMode: mode);
                }

                if (!ClickHandle(dialogState.YesHandle))
                {
                    return OperationResult.Fail(
                        "GSwitcher could not confirm the NVIDIA display-mode change.",
                        dialogState.ControlDump,
                        controlPath: ControlPath,
                        requestedMode: mode);
                }

                if (!WaitForDialogToClose(ConfirmDialogTitle, WaitForVerifyMs))
                {
                    return OperationResult.Fail(
                        "The NVIDIA display-change confirmation dialog never closed.",
                        controlPath: ControlPath,
                        requestedMode: mode);
                }

                var finalState = WaitForVerifiedState(session, mode, WaitForVerifyMs);
                if (finalState == null)
                {
                    return OperationResult.Fail(
                        "NVIDIA Control Panel did not verify the requested display-mode change.",
                        controlPath: ControlPath,
                        requestedMode: mode,
                        warning: "The panel accepted input, but the final display status could not be proven.");
                }

                var finalSnapshot = NvidiaPanelSnapshot.Create(
                    finalState.SelectedMode,
                    finalState.StatusMode,
                    finalState.StatusText,
                    ControlPath,
                    BuildEvidence(
                        finalState.SelectedMode,
                        finalState.StatusMode,
                        finalState.StatusText,
                        session.LaunchedByGSwitcher,
                        finalState.RawStatusText),
                    true);

                CacheSnapshot(finalSnapshot);

                if (!ModeAlreadyVerified(mode, finalState.StatusMode))
                {
                    return OperationResult.Fail(
                        AppendGpuActivityNote(
                            "NVIDIA display mode was set to " + DescribeMode(mode) +
                            ", but the live status is still " + DescribeStatusMode(finalState.StatusMode) + "."),
                        finalSnapshot.Evidence,
                        ControlPath,
                        requestedMode: mode,
                        verifiedMode: finalState.StatusMode,
                        warning: "A blocking app, battery state, or external display may still be preventing the route change.");
                }

                return OperationResult.Ok(
                    "NVIDIA display mode verified: " + DescribeMode(mode) +
                    ". " + finalState.StatusText + ".",
                    ControlPath,
                    requestedMode: mode,
                    verifiedMode: finalState.StatusMode,
                    warning: blockerMessage);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(
                    "NVIDIA display-mode change failed: " + ex.Message,
                    ex.ToString(),
                    ControlPath,
                    requestedMode: mode);
            }
            finally
            {
                if (hadCursor)
                {
                    NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
                }

                CleanupPanelSession(session);
            }
        }

        public static OperationResult OpenControlPanel()
        {
            try
            {
                if (!EnablePanelAutomation)
                {
                    return OperationResult.Fail(
                        "Opening NVIDIA Control Panel from GSwitcher is disabled because nvcplui.exe has been crashing on this machine. Open it manually from Windows if you need to inspect it.",
                        controlPath: ControlPath);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = ControlPanelAppId,
                    UseShellExecute = true
                };

                Process.Start(startInfo);

                return OperationResult.Ok(
                    "NVIDIA Control Panel opened to the display-mode surface.",
                    ControlPath,
                    verified: true);
            }
            catch (Win32Exception ex)
            {
                return OperationResult.Fail(
                    "NVIDIA Control Panel could not be opened.",
                    ex.ToString(),
                    ControlPath);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(
                    "NVIDIA Control Panel could not be opened.",
                    ex.ToString(),
                    ControlPath);
            }
        }

        public static string DescribeMode(string mode)
        {
            mode = GpuManager.NormalizeMode(mode);

            switch (mode)
            {
                case "dgpu":
                    return "NVIDIA GPU only";
                case "hybrid":
                    return "Optimus";
                case "auto":
                    return "Automatic Select";
                default:
                    return "Unknown";
            }
        }

        public static string DescribeStatusMode(string mode)
        {
            mode = GpuManager.NormalizeRouteMode(mode);

            switch (mode)
            {
                case "dgpu":
                    return "Discrete Graphics";
                case "hybrid":
                    return "Integrated Graphics";
                default:
                    return "Unknown";
            }
        }

        public static bool TryRestartNvidiaDisplayAdapter(out string note)
        {
            note = null;

            if (!IsCurrentProcessElevated())
            {
                note = "Restarting the NVIDIA display adapter as a recovery step requires administrator access.";
                return false;
            }

            var instanceId = FindNvidiaDisplayAdapterInstanceId();
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                note = "GSwitcher could not locate the NVIDIA display adapter instance for a restart-based recovery.";
                return false;
            }

            var arguments = "/restart-device \"" + instanceId.Replace("\"", string.Empty) + "\"";
            var result = RunHiddenProcess("pnputil.exe", arguments, 45000);
            if (result.ExitCode != 0)
            {
                note = "NVIDIA adapter restart recovery failed: " +
                    (string.IsNullOrWhiteSpace(result.ErrorOutput) ? result.StandardOutput : result.ErrorOutput).Trim();
                return false;
            }

            Thread.Sleep(6000);
            note = "The NVIDIA display adapter was restarted as a hybrid-route recovery step.";
            return true;
        }

        private static NvidiaPanelSnapshot CacheSnapshot(NvidiaPanelSnapshot snapshot)
        {
            lock (SnapshotLock)
            {
                _lastSnapshot = snapshot;
                _lastSnapshotUtc = DateTime.UtcNow;
                return snapshot;
            }
        }

        private static bool PreflightDisplaySwitch(string mode, out string message)
        {
            message = null;

            if (mode != "dgpu")
            {
                return true;
            }

            if (!IsOnAcPower())
            {
                message = "NVIDIA GPU only requires the laptop to be connected to AC power.";
                return false;
            }

            if (HasExternalDisplay())
            {
                message = "NVIDIA GPU only is blocked while an external display is attached.";
                return false;
            }

            return true;
        }

        private static bool IsOnAcPower()
        {
            NativeMethods.SYSTEM_POWER_STATUS status;
            return NativeMethods.GetSystemPowerStatus(out status) && status.ACLineStatus == 1;
        }

        private static bool HasExternalDisplay()
        {
            return GpuRouteDetector.HasExternalDisplay();
        }

        private static bool IsCurrentProcessElevated()
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }

        private static string FindNvidiaDisplayAdapterInstanceId()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_VideoController"))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementObject controller in collection)
                    {
                        using (controller)
                        {
                            var name = Convert.ToString(controller["Name"]) ?? string.Empty;
                            var pnpDeviceId = Convert.ToString(controller["PNPDeviceID"]) ?? string.Empty;
                            if (name.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                pnpDeviceId.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return pnpDeviceId.Trim();
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static ProcessResult RunHiddenProcess(string fileName, string arguments, int timeoutMs)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    return new ProcessResult
                    {
                        ExitCode = -1,
                        StandardOutput = string.Empty,
                        ErrorOutput = "The recovery process could not be started."
                    };
                }

                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }

                    return new ProcessResult
                    {
                        ExitCode = -1,
                        StandardOutput = process.StandardOutput.ReadToEnd(),
                        ErrorOutput = "The recovery process timed out."
                    };
                }

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = process.StandardOutput.ReadToEnd(),
                    ErrorOutput = process.StandardError.ReadToEnd()
                };
            }
        }

        private static string BuildEvidence(
            string selectedMode,
            string statusMode,
            string statusText,
            bool launchedProbe,
            string rawStatusText)
        {
            var builder = new StringBuilder();
            builder.Append("NVIDIA Control Panel selects ");
            builder.Append(DescribeMode(selectedMode));
            builder.Append(".");

            if (!string.IsNullOrWhiteSpace(statusText))
            {
                builder.Append(" ");
                builder.Append(statusText.Trim());
                if (!statusText.Trim().EndsWith("."))
                {
                    builder.Append(".");
                }
            }
            else if (statusMode != "unknown")
            {
                builder.Append(" Current status: ");
                builder.Append(DescribeStatusMode(statusMode));
                builder.Append(".");
            }

            if (!string.IsNullOrWhiteSpace(rawStatusText) &&
                !string.Equals(rawStatusText.Trim(), statusText, StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(" ");
                builder.Append(rawStatusText.Trim());
                if (!rawStatusText.Trim().EndsWith("."))
                {
                    builder.Append(".");
                }
            }

            if (launchedProbe)
            {
                builder.Append(" Verified through an NVIDIA Control Panel probe.");
            }

            return builder.ToString();
        }

        private static bool ModeAlreadyVerified(string requestedMode, string statusMode)
        {
            requestedMode = GpuManager.NormalizeMode(requestedMode);
            if (requestedMode == "auto")
            {
                return true;
            }

            requestedMode = GpuManager.NormalizeRouteMode(requestedMode);
            statusMode = GpuManager.NormalizeRouteMode(statusMode);

            if (requestedMode == "unknown")
            {
                return false;
            }

            return requestedMode == statusMode;
        }

        private static string AppendGpuActivityNote(string message)
        {
            var activityNote = GpuActivityDetector.DescribeSessionLoad();
            if (string.IsNullOrWhiteSpace(activityNote))
            {
                return message;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return activityNote;
            }

            return message + " " + activityNote;
        }

        private static PanelSession OpenPanelSession(bool readOnlyProbe)
        {
            var existing = FindWindowByTitle(PanelWindowTitle);
            if (existing != IntPtr.Zero)
            {
                return new PanelSession(existing, false);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = ControlPanelAppId,
                UseShellExecute = true
            };

            Process.Start(startInfo);

            var windowHandle = WaitForWindowByTitle(PanelWindowTitle, WaitForWindowMs);
            if (windowHandle == IntPtr.Zero)
            {
                return null;
            }

            if (!readOnlyProbe)
            {
                NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(windowHandle);
            }

            return new PanelSession(windowHandle, true);
        }

        private static void CleanupPanelSession(PanelSession session)
        {
            if (session == null || !session.LaunchedByGSwitcher || session.WindowHandle == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.PostMessage(session.WindowHandle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            WaitForWindowToClose(session.WindowHandle, 4000);
        }

        private static bool EnsurePanelWindow(PanelSession session, bool reopenIfMissing, int timeoutMs)
        {
            if (session == null)
            {
                return false;
            }

            if (session.WindowHandle != IntPtr.Zero && NativeMethods.IsWindow(session.WindowHandle))
            {
                return true;
            }

            var existing = FindWindowByTitle(PanelWindowTitle);
            if (existing != IntPtr.Zero)
            {
                session.UpdateWindowHandle(existing);
                return true;
            }

            if (!reopenIfMissing || !session.LaunchedByGSwitcher)
            {
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = ControlPanelAppId,
                    UseShellExecute = true
                };

                Process.Start(startInfo);
            }
            catch
            {
                return false;
            }

            var handle = WaitForWindowByTitle(PanelWindowTitle, timeoutMs);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            session.UpdateWindowHandle(handle);
            return true;
        }

        private static IntPtr FindWindowByTitle(string title)
        {
            var found = IntPtr.Zero;

            NativeMethods.EnumWindows(
                delegate (IntPtr handle, IntPtr lParam)
                {
                    if (string.Equals(GetWindowText(handle), title, StringComparison.Ordinal))
                    {
                        found = handle;
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);

            return found;
        }

        private static IntPtr WaitForWindowByTitle(string title, int timeoutMs)
        {
            var startedAt = Environment.TickCount;

            while (Environment.TickCount - startedAt < timeoutMs)
            {
                var handle = FindWindowByTitle(title);
                if (handle != IntPtr.Zero)
                {
                    return handle;
                }

                Thread.Sleep(WaitStepMs);
            }

            return IntPtr.Zero;
        }

        private static bool WaitForWindowToClose(IntPtr handle, int timeoutMs)
        {
            var startedAt = Environment.TickCount;

            while (Environment.TickCount - startedAt < timeoutMs)
            {
                if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
                {
                    return true;
                }

                Thread.Sleep(WaitStepMs);
            }

            return false;
        }

        private static bool WaitForDialogToClose(string title, int timeoutMs)
        {
            var startedAt = Environment.TickCount;

            while (Environment.TickCount - startedAt < timeoutMs)
            {
                if (FindWindowByTitle(title) == IntPtr.Zero)
                {
                    return true;
                }

                Thread.Sleep(WaitStepMs);
            }

            return false;
        }

        private static PanelWindowState WaitForPanelState(PanelSession session, int timeoutMs)
        {
            var startedAt = Environment.TickCount;

            while (Environment.TickCount - startedAt < timeoutMs)
            {
                if (!EnsurePanelWindow(session, true, Math.Min(WaitForWindowMs, timeoutMs)))
                {
                    Thread.Sleep(WaitStepMs);
                    continue;
                }

                var state = ReadPanelState(session.WindowHandle);
                if (state != null && state.IsReady)
                {
                    return state;
                }

                Thread.Sleep(WaitStepMs);
            }

            return null;
        }

        private static PanelWindowState WaitForDisplayModeSurface(PanelSession session, int timeoutMs)
        {
            var startedAt = Environment.TickCount;
            var lastNavigationAttempt = Environment.TickCount - TreeNavigationRetryMs;

            while (Environment.TickCount - startedAt < timeoutMs)
            {
                if (!EnsurePanelWindow(session, true, Math.Min(WaitForWindowMs, timeoutMs)))
                {
                    Thread.Sleep(WaitStepMs);
                    continue;
                }

                var windowHandle = session.WindowHandle;
                var state = ReadPanelState(windowHandle);
                if (state != null && state.IsReady)
                {
                    return state;
                }

                if (Environment.TickCount - lastNavigationAttempt >= TreeNavigationRetryMs)
                {
                    lastNavigationAttempt = Environment.TickCount;
                    if (TryNavigateToDisplayModePage(windowHandle))
                    {
                        Thread.Sleep(TreeNavigationSettleMs);
                        continue;
                    }
                }

                Thread.Sleep(WaitStepMs);
            }

            return null;
        }

        private static PanelWindowState WaitForPendingSelection(PanelSession session, string mode, int timeoutMs)
        {
            var startedAt = Environment.TickCount;

            while (Environment.TickCount - startedAt < timeoutMs)
            {
                if (!EnsurePanelWindow(session, true, Math.Min(WaitForWindowMs, timeoutMs)))
                {
                    Thread.Sleep(WaitStepMs);
                    continue;
                }

                var windowHandle = session.WindowHandle;
                var state = ReadPanelState(windowHandle);
                if (state != null &&
                    state.IsReady &&
                    state.SelectedMode == mode &&
                    state.ApplyHandle != IntPtr.Zero)
                {
                    return state;
                }

                if (windowHandle != IntPtr.Zero)
                {
                    TryNavigateToDisplayModePage(windowHandle);
                }

                Thread.Sleep(WaitStepMs);
            }

            return null;
        }

        private static PanelWindowState WaitForVerifiedState(PanelSession session, string mode, int timeoutMs)
        {
            var startedAt = Environment.TickCount;

            while (Environment.TickCount - startedAt < timeoutMs)
            {
                if (!EnsurePanelWindow(session, true, Math.Min(WaitForWindowMs, timeoutMs)))
                {
                    Thread.Sleep(WaitStepMs);
                    continue;
                }

                var windowHandle = session.WindowHandle;
                var state = ReadPanelState(windowHandle);
                if (state != null &&
                    state.IsReady &&
                    state.SelectedMode == mode &&
                    state.ApplyHandle == IntPtr.Zero)
                {
                    return state;
                }

                if (windowHandle != IntPtr.Zero)
                {
                    TryNavigateToDisplayModePage(windowHandle);
                }

                Thread.Sleep(WaitStepMs);
            }

            return null;
        }

        private static bool TryNavigateToDisplayModePage(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return false;
            }

            TreeItemState targetItem;
            IntPtr treeHandle;
            if (!TryFindTreeItem(windowHandle, ManageDisplayModeNodeText, out treeHandle, out targetItem))
            {
                return false;
            }

            var clickX = targetItem.Rect.Left + TreeNodeTextOffsetX;
            NativeMethods.RECT treeRect;
            if (!NativeMethods.GetWindowRect(treeHandle, out treeRect))
            {
                return false;
            }

            var treeWidth = Math.Max(0, treeRect.Right - treeRect.Left);
            if (treeWidth > 0)
            {
                clickX = Math.Min(clickX, Math.Max(TreeNodeMinimumOffsetX, treeWidth - TreeNodeClickMarginX));
            }

            clickX = Math.Max(TreeNodeMinimumOffsetX, clickX);

            var clickY = targetItem.Rect.Top + ((targetItem.Rect.Bottom - targetItem.Rect.Top) / 2);
            var lParam = MakeLParam(clickX, clickY);

            NativeMethods.PostMessage(treeHandle, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam);
            Thread.Sleep(80);
            NativeMethods.PostMessage(treeHandle, NativeMethods.WM_LBUTTONDOWN, new IntPtr(NativeMethods.MK_LBUTTON), lParam);
            Thread.Sleep(60);
            NativeMethods.PostMessage(treeHandle, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, lParam);
            Thread.Sleep(120);
            NativeMethods.PostMessage(treeHandle, NativeMethods.WM_LBUTTONDBLCLK, new IntPtr(NativeMethods.MK_LBUTTON), lParam);
            Thread.Sleep(60);
            NativeMethods.PostMessage(treeHandle, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, lParam);

            return true;
        }

        private static bool TryFindTreeItem(
            IntPtr windowHandle,
            string expectedText,
            out IntPtr treeHandle,
            out TreeItemState itemState)
        {
            treeHandle = FindChildWindowByClass(windowHandle, TreeViewClassName);
            itemState = null;

            if (treeHandle == IntPtr.Zero || !NativeMethods.IsWindow(treeHandle))
            {
                return false;
            }

            IntPtr processHandle = IntPtr.Zero;

            try
            {
                uint processId;
                NativeMethods.GetWindowThreadProcessId(treeHandle, out processId);
                if (processId == 0)
                {
                    return false;
                }

                processHandle = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_QUERY_INFORMATION |
                    NativeMethods.PROCESS_VM_OPERATION |
                    NativeMethods.PROCESS_VM_READ |
                    NativeMethods.PROCESS_VM_WRITE,
                    false,
                    processId);

                if (processHandle == IntPtr.Zero)
                {
                    return false;
                }

                var rootItem = NativeMethods.SendMessage(
                    treeHandle,
                    NativeMethods.TVM_GETNEXTITEM,
                    new IntPtr(NativeMethods.TVGN_ROOT),
                    IntPtr.Zero);

                return TryFindTreeItemRecursive(treeHandle, processHandle, rootItem, expectedText, out itemState);
            }
            finally
            {
                if (processHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(processHandle);
                }
            }
        }

        private static bool TryFindTreeItemRecursive(
            IntPtr treeHandle,
            IntPtr processHandle,
            IntPtr itemHandle,
            string expectedText,
            out TreeItemState itemState)
        {
            while (itemHandle != IntPtr.Zero)
            {
                var text = ReadTreeItemText(treeHandle, processHandle, itemHandle);
                if (string.Equals(text, expectedText, StringComparison.Ordinal))
                {
                    itemState = new TreeItemState
                    {
                        Handle = itemHandle,
                        Text = text,
                        Rect = ReadTreeItemRect(treeHandle, processHandle, itemHandle)
                    };

                    return true;
                }

                var childHandle = NativeMethods.SendMessage(
                    treeHandle,
                    NativeMethods.TVM_GETNEXTITEM,
                    new IntPtr(NativeMethods.TVGN_CHILD),
                    itemHandle);

                if (TryFindTreeItemRecursive(treeHandle, processHandle, childHandle, expectedText, out itemState))
                {
                    return true;
                }

                itemHandle = NativeMethods.SendMessage(
                    treeHandle,
                    NativeMethods.TVM_GETNEXTITEM,
                    new IntPtr(NativeMethods.TVGN_NEXT),
                    itemHandle);
            }

            itemState = null;
            return false;
        }

        private static string ReadTreeItemText(IntPtr treeHandle, IntPtr processHandle, IntPtr itemHandle)
        {
            if (treeHandle == IntPtr.Zero || processHandle == IntPtr.Zero || itemHandle == IntPtr.Zero)
            {
                return string.Empty;
            }

            const int charCount = 260;
            var itemStructSize = Marshal.SizeOf(typeof(NativeMethods.TVITEM));
            var remoteText = NativeMethods.VirtualAllocEx(
                processHandle,
                IntPtr.Zero,
                (UIntPtr)(charCount * 2),
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);
            var remoteItem = NativeMethods.VirtualAllocEx(
                processHandle,
                IntPtr.Zero,
                (UIntPtr)itemStructSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            if (remoteText == IntPtr.Zero || remoteItem == IntPtr.Zero)
            {
                if (remoteText != IntPtr.Zero)
                {
                    NativeMethods.VirtualFreeEx(processHandle, remoteText, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
                }

                if (remoteItem != IntPtr.Zero)
                {
                    NativeMethods.VirtualFreeEx(processHandle, remoteItem, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
                }

                return string.Empty;
            }

            try
            {
                var item = new NativeMethods.TVITEM
                {
                    mask = NativeMethods.TVIF_TEXT,
                    hItem = itemHandle,
                    pszText = remoteText,
                    cchTextMax = charCount
                };

                var localBytes = StructureToBytes(item);
                IntPtr written;
                if (!NativeMethods.WriteProcessMemory(processHandle, remoteItem, localBytes, localBytes.Length, out written))
                {
                    return string.Empty;
                }

                if (NativeMethods.SendMessage(treeHandle, NativeMethods.TVM_GETITEMW, IntPtr.Zero, remoteItem) == IntPtr.Zero)
                {
                    return string.Empty;
                }

                var textBytes = new byte[charCount * 2];
                IntPtr read;
                if (!NativeMethods.ReadProcessMemory(processHandle, remoteText, textBytes, textBytes.Length, out read))
                {
                    return string.Empty;
                }

                return Encoding.Unicode.GetString(textBytes).TrimEnd('\0');
            }
            finally
            {
                NativeMethods.VirtualFreeEx(processHandle, remoteText, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
                NativeMethods.VirtualFreeEx(processHandle, remoteItem, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            }
        }

        private static NativeMethods.RECT ReadTreeItemRect(IntPtr treeHandle, IntPtr processHandle, IntPtr itemHandle)
        {
            if (treeHandle == IntPtr.Zero || processHandle == IntPtr.Zero || itemHandle == IntPtr.Zero)
            {
                return new NativeMethods.RECT();
            }

            var rectSize = Marshal.SizeOf(typeof(NativeMethods.RECT));
            var remoteRect = NativeMethods.VirtualAllocEx(
                processHandle,
                IntPtr.Zero,
                (UIntPtr)rectSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            if (remoteRect == IntPtr.Zero)
            {
                return new NativeMethods.RECT();
            }

            try
            {
                var seed = new byte[rectSize];
                var itemBytes = IntPtr.Size == 8
                    ? BitConverter.GetBytes(itemHandle.ToInt64())
                    : BitConverter.GetBytes(itemHandle.ToInt32());

                Array.Copy(itemBytes, seed, Math.Min(itemBytes.Length, IntPtr.Size));

                IntPtr written;
                if (!NativeMethods.WriteProcessMemory(processHandle, remoteRect, seed, seed.Length, out written))
                {
                    return new NativeMethods.RECT();
                }

                if (NativeMethods.SendMessage(treeHandle, NativeMethods.TVM_GETITEMRECT, IntPtr.Zero, remoteRect) == IntPtr.Zero)
                {
                    return new NativeMethods.RECT();
                }

                var rectBytes = new byte[rectSize];
                IntPtr read;
                if (!NativeMethods.ReadProcessMemory(processHandle, remoteRect, rectBytes, rectBytes.Length, out read))
                {
                    return new NativeMethods.RECT();
                }

                return BytesToStructure<NativeMethods.RECT>(rectBytes);
            }
            finally
            {
                NativeMethods.VirtualFreeEx(processHandle, remoteRect, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            }
        }

        private static IntPtr FindChildWindowByClass(IntPtr windowHandle, string className)
        {
            var found = IntPtr.Zero;

            NativeMethods.EnumChildWindows(
                windowHandle,
                delegate (IntPtr handle, IntPtr lParam)
                {
                    if (string.Equals(GetWindowClassName(handle), className, StringComparison.Ordinal))
                    {
                        found = handle;
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);

            return found;
        }

        private static IntPtr MakeLParam(int x, int y)
        {
            return new IntPtr((y << 16) | (x & 0xFFFF));
        }

        private static PanelWindowState ReadPanelState(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return null;
            }

            var controls = EnumerateControls(windowHandle, new[]
            {
                AutoModeId,
                OptimusModeId,
                DgpuModeId,
                ApplyButtonId,
                StatusLabelId,
                StatusLabelAltId
            });

            var state = new PanelWindowState();

            for (var index = 0; index < controls.Count; index++)
            {
                var control = controls[index];

                if (control.Id == AutoModeId)
                {
                    state.AutoHandle = control.Handle;
                    state.AutoChecked = control.CheckState == 1;
                }
                else if (control.Id == OptimusModeId)
                {
                    state.OptimusHandle = control.Handle;
                    state.OptimusChecked = control.CheckState == 1;
                }
                else if (control.Id == DgpuModeId)
                {
                    state.DgpuHandle = control.Handle;
                    state.DgpuChecked = control.CheckState == 1;
                }
                else if (control.Id == ApplyButtonId &&
                         control.Visible &&
                         control.Text.IndexOf("Apply", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    state.ApplyHandle = control.Handle;
                }
                else if ((control.Id == StatusLabelId || control.Id == StatusLabelAltId) &&
                         !string.IsNullOrWhiteSpace(control.Text))
                {
                    state.RawStatusText = control.Text.Trim();
                }
            }

            if (state.AutoChecked)
            {
                state.SelectedMode = "auto";
            }
            else if (state.OptimusChecked)
            {
                state.SelectedMode = "hybrid";
            }
            else if (state.DgpuChecked)
            {
                state.SelectedMode = "dgpu";
            }
            else
            {
                state.SelectedMode = "unknown";
            }

            state.StatusMode = NormalizeStatusMode(state.RawStatusText);
            state.StatusText = NormalizeStatusText(state.RawStatusText, state.StatusMode);

            return state;
        }

        private static DialogWindowState ReadDialogState(IntPtr dialogHandle)
        {
            if (dialogHandle == IntPtr.Zero || !NativeMethods.IsWindow(dialogHandle))
            {
                return null;
            }

            var controls = EnumerateControls(dialogHandle, null);

            var state = new DialogWindowState();
            var visibleButtons = new List<ControlState>();
            var bodyParts = new List<string>();

            for (var index = 0; index < controls.Count; index++)
            {
                var control = controls[index];

                if (IsAffirmativeButton(control))
                {
                    state.YesHandle = control.Handle;
                }

                if (IsNegativeButton(control))
                {
                    state.NoHandle = control.Handle;
                }

                if (control.ClassName == "Button" && control.Visible)
                {
                    visibleButtons.Add(control);
                }

                if (control.Id == ConfirmCountdownId)
                {
                    state.CountdownText = control.Text;
                }

                if ((control.Id == ConfirmBodyId || control.ClassName == "Static") &&
                    !string.IsNullOrWhiteSpace(control.Text) &&
                    bodyParts.IndexOf(control.Text.Trim()) < 0)
                {
                    bodyParts.Add(control.Text.Trim());
                }
            }

            if (state.YesHandle == IntPtr.Zero && visibleButtons.Count == 1)
            {
                state.YesHandle = visibleButtons[0].Handle;
            }

            if (string.IsNullOrWhiteSpace(state.BodyText) && bodyParts.Count > 0)
            {
                state.BodyText = string.Join(" ", bodyParts.ToArray());
            }

            state.ControlDump = BuildControlDump(controls);
            return state;
        }

        private static List<ControlState> EnumerateControls(IntPtr windowHandle, int[] ids)
        {
            var controls = new List<ControlState>();

            NativeMethods.EnumChildWindows(
                windowHandle,
                delegate (IntPtr handle, IntPtr lParam)
                {
                    var id = NativeMethods.GetDlgCtrlID(handle);
                    if (ids != null && ids.Length > 0 && !ContainsId(ids, id))
                    {
                        return true;
                    }

                    controls.Add(new ControlState
                    {
                        Handle = handle,
                        Id = id,
                        ClassName = GetWindowClassName(handle),
                        Text = GetWindowText(handle),
                        Visible = NativeMethods.IsWindowVisible(handle),
                        CheckState = NativeMethods.SendMessage(handle, NativeMethods.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32()
                    });

                    return true;
                },
                IntPtr.Zero);

            return controls;
        }

        private static bool ContainsId(int[] ids, int value)
        {
            if (ids == null || ids.Length == 0)
            {
                return false;
            }

            for (var index = 0; index < ids.Length; index++)
            {
                if (ids[index] == value)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ClickHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            {
                return false;
            }

            try
            {
                NativeMethods.SetForegroundWindow(handle);
                NativeMethods.SendMessage(handle, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(180);
                return true;
            }
            catch
            {
            }

            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(handle, out rect))
            {
                return false;
            }

            var centerX = rect.Left + ((rect.Right - rect.Left) / 2);
            var centerY = rect.Top + ((rect.Bottom - rect.Top) / 2);

            NativeMethods.SetCursorPos(centerX, centerY);
            Thread.Sleep(100);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(160);

            return true;
        }

        private static bool IsAffirmativeButton(ControlState control)
        {
            if (control == null)
            {
                return false;
            }

            if (control.Id == ConfirmYesId)
            {
                return true;
            }

            if (control.ClassName != "Button" || !control.Visible)
            {
                return false;
            }

            var text = (control.Text ?? string.Empty).Trim();
            return text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("apply", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNegativeButton(ControlState control)
        {
            if (control == null)
            {
                return false;
            }

            if (control.Id == ConfirmNoId)
            {
                return true;
            }

            if (control.ClassName != "Button" || !control.Visible)
            {
                return false;
            }

            var text = (control.Text ?? string.Empty).Trim();
            return text.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("cancel", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildControlDump(List<ControlState> controls)
        {
            if (controls == null || controls.Count == 0)
            {
                return "The NVIDIA confirmation dialog exposed no readable controls.";
            }

            var parts = new List<string>();
            for (var index = 0; index < controls.Count; index++)
            {
                var control = controls[index];
                var text = (control.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = "[no text]";
                }

                parts.Add(
                    "id=" + control.Id +
                    ",class=" + (string.IsNullOrWhiteSpace(control.ClassName) ? "[none]" : control.ClassName) +
                    ",visible=" + control.Visible +
                    ",text=" + text);
            }

            return "Dialog controls: " + string.Join(" | ", parts.ToArray());
        }

        private static string NormalizeStatusMode(string text)
        {
            text = (text ?? string.Empty).Trim();

            if (text.IndexOf("Discrete Graphics", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "dgpu";
            }

            if (text.IndexOf("Integrated Graphics", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "hybrid";
            }

            return "unknown";
        }

        private static string NormalizeStatusText(string text, string statusMode)
        {
            text = (text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            if (statusMode == "dgpu")
            {
                return "Current status: Discrete Graphics";
            }

            if (statusMode == "hybrid")
            {
                return "Current status: Integrated Graphics";
            }

            return string.Empty;
        }

        private static string GetWindowText(IntPtr handle)
        {
            var builder = new StringBuilder(512);
            NativeMethods.GetWindowText(handle, builder, builder.Capacity);
            return builder.ToString();
        }

        private static string GetWindowClassName(IntPtr handle)
        {
            var builder = new StringBuilder(256);
            NativeMethods.GetClassName(handle, builder, builder.Capacity);
            return builder.ToString();
        }

        private static byte[] StructureToBytes<T>(T value) where T : struct
        {
            var size = Marshal.SizeOf(typeof(T));
            var bytes = new byte[size];
            var pointer = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(value, pointer, false);
                Marshal.Copy(pointer, bytes, 0, size);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        private static T BytesToStructure<T>(byte[] bytes) where T : struct
        {
            var pointer = Marshal.AllocHGlobal(bytes.Length);

            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                return (T)Marshal.PtrToStructure(pointer, typeof(T));
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        private sealed class PanelSession
        {
            public PanelSession(IntPtr windowHandle, bool launchedByGSwitcher)
            {
                WindowHandle = windowHandle;
                LaunchedByGSwitcher = launchedByGSwitcher;
            }

            public IntPtr WindowHandle { get; private set; }
            public bool LaunchedByGSwitcher { get; private set; }

            public void UpdateWindowHandle(IntPtr windowHandle)
            {
                WindowHandle = windowHandle;
            }
        }

        private sealed class PanelWindowState
        {
            public IntPtr AutoHandle;
            public IntPtr OptimusHandle;
            public IntPtr DgpuHandle;
            public IntPtr ApplyHandle;
            public bool AutoChecked;
            public bool OptimusChecked;
            public bool DgpuChecked;
            public string SelectedMode;
            public string StatusMode;
            public string StatusText;
            public string RawStatusText;

            public bool IsReady
            {
                get
                {
                    return AutoHandle != IntPtr.Zero &&
                           OptimusHandle != IntPtr.Zero &&
                           DgpuHandle != IntPtr.Zero;
                }
            }

            public IntPtr GetModeHandle(string mode)
            {
                mode = GpuManager.NormalizeMode(mode);

                switch (mode)
                {
                    case "auto":
                        return AutoHandle;
                    case "hybrid":
                        return OptimusHandle;
                    case "dgpu":
                        return DgpuHandle;
                    default:
                        return IntPtr.Zero;
                }
            }
        }

        private sealed class DialogWindowState
        {
            public IntPtr YesHandle;
            public IntPtr NoHandle;
            public string CountdownText;
            public string BodyText;
            public string ControlDump;
        }

        private sealed class TreeItemState
        {
            public IntPtr Handle;
            public string Text;
            public NativeMethods.RECT Rect;
        }

        private sealed class ControlState
        {
            public IntPtr Handle;
            public int Id;
            public string ClassName;
            public string Text;
            public bool Visible;
            public int CheckState;
        }

        private sealed class ProcessResult
        {
            public int ExitCode;
            public string StandardOutput;
            public string ErrorOutput;
        }

        private static class NativeMethods
        {
            public const int BM_GETCHECK = 0x00F0;
            public const int BM_CLICK = 0x00F5;
            public const int SW_RESTORE = 9;
            public const int SW_MINIMIZE = 6;
            public const int WM_CLOSE = 0x0010;
            public const int WM_MOUSEMOVE = 0x0200;
            public const int WM_LBUTTONDOWN = 0x0201;
            public const int WM_LBUTTONUP = 0x0202;
            public const int WM_LBUTTONDBLCLK = 0x0203;
            public const int MK_LBUTTON = 0x0001;
            public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
            public const uint MOUSEEVENTF_LEFTUP = 0x0004;
            public const int TV_FIRST = 0x1100;
            public const int TVM_GETNEXTITEM = TV_FIRST + 10;
            public const int TVM_GETITEMRECT = TV_FIRST + 4;
            public const int TVM_GETITEMW = TV_FIRST + 62;
            public const int TVGN_ROOT = 0;
            public const int TVGN_NEXT = 1;
            public const int TVGN_CHILD = 4;
            public const uint TVIF_TEXT = 0x0001;
            public const uint PROCESS_VM_OPERATION = 0x0008;
            public const uint PROCESS_VM_READ = 0x0010;
            public const uint PROCESS_VM_WRITE = 0x0020;
            public const uint PROCESS_QUERY_INFORMATION = 0x0400;
            public const uint MEM_COMMIT = 0x1000;
            public const uint MEM_RESERVE = 0x2000;
            public const uint MEM_RELEASE = 0x8000;
            public const uint PAGE_READWRITE = 0x04;

            public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct TVITEM
            {
                public uint mask;
                public IntPtr hItem;
                public uint state;
                public uint stateMask;
                public IntPtr pszText;
                public int cchTextMax;
                public int iImage;
                public int iSelectedImage;
                public int cChildren;
                public IntPtr lParam;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SYSTEM_POWER_STATUS
            {
                public byte ACLineStatus;
                public byte BatteryFlag;
                public byte BatteryLifePercent;
                public byte SystemStatusFlag;
                public int BatteryLifeTime;
                public int BatteryFullLifeTime;
            }

            [DllImport("user32.dll")]
            public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll")]
            public static extern int GetDlgCtrlID(IntPtr hwndCtl);

            [DllImport("user32.dll")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool IsWindowVisible(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool IsWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll")]
            public static extern bool SetCursorPos(int x, int y);

            [DllImport("user32.dll")]
            public static extern bool GetCursorPos(out POINT point);

            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

            [DllImport("kernel32.dll")]
            public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr VirtualAllocEx(
                IntPtr hProcess,
                IntPtr lpAddress,
                UIntPtr dwSize,
                uint flAllocationType,
                uint flProtect);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool VirtualFreeEx(
                IntPtr hProcess,
                IntPtr lpAddress,
                UIntPtr dwSize,
                uint dwFreeType);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool WriteProcessMemory(
                IntPtr hProcess,
                IntPtr lpBaseAddress,
                byte[] lpBuffer,
                int nSize,
                out IntPtr lpNumberOfBytesWritten);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool ReadProcessMemory(
                IntPtr hProcess,
                IntPtr lpBaseAddress,
                byte[] lpBuffer,
                int dwSize,
                out IntPtr lpNumberOfBytesRead);
        }
    }
}
