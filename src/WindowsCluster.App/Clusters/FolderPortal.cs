using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;

namespace WindowsCluster.App.Clusters;

/// <summary>
/// Keeps a Cluster's items in sync with a real folder on disk (a "folder portal").
/// Changes made in Explorer show up in the Cluster and vice versa.
/// </summary>
public sealed class FolderPortal : IDisposable
{
    private readonly ObservableCollection<ClusterItemViewModel> _items;
    private readonly Dispatcher _dispatcher;
    private readonly FileSystemWatcher _watcher;
    private bool _reloadQueued;

    public FolderPortal(string folderPath, ObservableCollection<ClusterItemViewModel> items, Dispatcher dispatcher)
    {
        FolderPath = folderPath;
        _items = items;
        _dispatcher = dispatcher;

        _watcher = new FileSystemWatcher(folderPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
        };
        _watcher.Created += (_, e) => OnChanged(e.FullPath, structural: true);
        _watcher.Deleted += (_, e) => OnChanged(e.FullPath, structural: true);
        _watcher.Renamed += (_, e) => OnChanged(e.FullPath, structural: true);
        _watcher.Changed += (_, e) => OnChanged(e.FullPath, structural: false);
        _watcher.EnableRaisingEvents = true;

        Reload();
    }

    public string FolderPath { get; }

    public void Dispose() => _watcher.Dispose();

    /// <summary>
    /// Direct children appearing or disappearing reload the list; anything deeper (or a
    /// content change) only refreshes the icon of the child it belongs to.
    /// </summary>
    private void OnChanged(string path, bool structural)
    {
        var isDirectChild = string.Equals(Path.GetDirectoryName(path), FolderPath, StringComparison.OrdinalIgnoreCase);
        if (structural && isDirectChild)
        {
            QueueReload();
            return;
        }
        var child = Path.Combine(FolderPath, Path.GetRelativePath(FolderPath, path).Split(Path.DirectorySeparatorChar)[0]);
        _dispatcher.BeginInvoke(() => _items
            .FirstOrDefault(i => string.Equals(i.ParsingName, child, StringComparison.OrdinalIgnoreCase))
            ?.RequestRefresh());
    }

    private void QueueReload()
    {
        // Explorer often raises several events per operation; coalesce them.
        if (_reloadQueued)
            return;
        _reloadQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _reloadQueued = false;
            Reload();
        });
    }

    private void Reload()
    {
        var entries = new DirectoryInfo(FolderPath)
            .EnumerateFileSystemInfos()
            .Where(e => (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .OrderByDescending(e => e is DirectoryInfo)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _items.Clear();
        foreach (var entry in entries)
        {
            var name = entry is FileInfo { Extension: ".lnk" or ".url" }
                ? Path.GetFileNameWithoutExtension(entry.Name)
                : entry.Name;
            _items.Add(new ClusterItemViewModel(entry.FullName, name));
        }
    }
}
