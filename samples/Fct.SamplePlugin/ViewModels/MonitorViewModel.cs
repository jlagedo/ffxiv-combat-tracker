using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Fct.SamplePlugin.ViewModels;

// The view model behind MonitorView.axaml. It holds only display state and raises change
// notifications — the plugin pushes fresh values into it (RefreshOnce), and the XAML binds to it.
//
// [ObservableProperty] generates a public property + INotifyPropertyChanged plumbing from each field,
// so `_zone` becomes `Zone`. [RelayCommand] turns a method into an ICommand a Button can bind to.
// Keep view models UI-only: no host services, no game data — the plugin owns those and feeds the VM.
public sealed partial class MonitorViewModel : ObservableObject
{
    // One-way, live: written by the plugin's 500 ms refresh loop, displayed by the view.
    [ObservableProperty] private string _zone = "—";
    [ObservableProperty] private bool _inCombat;
    [ObservableProperty] private long _actionsSeen;
    [ObservableProperty] private long _deaths;
    [ObservableProperty] private double _dps;

    // Two-way: the checkbox writes this straight back; OnAnnounceDeathsChanged notifies the plugin,
    // which persists it. This is the clean MVVM alternative to a code-behind event handler.
    [ObservableProperty] private bool _announceDeaths;

    private readonly Action<bool> _onAnnounceChanged;
    private readonly Action _onShowAlert;

    public MonitorViewModel(Action<bool> onAnnounceChanged, Action onShowAlert)
    {
        _onAnnounceChanged = onAnnounceChanged;
        _onShowAlert = onShowAlert;
    }

    // Generated partial hook — fires after AnnounceDeaths changes (from the two-way binding).
    partial void OnAnnounceDeathsChanged(bool value) => _onAnnounceChanged(value);

    // Generates ShowAlertCommand for Command="{Binding ShowAlertCommand}" in the XAML.
    [RelayCommand]
    private void ShowAlert() => _onShowAlert();
}
