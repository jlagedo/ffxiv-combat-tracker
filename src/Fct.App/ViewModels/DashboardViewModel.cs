namespace Fct.App.ViewModels;

// At-a-glance host state. Reads runtime / satellite / roster figures from the shell.
public sealed class DashboardViewModel : PageViewModel
{
    public DashboardViewModel(MainViewModel shell) : base(shell) { }

    public override Section Section => Section.Dashboard;
    public override string Eyebrow => "Dashboard";
    public override string Title => "Dashboard";
    public override string Subtitle =>
        "The state of the host at a glance — runtime, satellite, and the plugins it carries.";
}
