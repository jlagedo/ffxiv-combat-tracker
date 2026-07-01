using Fct.App.Lang;

namespace Fct.App.ViewModels;

// The Plugins page: two grouped rosters (legacy · satellite, modern · host) on the left, and a
// detail/config bay on the right for the selected plugin. Legacy plugins embed their own WinForms
// configuration window; modern plugins show their manifest details. Supplies its own compact
// chrome, so it hides the shell's generic header.
public sealed class PluginsViewModel : PageViewModel
{
    public PluginsViewModel(MainViewModel shell) : base(shell) { }

    public override Section Section => Section.Plugins;
    public override string Eyebrow => Resources.Nav_Plugins;
    public override string Title => Resources.Nav_Plugins;
    public override string Subtitle => Resources.Plugins_Subtitle;
    public override bool ShowGenericHeader => false;
}
