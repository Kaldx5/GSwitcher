using System;
using GSwitcher.Models;
using NvAPIWrapper.DRS;

namespace GSwitcher.Backend
{
    public static class NvidiaController
    {
        public const string ControlPath = "nvidia-drs";
        private const uint ShimIntegrated = 0x00000000U;
        private const uint ShimEnable = 0x00000001U;
        private const uint ShimAuto = 0x00000010U;
        private const uint DefaultRenderingMode = 0x00000000U;

        public static bool IsAvailable()
        {
            try
            {
                NvAPIWrapper.NVIDIA.Initialize();

                using (var session = DriverSettingsSession.CreateAndLoad())
                {
                    return session != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static string NormalizeMode(string mode)
        {
            mode = (mode ?? string.Empty).Trim().ToLowerInvariant();

            switch (mode)
            {
                case "nvidia":
                case "dgpu":
                case "rtx":
                case "discrete":
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

        public static string DetectPreference()
        {
            if (!IsAvailable())
            {
                return "unknown";
            }

            try
            {
                NvAPIWrapper.NVIDIA.Initialize();

                using (var session = DriverSettingsSession.CreateAndLoad())
                {
                    var profile = session.CurrentGlobalProfile ?? session.BaseProfile;
                    var setting = profile.GetSetting(KnownSettingId.ShimRenderingMode);
                    if (setting == null || setting.CurrentValue == null)
                    {
                        return null;
                    }

                    var value = Convert.ToUInt32(setting.CurrentValue);
                    if ((value & ShimAuto) == ShimAuto)
                    {
                        return "auto";
                    }

                    if ((value & ShimEnable) == ShimEnable)
                    {
                        return "dgpu";
                    }

                    return "hybrid";
                }
            }
            catch
            {
                return "unknown";
            }
        }

        public static OperationResult ApplyPreference(string mode)
        {
            mode = NormalizeMode(mode);
            if (mode == "unknown")
            {
                return OperationResult.Fail("Unsupported GPU preference mode.", controlPath: ControlPath, requestedMode: mode);
            }

            if (!IsAvailable())
            {
                return OperationResult.Fail(
                    "NVIDIA app preference control is not available on this machine right now.",
                    controlPath: ControlPath,
                    requestedMode: mode);
            }

            try
            {
                NvAPIWrapper.NVIDIA.Initialize();

                using (var session = DriverSettingsSession.CreateAndLoad())
                {
                    var profile = session.CurrentGlobalProfile ?? session.BaseProfile;

                    switch (mode)
                    {
                        case "dgpu":
                            profile.SetSetting(KnownSettingId.ShimRenderingMode, ShimEnable);
                            profile.SetSetting(KnownSettingId.ShimMCCOMPAT, ShimEnable);
                            profile.SetSetting(KnownSettingId.ShimRenderingOptions, DefaultRenderingMode);
                            break;
                        case "hybrid":
                            profile.SetSetting(KnownSettingId.ShimRenderingMode, ShimIntegrated);
                            profile.SetSetting(KnownSettingId.ShimMCCOMPAT, ShimIntegrated);
                            profile.SetSetting(KnownSettingId.ShimRenderingOptions, DefaultRenderingMode);
                            break;
                        default:
                            profile.SetSetting(KnownSettingId.ShimRenderingMode, ShimAuto);
                            profile.SetSetting(KnownSettingId.ShimMCCOMPAT, ShimAuto);
                            profile.SetSetting(KnownSettingId.ShimRenderingOptions, DefaultRenderingMode);
                            break;
                    }

                    session.Save();
                }

                var verifiedMode = DetectPreference();
                if (!string.Equals(verifiedMode, mode, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail(
                        "GPU preference change could not be verified.",
                        "Requested " + mode + " but detected " + verifiedMode + ".",
                        ControlPath,
                        mode,
                        verifiedMode);
                }

                return OperationResult.Ok(
                    "GPU preference verified: " + DescribeMode(mode) +
                    ". This is NVIDIA driver preference control, not a hard display-mux switch.",
                    ControlPath,
                    mode,
                    verifiedMode,
                    verified: true);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(
                    "GPU preference change failed: " + ex.Message +
                    ". Preference-only control is the available fallback.",
                    ex.ToString(),
                    ControlPath,
                    mode);
            }
        }

        public static string[] GetAvailableModes()
        {
            return IsAvailable() ? new[] { "auto", "dgpu", "hybrid" } : new string[0];
        }

        private static string DescribeMode(string mode)
        {
            switch (NormalizeMode(mode))
            {
                case "dgpu":
                    return "RTX Priority";
                case "hybrid":
                    return "Optimus";
                default:
                    return "Automatic";
            }
        }
    }
}
