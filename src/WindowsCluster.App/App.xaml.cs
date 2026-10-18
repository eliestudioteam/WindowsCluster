using System.Windows;
using WindowsCluster.App.Clusters;
using WindowsCluster.App.Desktop;

namespace WindowsCluster.App;

public partial class App : Application
{
    private DesktopHost? _host;
    private ClusterService? _clusters;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = new DesktopHost();
        _clusters = new ClusterService(_host);

        MainWindow = new MainWindow(_host, _clusters);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _clusters?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
