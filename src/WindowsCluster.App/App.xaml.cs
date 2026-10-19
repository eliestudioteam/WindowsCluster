using System.Windows;
using WindowsCluster.App.Clusters;
using WindowsCluster.App.Desktop;
using WindowsCluster.App.Input;
using WindowsCluster.App.Theming;

namespace WindowsCluster.App;

public partial class App : Application
{
    private ThemeService? _theme;
    private DesktopHost? _host;
    private ClusterService? _clusters;
    private MouseHook? _mouseHook;
    private DesktopDragGesture? _dragGesture;
    private DesktopMenuController? _desktopMenu;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _theme = ThemeService.Start(this);
        _host = new DesktopHost();
        _clusters = new ClusterService(_host);

        _mouseHook = new MouseHook();
        _dragGesture = new DesktopDragGesture(_mouseHook, new DesktopIconService(_host), Dispatcher);
        _desktopMenu = new DesktopMenuController(_dragGesture, _mouseHook, _clusters);
        _mouseHook.Install();

        MainWindow = new MainWindow(_host, _clusters);
        _theme.Track(MainWindow);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Remove the hook first so no input is held back while the rest shuts down.
        _mouseHook?.Dispose();
        _desktopMenu?.Dispose();
        _dragGesture?.Dispose();
        _clusters?.Dispose();
        _host?.Dispose();
        _theme?.Dispose();
        base.OnExit(e);
    }
}
