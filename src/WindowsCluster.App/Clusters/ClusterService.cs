using System.Collections.ObjectModel;
using WindowsCluster.App.Desktop;

namespace WindowsCluster.App.Clusters;

/// <summary>
/// Owns every Cluster: creates its window, attaches it to the desktop and
/// recreates windows that Explorer destroyed when it restarted.
/// </summary>
public sealed class ClusterService : IDisposable
{
    private readonly DesktopHost _host;
    private readonly Dictionary<Guid, ClusterWindow> _windows = new();

    public ClusterService(DesktopHost host)
    {
        _host = host;
        _host.ShellRestarted += OnShellRestarted;
    }

    public ObservableCollection<ClusterViewModel> Clusters { get; } = new();

    public ClusterViewModel Create(EmbedMode mode)
    {
        var offset = 30 * (Clusters.Count % 8);
        var vm = new ClusterViewModel
        {
            Title = $"Cluster {Clusters.Count + 1}",
            Mode = mode,
            Bounds = new System.Windows.Rect(120 + offset, 120 + offset, 360, 240),
        };
        Clusters.Add(vm);
        ShowWindow(vm);
        return vm;
    }

    public void Remove(ClusterViewModel vm)
    {
        Clusters.Remove(vm);
        if (_windows.Remove(vm.Id, out var window))
        {
            _host.Detach(window);
            window.Close();
        }
    }

    /// <summary>Moves a Cluster to another layer by recreating its window.</summary>
    public void ChangeMode(ClusterViewModel vm, EmbedMode mode)
    {
        if (vm.Mode == mode)
            return;
        CloseWindow(vm);
        vm.Mode = mode;
        ShowWindow(vm);
    }

    public void Dispose()
    {
        _host.ShellRestarted -= OnShellRestarted;
        foreach (var vm in Clusters.ToList())
            CloseWindow(vm);
    }

    private void ShowWindow(ClusterViewModel vm)
    {
        var window = new ClusterWindow(vm);
        // Attach before Show so the window never flashes as a normal top-level window.
        _host.Attach(window, vm.Mode);
        window.Show();
        _windows[vm.Id] = window;
    }

    private void CloseWindow(ClusterViewModel vm)
    {
        if (!_windows.Remove(vm.Id, out var window))
            return;
        _host.Detach(window);
        window.Close();
    }

    private void OnShellRestarted(object? sender, DesktopTopology topology)
    {
        // The old owner/parent windows are gone and took our windows with them.
        foreach (var vm in Clusters)
        {
            if (_windows.Remove(vm.Id, out var stale) && stale.IsLoaded)
                stale.Close();
            ShowWindow(vm);
        }
    }
}
