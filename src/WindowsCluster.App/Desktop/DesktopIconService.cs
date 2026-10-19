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
public sealed record DesktopIcon(int Index, string ParsingName, string DisplayName, RECT ScreenBounds, POINT ClientPosition)
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

    /// <summary>
    /// True when an icon (not empty space) is under <paramref name="screenPoint"/>.
    /// Uses the list view's accessibility object, which reports the exact item hit;
    /// the grid cells from IFolderView are larger than the icons and would misreport.
    /// </summary>
    public static bool IsOverIcon(POINT screenPoint)
    {
        if (AccessibleObjectFromPoint(screenPoint, out var accessible, out var child) != 0)
            return false;
        if (accessible is not null)
            Marshal.ReleaseComObject(accessible);
        return child is int id && id != 0; // CHILDID_SELF (0) means the list itself
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(POINT pt,
        [MarshalAs(UnmanagedType.Interface)] out object? ppacc, out object? pvarChild);

    /// <summary>Desktop icon size in logical pixels (32 small, 48 medium, 96 large), or null if unknown.</summary>
    public int? GetIconSize()
    {
        try
        {
            if (GetView() is IFolderView2 view)
            {
                view.GetViewModeAndIconSize(out _, out var size);
                return size;
            }
        }
        catch (COMException)
        {
            _view = null;
        }
        return null;
    }

    /// <summary>
    /// Moves desktop icons that are covered by one of <paramref name="blocked"/> (screen pixels) to the
    /// nearest free cell of the icon grid.
    /// Does nothing under "Auto arrange icons", where Explorer owns every position.
    /// </summary>
    /// <returns>How many icons were moved.</returns>
    public int RelocateTrapped(IReadOnlyList<Layout.PixelRect> blocked)
    {
        var view = GetView();
        if (view is null || blocked.Count == 0)
            return 0;
        try
        {
            if (view.GetAutoArrange() == 0) // S_OK: auto-arrange is on
                return 0;

            var icons = ReadIcons(view);
            if (icons.Count == 0)
                return 0;
            view.GetSpacing(out var spacing);
            if (spacing.X <= 0 || spacing.Y <= 0)
                return 0;

            var listView = _host.Topology.IconListView;
            var listOrigin = new POINT(0, 0);
            ClientToScreen(listView, ref listOrigin);
            var first = icons[0].ClientPosition;
            var origin = new Layout.PixelPoint(
                listOrigin.X + (((first.X % spacing.X) + spacing.X) % spacing.X),
                listOrigin.Y + (((first.Y % spacing.Y) + spacing.Y) % spacing.Y));

            var moved = 0;
            // Icons live on one monitor's work area each; group them so the grid search stays on that monitor.
            foreach (var group in icons.GroupBy(i => MonitorFromPoint(new POINT(i.ScreenBounds.Left, i.ScreenBounds.Top), MONITOR_DEFAULTTONEAREST)))
            {
                var any = group.First();
                var work = GetWorkAreaAt(new POINT(any.ScreenBounds.Left, any.ScreenBounds.Top));
                var area = new Layout.PixelRect(work.Left, work.Top, work.Right, work.Bottom);
                var input = group.Select(i => new Layout.IconGrid.Icon(
                    i.Index,
                    new Layout.PixelPoint(listOrigin.X + i.ClientPosition.X, listOrigin.Y + i.ClientPosition.Y),
                    new Layout.PixelRect(i.ScreenBounds.Left, i.ScreenBounds.Top, i.ScreenBounds.Right, i.ScreenBounds.Bottom))).ToList();

                var moves = Layout.IconGrid.Relocate(input, new Layout.PixelPoint(spacing.X, spacing.Y), origin, area, blocked);
                if (moves.Count == 0)
                    continue;

                foreach (var (index, cell) in moves)
                {
                    if (SetItemPosition(listView, index, cell.X - listOrigin.X, cell.Y - listOrigin.Y))
                        moved++;
                }
            }
            return moved;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[DesktopIconService] RelocateTrapped: {ex.Message}");
            _view = null;
            return 0;
        }
    }

    private const uint LVM_SETITEMPOSITION = 0x100F;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// Moves one desktop icon with the list-view message LVM_SETITEMPOSITION (client pixels). The
    /// documented IFolderView.SelectAndPositionItems reports success but does nothing on the desktop
    /// of Windows 11 build 26300, which is why the list view's own message is used. The send has a
    /// timeout so an unresponsive Explorer can never hang this app.
    /// </summary>
    private static bool SetItemPosition(IntPtr listView, int index, int x, int y) =>
        listView != IntPtr.Zero &&
        SendMessageTimeout(listView, LVM_SETITEMPOSITION, (IntPtr)index, (IntPtr)((y << 16) | (x & 0xFFFF)),
            SMTO_ABORTIFHUNG, 1000, out _) != IntPtr.Zero;

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
            var drawn = ReadDrawnBounds(listView);
            var icons = new List<DesktopIcon>(count);
            for (var i = 0; i < count; i++)
            {
                view.Item(i, out var childPidl);
                try
                {
                    view.GetItemPosition(childPidl, out var pos);
                    var clientPosition = pos;
                    ClientToScreen(listView, ref pos);

                    var absolute = ILCombine(folderPidl, childPidl);
                    try
                    {
                        var parsing = GetName(absolute, SIGDN_DESKTOPABSOLUTEPARSING);
                        var display = GetName(absolute, SIGDN_NORMALDISPLAY) ?? parsing;
                        if (parsing is null)
                            continue;
                        // Prefer the rectangle Explorer actually draws (icon + label); the grid
                        // cell from IFolderView is wider and caught neighbouring icons.
                        var bounds = MatchDrawnBounds(drawn, i, display!)
                            ?? new RECT { Left = pos.X, Top = pos.Y, Right = pos.X + spacing.X, Bottom = pos.Y + spacing.Y };
                        icons.Add(new DesktopIcon(i, parsing, display!, bounds, clientPosition));
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

    private static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private const uint OBJID_CLIENT = 0xFFFFFFFC;

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint dwObjectId, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppvObject);

    /// <summary>
    /// Screen rectangles of every list item as Explorer draws them, read through the list
    /// view's accessibility object (child N is list item N−1). Same order as IFolderView.
    /// </summary>
    private static List<(string Name, RECT Bounds)> ReadDrawnBounds(IntPtr listView)
    {
        var result = new List<(string, RECT)>();
        var iid = IID_IAccessible;
        if (listView == IntPtr.Zero ||
            AccessibleObjectFromWindow(listView, OBJID_CLIENT, ref iid, out var obj) != 0 ||
            obj is not Accessibility.IAccessible accessible)
            return result;
        try
        {
            var count = accessible.accChildCount;
            for (var child = 1; child <= count; child++)
            {
                accessible.accLocation(out var left, out var top, out var width, out var height, child);
                var name = accessible.get_accName(child) as string ?? "";
                result.Add((name, new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height }));
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            Debug.WriteLine($"[DesktopIconService] Accessible bounds: {ex.Message}");
        }
        finally
        {
            Marshal.ReleaseComObject(accessible);
        }
        return result;
    }

    private static RECT? MatchDrawnBounds(List<(string Name, RECT Bounds)> drawn, int index, string displayName)
    {
        if (index < drawn.Count && drawn[index].Name == displayName)
            return drawn[index].Bounds;
        foreach (var (name, bounds) in drawn)
        {
            if (name == displayName)
                return bounds;
        }
        return null;
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
