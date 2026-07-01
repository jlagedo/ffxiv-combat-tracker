using System;
using Fct.App.Hosting;

namespace Fct.App.ViewModels;

// One notification row — in a toast and in the notification centre. Pure projection of the
// immutable Notification record into display strings + a severity the theme maps to an accent.
public sealed class NotificationViewModel
{
    private readonly Notification _model;

    public NotificationViewModel(Notification model) => _model = model;

    public NotificationSeverity Severity => _model.Severity;
    public string Source => _model.Source;
    public string Title => _model.Title;
    public string? Message => _model.Message;
    public bool HasMessage => !string.IsNullOrWhiteSpace(_model.Message);

    public string TimeLabel => _model.Timestamp.ToLocalTime().ToString("HH:mm");

    // A small glyph per severity (check / info / warning / error), drawn in the accent colour.
    public string IconData => Severity switch
    {
        NotificationSeverity.Success => "M2 8 L6 12 L14 3",                       // check
        NotificationSeverity.Warning => "M8 1 L15 14 H1 Z M8 6 V10 M8 12 V12.5", // triangle + bang
        NotificationSeverity.Error   => "M3 3 L13 13 M13 3 L3 13",               // cross
        _ => "M8 2 A6 6 0 1 0 8.01 2 M8 6 V6.2 M8 8 V11",                         // info dot
    };
}
