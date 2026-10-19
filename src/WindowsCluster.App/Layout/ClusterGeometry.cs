namespace WindowsCluster.App.Layout;

// Pure geometry for Cluster placement: magnetic snapping, push-away collision resolution and
// free-cell search for desktop icons. It has no WPF or Win32 dependency, so it is unit-tested
// on its own; ClusterLayoutManager applies its results to real windows.

/// <summary>Integer rectangle in physical screen pixels (right/bottom exclusive).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public static PixelRect FromSize(int x, int y, int width, int height) => new(x, y, x + width, y + height);

    public PixelRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public PixelRect Inflate(int amount) => new(Left - amount, Top - amount, Right + amount, Bottom + amount);

    public bool Intersects(PixelRect other) =>
        Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;

    public bool Contains(PixelRect other) =>
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;
}

public readonly record struct PixelPoint(int X, int Y);

/// <summary>The edges of a window that a user action is changing.</summary>
[Flags]
public enum Edges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,

    /// <summary>All four: the window is being moved as a whole, not resized.</summary>
    All = Left | Top | Right | Bottom,
}

/// <param name="Margin">Clean gap kept between Clusters and to the screen edge.</param>
/// <param name="SnapDistance">How close an edge must come to a target before the magnet grabs it.</param>
public readonly record struct LayoutSettings(int Margin, int SnapDistance, int MinWidth, int MinHeight);

/// <param name="Mover">Final rectangle of the window the user is moving or resizing.</param>
/// <param name="Others">Final rectangles of every other Cluster, keyed by the caller's id.</param>
/// <param name="Yielded">True when neighbors could not make room and the mover gave way instead.</param>
public sealed record LayoutResult(PixelRect Mover, IReadOnlyDictionary<int, PixelRect> Others, bool Yielded);

public static class ClusterGeometry
{
    // ------------------------------------------------------------------ magnetic snapping

    /// <summary>
    /// Pulls the edges being changed onto nearby targets: other Clusters (flush with a clean
    /// margin, or aligned edge to edge) and the screen edge (also with the margin).
    /// <paramref name="bounds"/> is the area Clusters may occupy, already inset by the margin.
    /// </summary>
    public static PixelRect Snap(PixelRect rect, Edges edges, IReadOnlyCollection<PixelRect> others,
        PixelRect bounds, LayoutSettings settings)
    {
        var horizontalNeighbors = others
            .Where(o => rect.Top < o.Bottom + settings.SnapDistance && o.Top < rect.Bottom + settings.SnapDistance)
            .Select(o => (Lo: o.Left, Hi: o.Right)).ToList();
        var verticalNeighbors = others
            .Where(o => rect.Left < o.Right + settings.SnapDistance && o.Left < rect.Right + settings.SnapDistance)
            .Select(o => (Lo: o.Top, Hi: o.Bottom)).ToList();

        var (left, right) = SnapAxis(rect.Left, rect.Right, edges.HasFlag(Edges.Left), edges.HasFlag(Edges.Right),
            bounds.Left, bounds.Right, horizontalNeighbors, settings.Margin, settings.SnapDistance, settings.MinWidth);
        var (top, bottom) = SnapAxis(rect.Top, rect.Bottom, edges.HasFlag(Edges.Top), edges.HasFlag(Edges.Bottom),
            bounds.Top, bounds.Bottom, verticalNeighbors, settings.Margin, settings.SnapDistance, settings.MinHeight);
        return new PixelRect(left, top, right, bottom);
    }

