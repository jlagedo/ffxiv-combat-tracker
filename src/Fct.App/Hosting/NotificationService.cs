using System;
using System.Collections.Generic;
using System.Linq;

namespace Fct.App.Hosting;

/// <summary>How prominent a notification is, and which accent it wears.</summary>
public enum NotificationSeverity { Success, Info, Warning, Error }

/// <summary>
/// A single user-facing message. Produced by the host (satellite lifecycle, forwarded legacy
/// records — including ACT's <c>NotificationAdd</c> — and native plugin load/fault events) and
/// consumed by the shell's toasts + notification centre.
/// </summary>
public sealed record Notification(
    NotificationSeverity Severity,
    string Source,
    string Title,
    string? Message,
    DateTimeOffset Timestamp);

/// <summary>
/// The shell-facing surface for user messages. One hub the whole app publishes to and the
/// notification centre subscribes to — the public seam over the internal <see cref="NotificationService"/>.
/// </summary>
public interface INotificationHub
{
    /// <summary>Raised on the publishing thread for every new notification (marshal to the UI thread).</summary>
    event Action<Notification>? Published;

    /// <summary>Publish a message. <paramref name="source"/> names who it's about ("Satellite", a plugin id).</summary>
    void Publish(NotificationSeverity severity, string source, string title, string? message = null);

    /// <summary>Newest-first copy of the retained history (bounded).</summary>
    IReadOnlyList<Notification> Snapshot();
}

/// <summary>
/// Collects notifications from every host source into one bounded, newest-first history and fans
/// each one out to subscribers. The translation of raw log records into notable notifications lives
/// with the producers (<see cref="SatelliteHost"/>, <see cref="Plugins.PluginManager"/>); this hub is
/// the neutral collection point.
/// </summary>
internal sealed class NotificationService : INotificationHub
{
    private const int MaxHistory = 200;
    private readonly object _gate = new();
    private readonly LinkedList<Notification> _history = new();

    public event Action<Notification>? Published;

    public void Publish(NotificationSeverity severity, string source, string title, string? message = null)
    {
        var n = new Notification(severity, source, title, string.IsNullOrWhiteSpace(message) ? null : message,
            DateTimeOffset.Now);
        lock (_gate)
        {
            _history.AddFirst(n);
            while (_history.Count > MaxHistory) _history.RemoveLast();
        }
        Published?.Invoke(n);
    }

    public IReadOnlyList<Notification> Snapshot()
    {
        lock (_gate) return _history.ToArray();
    }
}
