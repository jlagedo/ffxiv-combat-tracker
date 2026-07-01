using Fct.Abstractions.UI;

namespace Fct.App.ViewModels;

// One transient corner control a plugin contributed via IUiHost.AddCornerControl (Triggernometry's
// CornerControlAdd). PluginId is carried for diagnostics/removal bookkeeping in the shell.
public sealed class CornerControlViewModel
{
    public required string PluginId { get; init; }
    public required UiSurface Surface { get; init; }
}
