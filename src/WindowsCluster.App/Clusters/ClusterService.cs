using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using WindowsCluster.App.Desktop;
using static WindowsCluster.App.Interop.NativeMethods;

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
            Bounds = new Rect(120 + offset, 120 + offset, 360, 240),
        };
        Clusters.Add(vm);
        ShowWindow(vm);
        return vm;
    }

    /// <summary>
    /// Creates a Cluster that exactly covers <paramref name="pixelBounds"/> (physical screen pixels),
    /// optionally showing <paramref name="icons"/> or mirroring the real folder <paramref name="folderPath"/>.
    /// </summary>
    public ClusterViewModel CreateAt(RECT pixelBounds, string title,
        IEnumerable<DesktopIcon>? icons = null, string? folderPath = null)
    {
        var origin = new POINT(pixelBounds.Left, pixelBounds.Top);
        var scale = GetScaleAt(origin);
        // A Cluster that will hold items needs room for its header plus one row of icons.
        var holdsItems = icons is not null || folderPath is not null;
        var minWidth = (int)Math.Ceiling(160 * scale);
        var minHeight = (int)Math.Ceiling((holdsItems ? 140 : 90) * scale);
        pixelBounds.Right = Math.Max(pixelBounds.Right, pixelBounds.Left + minWidth);
        pixelBounds.Bottom = Math.Max(pixelBounds.Bottom, pixelBounds.Top + minHeight);

        var vm = new ClusterViewModel
        {
            Title = title,
            Mode = EmbedMode.DesktopLayer,
            Bounds = new Rect(pixelBounds.Left / scale, pixelBounds.Top / scale,
                pixelBounds.Width / scale, pixelBounds.Height / scale),
        };
        foreach (var icon in icons ?? [])
            vm.Items.Add(new ClusterItemViewModel(icon.ParsingName, icon.DisplayName));
        if (folderPath is not null)
            vm.Portal = new FolderPortal(folderPath, vm.Items, Dispatcher.CurrentDispatcher);

        Clusters.Add(vm);
        ShowWindow(vm, pixelBounds);
        return vm;
    }

    public void Remove(ClusterViewModel vm)
    {
        vm.Portal?.Dispose();
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
        {
            vm.Portal?.Dispose();
            CloseWindow(vm);
        }
    }

    private void ShowWindow(ClusterViewModel vm, RECT? pixelBounds = null)
    {
        var window = new ClusterWindow(vm);
        // Attach before Show so the window never flashes as a normal top-level window.
        _host.Attach(window, vm.Mode);
        if (pixelBounds is { } bounds)
            window.PlaceAtPixels(bounds);
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
