using System;
using System.Threading.Tasks;

namespace Fct.Host.Plugins;

/// <summary>
/// The host→satellite plugin command surface the installer needs: load a real-legacy plugin, or tear
/// one down and await its ack. Implemented by <c>SatelliteRouter</c>, which resolves the plugin's
/// package (<see cref="PackageResolver"/>), ensures that package's satellite is launched, and forwards
/// the command to it. Abstracted so the installer (and its headless tests) take no dependency on the
/// Avalonia/satellite launch chain. Load is async because ensuring a satellite awaits a process handshake.
/// </summary>
internal interface ISatellitePluginChannel
{
    Task<bool> RequestLoadPluginAsync(string key, string dllPath, string title);
    Task<bool> RequestUnloadPluginAsync(string key, TimeSpan timeout);
}
