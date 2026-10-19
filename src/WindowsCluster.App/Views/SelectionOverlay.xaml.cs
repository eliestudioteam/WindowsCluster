using System.Windows;
using System.Windows.Interop;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Views;

/// <summary>Click-through rectangle shown while the user right-drags on the desktop.</summary>
public partial class SelectionOverlay : Window
{
    private readonly IntPtr _hwnd;

    public SelectionOverlay()
    {
        InitializeComponent();
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE,
            (IntPtr)(ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    /// <summary>Shows the overlay over <paramref name="bounds"/> (physical screen pixels).</summary>
    public void ShowAt(RECT bounds)
    {
        if (!IsVisible)
            Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, bounds.Left, bounds.Top,
            Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), SWP_NOACTIVATE);
    }
}
