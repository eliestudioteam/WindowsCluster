using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using WindowsCluster.App.Clusters;
using WindowsCluster.App.Desktop;
using WindowsCluster.App.Shell;

namespace WindowsCluster.App.Manager;

public enum ManagerPage { Home, Clusters, About }

/// <summary>State of the Manager window (MainWindow).</summary>
public sealed class ManagerViewModel : INotifyPropertyChanged
{
    private readonly ClusterService _clusters;
    private readonly DesktopHost _host;
    private ManagerPage _page = ManagerPage.Home;
    private ClusterViewModel? _selected;

    public ManagerViewModel(ClusterService clusters, DesktopHost host)
    {
        _clusters = clusters;
        _host = host;
        _clusters.Clusters.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ClusterCount));
            OnPropertyChanged(nameof(HasClusters));
            OnPropertyChanged(nameof(HasNoClusters));
            if (Selected is not null && !Clusters.Contains(Selected))
                Selected = Clusters.FirstOrDefault();
        };
        _selected = Clusters.FirstOrDefault();
    }

    public ObservableCollection<ClusterViewModel> Clusters => _clusters.Clusters;

    public ManagerPage Page
    {
        get => _page;
        set => Set(ref _page, value);
    }

    public ClusterViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => Selected is not null;
    public bool HasClusters => Clusters.Count > 0;
    public bool HasNoClusters => !HasClusters;
    public int ClusterCount => Clusters.Count;

    /// <summary>With "Show hidden items" on, grouped icons stay visible (dimmed) on the desktop.</summary>
    public bool ShowsHiddenItemsWarning => DesktopItemVisibility.ExplorerShowsHiddenItems;

    public string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public string Diagnostics => _host.Topology.ToString();

    public void NewCluster() => Selected = _clusters.Create(EmbedMode.DesktopLayer);

    public void DeleteSelected()
    {
        if (Selected is { } vm)
            _clusters.Remove(vm);
    }

    public static void ResetAppearance(ClusterViewModel vm)
    {
        vm.Transparency = 15;
        vm.Brightness = 0;
        vm.Saturation = 0;
        vm.Hue = 210;
        vm.Contrast = 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
