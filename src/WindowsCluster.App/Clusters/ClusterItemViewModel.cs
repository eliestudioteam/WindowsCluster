using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Media;
using WindowsCluster.App.Shell;

namespace WindowsCluster.App.Clusters;

/// <summary>
/// A shell item shown inside a Cluster. It only references the real item
/// (file, folder, shortcut or virtual location); nothing is copied or moved.
/// </summary>
public sealed class ClusterItemViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public ClusterItemViewModel(string parsingName, string displayName)
    {
        ParsingName = parsingName;
        DisplayName = displayName;
        _ = LoadIconAsync();
    }

    /// <summary>File path or shell parsing name ("::{CLSID}") of the real item.</summary>
    public string ParsingName { get; }

    public string DisplayName { get; }

    /// <summary>256×256 shell image; null while loading.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Opens the item exactly as a double-click in Explorer would.</summary>
    public void Open()
    {
        var info = ParsingName.StartsWith("::", StringComparison.Ordinal)
            ? new ProcessStartInfo("explorer.exe", "shell:" + ParsingName)
            : new ProcessStartInfo(ParsingName) { UseShellExecute = true };
        try
        {
            Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"[ClusterItem] Open {ParsingName}: {ex.Message}");
        }
    }

    private async Task LoadIconAsync() => Icon = await ShellIconLoader.Shared.GetAsync(ParsingName);
}
