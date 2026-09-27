using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using GSwitcher.Models;

namespace GSwitcher.Services
{
    public static class EphemeralElevationService
    {
        private const string ControlPath = "ephemeral-task";
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly string PowerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        public static bool IsCurrentProcessElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static OperationResult RunElevated(string operation, string mode)
        {
            if (string.IsNullOrWhiteSpace(operation))
            {
                return OperationResult.Fail("No elevated operation was specified.", controlPath: ControlPath);
            }

            var exePath = GetCurrentExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return OperationResult.Fail("GSwitcher could not find its executable for elevation.", controlPath: ControlPath);
            }

            Directory.CreateDirectory(AppPaths.ControlDirectory);

            var taskName = "GSwitcher-Ephemeral-" + Guid.NewGuid().ToString("N");
            var responsePath = Path.Combine(AppPaths.ControlDirectory, taskName + ".json");
            var responseToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(responsePath));
            var childArgs = "--ephemeral-operation " + QuoteArgument(operation.Trim()) +
                            " --ephemeral-mode " + QuoteArgument((mode ?? string.Empty).Trim()) +
                            " --ephemeral-response " + QuoteArgument(responseToken);

            try
            {
                if (File.Exists(responsePath))
                {
                    File.Delete(responsePath);
                }

                var script = BuildTaskScript(taskName, exePath, childArgs, responsePath);
                var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var startInfo = new ProcessStartInfo
                {
                    FileName = File.Exists(PowerShellPath) ? PowerShellPath : "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encodedScript,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return OperationResult.Fail("The elevated task launcher could not start.", controlPath: ControlPath, requestedMode: mode);
                    }

                    process.WaitForExit(15000);
                }

                var deadlineUtc = DateTime.UtcNow.AddSeconds(260);
                while (DateTime.UtcNow < deadlineUtc)
                {
                    if (File.Exists(responsePath))
                    {
                        var text = File.ReadAllText(responsePath);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var result = Serializer.Deserialize<OperationResult>(text);
                            if (result != null)
                            {
                                result.ControlPath = string.IsNullOrWhiteSpace(result.ControlPath) ? ControlPath : result.ControlPath;
                                return result;
                            }
                        }
                    }

                    Thread.Sleep(250);
                }

                return OperationResult.Fail(
                    "The elevated action timed out before it produced a result.",
                    controlPath: ControlPath,
                    requestedMode: mode);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                {
                    return OperationResult.Fail(
                        "Administrator access was canceled. No persistent helper was installed.",
                        ex.ToString(),
                        ControlPath,
                        mode);
                }

                return OperationResult.Fail("The elevated action could not start.", ex.ToString(), ControlPath, mode);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail("The elevated action failed.", ex.ToString(), ControlPath, mode);
            }
        }

        public static bool TryHandleEphemeralOperation(string[] args, Func<string, string, OperationResult> execute)
        {
            var operation = GetArgumentValue(args, "--ephemeral-operation");
            if (string.IsNullOrWhiteSpace(operation))
            {
                return false;
            }

            var mode = GetArgumentValue(args, "--ephemeral-mode");
            var responseToken = GetArgumentValue(args, "--ephemeral-response");
            var responsePath = DecodeResponsePath(responseToken);

            OperationResult result;
            try
            {
                result = execute(operation, mode);
            }
            catch (Exception ex)
            {
                result = OperationResult.Fail("Elevated operation crashed.", ex.ToString(), ControlPath, mode);
            }

            if (!string.IsNullOrWhiteSpace(responsePath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(responsePath));
                    File.WriteAllText(responsePath, Serializer.Serialize(result));
                }
                catch
                {
                }
            }

            return true;
        }

        private static string BuildTaskScript(string taskName, string exePath, string childArgs, string responsePath)
        {
            var safeTaskName = EscapePowerShell(taskName);
            var safeExePath = EscapePowerShell(exePath);
            var safeChildArgs = EscapePowerShell(childArgs);
            var safeResponsePath = EscapePowerShell(responsePath);

            return
                "$ErrorActionPreference='Stop';" +
                "$taskName='" + safeTaskName + "';" +
                "$responsePath='" + safeResponsePath + "';" +
                "try {" +
                "$action=New-ScheduledTaskAction -Execute '" + safeExePath + "' -Argument '" + safeChildArgs + "';" +
                "$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5);" +
                "$principal=New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest;" +
                "$task=New-ScheduledTask -Action $action -Principal $principal -Settings $settings;" +
                "Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null;" +
                "Start-ScheduledTask -TaskName $taskName;" +
                "$deadline=(Get-Date).AddSeconds(250);" +
                "while((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $responsePath)){Start-Sleep -Milliseconds 250;}" +
                "} finally {" +
                "try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue } catch {}" +
                "}";
        }

        private static string GetArgumentValue(string[] args, string name)
        {
            if (args == null)
            {
                return null;
            }

            for (var index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        private static string DecodeResponsePath(string token)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(token ?? string.Empty));
            }
            catch
            {
                return null;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string EscapePowerShell(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static string GetCurrentExecutablePath()
        {
            using (var currentProcess = Process.GetCurrentProcess())
            {
                return currentProcess.MainModule != null
                    ? currentProcess.MainModule.FileName
                    : null;
            }
        }
    }
}
