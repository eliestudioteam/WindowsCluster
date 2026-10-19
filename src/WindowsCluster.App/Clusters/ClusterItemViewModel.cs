using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsCluster.App.Desktop;
using WindowsCluster.App.Shell;

namespace WindowsCluster.App.Clusters;

/// <summary>
/// A shell item shown inside a Cluster. It only references the real item
/// (file, folder, shortcut or virtual location); nothing is copied or moved.
/// </summary>
public sealed class ClusterItemViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isRenaming;
    private bool _isSelected;
    private string? _infoTip;
    private DispatcherTimer? _refreshTimer;

    public ClusterItemViewModel(string parsingName, string? fallbackName = null)
    {
        ParsingName = parsingName;
        // The shell's display name follows Explorer's settings (e.g. hidden extensions).
        DisplayName = ShellItems.GetDisplayName(parsingName)
            ?? fallbackName
            ?? System.IO.Path.GetFileName(parsingName);
        _ = LoadIconAsync();
    }

    /// <summary>File path or shell parsing name ("::{CLSID}") of the real item.</summary>
    public string ParsingName { get; }

    public string DisplayName { get; }

    /// <summary>How the real item was removed from Explorer's desktop grid while it is in the Cluster.</summary>
    public HiddenState Hidden { get; set; } = HiddenState.NotHidden;

    /// <summary>Shell image at the desktop's current icon size; null while loading.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set => Set(ref _icon, value);
    }

    /// <summary>True while the name is being edited in place.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set => Set(ref _isRenaming, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>
    /// Explorer's own hover text for the item (type, size, dates, …), read lazily
    /// the first time the tooltip opens.
    /// </summary>
    public string InfoTip => _infoTip ??= BuildInfoTip();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Reloads the icon at the desktop's current size (e.g. after Small/Medium/Large changed).</summary>
    public void ReloadIcon() => _ = LoadIconAsync();

    /// <summary>
    /// Refreshes the icon and tooltip after the item or a folder's content changed. Waits a
    /// moment so Explorer updates its own icon cache first (e.g. an empty folder icon
    /// turning into a folder with documents), then asks again.
    /// </summary>
    public void RequestRefresh()
    {
        if (_refreshTimer is null)
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer.Stop();
                ShellIconLoader.Shared.Invalidate(ParsingName);
                _infoTip = null;
                OnPropertyChanged(nameof(InfoTip));
                ReloadIcon();
            };
        }
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>
    /// Renames the real file or folder, keeping a shortcut's hidden extension.
    /// The desktop watcher then swaps this item for one with the new path.
    /// </summary>
    public bool TryRename(string newName, out string? error)
    {
        error = null;
        newName = newName.Trim();
        if (newName.Length == 0 || newName == DisplayName || ParsingName.StartsWith("::", StringComparison.Ordinal))
            return false;
        if (newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "El nombre contiene caracteres no válidos.";
            return false;
        }

        var folder = System.IO.Path.GetDirectoryName(ParsingName)!;
        var extension = System.IO.Path.GetExtension(ParsingName);
        // When the shown name omits the extension (shortcuts, or Explorer's "hide extensions"
        // setting), keep the real extension unless the user typed one.
        var keepsExtension = System.IO.File.Exists(ParsingName)
            && extension.Length > 0
            && !DisplayName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            && !newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        var target = System.IO.Path.Combine(folder, keepsExtension ? newName + extension : newName);
        try
        {
            if (System.IO.Directory.Exists(ParsingName))
                System.IO.Directory.Move(ParsingName, target);
            else
                System.IO.File.Move(ParsingName, target);
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Opens the item exactly as a double-click on the desktop would: through its
    /// desktop identity, so a folder opens as "Desktop › Folder" in Explorer.
    /// </summary>
    public void Open(IntPtr owner) => ShellItems.Open(ParsingName, owner);

    private async Task LoadIconAsync()
    {
        var image = await ShellIconLoader.Shared.GetAsync(ParsingName, DesktopViewState.Current.IconPixelSize);
        if (image is not null || Icon is null)
            Icon = image;
    }

    private string BuildInfoTip()
    {
        try
        {
            var tip = ShellItems.GetInfoTip(ParsingName);
            return string.IsNullOrWhiteSpace(tip) ? DisplayName : tip;
        }
        catch (Exception)
        {
            return DisplayName;
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
