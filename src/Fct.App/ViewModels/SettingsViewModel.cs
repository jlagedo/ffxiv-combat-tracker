using CommunityToolkit.Mvvm.ComponentModel;

namespace Fct.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(MainViewModel shell) : base(shell) { }

    public override Section Section => Section.Settings;
    public override string Eyebrow => "Settings";
    public override string Title => "Settings";
    public override string Subtitle =>
        "How the host launches and presents the two-process stack.";

    // Startup preferences. Bound state only for now — the launch/embed behaviour they describe
    // is unconditional in this build; wiring them to actual behaviour is a later slice.
    [ObservableProperty]
    private bool _launchSatelliteWithHost = true;

    [ObservableProperty]
    private bool _embedPluginTabs = true;
}
