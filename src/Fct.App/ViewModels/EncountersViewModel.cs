using System;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Fct.Abstractions;

namespace Fct.App.ViewModels;

// The live combat meter. Binds the host's encounter service — which lights up once the
// net48→net10 data bridge forwards combat data. Until then it shows an honest "waiting" state
// rather than placeholder numbers.
public sealed partial class EncountersViewModel : PageViewModel
{
    private readonly IEncounterService? _encounters;
    private readonly DispatcherTimer? _timer;

    public EncountersViewModel(MainViewModel shell, IEncounterService? encounters) : base(shell)
    {
        _encounters = encounters;
        if (_encounters is not null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
        }
    }

    public override Section Section => Section.Encounters;
    public override string Eyebrow => "Encounters";
    public override string Title => "Encounters";
    public override string Subtitle =>
        "Live DPS and per-combatant metrics for the current fight, aggregated by the host engine.";

    public ObservableCollection<EncounterCombatantViewModel> Combatants { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData))]
    private bool _hasEncounter;

    public bool HasData => HasEncounter;

    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _encounterTitle = "";
    [ObservableProperty] private string _zone = "";
    [ObservableProperty] private string _durationLabel = "00:00";
    [ObservableProperty] private string _dpsLabel = "0";
    [ObservableProperty] private string _damageLabel = "0";
    [ObservableProperty] private string _sourceLabel = "";

    private void Refresh()
    {
        var snap = _encounters?.Active ?? _encounters?.Last;
        if (snap is null)
        {
            if (HasEncounter) { HasEncounter = false; Combatants.Clear(); }
            return;
        }

        HasEncounter = true;
        IsLive = snap.Active;
        SourceLabel = snap.Active ? "Live" : "Last fight";
        EncounterTitle = string.IsNullOrWhiteSpace(snap.Title) ? "Encounter" : snap.Title;
        Zone = snap.Zone ?? "";
        DurationLabel = snap.Duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        DpsLabel = Compact(snap.Dps);
        DamageLabel = Compact(snap.Damage);

        SyncCombatants(snap);
    }

    // Rebuild only when the roster shape changes; otherwise update the existing rows in place so
    // the list doesn't flicker on every tick.
    private void SyncCombatants(EncounterSnapshot snap)
    {
        var list = snap.Combatants;
        if (Combatants.Count != list.Count)
        {
            Combatants.Clear();
            for (var i = 0; i < list.Count; i++)
                Combatants.Add(new EncounterCombatantViewModel(i + 1, list[i]));
            return;
        }
        for (var i = 0; i < list.Count; i++)
            Combatants[i].Update(i + 1, list[i]);
    }

    internal static string Compact(double value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("0", CultureInfo.InvariantCulture),
    };
}

// One combatant row in the meter. Mutable so the encounter timer can update it in place.
public sealed partial class EncounterCombatantViewModel : ObservableObject
{
    public EncounterCombatantViewModel(int rank, CombatantMetrics m) => Update(rank, m);

    [ObservableProperty] private int _rank;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _jobLabel = "";
    [ObservableProperty] private string _dpsLabel = "0";
    [ObservableProperty] private string _damagePercentLabel = "0%";
    [ObservableProperty] private double _damageFraction;
    [ObservableProperty] private string _hpsLabel = "0";
    [ObservableProperty] private int _deaths;

    public void Update(int rank, CombatantMetrics m)
    {
        Rank = rank;
        Name = m.Name;
        JobLabel = m.Job > 0 ? "Job " + m.Job.ToString(CultureInfo.InvariantCulture) : "—";
        DpsLabel = EncountersViewModel.Compact(m.EncDps);
        DamagePercentLabel = m.DamagePercent.ToString("0", CultureInfo.InvariantCulture) + "%";
        DamageFraction = Math.Clamp(m.DamagePercent / 100.0, 0, 1);
        HpsLabel = m.Healing > 0 ? EncountersViewModel.Compact(m.Healing) : "—";
        Deaths = m.Deaths;
    }
}
