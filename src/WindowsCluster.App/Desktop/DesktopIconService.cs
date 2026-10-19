using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsCluster.App.Interop;
using static WindowsCluster.App.Interop.NativeMethods;
using static WindowsCluster.App.Interop.ShellInterop;

namespace WindowsCluster.App.Desktop;

/// <summary>An icon on the Explorer desktop and where it is drawn.</summary>
/// <param name="ParsingName">Absolute parsing name: a file path or a "::{CLSID}" shell location.</param>
/// <param name="DisplayName">Name shown under the icon.</param>
/// <param name="ScreenBounds">Item cell in physical screen pixels.</param>
public sealed record DesktopIcon(int Index, string ParsingName, string DisplayName, RECT ScreenBounds)
{
    public bool Contains(POINT p) =>
        p.X >= ScreenBounds.Left && p.X < ScreenBounds.Right &&
        p.Y >= ScreenBounds.Top && p.Y < ScreenBounds.Bottom;

    public bool IntersectsWith(RECT r) =>
        ScreenBounds.Left < r.Right && r.Left < ScreenBounds.Right &&
        ScreenBounds.Top < r.Bottom && r.Top < ScreenBounds.Bottom;
}

/// <summary>
/// Reads and selects the real desktop icons through Explorer's own view (IFolderView),
/// the documented COM route. No memory is read from or written to explorer.exe.
/// Must be used from the UI (STA) thread.
/// </summary>
public sealed class DesktopIconService
{
    private readonly DesktopHost _host;
    private IFolderView? _view;

    public DesktopIconService(DesktopHost host) => _host = host;

    /// <summary>Returns every icon currently on the desktop.</summary>
    public IReadOnlyList<DesktopIcon> GetIcons()
    {
        var view = GetView();
        if (view is null)
            return [];

        try
        {
            return ReadIcons(view);
        }
        catch (COMException ex)
        {
            // Explorer restarted or the view was recreated; retry once with a fresh view.
            Debug.WriteLine($"[DesktopIconService] {ex.Message}");
            _view = null;
            view = GetView();
            return view is null ? [] : ReadIcons(view);
        }
    }

    public DesktopIcon? HitTest(POINT screenPoint) =>
        GetIcons().FirstOrDefault(i => i.Contains(screenPoint));

    /// <summary>Selects exactly <paramref name="icons"/> in Explorer, as its own rubber band would.</summary>
    public void Select(IReadOnlyList<DesktopIcon> icons)
    {
        var view = GetView();
        if (view is null)
            return;
        try
        {
            if (icons.Count == 0)
            {
                view.ItemCount(SVGIO_ALLVIEW, out var count);
                for (var i = 0; i < count; i++)
                    view.SelectItem(i, SVSI_DESELECT);
                return;
            }
            view.SelectItem(icons[0].Index, SVSI_SELECT | SVSI_DESELECTOTHERS);
            foreach (var icon in icons.Skip(1))
                view.SelectItem(icon.Index, SVSI_SELECT);
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[DesktopIconService] Select: {ex.Message}");
            _view = null;
        }
    }

    private IReadOnlyList<DesktopIcon> ReadIcons(IFolderView view)
    {
        var listView = _host.Topology.IconListView;
        var folderPidl = GetFolderPidl(view);
        if (folderPidl == IntPtr.Zero)
            return [];

        try
        {
            view.GetSpacing(out var spacing);
            view.ItemCount(SVGIO_ALLVIEW, out var count);
            var icons = new List<DesktopIcon>(count);
            for (var i = 0; i < count; i++)
            {
                view.Item(i, out var childPidl);
                try
                {
                    view.GetItemPosition(childPidl, out var pos);
                    ClientToScreen(listView, ref pos);

                    var absolute = ILCombine(folderPidl, childPidl);
                    try
                    {
                        var parsing = GetName(absolute, SIGDN_DESKTOPABSOLUTEPARSING);
                        var display = GetName(absolute, SIGDN_NORMALDISPLAY) ?? parsing;
                        if (parsing is null)
                            continue;
                        var bounds = new RECT { Left = pos.X, Top = pos.Y, Right = pos.X + spacing.X, Bottom = pos.Y + spacing.Y };
                        icons.Add(new DesktopIcon(i, parsing, display!, bounds));
                    }
                    finally
                    {
                        ILFree(absolute);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(childPidl);
                }
            }
            return icons;
        }
        finally
        {
            Marshal.FreeCoTaskMem(folderPidl);
        }
    }

    private static IntPtr GetFolderPidl(IFolderView view)
    {
        var iid = IID_IPersistFolder2;
        view.GetFolder(ref iid, out var folder);
        try
        {
            ((IPersistFolder2)folder).GetCurFolder(out var pidl);
            return pidl;
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }
    }

    /// <summary>
    /// Desktop view lookup: ShellWindows.FindWindowSW(SWC_DESKTOP) → top-level browser →
    /// active shell view → IFolderView.
    /// </summary>
    private IFolderView? GetView()
    {
        if (_view is not null)
            return _view;
        try
        {
            var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows)!)!;
            object location = CSIDL_DESKTOP;
            object root = null!; // VT_EMPTY
            var dispatch = shellWindows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);

            var sid = SID_STopLevelBrowser;
            var iid = IID_IShellBrowser;
            var hr = ((ShellInterop.IServiceProvider)dispatch).QueryService(ref sid, ref iid, out var browser);
            if (hr != 0)
                return null;

            ((IShellBrowser)browser).QueryActiveShellView(out var shellView);
            _view = (IFolderView)shellView;
            return _view;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Debug.WriteLine($"[DesktopIconService] GetView: {ex.Message}");
            return null;
        }
    }
}
