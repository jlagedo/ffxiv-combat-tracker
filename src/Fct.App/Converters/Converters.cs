using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Fct.App.ViewModels;

namespace Fct.App.Converters;

// Maps a PluginStatus to a themed brush. ConverterParameter selects the role:
//   "fill" (orb core), "glow" (soft halo), "text"/"pill" (label colour).
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as PluginStatus? ?? PluginStatus.Loaded;
        var key = status switch
        {
            PluginStatus.Live => "Ember",
            PluginStatus.Running => "Frost",
            PluginStatus.Loaded => "FrostDim",
            PluginStatus.Loading => "Hoarfrost",
            PluginStatus.NotLoaded => "Hoarfrost",
            PluginStatus.Unavailable => "Warn",
            _ => "Warn",
        };

        return (parameter as string) == "glow" ? Glow(key) : Brush(key + "Brush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static IBrush Brush(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is IBrush b
            ? b : Brushes.Gray;

    // A translucent halo of the status colour. Derived once per status key and reused — the item
    // templates re-evaluate this binding on every roster/status change, so caching avoids a fresh
    // SolidColorBrush allocation each pass. UI-thread only, so a plain dictionary is safe.
    private static readonly Dictionary<string, IBrush> GlowCache = new();

    private static IBrush Glow(string key)
    {
        if (GlowCache.TryGetValue(key, out var cached)) return cached;
        var glow = Brush(key + "Brush") is ISolidColorBrush scb
            ? new SolidColorBrush(Color.FromArgb(0x33, scb.Color.R, scb.Color.G, scb.Color.B))
            : Brush(key + "Brush");
        GlowCache[key] = glow;
        return glow;
    }
}

// Maps a NotificationSeverity to a themed accent brush (with a "glow" halo variant), so toasts and
// notification rows wear the palette's success/info/warn/error colours.
public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, IBrush> GlowCache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as Fct.Host.Hosting.NotificationSeverity?) switch
        {
            Fct.Host.Hosting.NotificationSeverity.Success => "Frost",
            Fct.Host.Hosting.NotificationSeverity.Warning => "Ember",
            Fct.Host.Hosting.NotificationSeverity.Error => "Warn",
            _ => "FrostDim",
        };
        return (parameter as string) == "glow" ? Glow(key) : Brush(key + "Brush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static IBrush Brush(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is IBrush b
            ? b : Brushes.Gray;

    private static IBrush Glow(string key)
    {
        if (GlowCache.TryGetValue(key, out var cached)) return cached;
        var glow = Brush(key + "Brush") is ISolidColorBrush scb
            ? new SolidColorBrush(Color.FromArgb(0x2E, scb.Color.R, scb.Color.G, scb.Color.B))
            : Brush(key + "Brush");
        GlowCache[key] = glow;
        return glow;
    }
}

// True when the bound enum value's name equals the ConverterParameter — drives the nav
// rail's active styling against MainViewModel.CurrentPage.Section.
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}
