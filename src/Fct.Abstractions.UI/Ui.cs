using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Fct.Abstractions.UI
{
    /// <summary>
    /// Implemented alongside <c>IPlugin</c> by a plugin that contributes UI to the Avalonia shell.
    /// Replaces the legacy single fixed WinForms <c>TabPage</c> handed to <c>IActPluginV1.InitPlugin</c>.
    /// </summary>
    public interface IUiContributor
    {
        /// <summary>
        /// Add surfaces to the shell. Invoked once on the UI thread after the plugin's
        /// <c>IPlugin.InitializeAsync</c> completes and the shell is live.
        /// </summary>
        void RegisterUi(IUiHost ui);
    }

    /// <summary>The shell's UI extension surface. All <c>Add*</c> calls run on the UI thread.</summary>
    public interface IUiHost
    {
        /// <summary>A config page in the Plugins section (the WinForms TabPage replacement).</summary>
        void AddSettingsPage(UiSurface page);

        /// <summary>
        /// Bring a previously-contributed settings page to the foreground — the modern form of
        /// Triggernometry's <c>LocateTab</c> "reveal my page" request.
        /// </summary>
        void RevealPage(string pageId);

        /// <summary>
        /// Show a transient corner control/notification over the shell (Triggernometry's
        /// <c>CornerControlAdd</c>). Dispose the handle, or call <see cref="RemoveCornerControl"/> with
        /// the surface id, to remove it (<c>CornerControlRemove</c>).
        /// </summary>
        IDisposable AddCornerControl(UiSurface control);

        /// <summary>Remove a corner control by id (Triggernometry's <c>CornerControlRemove</c>).</summary>
        void RemoveCornerControl(string id);

        /// <summary>Marshal work onto the UI thread (the modern form of InvokeRequired/Invoke).</summary>
        IUiDispatcher Dispatcher { get; }
    }

    /// <summary>UI-thread marshaling for plugins updating views from background threads.</summary>
    public interface IUiDispatcher
    {
        /// <summary>True when the caller is already on the UI thread (the modern form of <c>!InvokeRequired</c>).</summary>
        bool CheckAccess();

        /// <summary>Queue <paramref name="action"/> to run on the UI thread; returns immediately (fire-and-forget).</summary>
        void Post(Action action);

        /// <summary>Run <paramref name="action"/> on the UI thread and await its completion.</summary>
        Task InvokeAsync(Action action);

        /// <summary>Run <paramref name="func"/> on the UI thread and await its result.</summary>
        Task<T> InvokeAsync<T>(Func<T> func);
    }

    /// <summary>
    /// A contributed page. <see cref="CreateView"/> is lazy — built on the UI thread the first time
    /// the surface is shown; a throw is contained to an error placeholder, never a dead shell.
    /// </summary>
    public sealed record UiSurface(string Id, string Title, Func<Control> CreateView, string? IconGlyph = null, int Order = 0);
}
