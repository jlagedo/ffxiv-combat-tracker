using System;
using System.Threading;

namespace Fct.Host.Hosting;

/// <summary>An <see cref="IDisposable"/> that runs an action exactly once (unregister handles).</summary>
internal sealed class ActionDisposable : IDisposable
{
    private Action? _action;

    public ActionDisposable(Action action) => _action = action;

    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}
