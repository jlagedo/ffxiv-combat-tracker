using System;
using System.Collections.Generic;
using Fct.Abstractions;

namespace Fct.App.Hosting;

/// <summary>
/// The per-plugin <see cref="IPluginRegistry"/> handed to one plugin. Delegates to the shared
/// <see cref="RegistryService"/> but <b>records every registration</b> (callbacks, subscriptions) it
/// hands out and disposes them all when the plugin unloads — so a plugin that forgets to dispose a
/// handle can't pin its collectible ALC and block hot-unload. Also drops the plugin's peer service.
/// Cross-plugin reads (<see cref="InvokeCallback"/>/<see cref="Publish{T}"/>/<see cref="TryGetPeerService{T}"/>)
/// pass straight through to the shared registry.
/// </summary>
internal sealed class ScopedPluginRegistry : IPluginRegistry, IDisposable
{
    private readonly RegistryService _inner;
    private readonly string _pluginId;
    private readonly object _gate = new();
    private readonly List<IDisposable> _handles = new();
    private bool _disposed;

    public ScopedPluginRegistry(RegistryService inner, string pluginId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
    }

    public IReadOnlyList<PluginInfo> LoadedPlugins => _inner.LoadedPlugins;

    public IDisposable RegisterCallback(string name, Action<object?> callback, object? owner = null, bool allowDuplicate = false)
        => Track(_inner.RegisterCallback(name, callback, owner ?? _pluginId, allowDuplicate));

    public void InvokeCallback(string name, object? argument = null) => _inner.InvokeCallback(name, argument);

    public void Publish<T>(T evt) where T : notnull => _inner.Publish(evt);

    public IDisposable Subscribe<T>(Action<T> handler) where T : notnull => Track(_inner.Subscribe(handler));

    public bool TryGetPeerService<T>(string pluginId, out T service) where T : class
        => _inner.TryGetPeerService(pluginId, out service);

    private IDisposable Track(IDisposable handle)
    {
        lock (_gate)
        {
            if (_disposed) { handle.Dispose(); return handle; }
            _handles.Add(handle);
        }
        return handle;
    }

    public void Dispose()
    {
        IDisposable[] handles;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            handles = _handles.ToArray();
            _handles.Clear();
        }
        foreach (var h in handles)
        {
            try { h.Dispose(); } catch { /* best-effort teardown */ }
        }
        _inner.UnregisterPeerService(_pluginId);
    }
}
