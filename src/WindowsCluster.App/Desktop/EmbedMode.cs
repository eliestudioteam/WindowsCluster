namespace WindowsCluster.App.Desktop;

/// <summary>
/// How a Cluster window is attached to the desktop.
/// </summary>
public enum EmbedMode
{
    /// <summary>
    /// Top-level window owned by the desktop host and pinned to the bottom of the Z-order.
    /// Interactive, supports per-pixel transparency and stays with the desktop on "Show desktop".
    /// Draws above the Explorer icons.
    /// </summary>
    DesktopLayer,

    /// <summary>
    /// Child of the wallpaper WorkerW: draws between the wallpaper and the icons.
    /// Explorer's icon list covers it, so it receives no mouse input without
    /// code running inside explorer.exe. Visual-only for now.
    /// Tested on Windows build 26300 (24H2+ layout): windows parented to the WorkerW
    /// are not rendered at all, layered or not, so this mode is experimental.
    /// </summary>
    WallpaperLayer,
}
