using System;
using System.ServiceProcess;
using GSwitcher.Backend;

namespace GSwitcher.Services
{
    public sealed class NetworkFocusService : IDisposable
    {
        private bool _pausedBits;
        private string _lastMode = string.Empty;

        public void Refresh(string powerMode, bool enabled, Action<string, bool> pushStatus)
        {
            powerMode = PowerManager.NormalizeMode(powerMode);
            if (!enabled || powerMode != "tested-ultra")
            {
                ResumeIfOwned(pushStatus);
                _lastMode = powerMode;
                return;
            }

            if (_pausedBits && _lastMode == "tested-ultra")
            {
                return;
            }

            TryPauseBits(pushStatus);
            _lastMode = powerMode;
        }

        private void TryPauseBits(Action<string, bool> pushStatus)
        {
            try
            {
                using (var service = new ServiceController("BITS"))
                {
                    service.Refresh();
                    if (service.Status == ServiceControllerStatus.Paused)
                    {
                        AppLogger.Write("Network focus: BITS was already paused; GSwitcher will not claim ownership.");
                        return;
                    }

                    if (service.Status != ServiceControllerStatus.Running)
                    {
                        AppLogger.Write("Network focus: BITS is not running (" + service.Status + ").");
                        return;
                    }

                    if (!service.CanPauseAndContinue)
                    {
                        AppLogger.Write("Network focus: BITS does not expose pause/continue on this Windows build.");
                        if (pushStatus != null)
                        {
                            pushStatus("Network focus is unavailable: BITS cannot be paused on this Windows build.", true);
                        }
                        return;
                    }

                    service.Pause();
                    service.WaitForStatus(ServiceControllerStatus.Paused, TimeSpan.FromSeconds(3));
                    _pausedBits = true;
                    AppLogger.Write("Network focus: BITS paused while Ultra is active.");
                    if (pushStatus != null)
                    {
                        pushStatus("Network focus paused background transfers for Ultra.", false);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Network focus pause failed: " + ex.Message);
                if (pushStatus != null)
                {
                    pushStatus("Network focus could not pause background transfers.", true);
                }
            }
        }

        public void ResumeIfOwned(Action<string, bool> pushStatus)
        {
            if (!_pausedBits)
            {
                return;
            }

            try
            {
                using (var service = new ServiceController("BITS"))
                {
                    service.Refresh();
                    if (service.Status == ServiceControllerStatus.Paused && service.CanPauseAndContinue)
                    {
                        service.Continue();
                        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(3));
                    }

                    _pausedBits = false;
                    AppLogger.Write("Network focus: BITS resumed.");
                    if (pushStatus != null)
                    {
                        pushStatus("Network focus resumed background transfers.", false);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Network focus resume failed: " + ex.Message);
                if (pushStatus != null)
                {
                    pushStatus("Network focus could not resume background transfers. Check BITS manually.", true);
                }
            }
        }

        public void Dispose()
        {
            ResumeIfOwned(null);
        }
    }
}
