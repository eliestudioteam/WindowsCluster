using System.Windows;
using System.Windows.Controls;

namespace WindowsCluster.App.Manager;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private void OpenClusters_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ManagerViewModel vm)
            vm.Page = ManagerPage.Clusters;
    }
}
