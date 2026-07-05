#nullable enable
using System;
using System.Globalization;
using Fct.Abstractions;

namespace Fct.Bridge
{
    // A recorded session of bridged game-event frames: the exact decoded stream the host bus carried,
    // each line prefixed with the event's offset from the session start so a driver can reproduce the
    // cadence. The payload is the identical <see cref="GameEventFrame"/> wire the satellite forwards, so
    // one codec records, replays in-process, and plays back over a pipe (ISOLATION-PLAN P2). A recorded
    // stream carries the encounter-lifecycle frames (SetEncounter/EndCombat/ZoneChange) explicitly, so
    // replaying it reproduces the same encounter splits deterministically — no wall clock, no plugin.
    //
    // Line form: <relativeMicros>\t<GameEventFrame wire>  (e.g. "1523471\tEVT SWING\t2026-…\t2\t0\t…").
    public static class FrameSession
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // The tab-free, invariant header written first so a reader can sniff the format and version.
        public const string Header = "# fct-frame-session v1";

        /// <summary>
        /// Format one recorded frame, or null when the event is not forwardable (mirrors
        /// <see cref="GameEventFrame.ToWire"/>). <paramref name="relativeMicros"/> is the event's offset
        /// from the session start in microseconds.
        /// </summary>
        public static string? FormatLine(GameEvent evt, long relativeMicros)
        {
            var wire = GameEventFrame.ToWire(evt);
            if (wire is null) return null;
            if (relativeMicros < 0) relativeMicros = 0;
            return relativeMicros.ToString(Inv) + "\t" + wire;
        }

        /// <summary>
        /// Parse one recorded line back into its relative offset + decoded event. Returns false for the
        /// header, blank lines, or any line whose payload is not a valid frame (as with the wire codec,
        /// decoded events carry <c>Sequence == 0</c>; the replay sink re-stamps them).
        /// </summary>
        public static bool TryParseLine(string? line, out long relativeMicros, out GameEvent? evt)
        {
            relativeMicros = 0;
            evt = null;
            if (string.IsNullOrEmpty(line) || line![0] == '#') return false;

            int tab = line.IndexOf('\t');
            if (tab <= 0) return false;
            if (!long.TryParse(line.Substring(0, tab), NumberStyles.Integer, Inv, out relativeMicros))
                return false;

            return GameEventFrame.TryParse(line.Substring(tab + 1), out evt) && evt is not null;
        }
    }
}
