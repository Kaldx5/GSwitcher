using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;

namespace GSwitcher.Backend
{
    public sealed class GpuRouteSnapshot
    {
        public string Mode { get; private set; }
        public string Source { get; private set; }
        public string Evidence { get; private set; }

        public static GpuRouteSnapshot Create(string mode, string source, string evidence)
        {
            return new GpuRouteSnapshot
            {
                Mode = GpuManager.NormalizeRouteMode(mode),
                Source = NormalizeSource(source),
                Evidence = string.IsNullOrWhiteSpace(evidence)
                    ? "GPU route verification is waiting for more evidence."
                    : evidence.Trim()
            };
        }

        public static GpuRouteSnapshot Unknown(string evidence)
        {
            return Create("unknown", "unknown", evidence);
        }

        private static string NormalizeSource(string source)
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
    }

    internal static class GpuRouteDetector
    {
        private const int DisplayDeviceAttachedToDesktop = 0x00000001;
        private const int DisplayDevicePrimaryDevice = 0x00000004;
        private const int DisplayDeviceMirroringDriver = 0x00000008;

        private sealed class DesktopAdapterState
        {
            public string DeviceName;
            public string Name;
            public string DeviceId;
            public string DeviceKey;
            public int StateFlags;
            public bool Primary;
            public readonly List<MonitorState> Monitors = new List<MonitorState>();
        }

        private sealed class MonitorState
        {
            public string Name;
            public string DeviceId;
        }

        private sealed class VideoControllerState
        {
            public string Name;
            public string PnpDeviceId;
            public int Width;
            public int Height;
        }

        public static GpuRouteSnapshot Detect()
        {
            try
            {
                var desktopAdapters = EnumerateDesktopAdapters();
                if (desktopAdapters.Count > 0)
                {
                    return BuildDesktopOwnershipSnapshot(desktopAdapters);
                }

                return DetectWithVideoControllerFallback(
                    "EnumDisplayDevices did not report any attached desktop adapters.");
            }
            catch (Exception ex)
            {
                return DetectWithVideoControllerFallback(
                    "Display-topology detection fell back after a Win32 error: " + ex.Message);
            }
        }

        public static bool HasExternalDisplay()
        {
            try
            {
                var adapters = EnumerateDesktopAdapters();
                var activeMonitorCount = 0;

                for (var adapterIndex = 0; adapterIndex < adapters.Count; adapterIndex++)
                {
                    activeMonitorCount += adapters[adapterIndex].Monitors.Count;
                }

                return activeMonitorCount > 1;
            }
            catch
            {
                return false;
            }
        }

        private static GpuRouteSnapshot BuildDesktopOwnershipSnapshot(List<DesktopAdapterState> adapters)
        {
            var intelAttached = false;
            var nvidiaAttached = false;
            var intelPrimary = false;
            var nvidiaPrimary = false;

            for (var index = 0; index < adapters.Count; index++)
            {
                var adapter = adapters[index];
                if (IsIntelAdapter(adapter))
                {
                    intelAttached = true;
                    intelPrimary = intelPrimary || adapter.Primary;
                }

                if (IsNvidiaAdapter(adapter))
                {
                    nvidiaAttached = true;
                    nvidiaPrimary = nvidiaPrimary || adapter.Primary;
                }
            }

            var evidence = BuildDesktopEvidence(adapters);

            if (nvidiaPrimary || (nvidiaAttached && !intelAttached))
            {
                return GpuRouteSnapshot.Create(
                    "dgpu",
                    "display-topology",
                    "Windows display ownership shows the active desktop path on NVIDIA graphics. " + evidence);
            }

            if (intelPrimary || (intelAttached && !nvidiaAttached))
            {
                return GpuRouteSnapshot.Create(
                    "hybrid",
                    "display-topology",
                    "Windows display ownership shows the active desktop path on Intel graphics. " + evidence);
            }

            if (intelAttached && nvidiaAttached)
            {
                return GpuRouteSnapshot.Create(
                    "hybrid",
                    "display-topology",
                    "Intel and NVIDIA both own active desktop outputs, so the machine is still in a hybrid display topology. " + evidence);
            }

            return DetectWithVideoControllerFallback(
                "Desktop adapters were enumerated, but neither Intel nor NVIDIA could be identified. " + evidence);
        }