    private static (int Lo, int Hi) SnapAxis(int lo, int hi, bool loMoves, bool hiMoves, int boundLo, int boundHi,
        List<(int Lo, int Hi)> neighbors, int margin, int snap, int minSize)
    {
        // A move changes both edges together, so the closest target of either edge wins and the
        // whole window shifts by that amount. A resize snaps each changed edge on its own.
        //
        // A "clean gap" target only attracts an edge that is still outside it. Once an edge has gone
        // past it, the window is pushing into the neighbor, and pulling it back would fight the push
        // (the neighbor always ends up exactly one margin away, inside the magnet's range).
        var targetsForLo = new List<int> { boundLo };
        var targetsForHi = new List<int> { boundHi };
        foreach (var (nLo, nHi) in neighbors)
        {
            if (lo >= nHi + margin)
                targetsForLo.Add(nHi + margin); // sit just after the neighbor, with a clean gap
            targetsForLo.Add(nLo);              // align left/top edges
            if (hi <= nLo - margin)
                targetsForHi.Add(nLo - margin); // sit just before the neighbor
            targetsForHi.Add(nHi);              // align right/bottom edges
        }

        if (loMoves && hiMoves)
        {
            var best = 0;
            var bestAbs = int.MaxValue;
            foreach (var t in targetsForLo)
                Consider(t - lo);
            foreach (var t in targetsForHi)
                Consider(t - hi);
            return bestAbs <= snap ? (lo + best, hi + best) : (lo, hi);

            void Consider(int delta)
            {
                if (Math.Abs(delta) < bestAbs)
                {
                    bestAbs = Math.Abs(delta);
                    best = delta;
                }
            }
        }

        var newLo = loMoves ? Closest(lo, targetsForLo, snap) : lo;
        var newHi = hiMoves ? Closest(hi, targetsForHi, snap) : hi;
        return newHi - newLo >= minSize ? (newLo, newHi) : (lo, hi);
    }

    private static int Closest(int value, List<int> targets, int snap)
    {
        var best = value;
        var bestAbs = snap + 1;
        foreach (var t in targets)
        {
            var d = Math.Abs(t - value);
            if (d < bestAbs)
            {
                bestAbs = d;
                best = t;
            }
        }
        return best;
    }

    // ------------------------------------------------------------------ collisions

    /// <summary>
    /// Makes room for <paramref name="mover"/>: every Cluster it touches (closer than the margin)
    /// is pushed away along the axis that costs least, preferring the direction of
    /// <paramref name="motion"/>, and pushed Clusters push their own neighbors in turn. If a
    /// Cluster has nowhere to go (screen edge, jammed row) nothing is pushed and the mover
    /// yields instead, so Clusters never end up overlapping.
    /// </summary>
    public static LayoutResult Resolve(PixelRect mover, Edges edges, PixelPoint motion,
        IReadOnlyDictionary<int, PixelRect> others, PixelRect bounds, LayoutSettings settings)
    {
        if (!others.Values.Any(o => mover.Inflate(settings.Margin).Intersects(o)))
            return new LayoutResult(mover, others, false);

        var pushed = TryPush(mover, edges, motion, others, bounds, settings);
        if (pushed is not null)
            return new LayoutResult(mover, pushed, false);

        var current = mover;
        for (var i = 0; i < 8; i++)
        {
            var hit = others.Values.Where(o => current.Inflate(settings.Margin).Intersects(o)).Cast<PixelRect?>().FirstOrDefault();
            if (hit is null)
                break;
            current = Yield(current, edges, hit.Value, bounds, settings);
        }
        return new LayoutResult(current, others, true);
    }

    private static Dictionary<int, PixelRect>? TryPush(PixelRect mover, Edges edges, PixelPoint motion,
        IReadOnlyDictionary<int, PixelRect> others, PixelRect bounds, LayoutSettings settings)
    {
        var rects = new Dictionary<int, PixelRect>(others);
        var queue = new Queue<(PixelRect Rect, int Id)>();
        queue.Enqueue((mover, int.MinValue));
        var budget = 12 * (others.Count + 1);

        while (queue.Count > 0)
        {
            if (budget-- <= 0)
                return null;
            var (pusher, pusherId) = queue.Dequeue();
            foreach (var id in rects.Keys.ToList())
            {
                if (id == pusherId)
                    continue;
                var other = rects[id];
                if (!pusher.Inflate(settings.Margin).Intersects(other))
                    continue;
                // Resizing only pushes the way the dragged edge is heading; a Cluster that is itself
                // being pushed may shove its neighbors in any direction.
                var allowed = pusherId == int.MinValue ? edges : Edges.All;
                if (!TryPushOne(pusher, other, motion, allowed, mover, bounds, settings.Margin, out var moved))
                    return null;
                rects[id] = moved;
                queue.Enqueue((moved, id));
            }
        }
        return IsValid(mover, rects, settings.Margin) ? rects : null;
    }

