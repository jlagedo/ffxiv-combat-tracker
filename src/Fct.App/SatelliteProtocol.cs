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
}
