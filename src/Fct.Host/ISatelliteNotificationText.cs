namespace Fct.Host;

// Localized text for the notifications SatelliteHost surfaces. Implemented in Fct.App over the
// shell's resx so this runtime library takes no localization dependency; SatelliteHost falls back to
// the English defaults below when none is supplied (design-time / headless construction).
public interface ISatelliteNotificationText
{
    string SourceClassicEngine { get; }
    string SourceClassicPlugin { get; }
    string EngineStoppedTitle { get; }
    string EngineStoppedBody { get; }
    string PluginReportedErrorTitle { get; }
    string PluginLoadFailedTitle { get; }
    string EngineErrorTitle { get; }
    string EngineWarningTitle { get; }
}

// English fallbacks, matching the Fct.App resx values. Used when SatelliteHost is constructed without
// a localized provider — the same plain-literal fault posture PluginUiCoordinator's placeholders use.
internal sealed class DefaultSatelliteNotificationText : ISatelliteNotificationText
{
    public static readonly DefaultSatelliteNotificationText Instance = new();

    public string SourceClassicEngine => "Classic engine";
    public string SourceClassicPlugin => "Classic plugin";
    public string EngineStoppedTitle => "The classic engine stopped";
    public string EngineStoppedBody => "Classic plugins are no longer running. Restart it from the Plugins page.";
    public string PluginReportedErrorTitle => "A classic plugin reported an error";
    public string PluginLoadFailedTitle => "A classic plugin failed to load";
    public string EngineErrorTitle => "Classic engine error";
    public string EngineWarningTitle => "Classic engine warning";
}
