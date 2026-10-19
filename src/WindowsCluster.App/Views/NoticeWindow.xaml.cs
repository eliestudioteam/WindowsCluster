using System.Windows;
using WindowsCluster.App.Theming;

namespace WindowsCluster.App.Views;

/// <summary>Small themed message dialog.</summary>
public partial class NoticeWindow : Window
{
    private NoticeWindow(string heading, string body)
    {
        InitializeComponent();
        Heading.Text = heading;
        Body.Text = body;
        ThemeService.Current?.Track(this);
    }

    public static void Show(string heading, string body) => new NoticeWindow(heading, body).ShowDialog();

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
