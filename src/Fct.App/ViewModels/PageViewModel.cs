namespace Fct.App.ViewModels;

// Base for the shell's content pages. Each page owns its own header copy and binds the
// shared host/satellite state through Shell. The shell swaps CurrentPage; a DataTemplate
// maps the concrete page type to its view.
public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(MainViewModel shell) => Shell = shell;

    // The shell coordinator: shared plugin roster + satellite state the pages display.
    public MainViewModel Shell { get; }

    public abstract Section Section { get; }

    // Header copy shown in the shell's generic content header (eyebrow / title / subtitle).
    public abstract string Eyebrow { get; }
    public abstract string Title { get; }
    public abstract string Subtitle { get; }

    // Pages that supply their own compact chrome (Plugins) hide the shared header.
    public virtual bool ShowGenericHeader => true;
}
