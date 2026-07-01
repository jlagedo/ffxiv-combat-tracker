namespace Fct.App.ViewModels;

// Design-time only view models for the XAML previewer. Referenced from views via
// d:DataContext="{x:Static vm:DesignData.*}", which the runtime XAML loader strips (mc:Ignorable="d"),
// so this is never constructed in the running app. A single shared shell backs every page so the
// previewer shows a populated, self-consistent roster.
internal static class DesignData
{
    public static MainViewModel Shell { get; } = new();

    public static DashboardViewModel Dashboard => Shell.DashboardPage;
    public static PluginsViewModel Plugins => Shell.PluginsPage;
    public static OverlaysViewModel Overlays => Shell.OverlaysPage;
    public static SettingsViewModel Settings => Shell.SettingsPage;
}
