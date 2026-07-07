using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Fct.Abstractions;
using FFXIV_ACT_Plugin.Common;
using SdkModels = FFXIV_ACT_Plugin.Common.Models;

namespace Fct.Compat.Shim;

/// <summary>
/// The FFXIV SDK pull-state surface (<see cref="IDataRepository"/>), projected over the modern
/// <see cref="IPluginHost"/>. Every call reads a fresh free-threaded <see cref="IGameSnapshot"/>, so a
/// recompiled OverlayPlugin/Hojoring polling <c>GetCombatantList()</c>/<c>GetPlayer()</c>/zone/region
/// sees live state exactly as under real ACT. Discovered by reflection off
/// <see cref="SyntheticFfxivPlugin"/>.<c>DataRepository</c>.
/// </summary>
public sealed class DataRepository : IDataRepository
{
    private readonly IPluginHost _host;

    public DataRepository(IPluginHost host)
        => _host = host ?? throw new ArgumentNullException(nameof(host));

    private IGameSnapshot Snapshot() => _host.Game.Snapshot();

    public ReadOnlyCollection<SdkModels.Combatant> GetCombatantList()
        => new ReadOnlyCollection<SdkModels.Combatant>(
            Snapshot().Actors.Select(CombatantProjector.ToCombatant).ToList());

    public SdkModels.Player GetPlayer() => CombatantProjector.ToPlayer(Snapshot().Player);

    public uint GetCurrentPlayerID() => Snapshot().Player?.Id ?? 0u;

    public uint GetCurrentTerritoryID() => Snapshot().Zone.Id;

    public Language GetSelectedLanguageID() => MapLanguage(Snapshot().Client.Language);

    public byte GetGameRegion() => (byte)Snapshot().Client.Region;

    public string GetGameVersion() => Snapshot().Client.Version;

    // Offset-corrected server-clock approximation (PIPELINE-COMPLETENESS-PLAN §3/§7): the real
    // plugin's GetServerTimestamp() is only ever populated by a live memory scan (DateTime.MinValue
    // headless, P0.3), so the consumer instead serves a usable clock for custom-line timestamps — the
    // host clock plus the parser's forwarded ServerClockOffset (Zero when the parser has no live
    // server time either).
    public DateTime GetServerTimestamp() => (_host.Clock.ServerNow + Snapshot().Client.ServerClockOffset).UtcDateTime;

    // Forwarded from the parser's env tap (G4/G5) — never a satellite-local re-derivation.
    public bool IsChatLogAvailable() => Snapshot().Client.IsChatLogAvailable;

    // ACT's AV-detection diagnostic has no equivalent here.
    public string[] GetAntiVirusNames() => Array.Empty<string>();

    // The game connection lives in the parser satellite; its PID is forwarded onto the snapshot so a
    // consumer materializes a live Process handle by id. Null when no PID is reported or the process is
    // gone — consumers null-check this to mean "not attached" (e.g. OverlayPlugin's FFXIVRepository).
    public Process GetCurrentFFXIVProcess()
    {
        var pid = Snapshot().Client.ProcessId;
        if (pid is null or 0) return null!;
        try { return Process.GetProcessById(pid.Value); }
        catch (ArgumentException) { return null!; }   // no such process
    }

    // Name tables are sourced from the parser satellite's forwarded resource dictionaries (folded into
    // the snapshot's IResourceCatalog); an unforwarded kind resolves to an empty dictionary.
    public IDictionary<uint, string> GetResourceDictionary(ResourceType resourceType)
    {
        var kind = MapResource(resourceType);
        if (kind is null) return new Dictionary<uint, string>();
        return new Dictionary<uint, string>(Snapshot().Resources.All(kind.Value));
    }

    private static Language MapLanguage(GameLanguage l) => l switch
    {
        GameLanguage.English => Language.English,
        GameLanguage.French => Language.French,
        GameLanguage.German => Language.German,
        GameLanguage.Japanese => Language.Japanese,
        GameLanguage.Chinese => Language.Chinese,
        GameLanguage.Korean => Language.Korean,
        GameLanguage.TraditionalChinese => Language.TraditionalChinese,
        _ => Language.English,
    };

    // ResourceType is locale-tagged; the modern catalog is locale-neutral, so the suffix is dropped.
    // MountList has no ResourceKind equivalent → null (empty dictionary).
    private static ResourceKind? MapResource(ResourceType t) => t switch
    {
        ResourceType.BuffList_EN or ResourceType.BuffList_FR or ResourceType.BuffList_DE
            or ResourceType.BuffList_JP or ResourceType.BuffList_KR => ResourceKind.Status,
        ResourceType.SkillList_EN or ResourceType.SkillList_FR or ResourceType.SkillList_DE
            or ResourceType.SkillList_JP or ResourceType.SkillList_KR => ResourceKind.Action,
        ResourceType.WorldList_EN => ResourceKind.World,
        ResourceType.ZoneList_EN or ResourceType.TerritoryList_EN => ResourceKind.Zone,
        ResourceType.ItemList_EN => ResourceKind.Item,
        _ => null,
    };
}
