#nullable enable
using System;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Fct.Logging
{
    // One log record forwarded from the net48 satellite to the net10 host over the bridge pipe, so
    // every satellite log line lands in the host's unified Serilog pipeline. Shared source: the
    // satellite serializes with ToWire(); the host parses with TryParse() — same code, same shape.
    //
    // Wire form is a single line, tab-delimited, with the free-text message/exception escaped so a
    // record never spans lines or collides with the delimiter (no JSON dependency on either side):
    //   LOG <iso8601>\t<level>\t<eventId>\t<eventName>\t<category>\t<message>\t<exception>
    internal sealed class BridgeLogRecord
    {
        public const string Prefix = "LOG ";

        public DateTimeOffset Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Category { get; set; } = "";
        public int EventId { get; set; }
        public string EventName { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Exception { get; set; }   // null when no exception

        public string ToWire()
        {
            var sb = new StringBuilder(Prefix.Length + Message.Length + 64);
            sb.Append(Prefix);
            sb.Append(Timestamp.ToString("o", CultureInfo.InvariantCulture));
            sb.Append('\t').Append((int)Level);
            sb.Append('\t').Append(EventId.ToString(CultureInfo.InvariantCulture));
            sb.Append('\t').Append(Enc(EventName));
            sb.Append('\t').Append(Enc(Category));
            sb.Append('\t').Append(Enc(Message));
            sb.Append('\t').Append(Enc(Exception));
            return sb.ToString();
        }

        // True only for a well-formed "LOG ..." line; any other line (handshake, malformed) → false.
        public static bool TryParse(string line, out BridgeLogRecord? record)
        {
            record = null;
            if (line == null || !line.StartsWith(Prefix, StringComparison.Ordinal))
                return false;

            var body = line.Substring(Prefix.Length);
            var f = body.Split('\t');
            if (f.Length < 7)
                return false;

            if (!DateTimeOffset.TryParse(f[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var ts))
                return false;
            if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl))
                return false;
            if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var eid))
                return false;

            var ex = Dec(f[6]);
            record = new BridgeLogRecord
            {
                Timestamp = ts,
                Level = (lvl >= (int)LogLevel.Trace && lvl <= (int)LogLevel.None) ? (LogLevel)lvl : LogLevel.Information,
                EventName = Dec(f[3]),
                Category = Dec(f[4]),
                Message = Dec(f[5]),
                Exception = ex.Length == 0 ? null : ex,
            };
            return true;
        }

        private static string Enc(string? s)
        {
            if (s is null || s.Length == 0) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        private static string Dec(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch == '\\' && i + 1 < s.Length)
                {
                    var n = s[++i];
                    switch (n)
                    {
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'n': sb.Append('\n'); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
