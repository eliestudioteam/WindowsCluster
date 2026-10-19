using System.Windows;
using System.Windows.Controls;

namespace WindowsCluster.App.Manager;

public partial class ClustersView : UserControl
{
    public ClustersView() => InitializeComponent();

    private ManagerViewModel? Model => DataContext as ManagerViewModel;

    private void NewCluster_Click(object sender, RoutedEventArgs e) => Model?.NewCluster();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Model?.Selected is { } cluster)
            ManagerViewModel.ResetAppearance(cluster);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => Model?.DeleteSelected();
}
