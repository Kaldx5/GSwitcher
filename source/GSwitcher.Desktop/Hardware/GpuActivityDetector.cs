using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace GSwitcher.Backend
{
    internal static class GpuActivityDetector
    {
        private static readonly Regex ProcessPattern = new Regex(
            @"pid_(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private sealed class ProcessLoad
        {
            public int ProcessId;
            public string ProcessName;
            public float Utilization;
        }

        public static string DescribeSessionLoad(int maxProcesses = 4)
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instanceNames = category.GetInstanceNames();
                var counters = new List<PerformanceCounter>();

                for (var index = 0; index < instanceNames.Length; index++)
                {
                    var instance = instanceNames[index];
                    if (instance.IndexOf("pid_", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    counters.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true));
                }

                if (counters.Count == 0)
                {
                    return null;
                }

                try
                {
                    for (var index = 0; index < counters.Count; index++)
                    {
                        counters[index].NextValue();
                    }

                    Thread.Sleep(350);

                    var loads = new Dictionary<int, ProcessLoad>();

                    for (var index = 0; index < counters.Count; index++)
                    {
                        var counter = counters[index];
                        var match = ProcessPattern.Match(counter.InstanceName ?? string.Empty);
                        if (!match.Success)
                        {
                            continue;
                        }

                        int processId;
                        if (!int.TryParse(match.Groups[1].Value, out processId) || processId <= 0)
                        {
                            continue;
                        }

                        var utilization = counter.NextValue();
                        if (utilization < 0.5f)
                        {
                            continue;
                        }

                        ProcessLoad load;
                        if (!loads.TryGetValue(processId, out load))
                        {
                            load = new ProcessLoad
                            {
                                ProcessId = processId,
                                ProcessName = SafeGetProcessName(processId),
                                Utilization = utilization
                            };
                            loads[processId] = load;
                        }
                        else if (utilization > load.Utilization)
                        {
                            load.Utilization = utilization;
                        }
                    }

                    if (loads.Count == 0)
                    {
                        return null;
                    }

                    var ordered = new List<ProcessLoad>(loads.Values);
                    ordered.Sort(
                        delegate (ProcessLoad left, ProcessLoad right)
                        {
                            return right.Utilization.CompareTo(left.Utilization);
                        });

                    var builder = new StringBuilder();
                    builder.Append("Active GPU workloads in this Windows session may still be pinning the route: ");

                    var added = 0;
                    for (var index = 0; index < ordered.Count && added < maxProcesses; index++)
                    {
                        var load = ordered[index];
                        if (string.IsNullOrWhiteSpace(load.ProcessName))
                        {
                            continue;
                        }

                        if (added > 0)
                        {
                            builder.Append(", ");
                        }

                        builder.Append(load.ProcessName);
                        builder.Append(" (");
                        builder.Append(Math.Round(load.Utilization));
                        builder.Append("%)");
                        added++;
                    }

                    if (added == 0)
                    {
                        return null;
                    }

                    builder.Append(".");
                    return builder.ToString();
                }
                finally
                {
                    for (var index = 0; index < counters.Count; index++)
                    {
                        counters[index].Dispose();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetProcessName(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
