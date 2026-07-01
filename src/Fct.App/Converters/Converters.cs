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
        // The satellite chip binds a plain string ("Online"/"Offline"/"Starting").
        if (value is string s)
            value = s switch
            {
                "Online" => PluginStatus.Running,
                "Offline" => PluginStatus.Unavailable,
                _ => PluginStatus.Loading,
            };

        var status = value as PluginStatus? ?? PluginStatus.Loaded;
        var key = status switch
        {
            PluginStatus.Live => "Ember",
            PluginStatus.Running => "Frost",
            PluginStatus.Loaded => "FrostDim",
            PluginStatus.Loading => "Hoarfrost",
            PluginStatus.Disabled => "Hoarfrost",
            PluginStatus.Preview => "Hoarfrost",
            PluginStatus.NotLoaded => "Hoarfrost",
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
