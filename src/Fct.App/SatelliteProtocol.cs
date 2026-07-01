using System;
using System.Globalization;

namespace Fct.App;

// One plugin the satellite loaded into its own embeddable window.
internal readonly struct PluginLine
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
// Pure parsing, separated from the I/O in SatelliteHost so it can be unit-tested.
internal static class SatelliteProtocol
{
    public const string ReadyPrefix = "READY";
    public const string HwndPrefix = "HWND ";
    public const string PluginPrefix = "PLUGIN ";
    public const string PluginsEnd = "PLUGINS-END";

    // Host -> satellite commands (over the separate command pipe), and the satellite's teardown ack.
    public const string LoadPluginPrefix = "LOADPLUGIN ";
    public const string UnloadPluginPrefix = "UNLOADPLUGIN ";
    public const string UnloadedPrefix = "UNLOADED ";

    public static bool IsReady(string? line) =>
        line != null && line.StartsWith(ReadyPrefix, StringComparison.Ordinal);

    // True when the READY line reports a 64-bit satellite (required: FFXIV_ACT_Plugin
    // demands a 64-bit process).
    public static bool IsReady64(string? line) =>
        IsReady(line) && line!.IndexOf("x64=True", StringComparison.OrdinalIgnoreCase) >= 0;

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

    private static string San(string? value) => (value ?? "").Replace('|', '/');
}
