using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Explorer;

/// <summary>
/// Hosts Windows' own folder view (the one File Explorer uses) inside a WPF window through
/// <c>IExplorerBrowser</c>, a documented COM control meant for exactly this. Icons, thumbnails,
/// selection, in-place rename, tooltips, drag &amp; drop and the shell context menu are all
/// drawn and handled by Windows, not by this app.
/// </summary>
public sealed class ExplorerBrowserHost : HwndHost
{
    private static readonly Guid CLSID_ExplorerBrowser = new("71F96385-DDD6-48D3-A0C1-AE06E8B055FB");

    private const uint FVM_ICON = 1;

    // FOLDERSETTINGS.fFlags
    private const uint FWF_AUTOARRANGE = 0x1;
    private const uint FWF_SNAPTOGRID = 0x4;
    private const uint FWF_TRANSPARENT = 0x100;
    private const uint FWF_NOCLIENTEDGE = 0x200;
    private const uint FWF_NOWEBVIEW = 0x10000;
    private const uint FWF_NOHEADERINALLVIEWS = 0x1000000;
    private const uint FWF_SHOWSELALWAYS = 0x2000;

    // EXPLORER_BROWSER_OPTIONS
    private const uint EBO_NOBORDER = 0x40;
    private const uint EBO_NOTRAVELLOG = 0x8;
    private const uint EBO_NOPERSISTVIEWSTATE = 0x80;

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;

    private IExplorerBrowser? _browser;
    private IntPtr _container;

    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x2;
    private double _viewOpacity = 1.0;

    /// <summary>
    /// How opaque the whole folder view is (0.2–1). Lower values let the Cluster's backdrop show
    /// through the view, icons included.
    /// </summary>
    public double ViewOpacity
    {
        get => _viewOpacity;
        set
        {
            _viewOpacity = Math.Clamp(value, 0.2, 1.0);
            ApplyViewOpacity();
        }
    }

