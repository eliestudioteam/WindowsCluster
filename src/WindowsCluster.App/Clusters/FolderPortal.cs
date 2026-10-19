using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using WindowsCluster.App.Shell;

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
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
        };
        _watcher.Created += (_, _) => QueueReload();
        _watcher.Deleted += (_, _) => QueueReload();
        _watcher.Renamed += (_, _) => QueueReload();
        _watcher.Changed += (_, e) => ShellIconLoader.Shared.Invalidate(e.FullPath);
        _watcher.EnableRaisingEvents = true;

        Reload();
    }

    public string FolderPath { get; }

    public void Dispose() => _watcher.Dispose();

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
