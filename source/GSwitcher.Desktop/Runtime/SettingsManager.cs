using System;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace GSwitcher.Services
{
    public static class SettingsManager
    {
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "GSwitcher";
        private static readonly object SyncRoot = new object();
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static AppSettings _cachedSettings;

        public static string SettingsFilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"); }
        }

        public static AppSettings Current
        {
            get { return Load(); }
        }

        public static AppSettings Load()
        {
            lock (SyncRoot)
            {
                if (_cachedSettings != null)
                {
                    var cached = _cachedSettings.Clone();
                    cached.RunOnStartup = IsRunOnStartupEnabled();
                    return cached;
                }

                var settings = AppSettings.CreateDefault();
                try
                {
                    if (File.Exists(SettingsFilePath))
                    {
                        var json = File.ReadAllText(SettingsFilePath);
                        var loaded = Serializer.Deserialize<AppSettings>(json);
                        if (loaded != null)
                        {
                            settings = loaded;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Write("Settings load failed: " + ex.Message);
                }

                settings.Normalize();
                settings.RunOnStartup = IsRunOnStartupEnabled();
                _cachedSettings = settings.Clone();
                return settings;
            }
        }

        public static AppSettings Save(AppSettings settings)
        {
            lock (SyncRoot)
            {
                settings = settings == null ? AppSettings.CreateDefault() : settings.Clone();
                settings.Normalize();
                settings.RunOnStartup = IsRunOnStartupEnabled();

                try
                {
                    var json = Serializer.Serialize(settings);
                    File.WriteAllText(SettingsFilePath, FormatJson(json));
                    _cachedSettings = settings.Clone();
                }
                catch (Exception ex)
                {
                    AppLogger.Write("Settings save failed: " + ex.Message);
                }

                return settings.Clone();
            }
        }

        public static AppSettings Update(string key, bool enabled)
        {
            lock (SyncRoot)
            {
                var settings = Load();
                key = NormalizeKey(key);

                if (key == "runOnStartup")
                {
                    SetRunOnStartup(enabled);
                    settings.RunOnStartup = IsRunOnStartupEnabled();
                }
                else if (key == "afkDownclock")
                {
                    settings.AfkDownclock = enabled;
                }
                else if (key == "standbyHygiene")
                {
                    settings.StandbyHygiene = enabled;
                }
                else if (key == "batteryBleedOut")
                {
                    settings.BatteryBleedOut = enabled;
                }
                else if (key == "quickSwitchOsd")
                {
                    settings.QuickSwitchOsd = enabled;
                }
                else if (key == "bitsServiceHook")
                {
                    settings.BitsServiceHook = enabled;
                }
                else if (key == "lidClosedFailsafe")
                {
                    settings.LidClosedFailsafe = enabled;
                }
                else if (key == "telemetryHud")
                {
                    settings.TelemetryHud = enabled;
                }
                else if (key == "audioPrivacyShield")
                {
                    settings.AudioPrivacyShield = enabled;
                }
                else if (key == "smartCoolingAutomaticEnabled")
                {
                    settings.SmartCoolingAutomaticEnabled = enabled;
                }

                return Save(settings);
            }
        }

        public static AppSettings UpdateTheme(string theme)
        {
            lock (SyncRoot)
            {
                var settings = Load();
                settings.Theme = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)
                    ? "light"
                    : "dark";
                return Save(settings);
            }
        }

        public static AppSettings UpdateSignaturePlacement(string placement)
        {
            lock (SyncRoot)
            {
                var settings = Load();
                settings.SignaturePlacement = placement;
                return Save(settings);
            }
        }

        public static bool IsRunOnStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, false))
                {
                    var value = key == null ? null : Convert.ToString(key.GetValue(StartupValueName));
                    return !string.IsNullOrWhiteSpace(value) &&
                           value.IndexOf(GetExecutablePath(), StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Startup registry read failed: " + ex.Message);
                return false;
            }
        }

        private static void SetRunOnStartup(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath))
                {
                    if (key == null)
                    {
                        throw new InvalidOperationException("HKCU startup registry key could not be opened.");
                    }

                    if (enabled)
                    {
                        key.SetValue(StartupValueName, "\"" + GetExecutablePath() + "\" --startup", RegistryValueKind.String);
                    }
                    else
                    {
                        key.DeleteValue(StartupValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("Startup registry update failed: " + ex);
                throw;
            }
        }

        private static string GetExecutablePath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule.FileName;
            }
            catch
            {
                return System.Reflection.Assembly.GetEntryAssembly().Location;
            }
        }

        private static string NormalizeKey(string key)
        {
            key = (key ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                return string.Empty;
            }

            return char.ToLowerInvariant(key[0]) + key.Substring(1);
        }

        private static string FormatJson(string json)
        {
            // JavaScriptSerializer already emits valid JSON. Do not format by replacing
            // punctuation because commas/braces can legitimately occur inside strings.
            return string.IsNullOrWhiteSpace(json) ? "{}" : json;
        }
    }
}
