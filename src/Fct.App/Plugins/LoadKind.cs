namespace Fct.App.Plugins;

/// <summary>
/// How a plugin must be hosted, decided once at install by <see cref="PluginClassifier"/> and then
/// recorded in the persisted registry so startup never has to re-classify.
/// </summary>
internal enum LoadKind
{
    /// <summary>A modern <c>Fct.Abstractions.IPlugin</c> — loads in-process in its own ALC.</summary>
    Native,

    /// <summary>A legacy plugin recompiled against the compat shim (<c>IActPluginV1</c> on net10) —
    /// loads in-process via <c>LegacyPluginHost</c>.</summary>
    RecompiledShim,

    /// <summary>A real, unmodified net48 legacy plugin — cannot run in net10; routed to the satellite.</summary>
    RealLegacy,
}
