using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fct.Host.Hosting;

namespace Fct.App.ViewModels;

// Owns the shell's notification surfaces: transient toasts (auto-dismiss for info/success, sticky
// for warnings/errors) and the persistent centre (bell → drawer with full history). Subscribes to
// the host's notification hub and marshals every message onto the UI thread.
public sealed partial class NotificationCenterViewModel : ObservableObject
{
    private const int MaxToasts = 4;
    private static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(6);

    private readonly INotificationHub? _hub;
    private readonly Dictionary<NotificationViewModel, DispatcherTimer> _timers = new();

    // Newest-first, so the centre and the recent-activity feed read top-down.
    public ObservableCollection<NotificationViewModel> History { get; } = new();
    public ObservableCollection<NotificationViewModel> Toasts { get; } = new();

    public NotificationCenterViewModel() { }   // design-time

    public NotificationCenterViewModel(INotificationHub hub)
    {
        _hub = hub;
        foreach (var n in hub.Snapshot())
            History.Add(new NotificationViewModel(n));   // Snapshot is already newest-first
        UpdateHasHistory();
        hub.Published += OnPublished;
    }

    // The hub fires on the producer thread; hop to the UI thread before touching the collections.
    private void OnPublished(Notification n)
    {
        if (Dispatcher.UIThread.CheckAccess()) Add(n);
        else Dispatcher.UIThread.Post(() => Add(n));
    }

    private void Add(Notification model)
    {
        var vm = new NotificationViewModel(model);
        History.Insert(0, vm);
        UpdateHasHistory();
        if (!IsOpen) UnreadCount++;

        Toasts.Insert(0, vm);
        while (Toasts.Count > MaxToasts) RemoveToast(Toasts[^1]);

        // Info/success self-dismiss; warnings and errors wait for the user to acknowledge them.
        if (model.Severity is NotificationSeverity.Success or NotificationSeverity.Info)
        {
            var timer = new DispatcherTimer { Interval = ToastLifetime };
            timer.Tick += (_, _) => RemoveToast(vm);
            _timers[vm] = timer;
            timer.Start();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    public bool HasUnread => UnreadCount > 0;

    [ObservableProperty]
    private bool _isOpen;

    public bool HasHistory => History.Count > 0;

    private void UpdateHasHistory() => OnPropertyChanged(nameof(HasHistory));

    [RelayCommand]
    private void ToggleCenter()
    {
        IsOpen = !IsOpen;
        if (IsOpen) UnreadCount = 0;
    }

    [RelayCommand]
    private void CloseCenter() => IsOpen = false;

    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        UpdateHasHistory();
        UnreadCount = 0;
    }

    [RelayCommand]
    private void DismissToast(NotificationViewModel? toast)
    {
        if (toast is not null) RemoveToast(toast);
    }

    private void RemoveToast(NotificationViewModel toast)
    {
        Toasts.Remove(toast);
        if (_timers.Remove(toast, out var timer)) timer.Stop();
    }
}
