namespace Fct.Abstractions
{
    /// <summary>
    /// The FFXIV log-line type taxonomy carried by <see cref="RawLogLine"/>. Native types 0–43 +
    /// 249–254 are the FFXIV_ACT_Plugin set; 256–274 are OverlayPlugin's custom range. A recompiled
    /// Triggernometry/cactbot consumes <see cref="RawLogLine"/> and switches on this exactly as it
    /// switches on the legacy <c>detectedType</c> today.
    /// </summary>
    public enum LogMessageType
    {
        ChatLog = 0,
        Territory = 1,
        ChangePrimaryPlayer = 2,
        AddCombatant = 3,
        RemoveCombatant = 4,
        PartyList = 11,
        PlayerStats = 12,
        StartsCasting = 20,
        ActionEffect = 21,
        AOEActionEffect = 22,
        CancelAction = 23,
        DoTHoT = 24,
        Death = 25,
        StatusAdd = 26,
        TargetIcon = 27,
        WaymarkMarker = 28,
        SignMarker = 29,
        StatusRemove = 30,
        Gauge = 31,
        World = 32,
        Director = 33,
        NameToggle = 34,
        Tether = 35,
        LimitBreak = 36,
        EffectResult = 37,
        StatusList = 38,
        UpdateHp = 39,
        ChangeMap = 40,
        SystemLogMessage = 41,
        StatusList3 = 42,
        StatusListForay3 = 43,
        Settings = 249,
        Process = 250,
        Debug = 251,
        PacketDump = 252,
        Version = 253,
        Error = 254,

        // OverlayPlugin custom range (256+).
        RegisterLogLine = 256,
        MapEffect = 257,
        FateDirector = 258,
        CEDirector = 259,
        InCombat = 260,
        Combatant = 261,
        RSV = 262,
        ActorCastExtra = 263,
        AbilityExtra = 264,
        ContentFinderSettings = 265,
        NpcYell = 266,
        BattleTalk2 = 267,
        Countdown = 268,
        CountdownCancel = 269,
        ActorMove = 270,
        ActorSetPos = 271,
        SpawnNpcExtra = 272,
        ActorControlExtra = 273,
        ActorControlSelfExtra = 274,
    }
}
