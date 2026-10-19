using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WindowsCluster.App.Desktop;
using WindowsCluster.App.Input;
using WindowsCluster.App.Shell;
using WindowsCluster.App.Views;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Clusters;

/// <summary>
/// Borderless window that renders one Cluster on the desktop, with Explorer-like
/// selection, the native shell menu, rename, drag &amp; drop and keyboard shortcuts.
/// </summary>
public partial class ClusterWindow : Window
{
    private readonly ClusterService _service;
    private readonly MouseHook _mouseHook;
    private readonly DropTargetHelper _dropHelper = new();
    private ClusterItemViewModel? _anchor;
    private Point? _bandStart;
    private ClusterItemViewModel? _downItem;
    private Point _downPoint;
    private bool _wasSelectedOnDown;
    private System.Windows.Threading.DispatcherTimer? _renameTimer;
    private bool _moving;
    private POINT _moveCursorStart;
    private RECT _moveWindowStart;

    public ClusterWindow(ClusterViewModel viewModel, ClusterService service, MouseHook mouseHook)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        _service = service;
        _mouseHook = mouseHook;

        var b = viewModel.Bounds;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;

        LocationChanged += (_, _) => SyncBounds();
        SizeChanged += (_, _) => SyncBounds();

        viewModel.Items.CollectionChanged += OnItemsChanged;
        Closed += (_, _) => viewModel.Items.CollectionChanged -= OnItemsChanged;
        UpdateEmptyHint();
    }

    public ClusterViewModel ViewModel { get; }

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    private IReadOnlyList<ClusterItemViewModel> Selection => ViewModel.Items.Where(i => i.IsSelected).ToList();

    /// <summary>Moves and sizes the window in physical screen pixels (before or after Show).</summary>
    public void PlaceAtPixels(RECT bounds)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void SyncBounds()
    {
        // Left/Top are only meaningful for top-level windows; a WorkerW child
        // is positioned in parent-client pixels by DesktopHost.
        if (ViewModel.Mode == EmbedMode.DesktopLayer && IsLoaded)
            ViewModel.Bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyHint();

    private void UpdateEmptyHint() =>
        EmptyHint.Visibility = ViewModel.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- title

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BeginTitleEdit();
            e.Handled = true;
            return;
        }

        if (TitleEditor.Visibility == Visibility.Visible)
            return;

        // Moved by hand instead of DragMove(): DragMove runs the system move loop, which
        // Windows 11 treats as a normal app window (Snap layouts, edge snapping).
        GetCursorPos(out _moveCursorStart);
        GetWindowRect(Hwnd, out _moveWindowStart);
        _moving = Header.CaptureMouse();
        e.Handled = true;
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_moving)
            return;
        GetCursorPos(out var cursor);
        SetWindowPos(Hwnd, IntPtr.Zero,
            _moveWindowStart.Left + cursor.X - _moveCursorStart.X,
            _moveWindowStart.Top + cursor.Y - _moveCursorStart.Y,
            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_moving)
            Header.ReleaseMouseCapture();
    }

    private void Header_LostMouseCapture(object sender, MouseEventArgs e) => _moving = false;

    public void BeginTitleEdit()
    {
        // Keyboard input requires activation; desktop-layer clusters are not activated on show.
        Activate();
        TitleEditor.Text = ViewModel.Title;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditor.Visibility = Visibility.Visible;
        TitleEditor.Focus();
        TitleEditor.SelectAll();
    }

    private void EndTitleEdit(bool commit)
    {
        if (TitleEditor.Visibility != Visibility.Visible)
            return;
        if (commit)
            ViewModel.Title = TitleEditor.Text;
        TitleEditor.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    private void TitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EndTitleEdit(commit: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndTitleEdit(commit: false);
            e.Handled = true;
        }
    }

    private void TitleEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        EndTitleEdit(commit: true);

    // ---------------------------------------------------------------- selection (Explorer rules)

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClusterItemViewModel item)
            return;
        e.Handled = true;
        Activate();
        Focus();

        if (e.ClickCount == 2)
        {
            _renameTimer?.Stop();
            _downItem = null;
            item.Open(Hwnd);
            return;
        }

        _downItem = item;
        _downPoint = e.GetPosition(this);
        _wasSelectedOnDown = item.IsSelected;

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Shift) != 0 && _anchor is not null)
        {
            SelectRange(_anchor, item, add: (modifiers & ModifierKeys.Control) != 0);
        }
        else if ((modifiers & ModifierKeys.Control) != 0)
        {
            item.IsSelected = !item.IsSelected;
            _anchor = item;
        }
        else
        {
            SelectOnly(item);
            _anchor = item;
        }
    }

    /// <summary>
    /// Explorer's slow click: clicking the name of an item that was already the only selected
    /// one starts renaming, unless it turns into a double-click.
    /// </summary>
    private void Label_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClusterItemViewModel item ||
            item != _downItem || !_wasSelectedOnDown || Keyboard.Modifiers != ModifierKeys.None ||
            Selection.Count != 1)
            return;

        if (_renameTimer is null)
        {
            _renameTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime() + 50),
            };
            _renameTimer.Tick += StartPendingRename;
        }
        _renameTimer.Stop();
        _renameTimer.Tag = item;
        _renameTimer.Start();
    }

    private void StartPendingRename(object? sender, EventArgs e)
    {
        _renameTimer!.Stop();
        if (_renameTimer.Tag is ClusterItemViewModel { IsSelected: true } item)
            item.IsRenaming = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>Clears the selection (a click elsewhere on the desktop or in another Cluster).</summary>
    public void ClearSelection()
    {
        if (ViewModel.Items.Any(i => i.IsSelected))
            SelectOnly(null);
    }

    /// <summary>
    /// Selects the items under a rectangle drawn on the desktop (physical screen pixels), so a
    /// rubber band started on the desktop also picks up the icons inside this Cluster.
    /// </summary>
    public void SelectInScreenRect(RECT band, bool additive)
    {
        var bandRect = new Rect(band.Left, band.Top, band.Width, band.Height);
        foreach (var item in ViewModel.Items)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement { IsVisible: true } container)
                continue;
            var topLeft = container.PointToScreen(new Point(0, 0));
            var bottomRight = container.PointToScreen(new Point(container.ActualWidth, container.ActualHeight));
            var hit = new Rect(topLeft, bottomRight).IntersectsWith(bandRect);
            if (hit)
                item.IsSelected = true;
            else if (!additive)
                item.IsSelected = false;
        }
    }

    // ---------------------------------------------------------------- drag out

    private void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (_downItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var delta = e.GetPosition(this) - _downPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _downItem;
        _downItem = null;
        _renameTimer?.Stop();
        if (!item.IsSelected)
            SelectOnly(item);
        StartDragOut();
    }

    /// <summary>
    /// Drags the selected items with the shell's own data object and drag loop, so any drop
    /// target (desktop, Explorer folders, other apps, other Clusters) treats them as real files.
    /// </summary>
    private void StartDragOut()
    {
        var items = Selection;
        var data = ShellItems.BindToHandler(items.Select(i => i.ParsingName).ToList(),
            new Guid("0000010E-0000-0000-C000-000000000046"), Hwnd) as System.Runtime.InteropServices.ComTypes.IDataObject;
        if (data is null)
            return;

        _service.BeginDragOut(items);
        try
        {
            SHDoDragDrop(Hwnd, data, IntPtr.Zero, DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK, out _);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(data);
        }

        GetCursorPos(out var cursor);
        GetWindowRect(Hwnd, out var own);
        var droppedOnOwnCluster = cursor.X >= own.Left && cursor.X < own.Right && cursor.Y >= own.Top && cursor.Y < own.Bottom;
        _service.EndDragOut(ViewModel, items, droppedOnOwnCluster);
    }

    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_MOVE = 2;
    private const uint DROPEFFECT_LINK = 4;

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHDoDragDrop(IntPtr hwnd, System.Runtime.InteropServices.ComTypes.IDataObject pdata,
        IntPtr pdsrc, uint dwEffect, out uint pdwEffect);

    private void SelectOnly(ClusterItemViewModel? item)
    {
        foreach (var other in ViewModel.Items)
            other.IsSelected = other == item;
    }

    private void SelectRange(ClusterItemViewModel from, ClusterItemViewModel to, bool add)
    {
        var items = ViewModel.Items;
        int a = items.IndexOf(from), b = items.IndexOf(to);
        if (a < 0 || b < 0)
            return;
        var (low, high) = a <= b ? (a, b) : (b, a);
        for (var i = 0; i < items.Count; i++)
        {
            var inRange = i >= low && i <= high;
            if (inRange)
                items[i].IsSelected = true;
            else if (!add)
                items[i].IsSelected = false;
        }
    }

    /// <summary>Left press on empty space starts a rubber-band selection.</summary>
    private void Body_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Activate();
        Focus();
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            SelectOnly(null);
        _bandStart = e.GetPosition(Body);
        Body.CaptureMouse();
        e.Handled = true;
    }

    private void Body_MouseMove(object sender, MouseEventArgs e)
    {
        if (_bandStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;
        var current = e.GetPosition(Body);
        var band = new Rect(start, current);
        Canvas.SetLeft(Band, band.X);
        Canvas.SetTop(Band, band.Y);
        Band.Width = band.Width;
        Band.Height = band.Height;
        Band.Visibility = Visibility.Visible;

        var additive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        foreach (var item in ViewModel.Items)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                continue;
            var bounds = container.TransformToAncestor(Body)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            var hit = bounds.IntersectsWith(band);
            if (hit)
                item.IsSelected = true;
            else if (!additive)
                item.IsSelected = false;
        }
    }

    private void Body_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_bandStart is null)
            return;
        _bandStart = null;
        Band.Visibility = Visibility.Collapsed;
        Body.ReleaseMouseCapture();
    }

    // ---------------------------------------------------------------- keyboard

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
            return; // title or rename editor owns the keys
        var selection = Selection;
        switch (e.Key)
        {
            case Key.Enter when selection.Count > 0:
                foreach (var item in selection)
                    item.Open(Hwnd);
                break;
            case Key.Delete when selection.Count > 0:
                // Same as Delete on the desktop: Recycle Bin, with Explorer's confirmation and undo.
                ShellFileOperations.Recycle(selection.Select(i => i.ParsingName).ToList(), Hwnd);
                break;
            case Key.F2 when selection.Count == 1:
                selection[0].IsRenaming = true;
                break;
            case Key.A when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                foreach (var item in ViewModel.Items)
                    item.IsSelected = true;
                break;
            case Key.Escape:
                SelectOnly(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- context menus

    /// <summary>Right-click on a file: Windows' complete shell context menu for the selection.</summary>
    private void Item_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClusterItemViewModel item)
            return;
        e.Handled = true;
        Activate();

        if (!item.IsSelected)
        {
            SelectOnly(item);
            _anchor = item;
        }

        var names = Selection.Select(i => i.ParsingName).ToList();
        // Windows 11 layout over the shell's own commands; "Show more options" opens the classic menu.
        ModernShellMenu.Show(Hwnd, names, ToScreenPixels(e.GetPosition(this)), _mouseHook,
            onRename: () => item.IsRenaming = true);
    }

    /// <summary>Right-click on empty Cluster space: the Windows Cluster menu.</summary>
    private void Body_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled)
            return;
        e.Handled = true;

        var entries = new List<MenuEntry>
        {
            new("", "Cambiar el nombre del Cluster", BeginTitleEdit),
            new("", "Personalizar en Windows Cluster", () => ((App)Application.Current).ShowManager(ViewModel)),
            new("", "Eliminar Cluster (los archivos vuelven al escritorio)", () => _service.Remove(ViewModel)),
        };
        ClusterMenu.ShowAt(ToScreenPixels(e.GetPosition(this)), entries, _mouseHook);
    }

    // ---------------------------------------------------------------- rename

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            Activate();
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ClusterItemViewModel item } box)
            return;
        if (e.Key == Key.Enter)
        {
            CommitRename(item, box.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            item.IsRenaming = false;
            e.Handled = true;
        }
    }

    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ClusterItemViewModel { IsRenaming: true } item } box)
            CommitRename(item, box.Text);
    }

    private void CommitRename(ClusterItemViewModel item, string name)
    {
        item.IsRenaming = false;
        if (!item.TryRename(name, out var error) && error is not null)
            NoticeWindow.Show("No se pudo cambiar el nombre", error);
    }

    // ---------------------------------------------------------------- drag & drop

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        UpdateDropEffect(e);
        _dropHelper.DragEnter(Hwnd, e, CursorScreenPoint(e));
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        UpdateDropEffect(e);
        _dropHelper.DragOver(e, CursorScreenPoint(e));
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        _dropHelper.DragLeave();
    }

    private void UpdateDropEffect(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
            DropHighlight.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        _dropHelper.Drop(e, CursorScreenPoint(e));
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        var copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        // Do the work after the OLE drop returns, so Explorer's drag loop is not blocked by dialogs.
        Dispatcher.BeginInvoke(() => _service.AcceptDrop(ViewModel, paths, copy, Hwnd));

        // We perform the move ourselves (or just adopt desktop items). Reporting "none" stops
        // the drag source from deleting its originals, as it would after a non-optimized move.
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private POINT CursorScreenPoint(DragEventArgs e) => ToScreenPixels(e.GetPosition(this));

    private POINT ToScreenPixels(Point clientDip)
    {
        var screen = PointToScreen(clientDip); // WPF returns physical pixels here
        return new POINT((int)screen.X, (int)screen.Y);
    }
}
