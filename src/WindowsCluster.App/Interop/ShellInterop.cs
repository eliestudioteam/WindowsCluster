using System.Runtime.InteropServices;

namespace WindowsCluster.App.Interop;

/// <summary>
/// COM interfaces of the Windows Shell. Methods that Windows Cluster never calls are
/// declared as placeholders only to keep the vtable layout correct.
/// </summary>
internal static class ShellInterop
{
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid IID_IPersistFolder2 = new("1AC3D9F0-175C-11D1-95BE-00609797EA4F");
    public static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    public const int SWC_DESKTOP = 8;
    public const int SWFO_NEEDDISPATCH = 1;
    public const int CSIDL_DESKTOP = 0;

    public const uint SVGIO_ALLVIEW = 0x2;
    public const uint SVSI_DESELECT = 0x0;
    public const uint SVSI_SELECT = 0x1;
    public const uint SVSI_DESELECTOTHERS = 0x4;

    public const uint SIGDN_NORMALDISPLAY = 0x00000000;
    public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    [Flags]
    public enum SIIGBF : uint
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
        ScaleUp = 0x100,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx, cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IShellWindows
    {
        void _get_Count();
        void _Item();
        void _NewEnum();
        void _Register();
        void _RegisterPending();
        void _Revoke();
        void _OnNavigate();
        void _OnActivated();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(
            [MarshalAs(UnmanagedType.Struct)] ref object pvarLoc,
            [MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot,
            int swClass,
            out int phwnd,
            int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellBrowser
    {
        void _GetWindow();
        void _ContextSensitiveHelp();
        void _InsertMenusSB();
        void _SetMenuSB();
        void _RemoveMenusSB();
        void _SetStatusTextSB();
        void _EnableModelessSB();
        void _TranslateAcceleratorSB();
        void _BrowseObject();
        void _GetViewStateStream();
        void _GetControlWindow();
        void _SendControlMsg();

        void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFolderView
    {
        void GetCurrentViewMode(out uint pViewMode);
        void SetCurrentViewMode(uint viewMode);
        void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        void Item(int iItemIndex, out IntPtr ppidl);
        void ItemCount(uint uFlags, out int pcItems);
        void _Items();
        void GetSelectionMarkedItem(out int piItem);
        void GetFocusedItem(out int piItem);
        void GetItemPosition(IntPtr pidl, out NativeMethods.POINT ppt);
        void GetSpacing(out NativeMethods.POINT ppt);
        void GetDefaultSpacing(out NativeMethods.POINT ppt);
        [PreserveSig] int GetAutoArrange();
        void SelectItem(int iItem, uint dwFlags);
    }

    [ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFolder2
    {
        void GetClassID(out Guid pClassID);
        void Initialize(IntPtr pidl);
        void GetCurFolder(out IntPtr ppidl);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [DllImport("shell32.dll", PreserveSig = false)]
    public static extern void SHGetNameFromIDList(IntPtr pidl, uint sigdnName, out IntPtr ppszName);

    [DllImport("shell32.dll")]
    public static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    public static string? GetName(IntPtr absolutePidl, uint sigdn)
    {
        try
        {
            SHGetNameFromIDList(absolutePidl, sigdn, out var psz);
            try { return Marshal.PtrToStringUni(psz); }
            finally { Marshal.FreeCoTaskMem(psz); }
        }
        catch (COMException)
        {
            return null;
        }
    }
}
