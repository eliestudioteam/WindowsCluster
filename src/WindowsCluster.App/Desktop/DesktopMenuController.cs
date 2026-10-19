using System.Diagnostics;
using System.IO;
using WindowsCluster.App.Clusters;
using WindowsCluster.App.Input;
using WindowsCluster.App.Views;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Desktop;

/// <summary>
/// Turns a completed right-drag on the desktop into the Windows Cluster menu
/// and runs the chosen action.
/// </summary>
public sealed class DesktopMenuController : IDisposable
{
    private const string GlyphAdd = "";
    private const string GlyphNewFolder = "";
    private const string GlyphGroup = "";

    private readonly DesktopDragGesture _gesture;
    private readonly MouseHook _hook;
    private readonly ClusterService _clusters;

    public DesktopMenuController(DesktopDragGesture gesture, MouseHook hook, ClusterService clusters)
    {
        _gesture = gesture;
        _hook = hook;
        _clusters = clusters;
        _gesture.DragCompleted += OnDragCompleted;
    }

    public void Dispose() => _gesture.DragCompleted -= OnDragCompleted;

    private void OnDragCompleted(object? sender, DesktopDragResult drag)
    {
        var entries = new List<MenuEntry>
        {
            new(GlyphAdd, "Crear aquí un Cluster", () => CreateCluster(drag)),
            new(GlyphNewFolder, "Crear una Carpeta", () => CreateFolder(drag)),
        };
        if (drag.Icons.Count > 0)
        {
            var label = drag.Icons.Count == 1
                ? "Crear un Cluster con este ícono"
                : $"Crear un Cluster con estos íconos ({drag.Icons.Count})";
            entries.Add(new(GlyphGroup, label, () => CreateClusterWithIcons(drag)));
        }

        // Anchor the menu to the outer bottom-right corner of the dragged rectangle, which
        // stays on screen until the menu closes (choice, Escape or a click elsewhere).
        var corner = new POINT(drag.Bounds.Right, drag.Bounds.Bottom);
        var menu = ClusterMenu.ShowAt(corner, entries, _hook);
        menu.Closed += (_, _) => _gesture.HideSelection();
    }

    private void CreateCluster(DesktopDragResult drag) => _clusters.CreateAt(drag.Bounds);

    /// <summary>The selected icons move into the Cluster and leave Explorer's grid (no copies).</summary>
    private void CreateClusterWithIcons(DesktopDragResult drag) => _clusters.CreateAt(drag.Bounds, icons: drag.Icons);

    /// <summary>
    /// Creates a real, empty folder in the user's Desktop folder (honoring OneDrive or
    /// other redirection) and a Cluster that mirrors it.
    /// </summary>
    private void CreateFolder(DesktopDragResult drag)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = UniqueFolderPath(desktop, "Nueva carpeta");
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[DesktopMenu] CreateFolder: {ex.Message}");
            NoticeWindow.Show("No se pudo crear la carpeta", ex.Message);
            return;
        }
        _clusters.CreateAt(drag.Bounds, Path.GetFileName(path), folderPath: path);
    }

    private static string UniqueFolderPath(string parent, string baseName)
    {
        var path = Path.Combine(parent, baseName);
        for (var n = 2; Directory.Exists(path) || File.Exists(path); n++)
            path = Path.Combine(parent, $"{baseName} ({n})");
        return path;
    }
}
