using Fct.App.Lang;

namespace Fct.App.ViewModels;

// The landing page: connection state, plugins at a glance, and recent activity. Reads everything
// from the shell; owns no state of its own.
public sealed class OverviewViewModel : PageViewModel
{
    public OverviewViewModel(MainViewModel shell) : base(shell) { }

    public override Section Section => Section.Overview;
    public override string Eyebrow => Resources.Nav_Overview;
    public override string Title => Resources.Nav_Overview;
    public override string Subtitle => Resources.Overview_Subtitle;
}
