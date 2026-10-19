using WindowsCluster.App.Layout;

namespace WindowsCluster.Tests;

public class ClusterGeometryTests
{
    private static readonly LayoutSettings Settings = new(Margin: 8, SnapDistance: 14, MinWidth: 100, MinHeight: 80);
    private static readonly PixelRect Bounds = new(8, 8, 1912, 1032); // 1920x1040 work area inset by the margin

    private static PixelRect R(int x, int y, int w, int h) => PixelRect.FromSize(x, y, w, h);

    // ---------------------------------------------------------------- snapping

    [Fact]
    public void Move_snaps_to_neighbor_with_clean_margin()
    {
        var neighbor = R(100, 100, 300, 200);              // right edge at 400, clean gap puts the next left edge at 408
        var moving = R(408 + 5, 120, 200, 150);
        var snapped = ClusterGeometry.Snap(moving, Edges.All, [neighbor], Bounds, Settings);
        Assert.Equal(408, snapped.Left);
        Assert.Equal(200, snapped.Width);
    }

    [Fact]
    public void Pushing_into_a_neighbor_is_not_pulled_back_by_the_magnet()
    {
        // The neighbor sits exactly one margin left of where the mover started; the mover then
        // goes 13 px further in. The magnet (14 px) must not drag it back out.
        var neighbor = R(100, 100, 300, 200);              // right edge at 400, clean gap at 408
        var penetrating = R(408 - 13, 120, 200, 150);
        var snapped = ClusterGeometry.Snap(penetrating, Edges.All, [neighbor], Bounds, Settings);
        Assert.Equal(penetrating, snapped);
    }

    [Fact]
    public void Move_aligns_top_edges_of_neighbors()
    {
        var neighbor = R(100, 100, 300, 200);
        var moving = R(412, 94, 200, 150);                 // next to the neighbor, 6 px above its top
        var snapped = ClusterGeometry.Snap(moving, Edges.All, [neighbor], Bounds, Settings);
        Assert.Equal(100, snapped.Top);
    }

    [Fact]
    public void Move_does_not_snap_beyond_the_snap_distance()
    {
        var neighbor = R(100, 100, 300, 200);
        var moving = R(408 + 40, 120, 200, 150);
        var snapped = ClusterGeometry.Snap(moving, Edges.All, [neighbor], Bounds, Settings);
        Assert.Equal(moving, snapped);
    }

    [Fact]
    public void Move_snaps_to_screen_edge_margin()
    {
        var moving = R(15, 500, 200, 150);                 // 7 px from the margin line at x=8
        var snapped = ClusterGeometry.Snap(moving, Edges.All, [], Bounds, Settings);
        Assert.Equal(8, snapped.Left);
    }

    [Fact]
    public void Resize_only_moves_the_edge_being_dragged()
    {
        var neighbor = R(600, 100, 300, 200);              // left edge at 600
        var resizing = R(100, 100, 480, 200);              // right edge at 580, snaps to 592 (600 - margin)
        var snapped = ClusterGeometry.Snap(resizing, Edges.Right, [neighbor], Bounds, Settings);
        Assert.Equal(592, snapped.Right);
        Assert.Equal(100, snapped.Left);
        Assert.Equal(200, snapped.Height);
    }

    [Fact]
    public void Snapping_ignores_neighbors_that_are_far_away_vertically()
    {
        var neighbor = R(100, 900, 300, 100);              // far below
        var moving = R(417, 100, 200, 150);                // would snap to its margin if it were near
        var snapped = ClusterGeometry.Snap(moving, Edges.All, [neighbor], Bounds, Settings);
        Assert.Equal(moving, snapped);
    }

    // ---------------------------------------------------------------- collisions

    [Fact]
    public void No_overlap_changes_nothing()
    {
        var others = new Dictionary<int, PixelRect> { [1] = R(800, 100, 200, 200) };
        var result = ClusterGeometry.Resolve(R(100, 100, 200, 200), Edges.All, new PixelPoint(10, 0), others, Bounds, Settings);
        Assert.Equal(R(100, 100, 200, 200), result.Mover);
        Assert.Equal(others[1], result.Others[1]);
        Assert.False(result.Yielded);
    }

    [Fact]
    public void Dragging_into_a_neighbor_pushes_it_along_the_motion()
    {
        var others = new Dictionary<int, PixelRect> { [1] = R(500, 100, 200, 200) };
        var mover = R(350, 110, 200, 200);                 // overlaps; moving right
        var result = ClusterGeometry.Resolve(mover, Edges.All, new PixelPoint(20, 0), others, Bounds, Settings);
        Assert.False(result.Yielded);
        Assert.Equal(mover, result.Mover);
        Assert.Equal(558, result.Others[1].Left);          // mover.Right (550) + margin
        Assert.Equal(100, result.Others[1].Top);           // not pushed sideways
    }

