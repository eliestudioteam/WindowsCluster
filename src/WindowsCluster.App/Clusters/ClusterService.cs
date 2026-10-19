using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WindowsCluster.App.Desktop;
using WindowsCluster.App.Input;
using WindowsCluster.App.Shell;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Clusters;

/// <summary>
/// Owns every Cluster: creates its window, attaches it to the desktop, hides and releases
/// the real desktop items it holds, persists everything, and recreates windows that
/// Explorer destroyed when it restarted.
/// </summary>
public sealed class ClusterService : IDisposable
{
    private readonly DesktopHost _host;
    private readonly MouseHook _mouseHook;
    private readonly ClusterStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Guid, ClusterWindow> _windows = new();
    private readonly List<FileSystemWatcher> _desktopWatchers = new();
    private readonly DispatcherTimer _saveTimer;

    public ClusterService(DesktopHost host, MouseHook mouseHook, ClusterStore store)
    {
        _host = host;
        _mouseHook = mouseHook;
        _store = store;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _host.ShellRestarted += OnShellRestarted;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveNow();

        WatchDesktopFolders();
        DesktopViewState.Current.PropertyChanged += OnDesktopViewChanged;
    }

    public ObservableCollection<ClusterViewModel> Clusters { get; } = new();

    /// <summary>Recreates the Clusters saved by a previous session.</summary>
    public void LoadSaved()
    {
        foreach (var stored in _store.Load())
        {
            var vm = new ClusterViewModel(stored.Id)
            {
                Title = stored.Title,
                Bounds = new Rect(stored.X, stored.Y, stored.Width, stored.Height),
                Transparency = stored.Transparency,
                Brightness = stored.Brightness,
                Saturation = stored.Saturation,
                Hue = stored.Hue,
                Contrast = stored.Contrast,
                PortalFolderHidden = stored.PortalFolderHidden,
            };
            foreach (var item in stored.Items)
            {
                // A file deleted while the app was closed simply drops out of its Cluster.
                if (Exists(item.ParsingName))
                    vm.Items.Add(new ClusterItemViewModel(item.ParsingName, item.DisplayName) { Hidden = item.Hidden });
            }
            if (stored.FolderPath is not null && Directory.Exists(stored.FolderPath))
                vm.Portal = new FolderPortal(stored.FolderPath, vm.Items, _dispatcher);

            Register(vm);
            ShowWindow(vm);
        }

        // Anything hidden by an earlier session that no Cluster holds goes back to the desktop.
        DesktopItemVisibility.ReleaseOrphans(Clusters.SelectMany(c =>
            c.Portal is { } portal ? [portal.FolderPath] : c.Items.Select(i => i.ParsingName)));
    }

    public ClusterViewModel Create(EmbedMode mode)
    {
        var offset = 30 * (Clusters.Count % 8);
        var vm = new ClusterViewModel
        {
            Title = NextTitle(),
            Mode = mode,
            Bounds = new Rect(120 + offset, 120 + offset, 360, 240),
        };
        Register(vm);
        ShowWindow(vm);
        return vm;
    }

    /// <summary>
    /// Creates a Cluster that exactly covers <paramref name="pixelBounds"/> (physical screen pixels),
    /// optionally holding desktop <paramref name="icons"/> or mirroring the real folder <paramref name="folderPath"/>.
    /// </summary>
    public ClusterViewModel CreateAt(RECT pixelBounds, string? title = null,
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
            Title = title ?? NextTitle(),
            Mode = EmbedMode.DesktopLayer,
            Bounds = new Rect(pixelBounds.Left / scale, pixelBounds.Top / scale,
                pixelBounds.Width / scale, pixelBounds.Height / scale),
        };
        if (icons is not null)
            Adopt(vm, icons.Select(i => (i.ParsingName, i.DisplayName)));
        if (folderPath is not null)
        {
            // The portal stands in for the folder, so its own desktop icon is hidden too.
            vm.PortalFolderHidden = DesktopItemVisibility.Hide(folderPath);
            vm.Portal = new FolderPortal(folderPath, vm.Items, _dispatcher);
        }

