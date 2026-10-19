using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WindowsCluster.App.Theming;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Explorer;

/// <summary>
/// Prototype Cluster window: WPF frame (header, DWM-rounded corners, system backdrop and theme)
/// around Windows' own folder view for a real folder.
/// </summary>
public partial class ExplorerClusterWindow : Window
{
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    private const uint LVM_SETBKCOLOR = 0x1001;
    private const uint LVM_SETTEXTCOLOR = 0x1024;
    private const uint LVM_SETTEXTBKCOLOR = 0x1026;
    private const uint LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1036;
    private const int LVS_EX_TRANSPARENTBKGND = 0x00400000;
    private const int CLR_NONE = unchecked((int)0xFFFFFFFF);

    private readonly ExplorerBrowserHost _browser;
    private bool _moving;
    private POINT _moveCursorStart;
    private RECT _moveWindowStart;

    public ExplorerClusterWindow(string folderPath, string title)
    {
        InitializeComponent();
        Title = title;
        FolderPath = folderPath;
        _browser = new ExplorerBrowserHost(folderPath);
        if (double.TryParse(Environment.GetEnvironmentVariable("WC_VIEW_ALPHA"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var alpha))
            _browser.ViewOpacity = alpha;
        _browser.ViewReady += OnViewReady;
        BrowserSlot.Content = _browser;

        if (ThemeService.Current is { } theme)
        {
            theme.ThemeChanged += (_, _) => ApplyListColors();
        }
    }

    public string FolderPath { get; }

    /// <summary>Moves and sizes the window in physical screen pixels (before or after Show).</summary>
    public void PlaceAtPixels(WindowsCluster.App.Layout.PixelRect bounds)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Window handle of the icon list, for diagnostics and tests.</summary>
    public IntPtr ListViewHandle => _browser.ListViewHandle;

    public event EventHandler? ViewReady;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        ThemeService.Current?.Track(this);

        // Rounded corners from DWM, and no accent-colored border around the window.
        var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        var corners = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
    }

    private void OnViewReady(object? sender, EventArgs e)
    {
        ApplyListColors();
        ViewReady?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The folder view runs in this process, so its list view can be styled with the standard
    /// list-view messages: transparent background, and label color that follows the theme.
    /// </summary>
    private void ApplyListColors()
    {
        var list = _browser.ListViewHandle;
        if (list == IntPtr.Zero)
            return;
        var dark = ThemeService.Current?.IsDark ?? true;
        var text = dark ? 0x00FFFFFF : 0x00000000; // COLORREF 0x00BBGGRR
        SendMessage(list, LVM_SETBKCOLOR, IntPtr.Zero, (IntPtr)CLR_NONE);
        SendMessage(list, LVM_SETTEXTBKCOLOR, IntPtr.Zero, (IntPtr)CLR_NONE);
        SendMessage(list, LVM_SETTEXTCOLOR, IntPtr.Zero, (IntPtr)text);
        SendMessage(list, LVM_SETEXTENDEDLISTVIEWSTYLE, (IntPtr)LVS_EX_TRANSPARENTBKGND, (IntPtr)LVS_EX_TRANSPARENTBKGND);
        InvalidateRect(list, IntPtr.Zero, true);
    }

    // ---------------------------------------------------------------- moving (no Snap, see ClusterWindow)

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        GetCursorPos(out _moveCursorStart);
        GetWindowRect(new WindowInteropHelper(this).Handle, out _moveWindowStart);
        _moving = Header.CaptureMouse();
        e.Handled = true;
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_moving)
            return;
        GetCursorPos(out var cursor);
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
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

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
}