    [Fact]
    public void Pushed_neighbors_push_their_own_neighbors()
    {
        var others = new Dictionary<int, PixelRect>
        {
            [1] = R(500, 100, 200, 200),
            [2] = R(708, 100, 200, 200),                    // sits right behind #1 with a clean gap
        };
        var mover = R(350, 110, 200, 200);
        var result = ClusterGeometry.Resolve(mover, Edges.All, new PixelPoint(20, 0), others, Bounds, Settings);
        Assert.False(result.Yielded);
        Assert.Equal(558, result.Others[1].Left);
        Assert.Equal(766, result.Others[2].Left);          // #1.Right (758) + margin
        AssertNoOverlaps(result);
    }

    [Fact]
    public void Neighbor_that_cannot_move_makes_the_mover_yield()
    {
        // The neighbor is already at the right screen edge, so pushing it any further is impossible.
        var others = new Dictionary<int, PixelRect> { [1] = R(1708, 100, 204, 200) };
        var mover = R(1600, 110, 200, 200);
        var result = ClusterGeometry.Resolve(mover, Edges.All, new PixelPoint(30, 0), others, Bounds, Settings);
        Assert.False(result.Mover.Inflate(Settings.Margin).Intersects(result.Others[1]));
        AssertNoOverlaps(result);
    }

    [Fact]
    public void Resizing_into_a_blocked_neighbor_shrinks_the_dragged_edge()
    {
        var others = new Dictionary<int, PixelRect> { [1] = R(1708, 100, 204, 200) };
        var mover = R(1500, 100, 300, 200);                // right edge at 1800 overlaps the neighbor
        var result = ClusterGeometry.Resolve(mover, Edges.Right, new PixelPoint(0, 0), others, Bounds, Settings);
        Assert.Equal(1500, result.Mover.Left);
        Assert.Equal(1700, result.Mover.Right);            // neighbor.Left (1708) - margin
        Assert.Equal(others[1], result.Others[1]);
        AssertNoOverlaps(result);
    }

    [Fact]
    public void Resizing_pushes_the_neighbor_only_in_the_direction_of_the_dragged_edge()
    {
        var others = new Dictionary<int, PixelRect> { [1] = R(500, 100, 200, 200) };
        var mover = R(100, 100, 450, 200);                 // right edge at 550 now overlaps the neighbor
        var result = ClusterGeometry.Resolve(mover, Edges.Right, new PixelPoint(0, 0), others, Bounds, Settings);
        Assert.False(result.Yielded);
        Assert.Equal(558, result.Others[1].Left);
        Assert.Equal(100, result.Others[1].Top);
    }

    [Fact]
    public void Dragging_down_pushes_a_neighbor_down_when_there_is_room()
    {
        var others = new Dictionary<int, PixelRect> { [1] = R(300, 400, 200, 200) };
        var mover = R(280, 250, 200, 200);                  // overlaps from above, moving down
        var result = ClusterGeometry.Resolve(mover, Edges.All, new PixelPoint(0, 25), others, Bounds, Settings);
        Assert.False(result.Yielded);
        Assert.Equal(458, result.Others[1].Top);            // mover.Bottom (450) + margin
        Assert.Equal(300, result.Others[1].Left);
    }

    [Fact]
    public void Pushed_neighbors_stay_inside_the_bounds_and_nothing_overlaps()
    {
        var others = new Dictionary<int, PixelRect>
        {
            [1] = R(1500, 100, 200, 200),
            [2] = R(1708, 100, 200, 200),
        };
        var result = ClusterGeometry.Resolve(R(1400, 110, 200, 200), Edges.All, new PixelPoint(30, 0), others, Bounds, Settings);
        foreach (var rect in result.Others.Values)
            Assert.True(Bounds.Contains(rect), $"{rect} outside bounds");
        AssertNoOverlaps(result);
    }

    [Fact]
    public void Neighbors_squeezed_against_the_edge_stack_upwards_without_touching_the_mover()
    {
        // Two Clusters at the left edge; the mover plows in from the right on the lower row.
        var others = new Dictionary<int, PixelRect> { [0] = R(8, 532, 240, 180), [1] = R(8, 720, 240, 180) };
        var mover = R(150, 720, 240, 180);
        var result = ClusterGeometry.Resolve(mover, Edges.All, new PixelPoint(-25, 0), others, Bounds, Settings);
        AssertNoOverlaps(result);
        Assert.False(result.Mover.Inflate(Settings.Margin).Intersects(result.Others[1]));
        Assert.False(result.Mover.Inflate(Settings.Margin).Intersects(result.Others[0]));
        foreach (var rect in result.Others.Values)
            Assert.True(Bounds.Contains(rect));
    }

