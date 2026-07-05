using System;
using System.Globalization;

namespace Fct.Bridge;

// One plugin the satellite loaded into its own embeddable window.
public readonly struct PluginLine
{
    public PluginLine(string key, IntPtr hwnd, string status, string title)
    { Key = key; Hwnd = hwnd; Status = status; Title = title; }

    public string Key { get; }
    public IntPtr Hwnd { get; }
    public string Status { get; }
    public string Title { get; }
}

// The line-oriented handshake the net48 satellite sends over the bridge pipe at startup:
//   READY pid=<n> x64=<bool> clr=<version>
//   HWND <hex>                              (primary window; handshake compat)
//   PLUGIN <key>|<hwndHex>|<status>|<title> (one per loaded plugin window)
//   PLUGINS-END                             (no more plugin lines)
// Pure parsing/formatting, separated from the I/O (SatelliteHost on the host, Program on the
// satellite) so both ends share one implementation and it can be unit-tested.
public static class SatelliteProtocol
{
    // Wire-protocol version (distinct from the CLR runtime version in the READY `clr` field). v2 adds
    // the satellite identity + hosted package to the handshake so the host can attribute N concurrent
    // satellites; v3 adds the host-routed service commands (audio produce/sink, P6). Bumped whenever the
    // frame grammar changes; both ends live in this one file.
    public const int ProtocolVersion = 3;

    public const string ReadyPrefix = "READY";
    public const string HwndPrefix = "HWND ";
    public const string PluginPrefix = "PLUGIN ";
    public const string PluginsEnd = "PLUGINS-END";

    // Host -> satellite commands (over the separate command pipe), and the satellite's teardown ack.
    public const string LoadPluginPrefix = "LOADPLUGIN ";
    public const string UnloadPluginPrefix = "UNLOADPLUGIN ";
    public const string UnloadedPrefix = "UNLOADED ";

    // Satellite -> host: the downstream stream set this satellite's facade needs (P4). The host fans
    // only these streams down to it. Comma-separated canonical tokens (see the Stream* constants).
    public const string SubscribePrefix = "SUBSCRIBE ";

    // Host-routed service commands (P6). These are point-to-point RPC, NOT game events — they carry the
    // facade's audio/sink/callback traffic over the control channels (never the re-sequenced game-event
    // bus). SPEAK/PLAYSND flow BOTH ways with one codec: satellite->host is a producer request the host
    // fans to registered sinks; host->satellite is the host relaying a produced call down to a satellite
    // whose plugin registered a terminal sink. REGISTERSINK/UNREGISTERSINK are satellite->host only.
    // Free-text payloads (TTS text, file paths) are base64(UTF-8) so '|'/tab/newline cross losslessly.
    public const string SpeakPrefix = "SPEAK ";              // <vol>|<channel>|<sync 0/1>|<b64 text>
    public const string PlaySoundPrefix = "PLAYSND ";        // <vol>|<b64 filePath>
    public const string RegisterSinkPrefix = "REGISTERSINK ";    // <tts|sound|both>
    public const string UnregisterSinkPrefix = "UNREGISTERSINK "; // <tts|sound|both>

    // Canonical downstream stream tokens. The host maps them to concrete event types; unknown tokens
    // are ignored. Kept as protocol constants so both ends name the streams identically.
    public const string StreamSwings = "swings";          // CombatSwing + the encounter lifecycle
    public const string StreamRawLog = "rawlog";          // the RawLogLine firehose
    public const string StreamPackets = "packets";        // the RawPacketReceived firehose
    public const string StreamCombatants = "combatants";  // CombatantAdded/Removed
    public const string StreamZoneParty = "zoneparty";    // ZoneChanged/PartyChanged/PrimaryPlayerChanged
    public const string StreamRepository = "repository";  // RepositorySnapshot + ResourceDictionaryForwarded + GameProcessChanged

    // ---- satellite -> host handshake frames (formatted on the satellite, parsed on the host) ----

    /// <summary>
    /// Format the v2 handshake "READY pid=&lt;n&gt; x64=&lt;bool&gt; clr=&lt;version&gt; proto=&lt;v&gt; id=&lt;satId&gt; pkg=&lt;package&gt;".
    /// The v1 fields lead, so the substring checks (<see cref="IsReady"/>/<see cref="IsReady64"/>) and any
    /// older reader still work; the id/package are host-assigned so the host verifies the satellite it
    /// connected is the one it launched.
    /// </summary>
    public static string FormatReady(int pid, bool x64, string clr, string satelliteId, string package)
        => ReadyPrefix + $" pid={pid} x64={x64} clr={clr} proto={ProtocolVersion} id={Token(satelliteId)} pkg={Token(package)}";

    /// <summary>v1-shape overload (no assigned identity): the dev-standalone / parser default.</summary>
    public static string FormatReady(int pid, bool x64, string clr)
        => FormatReady(pid, x64, clr, "", "");

