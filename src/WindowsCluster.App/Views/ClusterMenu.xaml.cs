using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using WindowsCluster.App.Input;
using WindowsCluster.App.Theming;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Views;

/// <summary>One command of a <see cref="ClusterMenu"/>.</summary>
/// <param name="Glyph">Segoe Fluent Icons code point.</param>
public sealed record MenuEntry(string Glyph, string Text, Action Invoke);

/// <summary>
/// Windows 11-style context menu built in WPF (rounded, shadowed, light/dark aware).
/// Closes on selection, Escape, focus loss, or any click outside it.
/// </summary>
public partial class ClusterMenu : Window
{
    private readonly MouseHook _hook;
    private readonly IntPtr _hwnd;
    private bool _closing;

    private ClusterMenu(IEnumerable<MenuEntry> entries, MouseHook hook)
    {
        InitializeComponent();
        Entries.ItemsSource = entries.ToList();
        _hook = hook;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TOOLWINDOW));
        ThemeService.Current?.Track(this);

        // The menu may not get activation (Windows restricts foreground changes), so
        // outside clicks are detected through the global hook instead of Deactivated alone.
        _hook.MouseEvent += OnGlobalMouse;
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => _hook.MouseEvent -= OnGlobalMouse;
    }

    /// <summary>
    /// Opens the menu with its top-left corner at <paramref name="anchor"/> (physical pixels),
    /// flipping left/up when it would leave the monitor's work area.
    /// </summary>
    public static ClusterMenu ShowAt(POINT anchor, IEnumerable<MenuEntry> entries, MouseHook hook)
    {
        var menu = new ClusterMenu(entries, hook);
        menu.Show();
        menu.UpdateLayout();
        menu.PlaceWithinWorkArea(anchor);
        menu.Activate();
        if (menu.Entries.ItemContainerGenerator.ContainerFromIndex(0) is ContentPresenter first)
            (VisualChild(first) as Button)?.Focus();
        return menu;
    }

    private void PlaceWithinWorkArea(POINT anchor)
    {
        GetWindowRect(_hwnd, out var size);
        var scale = GetScaleAt(anchor);
        // The visible menu starts 12 DIPs inside the window (shadow margin).
        var inset = (int)Math.Round(12 * scale);
        var x = anchor.X - inset;
        var y = anchor.Y - inset;

        var work = GetWorkAreaAt(anchor);
        if (x + size.Width > work.Right) x = anchor.X - size.Width + inset;
        if (y + size.Height > work.Bottom) y = anchor.Y - size.Height + inset;
        x = Math.Max(work.Left - inset, x);
        y = Math.Max(work.Top - inset, y);

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE);
    }

    private void OnGlobalMouse(object? sender, LowLevelMouseEventArgs e)
    {
        if (e.Message is not (WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN))
            return;
        GetWindowRect(_hwnd, out var r);
        var inside = e.Point.X >= r.Left && e.Point.X < r.Right && e.Point.Y >= r.Top && e.Point.Y < r.Bottom;
        if (!inside)
            Dispatcher.BeginInvoke(Dismiss);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    private void Entry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MenuEntry entry)
            return;
        Dismiss();
        entry.Invoke();
    }

    private void Dismiss()
    {
        if (_closing)
            return;
        _closing = true;
        Close();
    }

    private static DependencyObject? VisualChild(DependencyObject parent) =>
        System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent) > 0
            ? System.Windows.Media.VisualTreeHelper.GetChild(parent, 0)
            : null;
}
