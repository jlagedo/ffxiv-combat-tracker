using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Fct.Logging;
using Microsoft.Extensions.Logging;

namespace Fct.App;

// Hosts the satellite's (foreign-process) top-level WinForms window inside the Avalonia
// visual tree by reparenting its HWND under Avalonia's native host window. NativeControlHost
// handles positioning/sizing/clipping of the embedded HWND across the process boundary.
internal sealed class EmbeddedSatelliteView : NativeControlHost
{
    private readonly IntPtr _childHwnd;
    private readonly ILogger _log;

    public EmbeddedSatelliteView(IntPtr childHwnd, ILogger log)
    {
        _childHwnd = childHwnd;
        _log = log;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        const int GWL_STYLE = -16;
        const long WS_CHILD = 0x40000000L;
        const long WS_POPUP = 0x80000000L;
        const long WS_CAPTION = 0x00C00000L;
        const long WS_THICKFRAME = 0x00040000L;
        const int SW_SHOW = 5;

        // Convert the satellite's top-level window into an embeddable child.
        long style = GetWindowLongPtr(_childHwnd, GWL_STYLE).ToInt64();
        style = (style | WS_CHILD) & ~WS_POPUP & ~WS_CAPTION & ~WS_THICKFRAME;
        SetWindowLongPtr(_childHwnd, GWL_STYLE, new IntPtr(style));

        SetParent(_childHwnd, parent.Handle);
        ShowWindow(_childHwnd, SW_SHOW);

        // Confirm the child is now parented into our process's window.
        IntPtr newParent = GetParent(_childHwnd);
        GetWindowThreadProcessId(newParent, out uint parentPid);
        if (newParent == parent.Handle)
            _log.LogInformation(LogEvents.WindowReparented,
                "Embedded plugin window 0x{Child:X} under host window 0x{Parent:X} (ourPid {OurPid})",
                _childHwnd.ToInt64(), newParent.ToInt64(), Process.GetCurrentProcess().Id);
        else
            _log.LogWarning(LogEvents.WindowReparentFailed,
                "Reparent of 0x{Child:X} did not stick: parent is 0x{Parent:X} (pid {ParentPid}), expected 0x{Expected:X}",
                _childHwnd.ToInt64(), newParent.ToInt64(), parentPid, parent.Handle.ToInt64());

        return new PlatformHandle(_childHwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // The satellite owns its window; detach (and hide) instead of destroying it, so a
        // deselected plugin's window doesn't flash on screen as a stray top-level window.
        const int SW_HIDE = 0;
        SetParent(_childHwnd, IntPtr.Zero);
        ShowWindow(_childHwnd, SW_HIDE);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
