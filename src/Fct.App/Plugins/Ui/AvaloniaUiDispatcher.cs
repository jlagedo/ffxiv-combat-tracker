using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Fct.Abstractions.UI;

namespace Fct.App.Plugins.Ui;

// Wraps Avalonia's UI-thread dispatcher for plugins (the modern InvokeRequired/Invoke). Safe to
// construct before Avalonia starts — it only touches Dispatcher.UIThread when a member is called.
internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public async Task InvokeAsync(Action action) => await Dispatcher.UIThread.InvokeAsync(action);

    public async Task<T> InvokeAsync<T>(Func<T> func) => await Dispatcher.UIThread.InvokeAsync(func);
}
