using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Forms;
using GSwitcher.Backend;
using GSwitcher.Services;

namespace GSwitcher
{
    public partial class TelemetryHudWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;

        private readonly Func<string> _getPowerMode;
        private readonly DispatcherTimer _timer;
        private PerformanceCounter _cpuCounter;

        public TelemetryHudWindow(Func<string> getPowerMode)
        {
            InitializeComponent();
            _getPowerMode = getPowerMode;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += delegate { RefreshText(); };
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint DwLength;
            public uint DwMemoryLoad;
            public ulong UllTotalPhys;
            public ulong UllAvailPhys;
            public ulong UllTotalPageFile;
            public ulong UllAvailPageFile;
            public ulong UllTotalVirtual;
            public ulong UllAvailVirtual;
            public ulong UllAvailExtendedVirtual;
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExLayered);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PositionTopRight();
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();
            }
            catch (Exception ex)
            {
                AppLogger.Write("Telemetry HUD CPU counter unavailable: " + ex.Message);
            }

            RefreshText();
            _timer.Start();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _timer.Stop();
            if (_cpuCounter != null)
            {
                _cpuCounter.Dispose();
                _cpuCounter = null;
            }
        }

        private void PositionTopRight()
        {
            var screen = Screen.PrimaryScreen.WorkingArea;
            Left = screen.Right - Width - 18;
            Top = screen.Top + 18;
        }

        private void RefreshText()
        {
            var power = PowerManager.DescribeMode(_getPowerMode == null ? "unknown" : _getPowerMode()).ToUpperInvariant();
            var cpu = GetCpuUsage();
            var ram = GetMemoryLoad();
            HudText.Text = string.Format("{0}  |  CPU {1}%  |  RAM {2}%", power, cpu, ram);
        }

        private int GetCpuUsage()
        {
            try
            {
                if (_cpuCounter == null)
                {
                    return 0;
                }

                return Math.Max(0, Math.Min(100, (int)Math.Round(_cpuCounter.NextValue())));
            }
            catch
            {
                return 0;
            }
        }

        private static int GetMemoryLoad()
        {
            var status = new MemoryStatusEx();
            status.DwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            return GlobalMemoryStatusEx(ref status)
                ? Math.Max(0, Math.Min(100, (int)status.DwMemoryLoad))
                : 0;
        }
    }
}