    /// <summary>Format "HWND &lt;hex&gt;" for an embeddable window handle.</summary>
    public static string FormatHwnd(IntPtr handle)
        => HwndPrefix + handle.ToInt64().ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Format "PLUGIN &lt;key&gt;|&lt;hwndHex&gt;|&lt;status&gt;|&lt;title&gt;". '|' in status/title is sanitized to '/'.</summary>
    public static string FormatPlugin(string key, IntPtr handle, string? status, string? title)
        => PluginPrefix + $"{key}|{handle.ToInt64().ToString("X", CultureInfo.InvariantCulture)}|{San(status)}|{San(title)}";

    public static bool IsReady(string? line) =>
        line != null && line.StartsWith(ReadyPrefix, StringComparison.Ordinal);

    // True when the READY line reports a 64-bit satellite (required: FFXIV_ACT_Plugin
    // demands a 64-bit process).
    public static bool IsReady64(string? line) =>
        IsReady(line) && line!.IndexOf("x64=True", StringComparison.OrdinalIgnoreCase) >= 0;

    // Read a "key=value" field from a READY line (v2 handshake). Returns false for a v1 line lacking it.
    public static bool TryGetReadyField(string? line, string key, out string value)
    {
        value = "";
        if (!IsReady(line)) return false;
        foreach (var tok in line!.Split(' '))
        {
            if (tok.StartsWith(key + "=", StringComparison.Ordinal))
            {
                value = tok.Substring(key.Length + 1);
                return true;
            }
        }
        return false;
    }

    /// <summary>The host-assigned satellite id the READY line echoes ("" for a v1/identity-less satellite).</summary>
    public static string ReadySatelliteId(string? line) =>
        TryGetReadyField(line, "id", out var v) ? v : "";

    /// <summary>The hosted-package name the READY line echoes ("" when none).</summary>
    public static string ReadyPackage(string? line) =>
        TryGetReadyField(line, "pkg", out var v) ? v : "";

    /// <summary>The wire-protocol version the READY line reports (1 when absent — a pre-v2 satellite).</summary>
    public static int ReadyProtocol(string? line) =>
        TryGetReadyField(line, "proto", out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;

    // Parse "HWND <hex>" into a window handle. Returns false for any other line or a
    // malformed/zero handle.
    public static bool TryParseHwnd(string? line, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (line == null || !line.StartsWith(HwndPrefix, StringComparison.Ordinal))
            return false;

        var hex = line.Substring(HwndPrefix.Length).Trim();
        if (hex.Length == 0)
            return false;

        if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) || value == 0)
            return false;

