using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Fct.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fct.App.Views;

public partial class PluginsView : UserControl
{
    // One embedded view per plugin window; the config bay shows whichever plugin is selected.
    private readonly Dictionary<IntPtr, EmbeddedSatelliteView> _embeds = new();
    private readonly ILoggerFactory _loggerFactory;

    private MainViewModel? _shell;

    public PluginsView()
    {
        InitializeComponent();
        _loggerFactory = App.Services?.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
    }

    // The config bay reacts to the plugin selection and embed readiness (both on the shell).
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell is not null) _shell.PropertyChanged -= OnShellChanged;
        _shell = (DataContext as PluginsViewModel)?.Shell;
        if (_shell is not null) _shell.PropertyChanged += OnShellChanged;
        UpdateEmbed();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateEmbed();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Detach the foreign HWND while this page is off-screen so it doesn't paint as a stray
        // top-level window; it re-embeds when the page comes back.
        EmbedSlot.Child = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedPlugin) or nameof(MainViewModel.ConfigReady))
            UpdateEmbed();
    }

    // Show the selected legacy plugin's own configuration window in the bay, but only with the
    // satellite up. The embedded HWND paints over Avalonia content, so detach it whenever the
    // selection has no window or the host isn't ready.
    private void UpdateEmbed()
    {
        if (!this.IsAttachedToVisualTree())
            return;

        var p = _shell?.SelectedPlugin;
        var canEmbed = _shell is { ConfigReady: true } && p is { HasNativeConfig: true };
        EmbedSlot.Child = canEmbed && p is { Hwnd: var h } && h != IntPtr.Zero
            ? GetEmbed(h)
            : null;
    }

    private EmbeddedSatelliteView GetEmbed(IntPtr hwnd)
    {
        if (!_embeds.TryGetValue(hwnd, out var view))
            _embeds[hwnd] = view = new EmbeddedSatelliteView(hwnd, _loggerFactory.CreateLogger<EmbeddedSatelliteView>());
        return view;
    }
}
