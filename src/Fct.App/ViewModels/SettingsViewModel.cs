using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Fct.Host.Hosting;
using Fct.App.Lang;
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
    public override string Eyebrow => Resources.Nav_Settings;
    public override string Title => Resources.Nav_Settings;
    public override string Subtitle => Resources.Settings_Subtitle;

    [ObservableProperty]
    private bool _launchSatelliteOnStartup;

    partial void OnLaunchSatelliteOnStartupChanged(bool value)
    {
        if (_loading || _store is null) return;
        var s = _store.Current;
        s.LaunchSatelliteOnStartup = value;
        // Best-effort disk write off the UI thread — the toggle shouldn't wait on the file system
        // (an AV scanner or slow disk could stall it). Save swallows its own failures.
        _ = Task.Run(() => _store.Save(s));
    }

    // Read-only environment info shown in the Diagnostics card.
    public string LogsDirectory => LogPaths.LogsDirectory;
}
