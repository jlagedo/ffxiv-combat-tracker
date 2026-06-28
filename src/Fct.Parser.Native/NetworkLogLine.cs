using System.Globalization;

namespace Fct.Parser.Native;

// ACT's decoded FFXIV log-line types (the leading numeric code of each Network_*.log line).
// Only the codes this structural parser projects are named; everything else is Unknown.
public enum LogLineType
{
    Unknown = -1,
    ChatLog = 0,
    ChangeZone = 1,
    ChangePrimaryPlayer = 2,
    AddCombatant = 3,
    RemoveCombatant = 4,
    NetworkAbility = 21,
    NetworkAOEAbility = 22,
}

// An actor reference (id + name) as it appears in combatant/ability lines.
public readonly record struct ActorRef(string Id, string Name);

// The structural projection of an ability line (21/22). Damage is intentionally absent.
public readonly record struct AbilityInfo(
    ActorRef Source, string AbilityId, string AbilityName, ActorRef Target);

// One parsed network-log line. Holds the raw pipe-split fields plus typed accessors for the
// parts that are structurally unambiguous. Parsing never throws; use TryParse.
public readonly struct NetworkLogLine
{
    private readonly string[] _fields;

    public int TypeCode { get; }
    public DateTimeOffset Timestamp { get; }
    public string Raw { get; }

    public IReadOnlyList<string> Fields => _fields;
    public int FieldCount => _fields.Length;

    public LogLineType Type =>
        Enum.IsDefined(typeof(LogLineType), TypeCode) ? (LogLineType)TypeCode : LogLineType.Unknown;

    private NetworkLogLine(int typeCode, DateTimeOffset timestamp, string[] fields, string raw)
    {
        TypeCode = typeCode;
        Timestamp = timestamp;
        _fields = fields;
        Raw = raw;
    }

    public string? Field(int index) =>
        index >= 0 && index < _fields.Length ? _fields[index] : null;

    // Minimum structurally valid line: type | timestamp | ... | checksum.
    public static bool TryParse(string? raw, out NetworkLogLine line)
    {
        line = default;
        if (string.IsNullOrEmpty(raw))
            return false;

        var fields = raw.Split('|');
        if (fields.Length < 3)
            return false;

        if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var typeCode))
            return false;

        if (!DateTimeOffset.TryParse(fields[1], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var ts))
            return false;

        line = new NetworkLogLine(typeCode, ts, fields, raw);
        return true;
    }

    public bool IsAbility => TypeCode is 21 or 22;

    // Zone name from a ChangeZone (01) line.
    public string? ZoneName => TypeCode == 1 ? Field(3) : null;

    // The actor named by an actor-centric line (ChangePrimaryPlayer/AddCombatant/RemoveCombatant).
    public ActorRef? Actor =>
        TypeCode is 2 or 3 or 4 && Field(2) is { } id && Field(3) is { } name
            ? new ActorRef(id, name)
            : null;

    // Source/target/ability of an ability line (21/22). Null if the line is too short.
    public AbilityInfo? Ability =>
        IsAbility && FieldCount > 7
            ? new AbilityInfo(
                new ActorRef(_fields[2], _fields[3]),
                _fields[4], _fields[5],
                new ActorRef(_fields[6], _fields[7]))
            : null;
}
