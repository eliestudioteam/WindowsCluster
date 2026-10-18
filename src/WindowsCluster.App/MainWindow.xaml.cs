using System.Windows;
using System.Windows.Controls;
using WindowsCluster.App.Clusters;
using WindowsCluster.App.Desktop;

namespace WindowsCluster.App;

public partial class MainWindow : Window
{
    private readonly DesktopHost _host;
    private readonly ClusterService _clusters;

    public MainWindow(DesktopHost host, ClusterService clusters)
    {
        InitializeComponent();
        _host = host;
        _clusters = clusters;
        ClusterList.ItemsSource = clusters.Clusters;
        _host.ShellRestarted += (_, _) => ShowTopology();
        ShowTopology();
    }

    private EmbedMode SelectedMode =>
        Enum.Parse<EmbedMode>((string)((ComboBoxItem)ModeCombo.SelectedItem).Tag);

    private void ShowTopology() => TopologyText.Text = _host.Topology.ToString();

    private void CreateCluster_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _clusters.Create(SelectedMode);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Windows Cluster", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ShowTopology();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _host.Refresh();
        ShowTopology();
    }

    private void ToggleMode_Click(object sender, RoutedEventArgs e)
    {
        if (ClusterList.SelectedItem is not ClusterViewModel vm)
            return;
        var target = vm.Mode == EmbedMode.DesktopLayer ? EmbedMode.WallpaperLayer : EmbedMode.DesktopLayer;
        _clusters.ChangeMode(vm, target);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ClusterList.SelectedItem is ClusterViewModel vm)
            _clusters.Remove(vm);
    }
}