    [Fact]
    public void Random_layouts_never_end_with_overlaps_or_neighbors_outside_the_bounds()
    {
        var random = new Random(12345);
        for (var trial = 0; trial < 3000; trial++)
        {
            // A valid starting layout of up to 5 Clusters.
            var others = new Dictionary<int, PixelRect>();
            for (var id = 0; id < random.Next(1, 6); id++)
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    var w = random.Next(160, 420);
                    var h = random.Next(110, 320);
                    var candidate = R(random.Next(8, 1912 - w), random.Next(8, 1032 - h), w, h);
                    if (others.Values.All(o => !o.Inflate(Settings.Margin).Intersects(candidate)))
                    {
                        others[id] = candidate;
                        break;
                    }
                }
            }
            if (others.Count == 0)
                continue;

            var mw = random.Next(160, 420);
            var mh = random.Next(110, 320);
            var mover = R(random.Next(0, 1920 - mw), random.Next(0, 1040 - mh), mw, mh);
            var motion = new PixelPoint(random.Next(-30, 31), random.Next(-30, 31));
            var snapped = ClusterGeometry.Snap(mover, Edges.All, others.Values.ToList(), Bounds, Settings);
            var result = ClusterGeometry.Resolve(snapped, Edges.All, motion, others, Bounds, Settings);

            foreach (var o in result.Others.Values)
                Assert.False(result.Mover.Intersects(o), $"trial {trial}: mover {result.Mover} overlaps {o}");
            var list = result.Others.Values.ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                    Assert.False(list[i].Intersects(list[j]), $"trial {trial}: {list[i]} overlaps {list[j]}");
            if (!result.Yielded)
                foreach (var o in result.Others.Values)
                    Assert.True(Bounds.Contains(o), $"trial {trial}: {o} outside bounds");
        }
    }

    // ---------------------------------------------------------------- icon grid

    private static readonly PixelPoint Cell = new(76, 96);

    private static IconGrid.Icon Icon(int id, int column, int row) =>
        new(id, new PixelPoint(column * 76, row * 96), PixelRect.FromSize(column * 76 + 8, row * 96 + 4, 60, 88));

    [Fact]
    public void Trapped_icon_moves_to_the_nearest_free_cell_outside_the_cluster()
    {
        var area = new PixelRect(0, 0, 760, 960);
        var cluster = R(70, 90, 300, 200).Inflate(8);
        var icons = new[] { Icon(1, 2, 1), Icon(2, 7, 7) }; // #1 is under the cluster, #2 is far away
        var moves = IconGrid.Relocate(icons, Cell, new PixelPoint(0, 0), area, [cluster]);
        Assert.True(moves.ContainsKey(1));
        Assert.False(moves.ContainsKey(2));
        Assert.False(cluster.Intersects(PixelRect.FromSize(moves[1].X, moves[1].Y, 76, 96)));
    }

    [Fact]
    public void Relocated_icons_never_share_a_cell_or_take_an_occupied_one()
    {
        var area = new PixelRect(0, 0, 760, 960);
        var cluster = R(0, 0, 230, 290);                    // covers columns 0-2, rows 0-2
        var icons = new List<IconGrid.Icon>();
        var id = 1;
        for (var c = 0; c < 3; c++)
            for (var r = 0; r < 3; r++)
                icons.Add(Icon(id++, c, r));
        icons.Add(Icon(99, 3, 0));                          // a free icon just outside, occupying its cell
        var moves = IconGrid.Relocate(icons, Cell, new PixelPoint(0, 0), area, [cluster]);
        Assert.Equal(9, moves.Count);
        Assert.Equal(9, moves.Values.Distinct().Count());
        Assert.DoesNotContain(new PixelPoint(3 * 76, 0), moves.Values);
        foreach (var p in moves.Values)
            Assert.False(cluster.Intersects(PixelRect.FromSize(p.X, p.Y, 76, 96)));
    }

    [Fact]
    public void Icons_outside_every_cluster_are_left_alone()
    {
        var area = new PixelRect(0, 0, 760, 960);
        var moves = IconGrid.Relocate([Icon(1, 5, 5)], Cell, new PixelPoint(0, 0), area, [R(0, 0, 100, 100)]);
        Assert.Empty(moves);
    }

    private static void AssertNoOverlaps(LayoutResult result)
    {
        var all = new List<PixelRect> { result.Mover };
        all.AddRange(result.Others.Values);
        for (var i = 0; i < all.Count; i++)
            for (var j = i + 1; j < all.Count; j++)
                Assert.False(all[i].Intersects(all[j]), $"{all[i]} overlaps {all[j]}");
    }
}
