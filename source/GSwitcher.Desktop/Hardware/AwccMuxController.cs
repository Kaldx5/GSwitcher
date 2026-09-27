using System;
using System.Collections.Generic;
using System.Management;
using GSwitcher.Models;

namespace GSwitcher.Backend
{
    internal sealed class AwccMuxController
    {
        private const string ClassName = "AWCCWmiMethodFunction";
        private const string ControlPath = "awcc-mux";
        private const string MuxMethodName = "MUXSwitch";
        private const uint QueryMuxStateArgument = 0x00000001;
        private const uint SetHybridArgument = 0x00000102;
        private const uint SetDgpuArgument = 0x00000202;
        private const uint HybridRawState = 1;
        private const uint DgpuRawState = 2;
        private const uint SetAcceptedNoRestart = 0;
        private const uint SetAcceptedRestart = 1;
        private bool _probeCompleted;
        private bool _hasMuxInterface;
        private bool _isAvailable;
        private string _probeError;

        public bool IsAvailable
        {
            get
            {
                EnsureProbe();
                return _isAvailable;
            }
        }

        public bool HasMuxInterface
        {
            get
            {
                EnsureProbe();
                return _hasMuxInterface;
            }
        }

        public string ProbeError
        {
            get
            {
                EnsureProbe();
                return _probeError;
            }
        }

        public string[] GetAvailableModes()
        {
            return new[] { "dgpu", "hybrid" };
        }

        public string DetectMode()
        {
            string detectedMode;
            uint ignoredRawValue;
            string ignoredError;
            return TryQueryMode(out detectedMode, out ignoredRawValue, out ignoredError) ? detectedMode : "unknown";
        }

