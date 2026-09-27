using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using GSwitcher.Backend;
using GSwitcher.Models;

namespace GSwitcher.Services
{
    internal sealed class DisplayTransitionContext
    {
        public bool BrightnessSupported { get; set; }
        public int BrightnessLevel { get; set; }
        public bool DisplayModeCaptured { get; set; }
        public string PrimaryDisplayDeviceName { get; set; }
        public DisplayStateService.DisplayNativeMethods.DEVMODE PrimaryDisplayMode { get; set; }
    }

    internal static class DisplayStateService
    {
        private static readonly string DisplaySwitchPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "DisplaySwitch.exe");

        public static DisplayTransitionContext CaptureTransitionContext()
        {
            int brightnessLevel;
            var brightnessSupported = TryGetCurrentBrightness(out brightnessLevel);
            string primaryDisplayDeviceName;
            DisplayNativeMethods.DEVMODE primaryDisplayMode;
            var displayModeCaptured = TryCapturePrimaryDisplayMode(out primaryDisplayDeviceName, out primaryDisplayMode);

            return new DisplayTransitionContext
            {
                BrightnessSupported = brightnessSupported,
                BrightnessLevel = brightnessLevel,
                DisplayModeCaptured = displayModeCaptured,
                PrimaryDisplayDeviceName = primaryDisplayDeviceName,
                PrimaryDisplayMode = primaryDisplayMode
            };
        }

        public static void Refresh(AppState state)
        {
            if (state == null)
            {
                return;
            }

            int brightnessLevel;
            state.BrightnessSupported = TryGetCurrentBrightness(out brightnessLevel);
            state.BrightnessLevel = brightnessLevel;
            state.DisplayWarning = BuildDisplayWarning(state);
        }

        public static OperationResult AdjustBrightness(int delta)
        {
            int currentBrightness;
            if (!TryGetCurrentBrightness(out currentBrightness))
            {
                return OperationResult.Fail(
                    "Brightness fallback is unavailable on the active internal panel right now.",
                    controlPath: "brightness-wmi");
            }

            var targetBrightness = currentBrightness + delta;
            if (targetBrightness < 0)
            {
                targetBrightness = 0;
            }
            else if (targetBrightness > 100)
            {
                targetBrightness = 100;
            }

            return SetBrightness(targetBrightness);
        }

