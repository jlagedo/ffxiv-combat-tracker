using System;
using System.IO;

namespace Fct.App.ViewModels;

// The Plugin Setup page: switches between the embedded-config bay ("Configure") and the
// add/remove/reorder rack ("Manage"). Roster mutations operate on the shell's shared
// Plugins collection and selection. Supplies its own compact chrome, so it hides the
// shell's generic header.
public sealed class PluginsViewModel : PageViewModel
{
    public PluginsViewModel(MainViewModel shell) : base(shell)
    {
        ManageCommand = new RelayCommand(() => PluginsMode = "Manage");
        ConfigureCommand = new RelayCommand(() => PluginsMode = "Configure");
        AddPluginCommand = new RelayCommand(() => AddPluginRequested?.Invoke());
        RemovePluginCommand = new RelayCommand(p => RemovePlugin(p as PluginViewModel));
        MoveUpCommand = new RelayCommand(p => Move(p as PluginViewModel, -1));
        MoveDownCommand = new RelayCommand(p => Move(p as PluginViewModel, +1));
        RetryCommand = new RelayCommand(() => RetryRequested?.Invoke());
    }

    public override Section Section => Section.Plugins;
    public override string Eyebrow => "Plugins";
    public override string Title => "Plugin Setup";
    public override string Subtitle =>
        "Load the legacy ecosystem unmodified, then configure each plugin's native tabs in place.";
    public override bool ShowGenericHeader => false;

    public RelayCommand ManageCommand { get; }
    public RelayCommand ConfigureCommand { get; }
    public RelayCommand AddPluginCommand { get; }
    public RelayCommand RemovePluginCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand RetryCommand { get; }

    // Raised when the user asks to relaunch the host after a failed start; the window owns
    // the satellite lifecycle and wires this up.
    public event Action? RetryRequested;

    // Raised when the user clicks "Add plugin"; the window owns the file picker.
    public event Action? AddPluginRequested;

    // The page has two surfaces in the same space: "Configure" (the embedded plugin tabs)
    // and "Manage" (add / remove / enable / reorder the roster).
    private string _pluginsMode = "Configure";
    public string PluginsMode
    {
        get => _pluginsMode;
        set { if (SetField(ref _pluginsMode, value)) { Raise(nameof(IsConfigure)); Raise(nameof(IsManage)); } }
    }

    public bool IsConfigure => _pluginsMode == "Configure";
    public bool IsManage => _pluginsMode == "Manage";

    // Reorder the roster in place (the channel rail and rack both reflect the order).
    private void Move(PluginViewModel? p, int dir)
    {
        if (p is null) return;
        var i = Shell.Plugins.IndexOf(p);
        var j = i + dir;
        if (i < 0 || j < 0 || j >= Shell.Plugins.Count) return;
        Shell.Plugins.Move(i, j);
    }

    // Remove an added/loaded plugin; keep a sensible selection on whatever remains.
    private void RemovePlugin(PluginViewModel? p)
    {
        if (p is null) return;
        var i = Shell.Plugins.IndexOf(p);
        if (i < 0) return;
        Shell.Plugins.Remove(p);
        if (ReferenceEquals(Shell.SelectedPlugin, p))
            Shell.SelectedPlugin = Shell.Plugins.Count > 0 ? Shell.Plugins[Math.Min(i, Shell.Plugins.Count - 1)] : null;
        Shell.RaiseRosterChanged();
    }

    // Add a plugin by file. The satellite owns plugin loading, so a freshly added plugin
    // reads as "Not loaded" until the host relaunches and hosts it.
    public void AddPlugin(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var vm = new PluginViewModel
        {
            Name = name,
            Role = "Added · hosted on next launch",
            Version = "—",
            Kind = PluginKind.Legacy,
            FilePath = path,
            BaseStatus = PluginStatus.NotLoaded,
            Description =
                $"Added from {path}. The .NET 4.8 satellite loads plugins when the host starts, " +
                "so this plugin's configuration tabs appear here after the next relaunch.",
        };
        Shell.Plugins.Add(vm);
        Shell.SelectedPlugin = vm;
        PluginsMode = "Manage";
        Shell.RaiseRosterChanged();
    }
}
