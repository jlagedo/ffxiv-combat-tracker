#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Fct.Abstractions;

namespace Fct.Bridge
{
    // A typed game event forwarded from the net48 satellite to the net10 host over the bridge pipe,
    // feeding the host's IGameEventSink. Shared source: the satellite serializes with ToWire(); the
    // host parses with TryParse() — same code, same shape, compiled on both sides (net48 + net10).
    //
    // Wire form is a single line, tab-delimited, with free text escaped so a record never spans lines
    // or collides with the delimiter (no JSON dependency on either side), mirroring BridgeLogRecord:
    //   EVT <tag>\t<iso8601 timestamp>\t<payload fields...>
    //
    // Sequence is deliberately NOT on the wire: the host re-stamps each decoded event from its own
    // IGameEventSink.NextSequence() so the bus keeps one coherent per-session sequence space. TryParse
    // therefore yields records with Sequence == 0.
    //
    // Only the events the satellite SDK/ACT hub structurally exposes are representable here (RawLogLine,
    // ZoneChanged, PartyChanged, PrimaryPlayerChanged, CombatantAdded/Removed, ActionEffect, and the
    // RawPacketReceived firehose — raw bytes, never decoded on this side). The plugin is the sole parser,
    // so events that exist only as parsed log-line fields (StatusApplied/Removed, Cast*, DeathOccurred,
    // HpUpdated) are not synthesized here — consumers reach them through the RawLogLine firehose. ToWire
    // returns null for any unsupported record.
    internal static class GameEventFrame
    {
        public const string Prefix = "EVT ";

        private const string TagRaw = "RAW";
        private const string TagZone = "ZONE";
        private const string TagParty = "PARTY";
        private const string TagPrimary = "PRIMARY";
        private const string TagCombatantAdded = "CBADD";
        private const string TagCombatantRemoved = "CBDEL";
        private const string TagAction = "ACT";
        private const string TagPacket = "PKT";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Serialize a GameEvent to one wire line, or null if the event type is not forwardable.
        public static string? ToWire(GameEvent evt)
        {
            switch (evt)
            {
                case RawLogLine e:
                    return Head(TagRaw, e.Timestamp)
                        + '\t' + ((int)e.Type).ToString(Inv)
                        + '\t' + Enc(e.Line)
                        + '\t' + Enc(e.OriginalLine);

                case ZoneChanged e:
                    return Head(TagZone, e.Timestamp)
                        + '\t' + e.ZoneId.ToString(Inv)
                        + '\t' + Enc(e.ZoneName);

                case PartyChanged e:
                    return Head(TagParty, e.Timestamp)
                        + '\t' + JoinIds(e.Members);

                case PrimaryPlayerChanged e:
                    return Head(TagPrimary, e.Timestamp)
                        + '\t' + e.ActorId.ToString(Inv)
                        + '\t' + Enc(e.Name);

                case CombatantRemoved e:
                    return Head(TagCombatantRemoved, e.Timestamp)
                        + '\t' + e.ActorId.ToString(Inv);

                case CombatantAdded e:
                    return Head(TagCombatantAdded, e.Timestamp) + '\t' + EncodeActor(e.Combatant);

                case ActionEffect e:
                    return Head(TagAction, e.Timestamp) + '\t' + EncodeAction(e);

                case RawPacketReceived e:
                    // Bytes are base64 — the tab/backslash escaping (Enc) cannot carry arbitrary binary.
                    return Head(TagPacket, e.Timestamp)
                        + '\t' + Enc(e.Connection)
                        + '\t' + e.Epoch.ToString(Inv)
                        + '\t' + ((byte)e.Direction).ToString(Inv)
                        + '\t' + Convert.ToBase64String(e.Bytes ?? Array.Empty<byte>());

                default:
                    return null;
            }
        }

        // True only for a well-formed "EVT ..." line of a known tag; any other line → false.
        public static bool TryParse(string? line, out GameEvent? evt)
        {
            evt = null;
            if (line == null || !line.StartsWith(Prefix, StringComparison.Ordinal))
                return false;

            var f = line.Substring(Prefix.Length).Split('\t');
            if (f.Length < 2)
                return false;
            if (!DateTimeOffset.TryParse(f[1], Inv, DateTimeStyles.RoundtripKind, out var ts))
                return false;

            switch (f[0])
            {
                case TagRaw:
                    if (f.Length < 5 || !int.TryParse(f[2], NumberStyles.Integer, Inv, out var mt)) return false;
                    evt = new RawLogLine(0, ts, (LogMessageType)mt, Dec(f[3]), Dec(f[4]));
                    return true;

                case TagZone:
                    if (f.Length < 4 || !TryU(f[2], out var zid)) return false;
                    evt = new ZoneChanged(0, ts, zid, Dec(f[3]));
                    return true;

                case TagParty:
                    evt = new PartyChanged(0, ts, ParseIds(f.Length >= 3 ? f[2] : ""));
                    return true;

                case TagPrimary:
                    if (f.Length < 4 || !TryU(f[2], out var pid)) return false;
                    evt = new PrimaryPlayerChanged(0, ts, pid, Dec(f[3]));
                    return true;

                case TagCombatantRemoved:
                    if (f.Length < 3 || !TryU(f[2], out var rid)) return false;
                    evt = new CombatantRemoved(0, ts, rid);
                    return true;

                case TagCombatantAdded:
                    return TryDecodeActor(ts, f, out evt);

                case TagAction:
                    return TryDecodeAction(ts, f, out evt);

                case TagPacket:
                    return TryDecodePacket(ts, f, out evt);

                default:
                    return false;
            }
        }

