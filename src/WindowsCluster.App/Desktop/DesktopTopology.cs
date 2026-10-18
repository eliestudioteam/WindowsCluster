namespace WindowsCluster.App.Desktop;

/// <summary>
/// Snapshot of the Explorer desktop window hierarchy at a point in time.
/// </summary>
/// <param name="Progman">Top-level "Progman" window (the desktop root).</param>
/// <param name="DefViewHost">Top-level window that owns SHELLDLL_DefView (Progman or a WorkerW).</param>
/// <param name="DefView">"SHELLDLL_DefView" window that hosts the icon list.</param>
/// <param name="IconListView">"SysListView32" that draws the desktop icons.</param>
/// <param name="WallpaperWorkerW">WorkerW that paints the wallpaper, behind the icons.</param>
/// <param name="IsRaisedDesktop">
/// True for the Windows 11 24H2+ layout, where the wallpaper WorkerW is a child of Progman
/// instead of a top-level sibling placed behind the DefView host.
/// </param>
public sealed record DesktopTopology(
    IntPtr Progman,
    IntPtr DefViewHost,
    IntPtr DefView,
    IntPtr IconListView,
    IntPtr WallpaperWorkerW,
    bool IsRaisedDesktop)
{
    public bool IsValid => Progman != IntPtr.Zero && DefView != IntPtr.Zero;

    public bool HasWallpaperLayer => WallpaperWorkerW != IntPtr.Zero;

    public override string ToString() =>
        $"Progman=0x{Progman:X} DefViewHost=0x{DefViewHost:X} DefView=0x{DefView:X} " +
        $"ListView=0x{IconListView:X} WorkerW=0x{WallpaperWorkerW:X} " +
        $"Layout={(IsRaisedDesktop ? "24H2+" : "clásico")}";
}
