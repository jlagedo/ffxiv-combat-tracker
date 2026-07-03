using System;
using System.Collections.Generic;
using Fct.Abstractions;

namespace Fct.Host.Hosting;

/// <summary>
/// Production <see cref="IPluginRegistry"/> — the real form of the reference <c>InMemoryRegistry</c>.
/// Named callbacks carry owner + duplicate-name policy (G5); typed <see cref="Publish{T}"/>/
/// <see cref="Subscribe{T}"/> put a plugin's own events on the bus; <see cref="TryGetPeerService{T}"/>
/// hands a live typed peer handle (G6). <see cref="LoadedPlugins"/> reflects the live roster the
/// <see cref="PluginManager"/> maintains.
/// </summary>
internal sealed class RegistryService : IPluginRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Registration>> _callbacks = new();
    private readonly Dictionary<Type, List<Delegate>> _subscribers = new();
    private readonly Dictionary<string, object> _peerServices = new();
    private volatile IReadOnlyList<PluginInfo> _loaded = Array.Empty<PluginInfo>();

    public IReadOnlyList<PluginInfo> LoadedPlugins => _loaded;

    /// <summary>Raised after the roster changes so the shell can reconcile its modern-plugin rows
    /// live (load/unload without a restart).</summary>
    public event Action? RosterChanged;

    /// <summary>Set by the <see cref="PluginManager"/> as plugins load/unload.</summary>
    public void SetRoster(IReadOnlyList<PluginInfo> plugins)
    {
        _loaded = plugins ?? Array.Empty<PluginInfo>();
        RosterChanged?.Invoke();
    }

    /// <summary>Drop a plugin's published peer service on unload (there is no cooperative handle for
    /// this, unlike callbacks/subscriptions), so it can't pin the unloaded plugin's ALC.</summary>
    public void UnregisterPeerService(string pluginId)
    {
        if (pluginId is null) return;
        lock (_gate) _peerServices.Remove(pluginId);
    }

    public IDisposable RegisterCallback(string name, Action<object?> callback, object? owner = null, bool allowDuplicate = false)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (callback is null) throw new ArgumentNullException(nameof(callback));
        var reg = new Registration(callback, owner);
        lock (_gate)
        {
            if (!_callbacks.TryGetValue(name, out var list))
            {
                list = new List<Registration>();
                _callbacks[name] = list;
            }
            else if (!allowDuplicate)
            {
                throw new InvalidOperationException($"A callback named '{name}' is already registered.");
            }
            list.Add(reg);
        }
        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_callbacks.TryGetValue(name, out var list))
                {
                    list.Remove(reg);
                    if (list.Count == 0) _callbacks.Remove(name);
                }
            }
        });
    }

    public void InvokeCallback(string name, object? argument = null)
    {
        Registration[] targets;
        lock (_gate)
        {
            if (!_callbacks.TryGetValue(name, out var list)) return;
            targets = list.ToArray();
        }
        foreach (var reg in targets) reg.Callback(argument);
    }

    public void Publish<T>(T evt) where T : notnull
    {
        Delegate[] targets;
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(typeof(T), out var list)) return;
            targets = list.ToArray();
        }
        foreach (var d in targets) ((Action<T>)d)(evt);
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : notnull
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(typeof(T), out var list))
            {
                list = new List<Delegate>();
                _subscribers[typeof(T)] = list;
            }
            list.Add(handler);
        }
        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_subscribers.TryGetValue(typeof(T), out var list))
                {
                    list.Remove(handler);
                    if (list.Count == 0) _subscribers.Remove(typeof(T));
                }
            }
        });
    }

    /// <summary>Publish a typed service handle for a peer plugin (backs <see cref="TryGetPeerService{T}"/>).</summary>
    public void RegisterPeerService<T>(string pluginId, T service) where T : class
    {
        if (pluginId is null) throw new ArgumentNullException(nameof(pluginId));
        if (service is null) throw new ArgumentNullException(nameof(service));
        lock (_gate) _peerServices[pluginId] = service;
    }

    public bool TryGetPeerService<T>(string pluginId, out T service) where T : class
    {
        lock (_gate)
        {
            if (_peerServices.TryGetValue(pluginId, out var obj) && obj is T typed)
            {
                service = typed;
                return true;
            }
        }
        service = null!;
        return false;
    }

    private sealed class Registration
    {
        public Action<object?> Callback { get; }
        public object? Owner { get; }
        public Registration(Action<object?> callback, object? owner) { Callback = callback; Owner = owner; }
    }
}