    /// <summary>No Cluster may touch the mover's clean margin, and Clusters may not overlap each other.</summary>
    private static bool IsValid(PixelRect mover, Dictionary<int, PixelRect> rects, int margin)
    {
        var list = rects.Values.ToList();
        if (list.Any(r => mover.Inflate(margin).Intersects(r)))
            return false;
        for (var i = 0; i < list.Count; i++)
            for (var j = i + 1; j < list.Count; j++)
                if (list[i].Intersects(list[j]))
                    return false;
        return true;
    }

    private static bool TryPushOne(PixelRect pusher, PixelRect other, PixelPoint motion, Edges allowed,
        PixelRect wall, PixelRect bounds, int margin, out PixelRect moved)
    {
        var candidates = new List<(int Dx, int Dy, double Cost)>();
        if (allowed.HasFlag(Edges.Right))
            AddCandidate(pusher.Right + margin - other.Left, 0, positive: true);
        if (allowed.HasFlag(Edges.Left))
            AddCandidate(pusher.Left - margin - other.Right, 0, positive: false);
        if (allowed.HasFlag(Edges.Bottom))
            AddCandidate(0, pusher.Bottom + margin - other.Top, positive: true);
        if (allowed.HasFlag(Edges.Top))
            AddCandidate(0, pusher.Top - margin - other.Bottom, positive: false);

        foreach (var (dx, dy, _) in candidates.OrderBy(c => c.Cost))
        {
            var candidate = other.Offset(dx, dy);
            // The Cluster being dragged is a wall: a pushed neighbor may not be shoved onto it.
            if (bounds.Contains(candidate) && !wall.Inflate(margin).Intersects(candidate))
            {
                moved = candidate;
                return true;
            }
        }
        moved = other;
        return false;

        void AddCandidate(int dx, int dy, bool positive)
        {
            var d = dx != 0 ? dx : dy;
            if (positive ? d <= 0 : d >= 0)
                return;
            var cost = (double)Math.Abs(d);
            // Prefer pushing the way the mover is heading: it feels like the neighbor is shoved.
            var motionAlong = dx != 0 ? motion.X : motion.Y;
            if (motionAlong != 0 && (motionAlong > 0) == positive)
                cost *= 0.6;
            candidates.Add((dx, dy, cost));
        }
    }

    /// <summary>Moves or shrinks the mover just enough to clear <paramref name="other"/>.</summary>
    private static PixelRect Yield(PixelRect p, Edges edges, PixelRect other, PixelRect bounds, LayoutSettings s)
    {
        var m = s.Margin;
        if (edges != Edges.All)
        {
            // Resizing: pull back only the edges the user is dragging.
            var options = new List<(int Change, PixelRect Result)>();
            if (edges.HasFlag(Edges.Right) && p.Left < other.Left)
                AddIfValid(p.Right - (other.Left - m), new PixelRect(p.Left, p.Top, other.Left - m, p.Bottom));
            if (edges.HasFlag(Edges.Left) && p.Right > other.Right)
                AddIfValid((other.Right + m) - p.Left, new PixelRect(other.Right + m, p.Top, p.Right, p.Bottom));
            if (edges.HasFlag(Edges.Bottom) && p.Top < other.Top)
                AddIfValid(p.Bottom - (other.Top - m), new PixelRect(p.Left, p.Top, p.Right, other.Top - m));
            if (edges.HasFlag(Edges.Top) && p.Bottom > other.Bottom)
                AddIfValid((other.Bottom + m) - p.Top, new PixelRect(p.Left, other.Bottom + m, p.Right, p.Bottom));
            if (options.Count > 0)
                return options.OrderBy(o => o.Change).First().Result;

            void AddIfValid(int change, PixelRect result)
            {
                if (change > 0 && result.Width >= s.MinWidth && result.Height >= s.MinHeight)
                    options.Add((change, result));
            }
        }

        var moves = new List<(int Dx, int Dy)>
        {
            (other.Right + m - p.Left, 0),
            (other.Left - m - p.Right, 0),
            (0, other.Bottom + m - p.Top),
            (0, other.Top - m - p.Bottom),
        };
        var ordered = moves.OrderBy(v => Math.Abs(v.Dx) + Math.Abs(v.Dy)).ToList();
        foreach (var (dx, dy) in ordered)
        {
            if (bounds.Contains(p.Offset(dx, dy)))
                return p.Offset(dx, dy);
        }
        var (fx, fy) = ordered[0];
        return p.Offset(fx, fy);
    }
}

