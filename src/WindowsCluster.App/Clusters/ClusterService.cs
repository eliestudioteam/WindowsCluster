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
        _mouseHook.MouseEvent += OnGlobalMouse;
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
                // A file deleted while the app was closed simply drops out of its Cluster, and a
                // desktop item that was never hidden stays only on the desktop (no duplicate).
                if (Exists(item.ParsingName) &&
                    !(DesktopItemVisibility.IsDesktopItem(item.ParsingName) && item.Hidden.Method == HideMethod.None))
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
            Adopt(vm, icons.Select(i => i.ParsingName));
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
    /// (never copied). Items already in another Cluster move over from it. A desktop item that
    /// cannot be hidden (e.g. the UAC prompt for a Public Desktop shortcut was declined) stays
    /// on the desktop instead of appearing twice.
    /// </summary>
    public void Adopt(ClusterViewModel vm, IEnumerable<string> parsingNames)
    {
        var incoming = new List<string>();
        foreach (var name in parsingNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (FindOwner(name) is { } owner)
            {
                if (owner != vm)
                    MoveItem(owner, vm, name);
                continue;
            }
            incoming.Add(name);
        }

        var states = DesktopItemVisibility.HideMany(incoming.Where(DesktopItemVisibility.IsDesktopItem));
        foreach (var name in incoming)
        {
            var hidden = states.TryGetValue(name, out var state) ? state : HiddenState.NotHidden;
            if (DesktopItemVisibility.IsDesktopItem(name) && hidden.Method == HideMethod.None)
                continue;
            vm.Items.Add(new ClusterItemViewModel(name) { Hidden = hidden });
        }
        SaveNow();
    }

    private void MoveItem(ClusterViewModel from, ClusterViewModel to, string parsingName)
    {
        var item = from.Items.First(i => string.Equals(i.ParsingName, parsingName, StringComparison.OrdinalIgnoreCase));
        from.Items.Remove(item);
        if (item.Hidden.Method == HideMethod.None && DesktopItemVisibility.IsDesktopItem(parsingName))
            item.Hidden = DesktopItemVisibility.Hide(parsingName);
        item.IsSelected = false;
        to.Items.Add(item);
    }

    /// <summary>
    /// Called before items are dragged out of a Cluster: they become normal desktop files
    /// again, so wherever they land (another folder, the desktop) they are not left hidden.
    /// </summary>
    public void BeginDragOut(IReadOnlyList<ClusterItemViewModel> items)
    {
        DesktopItemVisibility.RestoreMany(items.Select(i => (i.ParsingName, i.Hidden)));
        foreach (var item in items)
            item.Hidden = HiddenState.NotHidden;
    }

    /// <summary>
    /// Called after the drag ends. Items dropped back on their Cluster (or a cancelled drag)
    /// are hidden again; items dropped on the desktop leave the Cluster. Items moved to
    /// another folder or Cluster were already handled by the drop.
    /// </summary>
    public void EndDragOut(ClusterViewModel vm, IReadOnlyList<ClusterItemViewModel> items, bool droppedOnOwnCluster)
    {
        var stillHere = items.Where(i => vm.Items.Contains(i) && Exists(i.ParsingName)).ToList();
        if (droppedOnOwnCluster)
        {
            var states = DesktopItemVisibility.HideMany(stillHere.Select(i => i.ParsingName));
            foreach (var item in stillHere)
                item.Hidden = states.TryGetValue(item.ParsingName, out var state) ? state : HiddenState.NotHidden;
        }
        else
        {
            foreach (var item in stillHere)
                vm.Items.Remove(item);
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
        Adopt(vm, onDesktop.Concat(arrived));
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
        DesktopItemVisibility.RestoreMany([(item.ParsingName, item.Hidden)]);
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
        _mouseHook.MouseEvent -= OnGlobalMouse;
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
            DesktopItemVisibility.RestoreMany([(portal.FolderPath, vm.PortalFolderHidden)]);
        else
            DesktopItemVisibility.RestoreMany(vm.Items.Select(i => (i.ParsingName, i.Hidden)).ToList());
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

    // ---------------------------------------------------------------- selection across the desktop

    private POINT? _desktopBandStart;
    private bool _desktopBandUpdateQueued;
    private POINT _desktopBandEnd;

    /// <summary>
    /// Makes Cluster selection behave like part of the desktop: a click anywhere else clears it,
    /// and a rubber band started on the desktop also selects the Cluster icons it covers.
    /// Runs inside the low-level hook, so it only records state and defers the work.
    /// </summary>
    private void OnGlobalMouse(object? sender, LowLevelMouseEventArgs e)
    {
        switch (e.Message)
        {
            case NativeMethodsMessages.LButtonDown:
                var point = e.Point;
                var additive = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
                _dispatcher.BeginInvoke(() =>
                {
                    if (!additive)
                    {
                        foreach (var window in _windows.Values)
                        {
                            GetWindowRect(new System.Windows.Interop.WindowInteropHelper(window).Handle, out var r);
                            var inside = point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom;
                            if (!inside)
                                window.ClearSelection();
                        }
                    }
                });
                if (DesktopDragGesture.IsDesktopIconList(point) && !DesktopIconService.IsOverIcon(point))
                    _desktopBandStart = point;
                break;

            case NativeMethodsMessages.MouseMove when _desktopBandStart is not null:
                _desktopBandEnd = e.Point;
                if (_desktopBandUpdateQueued)
                    break;
                _desktopBandUpdateQueued = true;
                _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
                {
                    _desktopBandUpdateQueued = false;
                    if (_desktopBandStart is not { } start)
                        return;
                    var band = new RECT
                    {
                        Left = Math.Min(start.X, _desktopBandEnd.X),
                        Top = Math.Min(start.Y, _desktopBandEnd.Y),
                        Right = Math.Max(start.X, _desktopBandEnd.X),
                        Bottom = Math.Max(start.Y, _desktopBandEnd.Y),
                    };
                    var add = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
                    foreach (var window in _windows.Values)
                        window.SelectInScreenRect(band, add);
                });
                break;

            case NativeMethodsMessages.LButtonUp:
                _desktopBandStart = null;
                break;
        }
    }

    private static class NativeMethodsMessages
    {
        public const int MouseMove = 0x0200;
        public const int LButtonDown = 0x0201;
        public const int LButtonUp = 0x0202;
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
        vm.Items[index] = new ClusterItemViewModel(newPath) { Hidden = old.Hidden };
    }

    private string NextTitle() => $"Cluster {Clusters.Count + 1}";

    private static bool Exists(string parsingName) =>
        parsingName.StartsWith("::", StringComparison.Ordinal) || File.Exists(parsingName) || Directory.Exists(parsingName);
}
