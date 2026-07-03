using System.Runtime.CompilerServices;
using Advanced_Combat_Tracker;

// The real precompiled plugins (FFXIV_ACT_Plugin, OverlayPlugin, …) reference the ACT aggregation
// types by their original assembly-qualified identity ("<type>, Advanced Combat Tracker"). The engine
// now lives in Fct.Aggregation, so this facade — which carries the "Advanced Combat Tracker" identity
// — forwards each engine type there. The plugins' baked-in type references resolve transparently and
// every consumer sees one consistent type identity. Forwarding a declaring type also covers its nested
// types (AttackType.DamageTypeDef, EncounterData.ColumnDef, …), so only the top-level types are listed.
[assembly: TypeForwardedTo(typeof(AttackTypeTypeEnum))]
[assembly: TypeForwardedTo(typeof(AttackType))]
[assembly: TypeForwardedTo(typeof(DamageTypeData))]
[assembly: TypeForwardedTo(typeof(CombatantData))]
[assembly: TypeForwardedTo(typeof(ZoneData))]
[assembly: TypeForwardedTo(typeof(EncounterData))]
[assembly: TypeForwardedTo(typeof(Dnum))]
[assembly: TypeForwardedTo(typeof(MasterSwing))]
[assembly: TypeForwardedTo(typeof(CustomTrigger))]
[assembly: TypeForwardedTo(typeof(LogLineEntry))]
[assembly: TypeForwardedTo(typeof(TextExportFormatOptions))]
[assembly: TypeForwardedTo(typeof(CombatTables))]
[assembly: TypeForwardedTo(typeof(DamageString))]
