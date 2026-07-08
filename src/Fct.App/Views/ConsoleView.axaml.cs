using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;   // SetTextAsync clipboard extension
using Avalonia.Interactivity;
using Avalonia.Threading;
using Fct.App.ViewModels;

namespace Fct.App.Views;

// The Console's autoscroll + clipboard glue. Both are view concerns: scrolling needs the realized
// ListBox, and the clipboard hangs off the TopLevel. The view model raises TailAppended /
// CopyTextRequested; this code-behind carries them out.
public partial class ConsoleView : UserControl
{
    private ConsoleViewModel? _vm;

    public ConsoleView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Scroll changes bubble from the ListBox's inner ScrollViewer; a manual scroll-up pauses follow.
        AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged, RoutingStrategies.Bubble);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.TailAppended -= OnTailAppended;
            _vm.CopyTextRequested -= OnCopyText;
        }
        _vm = DataContext as ConsoleViewModel;
        if (_vm is not null)
        {
            _vm.TailAppended += OnTailAppended;
            _vm.CopyTextRequested += OnCopyText;
            ScrollToTail();   // the seed batch was appended before we subscribed
        }
    }

    private void OnTailAppended() => ScrollToTail();

    private void ScrollToTail()
    {
        if (_vm is null || _vm.Entries.Count == 0) return;
        // Defer so the freshly-added item is realized before we scroll to it.
        Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(_vm.Entries.Count - 1), DispatcherPriority.Background);
    }

    // A downward scroll here is always our own tail-follow; only an upward move is the user choosing
    // to read back, which disarms follow-tail (re-armed via the Live button).
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_vm is not null && e.OffsetDelta.Y < -1) _vm.FollowTail = false;
    }

    private async void OnCopyText(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }
}
