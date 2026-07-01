using System;
using System.Threading.Tasks;

namespace Fct.App.Plugins;

/// <summary>
/// The host→satellite plugin command surface the installer needs: load a real-legacy plugin, or tear
/// one down and await its ack. Implemented by <c>SatelliteHost</c>; abstracted so the installer (and
/// its headless tests) take no dependency on the Avalonia/satellite launch chain.
/// </summary>
internal interface ISatellitePluginChannel
{
    bool RequestLoadPlugin(string key, string dllPath, string title);
    Task<bool> RequestUnloadPluginAsync(string key, TimeSpan timeout);
}
