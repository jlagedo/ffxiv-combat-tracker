using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fct.App.Logging;

namespace Fct.App.ViewModels;

// Which levels the console shows. Thresholds against ConsoleLevel's order (Verbose..Fatal), so a
// filter passes everything at or above it.
public enum LogLevelFilter { All, Info, Warnings, Errors }

// The live log console: subscribes the host's single log stream (host + forwarded satellite records)
// and renders it as a scrolling, copyable, filterable feed. Incoming records land on the logging
// thread and are coalesced onto the UI thread by a timer so a firehose never thrashes the list.
public sealed partial class ConsoleViewModel : PageViewModel
{
    // The console retains more than the source stream's ring so scrolling back stays useful; still
    // bounded so memory is flat under load.
    private const int Cap = 5000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SearchDebounceInterval = TimeSpan.FromMilliseconds(200);

    private readonly ILogStream _stream;
    private readonly ConcurrentQueue<LogEntry> _incoming = new();
    private readonly Queue<LogEntryViewModel> _ring = new(Cap);   // every retained row, unfiltered
    private readonly DispatcherTimer _flush;
    // Coalesces per-keystroke search edits: each rebuild clears and re-projects the whole ring (up to
    // Cap individual Add events), so re-project once typing settles rather than on every character.
    private readonly DispatcherTimer _searchDebounce;

    // Bound to the list; the filtered projection of _ring in chronological order.
    public ObservableCollection<LogEntryViewModel> Entries { get; } = new();

    // The view scrolls to the tail on this; also drives clipboard writes (clipboard is a view concern).
    public event Action? TailAppended;
    public event Action<string>? CopyTextRequested;

    public ConsoleViewModel(MainViewModel shell, ILogStream stream) : base(shell)
    {
        _stream = stream;

        foreach (var e in stream.Snapshot())
        {
            _ring.Enqueue(new LogEntryViewModel(e));
            while (_ring.Count > Cap) _ring.Dequeue();
        }
        RebuildEntries();

        stream.Emitted += OnEmitted;
        _flush = new DispatcherTimer { Interval = FlushInterval };
        _flush.Tick += (_, _) => Drain();
        _flush.Start();

        _searchDebounce = new DispatcherTimer { Interval = SearchDebounceInterval };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RebuildEntries(); };
    }

    public override Section Section => Section.Console;
    public override string Eyebrow => Lang.Resources.Nav_Console;
    public override string Title => Lang.Resources.Nav_Console;
    public override string Subtitle => Lang.Resources.Console_Subtitle;
    public override bool ShowGenericHeader => false;   // owns its own compact chrome + toolbar

    // Follow-tail (Live) vs paused. The view flips this off when the user scrolls up to read.
    [ObservableProperty] private bool _followTail = true;

    [ObservableProperty] private LogLevelFilter _levelFilter = LogLevelFilter.All;

    [ObservableProperty] private string _searchText = "";

    public bool IsEmpty => Entries.Count == 0;

    partial void OnLevelFilterChanged(LogLevelFilter value) => RebuildEntries();

    // Restart the debounce on every keystroke; the ring is re-projected once edits pause (or immediately
    // when the box is cleared, since an empty filter is the cheap common case worth snapping back to).
    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        if (value.Length == 0) RebuildEntries();
        else _searchDebounce.Start();
    }

    partial void OnFollowTailChanged(bool value)
    {
        if (value) TailAppended?.Invoke();   // re-arming Live snaps back to the newest line
    }

    // Producer thread: never touch the collections here — just hand off to the UI-thread drain.
    private void OnEmitted(LogEntry entry) => _incoming.Enqueue(entry);

    // UI thread (timer): fold the burst in as appends + front trims so selection and scroll survive.
    private void Drain()
    {
        var addedToView = false;
        while (_incoming.TryDequeue(out var e))
        {
            var row = new LogEntryViewModel(e);
            _ring.Enqueue(row);
            if (_ring.Count > Cap)
            {
                var old = _ring.Dequeue();
                if (Entries.Count > 0 && ReferenceEquals(Entries[0], old)) Entries.RemoveAt(0);
            }
            if (Passes(row)) { Entries.Add(row); addedToView = true; }
        }
        if (addedToView)
        {
            OnPropertyChanged(nameof(IsEmpty));
            if (FollowTail) TailAppended?.Invoke();
        }
    }

    // Only on an explicit filter/search change — clears and re-projects the whole ring.
    private void RebuildEntries()
    {
        Entries.Clear();
        foreach (var r in _ring)
            if (Passes(r)) Entries.Add(r);
        OnPropertyChanged(nameof(IsEmpty));
        if (FollowTail) TailAppended?.Invoke();
    }

    private bool Passes(LogEntryViewModel row)
    {
        var threshold = LevelFilter switch
        {
            LogLevelFilter.Info => ConsoleLevel.Information,
            LogLevelFilter.Warnings => ConsoleLevel.Warning,
            LogLevelFilter.Errors => ConsoleLevel.Error,
            _ => ConsoleLevel.Verbose,
        };
        if (row.Level < threshold) return false;
        if (string.IsNullOrEmpty(SearchText)) return true;
        return row.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Source.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand] private void ToggleFollow() => FollowTail = !FollowTail;

    [RelayCommand] private void SetLevelFilter(string? name)
    {
        if (Enum.TryParse<LogLevelFilter>(name, out var f)) LevelFilter = f;
    }

    [RelayCommand]
    private void Clear()
    {
        _ring.Clear();
        Entries.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand] private void CopyAll() => Emit(Entries);

    // The list passes its SelectedItems; empty selection copies everything (a friendly default).
    [RelayCommand]
    private void CopySelected(IList? items)
    {
        if (items is null || items.Count == 0) { Emit(Entries); return; }
        var set = new HashSet<LogEntryViewModel>(items.Cast<LogEntryViewModel>());
        Emit(Entries.Where(set.Contains));   // keep chronological order regardless of click order
    }

    private void Emit(IEnumerable<LogEntryViewModel> rows)
    {
        var text = string.Join(Environment.NewLine, rows.Select(r => r.CopyText));
        if (text.Length > 0) CopyTextRequested?.Invoke(text);
    }

    // A stream pre-loaded with representative lines so the XAML previewer shows a populated console.
    internal static LogStream CreateDesignStream()
    {
        var s = new LogStream();
        var now = new DateTimeOffset(2026, 7, 7, 14, 22, 1, TimeSpan.Zero);
        s.Append(new LogEntry(now, ConsoleLevel.Information, "host", "FFXIV Combat Tracker host starting (pid 12480)", null));
        s.Append(new LogEntry(now, ConsoleLevel.Debug, "SatelliteRouter", "Spawning satellite for package ffxiv", null));
        s.Append(new LogEntry(now, ConsoleLevel.Information, "ffxiv", "Parser online · subscription attached", null));
        s.Append(new LogEntry(now, ConsoleLevel.Warning, "satellite", "Named pipe stalled; retrying (attempt 2)", null));
        s.Append(new LogEntry(now, ConsoleLevel.Error, "parser", "Dropped log line (seq 4471) — ring buffer full", null));
        s.Append(new LogEntry(now, ConsoleLevel.Information, "EncounterEngine", "Encounter started · The Navel (Hard)", null));
        return s;
    }
}
