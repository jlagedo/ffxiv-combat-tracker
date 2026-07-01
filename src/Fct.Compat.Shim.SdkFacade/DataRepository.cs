using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using FFXIV_ACT_Plugin.Common.Models;

namespace FFXIV_ACT_Plugin.Common
{
    /// <summary>The client's selected UI language.</summary>
    public enum Language
    {
        English = 1,
        French = 2,
        German = 3,
        Japanese = 4,
        Chinese = 5,
        Korean = 6,
        TraditionalChinese = 7,
    }

    /// <summary>Selector for <see cref="IDataRepository.GetResourceDictionary"/> name tables.</summary>
    public enum ResourceType
    {
        BuffList_EN = 0,
        BuffList_FR = 1,
        BuffList_DE = 2,
        BuffList_JP = 3,
        SkillList_EN = 4,
        SkillList_FR = 5,
        SkillList_DE = 6,
        SkillList_JP = 7,
        WorldList_EN = 8,
        ZoneList_EN = 9,
        TerritoryList_EN = 10,
        ItemList_EN = 11,
        MountList_EN = 12,
        BuffList_KR = 13,
        SkillList_KR = 14,
    }

    /// <summary>
    /// The plugin's pull-state surface (resource tables, combatant list, player, region). The shim
    /// projects it from the modern <c>IGameSnapshot</c> + <c>IResourceCatalog</c>.
    /// </summary>
    public interface IDataRepository
    {
        Language GetSelectedLanguageID();
        Process GetCurrentFFXIVProcess();
        IDictionary<uint, string> GetResourceDictionary(ResourceType resourceType);
        uint GetCurrentTerritoryID();
        uint GetCurrentPlayerID();
        ReadOnlyCollection<Combatant> GetCombatantList();
        Player GetPlayer();
        DateTime GetServerTimestamp();
        string GetGameVersion();
        bool IsChatLogAvailable();
        string[] GetAntiVirusNames();
        byte GetGameRegion();
    }
}
