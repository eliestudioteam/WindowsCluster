using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Layout;

/// <summary>Raised once a move, resize or new Cluster has finished and everything has stopped moving.</summary>
/// <param name="ClusterRects">Final rectangle of every Cluster, in physical screen pixels.</param>
/// <param name="Margin">Clean gap, in physical pixels, that Clusters keep around themselves.</param>
public sealed class LayoutSettledEventArgs(IReadOnlyList<PixelRect> clusterRects, int margin) : EventArgs
{
    public IReadOnlyList<PixelRect> ClusterRects { get; } = clusterRects;
    public int Margin { get; } = margin;
}

/// <summary>
/// Positions Cluster windows relative to each other: magnetic snapping, push-away collisions
/// and a clean margin, for every window registered with it (real Clusters and prototypes alike).
///
/// It listens to WM_WINDOWPOSCHANGING, which Windows sends for every move and resize no matter
/// who causes it (our header drag, the resize border), and adjusts the proposed rectangle before
/// the change happens. Neighbors that have to make room are animated to their new place.
/// </summary>
public sealed class ClusterLayoutManager : IDisposable
{
    private const double MarginDip = 8;
    private const double SnapDip = 14;
    private const double DefaultMinWidthDip = 160;
    private const double DefaultMinHeightDip = 90;
    private const int VK_LBUTTON = 0x01;

    private sealed class Entry(Window window, IntPtr hwnd)
    {
        public Window Window { get; } = window;
        public IntPtr Hwnd { get; } = hwnd;
        public HwndSource? Source { get; set; }
        public HwndSourceHook? Hook { get; set; }
        public bool Ready { get; set; }
    }

    private readonly Dictionary<IntPtr, Entry> _entries = new();
    private readonly Dictionary<IntPtr, PixelRect> _targets = new();
    private readonly DispatcherTimer _animation;
    private readonly DispatcherTimer _settle;
    private int _applying;

    public ClusterLayoutManager()
    {
        _animation = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _animation.Tick += (_, _) => AnimateNeighbors();
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _settle.Tick += (_, _) => OnSettleTick();
    }

    /// <summary>Turns snapping, pushing and the clean margin on or off for every Cluster.</summary>
    public bool Enabled { get; set; } = true;

    public event EventHandler<LayoutSettledEventArgs>? Settled;