/// <summary>Finds free cells on the desktop icon grid for icons trapped under a Cluster.</summary>
public static class IconGrid
{
    /// <param name="Id">Caller's id for the icon.</param>
    /// <param name="Cell">Top-left of the icon's current grid cell.</param>
    /// <param name="Drawn">Rectangle the icon (picture and label) actually covers.</param>
    public sealed record Icon(int Id, PixelPoint Cell, PixelRect Drawn);

    /// <summary>
    /// For every icon whose drawn rectangle touches a blocked area, returns the nearest grid cell
    /// that is inside <paramref name="area"/>, not occupied by another icon, and not under a
    /// blocked area. Icons that already sit in a free place are left out of the result.
    /// </summary>
    public static IReadOnlyDictionary<int, PixelPoint> Relocate(IReadOnlyList<Icon> icons, PixelPoint cellSize,
        PixelPoint origin, PixelRect area, IReadOnlyList<PixelRect> blocked)
    {
        var moves = new Dictionary<int, PixelPoint>();
        if (cellSize.X <= 0 || cellSize.Y <= 0)
            return moves;

        var trapped = icons.Where(i => blocked.Any(b => b.Intersects(i.Drawn))).OrderBy(i => i.Cell.Y).ThenBy(i => i.Cell.X).ToList();
        if (trapped.Count == 0)
            return moves;

        var trappedIds = trapped.Select(t => t.Id).ToHashSet();
        var occupied = icons.Where(i => !trappedIds.Contains(i.Id)).Select(i => CellOf(i.Cell)).ToHashSet();
        var columns = Math.Max(1, (area.Width - 1) / cellSize.X + 1);
        var rows = Math.Max(1, (area.Height - 1) / cellSize.Y + 1);
        var firstColumn = (int)Math.Ceiling((double)(area.Left - origin.X) / cellSize.X);
        var firstRow = (int)Math.Ceiling((double)(area.Top - origin.Y) / cellSize.Y);

        foreach (var icon in trapped)
        {
            var start = CellOf(icon.Cell);
            var found = FindNearest(start);
            if (found is null)
                continue;
            occupied.Add(found.Value);
            moves[icon.Id] = new PixelPoint(origin.X + found.Value.Column * cellSize.X, origin.Y + found.Value.Row * cellSize.Y);
        }
        return moves;

        (int Column, int Row) CellOf(PixelPoint p) =>
            ((int)Math.Round((double)(p.X - origin.X) / cellSize.X), (int)Math.Round((double)(p.Y - origin.Y) / cellSize.Y));

        PixelRect RectOf((int Column, int Row) cell) =>
            PixelRect.FromSize(origin.X + cell.Column * cellSize.X, origin.Y + cell.Row * cellSize.Y, cellSize.X, cellSize.Y);

        bool IsFree((int Column, int Row) cell)
        {
            if (cell.Column < firstColumn || cell.Column >= firstColumn + columns ||
                cell.Row < firstRow || cell.Row >= firstRow + rows)
                return false;
            if (occupied.Contains(cell))
                return false;
            var rect = RectOf(cell);
            return !area.Contains(rect) ? false : !blocked.Any(b => b.Intersects(rect));
        }

        (int Column, int Row)? FindNearest((int Column, int Row) from)
        {
            var maxRadius = Math.Max(columns, rows);
            for (var radius = 1; radius <= maxRadius; radius++)
            {
                (int Column, int Row)? best = null;
                var bestDistance = double.MaxValue;
                for (var dc = -radius; dc <= radius; dc++)
                {
                    for (var dr = -radius; dr <= radius; dr++)
                    {
                        if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != radius)
                            continue;
                        var cell = (from.Column + dc, from.Row + dr);
                        if (!IsFree(cell))
                            continue;
                        var distance = (double)dc * dc * cellSize.X * cellSize.X + (double)dr * dr * cellSize.Y * cellSize.Y;
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = cell;
                        }
                    }
                }
                if (best is not null)
                    return best;
            }
            return null;
        }
    }
}
