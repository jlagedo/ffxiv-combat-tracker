using System;
using Fct.Abstractions.UI;

namespace Fct.App.Plugins.Ui;

// The per-plugin IUiHost handle. Forwards every call to the shared coordinator tagged with this
// plugin's id, so the coordinator can attribute faults and settings pages back to their owner.
internal sealed class PluginUiHost : IUiHost
{
    private readonly string _pluginId;
    private readonly PluginUiCoordinator _coordinator;

    public PluginUiHost(string pluginId, PluginUiCoordinator coordinator)
    {
        _pluginId = pluginId;
        _coordinator = coordinator;
    }

    public void AddSettingsPage(UiSurface page) => _coordinator.AddSettingsPage(_pluginId, page);

    public void RevealPage(string pageId) => _coordinator.RevealPage(pageId);

    public IDisposable AddCornerControl(UiSurface control) => _coordinator.AddCornerControl(_pluginId, control);

    public void RemoveCornerControl(string id) => _coordinator.RemoveCornerControl(id);

    public IUiDispatcher Dispatcher => _coordinator.Dispatcher;
}
