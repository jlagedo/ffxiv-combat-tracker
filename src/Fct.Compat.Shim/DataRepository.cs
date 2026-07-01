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

    public DateTime GetServerTimestamp() => _host.Clock.ServerNow.UtcDateTime;

    // The chat log rides the RawLogLine firehose, so it is always available to consumers.
    public bool IsChatLogAvailable() => true;

    // ACT's AV-detection diagnostic has no equivalent here.
    public string[] GetAntiVirusNames() => Array.Empty<string>();

    // No FFXIV game Process handle exists on the net10 side — the game connection lives in the net48
    // satellite. Consumers null-check this to mean "not attached" (e.g. OverlayPlugin's FFXIVRepository).
    public Process GetCurrentFFXIVProcess() => null!;

    // Name tables are not sourced yet (IResourceCatalog is a tracked open item); until then the modern
    // catalog is empty and this returns an empty dictionary.
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
