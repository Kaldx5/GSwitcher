using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using GSwitcher.Models;
using GSwitcher.Services;
using GSwitcher.ViewModels;

namespace GSwitcher
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private AppState _currentState;
        private bool _uiReady;
        private DateTime _ignoreDeactivateUntilUtc = DateTime.MinValue;

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(
            int nLeftRect,
            int nTopRect,
            int nRightRect,
            int nBottomRect,
            int nWidthEllipse,
            int nHeightEllipse);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public MainWindow(MainViewModel viewModel)
        {
            _viewModel = viewModel ?? new MainViewModel(AppState.CreateDefault(), null);
            _currentState = _viewModel.State ?? AppState.CreateDefault();

            InitializeComponent();

            SourceInitialized += MainWindow_SourceInitialized;
            Deactivated += MainWindow_Deactivated;
            Loaded += MainWindow_Loaded;
            SizeChanged += MainWindow_SizeChanged;
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            ApplyRoundedWindowRegion();
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyRoundedWindowRegion();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_uiReady)
            {
                return;
            }

            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2UserDataDirectory);
                await DashboardView.EnsureCoreWebView2Async(environment);
                DashboardView.ZoomFactor = 1.0;

                DashboardView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                DashboardView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                DashboardView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                DashboardView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                DashboardView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                DashboardView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;

                var uiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "index.html");
                if (!File.Exists(uiPath))
                {
                    throw new FileNotFoundException("Dashboard UI file was not found.", uiPath);
                }

                DashboardView.CoreWebView2.Navigate(new Uri(uiPath).AbsoluteUri);
            }
            catch (Exception ex)
            {
                AppLogger.Write("WebView2 startup failed: " + ex);
                PushStatus("WebView2 failed to initialize. Make sure the Microsoft Edge WebView2 runtime is installed.", true);
            }
        }

        private async void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _uiReady = e.IsSuccess;
            if (_uiReady)
            {
                await FitDashboardToWindowAsync();
                UpdateState(_currentState);
            }
            else
            {
                AppLogger.Write("Dashboard navigation failed: " + e.WebErrorStatus);
            }
        }

        private async Task FitDashboardToWindowAsync()
        {
            try
            {
                // WebView2 can report a smaller CSS viewport than its WPF host at high DPI.
                // The fixed-size dashboard then clips its footer and both menu buttons.
                var widthJson = await DashboardView.CoreWebView2.ExecuteScriptAsync("window.innerWidth");
                var heightJson = await DashboardView.CoreWebView2.ExecuteScriptAsync("window.innerHeight");
                double viewportWidth;
                double viewportHeight;
                if (!double.TryParse(widthJson, NumberStyles.Float, CultureInfo.InvariantCulture, out viewportWidth) ||
                    !double.TryParse(heightJson, NumberStyles.Float, CultureInfo.InvariantCulture, out viewportHeight) ||
                    viewportWidth <= 0 || viewportHeight <= 0)
                {
                    AppLogger.Write("Dashboard viewport could not be measured: " + widthJson + "x" + heightJson);
                    return;
                }

                var widthScale = viewportWidth / Math.Max(1.0, DashboardView.ActualWidth);
                var heightScale = viewportHeight / Math.Max(1.0, DashboardView.ActualHeight);
                var scale = Math.Min(widthScale, heightScale);
                var targetZoom = Math.Max(0.5, Math.Min(2.0, DashboardView.ZoomFactor * scale));
                if (Math.Abs(targetZoom - DashboardView.ZoomFactor) > 0.01)
                {
                    DashboardView.ZoomFactor = targetZoom;
                }

                AppLogger.Write(string.Format(CultureInfo.InvariantCulture,
                    "Dashboard viewport {0}x{1}, host {2:F0}x{3:F0}, zoom {4:F3}.",
                    viewportWidth, viewportHeight, DashboardView.ActualWidth, DashboardView.ActualHeight, DashboardView.ZoomFactor));
            }
            catch (Exception ex)
            {
                AppLogger.Write("Dashboard zoom calibration failed: " + ex);
            }
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var message = e.TryGetWebMessageAsString();
            _viewModel.HandleWebMessage(message);
        }

        public void UpdateState(AppState state)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateState(state)));
                return;
            }

            _currentState = state ?? AppState.CreateDefault();
            _viewModel.UpdateState(_currentState);

            if (!_uiReady || DashboardView.CoreWebView2 == null)
            {
                return;
            }

            var settings = SettingsManager.Current;
            var payload = _serializer.Serialize(new
            {
                type = "state",
                state = new
                {
                    powerMode = _currentState.PowerMode,
                    gpuMode = _currentState.GpuMode,
                    gpuRouteMode = _currentState.GpuRouteMode,
                    gpuPreferenceMode = _currentState.GpuPreferenceMode,
                    gpuPanelMode = _currentState.GpuPanelMode,
                    theme = _currentState.Theme,
                    status = _currentState.LastStatus,
                    isBusy = _currentState.IsBusy,
                    busyLabel = _currentState.BusyLabel,
                    powerPlanHealth = _currentState.PowerPlanHealth,
                    powerPlanHealthState = _currentState.PowerPlanHealthState,
                    gpuControlPath = _currentState.GpuControlPath,
                    gpuRouteControlPath = _currentState.GpuRouteControlPath,
                    gpuPreferenceControlPath = _currentState.GpuPreferenceControlPath,
                    gpuPanelControlPath = _currentState.GpuPanelControlPath,
                    gpuRouteSource = _currentState.GpuRouteSource,
                    gpuRouteEvidence = _currentState.GpuRouteEvidence,
                    gpuPanelEvidence = _currentState.GpuPanelEvidence,
                    gpuPendingRouteMode = _currentState.GpuPendingRouteMode,
                    requiresRestart = _currentState.RequiresRestart,
                    isPinned = _currentState.IsPinned,
                    helperInstalled = _currentState.HelperInstalled,
                    evidenceSessionPath = _currentState.EvidenceSessionPath,
                    lastEvidenceCapturePath = _currentState.LastEvidenceCapturePath,
                    brightnessSupported = _currentState.BrightnessSupported,
                    brightnessLevel = _currentState.BrightnessLevel,
                    displayWarning = _currentState.DisplayWarning,
                    availableGpuModes = _currentState.AvailableGpuModes ?? new string[0],
                    availableGpuRouteModes = _currentState.AvailableGpuRouteModes ?? new string[0],
                    availableGpuPreferenceModes = _currentState.AvailableGpuPreferenceModes ?? new string[0],
                    settings = settings
                }
            });

            DashboardView.CoreWebView2.PostWebMessageAsJson(payload);
        }

        public void PushStatus(string message, bool isError)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => PushStatus(message, isError)));
                return;
            }

            _currentState.LastStatus = message;

            if (!_uiReady || DashboardView.CoreWebView2 == null)
            {
                return;
            }

            var payload = _serializer.Serialize(new
            {
                type = "status",
                message = message,
                isError = isError
            });

            DashboardView.CoreWebView2.PostWebMessageAsJson(payload);
        }

        public void PushEmergencyEco()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(PushEmergencyEco));
                return;
            }

            if (!_uiReady || DashboardView.CoreWebView2 == null)
            {
                return;
            }

            var payload = _serializer.Serialize(new
            {
                type = "emergency-eco"
            });

            DashboardView.CoreWebView2.PostWebMessageAsJson(payload);
        }

        public void PushThermalData(
            System.Collections.Generic.Dictionary<string, object> snapshot,
            System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> processes)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => PushThermalData(snapshot, processes)));
                return;
            }

            if (!_uiReady || DashboardView.CoreWebView2 == null)
            {
                return;
            }

            var payload = _serializer.Serialize(new
            {
                type = "thermal",
                snapshot = snapshot,
                processes = processes
            });

            DashboardView.CoreWebView2.PostWebMessageAsJson(payload);
        }

        public void PushProcessorSettings(object data)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => PushProcessorSettings(data)));
                return;
            }
            if (!_uiReady || DashboardView.CoreWebView2 == null) return;
            DashboardView.CoreWebView2.PostWebMessageAsJson(_serializer.Serialize(new { type = "processor-settings", data = data }));
        }

        public void ShowDashboard()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ShowDashboard));
                return;
            }

            _ignoreDeactivateUntilUtc = DateTime.UtcNow.AddSeconds(8);

            var workArea = SystemParameters.WorkArea;
            Left = Math.Max(workArea.Left + 16, workArea.Right - Width - 20);
            Top = Math.Max(workArea.Top + 16, workArea.Bottom - Height - 20);

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Show();
            Visibility = Visibility.Visible;
            Topmost = true;
            Activate();
            Focus();
            if (_currentState == null || !_currentState.IsPinned)
            {
                Topmost = false;
            }

            AppLogger.Write("Dashboard shown at " + Left + "," + Top + " size=" + Width + "x" + Height + ".");
        }

        public void ApplyPinnedState(bool pinned)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ApplyPinnedState(pinned)));
                return;
            }

            Topmost = pinned;
        }

        public void HideWindow()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(HideWindow));
                return;
            }

            Hide();
        }

        public void BeginWindowDrag()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(BeginWindowDrag));
                return;
            }

            try
            {
                if (Mouse.LeftButton == MouseButtonState.Pressed)
                {
                    DragMove();
                }
            }
            catch
            {
            }
        }

        public void PrepareForShutdown()
        {
            try
            {
                if (DashboardView != null)
                {
                    if (DashboardView.CoreWebView2 != null)
                    {
                        DashboardView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                        DashboardView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
                        DashboardView.CoreWebView2.Stop();
                    }

                    DashboardView.Dispose();
                }
            }
            catch
            {
            }

            try
            {
                Hide();
                Close();
            }
            catch
            {
            }
        }

        private void MainWindow_Deactivated(object sender, EventArgs e)
        {
            if (!_uiReady)
            {
                return;
            }

            if (DateTime.UtcNow < _ignoreDeactivateUntilUtc)
            {
                return;
            }

            if (_currentState != null && _currentState.IsPinned)
            {
                return;
            }

            if (_currentState != null && _currentState.IsBusy)
            {
                return;
            }

            HideWindow();
        }

        private void ApplyRoundedWindowRegion()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0)
                {
                    return;
                }

                var source = PresentationSource.FromVisual(this);
                var transform = source == null ? default(System.Windows.Media.Matrix) : source.CompositionTarget.TransformToDevice;
                var scaleX = transform.M11 == 0 ? 1.0 : transform.M11;
                var scaleY = transform.M22 == 0 ? 1.0 : transform.M22;

                var pixelWidth = Math.Max(1, (int)Math.Round(ActualWidth * scaleX));
                var pixelHeight = Math.Max(1, (int)Math.Round(ActualHeight * scaleY));
                var cornerDiameter = Math.Max(44, (int)Math.Round(80 * Math.Min(scaleX, scaleY)));

                var region = CreateRoundRectRgn(0, 0, pixelWidth + 1, pixelHeight + 1, cornerDiameter, cornerDiameter);
                if (region == IntPtr.Zero)
                {
                    return;
                }

                if (SetWindowRgn(hwnd, region, true) == 0)
                {
                    DeleteObject(region);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Rounded window region failed: " + ex.Message);
            }
        }

    }
}
