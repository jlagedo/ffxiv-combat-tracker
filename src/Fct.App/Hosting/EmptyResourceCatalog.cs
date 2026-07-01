using System.Collections.Generic;
using Fct.Abstractions;

namespace Fct.App.Hosting;

/// <summary>
/// An empty id→name catalog used until <see cref="IResourceCatalog"/> is sourced (see PLUGIN-API open
/// items). Shared by the empty placeholder snapshot and the live <see cref="GameSnapshotAggregator"/>,
/// which carries no resource tables — the bridge forwards state, not name catalogs.
/// </summary>
internal sealed class EmptyResourceCatalog : IResourceCatalog
{
    public static readonly EmptyResourceCatalog Instance = new();
    private static readonly IReadOnlyDictionary<uint, string> Empty = new Dictionary<uint, string>();

    public string? Name(ResourceKind kind, uint id) => null;
    public IReadOnlyDictionary<uint, string> All(ResourceKind kind) => Empty;
}
