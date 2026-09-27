using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using GSwitcher.Services;

namespace GSwitcher.Backend
{
    /// <summary>
    /// Safe, generic editor for the processor subgroup exposed by powercfg.
    /// Only GUIDs discovered in the active scheme's processor subgroup may be written.
    /// </summary>
    public static class ProcessorPowerSettingsService
    {
        private const string ProcessorSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
        private static readonly Regex Hex = new Regex(@"0x([0-9a-f]+)", RegexOptions.IgnoreCase);
        private static readonly Regex ActiveGuid = new Regex(@"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", RegexOptions.IgnoreCase);
        private static readonly Regex SettingHeader = new Regex(@"Power Setting GUID:\s*([0-9a-f-]{36})(?:\s+\(([^)]*)\))?", RegexOptions.IgnoreCase);
        private static readonly string PowerCfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");

        public sealed class Setting
        {
            public string Guid { get; set; }
            public string Name { get; set; }
            public string Subgroup { get; set; }
            public uint AcValue { get; set; }
            public uint DcValue { get; set; }
            public List<uint> PossibleValues { get; set; }
            public string Unit { get; set; }
            public uint MinimumValue { get; set; }
            public uint MaximumValue { get; set; }
            public uint Increment { get; set; }
            public bool HasRange { get; set; }
            public string Safety { get; set; }
            public bool Writable { get; set; }
        }

        public sealed class Snapshot
        {
            public string SchemeGuid { get; set; }
            public string SchemeName { get; set; }
            public DateTime CreatedUtc { get; set; }
            public List<Setting> Settings { get; set; }
        }

        public sealed class QueryResult
        {
            public string schemeGuid { get; set; }
            public string schemeName { get; set; }
            public string subgroupGuid { get; set; }
            public List<Setting> settings { get; set; }
        }

        public static QueryResult ReadActive()
        {
            var active = Run("/getactivescheme");
            var match = ActiveGuid.Match(active.StandardOutput);
            if (!match.Success) throw new InvalidOperationException("Windows did not report an active power scheme.");
            var scheme = match.Groups[1].Value.ToLowerInvariant();
            var query = Run("/query " + scheme + " " + ProcessorSubgroup);
            if (query.ExitCode != 0) throw new InvalidOperationException(Clean(query.ErrorOutput));
            var settings = Parse(query.StandardOutput);
            return new QueryResult { schemeGuid = scheme, schemeName = ReadSchemeName(query.StandardOutput), subgroupGuid = ProcessorSubgroup, settings = settings };
        }

        public static string SnapshotActive(bool exportCopy)
        {
            var data = ReadActive();
            var snapshot = new Snapshot
            {
                SchemeGuid = data.schemeGuid,
                SchemeName = data.schemeName,
                CreatedUtc = DateTime.UtcNow,
                Settings = ((IEnumerable<Setting>)data.settings).ToList()
            };
            var serializer = new JavaScriptSerializer();
            var path = Path.Combine(AppPaths.ControlDirectory, exportCopy
                ? "processor-power-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json"
                : "processor-power-snapshot-latest.json");
            File.WriteAllText(path, serializer.Serialize(snapshot), Encoding.UTF8);
            return path;
        }

        public static string RestoreLatest()
        {
            var path = Path.Combine(AppPaths.ControlDirectory, "processor-power-snapshot-latest.json");
            if (!File.Exists(path)) throw new FileNotFoundException("No processor power snapshot exists yet.", path);
            var snapshot = new JavaScriptSerializer().Deserialize<Snapshot>(File.ReadAllText(path));
            if (snapshot == null || snapshot.Settings == null) throw new InvalidDataException("The processor snapshot is invalid.");
            var current = ParseActiveSettings();
            var allowed = new HashSet<string>(current.Select(x => x.Guid), StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var item in snapshot.Settings)
            {
                if (item == null || !allowed.Contains(item.Guid)) continue;
                SetValidated(item.Guid, "ac", item.AcValue, current);
                SetValidated(item.Guid, "dc", item.DcValue, current);
                count++;
            }
            return "Restored " + count + " processor settings from the latest snapshot.";
        }

        public static string ApplyPreset(string preset)
        {
            var settings = ParseActiveSettings();
            var normalized = (preset ?? string.Empty).Trim().ToLowerInvariant();
            var matched = 0;
            foreach (var item in settings)
            {
                uint ac, dc;
                if (!TryPreset(item.Name, normalized, out ac, out dc)) continue;
                SetValidated(item.Guid, "ac", ac, settings);
                SetValidated(item.Guid, "dc", dc, settings);
                matched++;
            }
            if (matched == 0) throw new InvalidOperationException("No compatible processor settings were exposed by this Windows build.");
            return "Applied " + preset + " preset to " + matched + " discovered settings.";
        }

