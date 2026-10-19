using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WindowsCluster.App.Shell;

namespace WindowsCluster.App.Clusters;

/// <summary>
/// Saves and loads Clusters as JSON in %APPDATA%\WindowsCluster\clusters.json.
/// Persisting which items were hidden (and how) is what lets a Cluster release
/// them back to the desktop later, even after the app or Windows restarts.
/// </summary>
public sealed class ClusterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ClusterStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsCluster", "clusters.json");
    }

    public string FilePath { get; }

    public IReadOnlyList<StoredCluster> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize<List<StoredCluster>>(stream, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[ClusterStore] Load: {ex.Message}");
            return [];
        }
    }

    public void Save(IEnumerable<ClusterViewModel> clusters)
    {
        var data = clusters.Select(ToStored).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // Write-then-replace so a crash mid-write never loses the hidden-item records.
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    private static StoredCluster ToStored(ClusterViewModel vm) => new()
    {
        Id = vm.Id,
        Title = vm.Title,
        X = vm.Bounds.X,
        Y = vm.Bounds.Y,
        Width = vm.Bounds.Width,
        Height = vm.Bounds.Height,
        Transparency = vm.Transparency,
        Brightness = vm.Brightness,
        Saturation = vm.Saturation,
        Hue = vm.Hue,
        Contrast = vm.Contrast,
        FolderPath = vm.Portal?.FolderPath,
        PortalFolderHidden = vm.PortalFolderHidden,
        // Portal items are re-read from disk; only explicit items are stored.
        Items = vm.Portal is null
            ? vm.Items.Select(i => new StoredItem { ParsingName = i.ParsingName, DisplayName = i.DisplayName, Hidden = i.Hidden }).ToList()
            : [],
    };
}

public sealed class StoredCluster
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Transparency { get; set; } = 15;
    public double Brightness { get; set; }
    public double Saturation { get; set; }
    public double Hue { get; set; } = 210;
    public double Contrast { get; set; }
    public string? FolderPath { get; set; }
    public HiddenState PortalFolderHidden { get; set; } = HiddenState.NotHidden;
    public List<StoredItem> Items { get; set; } = [];
}

public sealed class StoredItem
{
    public string ParsingName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public HiddenState Hidden { get; set; } = HiddenState.NotHidden;
}
