using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Theming;

/// <summary>
/// Follows the Windows app theme (light/dark) and accent color, and swaps the
/// application's theme dictionary whenever the user changes them.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    private readonly Application _app;
    private readonly List<WeakReference<Window>> _windows = new();
    private ResourceDictionary? _palette;

    public ThemeService(Application app)
    {
        _app = app;
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static ThemeService? Current { get; private set; }

    public bool IsDark { get; private set; }

    public Color Accent { get; private set; }

    public event EventHandler? ThemeChanged;

    /// <summary>Starts the service and makes it available as <see cref="Current"/>.</summary>
    public static ThemeService Start(Application app) => Current = new ThemeService(app);

    /// <summary>Keeps the window's native title bar and corners in sync with the theme.</summary>
    public void Track(Window window)
    {
        _windows.Add(new WeakReference<Window>(window));
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            ApplyChrome(window);
        else
            window.SourceInitialized += (_, _) => ApplyChrome(window);
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (Current == this)
            Current = null;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Theme and accent changes arrive as General / Color on a SystemEvents thread.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            _app.Dispatcher.BeginInvoke(Apply);
    }

    private void Apply()
    {
        var dark = ReadIsDark();
        var accent = ReadAccent();
        var changed = _palette is null || dark != IsDark || accent != Accent;
        IsDark = dark;
        Accent = accent;
        if (!changed)
            return;

        var merged = _app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/WindowsCluster;component/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };
        if (_palette is not null)
            merged.Remove(_palette);
        merged.Insert(0, palette);
        _palette = palette;

        SetBrush("Brush.Accent", accent);
        SetBrush("Brush.SelectionFill", Color.FromArgb(0x40, accent.R, accent.G, accent.B));
        SetBrush("Brush.SelectionBorder", accent);

        foreach (var reference in _windows.ToList())
        {
            if (reference.TryGetTarget(out var window))
                ApplyChrome(window);
            else
                _windows.Remove(reference);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _app.Resources[key] = brush;
    }

    private void ApplyChrome(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private static bool ReadIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static Color ReadAccent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
        if (key?.GetValue("AccentColor") is int abgr)
        {
            var v = unchecked((uint)abgr);
            return Color.FromRgb((byte)v, (byte)(v >> 8), (byte)(v >> 16));
        }
        return Color.FromRgb(0x00, 0x78, 0xD4); // Windows default blue
    }
}
