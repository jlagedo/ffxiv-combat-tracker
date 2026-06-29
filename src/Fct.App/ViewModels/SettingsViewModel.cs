namespace Fct.App.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(MainViewModel shell) : base(shell) { }

    public override Section Section => Section.Settings;
    public override string Eyebrow => "Settings";
    public override string Title => "Settings";
    public override string Subtitle =>
        "How the host launches and presents the two-process stack.";
}
