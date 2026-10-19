using System.Threading;
using System.Windows;
using WindowsCluster.App.Clusters;
using WindowsCluster.App.Desktop;
using WindowsCluster.App.Explorer;
using WindowsCluster.App.Input;
using WindowsCluster.App.Layout;
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
    private DesktopIconAvoider? _iconAvoider;
    private MainWindow? _manager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated helper mode (see ElevatedAttributes): do the job and quit, no UI.
        if (ElevatedAttributes.TryRunHelper(e.Args) is int exitCode)
        {
            Shutdown(exitCode);
            return;
        }

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

        // Icons trapped under a Cluster are moved aside only after startup, so launching the app
        // never rearranges the desktop by itself.
        _iconAvoider = new DesktopIconAvoider(_host.Layout, desktopIcons);
        var activate = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        activate.Tick += (_, _) =>
        {
            activate.Stop();
            _iconAvoider.Active = true;
        };
        activate.Start();

        _dragGesture = new DesktopDragGesture(_mouseHook, desktopIcons, Dispatcher);
        _desktopMenu = new DesktopMenuController(_dragGesture, _mouseHook, _clusters);
        _mouseHook.Install();

        _manager = new MainWindow(new ManagerViewModel(_clusters, _host));
        MainWindow = _manager;
        _theme.Track(_manager);
        _manager.Show();

        // Test entry points: "--prototype" opens an Explorer-view prototype at the default spot,
        // "--prototype=x,y,width,height" at an exact rectangle in physical pixels (repeatable).
        foreach (var arg in e.Args.Where(a => a.StartsWith("--prototype", StringComparison.Ordinal)))
        {
            var parts = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..].Split(',') : [];
            NewExplorerPrototype(parts.Length == 4 && parts.All(p => int.TryParse(p, out _))
                ? PixelRect.FromSize(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]))
                : null);
        }
    }

    /// <summary>
    /// Prototype: a Cluster whose body is Windows' own folder view (IExplorerBrowser) for a real
    /// folder under %APPDATA%. Dropping files on it is a normal Explorer move.
    /// </summary>
    public ExplorerClusterWindow? NewExplorerPrototype(PixelRect? pixelBounds = null)
    {
        if (_host is null)
            return null;
        var root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsCluster", "Prototype");
        var n = 1;
        while (System.IO.Directory.Exists(System.IO.Path.Combine(root, $"Cluster {n}")))
            n++;
        var folder = System.IO.Path.Combine(root, $"Cluster {n}");
        System.IO.Directory.CreateDirectory(folder);

        var window = new ExplorerClusterWindow(folder, $"Cluster {n} (Explorador)");
        _host.Attach(window, EmbedMode.DesktopLayer);
        if (pixelBounds is { } bounds)
            window.PlaceAtPixels(bounds);
        else
        {
            window.Left = 200 + 30 * (n % 6);
            window.Top = 150 + 30 * (n % 6);
        }
        window.Show();
        _prototypes.Add(window);
        return window;
    }

    private readonly List<ExplorerClusterWindow> _prototypes = new();

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
        _iconAvoider?.Dispose();
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