        private static string BuildDesktopEvidence(List<DesktopAdapterState> adapters)
        {
            if (adapters == null || adapters.Count == 0)
            {
                return "Windows did not enumerate any active desktop adapters.";
            }

            var segments = new List<string>();
            for (var index = 0; index < adapters.Count; index++)
            {
                var adapter = adapters[index];
                var role = adapter.Primary ? "primary" : "attached";
                var vendor = DescribeVendor(adapter);
                var monitors = BuildMonitorSummary(adapter.Monitors);

                segments.Add(
                    role + " " + vendor + " adapter `" +
                    (string.IsNullOrWhiteSpace(adapter.Name) ? adapter.DeviceName : adapter.Name) +
                    "` monitors=" + monitors);
            }

            return string.Join(" | ", segments.ToArray());
        }

        private static string BuildMonitorSummary(List<MonitorState> monitors)
        {
            if (monitors == null || monitors.Count == 0)
            {
                return "none";
            }

            var labels = new List<string>();
            for (var index = 0; index < monitors.Count; index++)
            {
                var monitor = monitors[index];
                var label = ShortMonitorId(monitor.DeviceId);
                if (string.IsNullOrWhiteSpace(label))
                {
                    label = string.IsNullOrWhiteSpace(monitor.Name) ? "display" : monitor.Name.Trim();
                }

                labels.Add(label);
            }

            return string.Join(", ", labels.ToArray());
        }

        private static string ShortMonitorId(string deviceId)
        {
            deviceId = (deviceId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return string.Empty;
            }

            var parts = deviceId.Split('\\');
            if (parts.Length >= 2)
            {
                return parts[0] + "\\" + parts[1];
            }

            return deviceId;
        }

        private static string DescribeVendor(DesktopAdapterState adapter)
        {
            if (IsIntelAdapter(adapter))
            {
                return "Intel";
            }

            if (IsNvidiaAdapter(adapter))
            {
                return "NVIDIA";
            }

            return "unknown";
        }

        private static List<DesktopAdapterState> EnumerateDesktopAdapters()
        {
            var adapters = new List<DesktopAdapterState>();

            for (var adapterIndex = 0; adapterIndex < 32; adapterIndex++)
            {
                var displayDevice = CreateDisplayDevice();
                if (!NativeMethods.EnumDisplayDevices(null, adapterIndex, ref displayDevice, 0))
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(displayDevice.DeviceName))
                {
                    continue;
                }

                if ((displayDevice.StateFlags & DisplayDeviceMirroringDriver) != 0)
                {
                    continue;
                }

                if ((displayDevice.StateFlags & DisplayDeviceAttachedToDesktop) == 0)
                {
                    continue;
                }

                var adapter = new DesktopAdapterState
                {
                    DeviceName = displayDevice.DeviceName,
                    Name = displayDevice.DeviceString,
                    DeviceId = displayDevice.DeviceID,
                    DeviceKey = displayDevice.DeviceKey,
                    StateFlags = displayDevice.StateFlags,
                    Primary = (displayDevice.StateFlags & DisplayDevicePrimaryDevice) != 0
                };

                for (var monitorIndex = 0; monitorIndex < 16; monitorIndex++)
                {
                    var monitorDevice = CreateDisplayDevice();
                    if (!NativeMethods.EnumDisplayDevices(adapter.DeviceName, monitorIndex, ref monitorDevice, 0))
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(monitorDevice.DeviceName) &&
                        string.IsNullOrWhiteSpace(monitorDevice.DeviceString) &&
                        string.IsNullOrWhiteSpace(monitorDevice.DeviceID))
                    {
                        continue;
                    }

                    adapter.Monitors.Add(new MonitorState
                    {
                        Name = monitorDevice.DeviceString,
                        DeviceId = monitorDevice.DeviceID
                    });
                }

                adapters.Add(adapter);
            }

