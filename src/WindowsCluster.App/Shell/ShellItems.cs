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
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        void _EnumObjects();
        void _BindToObject();
        void _BindToStorage();
        void _CompareIDs();
        void _CreateViewObject();
        void _GetAttributesOf();

        // In COM interop an array parameter defaults to a SAFEARRAY; the shell expects a
        // plain C array of child PIDLs, so LPArray is required (without it: access violation).
        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv, out IntPtr ppidlLast);

    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");

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

    /// <summary>
    /// Gets a UI object (IContextMenu, IQueryInfo, …) for one or more items through their
    /// parent folder's GetUIObjectOf, the route Explorer's own views use. Items whose parent
    /// differs from the first item's are skipped. Returns null on failure.
    /// </summary>
    public static object? BindToHandler(IReadOnlyList<string> parsingNames, Guid riid, IntPtr owner = default)
    {
        var pidls = parsingNames.Select(ParsePidl).Where(p => p != IntPtr.Zero).ToArray();
        object? parent = null;
        try
        {
            if (pidls.Length == 0)
                return null;

            var children = new List<IntPtr>(pidls.Length);
            byte[]? parentKey = null;
            foreach (var pidl in pidls)
            {
                var iid = IID_IShellFolder;
                if (SHBindToParent(pidl, ref iid, out var folder, out var child) != 0)
                    continue;
                var key = ParentKey(pidl, child);
                if (parent is null)
                {
                    parent = folder;
                    parentKey = key;
                }
                else
                {
                    Marshal.ReleaseComObject(folder);
                    if (!key.AsSpan().SequenceEqual(parentKey))
                        continue;
                }
                children.Add(child); // points inside pidl, which stays alive until finally
            }
            if (parent is not IShellFolder shellFolder || children.Count == 0)
                return null;

            return shellFolder.GetUIObjectOf(owner, (uint)children.Count, children.ToArray(), ref riid, IntPtr.Zero, out var result) == 0
                ? result
                : null;
        }
        finally
        {
            if (parent is not null)
                Marshal.ReleaseComObject(parent);
            foreach (var pidl in pidls)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>Bytes of the parent part of an absolute PIDL (everything before the last item).</summary>
    private static byte[] ParentKey(IntPtr pidl, IntPtr last)
    {
        var length = (int)(last.ToInt64() - pidl.ToInt64());
        var bytes = new byte[length];
        Marshal.Copy(pidl, bytes, 0, length);
        return bytes;
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