        public static string Set(string guid, string plane, string value)
        {
            uint parsed = 0;
            var text = (value ?? string.Empty).Trim();
            var isHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            var numeric = isHex ? text.Substring(2) : text;
            if ((isHex && !uint.TryParse(numeric, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)) ||
                (!isHex && !uint.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)))
                throw new ArgumentException("Value must be a decimal or hexadecimal DWORD.");
            var settings = ParseActiveSettings();
            SetValidated(guid, plane, parsed, settings);
            return "Updated " + settings.First(x => x.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase)).Name + " (" + plane.ToUpperInvariant() + ").";
        }

        private static List<Setting> ParseActiveSettings()
        {
            var data = ReadActive();
            return ((IEnumerable<Setting>)data.settings).ToList();
        }

        private static void SetValidated(string guid, string plane, uint value, List<Setting> settings)
        {
            var setting = settings.FirstOrDefault(x => x.Guid.Equals(guid ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (setting == null || !setting.Writable) throw new InvalidOperationException("That setting is not in the active processor subgroup.");
            if (plane != "ac" && plane != "dc") throw new ArgumentException("Power plane must be AC or DC.");
            if (setting.PossibleValues.Count > 0 && !setting.PossibleValues.Contains(value))
                throw new ArgumentOutOfRangeException("value", "Windows did not advertise that value for this setting.");
            if (setting.HasRange && (value < setting.MinimumValue || value > setting.MaximumValue ||
                (setting.Increment > 1 && (value - setting.MinimumValue) % setting.Increment != 0)))
                throw new ArgumentOutOfRangeException("value", "Value is outside the range advertised by Windows.");
            var result = Run("/set" + plane + "valueindex " + GetActiveScheme() + " " + ProcessorSubgroup + " " + setting.Guid + " " + value.ToString(CultureInfo.InvariantCulture));
            if (result.ExitCode != 0) throw new InvalidOperationException(Clean(result.ErrorOutput));
        }

        private static bool TryPreset(string name, string preset, out uint ac, out uint dc)
        {
            ac = dc = 0;
            var n = (name ?? string.Empty).ToLowerInvariant();
            if (n.Contains("minimum processor") || n.Contains("minimum performance"))
            {
                ac = preset == "battery" ? 5u : 10u; dc = preset == "performance" ? 5u : 5u; return true;
            }
            if (n.Contains("energy performance preference") || n.Contains("epp"))
            {
                ac = preset == "performance" ? 0u : (preset == "battery" ? 80u : 40u); dc = preset == "battery" ? 90u : 60u; return true;
            }
            if (n.Contains("boost mode") || n.Contains("performance boost"))
            {
                ac = preset == "battery" ? 1u : 2u; dc = preset == "performance" ? 2u : 1u; return true;
            }
            if (n.Contains("maximum processor") || n.Contains("maximum performance"))
            {
                ac = preset == "battery" ? 85u : 100u; dc = preset == "battery" ? 70u : 100u; return true;
            }
            return false;
        }

        private static List<Setting> Parse(string output)
        {
            var result = new List<Setting>();
            var lines = (output ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            Setting current = null;
            var inProcessor = false;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("Subgroup GUID:", StringComparison.OrdinalIgnoreCase))
                {
                    inProcessor = line.IndexOf(ProcessorSubgroup, StringComparison.OrdinalIgnoreCase) >= 0;
                    current = null;
                }
                if (!inProcessor) continue;
                if (line.StartsWith("Power Setting GUID:", StringComparison.OrdinalIgnoreCase))
                {
                    var m = SettingHeader.Match(line);
                    if (!m.Success) continue;
                    current = new Setting
                    {
                        Guid = m.Groups[1].Value.ToLowerInvariant(),
                        Name = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null,
                        PossibleValues = new List<uint>(),
                        Writable = true,
                        Safety = "Validated processor subgroup setting"
                    };
                    result.Add(current);
                    continue;
                }
                if (current == null) continue;
                if (line.StartsWith("Power Setting Friendly Name:", StringComparison.OrdinalIgnoreCase)) current.Name = AfterColon(line);
                else if (line.StartsWith("Possible Setting Index:", StringComparison.OrdinalIgnoreCase)) AddHex(current.PossibleValues, line);
                else if (line.StartsWith("Minimum Possible Setting:", StringComparison.OrdinalIgnoreCase)) { current.MinimumValue = ParseHex(line); current.HasRange = true; }
                else if (line.StartsWith("Maximum Possible Setting:", StringComparison.OrdinalIgnoreCase)) { current.MaximumValue = ParseHex(line); current.HasRange = true; }
                else if (line.StartsWith("Possible Settings increment:", StringComparison.OrdinalIgnoreCase)) { current.Increment = ParseHex(line); current.HasRange = true; }
                else if (line.StartsWith("Possible Settings units:", StringComparison.OrdinalIgnoreCase)) current.Unit = AfterColon(line);
                else if (line.StartsWith("Current AC Power Setting Index:", StringComparison.OrdinalIgnoreCase)) current.AcValue = ParseHex(line);
                else if (line.StartsWith("Current DC Power Setting Index:", StringComparison.OrdinalIgnoreCase)) current.DcValue = ParseHex(line);
            }
            return result.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();
        }

        private static void AddHex(List<uint> values, string line) { values.Add(ParseHex(line)); }
        private static uint ParseHex(string line)
        {
            var m = Hex.Match(line);
            uint value;
            if (m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return value;
            var text = AfterColon(line).Trim();
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) ? value : 0;
        }
        private static string AfterColon(string line) { var p = line.IndexOf(':'); return p >= 0 ? line.Substring(p + 1).Trim() : line.Trim(); }
        private static string ReadSchemeName(string output) { var lines = (output ?? "").Split('\n'); return lines.Length > 0 ? lines[0].Trim() : ""; }
        private static string GetActiveScheme() { var m = ActiveGuid.Match(Run("/getactivescheme").StandardOutput); if (!m.Success) throw new InvalidOperationException("Active power scheme unavailable."); return m.Groups[1].Value; }
        private static string Clean(string text) { return string.IsNullOrWhiteSpace(text) ? "powercfg failed." : text.Trim(); }
        private sealed class ProcessResult { public int ExitCode; public string StandardOutput; public string ErrorOutput; }
        private static ProcessResult Run(string arguments)
        {
            if (!File.Exists(PowerCfgPath)) return new ProcessResult { ExitCode = -1, ErrorOutput = "powercfg.exe was not found." };
            var info = new ProcessStartInfo(PowerCfgPath, arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
                return new ProcessResult { ExitCode = process.ExitCode, StandardOutput = output, ErrorOutput = error };
            }
        }
    }
}
