using System.Threading;
using System.Windows;
using WindowsCluster.App.Clusters;
using WindowsCluster.App.Desktop;
using WindowsCluster.App.Input;
using WindowsCluster.App.Manager;
using WindowsCluster.App.Shell;
using WindowsCluster.App.Theming;

namespace WindowsCluster.App;

public partial class App : Application
{
    private const string InstanceMutexName = "WindowsCluster.SingleInstance";
    private const string ShowManagerEventName = "WindowsCluster.ShowManager";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showManagerEvent;
    private RegisteredWaitHandle? _showManagerWait;

    private ThemeService? _theme;
    private DesktopHost? _host;
    private ClusterService? _clusters;
    private MouseHook? _mouseHook;
    private DesktopDragGesture? _dragGesture;
    private DesktopMenuController? _desktopMenu;
    private MainWindow? _manager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance only: a second launch just brings the Manager of the first to front.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);
        _showManagerEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowManagerEventName);
        if (!isFirst)
        {
            _showManagerEvent.Set();
            Shutdown();
            return;
        }
        _showManagerWait = ThreadPool.RegisterWaitForSingleObject(_showManagerEvent,
            (_, _) => Dispatcher.BeginInvoke(() => ShowManager()), null, Timeout.Infinite, executeOnlyOnce: false);

        _theme = ThemeService.Start(this);
        NativeMenuTheme.Apply();
        _theme.ThemeChanged += (_, _) => NativeMenuTheme.Apply();

        _host = new DesktopHost();
        var desktopIcons = new DesktopIconService(_host);
        // Read the desktop's icon size and visibility before the first Cluster is drawn.
        DesktopViewState.Current.Start(_host, desktopIcons);
        _mouseHook = new MouseHook();
        _clusters = new ClusterService(_host, _mouseHook, new ClusterStore());
        _clusters.LoadSaved();

        _dragGesture = new DesktopDragGesture(_mouseHook, desktopIcons, Dispatcher);
        _desktopMenu = new DesktopMenuController(_dragGesture, _mouseHook, _clusters);
        _mouseHook.Install();

        _manager = new MainWindow(new ManagerViewModel(_clusters, _host));
        MainWindow = _manager;
        _theme.Track(_manager);
        _manager.Show();
    }

    /// <summary>Brings the Manager to front, optionally on a Cluster's settings.</summary>
    public void ShowManager(ClusterViewModel? cluster = null)
    {
        if (_manager is null)
            return;
        if (cluster is not null)
            _manager.SelectCluster(cluster);
        _manager.Show();
        if (_manager.WindowState == WindowState.Minimized)
            _manager.WindowState = WindowState.Normal;
        _manager.Activate();
    }

    /// <summary>Saves everything and quits. Grouped items stay hidden until the next launch.</summary>
    public void Quit()
    {
        if (_manager is not null)
            _manager.AllowClose = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Remove the hook first so no input is held back while the rest shuts down.
        _mouseHook?.Dispose();
        DesktopViewState.Current.Stop();
        _desktopMenu?.Dispose();
        _dragGesture?.Dispose();
        _clusters?.Dispose();
        _host?.Dispose();
        _theme?.Dispose();
        _showManagerWait?.Unregister(null);
        _showManagerEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
