using System;
using System.Globalization;

namespace Fct.App;

// The line-oriented handshake the net48 satellite sends over the bridge pipe at startup:
//   READY pid=<n> x64=<bool> clr=<version>
//   HWND <hex>            (the window handle to embed)
// Pure parsing, separated from the I/O in SatelliteHost so it can be unit-tested.
internal static class SatelliteProtocol
{
    public const string ReadyPrefix = "READY";
    public const string HwndPrefix = "HWND ";

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
}
