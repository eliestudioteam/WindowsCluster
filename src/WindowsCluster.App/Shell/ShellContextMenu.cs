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
            // Desktop-relative PIDLs through the parent folder: the same menu Explorer
            // shows for these icons on the desktop.
            menuObject = ShellItems.BindToHandler(parsingNames, typeof(IContextMenu).GUID, owner);
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

    // ---------------------------------------------------------------- menu model for the Windows 11 layout

    /// <summary>One command (or submenu) read from the shell's context menu.</summary>
    public sealed class Entry
    {
        public string Text { get; init; } = "";
        public string? Shortcut { get; init; }
        public System.Windows.Media.ImageSource? Icon { get; init; }
        public int CommandId { get; init; } = -1;
        public string? Verb { get; init; }
        public bool IsSeparator { get; init; }
        public bool IsEnabled { get; init; } = true;
        public List<Entry> Children { get; } = new();
    }

    /// <summary>
    /// A live shell context menu whose commands are read into <see cref="Entries"/> so they can
    /// be shown in a WPF menu. Every entry still runs the shell's own command when invoked.
    /// </summary>
    public sealed class Session : IDisposable
    {
        private const uint MIIM_STATE = 0x01, MIIM_ID = 0x02, MIIM_SUBMENU = 0x04, MIIM_STRING = 0x40,
            MIIM_BITMAP = 0x80, MIIM_FTYPE = 0x100;
        private const uint MFT_SEPARATOR = 0x800, MFT_OWNERDRAW = 0x100, MFS_DISABLED = 0x3;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MENUITEMINFO
        {
            public int cbSize;
            public uint fMask, fType, fState, wID;
            public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
            public uint cch;
            public IntPtr hbmpItem;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMenuItemInfo(IntPtr hmenu, uint item, bool fByPosition, ref MENUITEMINFO lpmii);

        [DllImport("user32.dll")]
        private static extern int GetMenuItemCount(IntPtr hMenu);

        private readonly object _menuObject;
        private readonly IContextMenu _menu;
        private readonly IntPtr _hmenu;
        private readonly IntPtr _owner;

        private Session(object menuObject, IntPtr hmenu, IntPtr owner)
        {
            _menuObject = menuObject;
            _menu = (IContextMenu)menuObject;
            _hmenu = hmenu;
            _owner = owner;
            Entries = Read(hmenu);
        }

        public IReadOnlyList<Entry> Entries { get; }

        /// <summary>Builds the menu for <paramref name="parsingNames"/>, or null if the shell offers none.</summary>
        public static Session? Create(IntPtr owner, IReadOnlyList<string> parsingNames)
        {
            object? menuObject = null;
            var hmenu = IntPtr.Zero;
            try
            {
                menuObject = ShellItems.BindToHandler(parsingNames, typeof(IContextMenu).GUID, owner);
                if (menuObject is not IContextMenu menu)
                    return null;
                hmenu = CreatePopupMenu();
                var flags = CMF_NORMAL | CMF_CANRENAME;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                    flags |= CMF_EXTENDEDVERBS;
                if (menu.QueryContextMenu(hmenu, 0, FirstCommand, LastCommand, flags) < 0)
                    return null;
                var session = new Session(menuObject, hmenu, owner);
                menuObject = null;
                hmenu = IntPtr.Zero;
                return session;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellContextMenu.Session] {ex}");
                return null;
            }
            finally
            {
                if (hmenu != IntPtr.Zero)
                    DestroyMenu(hmenu);
                if (menuObject is not null)
                    Marshal.ReleaseComObject(menuObject);
            }
        }

        /// <summary>Runs the shell command behind <paramref name="entry"/>.</summary>
        public void Invoke(Entry entry, POINT point)
        {
            if (entry.CommandId < FirstCommand)
                return;
            try
            {
                ShellContextMenu.Invoke(_menu, _owner, (IntPtr)(entry.CommandId - FirstCommand), point);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellContextMenu.Session] Invoke: {ex.Message}");
            }
        }

        public void Dispose()
        {
            DestroyMenu(_hmenu);
            Marshal.ReleaseComObject(_menuObject);
        }

        private List<Entry> Read(IntPtr hmenu)
        {
            var entries = new List<Entry>();
            var count = GetMenuItemCount(hmenu);
            for (var i = 0; i < count; i++)
            {
                var info = new MENUITEMINFO
                {
                    cbSize = Marshal.SizeOf<MENUITEMINFO>(),
                    fMask = MIIM_FTYPE | MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_STRING | MIIM_BITMAP,
                };
                if (!GetMenuItemInfo(hmenu, (uint)i, true, ref info))
                    continue;
                if ((info.fType & MFT_SEPARATOR) != 0)
                {
                    if (entries.Count > 0 && !entries[^1].IsSeparator)
                        entries.Add(new Entry { IsSeparator = true });
                    continue;
                }
                if ((info.fType & MFT_OWNERDRAW) != 0)
                    continue; // drawn by the extension itself; still available in "Show more options"

                var text = ReadText(hmenu, i, info.cch);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                var tab = text.IndexOf('\t');
                var label = (tab >= 0 ? text[..tab] : text).Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
                var shortcut = tab >= 0 ? text[(tab + 1)..] : null;

                var entry = new Entry
                {
                    Text = label,
                    Shortcut = shortcut,
                    Icon = ReadIcon(info.hbmpItem),
                    CommandId = info.hSubMenu == IntPtr.Zero ? (int)info.wID : -1,
                    Verb = info.hSubMenu == IntPtr.Zero && info.wID >= FirstCommand ? GetVerb(_menu, (int)info.wID - FirstCommand) : null,
                    IsEnabled = (info.fState & MFS_DISABLED) == 0,
                };
                if (info.hSubMenu != IntPtr.Zero)
                {
                    // Submenus such as "Send to" are filled on demand by the extension.
                    if (_menuObject is IContextMenu3 cm3)
                        cm3.HandleMenuMsg2(WM_INITMENUPOPUP, info.hSubMenu, (IntPtr)i, out _);
                    else if (_menuObject is IContextMenu2 cm2)
                        cm2.HandleMenuMsg(WM_INITMENUPOPUP, info.hSubMenu, (IntPtr)i);
                    entry.Children.AddRange(Read(info.hSubMenu));
                    if (entry.Children.Count == 0)
                        continue;
                }
                entries.Add(entry);
            }
            if (entries.Count > 0 && entries[^1].IsSeparator)
                entries.RemoveAt(entries.Count - 1);
            return entries;
        }

        private static string ReadText(IntPtr hmenu, int index, uint length)
        {
            if (length == 0)
                return "";
            var buffer = Marshal.AllocHGlobal((int)(length + 1) * 2);
            try
            {
                var info = new MENUITEMINFO
                {
                    cbSize = Marshal.SizeOf<MENUITEMINFO>(),
                    fMask = MIIM_STRING,
                    dwTypeData = buffer,
                    cch = length + 1,
                };
                return GetMenuItemInfo(hmenu, (uint)index, true, ref info) ? Marshal.PtrToStringUni(buffer) ?? "" : "";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static System.Windows.Media.ImageSource? ReadIcon(IntPtr hbmp)
        {
            // HBMMENU_CALLBACK (-1) and the predefined HBMMENU_* values (1..11) are not real bitmaps.
            var value = hbmp.ToInt64();
            if (value is >= -1 and <= 11)
                return null;
            try
            {
                return ShellIconLoader.ToBitmapSource(hbmp);
            }
            catch (Exception)
            {
                return null;
            }
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