        // ---- Actor (CombatantAdded) — snapshot-relevant scalar set; lossless for DoL/DoH (G10). ----
        // Nested Statuses/Enmity are firehose-only today, so they cross empty; Cast/Position default.

        private static string EncodeActor(Actor a)
        {
            var sb = new StringBuilder(160);
            sb.Append(a.Id.ToString(Inv));
            sb.Append('\t').Append(a.OwnerId.ToString(Inv));
            sb.Append('\t').Append(((byte)a.Kind).ToString(Inv));
            sb.Append('\t').Append(a.Job.ToString(Inv));
            sb.Append('\t').Append(a.Level.ToString(Inv));
            sb.Append('\t').Append(Enc(a.Name));
            sb.Append('\t').Append(a.Hp.ToString(Inv));
            sb.Append('\t').Append(a.MaxHp.ToString(Inv));
            sb.Append('\t').Append(a.Mp.ToString(Inv));
            sb.Append('\t').Append(a.MaxMp.ToString(Inv));
            sb.Append('\t').Append(a.WorldId.ToString(Inv));
            sb.Append('\t').Append(Enc(a.WorldName));
            sb.Append('\t').Append(a.TargetId.ToString(Inv));
            sb.Append('\t').Append(((byte)a.Party).ToString(Inv));
            sb.Append('\t').Append(a.InCombat ? '1' : '0');
            sb.Append('\t').Append(NU(a.CurrentWorldId));
            sb.Append('\t').Append(NU(a.CurrentCp));
            sb.Append('\t').Append(NU(a.MaxCp));
            sb.Append('\t').Append(NU(a.CurrentGp));
            sb.Append('\t').Append(NU(a.MaxGp));
            sb.Append('\t').Append(a.Order.HasValue ? a.Order.Value.ToString(Inv) : "");
            return sb.ToString();
        }

        // 2 header fields (tag, ts) + 21 actor fields.
        private const int ActorFieldCount = 21;

        private static bool TryDecodeActor(DateTimeOffset ts, string[] f, out GameEvent? evt)
        {
            evt = null;
            if (f.Length < 2 + ActorFieldCount) return false;
            int i = 2;
            if (!TryU(f[i++], out var id)) return false;
            if (!TryU(f[i++], out var ownerId)) return false;
            if (!TryB(f[i++], out var kind)) return false;
            if (!TryI(f[i++], out var job)) return false;
            if (!TryI(f[i++], out var level)) return false;
            var name = Dec(f[i++]);
            if (!TryU(f[i++], out var hp)) return false;
            if (!TryU(f[i++], out var maxHp)) return false;
            if (!TryU(f[i++], out var mp)) return false;
            if (!TryU(f[i++], out var maxMp)) return false;
            if (!TryU(f[i++], out var worldId)) return false;
            var worldName = Dec(f[i++]);
            if (!TryU(f[i++], out var targetId)) return false;
            if (!TryB(f[i++], out var party)) return false;
            var inCombat = f[i++] == "1";

            var actor = new Actor(
                id, ownerId, (ActorKind)kind, job, level, name,
                hp, maxHp, mp, maxMp,
                Cast: null,
                Position: default,
                WorldId: worldId, WorldName: worldName,
                BNpcNameId: 0, BNpcId: 0,
                TargetId: targetId, TargetOfTargetId: 0,
                EffectiveDistance: 0,
                Party: (PartyMembership)party,
                InCombat: inCombat,
                Statuses: Array.Empty<StatusEffect>(),
                Enmity: Array.Empty<EnmityEntry>())
            {
                CurrentWorldId = ParseNU(f[i++]),
                CurrentCp = ParseNU(f[i++]),
                MaxCp = ParseNU(f[i++]),
                CurrentGp = ParseNU(f[i++]),
                MaxGp = ParseNU(f[i++]),
                Order = ParseNI(f[i]),
            };
            evt = new CombatantAdded(0, ts, actor);
            return true;
        }

        // ---- ActionEffect (from a post-aggregation MasterSwing) — N targets, flat. ----

