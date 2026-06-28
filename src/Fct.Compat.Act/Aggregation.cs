using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Advanced_Combat_Tracker
{
    // Clean-room reimplementation of ACT's combat aggregation. Matches the public surface
    // the real FFXIV_ACT_Plugin populates and OverlayPlugin reads, with simplified internals
    // (no caching/threat/personal-duration). The "All" attack-type bucket aggregates everything.
    public enum AttackTypeTypeEnum { Unknown, Melee, UnknownNonMelee }

    public class AttackType
    {
        public delegate string StringDataCallback(AttackType Data);
        public delegate Color ColorDataCallback(AttackType Data);

        public class ColumnDef
        {
            public StringDataCallback GetCellData;
            public StringDataCallback GetSqlData;
            public Comparison<AttackType> SortComparer;
            public ColorDataCallback GetCellForeColor = _ => Color.Transparent;
            public ColorDataCallback GetCellBackColor = _ => Color.Transparent;
            public string SqlDataType { get; }
            public string SqlDataName { get; }
            public bool DefaultVisible { get; }
            public string Label { get; }
            public ColumnDef(string Label, bool DefaultVisible, string SqlDataType, string SqlDataName,
                StringDataCallback CellDataCallback, StringDataCallback SqlDataCallback, Comparison<AttackType> SortComparer)
            { this.Label = Label; this.DefaultVisible = DefaultVisible; this.SqlDataType = SqlDataType;
              this.SqlDataName = SqlDataName; GetCellData = CellDataCallback; GetSqlData = SqlDataCallback; this.SortComparer = SortComparer; }
        }

        public static Dictionary<string, ColumnDef> ColumnDefs = new Dictionary<string, ColumnDef>();

        private readonly string type;
        public DamageTypeData Parent { get; }
        public List<MasterSwing> Items { get; } = new List<MasterSwing>();
        public Dictionary<string, object> Tags { get; set; } = new Dictionary<string, object>();
        public string Type => type;

        public AttackType(string theAttackType, DamageTypeData parent) { type = theAttackType; Parent = parent; }

        public void AddCombatAction(MasterSwing action) => Items.Add(action);

        public long Damage => Items.Where(s => (long)s.Damage > 0).Sum(s => (long)s.Damage);
        public int Hits => Items.Count(s => ActGlobals.blockIsHit ? (long)s.Damage >= 0 : (long)s.Damage > 0);
        public int CritHits => Items.Count(s => s.Critical && (long)s.Damage > 0);
        public float CritPerc => Hits == 0 ? 0f : (float)CritHits / Hits * 100f;
        public int Swings => Items.Count(s => s.Damage != Dnum.Death);
        public int Misses => Items.Count(s => s.Damage == Dnum.Miss);
        public int Blocked => 0;
        public double Average => Hits > 0 ? (double)Damage / Hits : 0.0;
        public long MaxHit => Items.Where(s => (long)s.Damage > 0).Select(s => (long)s.Damage).DefaultIfEmpty(0).Max();
        public long MinHit => Items.Where(s => (long)s.Damage > 0).Select(s => (long)s.Damage).DefaultIfEmpty(0).Min();
        public long Median
        {
            get { var l = Items.Where(s => (long)s.Damage >= 0).Select(s => (long)s.Damage).OrderBy(x => x).ToList();
                  return l.Count == 0 ? 0L : l[l.Count / 2]; }
        }
        public DateTime StartTime => Items.Count == 0 ? DateTime.MaxValue : Items.Min(s => s.Time);
        public DateTime EndTime => Items.Count == 0 ? DateTime.MinValue : Items.Max(s => s.Time);
        public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;
        public double EncDPS { get { var d = Parent?.Parent?.Parent?.Duration.TotalSeconds ?? 0; return d > 0 ? Damage / d : 0.0; } }
        public double DPS { get { var d = Duration.TotalSeconds; return d > 0 ? Damage / d : 0.0; } }
        public double CharDPS => EncDPS;
        public float ToHit => Swings == 0 ? 0f : (float)Hits / Swings * 100f;

        public string GetColumnByName(string name) => ColumnDefs.ContainsKey(name) ? ColumnDefs[name].GetCellData(this) : string.Empty;
        public override string ToString() => type;
    }

    public class DamageTypeData
    {
        public delegate string StringDataCallback(DamageTypeData Data);
        public delegate Color ColorDataCallback(DamageTypeData Data);

        public class ColumnDef
        {
            public StringDataCallback GetCellData;
            public StringDataCallback GetSqlData;
            public ColorDataCallback GetCellForeColor = _ => Color.Transparent;
            public ColorDataCallback GetCellBackColor = _ => Color.Transparent;
            public string SqlDataType { get; }
            public string SqlDataName { get; }
            public bool DefaultVisible { get; }
            public string Label { get; }
            public ColumnDef(string Label, bool DefaultVisible, string SqlDataType, string SqlDataName,
                StringDataCallback CellDataCallback, StringDataCallback SqlDataCallback)
            { this.Label = Label; this.DefaultVisible = DefaultVisible; this.SqlDataType = SqlDataType;
              this.SqlDataName = SqlDataName; GetCellData = CellDataCallback; GetSqlData = SqlDataCallback; }
        }

        public static Dictionary<string, ColumnDef> ColumnDefs = new Dictionary<string, ColumnDef>();

        private readonly string tag;
        public CombatantData Parent { get; }
        public bool Outgoing { get; set; }
        public SortedList<string, AttackType> Items { get; set; } = new SortedList<string, AttackType>();
        public Dictionary<string, object> Tags { get; set; } = new Dictionary<string, object>();

        public DamageTypeData(bool outgoing, string Tag, CombatantData parent) { Outgoing = outgoing; tag = Tag; Parent = parent; }

        public void AddCombatAction(MasterSwing action, string theAttackTypeListed)
        {
            if (!Items.TryGetValue(theAttackTypeListed, out var at))
            { at = new AttackType(theAttackTypeListed, this); Items.Add(theAttackTypeListed, at); }
            at.AddCombatAction(action);
        }

        private AttackType All => Items.TryGetValue("All", out var v) ? v : null;
        public long Damage => All?.Damage ?? 0L;
        public int Swings => All?.Swings ?? 0;
        public int Hits => All?.Hits ?? 0;
        public int CritHits => All?.CritHits ?? 0;
        public float CritPerc => All?.CritPerc ?? 0f;
        public int Misses => All?.Misses ?? 0;
        public int Blocked => 0;
        public double Average => All?.Average ?? 0.0;
        public long Median => All?.Median ?? 0L;
        public long MaxHit => All?.MaxHit ?? 0L;
        public long MinHit => All?.MinHit ?? 0L;
        public DateTime StartTime => All?.StartTime ?? DateTime.MaxValue;
        public DateTime EndTime => All?.EndTime ?? DateTime.MinValue;
        public double EncDPS => All?.EncDPS ?? 0.0;
        public double DPS => All?.DPS ?? 0.0;
        public string Type => tag;
        public string GetColumnByName(string name) => ColumnDefs.ContainsKey(name) ? ColumnDefs[name].GetCellData(this) : string.Empty;
        public override string ToString() => tag;
    }

    public class CombatantData : IComparable, IComparable<CombatantData>
    {
        public delegate string ExportStringDataCallback(CombatantData Data, string ExtraFormat);
        public delegate string StringDataCallback(CombatantData Data);
        public delegate Color ColorDataCallback(CombatantData Data);

        public class TextExportFormatter
        {
            public ExportStringDataCallback GetExportString;
            public string Label { get; }
            public string Description { get; }
            public string Name { get; }
            public TextExportFormatter(string Name, string Label, string Description, ExportStringDataCallback FormatterCallback)
            { this.Name = Name; this.Label = Label; this.Description = Description; GetExportString = FormatterCallback; }
        }

        public class ColumnDef
        {
            public StringDataCallback GetCellData;
            public StringDataCallback GetSqlData;
            public Comparison<CombatantData> SortComparer;
            public ColorDataCallback GetCellForeColor = _ => Color.Transparent;
            public ColorDataCallback GetCellBackColor = _ => Color.Transparent;
            public string SqlDataType { get; }
            public string SqlDataName { get; }
            public bool DefaultVisible { get; }
            public string Label { get; }
            public ColumnDef(string Label, bool DefaultVisible, string SqlDataType, string SqlDataName,
                StringDataCallback CellDataCallback, StringDataCallback SqlDataCallback, Comparison<CombatantData> SortComparer)
            { this.Label = Label; this.DefaultVisible = DefaultVisible; this.SqlDataType = SqlDataType;
              this.SqlDataName = SqlDataName; GetCellData = CellDataCallback; GetSqlData = SqlDataCallback; this.SortComparer = SortComparer; }
        }

        public class DamageTypeDef
        {
            public string Label { get; }
            public int AllyValue { get; }
            public Color TypeColor { get; }
            public DamageTypeDef(string Label, int AllyValue, Color TypeColor) { this.Label = Label; this.AllyValue = AllyValue; this.TypeColor = TypeColor; }
        }

        public static Dictionary<string, TextExportFormatter> ExportVariables = new Dictionary<string, TextExportFormatter>();
        public static Dictionary<string, ColumnDef> ColumnDefs = new Dictionary<string, ColumnDef>();
        public static Dictionary<string, DamageTypeDef> OutgoingDamageTypeDataObjects = new Dictionary<string, DamageTypeDef>();
        public static Dictionary<string, DamageTypeDef> IncomingDamageTypeDataObjects = new Dictionary<string, DamageTypeDef>();
        public static SortedDictionary<int, List<string>> SwingTypeToDamageTypeDataLinksOutgoing = new SortedDictionary<int, List<string>>();
        public static SortedDictionary<int, List<string>> SwingTypeToDamageTypeDataLinksIncoming = new SortedDictionary<int, List<string>>();
        public static List<int> DamageSwingTypes = new List<int> { 0, 2, 3 };
        public static List<int> HealingSwingTypes = new List<int> { 4, 5, 8, 9, 1 };

        // String keys the plugin's damage-type dictionaries use; set in CombatTables.Setup().
        public static string DamageTypeDataNonSkillDamage = "Auto-Attack (Out)";
        public static string DamageTypeDataOutgoingDamage = "Outgoing Damage";
        public static string DamageTypeDataOutgoingHealing = "Healed (Out)";
        public static string DamageTypeDataIncomingDamage = "Incoming Damage";
        public static string DamageTypeDataIncomingHealing = "Healed (Inc)";
        public static string DamageTypeDataOutgoingPowerReplenish = "Power Replenish (Out)";
        public static string DamageTypeDataOutgoingPowerDamage = "Power Drain (Out)";
        public static string DamageTypeDataOutgoingCures = "Cure/Dispel (Out)";

        private readonly Dictionary<string, DamageTypeData> items = new Dictionary<string, DamageTypeData>();
        public EncounterData Parent { get; }
        public string Name { get; }
        public SortedList<string, int> Allies { get; set; } = new SortedList<string, int>();
        public Dictionary<string, object> Tags { get; set; } = new Dictionary<string, object>();

        public CombatantData(string combatantName, EncounterData parent)
        {
            Name = combatantName; Parent = parent;
            foreach (var kv in OutgoingDamageTypeDataObjects) items[kv.Key] = new DamageTypeData(true, kv.Key, this);
            foreach (var kv in IncomingDamageTypeDataObjects) if (!items.ContainsKey(kv.Key)) items[kv.Key] = new DamageTypeData(false, kv.Key, this);
        }

        public Dictionary<string, DamageTypeData> Items { get => items; set { } }

        public void AddCombatAction(MasterSwing action)
        {
            var victim = action.Victim?.ToUpper() ?? "";
            if (SwingTypeToDamageTypeDataLinksOutgoing.TryGetValue(action.SwingType, out var links))
                foreach (var key in links)
                    if (items.TryGetValue(key, out var dtd))
                    {
                        ModAlly(victim, OutgoingDamageTypeDataObjects.TryGetValue(key, out var def) ? def.AllyValue : 0);
                        dtd.AddCombatAction(action, "All");
                        dtd.AddCombatAction(action, action.AttackType);
                    }
        }

        public void AddReverseCombatAction(MasterSwing action)
        {
            var attacker = action.Attacker?.ToUpper() ?? "";
            if (SwingTypeToDamageTypeDataLinksIncoming.TryGetValue(action.SwingType, out var links))
                foreach (var key in links)
                    if (items.TryGetValue(key, out var dtd))
                    {
                        ModAlly(attacker, IncomingDamageTypeDataObjects.TryGetValue(key, out var def) ? def.AllyValue : 0);
                        dtd.AddCombatAction(action, "All");
                        dtd.AddCombatAction(action, action.AttackType);
                    }
        }

        public void ModAlly(string combatant, int mod)
        {
            if (Name == "Unknown" || combatant == "UNKNOWN") return;
            if (!Allies.ContainsKey(combatant)) Allies.Add(combatant, 0);
            if (mod != 0) { Allies[combatant] += mod; Parent?.SetAlliesUncached(); }
        }

        private DamageTypeData D(string key) => items.TryGetValue(key, out var v) ? v : null;
        public long Damage => D(DamageTypeDataOutgoingDamage)?.Damage ?? 0L;
        public long Healed => D(DamageTypeDataOutgoingHealing)?.Damage ?? 0L;
        public long DamageTaken => D(DamageTypeDataIncomingDamage)?.Damage ?? 0L;
        public long HealsTaken => D(DamageTypeDataIncomingHealing)?.Damage ?? 0L;
        public long PowerReplenish => D(DamageTypeDataOutgoingPowerReplenish)?.Damage ?? 0L;
        public long PowerDamage => D(DamageTypeDataOutgoingPowerDamage)?.Damage ?? 0L;
        public int Swings => D(DamageTypeDataOutgoingDamage)?.Swings ?? 0;
        public int Hits => D(DamageTypeDataOutgoingDamage)?.Hits ?? 0;
        public int CritHits => D(DamageTypeDataOutgoingDamage)?.CritHits ?? 0;
        public float CritDamPerc => D(DamageTypeDataOutgoingDamage)?.CritPerc ?? 0f;
        public float CritHealPerc => D(DamageTypeDataOutgoingHealing)?.CritPerc ?? 0f;
        public int CritHeals => D(DamageTypeDataOutgoingHealing)?.CritHits ?? 0;
        public int Heals => D(DamageTypeDataOutgoingHealing)?.Swings ?? 0;
        public int CureDispels => D(DamageTypeDataOutgoingCures)?.Swings ?? 0;
        public int Misses => D(DamageTypeDataOutgoingDamage)?.Misses ?? 0;
        public int Blocked => 0;
        public float ToHit => Swings == 0 ? 0f : (float)Hits / Swings * 100f;
        public DateTime StartTime => D(DamageTypeDataOutgoingDamage)?.StartTime ?? DateTime.MaxValue;
        public DateTime EndTime => D(DamageTypeDataOutgoingDamage)?.EndTime ?? DateTime.MinValue;
        public DateTime ShortEndTime => EndTime;
        public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;
        public string DurationS => Duration.Hours == 0 ? $"{Duration.Minutes:00}:{Duration.Seconds:00}" : $"{Duration.Hours:00}:{Duration.Minutes:00}:{Duration.Seconds:00}";
        public double DPS => Duration.TotalSeconds > 0 ? (double)Damage / Duration.TotalSeconds : 0.0;
        public double EncDPS { get { var d = Parent?.Duration.TotalSeconds ?? 0; return d > 0 ? (double)Damage / d : 0.0; } }
        public double ExtDPS => EncDPS;
        public double EncHPS { get { var d = Parent?.Duration.TotalSeconds ?? 0; return d > 0 ? (double)Healed / d : 0.0; } }
        public double ExtHPS => EncHPS;
        public int Deaths { get { var at = GetAttackType("All", DamageTypeDataIncomingDamage); return at?.Items.Count(s => s.Damage == Dnum.Death) ?? 0; } }
        public int Kills { get { var at = GetAttackType("All", DamageTypeDataOutgoingDamage); return at?.Items.Count(s => s.Damage == Dnum.Death) ?? 0; } }
        public string DamagePercent { get { var t = Parent?.Damage ?? 0; return t > 0 ? (int)((float)Damage / t * 100f) + "%" : "--"; } }
        public string HealedPercent { get { var t = Parent?.Healed ?? 0; return t > 0 ? (int)((float)Healed / t * 100f) + "%" : "--"; } }
        public string MaxHit { get { var at = GetAttackType("All", DamageTypeDataOutgoingDamage); return (at?.MaxHit ?? 0).ToString(); } }

        public AttackType GetAttackType(string attackTypeName, string type) => items.TryGetValue(type, out var d) && d.Items.TryGetValue(attackTypeName, out var at) ? at : null;
        public string GetColumnByName(string name) => ColumnDefs.ContainsKey(name) ? ColumnDefs[name].GetCellData(this) : string.Empty;
        public string GetMaxHit(bool showType = true, bool useSuffix = true) => MaxHit;
        public string GetMaxHeal(bool showType = true, bool countWards = false, bool useSuffix = true) { var at = GetAttackType("All", DamageTypeDataOutgoingHealing); return (at?.MaxHit ?? 0).ToString(); }

        public int CompareTo(object obj) => CompareTo(obj as CombatantData);
        public int CompareTo(CombatantData other) => other == null ? 1 : other.Damage.CompareTo(Damage);
        public override bool Equals(object obj) => obj is CombatantData c && c.Name.ToLower() == Name.ToLower();
        public override int GetHashCode() => Name.ToLower().GetHashCode();
        public override string ToString() => Name;
    }

    public class ZoneData
    {
        public string ZoneName = "";
        public EncounterData ActiveEncounter;
        public List<EncounterData> Items = new List<EncounterData>();
        public ZoneData() { ActiveEncounter = new EncounterData(ActGlobals.charName, "", this); }
    }

    public class EncounterData
    {
        public delegate string ExportStringDataCallback(EncounterData Data, List<CombatantData> SelectiveAllies, string ExtraFormat);
        public delegate string StringDataCallback(EncounterData Data);
        public delegate Color ColorDataCallback(EncounterData Data);

        public class TextExportFormatter
        {
            public ExportStringDataCallback GetExportString;
            public string Label { get; }
            public string Description { get; }
            public string Name { get; }
            public TextExportFormatter(string Name, string Label, string Description, ExportStringDataCallback FormatterCallback)
            { this.Name = Name; this.Label = Label; this.Description = Description; GetExportString = FormatterCallback; }
        }

        public class ColumnDef
        {
            public StringDataCallback GetCellData;
            public StringDataCallback GetSqlData;
            public ColorDataCallback GetCellForeColor = _ => Color.Transparent;
            public ColorDataCallback GetCellBackColor = _ => Color.Transparent;
            public string SqlDataType { get; }
            public string SqlDataName { get; }
            public bool DefaultVisible { get; }
            public string Label { get; }
            public ColumnDef(string Label, bool DefaultVisible, string SqlDataType, string SqlDataName,
                StringDataCallback CellDataCallback, StringDataCallback SqlDataCallback)
            { this.Label = Label; this.DefaultVisible = DefaultVisible; this.SqlDataType = SqlDataType;
              this.SqlDataName = SqlDataName; GetCellData = CellDataCallback; GetSqlData = SqlDataCallback; }
        }

        public static Dictionary<string, TextExportFormatter> ExportVariables = new Dictionary<string, TextExportFormatter>();
        public static Dictionary<string, ColumnDef> ColumnDefs = new Dictionary<string, ColumnDef>();

        private readonly SortedList<string, CombatantData> combatants = new SortedList<string, CombatantData>();
        private readonly bool ignoreEnemies;

        public ZoneData Parent { get; set; }
        public string CharName { get; set; }
        public string ZoneName { get; set; }
        public bool Active { get; set; }
        public string Title { get; set; } = "Encounter";
        public List<DateTime> StartTimes { get; set; } = new List<DateTime>();
        public List<DateTime> EndTimes { get; set; } = new List<DateTime>();
        public List<object> LogLines { get; set; } = new List<object>();
        public Dictionary<string, object> Tags { get; set; } = new Dictionary<string, object>();
        public object HistoryRecord { get; set; }
        public bool DuplicateDetection { get; set; }

        public EncounterData(string charName, string zoneName, ZoneData parent) { CharName = charName; ZoneName = zoneName; Parent = parent; }
        public EncounterData(string charName, string zoneName, bool ignoreEnemies, ZoneData parent) { CharName = charName; ZoneName = zoneName; this.ignoreEnemies = ignoreEnemies; Parent = parent; }

        public SortedList<string, CombatantData> Items { get => combatants; set { } }

        public void AddCombatAction(MasterSwing action)
        {
            action.ParentEncounter = this;
            var atk = action.Attacker?.ToUpper() ?? "";
            if (!combatants.TryGetValue(atk, out var c)) { c = new CombatantData(action.Attacker, this); combatants.Add(atk, c); }
            c.AddCombatAction(action);
            var vic = action.Victim?.ToUpper() ?? "";
            if (!combatants.TryGetValue(vic, out var cv)) { cv = new CombatantData(action.Victim, this); combatants.Add(vic, cv); }
            cv.AddReverseCombatAction(action);
        }

        public void SetAlliesUncached() { }
        public List<CombatantData> GetAllies() => new List<CombatantData>(combatants.Values);
        public List<CombatantData> GetAllies(bool allowLimited) => GetAllies();
        public CombatantData GetCombatant(string name) => name != null && combatants.TryGetValue(name.ToUpper(), out var v) ? v : null;
        public bool GetIgnoreEnemies() => ignoreEnemies;

        public DateTime StartTime => combatants.Count == 0 ? DateTime.MaxValue : combatants.Values.Min(c => c.StartTime);
        public DateTime EndTime => combatants.Count == 0 ? DateTime.MinValue : combatants.Values.Max(c => c.EndTime);
        public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;
        public string DurationS => Duration.Hours == 0 ? $"{Duration.Minutes:00}:{Duration.Seconds:00}" : $"{Duration.Hours:00}:{Duration.Minutes:00}:{Duration.Seconds:00}";
        public long Damage => GetAllies().Sum(c => c.Damage);
        public long Healed => GetAllies().Sum(c => c.Healed);
        public int AlliedKills => GetAllies().Sum(c => c.Kills);
        public int AlliedDeaths => GetAllies().Sum(c => c.Deaths);
        public double DPS => Duration.TotalSeconds > 0 ? (double)Damage / Duration.TotalSeconds : 0.0;
        public int NumCombatants => combatants.Count;
        public int NumAllies => GetAllies().Count;
        public string EncId => GetHashCode().ToString("x8");
        public string GetColumnByName(string name) => ColumnDefs.ContainsKey(name) ? ColumnDefs[name].GetCellData(this) : string.Empty;

        public void EndCombat(bool finalize)
        {
            Active = false;
            if (StartTimes.Count > EndTimes.Count) EndTimes.Add(EndTime > DateTime.MinValue ? EndTime : DateTime.Now);
        }

        public string GetMaxHit(bool showType = true, bool useSuffix = true) => "";
        public string GetMaxHeal(bool showType = true, bool countWards = true, bool useSuffix = true) => "";
        public override string ToString() => $"{Title} - [{DurationS}]";
    }
}
