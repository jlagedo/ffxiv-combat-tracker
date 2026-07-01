using System;
using System.Collections.Generic;

namespace Fct.Abstractions.Testing
{
    /// <summary>
    /// In-memory <see cref="IPluginRegistry"/>: real dictionaries backing named callbacks (with
    /// owner + duplicate-name policy, G5), typed publish/subscribe, and version-gated peer service
    /// handles (G6, via the <see cref="RegisterPeerService{T}"/> test seam).
    /// </summary>
    public sealed class InMemoryRegistry : IPluginRegistry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, List<Registration>> _callbacks = new Dictionary<string, List<Registration>>();
        private readonly Dictionary<Type, List<Delegate>> _subscribers = new Dictionary<Type, List<Delegate>>();
        private readonly Dictionary<string, object> _peerServices = new Dictionary<string, object>();

        public InMemoryRegistry(params PluginInfo[] loaded)
        {
            LoadedPlugins = loaded ?? Array.Empty<PluginInfo>();
        }

        public IReadOnlyList<PluginInfo> LoadedPlugins { get; }

        public IDisposable RegisterCallback(string name, Action<object?> callback, object? owner = null, bool allowDuplicate = false)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
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

        /// <summary>Test seam: publish a typed service handle for a peer plugin (backs G6 lookup).</summary>
        public void RegisterPeerService<T>(string pluginId, T service) where T : class
        {
            if (pluginId == null) throw new ArgumentNullException(nameof(pluginId));
            if (service == null) throw new ArgumentNullException(nameof(service));
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
            if (handler == null) throw new ArgumentNullException(nameof(handler));
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
    }
}