        public static OperationResult SetBrightness(int brightnessLevel)
        {
            if (brightnessLevel < 0)
            {
                brightnessLevel = 0;
            }
            else if (brightnessLevel > 100)
            {
                brightnessLevel = 100;
            }

            OperationResult wmiFailure = null;

            try
            {
                using (var mclass = new ManagementClass("WmiMonitorBrightnessMethods"))
                {
                    mclass.Scope = new ManagementScope(@"\\.\root\wmi");

                    using (var instances = mclass.GetInstances())
                    {
                        var applied = false;
                        foreach (ManagementObject instance in instances)
                        {
                            using (instance)
                            {
                                instance.InvokeMethod("WmiSetBrightness", new object[] { 1, brightnessLevel });
                                applied = true;
                            }
                        }

                        if (!applied)
                        {
                            wmiFailure = OperationResult.Fail(
                                "Brightness fallback did not find a writable internal panel.",
                                controlPath: "brightness-wmi");
                        }
                        else
                        {
                            Thread.Sleep(120);

                            int verifiedBrightness;
                            if (!TryGetCurrentBrightness(out verifiedBrightness))
                            {
                                return OperationResult.Ok(
                                    "Brightness command was sent to the internal panel.",
                                    "brightness-wmi");
                            }

                            return OperationResult.Ok(
                                "Brightness restored to " + verifiedBrightness + "%.",
                                "brightness-wmi",
                                verifiedMode: verifiedBrightness.ToString());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                wmiFailure = OperationResult.Fail(
                    "Brightness fallback could not write the requested level.",
                    ex.ToString(),
                    "brightness-wmi");
            }

            int physicalVerifiedBrightness;
            string physicalError;
            if (TrySetPhysicalBrightness(brightnessLevel, out physicalVerifiedBrightness, out physicalError))
            {
                return OperationResult.Ok(
                    "Brightness restored to " + physicalVerifiedBrightness + "% through the monitor control API.",
                    "brightness-monitorapi",
                    verifiedMode: physicalVerifiedBrightness.ToString(),
                    warning: wmiFailure == null ? null : wmiFailure.Message);
            }

            return OperationResult.Fail(
                wmiFailure == null
                    ? "Brightness control is unavailable on the active panel right now."
                    : wmiFailure.Message,
                string.IsNullOrWhiteSpace(physicalError)
                    ? (wmiFailure == null ? null : wmiFailure.ErrorDetail)
                    : CombineErrorDetails(
                        wmiFailure == null ? null : wmiFailure.ErrorDetail,
                        "Monitor API fallback: " + physicalError),
                "brightness-monitorapi");
        }

        public static string TryRecoverAfterRouteChange(
            string requestedRoute,
            OperationResult routeResult,
            DisplayTransitionContext context)
        {
            requestedRoute = GpuManager.NormalizeRouteMode(requestedRoute);
            if (routeResult == null || !routeResult.Success)
            {
                return string.Empty;
            }

            var notes = new List<string>();

            if (requestedRoute == "dgpu" && !GpuRouteDetector.HasExternalDisplay())
            {
                if (RunDisplaySwitchInternalOnly())
                {
                    notes.Add("Windows display mode was refreshed to the internal panel.");
                }

                string displayModeRecoveryNote;
                if (TryReapplyPrimaryDisplayMode(context, out displayModeRecoveryNote))
                {
                    notes.Add(displayModeRecoveryNote);
                }
            }

            if (context != null && context.BrightnessSupported && context.BrightnessLevel >= 0)
            {
                int currentBrightness;
                if (TryGetCurrentBrightness(out currentBrightness) &&
                    currentBrightness >= 0 &&
                    Math.Abs(currentBrightness - context.BrightnessLevel) >= 10)
                {
                    var brightnessResult = SetBrightness(context.BrightnessLevel);
                    if (brightnessResult.Success)
                    {
                        notes.Add("Brightness fallback restored the panel level to " + context.BrightnessLevel + "%.");
                    }
                }
            }

            return string.Join(" ", notes.ToArray());
        }

        private static bool TryGetCurrentBrightness(out int brightnessLevel)
        {
            brightnessLevel = -1;

            try
            {
                using (var mclass = new ManagementClass("WmiMonitorBrightness"))
                {
                    mclass.Scope = new ManagementScope(@"\\.\root\wmi");

                    using (var instances = mclass.GetInstances())
                    {
                        foreach (ManagementObject instance in instances)
                        {
                            using (instance)
                            {
                                var activeValue = instance["Active"];
                                var currentValue = instance["CurrentBrightness"];
                                if (activeValue is bool && !(bool)activeValue)
                                {
                                    continue;
                                }

                                if (currentValue == null)
                                {
                                    continue;
                                }

                                brightnessLevel = Convert.ToInt32(currentValue);
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Brightness probe failed: " + ex.Message);
            }

            string physicalError;
            if (TryGetPhysicalBrightness(out brightnessLevel, out physicalError))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(physicalError))
            {
                AppLogger.Write("Physical brightness probe failed: " + physicalError);
            }

            return false;
        }

        private static string BuildDisplayWarning(AppState state)
        {
            if (state.RequiresRestart && state.GpuPendingRouteMode != "unknown")
            {
                return "Display recovery is waiting for restart because the native route change is still pending.";
            }

            if (state.GpuRouteMode == "dgpu")
            {
                var brightnessText = state.BrightnessSupported
                    ? ("App brightness fallback is ready at " + state.BrightnessLevel + "%.")
                    : "Brightness fallback is unavailable right now.";

                return
                    "Dedicated route is active. Dell documents DPI scale drift on this platform after dGPU-only switches, " +
                    "and NVIDIA documents that Windows may show Display 1 disabled and Display 2 active even though it is still the one built-in laptop panel. " +
                    brightnessText;
            }

            if (state.GpuRouteControlPath == "nvidia-panel" &&
                state.GpuRouteMode == "hybrid" &&
                state.GpuPanelMode == "dgpu")
            {
                return
                    "Windows still reports the desktop on Intel graphics while the NVIDIA panel status is discrete. " +
                    "Dell documents this stale panel-state condition and recommends normalizing through Optimus before retrying Auto Select.";
            }

            if (state.BrightnessSupported && state.BrightnessLevel >= 0)
            {
                return "Internal-panel brightness fallback is ready at " + state.BrightnessLevel + "%.";
            }

            return "Display recovery is watching the internal panel state.";
        }

        private static bool RunDisplaySwitchInternalOnly()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = DisplaySwitchPath,
                    Arguments = "/internal",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    if (!process.WaitForExit(10000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        return false;
                    }
                }

                Thread.Sleep(800);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write("DisplaySwitch /internal failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryCapturePrimaryDisplayMode(
            out string deviceName,
            out DisplayNativeMethods.DEVMODE mode)
        {
            deviceName = null;
            mode = DisplayNativeMethods.CreateDevMode();

            try
            {
                var displayDevice = DisplayNativeMethods.CreateDisplayDevice();
                for (var index = 0; index < 16; index++)
                {
                    displayDevice = DisplayNativeMethods.CreateDisplayDevice();
                    if (!DisplayNativeMethods.EnumDisplayDevices(null, index, ref displayDevice, 0))
                    {
                        break;
                    }

                    if ((displayDevice.StateFlags & DisplayNativeMethods.DisplayDeviceAttachedToDesktop) == 0)
                    {
                        continue;
                    }

                    if ((displayDevice.StateFlags & DisplayNativeMethods.DisplayDevicePrimaryDevice) == 0)
                    {
                        continue;
                    }

                    deviceName = string.IsNullOrWhiteSpace(displayDevice.DeviceName)
                        ? null
                        : displayDevice.DeviceName.Trim();
                    break;
                }

                if (!DisplayNativeMethods.EnumDisplaySettings(deviceName, DisplayNativeMethods.EnumCurrentSettings, ref mode))
                {
                    deviceName = null;
                    mode = DisplayNativeMethods.CreateDevMode();
                    if (!DisplayNativeMethods.EnumDisplaySettings(null, DisplayNativeMethods.EnumCurrentSettings, ref mode))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write("Display mode capture failed: " + ex.Message);
                deviceName = null;
                mode = DisplayNativeMethods.CreateDevMode();
                return false;
            }
        }

        private static bool TryReapplyPrimaryDisplayMode(
            DisplayTransitionContext context,
            out string note)
        {
            note = null;

            if (context == null || !context.DisplayModeCaptured)
            {
                return false;
            }

            try
            {
                var mode = context.PrimaryDisplayMode;
                mode.dmSize = (short)Marshal.SizeOf(typeof(DisplayNativeMethods.DEVMODE));

                var result = DisplayNativeMethods.ChangeDisplaySettingsEx(
                    context.PrimaryDisplayDeviceName,
                    ref mode,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero);

                if (result == DisplayNativeMethods.DispChangeSuccessful)
                {
                    Thread.Sleep(500);
                    note = "The primary display mode was re-applied to normalize size and DPI drift.";
                    return true;
                }

                if (result == DisplayNativeMethods.DispChangeRestart)
                {
                    note = "Windows accepted the display normalization, but it requested a restart before the mode reset becomes active.";
                    return true;
                }

                AppLogger.Write("Primary display mode re-apply failed with code " + result + ".");
            }
            catch (Exception ex)
            {
                AppLogger.Write("Primary display mode re-apply failed: " + ex.Message);
            }

            return false;
        }

        private static bool TryGetPhysicalBrightness(out int brightnessLevel, out string errorDetail)
        {
            brightnessLevel = -1;
            errorDetail = null;

            DisplayNativeMethods.PHYSICAL_MONITOR[] physicalMonitors;
            if (!TryOpenPrimaryPhysicalMonitors(out physicalMonitors, out errorDetail))
            {
                return false;
            }

            try
            {
                for (var index = 0; index < physicalMonitors.Length; index++)
                {
                    uint minimum;
                    uint current;
                    uint maximum;
                    uint capabilities;
                    uint supportedColorTemperatures;

                    if (!DisplayNativeMethods.GetMonitorCapabilities(
                            physicalMonitors[index].hPhysicalMonitor,
                            out capabilities,
                            out supportedColorTemperatures) ||
                        (capabilities & DisplayNativeMethods.McCapsBrightness) == 0)
                    {
                        continue;
                    }

                    if (!DisplayNativeMethods.GetMonitorBrightness(
                            physicalMonitors[index].hPhysicalMonitor,
                            out minimum,
                            out current,
                            out maximum))
                    {
                        continue;
                    }

                    brightnessLevel = NormalizeBrightness(current, minimum, maximum);
                    return true;
                }

                errorDetail = "No physical monitor reported brightness control support.";
                return false;
            }
            finally
            {
                DisplayNativeMethods.DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
            }
        }

        private static bool TrySetPhysicalBrightness(
            int brightnessLevel,
            out int verifiedBrightness,
            out string errorDetail)
        {
            verifiedBrightness = brightnessLevel;
            errorDetail = null;

            DisplayNativeMethods.PHYSICAL_MONITOR[] physicalMonitors;
            if (!TryOpenPrimaryPhysicalMonitors(out physicalMonitors, out errorDetail))
            {
                return false;
            }

            try
            {
                for (var index = 0; index < physicalMonitors.Length; index++)
                {
                    uint minimum;
                    uint current;
                    uint maximum;
                    uint capabilities;
                    uint supportedColorTemperatures;

                    if (!DisplayNativeMethods.GetMonitorCapabilities(
                            physicalMonitors[index].hPhysicalMonitor,
                            out capabilities,
                            out supportedColorTemperatures) ||
                        (capabilities & DisplayNativeMethods.McCapsBrightness) == 0)
                    {
                        continue;
                    }

                    if (!DisplayNativeMethods.GetMonitorBrightness(
                            physicalMonitors[index].hPhysicalMonitor,
                            out minimum,
                            out current,
                            out maximum))
                    {
                        continue;
                    }

                    var nativeBrightness = DenormalizeBrightness(brightnessLevel, minimum, maximum);
                    if (!DisplayNativeMethods.SetMonitorBrightness(
                            physicalMonitors[index].hPhysicalMonitor,
                            nativeBrightness))
                    {
                        continue;
                    }

                    Thread.Sleep(120);

                    uint verifyMinimum;
                    uint verifyCurrent;
                    uint verifyMaximum;
                    if (DisplayNativeMethods.GetMonitorBrightness(
                            physicalMonitors[index].hPhysicalMonitor,
                            out verifyMinimum,
                            out verifyCurrent,
                            out verifyMaximum))
                    {
                        verifiedBrightness = NormalizeBrightness(
                            verifyCurrent,
                            verifyMinimum,
                            verifyMaximum);
                    }
                    else
                    {
                        verifiedBrightness = brightnessLevel;
                    }

                    return true;
                }

                errorDetail = "No physical monitor accepted the requested brightness value.";
                return false;
            }
            finally
            {
                DisplayNativeMethods.DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
            }
        }

        private static bool TryOpenPrimaryPhysicalMonitors(
            out DisplayNativeMethods.PHYSICAL_MONITOR[] physicalMonitors,
            out string errorDetail)
        {
            physicalMonitors = null;
            errorDetail = null;

            var monitorHandle = DisplayNativeMethods.MonitorFromPoint(new DisplayNativeMethods.POINT { X = 0, Y = 0 }, DisplayNativeMethods.MonitorDefaultToPrimary);
            if (monitorHandle == IntPtr.Zero)
            {
                errorDetail = "MonitorFromPoint returned no primary monitor handle.";
                return false;
            }

            uint monitorCount;
            if (!DisplayNativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(monitorHandle, out monitorCount) || monitorCount == 0)
            {
                errorDetail = "Windows did not expose any physical monitor handles for the primary display.";
                return false;
            }

            physicalMonitors = new DisplayNativeMethods.PHYSICAL_MONITOR[monitorCount];
            if (!DisplayNativeMethods.GetPhysicalMonitorsFromHMONITOR(monitorHandle, monitorCount, physicalMonitors))
            {
                errorDetail = "Windows could not open the primary physical monitor handles.";
                physicalMonitors = null;
                return false;
            }

            return true;
        }

        private static int NormalizeBrightness(uint current, uint minimum, uint maximum)
        {
            if (maximum <= minimum)
            {
                return (int)Math.Max(0, Math.Min(100, current));
            }

            var scaled = (double)(current - minimum) * 100.0 / (double)(maximum - minimum);
            return (int)Math.Max(0, Math.Min(100, Math.Round(scaled)));
        }

        private static uint DenormalizeBrightness(int brightnessLevel, uint minimum, uint maximum)
        {
            if (brightnessLevel < 0)
            {
                brightnessLevel = 0;
            }
            else if (brightnessLevel > 100)
            {
                brightnessLevel = 100;
            }

            if (maximum <= minimum)
            {
                return (uint)brightnessLevel;
            }

            return minimum + (uint)Math.Round((maximum - minimum) * (brightnessLevel / 100.0));
        }

        private static string CombineErrorDetails(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first))
            {
                return second;
            }

            if (string.IsNullOrWhiteSpace(second))
            {
                return first;
            }

            return first + Environment.NewLine + second;
        }

        internal static class DisplayNativeMethods
        {
            public const int EnumCurrentSettings = -1;
            public const int DispChangeSuccessful = 0;
            public const int DispChangeRestart = 1;
            public const int DisplayDeviceAttachedToDesktop = 0x00000001;
            public const int DisplayDevicePrimaryDevice = 0x00000004;
            public const uint McCapsBrightness = 0x00000002;
            public const uint MonitorDefaultToPrimary = 0x00000001;

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct DISPLAY_DEVICE
            {
                public int cb;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                public string DeviceName;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
                public string DeviceString;

                public int StateFlags;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
                public string DeviceID;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
                public string DeviceKey;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct DEVMODE
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                public string dmDeviceName;
                public short dmSpecVersion;
                public short dmDriverVersion;
                public short dmSize;
                public short dmDriverExtra;
                public int dmFields;
                public int dmPositionX;
                public int dmPositionY;
                public int dmDisplayOrientation;
                public int dmDisplayFixedOutput;
                public short dmColor;
                public short dmDuplex;
                public short dmYResolution;
                public short dmTTOption;
                public short dmCollate;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                public string dmFormName;
                public short dmLogPixels;
                public int dmBitsPerPel;
                public int dmPelsWidth;
                public int dmPelsHeight;
                public int dmDisplayFlags;
                public int dmDisplayFrequency;
                public int dmICMMethod;
                public int dmICMIntent;
                public int dmMediaType;
                public int dmDitherType;
                public int dmReserved1;
                public int dmReserved2;
                public int dmPanningWidth;
                public int dmPanningHeight;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct PHYSICAL_MONITOR
            {
                public IntPtr hPhysicalMonitor;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
                public string szPhysicalMonitorDescription;
            }

            public static DISPLAY_DEVICE CreateDisplayDevice()
            {
                return new DISPLAY_DEVICE
                {
                    cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE))
                };
            }

            public static DEVMODE CreateDevMode()
            {
                return new DEVMODE
                {
                    dmSize = (short)Marshal.SizeOf(typeof(DEVMODE))
                };
            }

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern bool EnumDisplayDevices(
                string lpDevice,
                int iDevNum,
                ref DISPLAY_DEVICE lpDisplayDevice,
                int dwFlags);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern bool EnumDisplaySettings(
                string lpszDeviceName,
                int iModeNum,
                ref DEVMODE lpDevMode);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int ChangeDisplaySettingsEx(
                string lpszDeviceName,
                ref DEVMODE lpDevMode,
                IntPtr hwnd,
                int dwflags,
                IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

            [DllImport("dxva2.dll", SetLastError = true)]
            public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
                IntPtr hMonitor,
                out uint pdwNumberOfPhysicalMonitors);

            [DllImport("dxva2.dll", SetLastError = true)]
            public static extern bool GetPhysicalMonitorsFromHMONITOR(
                IntPtr hMonitor,
                uint dwPhysicalMonitorArraySize,
                [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

            [DllImport("dxva2.dll", SetLastError = true)]
            public static extern bool DestroyPhysicalMonitors(
                uint dwPhysicalMonitorArraySize,
                [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

            [DllImport("dxva2.dll", SetLastError = true)]
            public static extern bool GetMonitorCapabilities(
                IntPtr hMonitor,
                out uint pdwMonitorCapabilities,
                out uint pdwSupportedColorTemperatures);

            [DllImport("dxva2.dll", SetLastError = true)]
            public static extern bool GetMonitorBrightness(
                IntPtr hMonitor,
                out uint pdwMinimumBrightness,
                out uint pdwCurrentBrightness,
                out uint pdwMaximumBrightness);

            [DllImport("dxva2.dll", SetLastError = true)]
            public static extern bool SetMonitorBrightness(
                IntPtr hMonitor,
                uint dwNewBrightness);
        }
    }
}
