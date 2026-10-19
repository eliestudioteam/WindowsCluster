using System.Runtime.InteropServices;
using System.Windows.Threading;
using WindowsCluster.App.Desktop;
using WindowsCluster.App.Views;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Input;

/// <summary>Result of a right-button drag over empty desktop space.</summary>
/// <param name="Bounds">Dragged rectangle in physical screen pixels.</param>
/// <param name="Icons">Desktop icons inside the rectangle (now selected in Explorer).</param>
public sealed record DesktopDragResult(RECT Bounds, IReadOnlyList<DesktopIcon> Icons);

/// <summary>
/// Detects "right-press on empty desktop space, drag, release".
///
/// The right button-down is held back (swallowed) until we know what is under it:
///   • over an icon → it is replayed to Explorer, so icon menus and right-drag copy/move keep working;
///   • over empty space and released without dragging → down+up are replayed, so Explorer
///     shows its normal desktop menu;
///   • over empty space and dragged → the release is swallowed too and
///     <see cref="DragCompleted"/> is raised instead of Explorer's menu.
/// Mouse moves are never swallowed (that would freeze the cursor).
/// </summary>
public sealed class DesktopDragGesture : IDisposable
{
    /// <summary>dwExtraInfo stamped on the input we replay, so the hook can let it through.</summary>
    private static readonly IntPtr ReplayMarker = (IntPtr)0x57434C55; // "WCLU"

    private enum State { Idle, Pending, Tracking, PassThrough }

    private readonly MouseHook _hook;
    private readonly DesktopIconService _icons;
    private readonly Dispatcher _dispatcher;
    private readonly int _dragThresholdX = GetSystemMetrics(SM_CXDRAG);
    private readonly int _dragThresholdY = GetSystemMetrics(SM_CYDRAG);

    private State _state;
    private POINT _start;
    private POINT _current;
    private bool _dragging;
    private bool _releasedWhilePending;
    private bool _overlayUpdateQueued;
    private SelectionOverlay? _overlay;

    public DesktopDragGesture(MouseHook hook, DesktopIconService icons, Dispatcher dispatcher)
    {
        _hook = hook;
        _icons = icons;
        _dispatcher = dispatcher;
        _hook.MouseEvent += OnMouseEvent;
    }

    public event EventHandler<DesktopDragResult>? DragCompleted;

    /// <summary>
    /// Hides the selection rectangle. After a drag it stays on screen until the menu
    /// that answers it closes, so the owner of that menu calls this.
    /// </summary>
    public void HideSelection() => _overlay?.Hide();

    public void Dispose()
    {
        _hook.MouseEvent -= OnMouseEvent;
        _overlay?.Close();
    }

    // Runs inside the low-level hook: keep it fast and defer everything else to the dispatcher.
    private void OnMouseEvent(object? sender, LowLevelMouseEventArgs e)
    {
        if (e.ExtraInfo == ReplayMarker)
            return;

        switch (e.Message)
        {
            case WM_RBUTTONDOWN when _state == State.Idle && IsDesktopIconList(e.Point):
                _dispatcher.BeginInvoke(HideSelection);
                _state = State.Pending;
                _start = _current = e.Point;
                _dragging = false;
                _releasedWhilePending = false;
                e.Handled = true;
                _dispatcher.BeginInvoke(ResolvePending);
                break;

            case WM_MOUSEMOVE when _state is State.Pending or State.Tracking:
                _current = e.Point;
                if (!_dragging &&
                    (Math.Abs(_current.X - _start.X) > _dragThresholdX ||
                     Math.Abs(_current.Y - _start.Y) > _dragThresholdY))
                {
                    _dragging = true;
                }
                if (_dragging && _state == State.Tracking)
                    QueueOverlayUpdate();
                break;

            case WM_RBUTTONUP when _state == State.Pending:
                _current = e.Point;
                _releasedWhilePending = true;
                e.Handled = true;
                break;

            case WM_RBUTTONUP when _state == State.Tracking:
                _current = e.Point;
                e.Handled = true;
                _state = State.Idle;
                if (_dragging)
                    _dispatcher.BeginInvoke(Complete);
                else
                    _dispatcher.BeginInvoke(ReplayRightClick);
                break;

            case WM_RBUTTONUP when _state == State.PassThrough:
                _state = State.Idle;
                break;
        }
    }

    /// <summary>The desktop icon list is the SysListView32 inside SHELLDLL_DefView.</summary>
    internal static bool IsDesktopIconList(POINT point)
    {
        var hwnd = WindowFromPoint(point);
        return hwnd != IntPtr.Zero &&
               GetClassName(hwnd) == "SysListView32" &&
               GetClassName(GetAncestor(hwnd, GA_PARENT)) == "SHELLDLL_DefView";
    }

    /// <summary>Decides, outside the hook callback, whether the press landed on an icon.</summary>
    private void ResolvePending()
    {
        if (_state != State.Pending)
            return;

        if (DesktopIconService.IsOverIcon(_start))
        {
            _state = State.PassThrough;
            Replay(MOUSEEVENTF_RIGHTDOWN);
            if (_releasedWhilePending)
            {
                Replay(MOUSEEVENTF_RIGHTUP);
                _state = State.Idle;
            }
            return;
        }

        _state = State.Tracking;
        if (_releasedWhilePending)
        {
            _state = State.Idle;
            if (_dragging)
                Complete();
            else
                ReplayRightClick();
        }
        else if (_dragging)
        {
            QueueOverlayUpdate();
        }
    }

    private void QueueOverlayUpdate()
    {
        if (_overlayUpdateQueued)
            return;
        _overlayUpdateQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _overlayUpdateQueued = false;
            if (_state != State.Tracking)
                return;
            _overlay ??= new SelectionOverlay();
            _overlay.ShowAt(CurrentRect());
        });
    }

    private void Complete()
    {
        // The rectangle stays visible (see HideSelection) while the menu is open.
        var rect = CurrentRect();
        _overlay ??= new SelectionOverlay();
        _overlay.ShowAt(rect);
        var icons = _icons.GetIcons().Where(i => i.IntersectsWith(rect)).ToList();
        _icons.Select(icons);
        DragCompleted?.Invoke(this, new DesktopDragResult(rect, icons));
    }

    /// <summary>A plain right-click on empty space: give Explorer its click back.</summary>
    private static void ReplayRightClick()
    {
        Replay(MOUSEEVENTF_RIGHTDOWN);
        Replay(MOUSEEVENTF_RIGHTUP);
    }

    private static void Replay(uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dwFlags = flags, dwExtraInfo = ReplayMarker },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private RECT CurrentRect() => new()
    {
        Left = Math.Min(_start.X, _current.X),
        Top = Math.Min(_start.Y, _current.Y),
        Right = Math.Max(_start.X, _current.X),
        Bottom = Math.Max(_start.Y, _current.Y),
    };
}
