using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Fct.Abstractions;
using Fct.Abstractions.UI;
using Fct.Host.Plugins;
using Fct.Host.Plugins.Ui;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fct.App.Tests.Plugins;

public class PluginUiCoordinatorTests
{
    private static PluginManifest Manifest(string id, bool ui) => new(
        id, "1.0.0", "1.0", "Test.dll", "Test.Entry", ui ? new[] { "ui" } : Array.Empty<string>());

    private static PluginUiCoordinator NewCoordinator() => new(new ImmediateDispatcher(), NullLoggerFactory.Instance);

    [Fact]
    public void RegisterUi_is_called_for_a_ui_capable_contributor()
    {
        var coordinator = NewCoordinator();
        var plugin = new RecordingUiPlugin();

        coordinator.FlushRegisterUi(new (PluginManifest, IPlugin)[] { (Manifest("com.a", ui: true), plugin) });

        Assert.True(plugin.RegisterUiCalled);
    }

    [Fact]
    public void RegisterUi_is_skipped_without_the_ui_capability()
    {
        var coordinator = NewCoordinator();
        var plugin = new RecordingUiPlugin();

        coordinator.FlushRegisterUi(new (PluginManifest, IPlugin)[] { (Manifest("com.a", ui: false), plugin) });

        Assert.False(plugin.RegisterUiCalled);
    }

    [Fact]
    public void Ui_capability_without_IUiContributor_is_a_noop()
    {
        var coordinator = NewCoordinator();
        var plugin = new PlainPlugin();

        // Declares "ui" but doesn't implement IUiContributor — must be skipped, not throw.
        coordinator.FlushRegisterUi(new (PluginManifest, IPlugin)[] { (Manifest("com.a", ui: true), plugin) });
    }

    [Fact]
    public void A_throwing_RegisterUi_is_contained_and_peers_still_register()
    {
        var coordinator = NewCoordinator();
        var settingsPages = new List<(string PluginId, UiSurface Page)>();
        coordinator.SettingsPageAdded += (id, page) => settingsPages.Add((id, page));

        var thrower = new ThrowingUiPlugin();
        var peer = new RecordingUiPlugin();

        coordinator.FlushRegisterUi(new (PluginManifest, IPlugin)[]
        {
            (Manifest("com.thrower", ui: true), thrower),
            (Manifest("com.peer", ui: true), peer),
        });

        // The peer still registered despite the thrower's fault…
        Assert.True(peer.RegisterUiCalled);
        // …and the thrower's fault surfaced as an inline error page instead of propagating.
        Assert.Contains(settingsPages, p => p.PluginId == "com.thrower");
        Assert.Contains(settingsPages, p => p.PluginId == "com.peer");
    }

    [Fact]
    public void AddSettingsPage_raises_the_event_and_ignores_duplicate_ids()
    {
        var coordinator = NewCoordinator();
        var received = new List<(string PluginId, UiSurface Page)>();
        coordinator.SettingsPageAdded += (id, page) => received.Add((id, page));

        var host = new PluginUiHost("com.a", coordinator);
        var page = new UiSurface("com.a.settings", "A", () => new TextBlock());
        host.AddSettingsPage(page);
        host.AddSettingsPage(page);   // duplicate id — ignored

        Assert.Single(received);
    }

    [Fact]
    public void RevealPage_raises_the_event()
    {
        var coordinator = NewCoordinator();
        string? revealed = null;
        coordinator.PageRevealRequested += id => revealed = id;

        new PluginUiHost("com.a", coordinator).RevealPage("com.a.settings");

        Assert.Equal("com.a.settings", revealed);
    }

    [Fact]
    public void AddCornerControl_raises_added_then_dispose_raises_removed_exactly_once()
    {
        var coordinator = NewCoordinator();
        var added = new List<string>();
        var removed = new List<string>();
        coordinator.CornerControlAdded += (_, control) => added.Add(control.Id);
        coordinator.CornerControlRemoved += id => removed.Add(id);

        var host = new PluginUiHost("com.a", coordinator);
        var control = new UiSurface("com.a.corner", "Corner", () => new TextBlock());
        var handle = host.AddCornerControl(control);

        Assert.Equal(new[] { "com.a.corner" }, added);

        handle.Dispose();
        handle.Dispose();   // idempotent — must not double-fire

        Assert.Equal(new[] { "com.a.corner" }, removed);
    }

    // Runs everything synchronously, standing in for the real Avalonia UI thread.
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
    }

    private sealed class PlainPlugin : IPlugin
    {
        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }

    private sealed class RecordingUiPlugin : IPlugin, IUiContributor
    {
        public bool RegisterUiCalled { get; private set; }
        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;

        public void RegisterUi(IUiHost ui)
        {
            RegisterUiCalled = true;
            ui.AddSettingsPage(new UiSurface("recording.page", "Recording", () => new TextBlock()));
        }
    }

    private sealed class ThrowingUiPlugin : IPlugin, IUiContributor
    {
        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
        public void RegisterUi(IUiHost ui) => throw new InvalidOperationException("boom");
    }
}
