using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using WindowsCluster.App.Desktop;

namespace WindowsCluster.App.Clusters;

/// <summary>
/// State of one Cluster. Outlives its window so the window can be recreated
/// (for example after Explorer restarts and destroys it).
/// </summary>
public sealed class ClusterViewModel : INotifyPropertyChanged
{
    private string _title = "Nuevo Cluster";
    private double _opacity = 0.85;
    private Rect _bounds = new(120, 120, 360, 240);
    private EmbedMode _mode = EmbedMode.DesktopLayer;

    public Guid Id { get; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set => Set(ref _title, string.IsNullOrWhiteSpace(value) ? "Cluster" : value.Trim());
    }

    /// <summary>Background opacity (0–1). Icons and title stay fully opaque.</summary>
    public double Opacity
    {
        get => _opacity;
        set => Set(ref _opacity, Math.Clamp(value, 0.05, 1.0));
    }

    /// <summary>Window bounds in WPF device-independent units.</summary>
    public Rect Bounds
    {
        get => _bounds;
        set => Set(ref _bounds, value);
    }

    public EmbedMode Mode
    {
        get => _mode;
        set => Set(ref _mode, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
