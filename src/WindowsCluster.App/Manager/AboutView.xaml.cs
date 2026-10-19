using System.Windows;
using System.Windows.Controls;

namespace WindowsCluster.App.Manager;

public partial class AboutView : UserControl
{
    public AboutView() => InitializeComponent();

    private void Exit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();
}
