using CommunityToolkit.Mvvm.ComponentModel;
using Fct.App.Hosting;
using Fct.Logging;

namespace Fct.App.ViewModels;

// App preferences. The startup toggle is persisted and honoured by the window; the rest is
// read-only environment info (log location, runtimes, version).
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly UiSettingsStore? _store;
    private bool _loading;

    public SettingsViewModel(MainViewModel shell, UiSettingsStore? store) : base(shell)
    {
        _store = store;
        _loading = true;
        _launchSatelliteOnStartup = store?.Load().LaunchSatelliteOnStartup ?? true;
        _loading = false;
    }

    public override Section Section => Section.Settings;
    public override string Eyebrow => "Settings";
    public override string Title => "Settings";
    public override string Subtitle => "How the app launches and where it keeps its files.";

    [ObservableProperty]
    private bool _launchSatelliteOnStartup;

    partial void OnLaunchSatelliteOnStartupChanged(bool value)
    {
        if (_loading || _store is null) return;
        var s = _store.Current;
        s.LaunchSatelliteOnStartup = value;
        _store.Save(s);
    }

    // Read-only environment info shown in the Diagnostics card.
    public string LogsDirectory => LogPaths.LogsDirectory;
}
