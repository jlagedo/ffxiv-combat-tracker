using Fct.App.Lang;
using Fct.Host;

namespace Fct.App;

// Binds the host runtime's satellite-notification text seam to the shell's localized resx, so the
// classic-engine notifications SatelliteHost raises follow the app's culture. Kept in Fct.App because
// the ResourceManager resolves against this assembly's embedded Resources.
internal sealed class SatelliteNotificationText : ISatelliteNotificationText
{
    public string SourceClassicEngine => Resources.Notify_Source_ClassicEngine;
    public string SourceClassicPlugin => Resources.Notify_Source_ClassicPlugin;
    public string EngineStoppedTitle => Resources.Notify_EngineStoppedTitle;
    public string EngineStoppedBody => Resources.Notify_EngineStoppedBody;
    public string PluginReportedErrorTitle => Resources.Notify_PluginReportedErrorTitle;
    public string PluginLoadFailedTitle => Resources.Notify_PluginLoadFailedTitle;
    public string EngineErrorTitle => Resources.Notify_EngineErrorTitle;
    public string EngineWarningTitle => Resources.Notify_EngineWarningTitle;
}
