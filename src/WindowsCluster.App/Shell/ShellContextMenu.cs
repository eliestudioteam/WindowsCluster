using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Shell;

/// <summary>
/// Shows Windows' own context menu (IContextMenu) for real shell items: Open with, Send to,
/// Cut/Copy, Properties and every installed shell extension. This menu only exists as a
/// Win32 menu; <see cref="NativeMenuTheme"/> makes it follow the light/dark system theme.
/// </summary>
public static class ShellContextMenu
{
    private const uint CMF_NORMAL = 0x0000;
    private const uint CMF_CANRENAME = 0x0010;
    private const uint CMF_EXTENDEDVERBS = 0x0100;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;
    private const uint CMIC_MASK_SHIFT_DOWN = 0x10000000;
    private const uint CMIC_MASK_CONTROL_DOWN = 0x40000000;
    private const uint GCS_VERBW = 0x00000004;
    private const int SW_SHOWNORMAL = 1;
    private const int FirstCommand = 1;
    private const int LastCommand = 0x7FFF;

    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_DRAWITEM = 0x002B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_MENUCHAR = 0x0120;
    private const int WM_NULL = 0x0000;

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Shows the shell menu for <paramref name="parsingNames"/> at <paramref name="screenPoint"/>
    /// and runs the chosen command. Never throws: a shell failure just shows no menu.
    /// </summary>
    /// <returns>The canonical verb chosen (e.g. "open", "delete", "rename"), or null.</returns>
    public static string? Show(IntPtr owner, IReadOnlyList<string> parsingNames, POINT screenPoint)
    {
        object? menuObject = null;
        var hmenu = IntPtr.Zero;
        HwndSource? source = null;
        HwndSourceHook? hook = null;
        try
        {
            // Bound through IShellItemArray from desktop-relative PIDLs: the same menu
            // Explorer shows for these icons on the desktop.
            menuObject = ShellItems.BindToHandler(parsingNames, typeof(IContextMenu).GUID);
            if (menuObject is not IContextMenu menu)
                return null;

            hmenu = CreatePopupMenu();
            var flags = CMF_NORMAL | CMF_CANRENAME;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                flags |= CMF_EXTENDEDVERBS;
            if (menu.QueryContextMenu(hmenu, 0, FirstCommand, LastCommand, flags) < 0)
                return null;

            // Submenus such as "Send to" and "Open with" are filled lazily through these messages.
            source = HwndSource.FromHwnd(owner);
            if (source is not null)
            {
                var target = menuObject;
                hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR))
                        return IntPtr.Zero;
                    if (target is IContextMenu3 cm3 && cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
                    {
                        handled = true;
                        return result;
                    }
                    if (target is IContextMenu2 cm2 && cm2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                    {
                        handled = true;
                    }
                    return IntPtr.Zero;
                };
                source.AddHook(hook);
            }

            NativeMenuTheme.Apply();
            // TrackPopupMenu requires the owner to be the foreground window, or the menu
            // will not close when the user clicks elsewhere (documented Win32 behavior).
            SetForegroundWindow(owner);
            var command = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenPoint.X, screenPoint.Y, owner, IntPtr.Zero);
            PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (command < FirstCommand)
                return null;

            var verb = GetVerb(menu, command - FirstCommand);
            if (verb == "rename")
                return verb; // the caller renames in place; the shell cannot edit our view

            Invoke(menu, owner, (IntPtr)(command - FirstCommand), screenPoint);
            return verb;
        }
        catch (Exception ex)
        {
            // Shell extensions are third-party code; never let one take the app down.
            Debug.WriteLine($"[ShellContextMenu] {ex}");
            return null;
        }
        finally
        {
            if (source is not null && hook is not null)
                source.RemoveHook(hook);
            if (hmenu != IntPtr.Zero)
                DestroyMenu(hmenu);
            if (menuObject is not null)
                Marshal.ReleaseComObject(menuObject);
        }
    }

    private static void Invoke(IContextMenu menu, IntPtr owner, IntPtr verb, POINT point)
    {
        var modifiers = Keyboard.Modifiers;
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
                    | ((modifiers & ModifierKeys.Control) != 0 ? CMIC_MASK_CONTROL_DOWN : 0)
                    | ((modifiers & ModifierKeys.Shift) != 0 ? CMIC_MASK_SHIFT_DOWN : 0),
            hwnd = owner,
            lpVerb = verb,
            lpVerbW = verb,
            nShow = SW_SHOWNORMAL,
            ptInvoke = point,
        };
        var hr = menu.InvokeCommand(ref info);
        if (hr < 0)
            Debug.WriteLine($"[ShellContextMenu] InvokeCommand 0x{hr:X8}");
    }

    private static string? GetVerb(IContextMenu menu, int offset)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            return menu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, 256) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>
/// Makes Win32 popup menus created by this process follow the Windows light/dark theme.
/// Uses the uxtheme entry points Explorer itself relies on (exported by ordinal only,
/// available since Windows 10 1903); on older systems this silently does nothing.
/// </summary>
public static class NativeMenuTheme
{
    private enum PreferredAppMode { Default = 0, AllowDark = 1, ForceDark = 2, ForceLight = 3 }

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(PreferredAppMode mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    public static void Apply()
    {
        try
        {
            var dark = Theming.ThemeService.Current?.IsDark ?? false;
            SetPreferredAppMode(dark ? PreferredAppMode.ForceDark : PreferredAppMode.ForceLight);
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Unsupported Windows build: menus stay light.
        }
    }
}
