using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fct.Integration.Tests
{
    // Shared harness for the OverlayPlugin MiniParse-over-WebSocket gates (P8 OverlaySatelliteTests + the P9b
    // four-package soak): the CombatData snapshot shape, the WS frame reader, the export-vars oracle baseline
    // reader, and the sandbox CEF/config staging that lets the real OverlayPlugin come up headless on 10501.
    internal static class OverlayWsHarness
    {
        // A single MiniParse CombatData push, flattened to strings (ExportVariables render as strings).
        public sealed record CombatSnapshot(
            string IsActive,
            Dictionary<string, string> Encounter,
            Dictionary<string, Dictionary<string, string>> Combatant);

        // (name, key) -> expected export string, read straight from the committed baseline so the assertion
        // is single-sourced with ExportVarsCompatTests. name "*ENCOUNTER*" holds the encounter-level keys.
        public static Dictionary<(string, string), string> ReadExportBaseline(string root, string slice)
        {
            var path = Path.Combine(root, "tests", "Fct.Compat.Act.Tests", "fixtures", slice + ".exportvars.tsv");
            var map = new Dictionary<(string, string), string>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("name\tkey", StringComparison.Ordinal)) continue;
                var c = line.Split('\t');
                if (c.Length < 3) continue;
                map[(c[0], c[1])] = c[2];
            }
            return map;
        }

        public static string? Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : null;

        public static double EncDamage(CombatSnapshot f) =>
            double.TryParse(Get(f.Encounter, "damage"), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

        // The /MiniParse LegacyHandler wraps each push as {type:broadcast, msgtype:CombatData|Chat, msg:…}.
        public static async Task ReadFramesAsync(ClientWebSocket ws, List<CombatSnapshot> combat,
            List<string> chat, CancellationToken ct)
        {
            var buf = new byte[64 * 1024];
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                } while (!res.EndOfMessage);

                CombatSnapshot? snap = null;
                string? chatMsg = null;
                try
                {
                    using var doc = JsonDocument.Parse(sb.ToString());
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("msgtype", out var mt)) continue;
                    var msgtype = mt.GetString();
                    if (msgtype == "CombatData" && r.TryGetProperty("msg", out var msg))
                        snap = ParseCombat(msg);
                    else if (msgtype == "Chat" && r.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String)
                        chatMsg = m.GetString();
                }
                catch { continue; }

                if (snap is not null) combat.Add(snap);
                if (chatMsg is not null) chat.Add(chatMsg);
            }
        }

        private static CombatSnapshot ParseCombat(JsonElement msg)
        {
            var enc = new Dictionary<string, string>();
            if (msg.TryGetProperty("Encounter", out var e) && e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject()) enc[p.Name] = p.Value.ToString();

            var combatants = new Dictionary<string, Dictionary<string, string>>();
            if (msg.TryGetProperty("Combatant", out var c) && c.ValueKind == JsonValueKind.Object)
                foreach (var member in c.EnumerateObject())
                {
                    var vals = new Dictionary<string, string>();
                    if (member.Value.ValueKind == JsonValueKind.Object)
                        foreach (var p in member.Value.EnumerateObject()) vals[p.Name] = p.Value.ToString();
                    combatants[member.Name] = vals;
                }

            var isActive = msg.TryGetProperty("isActive", out var a) ? a.ToString() : "";
            return new CombatSnapshot(isActive, enc, combatants);
        }

        // The satellite's sandbox ACT data folder (mirrors FacadeHost.LegacyActDataDir). Resolved through
        // AppData.Root — NOT %LOCALAPPDATA% — because in DEBUG the data root sits next to the install dir,
        // which the caller overrides via FCT_INSTALL_DIR before staging; the spawned satellite inherits the
        // identical root through FCT_DATA_ROOT, so both processes agree on one sandbox.
        public static string SandboxActFolder() => Path.Combine(
            Fct.Logging.AppData.Root, "legacy", "Advanced Combat Tracker");

        // Junction the satellite sandbox's OverlayPluginCef to the CEF the installed OverlayPlugin already
        // extracted into the real ACT AppDataFolder, so the satellite reuses it instead of downloading.
        public static void TryStageCef(Action<string> log)
        {
            try
            {
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var realCef = Path.Combine(roaming, "Advanced Combat Tracker", "OverlayPluginCef");
                var sandbox = SandboxActFolder();
                var sandboxCef = Path.Combine(sandbox, "OverlayPluginCef");
                if (!Directory.Exists(realCef)) { log("no extracted CEF to reuse; OverlayPlugin will download"); return; }
                if (Directory.Exists(sandboxCef) || File.Exists(sandboxCef)) return;   // already staged
                Directory.CreateDirectory(sandbox);
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{sandboxCef}\" \"{realCef}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = System.Diagnostics.Process.Start(psi);
                p!.WaitForExit(5000);
                log($"CEF junction {sandboxCef} -> {realCef} (exit {p.ExitCode})");
            }
            catch (Exception ex) { log("CEF stage failed: " + ex.Message); }
        }

        // Seed OverlayPlugin's config with the WSServer enabled on 127.0.0.1:10501 (off by default in a fresh
        // profile). "Overlays":[] is required — OverlayPlugin's LoadJson iterates it unguarded (a null throws,
        // which drops the whole config back to defaults, incl. WSServerRunning=false).
        public static void SeedOverlayConfig(Action<string> log)
        {
            try
            {
                var cfgDir = Path.Combine(SandboxActFolder(), "Config");
                Directory.CreateDirectory(cfgDir);
                var cfg = Path.Combine(cfgDir, "RainbowMage.OverlayPlugin.config.json");
                File.WriteAllText(cfg,
                    "{\"Overlays\":[],\"WSServerRunning\":true,\"WSServerIP\":\"127.0.0.1\",\"WSServerPort\":10501,\"WSServerSSL\":false}");
                log("seeded OverlayPlugin config with WSServer on 10501");
            }
            catch (Exception ex) { log("config seed failed: " + ex.Message); }
        }

        public static async Task<bool> WaitForPort(int port, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var c = new TcpClient();
                    var connect = c.ConnectAsync("127.0.0.1", port);
                    if (await Task.WhenAny(connect, Task.Delay(500)) == connect && c.Connected) return true;
                }
                catch { /* not up yet */ }
                await Task.Delay(500);
            }
            return false;
        }
    }
}