        private static string EncodeAction(ActionEffect e)
        {
            var sb = new StringBuilder(96);
            sb.Append(e.Source.Id.ToString(Inv));
            sb.Append('\t').Append(Enc(e.Source.Name));
            sb.Append('\t').Append(e.ActionId.ToString(Inv));
            sb.Append('\t').Append(Enc(e.ActionName ?? ""));
            sb.Append('\t').Append(e.Targets.Count.ToString(Inv));
            foreach (var t in e.Targets)
            {
                sb.Append('\t').Append(t.Target.Id.ToString(Inv));
                sb.Append('\t').Append(Enc(t.Target.Name));
                sb.Append('\t').Append(t.Amount.ToString(Inv));
                sb.Append('\t').Append(((int)t.Flags).ToString(Inv));
            }
            return sb.ToString();
        }

        private static bool TryDecodeAction(DateTimeOffset ts, string[] f, out GameEvent? evt)
        {
            evt = null;
            // 2 header + 5 fixed (srcId, srcName, actionId, actionName, targetCount).
            if (f.Length < 7) return false;
            int i = 2;
            if (!TryU(f[i++], out var srcId)) return false;
            var srcName = Dec(f[i++]);
            if (!TryU(f[i++], out var actionId)) return false;
            var actionNameRaw = Dec(f[i++]);
            string? actionName = actionNameRaw.Length == 0 ? null : actionNameRaw;
            if (!TryI(f[i++], out var count) || count < 0) return false;
            if (f.Length < i + count * 4) return false;

            var targets = new List<EffectTarget>(count);
            for (int t = 0; t < count; t++)
            {
                if (!TryU(f[i++], out var tid)) return false;
                var tname = Dec(f[i++]);
                if (!TryL(f[i++], out var amount)) return false;
                if (!TryI(f[i++], out var flags)) return false;
                targets.Add(new EffectTarget(new ActorRef(tid, tname), amount, (EffectFlags)flags));
            }
            evt = new ActionEffect(0, ts, new ActorRef(srcId, srcName), actionId, actionName, targets);
            return true;
        }

        // ---- RawPacketReceived (the NetworkReceived/Sent firehose) — bytes carried as base64. ----

        private static bool TryDecodePacket(DateTimeOffset ts, string[] f, out GameEvent? evt)
        {
            evt = null;
            // 2 header + 4 fields (connection, epoch, direction, base64 bytes).
            if (f.Length < 6) return false;
            var connection = Dec(f[2]);
            if (!TryL(f[3], out var epoch)) return false;
            if (!TryB(f[4], out var dir)) return false;
            byte[] bytes;
            try { bytes = f[5].Length == 0 ? Array.Empty<byte>() : Convert.FromBase64String(f[5]); }
            catch (FormatException) { return false; }
            evt = new RawPacketReceived(0, ts, connection, epoch, (PacketDirection)dir, bytes);
            return true;
        }

        // ---- Primitives ----------------------------------------------------------------------

        private static string Head(string tag, DateTimeOffset ts) =>
            Prefix + tag + '\t' + ts.ToString("o", Inv);

        private static string JoinIds(IReadOnlyList<uint> ids)
        {
            if (ids.Count == 0) return "";
            var sb = new StringBuilder(ids.Count * 8);
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ids[i].ToString(Inv));
            }
            return sb.ToString();
        }

        private static IReadOnlyList<uint> ParseIds(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<uint>();
            var parts = s.Split(',');
            var list = new List<uint>(parts.Length);
            foreach (var p in parts)
                if (p.Length > 0 && uint.TryParse(p, NumberStyles.Integer, Inv, out var v)) list.Add(v);
            return list;
        }

        // Nullable-uint / nullable-int wire form: empty field = null.
        private static string NU(uint? v) => v.HasValue ? v.Value.ToString(Inv) : "";
        private static uint? ParseNU(string s) =>
            s.Length == 0 ? (uint?)null : (uint.TryParse(s, NumberStyles.Integer, Inv, out var v) ? v : (uint?)null);
        private static int? ParseNI(string s) =>
            s.Length == 0 ? (int?)null : (int.TryParse(s, NumberStyles.Integer, Inv, out var v) ? v : (int?)null);

        private static bool TryU(string s, out uint v) => uint.TryParse(s, NumberStyles.Integer, Inv, out v);
        private static bool TryI(string s, out int v) => int.TryParse(s, NumberStyles.Integer, Inv, out v);
        private static bool TryL(string s, out long v) => long.TryParse(s, NumberStyles.Integer, Inv, out v);
        private static bool TryB(string s, out byte v) => byte.TryParse(s, NumberStyles.Integer, Inv, out v);

        // Same backslash escaping as BridgeLogRecord: a field never contains a raw tab/newline.
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