    private void ApplyViewOpacity()
    {
        if (_container != IntPtr.Zero)
            SetLayeredWindowAttributes(_container, 0, (byte)Math.Round(_viewOpacity * 255), LWA_ALPHA);
    }

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);

    /// <summary>Folder shown in the view.</summary>
    public string FolderPath { get; }

    public ExplorerBrowserHost(string folderPath) => FolderPath = folderPath;

    /// <summary>Raised after the view exists and was navigated to <see cref="FolderPath"/>.</summary>
    public event EventHandler? ViewReady;

    /// <summary>Window handle of the shell view (SHELLDLL_DefView) once navigation finished, else zero.</summary>
    public IntPtr ShellViewHandle { get; private set; }

    /// <summary>Window handle of the icon list (SysListView32), else zero.</summary>
    public IntPtr ListViewHandle { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // Only a plain child window is created here. WPF calls this while the dispatcher is
        // suspended (during layout), and the shell view pumps messages as soon as it is created,
        // which crashes WPF. The browser itself is created right after, from the dispatcher.
        // WS_EX_LAYERED on a child window (supported since Windows 8) lets the whole view be
        // blended with what is behind it, which is how the Cluster background becomes translucent.
        _container = CreateWindowEx(WS_EX_LAYERED, "STATIC", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ApplyViewOpacity();
        Dispatcher.BeginInvoke(new Action(CreateBrowser), System.Windows.Threading.DispatcherPriority.Background);
        return new HandleRef(this, _container);
    }

    private void CreateBrowser()
    {
        if (_browser is not null || _container == IntPtr.Zero || !IsWindow(_container))
            return;

        var type = Type.GetTypeFromCLSID(CLSID_ExplorerBrowser)
                   ?? throw new InvalidOperationException("ExplorerBrowser no está registrado.");
        _browser = (IExplorerBrowser)Activator.CreateInstance(type)!;

        var settings = new FOLDERSETTINGS
        {
            ViewMode = FVM_ICON,
            fFlags = FWF_AUTOARRANGE | FWF_SNAPTOGRID | FWF_NOWEBVIEW | FWF_NOHEADERINALLVIEWS
                     | FWF_NOCLIENTEDGE | FWF_SHOWSELALWAYS | FWF_TRANSPARENT,
        };
        GetClientRect(_container, out var rect);
        _browser.Initialize(_container, ref rect, ref settings);
        _browser.SetOptions(EBO_NOBORDER | EBO_NOTRAVELLOG | EBO_NOPERSISTVIEWSTATE);

        var iid = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName(FolderPath, IntPtr.Zero, ref iid, out var item);
        try
        {
            _browser.BrowseToObject(item, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
        FindViewWindows();
    }

    /// <summary>
    /// The folder view handles shortcuts such as F2 (rename), Delete and Ctrl+C/X/V itself, but only
    /// when the host forwards key messages to it through IInputObject.TranslateAcceleratorIO.
    /// </summary>
    protected override bool TranslateAcceleratorCore(ref System.Windows.Interop.MSG msg, System.Windows.Input.ModifierKeys modifiers)
    {
        if (_browser is IInputObject input)
        {
            var native = new NativeMsg
            {
                hwnd = msg.hwnd,
                message = (uint)msg.message,
                wParam = msg.wParam,
                lParam = msg.lParam,
                time = (uint)msg.time,
                pt_x = msg.pt_x,
                pt_y = msg.pt_y,
            };
            try
            {
                if (input.TranslateAcceleratorIO(ref native) == 0) // S_OK: the view handled the key
                    return true;
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"[ExplorerBrowserHost] TranslateAcceleratorIO: {ex.Message}");
            }
        }
        return base.TranslateAcceleratorCore(ref msg, modifiers);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        try
        {
            _browser?.Destroy();
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[ExplorerBrowserHost] Destroy: {ex.Message}");
        }
        if (_browser is not null)
            Marshal.ReleaseComObject(_browser);
        _browser = null;
        DestroyWindow(hwnd.Handle);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_resizeQueued)
            return;
        // SetRect also pumps messages, so it must not run inside WPF's layout pass.
        _resizeQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _resizeQueued = false;
            if (_browser is null || _container == IntPtr.Zero || !IsWindow(_container))
                return;
            GetClientRect(_container, out var client);
            var hdwp = IntPtr.Zero;
            _browser.SetRect(ref hdwp, client);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool _resizeQueued;

    /// <summary>Finds the shell view and list view the browser created and notifies listeners.</summary>
    private void FindViewWindows()
    {
        var attempts = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            attempts++;
            var shellView = FindDescendant(_container, "SHELLDLL_DefView");
            var list = shellView == IntPtr.Zero ? IntPtr.Zero : FindDescendant(shellView, "SysListView32");
            if (list == IntPtr.Zero && attempts < 40)
                return;
            timer.Stop();
            ShellViewHandle = shellView;
            ListViewHandle = list;
            ViewReady?.Invoke(this, EventArgs.Empty);
        };
        timer.Start();
    }

    /// <summary>Depth-first search of <paramref name="parent"/>'s descendants for a window class.</summary>
    public static IntPtr FindDescendant(IntPtr parent, string className)
    {
        if (parent == IntPtr.Zero)
            return IntPtr.Zero;
        var found = IntPtr.Zero;
        EnumChildWindows(parent, (hwnd, _) =>
        {
            if (GetClassName(hwnd) == className)
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---------------------------------------------------------------- COM + Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct FOLDERSETTINGS
    {
        public uint ViewMode;
        public uint fFlags;
    }

    [ComImport, Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExplorerBrowser
    {
        void Initialize(IntPtr hwndParent, ref RECT prc, ref FOLDERSETTINGS pfs);
        void Destroy();
        void SetRect(ref IntPtr phdwp, RECT rcBrowser);
        void SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string pszPropertyBag);
        void SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string pszEmptyText);
        void SetFolderSettings(ref FOLDERSETTINGS pfs);
        void Advise(IntPtr psbe, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint dwFlag);
        void GetOptions(out uint pdwFlag);
        void BrowseToIDList(IntPtr pidl, uint uFlags);
        void BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint uFlags);
        void FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, int dwFlags);
        void RemoveAll();
        void GetCurrentView(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [ComImport, Guid("68284FAA-6A48-11D0-8C78-00C04FD918B4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInputObject
    {
        [PreserveSig] int UIActivateIO([MarshalAs(UnmanagedType.Bool)] bool fActivate, IntPtr pMsg);
        [PreserveSig] int HasFocusIO();
        [PreserveSig] int TranslateAcceleratorIO(ref NativeMsg pMsg);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);
}
