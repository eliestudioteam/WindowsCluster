using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WindowsCluster.App.Desktop;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Clusters;

/// <summary>
/// Borderless window that renders one Cluster on the desktop.
/// </summary>
public partial class ClusterWindow : Window
{
    public ClusterWindow(ClusterViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

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

    /// <summary>Moves and sizes the window in physical screen pixels (before or after Show).</summary>
    public void PlaceAtPixels(RECT bounds)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyHint();

    private void UpdateEmptyHint() =>
        EmptyHint.Visibility = ViewModel.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is ClusterItemViewModel item)
        {
            item.Open();
            e.Handled = true;
        }
    }

    private void SyncBounds()
    {
        // Left/Top are only meaningful for top-level windows; a WorkerW child
        // is positioned in parent-client pixels by DesktopHost.
        if (ViewModel.Mode == EmbedMode.DesktopLayer && IsLoaded)
            ViewModel.Bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BeginTitleEdit();
            e.Handled = true;
            return;
        }

        if (TitleEditor.Visibility != Visibility.Visible && e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void BeginTitleEdit()
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
}
