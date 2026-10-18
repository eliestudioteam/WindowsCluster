using System.Windows;
using System.Windows.Input;
using WindowsCluster.App.Desktop;

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
    }

    public ClusterViewModel ViewModel { get; }

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
