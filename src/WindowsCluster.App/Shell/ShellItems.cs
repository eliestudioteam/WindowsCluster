using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WindowsCluster.App.Shell;

/// <summary>
/// Resolves Cluster items to shell items the same way Explorer's desktop does.
/// Desktop items are parsed relative to the desktop root, so they get the exact identity
/// Explorer uses for its own icons: opening a folder shows "Desktop › Folder" in the
/// address bar, and context menus and info tips match the desktop's.
/// </summary>
internal static class ShellItems
{
    public static readonly Guid BHID_SFUIObject = new("3981E225-F559-11D3-8E3A-0010A01BA102");
    public static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("00021500-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IQueryInfo
    {
        [PreserveSig] int GetInfoTip(uint dwFlags, out IntPtr ppwszTip);
        [PreserveSig] int GetInfoFlags(out uint pdwFlags);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint cidl, [In] IntPtr[] rgpidl, out IShellItemArray ppsiItemArray);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    private const int SW_SHOWNORMAL = 1;

    /// <summary>
    /// Absolute PIDL for <paramref name="parsingName"/>; desktop items are resolved as children
    /// of the desktop root. Free with <see cref="Marshal.FreeCoTaskMem"/>. Returns zero on failure.
    /// </summary>
    public static IntPtr ParsePidl(string parsingName)
    {
        var relative = DesktopItemVisibility.IsDesktopItem(parsingName) && !parsingName.StartsWith("::", StringComparison.Ordinal)
            ? Path.GetFileName(parsingName)
            : parsingName;

        if (SHGetDesktopFolder(out var desktop) == 0)
        {
            try
            {
                uint attributes = 0;
                if (desktop.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, relative, out _, out var pidl, ref attributes) == 0)
                    return pidl;
            }
            finally
            {
                Marshal.ReleaseComObject(desktop);
            }
        }
        return SHParseDisplayName(parsingName, IntPtr.Zero, out var fallback, 0, out _) == 0 ? fallback : IntPtr.Zero;
    }

    /// <summary>Binds a handler (IContextMenu, IQueryInfo, …) for one or more items.</summary>
    public static object? BindToHandler(IReadOnlyList<string> parsingNames, Guid riid)
    {
        var pidls = parsingNames.Select(ParsePidl).ToArray();
        try
        {
            if (pidls.Length == 0 || pidls.Any(p => p == IntPtr.Zero))
                return null;
            if (SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var array) != 0)
                return null;
            try
            {
                var bhid = BHID_SFUIObject;
                return array.BindToHandler(IntPtr.Zero, ref bhid, ref riid, out var handler) == 0 ? handler : null;
            }
            finally
            {
                Marshal.ReleaseComObject(array);
            }
        }
        finally
        {
            foreach (var pidl in pidls.Where(p => p != IntPtr.Zero))
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>Runs the default verb exactly as a double-click on the desktop would.</summary>
    public static void Open(string parsingName, IntPtr owner)
    {
        var pidl = ParsePidl(parsingName);
        if (pidl == IntPtr.Zero)
            return;
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_INVOKEIDLIST,
                hwnd = owner,
                lpIDList = pidl,
                nShow = SW_SHOWNORMAL,
            };
            if (!ShellExecuteEx(ref info))
                Debug.WriteLine($"[ShellItems] Open {parsingName}: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>
    /// The info tip Explorer shows when hovering an item (type, size, dates, …).
    /// Must be called on an STA thread.
    /// </summary>
    public static string? GetInfoTip(string parsingName)
    {
        var handler = BindToHandler([parsingName], typeof(IQueryInfo).GUID);
        if (handler is not IQueryInfo info)
            return null;
        try
        {
            if (info.GetInfoTip(0, out var tip) != 0 || tip == IntPtr.Zero)
                return null;
            try { return Marshal.PtrToStringUni(tip); }
            finally { Marshal.FreeCoTaskMem(tip); }
        }
        finally
        {
            Marshal.ReleaseComObject(info);
        }
    }
}
