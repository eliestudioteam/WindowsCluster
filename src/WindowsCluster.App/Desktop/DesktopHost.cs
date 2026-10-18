using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Desktop;

/// <summary>
/// Locates the Explorer desktop windows (Progman / WorkerW / SHELLDLL_DefView) and
/// attaches WPF windows to them. Also watches for Explorer restarts.
/// </summary>
public sealed class DesktopHost : IDisposable
{
    private readonly Dictionary<IntPtr, Attachment> _attachments = new();
    private readonly uint _taskbarCreatedMessage;
    private readonly HwndSource _shellWatcher;
    private DispatcherTimer? _restartTimer;

    public DesktopHost()
    {
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        // Hidden, unowned top-level window: "TaskbarCreated" is broadcast only to
        // top-level windows, so a message-only (HWND_MESSAGE) window would miss it.
        var parameters = new HwndSourceParameters("WindowsCluster.ShellWatcher")
        {
            WindowStyle = unchecked((int)WS_POPUP),
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        };
        _shellWatcher = new HwndSource(parameters);
        _shellWatcher.AddHook(ShellWatcherProc);

        Topology = Discover();
    }

    /// <summary>Latest known desktop window hierarchy.</summary>
    public DesktopTopology Topology { get; private set; }

    /// <summary>
    /// Raised once Explorer has restarted and the new desktop windows are available.
    /// Windows attached before the restart were destroyed together with their old owner/parent.
    /// </summary>
    public event EventHandler<DesktopTopology>? ShellRestarted;

    /// <summary>Re-reads the desktop hierarchy, asking Progman to create the WorkerW if needed.</summary>
    public DesktopTopology Refresh() => Topology = Discover();

    /// <summary>Attaches <paramref name="window"/> to the desktop using <paramref name="mode"/>.</summary>
    public void Attach(Window window, EmbedMode mode)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (_attachments.ContainsKey(hwnd))
            Detach(window);

        if (!IsAlive(Topology))
            Refresh();
        if (!Topology.IsValid)
            throw new InvalidOperationException("No se encontró la ventana del escritorio de Explorer.");

        var attachment = new Attachment(
            window,
            hwnd,
            mode,
            GetWindowLongPtr(hwnd, GWL_STYLE),
            GetWindowLongPtr(hwnd, GWL_EXSTYLE));

        // Hide from Alt+Tab and the taskbar in both modes.
        SetWindowLongPtr(hwnd, GWL_EXSTYLE,
            (IntPtr)(((long)attachment.OriginalExStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));

        switch (mode)
        {
            case EmbedMode.DesktopLayer:
                AttachToDesktopLayer(attachment);
                break;
            case EmbedMode.WallpaperLayer:
                if (!Topology.HasWallpaperLayer)
                    throw new InvalidOperationException("Explorer no creó la capa WorkerW del fondo de pantalla.");
                AttachToWallpaperLayer(attachment);
                break;
        }

        _attachments[hwnd] = attachment;
        window.Closed += OnAttachedWindowClosed;
    }

