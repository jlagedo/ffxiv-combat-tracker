using System;
using System.Collections.Generic;

namespace Fct.Abstractions.Testing
{
    /// <summary>
    /// In-memory <see cref="IPluginRegistry"/>: real dictionaries backing named callbacks and typed
    /// publish/subscribe. Implements the shipped (thin) contract exactly — the richer G5 owner/dup
    /// callback semantics are out of scope for this harness.
    /// </summary>
    public sealed class InMemoryRegistry : IPluginRegistry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, List<Action<object?>>> _callbacks = new Dictionary<string, List<Action<object?>>>();
        private readonly Dictionary<Type, List<Delegate>> _subscribers = new Dictionary<Type, List<Delegate>>();

        public InMemoryRegistry(params PluginInfo[] loaded)
        {
            LoadedPlugins = loaded ?? Array.Empty<PluginInfo>();
        }

        public IReadOnlyList<PluginInfo> LoadedPlugins { get; }

        public IDisposable RegisterCallback(string name, Action<object?> callback)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (_gate)
            {
                if (!_callbacks.TryGetValue(name, out var list))
                {
                    list = new List<Action<object?>>();
                    _callbacks[name] = list;
                }
                list.Add(callback);
            }
            return new ActionDisposable(() =>
            {
                lock (_gate)
                {
                    if (_callbacks.TryGetValue(name, out var list))
                    {
                        list.Remove(callback);
                        if (list.Count == 0) _callbacks.Remove(name);
                    }
                }
            });
        }

        public void InvokeCallback(string name, object? argument = null)
        {
            Action<object?>[] targets;
            lock (_gate)
            {
                if (!_callbacks.TryGetValue(name, out var list)) return;
                targets = list.ToArray();
            }
            foreach (var cb in targets) cb(argument);
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