        handle = new IntPtr(value);
        return true;
    }

    // Parse "PLUGIN <key>|<hwndHex>|<status>|<title>". Returns false for any other line or a
    // malformed/zero handle.
    public static bool TryParsePlugin(string? line, out PluginLine plugin)
    {
        plugin = default;
        if (line == null || !line.StartsWith(PluginPrefix, StringComparison.Ordinal))
            return false;

        var parts = line.Substring(PluginPrefix.Length).Split(new[] { '|' }, 4);
        if (parts.Length < 4)
            return false;

        if (!long.TryParse(parts[1].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) || value == 0)
            return false;

        plugin = new PluginLine(parts[0].Trim(), new IntPtr(value), parts[2], parts[3]);
        return true;
    }

    // ---- host -> satellite command frames ----

    /// <summary>Format "LOADPLUGIN &lt;key&gt;|&lt;dllPath&gt;|&lt;title&gt;". '|' in fields is sanitized to '/'.</summary>
    public static string FormatLoadPlugin(string key, string dllPath, string title)
        => LoadPluginPrefix + $"{San(key)}|{San(dllPath)}|{San(title)}";

    /// <summary>Format "UNLOADPLUGIN &lt;key&gt;".</summary>
    public static string FormatUnloadPlugin(string key) => UnloadPluginPrefix + San(key);

    /// <summary>Format the satellite's teardown ack "UNLOADED &lt;key&gt;|&lt;ok&gt;".</summary>
    public static string FormatUnloaded(string key, bool ok) => UnloadedPrefix + $"{San(key)}|{(ok ? "1" : "0")}";

    public static bool TryParseLoadPlugin(string? line, out string key, out string dllPath, out string title)
    {
        key = dllPath = title = "";
        if (line == null || !line.StartsWith(LoadPluginPrefix, StringComparison.Ordinal)) return false;
        var parts = line.Substring(LoadPluginPrefix.Length).Split(new[] { '|' }, 3);
        if (parts.Length < 3) return false;
        key = parts[0].Trim();
        dllPath = parts[1];
        title = parts[2];
        return key.Length != 0 && dllPath.Length != 0;
    }

    /// <summary>Format "SUBSCRIBE &lt;token&gt;[,&lt;token&gt;...]" — the satellite's declared downstream stream set.</summary>
    public static string FormatSubscribe(params string[] streams)
    {
        var tokens = new System.Collections.Generic.List<string>(streams.Length);
        foreach (var s in streams)
            if (!string.IsNullOrWhiteSpace(s)) tokens.Add(Token(s));
        return SubscribePrefix + string.Join(",", tokens);
    }

    public static bool TryParseSubscribe(string? line, out string[] streams)
    {
        streams = Array.Empty<string>();
        if (line == null || !line.StartsWith(SubscribePrefix, StringComparison.Ordinal)) return false;
        var body = line.Substring(SubscribePrefix.Length).Trim();
        streams = body.Length == 0 ? Array.Empty<string>() : body.Split(',');
        return true;
    }

    public static bool TryParseUnloadPlugin(string? line, out string key)
    {
        key = "";
        if (line == null || !line.StartsWith(UnloadPluginPrefix, StringComparison.Ordinal)) return false;
        key = line.Substring(UnloadPluginPrefix.Length).Trim();
        return key.Length != 0;
    }

    public static bool TryParseUnloaded(string? line, out string key, out bool ok)
    {
        key = ""; ok = false;
        if (line == null || !line.StartsWith(UnloadedPrefix, StringComparison.Ordinal)) return false;
        var parts = line.Substring(UnloadedPrefix.Length).Split(new[] { '|' }, 2);
        key = parts[0].Trim();
        ok = parts.Length > 1 && parts[1].Trim() == "1";
        return key.Length != 0;
    }

    // ---- host-routed service commands (P6): audio produce + sink registration ----

    /// <summary>Format "SPEAK &lt;vol&gt;|&lt;channel&gt;|&lt;sync&gt;|&lt;b64 text&gt;" — a TTS request (either direction).</summary>
    public static string FormatSpeak(string text, int volume, int channel, bool synchronous)
        => SpeakPrefix + $"{volume}|{channel}|{(synchronous ? 1 : 0)}|{B64(text)}";

    public static bool TryParseSpeak(string? line, out string text, out int volume, out int channel, out bool synchronous)
    {
        text = ""; volume = 100; channel = 0; synchronous = false;
        if (line == null || !line.StartsWith(SpeakPrefix, StringComparison.Ordinal)) return false;
        var parts = line.Substring(SpeakPrefix.Length).Split(new[] { '|' }, 4);
        if (parts.Length < 4) return false;
        int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out volume);
        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out channel);
        synchronous = parts[2].Trim() == "1";
        text = UnB64(parts[3]);
        return true;
    }

    /// <summary>Format "PLAYSND &lt;vol&gt;|&lt;b64 filePath&gt;" — a sound-file request (either direction).</summary>
    public static string FormatPlaySound(string filePath, int volume)
        => PlaySoundPrefix + $"{volume}|{B64(filePath)}";

    public static bool TryParsePlaySound(string? line, out string filePath, out int volume)
    {
        filePath = ""; volume = 100;
        if (line == null || !line.StartsWith(PlaySoundPrefix, StringComparison.Ordinal)) return false;
        var parts = line.Substring(PlaySoundPrefix.Length).Split(new[] { '|' }, 2);
        if (parts.Length < 2) return false;
        int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out volume);
        filePath = UnB64(parts[1]);
        return true;
    }

    /// <summary>Format "REGISTERSINK &lt;caps&gt;" (caps ∈ tts|sound|both) — the satellite's plugin took a slot.</summary>
    public static string FormatRegisterSink(string caps) => RegisterSinkPrefix + Token(caps);

    public static bool TryParseRegisterSink(string? line, out string caps)
    {
        caps = "";
        if (line == null || !line.StartsWith(RegisterSinkPrefix, StringComparison.Ordinal)) return false;
        caps = line.Substring(RegisterSinkPrefix.Length).Trim();
        return caps.Length != 0;
    }

    /// <summary>Format "UNREGISTERSINK &lt;caps&gt;" — the satellite's plugin released a slot.</summary>
    public static string FormatUnregisterSink(string caps) => UnregisterSinkPrefix + Token(caps);

    public static bool TryParseUnregisterSink(string? line, out string caps)
    {
        caps = "";
        if (line == null || !line.StartsWith(UnregisterSinkPrefix, StringComparison.Ordinal)) return false;
        caps = line.Substring(UnregisterSinkPrefix.Length).Trim();
        return caps.Length != 0;
    }

    // Loss-free field encoding for free text (TTS strings, file paths, log lines, callback args): base64
    // over UTF-8, so '|', tab, and newline never split a command or the outer line. Empty in/empty out.
    private static string B64(string? value)
        => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

    private static string UnB64(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch { return ""; }
    }

    private static string San(string? value) => (value ?? "").Replace('|', '/');

    // A space-delimited READY field must contain no whitespace or '|'. Satellite ids/packages are simple
    // identifiers by convention; sanitize defensively so a stray character can't split the handshake.
    private static string Token(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var chars = value!.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (char.IsWhiteSpace(chars[i]) || chars[i] == '|') chars[i] = '_';
        return new string(chars);
    }
}
