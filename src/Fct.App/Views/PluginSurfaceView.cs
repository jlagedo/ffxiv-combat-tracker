using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Fct.Abstractions.UI;
// Aliased: ContentControl inherits StyledElement.Resources, which shadows the Fct.App.Lang.Resources
// type name inside this class.
using Strings = Fct.App.Lang.Resources;

namespace Fct.App.Views;

// Renders a plugin-contributed UiSurface's lazily-built Control — the config-bay settings page and
// the corner-control overlay both host one of these. CreateView() runs at most once per surface (a
// throw becomes an inline error placeholder, never a dead shell — PLUGIN-API.md's UI fault-containment
// contract) and the built Control is cached by surface id so re-selecting a plugin doesn't rebuild it.
internal sealed class PluginSurfaceView : ContentControl
{
    public static readonly StyledProperty<UiSurface?> SurfaceProperty =
        AvaloniaProperty.Register<PluginSurfaceView, UiSurface?>(nameof(Surface));

    public UiSurface? Surface
    {
        get => GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    private readonly Dictionary<string, Control> _built = new(StringComparer.Ordinal);

    static PluginSurfaceView()
    {
        SurfaceProperty.Changed.AddClassHandler<PluginSurfaceView>((view, _) => view.Rebuild());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    private void Rebuild()
    {
        var surface = Surface;
        if (surface is null)
        {
            Content = null;
            return;
        }

        if (!_built.TryGetValue(surface.Id, out var view))
            _built[surface.Id] = view = BuildSafely(surface);
        Content = view;
    }

    private static Control BuildSafely(UiSurface surface)
    {
        try
        {
            return surface.CreateView();
        }
        catch (Exception ex)
        {
            return new TextBlock
            {
                Text = string.Format(Strings.Plugins_ModernUiErrorBodyFormat, surface.Title, ex.Message),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
            };
        }
    }
}
