using System.ComponentModel;
using System.Windows;
using WindowsCluster.App.Clusters;
using WindowsCluster.App.Manager;

namespace WindowsCluster.App;

/// <summary>The Manager: left navigation with Inicio, Mis Clusters and Acerca de.</summary>
public partial class MainWindow : Window
{
    private readonly ManagerViewModel _model;
    private readonly HomeView _home = new();
    private readonly ClustersView _clusters = new();
    private readonly AboutView _about = new();

    public MainWindow(ManagerViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        _home.DataContext = _clusters.DataContext = _about.DataContext = model;
        model.PropertyChanged += OnModelChanged;
        ShowPage(model.Page);
    }

    /// <summary>When false, closing the window only hides it so the Clusters stay on the desktop.</summary>
    public bool AllowClose { get; set; }

    public void SelectCluster(ClusterViewModel cluster)
    {
        _model.Selected = cluster;
        _model.Page = ManagerPage.Clusters;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManagerViewModel.Page))
            ShowPage(_model.Page);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_model is null)
            return; // raised during InitializeComponent
        _model.Page = sender == NavClusters ? ManagerPage.Clusters
            : sender == NavAbout ? ManagerPage.About
            : ManagerPage.Home;
    }

    private void ShowPage(ManagerPage page)
    {
        PageHost.Content = page switch
        {
            ManagerPage.Clusters => _clusters,
            ManagerPage.About => _about,
            _ => _home,
        };
        var nav = page switch
        {
            ManagerPage.Clusters => NavClusters,
            ManagerPage.About => NavAbout,
            _ => NavHome,
        };
        nav.IsChecked = true;
    }
}