        Register(vm);
        ShowWindow(vm, pixelBounds);
        return vm;
    }

    /// <summary>
    /// Moves real desktop items into <paramref name="vm"/>: they are hidden from Explorer's grid
    /// (never copied), and skipped when another Cluster already holds them.
    /// </summary>
    public void Adopt(ClusterViewModel vm, IEnumerable<(string ParsingName, string DisplayName)> items)
    {
        foreach (var (parsingName, displayName) in items)
        {
            if (FindOwner(parsingName) is not null)
                continue;
            var hidden = DesktopItemVisibility.IsDesktopItem(parsingName)
                ? DesktopItemVisibility.Hide(parsingName)
                : HiddenState.NotHidden;
            vm.Items.Add(new ClusterItemViewModel(parsingName, displayName) { Hidden = hidden });
        }
        SaveNow();
    }

    /// <summary>
    /// Handles files dropped on a Cluster. Desktop items are adopted in place; items from
    /// other folders are first moved (or copied, with Ctrl) to the Desktop through Explorer's
    /// file engine, so they end up exactly where a drop on the desktop would put them.
    /// </summary>
    public void AcceptDrop(ClusterViewModel vm, IReadOnlyList<string> paths, bool copy, IntPtr owner)
    {
        if (vm.Portal is { } portal)
        {
            var outside = paths.Where(p => !string.Equals(Path.GetDirectoryName(p), portal.FolderPath, StringComparison.OrdinalIgnoreCase));
            ShellFileOperations.Transfer(outside, portal.FolderPath, copy, owner);
            return; // the portal's watcher picks the new files up
        }

        var onDesktop = paths.Where(DesktopItemVisibility.IsDesktopItem).ToList();
        var elsewhere = paths.Except(onDesktop).ToList();
        var arrived = ShellFileOperations.Transfer(elsewhere, DesktopItemVisibility.DesktopFolders[0], copy, owner);
        Adopt(vm, onDesktop.Concat(arrived).Select(p => (p, DisplayNameOf(p))));
    }

    /// <summary>
    /// Deletes the Cluster only. Every item goes back to the desktop grid;
    /// no file is ever deleted from disk.
    /// </summary>
    public void Remove(ClusterViewModel vm)
    {
        ReleaseAll(vm);
        vm.Portal?.Dispose();
        Clusters.Remove(vm);
        CloseWindow(vm);
        SaveNow();
    }

    /// <summary>Takes one item out of its Cluster and returns it to the desktop grid.</summary>
    public void Release(ClusterViewModel vm, ClusterItemViewModel item)
    {
        if (vm.Portal is not null)
            return; // portal items are the folder's content, not desktop items
        DesktopItemVisibility.Restore(item.ParsingName, item.Hidden);
        vm.Items.Remove(item);
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
        DesktopViewState.Current.PropertyChanged -= OnDesktopViewChanged;
        foreach (var watcher in _desktopWatchers)
            watcher.Dispose();
        SaveNow();
        // Items stay hidden while the app is closed; the saved state lets the next session
        // show them again in their Clusters and release them when a Cluster is deleted.
        foreach (var vm in Clusters.ToList())
        {
            vm.Portal?.Dispose();
            CloseWindow(vm);
        }
    }

    // ---------------------------------------------------------------- internals

    private void Register(ClusterViewModel vm)
    {
        Clusters.Add(vm);
        vm.PropertyChanged += OnClusterChanged;
        vm.Items.CollectionChanged += OnItemsChanged;
        ScheduleSave();
    }

    private void ReleaseAll(ClusterViewModel vm)
    {
        if (vm.Portal is { } portal)
            DesktopItemVisibility.Restore(portal.FolderPath, vm.PortalFolderHidden);
        else
        {
            foreach (var item in vm.Items.ToList())
                DesktopItemVisibility.Restore(item.ParsingName, item.Hidden);
        }
        vm.PropertyChanged -= OnClusterChanged;
        vm.Items.CollectionChanged -= OnItemsChanged;
    }

    private ClusterViewModel? FindOwner(string parsingName) =>
        Clusters.FirstOrDefault(c => c.Portal is null &&
            c.Items.Any(i => string.Equals(i.ParsingName, parsingName, StringComparison.OrdinalIgnoreCase)));

    private void ShowWindow(ClusterViewModel vm, RECT? pixelBounds = null)
    {
        var window = new ClusterWindow(vm, this, _mouseHook);
        // Attach before Show so the window never flashes as a normal top-level window.
        _host.Attach(window, vm.Mode);
        if (pixelBounds is { } bounds)
            window.PlaceAtPixels(bounds);
        if (DesktopViewState.Current.IconsVisible)
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

    private void OnClusterChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleSave();

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            _store.Save(Clusters);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[ClusterService] Save: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps Cluster items in sync with the disk: renamed or deleted files (for example through
    /// the native context menu), changed files (new thumbnails) and changed folder content
    /// (Explorer's folder icon shows what is inside).
    /// </summary>
    private void WatchDesktopFolders()
    {
        foreach (var folder in DesktopItemVisibility.DesktopFolders.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = true,
            };
            watcher.Created += (_, e) => _dispatcher.BeginInvoke(() => OnDesktopChange(folder, e.FullPath));
            watcher.Changed += (_, e) => _dispatcher.BeginInvoke(() => OnDesktopChange(folder, e.FullPath));
            watcher.Deleted += (_, e) => _dispatcher.BeginInvoke(() =>
            {
                if (IsDirectChild(folder, e.FullPath)) OnDesktopItemDeleted(e.FullPath);
                else OnDesktopChange(folder, e.FullPath);
            });
            watcher.Renamed += (_, e) => _dispatcher.BeginInvoke(() =>
            {
                if (IsDirectChild(folder, e.FullPath)) OnDesktopItemRenamed(e.OldFullPath, e.FullPath);
                else OnDesktopChange(folder, e.FullPath);
            });
            watcher.EnableRaisingEvents = true;
            _desktopWatchers.Add(watcher);
        }
    }

    /// <summary>Refreshes the icon of the desktop item that is, or contains, <paramref name="path"/>.</summary>
    private void OnDesktopChange(string desktopFolder, string path)
    {
        var relative = Path.GetRelativePath(desktopFolder, path);
        var topLevel = Path.Combine(desktopFolder, relative.Split(Path.DirectorySeparatorChar)[0]);
        if (FindOwner(topLevel) is not { } vm)
            return;
        vm.Items.FirstOrDefault(i => string.Equals(i.ParsingName, topLevel, StringComparison.OrdinalIgnoreCase))
            ?.RequestRefresh();
    }

    private static bool IsDirectChild(string folder, string path) =>
        string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Mirrors the desktop's View menu: icon size and "Show desktop icons".</summary>
    private void OnDesktopViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DesktopViewState.IconPixelSize):
                foreach (var item in Clusters.SelectMany(c => c.Items))
                    item.ReloadIcon();
                break;
            case nameof(DesktopViewState.IconsVisible):
                foreach (var window in _windows.Values)
                {
                    if (DesktopViewState.Current.IconsVisible)
                        window.Show();
                    else
                        window.Hide();
                }
                break;
        }
    }

    private void OnDesktopItemDeleted(string path)
    {
        if (FindOwner(path) is not { } vm)
            return;
        var item = vm.Items.First(i => string.Equals(i.ParsingName, path, StringComparison.OrdinalIgnoreCase));
        vm.Items.Remove(item);
    }

    private void OnDesktopItemRenamed(string oldPath, string newPath)
    {
        if (FindOwner(oldPath) is not { } vm)
            return;
        var index = vm.Items.ToList().FindIndex(i => string.Equals(i.ParsingName, oldPath, StringComparison.OrdinalIgnoreCase));
        var old = vm.Items[index];
        // Renaming keeps the file's attributes, so it stays hidden; only the reference changes.
        vm.Items[index] = new ClusterItemViewModel(newPath, DisplayNameOf(newPath)) { Hidden = old.Hidden };
    }

    private string NextTitle() => $"Cluster {Clusters.Count + 1}";

    private static string DisplayNameOf(string path) =>
        Path.GetExtension(path) is ".lnk" or ".url" ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);

    private static bool Exists(string parsingName) =>
        parsingName.StartsWith("::", StringComparison.Ordinal) || File.Exists(parsingName) || Directory.Exists(parsingName);
}