    /// <summary>Returns <paramref name="window"/> to a normal top-level window.</summary>
    public void Detach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!_attachments.Remove(hwnd, out var attachment))
            return;

        window.Closed -= OnAttachedWindowClosed;
        attachment.Source?.RemoveHook(attachment.Hook!);
        if (!IsWindow(hwnd))
            return;

        GetWindowRect(hwnd, out var rect);
        SetParent(hwnd, IntPtr.Zero);
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, IntPtr.Zero);
        SetWindowLongPtr(hwnd, GWL_STYLE, attachment.OriginalStyle);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, attachment.OriginalExStyle);
        SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public EmbedMode? GetMode(Window window) =>
        _attachments.TryGetValue(new WindowInteropHelper(window).Handle, out var a) ? a.Mode : null;

    public void Dispose()
    {
        _restartTimer?.Stop();
        foreach (var attachment in _attachments.Values.ToList())
            Detach(attachment.Window);
        _shellWatcher.RemoveHook(ShellWatcherProc);
        _shellWatcher.Dispose();
    }

    // ---------------------------------------------------------------- discovery

    private static DesktopTopology Discover()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return new DesktopTopology(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, false);

        // Ask Explorer to create the WorkerW behind the icons. (0,0) is the classic form;
        // (0xD,1) is also sent because newer builds respond to it. Both are harmless if
        // the WorkerW already exists.
        SendMessageTimeout(progman, WM_PROGMAN_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);
        SendMessageTimeout(progman, WM_PROGMAN_SPAWN_WORKER, (IntPtr)0xD, (IntPtr)0x1, SMTO_NORMAL, 1000, out _);

        // Windows 11 24H2+: SHELLDLL_DefView and the wallpaper WorkerW are both children of Progman.
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        var childWorkerW = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (defView != IntPtr.Zero && childWorkerW != IntPtr.Zero)
            return Build(progman, progman, defView, childWorkerW, raised: true);

        // Classic layout: DefView lives in Progman or in a top-level WorkerW, and the wallpaper
        // WorkerW is the next top-level WorkerW after that host in Z-order.
        IntPtr defViewHost = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            var view = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero)
                return true;
            defViewHost = top;
            defView = view;
            return false;
        }, IntPtr.Zero);

        var wallpaperWorkerW = defViewHost != IntPtr.Zero
            ? FindWindowEx(IntPtr.Zero, defViewHost, "WorkerW", null)
            : IntPtr.Zero;
        return Build(progman, defViewHost, defView, wallpaperWorkerW, raised: false);
    }

    private static DesktopTopology Build(IntPtr progman, IntPtr host, IntPtr defView, IntPtr workerW, bool raised)
    {
        var listView = defView != IntPtr.Zero
            ? FindWindowEx(defView, IntPtr.Zero, "SysListView32", null)
            : IntPtr.Zero;
        var topology = new DesktopTopology(progman, host, defView, listView, workerW, raised);
        Debug.WriteLine($"[DesktopHost] {topology}");
        return topology;
    }

    private static bool IsAlive(DesktopTopology t) =>
        t.IsValid && IsWindow(t.Progman) && IsWindow(t.DefView);

    // ---------------------------------------------------------------- attach modes

    private void AttachToDesktopLayer(Attachment attachment)
    {
        // An owned window always stays above its owner. Owning it by the desktop host keeps it
        // just above the icons and is meant to keep it visible on "Show desktop" (Win+D).
        SetWindowLongPtr(attachment.Hwnd, GWLP_HWNDPARENT, Topology.DefViewHost);

        attachment.Source = HwndSource.FromHwnd(attachment.Hwnd);
        attachment.Hook = KeepAtBottomHook;
        attachment.Source.AddHook(attachment.Hook);

        SetWindowPos(attachment.Hwnd, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void AttachToWallpaperLayer(Attachment attachment)
    {
        var hwnd = attachment.Hwnd;
        var workerW = Topology.WallpaperWorkerW;

        GetWindowRect(hwnd, out var rect);

        var style = ((long)attachment.OriginalStyle & ~WS_POPUP) | WS_CHILD;
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);
        SetParent(hwnd, workerW);

        // Child coordinates are relative to the WorkerW client area (physical pixels).
        MapWindowPoints(IntPtr.Zero, workerW, ref rect, 2);
        SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    /// <summary>Forces every Z-order change of a desktop-layer window to the bottom.</summary>
    private static IntPtr KeepAtBottomHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- explorer restarts

    private IntPtr ShellWatcherProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
            BeginWaitForDesktop();
        return IntPtr.Zero;
    }

    /// <summary>
    /// The taskbar can appear before the desktop windows exist, so poll briefly
    /// until the new Progman/DefView pair is available.
    /// </summary>
    private void BeginWaitForDesktop()
    {
        _restartTimer?.Stop();
        var attempts = 0;
        _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _restartTimer.Tick += (_, _) =>
        {
            attempts++;
            var topology = Refresh();
            if (!topology.IsValid && attempts < 20)
                return;

            _restartTimer!.Stop();
            if (topology.IsValid)
                ShellRestarted?.Invoke(this, topology);
        };
        _restartTimer.Start();
    }

    private void OnAttachedWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;
        window.Closed -= OnAttachedWindowClosed;
        foreach (var (hwnd, attachment) in _attachments.ToList())
        {
            if (attachment.Window == window)
                _attachments.Remove(hwnd);
        }
    }

    private sealed class Attachment(Window window, IntPtr hwnd, EmbedMode mode, IntPtr originalStyle, IntPtr originalExStyle)
    {
        public Window Window { get; } = window;
        public IntPtr Hwnd { get; } = hwnd;
        public EmbedMode Mode { get; } = mode;
        public IntPtr OriginalStyle { get; } = originalStyle;
        public IntPtr OriginalExStyle { get; } = originalExStyle;
        public HwndSource? Source { get; set; }
        public HwndSourceHook? Hook { get; set; }
    }
}
