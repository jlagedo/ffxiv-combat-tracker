namespace Fct.App.ViewModels;

public sealed class OverlaysViewModel : PageViewModel
{
    public OverlaysViewModel(MainViewModel shell) : base(shell) { }

    public override Section Section => Section.Overlays;
    public override string Eyebrow => "Overlays";
    public override string Title => "Overlays";
    public override string Subtitle =>
        "Web overlays rendered for the browser layer — DPS meters, timelines, and raid tools.";
}
