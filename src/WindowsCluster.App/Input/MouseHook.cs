using System.ComponentModel;
using System.Runtime.InteropServices;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Input;

/// <summary>Mouse event seen by <see cref="MouseHook"/>.</summary>
public sealed class LowLevelMouseEventArgs(int message, POINT point, bool isInjected, IntPtr extraInfo) : EventArgs
{
    /// <summary>WM_MOUSEMOVE, WM_RBUTTONDOWN, WM_RBUTTONUP, …</summary>
    public int Message { get; } = message;

    /// <summary>Cursor position in physical screen pixels.</summary>
    public POINT Point { get; } = point;

    /// <summary>True when the event was synthesized with SendInput (by us or another app).</summary>
    public bool IsInjected { get; } = isInjected;

    /// <summary>dwExtraInfo of the event; used to recognize our own replayed input.</summary>
    public IntPtr ExtraInfo { get; } = extraInfo;

    /// <summary>
    /// Set to true to swallow the event so no window receives it.
    /// Never swallow WM_MOUSEMOVE: that freezes the cursor for the whole system.
    /// </summary>
    public bool Handled { get; set; }
}

/// <summary>
/// System-wide low-level mouse hook (WH_MOUSE_LL). Runs in this process: Windows calls it
/// on the installing thread's message loop, so no DLL is injected into other processes.
/// The callback must return quickly (LowLevelHooksTimeout, ~300 ms by default) or Windows
/// skips it and eventually removes the hook, so handlers must defer any slow work.
/// </summary>
public sealed class MouseHook : IDisposable
{
    // Kept in a field so the GC cannot collect the delegate while Windows still calls it.
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook;

    public MouseHook()
    {
        _proc = HookProc;
    }

    public event EventHandler<LowLevelMouseEventArgs>? MouseEvent;

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>Installs the hook. Call from a thread that pumps messages (the UI thread).</summary>
    public void Install()
    {
        if (IsInstalled)
            return;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo instalar el hook de ratón.");
    }

    public void Uninstall()
    {
        if (!IsInstalled)
            return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose() => Uninstall();

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && MouseEvent is { } handler)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var args = new LowLevelMouseEventArgs(
                (int)wParam,
                data.pt,
                (data.flags & LLMHF_INJECTED) != 0,
                data.dwExtraInfo);

            try
            {
                handler(this, args);
            }
            catch
            {
                // An exception escaping into user32 would tear down the hook chain; never swallow input on failure.
                args.Handled = false;
            }

            if (args.Handled && args.Message != WM_MOUSEMOVE)
                return (IntPtr)1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