    public void Register(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (_entries.ContainsKey(hwnd))
            return;

        var entry = new Entry(window, hwnd) { Source = HwndSource.FromHwnd(hwnd) };
        entry.Hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_WINDOWPOSCHANGING)
                OnPositionChanging(entry, lParam);
            return IntPtr.Zero;
        };
        entry.Source?.AddHook(entry.Hook);
        _entries[hwnd] = entry;
        window.Closed += (_, _) =>
        {
            if (_entries.Remove(hwnd))
                _targets.Remove(hwnd);
        };

        // Until the window has been shown and placed, moves are initial placement, not the user's.
        if (window.IsLoaded)
            MakeReady(entry);
        else
            window.Loaded += (_, _) => MakeReady(entry);
    }

    public void Unregister(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!_entries.Remove(hwnd, out var entry))
            return;
        entry.Source?.RemoveHook(entry.Hook!);
        _targets.Remove(hwnd);
    }

    /// <summary>Rectangles of every visible Cluster, using where moving ones are heading.</summary>
    public IReadOnlyList<PixelRect> CurrentRects() =>
        _entries.Values.Where(e => e.Ready && IsWindowVisible(e.Hwnd)).Select(e => RectOf(e)).ToList();

    public void Dispose()
    {
        _animation.Stop();
        _settle.Stop();
        foreach (var entry in _entries.Values)
            entry.Source?.RemoveHook(entry.Hook!);
        _entries.Clear();
        _targets.Clear();
    }

    // ---------------------------------------------------------------- reacting to moves

    private void MakeReady(Entry entry)
    {
        entry.Ready = true;
        // A Cluster that appears on top of others (or too close to an edge) is settled right away.
        if (Enabled && GetWindowRect(entry.Hwnd, out var rect))
        {
            var current = ToPixelRect(rect);
            ApplyToMover(entry, current, Compute(entry, current, current));
        }
        ScheduleSettle();
    }

    private void OnPositionChanging(Entry entry, IntPtr lParam)
    {
        if (!Enabled || _applying > 0 || !entry.Ready)
            return;
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        var noMove = (pos.flags & SWP_NOMOVE) != 0;
        var noSize = (pos.flags & SWP_NOSIZE) != 0;
        if ((noMove && noSize) || !GetWindowRect(entry.Hwnd, out var rect))
            return;

        var current = ToPixelRect(rect);
        var proposed = PixelRect.FromSize(
            noMove ? current.Left : pos.x, noMove ? current.Top : pos.y,
            noSize ? current.Width : pos.cx, noSize ? current.Height : pos.cy);
        if (proposed == current)
            return;

        var result = Compute(entry, current, proposed);
        var adjusted = result.Layout.Mover;
        if (adjusted != proposed)
        {
            pos.x = adjusted.Left;
            pos.y = adjusted.Top;
            pos.cx = adjusted.Width;
            pos.cy = adjusted.Height;
            if (adjusted.Left != current.Left || adjusted.Top != current.Top)
                pos.flags &= ~SWP_NOMOVE;
            if (adjusted.Width != current.Width || adjusted.Height != current.Height)
                pos.flags &= ~SWP_NOSIZE;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        PushNeighbors(result);
        ScheduleSettle();
    }

    private sealed record Computation(LayoutResult Layout, Dictionary<int, Entry> Neighbors,
        IReadOnlyDictionary<int, PixelRect> Before);

    private Computation Compute(Entry mover, PixelRect current, PixelRect proposed)
    {
        var center = new POINT((proposed.Left + proposed.Right) / 2, (proposed.Top + proposed.Bottom) / 2);
        var scale = GetScaleAt(center);
        var margin = (int)Math.Round(MarginDip * scale);
        var minWidth = (int)Math.Round((mover.Window.MinWidth > 0 ? mover.Window.MinWidth : DefaultMinWidthDip) * scale);
        var minHeight = (int)Math.Round((mover.Window.MinHeight > 0 ? mover.Window.MinHeight : DefaultMinHeightDip) * scale);
        var settings = new LayoutSettings(margin, (int)Math.Round(SnapDip * scale), minWidth, minHeight);

        var work = GetWorkAreaAt(center);
        var bounds = new PixelRect(work.Left + margin, work.Top + margin, work.Right - margin, work.Bottom - margin);
        var monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);

        // Only Clusters on the same monitor can collide with this one.
        var neighbors = new Dictionary<int, Entry>();
        var others = new Dictionary<int, PixelRect>();
        var id = 0;
        foreach (var entry in _entries.Values)
        {
            if (entry == mover || !entry.Ready || !IsWindowVisible(entry.Hwnd))
                continue;
            var rect = RectOf(entry);
            var otherCenter = new POINT((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            if (MonitorFromPoint(otherCenter, MONITOR_DEFAULTTONEAREST) != monitor)
                continue;
            neighbors[id] = entry;
            others[id] = rect;
            id++;
        }

        var sizeChanged = proposed.Width != current.Width || proposed.Height != current.Height;
        var edges = Edges.All;
        if (sizeChanged)
        {
            edges = Edges.None;
            if (proposed.Left != current.Left) edges |= Edges.Left;
            if (proposed.Right != current.Right) edges |= Edges.Right;
            if (proposed.Top != current.Top) edges |= Edges.Top;
            if (proposed.Bottom != current.Bottom) edges |= Edges.Bottom;
        }
        var motion = sizeChanged ? new PixelPoint(0, 0) : new PixelPoint(proposed.Left - current.Left, proposed.Top - current.Top);

        var snapped = ClusterGeometry.Snap(proposed, edges, others.Values.ToList(), bounds, settings);
        var layout = ClusterGeometry.Resolve(snapped, edges, motion, others, bounds, settings);
        Log($"mover {F(current)} prop {F(proposed)} snap {F(snapped)} -> {F(layout.Mover)} yield={layout.Yielded} | " +
            string.Join("; ", others.Select(kv => $"#{kv.Key} {F(kv.Value)}->{F(layout.Others[kv.Key])}")));
        return new Computation(layout, neighbors, others);
    }

    /// <summary>Applies the mover's own correction (used when a window is created, not dragged).</summary>
    private void ApplyToMover(Entry mover, PixelRect current, Computation result)
    {
        var adjusted = result.Layout.Mover;
        if (adjusted != current)
        {
            _applying++;
            try
            {
                SetWindowPos(mover.Hwnd, IntPtr.Zero, adjusted.Left, adjusted.Top, adjusted.Width, adjusted.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
            finally
            {
                _applying--;
            }
        }
        PushNeighbors(result);
    }

    private void PushNeighbors(Computation result)
    {
        var any = false;
        foreach (var (id, entry) in result.Neighbors)
        {
            var target = result.Layout.Others[id];
            if (target == result.Before[id])
                continue;
            _targets[entry.Hwnd] = target;
            any = true;
        }
        if (any && !_animation.IsEnabled)
            _animation.Start();
    }

    // ---------------------------------------------------------------- animation

    private void AnimateNeighbors()
    {
        foreach (var (hwnd, target) in _targets.ToList())
        {
            if (!_entries.ContainsKey(hwnd) || !GetWindowRect(hwnd, out var rect))
            {
                _targets.Remove(hwnd);
                continue;
            }
            var current = ToPixelRect(rect);
            if (current == target)
            {
                _targets.Remove(hwnd);
                continue;
            }

            var next = new PixelRect(
                current.Left + Step(target.Left - current.Left),
                current.Top + Step(target.Top - current.Top),
                current.Right + Step(target.Right - current.Right),
                current.Bottom + Step(target.Bottom - current.Bottom));
            _applying++;
            try
            {
                SetWindowPos(hwnd, IntPtr.Zero, next.Left, next.Top, next.Width, next.Height, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            finally
            {
                _applying--;
            }
        }
        if (_targets.Count == 0)
            _animation.Stop();
    }

    /// <summary>Ease-out: covers about a third of the remaining distance per frame, at least one pixel.</summary>
    private static int Step(int remaining)
    {
        if (remaining == 0)
            return 0;
        var step = (int)Math.Round(remaining * 0.35);
        return step != 0 ? step : Math.Sign(remaining);
    }

    // ---------------------------------------------------------------- settling

    private void ScheduleSettle()
    {
        _settle.Stop();
        _settle.Start();
    }

    private void OnSettleTick()
    {
        // Wait until the mouse button is up and every neighbor has reached its place.
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || _targets.Count > 0)
            return;
        _settle.Stop();
        var margin = (int)Math.Round(MarginDip * GetScaleAt(new POINT(0, 0)));
        Settled?.Invoke(this, new LayoutSettledEventArgs(CurrentRects(), margin));
    }

    // ---------------------------------------------------------------- helpers

    private PixelRect RectOf(Entry entry) =>
        _targets.TryGetValue(entry.Hwnd, out var target)
            ? target
            : GetWindowRect(entry.Hwnd, out var rect) ? ToPixelRect(rect) : default;

    private static PixelRect ToPixelRect(RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Debug aid: set WC_LAYOUT_LOG to a file path to record every layout decision.
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("WC_LAYOUT_LOG");

    private static string F(PixelRect r) => $"{r.Left},{r.Top},{r.Right},{r.Bottom}";

    private static void Log(string line)
    {
        if (LogPath is null)
            return;
        try { System.IO.File.AppendAllText(LogPath, line + Environment.NewLine); }
        catch (System.IO.IOException) { }
    }
}
