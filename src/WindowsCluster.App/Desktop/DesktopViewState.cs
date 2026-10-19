using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Desktop;

/// <summary>
/// Mirrors the desktop's own view settings so Clusters look and behave like the icons
/// around them: icon size (Small/Medium/Large) and "Show desktop icons".
/// Explorer offers no change notification for these, so they are polled cheaply.
/// </summary>
public sealed class DesktopViewState : INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer;
    private DesktopHost? _host;
    private DesktopIconService? _icons;
    private int _iconSize = 48;
    private bool _iconsVisible = true;

    private DesktopViewState()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Bound from XAML with {x:Static}.</summary>
    public static DesktopViewState Current { get; } = new();

    /// <summary>Icon size in device-independent units, as chosen in the desktop's View menu.</summary>
    public int IconSize
    {
        get => _iconSize;
        private set
        {
            if (!Set(ref _iconSize, value))
                return;
            OnPropertyChanged(nameof(ItemWidth));
            OnPropertyChanged(nameof(IconPixelSize));
        }
    }

    /// <summary>Width of an item cell, matching Explorer's desktop spacing for the icon size.</summary>
    public double ItemWidth => Math.Max(76, IconSize + 32);

    /// <summary>Pixels to request from the shell: icon size at the primary monitor's DPI.</summary>
    public int IconPixelSize => (int)Math.Round(IconSize * GetScaleAt(new POINT(0, 0)));

    /// <summary>False when the user unticked View › Show desktop icons.</summary>
    public bool IconsVisible
    {
        get => _iconsVisible;
        private set => Set(ref _iconsVisible, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Start(DesktopHost host, DesktopIconService icons)
    {
        _host = host;
        _icons = icons;
        Poll();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        if (_host is null || _icons is null)
            return;

        var listView = _host.Topology.IconListView;
        if (listView == IntPtr.Zero || !IsWindow(listView))
            listView = _host.Refresh().IconListView;
        if (listView != IntPtr.Zero)
            IconsVisible = IsWindowVisible(listView);

        if (_icons.GetIconSize() is int size && size > 0)
            IconSize = size;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
