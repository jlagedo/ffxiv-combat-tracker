namespace Fct.App.ViewModels;

// The landing page: connection state, plugins at a glance, and recent activity. Reads everything
// from the shell; owns no state of its own.
public sealed class OverviewViewModel : PageViewModel
{
    public OverviewViewModel(MainViewModel shell) : base(shell) { }

    public override Section Section => Section.Overview;
    public override string Eyebrow => "Overview";
    public override string Title => "Overview";
    public override string Subtitle =>
        "The state of the host at a glance — connection, the plugins it carries, and what's happened lately.";
}
