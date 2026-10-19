using WindowsCluster.App.Layout;

namespace WindowsCluster.App.Desktop;

/// <summary>
/// Keeps loose desktop icons from ending up hidden under a Cluster: once a Cluster has been
/// moved, resized or created and everything has stopped moving, any icon it covers is moved to
/// the nearest free cell of the desktop grid. Not done while dragging, so icons do not jump
/// around under the cursor.
/// </summary>
public sealed class DesktopIconAvoider : IDisposable
{
    private readonly ClusterLayoutManager _layout;
    private readonly DesktopIconService _icons;

    public DesktopIconAvoider(ClusterLayoutManager layout, DesktopIconService icons)
    {
        _layout = layout;
        _icons = icons;
        _layout.Settled += OnSettled;
    }

    /// <summary>User switch: when false, icons under a Cluster are left where they are.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// False while the app is starting: Clusters restored from the last session settle first, and
    /// the user's existing icon arrangement is not rearranged just because the app was launched.
    /// </summary>
    public bool Active { get; set; }

    public void Dispose() => _layout.Settled -= OnSettled;

    private void OnSettled(object? sender, LayoutSettledEventArgs e)
    {
        if (!Enabled || !Active || e.ClusterRects.Count == 0)
            return;
        var blocked = e.ClusterRects.Select(r => r.Inflate(e.Margin / 2)).ToList();
        _icons.RelocateTrapped(blocked);
    }
}