            return adapters;
        }

        private static NativeMethods.DISPLAY_DEVICE CreateDisplayDevice()
        {
            return new NativeMethods.DISPLAY_DEVICE
            {
                cb = Marshal.SizeOf(typeof(NativeMethods.DISPLAY_DEVICE))
            };
        }

        private static GpuRouteSnapshot DetectWithVideoControllerFallback(string reason)
        {
            try
            {
                var adapters = new List<VideoControllerState>();

                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name,PNPDeviceID,CurrentHorizontalResolution,CurrentVerticalResolution FROM Win32_VideoController"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        using (item)
                        {
                            adapters.Add(new VideoControllerState
                            {
                                Name = Convert.ToString(item["Name"]) ?? string.Empty,
                                PnpDeviceId = Convert.ToString(item["PNPDeviceID"]) ?? string.Empty,
                                Width = SafeToInt(item["CurrentHorizontalResolution"]),
                                Height = SafeToInt(item["CurrentVerticalResolution"])
                            });
                        }
                    }
                }

                var intelActive = false;
                var nvidiaActive = false;

                for (var index = 0; index < adapters.Count; index++)
                {
                    var adapter = adapters[index];
                    var isActive = adapter.Width > 0 && adapter.Height > 0;

                    if (IsIntelAdapter(adapter))
                    {
                        intelActive = intelActive || isActive;
                    }

                    if (IsNvidiaAdapter(adapter))
                    {
                        nvidiaActive = nvidiaActive || isActive;
                    }
                }

                if (intelActive && !nvidiaActive)
                {
                    return GpuRouteSnapshot.Create(
                        "hybrid",
                        "display-topology",
                        reason + " Fallback Win32_VideoController data still reports the active laptop display path on Intel graphics.");
                }

                if (nvidiaActive && !intelActive)
                {
                    return GpuRouteSnapshot.Create(
                        "dgpu",
                        "display-topology",
                        reason + " Fallback Win32_VideoController data still reports the active laptop display path on the NVIDIA GPU.");
                }

                if (intelActive && nvidiaActive)
                {
                    return GpuRouteSnapshot.Create(
                        "hybrid",
                        "display-topology",
                        reason + " Fallback Win32_VideoController data reports Intel and NVIDIA active at the same time.");
                }

                return GpuRouteSnapshot.Unknown(
                    reason + " Windows still did not report an active Intel or NVIDIA display owner for the current session.");
            }
            catch (Exception ex)
            {
                return GpuRouteSnapshot.Unknown(reason + " Fallback detection also failed: " + ex.Message);
            }
        }

        private static bool IsIntelAdapter(DesktopAdapterState adapter)
        {
            if (adapter == null)
            {
                return false;
            }

            return ContainsIntelVendor(adapter.DeviceId) ||
                   ContainsIntelVendor(adapter.DeviceKey) ||
                   ContainsIntelName(adapter.Name);
        }

        private static bool IsNvidiaAdapter(DesktopAdapterState adapter)
        {
            if (adapter == null)
            {
                return false;
            }

            return ContainsNvidiaVendor(adapter.DeviceId) ||
                   ContainsNvidiaVendor(adapter.DeviceKey) ||
                   ContainsNvidiaName(adapter.Name);
        }

        private static bool IsIntelAdapter(VideoControllerState adapter)
        {
            if (adapter == null)
            {
                return false;
            }

            return ContainsIntelVendor(adapter.PnpDeviceId) || ContainsIntelName(adapter.Name);
        }

        private static bool IsNvidiaAdapter(VideoControllerState adapter)
        {
            if (adapter == null)
            {
                return false;
            }

            return ContainsNvidiaVendor(adapter.PnpDeviceId) || ContainsNvidiaName(adapter.Name);
        }

        private static bool ContainsIntelVendor(string value)
        {
            return (value ?? string.Empty).IndexOf("VEN_8086", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsNvidiaVendor(string value)
        {
            return (value ?? string.Empty).IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsIntelName(string value)
        {
            return (value ?? string.Empty).IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsNvidiaName(string value)
        {
            return (value ?? string.Empty).IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int SafeToInt(object value)
        {
            if (value == null)
            {
                return 0;
            }

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static class NativeMethods
        {
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

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern bool EnumDisplayDevices(
                string lpDevice,
                int iDevNum,
                ref DISPLAY_DEVICE lpDisplayDevice,
                int dwFlags);
        }
    }
}