        public OperationResult Apply(string targetMode)
        {
            targetMode = GpuManager.NormalizeMode(targetMode);
            if (targetMode != "dgpu" && targetMode != "hybrid")
            {
                return OperationResult.Fail(
                    "Native Dell MUX control supports Dedicated GPU and Hybrid only.",
                    controlPath: ControlPath,
                    requestedMode: targetMode);
            }

            string currentMode;
            uint currentRawValue;
            string queryError;
            if (!TryQueryMode(out currentMode, out currentRawValue, out queryError))
            {
                uint directRawValue;
                string directError;
                if (!TrySetMode(targetMode, out directRawValue, out directError))
                {
                    return OperationResult.Fail(
                        "Native Dell MUX status could not be read, and the direct set command was rejected.",
                        CombineErrorDetails(queryError, directError),
                        ControlPath,
                        targetMode);
                }

                return OperationResult.Ok(
                    "Native Dell MUX command was accepted for " + GpuManager.DescribeMode(targetMode) +
                    ", but status could not be read afterward. Restart is required before the route can be trusted.",
                    ControlPath,
                    targetMode,
                    verifiedMode: "unknown",
                    verified: false,
                    requiresRestart: true,
                    warning: "Dell accepted a direct MUX set command after rejecting status reads. Restart, then rerun diagnostics to verify Windows display ownership.");
            }

            if (string.Equals(currentMode, targetMode, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Ok(
                    "Native GPU mode already queued as " + GpuManager.DescribeMode(targetMode) + ".",
                    ControlPath,
                    targetMode,
                    currentMode,
                    verified: true);
            }

            uint setRawValue;
            string setError;
            if (!TrySetMode(targetMode, out setRawValue, out setError))
            {
                return OperationResult.Fail(
                    "Native Dell MUX change failed.",
                    setError,
                    ControlPath,
                    targetMode,
                    currentMode);
            }

            string afterMode;
            uint afterRawValue;
            string afterError;
            if (!TryQueryMode(out afterMode, out afterRawValue, out afterError))
            {
                return OperationResult.Ok(
                    "Native Dell MUX command was accepted for " + GpuManager.DescribeMode(targetMode) +
                    ", but status could not be read afterward. Restart before trusting the route.",
                    ControlPath,
                    targetMode,
                    currentMode,
                    verified: false,
                    requiresRestart: true,
                    warning: "Dell accepted MUXSwitch arg2=0x" + BuildSetArgument(targetMode).ToString("X8") +
                        " with argr=" + setRawValue + ". Verification failed: " + afterError);
            }

            if (!string.Equals(afterMode, targetMode, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Ok(
                    "Native Dell MUX command was accepted for " + GpuManager.DescribeMode(targetMode) +
                    ", but the active BIOS-reported state still reads " + GpuManager.DescribeMode(afterMode) + ". Restart is required before the route can be trusted.",
                    ControlPath,
                    targetMode,
                    afterMode,
                    verified: false,
                    requiresRestart: true,
                    warning: "Status before=" + currentRawValue + ", set argr=" + setRawValue + ", after=" + afterRawValue +
                        ". Dell's AWCC AppState treats MUX switching as a reboot-reason workflow.");
            }

            return OperationResult.Ok(
                "Native GPU mode queued: " + GpuManager.DescribeMode(targetMode) + ".",
                ControlPath,
                targetMode,
                afterMode,
                verified: true,
                requiresRestart: setRawValue == SetAcceptedRestart,
                warning: setRawValue == SetAcceptedRestart
                    ? "Dell BIOS-controlled mux change requires restart."
                    : "Dell reported the requested MUX state without a reboot reason.");
        }

        private static uint BuildSetArgument(string targetMode)
        {
            return GpuManager.NormalizeRouteMode(targetMode) == "dgpu" ? SetDgpuArgument : SetHybridArgument;
        }

        private static bool TryQueryMode(out string mode, out uint rawValue, out string errorDetail)
        {
            mode = "unknown";
            rawValue = 0;
            errorDetail = null;

            if (!TryInvokeMuxRaw(QueryMuxStateArgument, out rawValue, out errorDetail))
            {
                return false;
            }

            if (rawValue == uint.MaxValue)
            {
                errorDetail = "The Dell AWCC MUX query returned 0xFFFFFFFF for MUXSwitch(arg2=0x00000001). This usually means the session is not elevated or the firmware blocked access.";
                return false;
            }

            if (!TryMapRawMode(rawValue, out mode))
            {
                errorDetail = "Unexpected MUX status value: " + rawValue + ".";
                return false;
            }

            return true;
        }

        private static bool TrySetMode(string targetMode, out uint rawValue, out string errorDetail)
        {
            rawValue = 0;
            errorDetail = null;
            targetMode = GpuManager.NormalizeRouteMode(targetMode);

            var argument = BuildSetArgument(targetMode);

            string candidateError;
            if (TryInvokeMuxRaw(argument, out rawValue, out candidateError) &&
                (rawValue == SetAcceptedNoRestart || rawValue == SetAcceptedRestart))
            {
                return true;
            }

            errorDetail =
                "Dell AWCC MUXSwitch rejected " + GpuManager.DescribeMode(targetMode) +
                " set arg2=0x" + argument.ToString("X8") +
                ". argr=" + rawValue +
                (string.IsNullOrWhiteSpace(candidateError) ? "." : ". " + candidateError);
            return false;
        }

        private static bool TryMapRawMode(uint rawValue, out string mode)
        {
            if (rawValue == HybridRawState)
            {
                mode = "hybrid";
                return true;
            }

            if (rawValue == DgpuRawState)
            {
                mode = "dgpu";
                return true;
            }

            mode = "unknown";
            return false;
        }

        private static bool TryInvokeMuxRaw(uint argument, out uint rawValue, out string errorDetail)
        {
            rawValue = 0;
            errorDetail = null;

            try
            {
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate
                };

                var scope = new ManagementScope(@"\\.\root\wmi", options);
                scope.Connect();

                string instancesError;
                if (TryInvokeOnInstances(scope, argument, out rawValue, out instancesError))
                {
                    return true;
                }

                errorDetail = instancesError;
                return false;
            }
            catch (Exception ex)
            {
                errorDetail = ex.ToString();
                return false;
            }
        }

        private static bool TryInvokeOnInstances(
            ManagementScope scope,
            uint argument,
            out uint rawValue,
            out string errorDetail)
        {
            rawValue = 0;
            errorDetail = null;
            var instanceCount = 0;
            var lastInstanceError = string.Empty;

            try
            {
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM " + ClassName)))
                using (var instances = searcher.Get())
                {
                    foreach (ManagementObject instance in instances)
                    {
                        instanceCount++;
                        using (instance)
                        {
                            if (TryInvoke(instance, argument, out rawValue, out errorDetail))
                            {
                                return true;
                            }

                            var path = "(unknown path)";
                            try
                            {
                                if (instance.Path != null && !string.IsNullOrWhiteSpace(instance.Path.Path))
                                {
                                    path = instance.Path.Path;
                                }
                            }
                            catch
                            {
                            }

                            lastInstanceError = "Instance " + path + ": " + (errorDetail ?? "Unknown failure.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errorDetail = "Instance enumeration failed: " + ex;
                return false;
            }

            if (instanceCount == 0)
            {
                errorDetail = "No accessible " + ClassName + " instances were returned by WMI.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(lastInstanceError))
            {
                errorDetail = lastInstanceError;
            }

            return false;
        }

        private static bool TryInvoke(
            ManagementObject target,
            uint argument,
            out uint rawValue,
            out string errorDetail)
        {
            rawValue = 0;
            errorDetail = null;
            try
            {
                using (var input = target.GetMethodParameters(MuxMethodName))
                {
                    input["arg2"] = Convert.ToInt32(argument);
                    using (var output = target.InvokeMethod(MuxMethodName, input, null))
                    {
                        return TryReadReturnValue(output, out rawValue, out errorDetail);
                    }
                }
            }
            catch (Exception ex)
            {
                errorDetail = ex.Message;
                return false;
            }
        }

        private static bool TryReadReturnValue(ManagementBaseObject output, out uint rawValue, out string errorDetail)
        {
            rawValue = 0;
            errorDetail = null;

            if (output == null)
            {
                errorDetail = "The WMI method returned no output.";
                return false;
            }

            object rawResultValue;
            if (!TryGetPropertyValue(output, "argr", out rawResultValue))
            {
                object returnValue;
                TryGetPropertyValue(output, "ReturnValue", out returnValue);
                errorDetail =
                    "The WMI method did not return argr. Available output properties: " +
                    DescribePropertyNames(output) +
                    ". ReturnValue=" + (returnValue == null ? "(missing)" : Convert.ToString(returnValue)) + ".";
                return false;
            }

            long parsedRawValue;
            if (!long.TryParse(Convert.ToString(rawResultValue), out parsedRawValue))
            {
                errorDetail = "The WMI method returned a non-numeric argr value: " + Convert.ToString(rawResultValue) + ".";
                return false;
            }

            rawValue = unchecked((uint)parsedRawValue);

            object methodReturnValue;
            if (TryGetPropertyValue(output, "ReturnValue", out methodReturnValue) &&
                methodReturnValue != null)
            {
                try
                {
                    if (!Convert.ToBoolean(methodReturnValue) && rawValue == uint.MaxValue)
                    {
                        errorDetail = "The Dell AWCC MUX method reported failure (ReturnValue=false, argr=4294967295). Administrator elevation is usually required for native MUX access.";
                        return false;
                    }
                }
                catch
                {
                }
            }

            return true;
        }

        private static bool TryGetPropertyValue(ManagementBaseObject output, string propertyName, out object value)
        {
            value = null;
            if (output == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            foreach (PropertyData property in output.Properties)
            {
                if (property == null)
                {
                    continue;
                }

                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = property.Value;
                return true;
            }

            return false;
        }

        private static string DescribePropertyNames(ManagementBaseObject output)
        {
            if (output == null)
            {
                return "(none)";
            }

            var names = new List<string>();
            foreach (PropertyData property in output.Properties)
            {
                if (property == null || string.IsNullOrWhiteSpace(property.Name))
                {
                    continue;
                }

                names.Add(property.Name);
            }

            return names.Count == 0 ? "(none)" : string.Join(", ", names.ToArray());
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

            return first.Trim() + Environment.NewLine + second.Trim();
        }

        private void EnsureProbe()
        {
            if (_probeCompleted)
            {
                return;
            }

            _probeCompleted = true;
            _hasMuxInterface = TryDetectMuxInterface(out _probeError);
            if (!_hasMuxInterface)
            {
                return;
            }

            string ignoredMode;
            uint ignoredRawValue;
            string queryError;
            _isAvailable = TryQueryMode(out ignoredMode, out ignoredRawValue, out queryError);
            _probeError = queryError;
        }

        private static bool TryDetectMuxInterface(out string errorDetail)
        {
            errorDetail = null;

            try
            {
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate
                };

                var scope = new ManagementScope(@"\\.\root\wmi", options);
                scope.Connect();

                using (var managementClass = new ManagementClass(scope, new ManagementPath(ClassName), null))
                {
                    if (managementClass.Methods[MuxMethodName] != null)
                    {
                        return true;
                    }

                    errorDetail = ClassName + " exists but does not expose Dell's " + MuxMethodName + " method.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                var managementException = ex as ManagementException;
                if (managementException != null && managementException.ErrorCode == ManagementStatus.InvalidClass)
                {
                    return false;
                }

                errorDetail = ex.ToString();
                return false;
            }
        }

    }
}
