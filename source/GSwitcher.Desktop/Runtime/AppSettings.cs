using System;

namespace GSwitcher.Services
{
    public sealed class AppSettings
    {
        public AppSettings()
        {
            Theme = "dark";
            SignaturePlacement = "right";
            TunedPCoreMaxFreq_Ultra = 4100;
            TunedECoreMaxFreq_Ultra = 3200;
            TunedEpp_Ultra = 30;
            TunedPCoreMaxFreq_Cool = 4100;
            TunedECoreMaxFreq_Cool = 3000;
            TunedEpp_Cool = 50;
            SmartCoolingAutomaticEnabled = false;
        }

        public string Theme { get; set; }
        public string SignaturePlacement { get; set; }
        public bool RunOnStartup { get; set; }
        public bool AfkDownclock { get; set; }
        public bool StandbyHygiene { get; set; }
        public bool BatteryBleedOut { get; set; }
        public bool QuickSwitchOsd { get; set; }
        public bool BitsServiceHook { get; set; }
        public bool LidClosedFailsafe { get; set; }
        public bool TelemetryHud { get; set; }
        public bool AudioPrivacyShield { get; set; }
        public bool SmartCoolingAutomaticEnabled { get; set; }

        public bool SmartSimple_Saved { get; set; }
        public int SmartSimple_Boost { get; set; }
        public int SmartSimple_MaxState { get; set; }
        public int SmartSimple_DellProfile { get; set; }

        public bool SmartMedium_Saved { get; set; }
        public int SmartMedium_Boost { get; set; }
        public int SmartMedium_MaxState { get; set; }
        public int SmartMedium_DellProfile { get; set; }

        public bool SmartHigh_Saved { get; set; }
        public int SmartHigh_Boost { get; set; }
        public int SmartHigh_MaxState { get; set; }
        public int SmartHigh_DellProfile { get; set; }

        // Tuned Power Plan Calibration Settings
        public int TunedPCoreMaxFreq_Ultra { get; set; }
        public int TunedECoreMaxFreq_Ultra { get; set; }
        public int TunedEpp_Ultra { get; set; }
        public int TunedPCoreMaxFreq_Cool { get; set; }
        public int TunedECoreMaxFreq_Cool { get; set; }
        public int TunedEpp_Cool { get; set; }

        public static AppSettings CreateDefault()
        {
            return new AppSettings();
        }

        public AppSettings Clone()
        {
            return new AppSettings
            {
                Theme = Theme,
                SignaturePlacement = SignaturePlacement,
                RunOnStartup = RunOnStartup,
                AfkDownclock = AfkDownclock,
                StandbyHygiene = StandbyHygiene,
                BatteryBleedOut = BatteryBleedOut,
                QuickSwitchOsd = QuickSwitchOsd,
                BitsServiceHook = BitsServiceHook,
                LidClosedFailsafe = LidClosedFailsafe,
                TelemetryHud = TelemetryHud,
                AudioPrivacyShield = AudioPrivacyShield,
                SmartCoolingAutomaticEnabled = SmartCoolingAutomaticEnabled,

                SmartSimple_Saved = SmartSimple_Saved,
                SmartSimple_Boost = SmartSimple_Boost,
                SmartSimple_MaxState = SmartSimple_MaxState,
                SmartSimple_DellProfile = SmartSimple_DellProfile,

                SmartMedium_Saved = SmartMedium_Saved,
                SmartMedium_Boost = SmartMedium_Boost,
                SmartMedium_MaxState = SmartMedium_MaxState,
                SmartMedium_DellProfile = SmartMedium_DellProfile,

                SmartHigh_Saved = SmartHigh_Saved,
                SmartHigh_Boost = SmartHigh_Boost,
                SmartHigh_MaxState = SmartHigh_MaxState,
                SmartHigh_DellProfile = SmartHigh_DellProfile,

                TunedPCoreMaxFreq_Ultra = TunedPCoreMaxFreq_Ultra,
                TunedECoreMaxFreq_Ultra = TunedECoreMaxFreq_Ultra,
                TunedEpp_Ultra = TunedEpp_Ultra,
                TunedPCoreMaxFreq_Cool = TunedPCoreMaxFreq_Cool,
                TunedECoreMaxFreq_Cool = TunedECoreMaxFreq_Cool,
                TunedEpp_Cool = TunedEpp_Cool
            };
        }

        public void Normalize()
        {
            Theme = string.Equals(Theme, "light", StringComparison.OrdinalIgnoreCase)
                ? "light"
                : "dark";
            SignaturePlacement = string.Equals(SignaturePlacement, "left", StringComparison.OrdinalIgnoreCase)
                ? "left"
                : string.Equals(SignaturePlacement, "bottom", StringComparison.OrdinalIgnoreCase)
                    ? "bottom"
                    : "right";
        }
    }
}
