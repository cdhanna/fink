using Fink.Core;

namespace Fink.Core.Tests;

public class PointsDataTests
{
    /// <summary>16x16 hero, line drawings 1 1 2 1 (2 is new), a hand point and a pivot, normal + glow channels.</summary>
    private static (Project p, Document hero, Layer line, History h, PointTrack hand, PointTrack pivot) Sample()
    {
        var p = Project.CreateDefault(16, 16);
        var hero = p.Assets[0];
        hero.Name = "hero";
        var h = new History();
        var line = hero.Layers[1];
        Array.Fill(hero.GetCel(line, hero.Frames[0]).Pixels, (byte)1, 0, 32); // two rows of ink
        DocumentOps.InsertFrame(hero, h, 1, NewFrameKind.Hold, line);
        DocumentOps.InsertFrame(hero, h, 2, NewFrameKind.Blank, line);
        Array.Fill(hero.GetCel(line, hero.Frames[2]).Pixels, (byte)2, 0, 16);
        DocumentOps.InsertFrame(hero, h, 3, NewFrameKind.Blank, line);
        DocumentOps.SetCell(hero, h, line, 3, 1);
        var hand = DocumentOps.AddPoint(hero, h, "hand", line);
        var pivot = DocumentOps.AddPoint(hero, h, "pivot", line, pivot: true);
        return (p, hero, line, h, hand, pivot);
    }

    [Fact]
    public void Points_ride_on_drawings_through_holds_and_reuse()
    {
        var (_, hero, _, h, hand, pivot) = Sample();
        Assert.Null(hero.PointAt(hand, 0, out _));
        Assert.Equal(new PointXY(8, 8), hero.PivotAt(0)); // no pivot set: the middle

        Assert.True(DocumentOps.SetPoint(hero, h, hand, 0, new PointXY(3, 4)));
        // frame 1 holds drawing 1, frame 3 reuses it: same point, their own
        Assert.Equal(new PointXY(3, 4), hero.PointAt(hand, 1, out var own1));
        Assert.True(own1);
        Assert.Equal(new PointXY(3, 4), hero.PointAt(hand, 3, out var own3));
        Assert.True(own3);
        // drawing 2 hasn't got one: it inherits from the frame before
        Assert.Equal(new PointXY(3, 4), hero.PointAt(hand, 2, out var own2));
        Assert.False(own2);

        DocumentOps.SetPoint(hero, h, hand, 2, new PointXY(9, 9));
        Assert.Equal(new PointXY(9, 9), hero.PointAt(hand, 2, out _));
        h.Undo();
        Assert.False(hero.PointAt(hand, 2, out _) is { } p && p == new PointXY(9, 9));

        DocumentOps.SetPoint(hero, h, pivot, 0, new PointXY(8, 15));
        Assert.Equal(new PointXY(8, 15), hero.PivotAt(3));
    }

    [Fact]
    public void Points_channels_and_maps_survive_a_round_trip()
    {
        var (p, hero, line, h, hand, _) = Sample();
        DocumentOps.SetPoint(hero, h, hand, 0, new PointXY(3, 4));
        var normal = p.FindChannel("normal")!;
        hero.DrawingAt(line, hero.Frames[0])!.Channel(normal.Id).Pixels[5] = 7;
        p.Atlases[0].Maps.Add(new MapConfig { Name = "n", R = "normal.x", G = "normal.y", B = "normal.z" });

        using var ms = new MemoryStream();
        ProjectFile.Write(p, ms);
        ms.Position = 0;
        var back = ProjectFile.Read(ms);
        var bh = back.Assets[0];
        var bline = bh.Layers[1];
        var bhand = bh.Points.Single(t => t.Name == "hand");
        Assert.Equal(hand.Id, bhand.Id);
        Assert.Same(bline, bhand.Layer);
        Assert.True(bh.Pivot!.IsPivot);
        Assert.Equal(new PointXY(3, 4), bh.PointAt(bhand, 0, out _));
        Assert.Equal(new[] { "normal", "emissive" }, back.Channels.Select(c => c.Name));
        Assert.Equal(7, bh.DrawingAt(bline, bh.Frames[0])!.Channels[normal.Id].Pixels[5]);
        Assert.Equal("normal.x", back.Atlases[0].Maps.Single().R);
    }

    [Fact]
    public void Duplicating_a_drawing_copies_its_points_and_data()
    {
        var (p, hero, line, h, hand, _) = Sample();
        DocumentOps.SetPoint(hero, h, hand, 0, new PointXY(3, 4));
        var glow = p.FindChannel("emissive")!;
        hero.DrawingAt(line, hero.Frames[0])!.Channel(glow.Id).Pixels[0] = 200;
        DocumentOps.NewDrawing(hero, h, line, 0, duplicate: true);
        var copy = hero.DrawingAt(line, hero.Frames[0])!;
        Assert.Equal(new PointXY(3, 4), copy.Points[hand.Id]);
        Assert.Equal(200, copy.Channels[glow.Id].Pixels[0]);
        Assert.NotSame(hero.DrawingAt(line, hero.Frames[3])!.Channels[glow.Id], copy.Channels[glow.Id]);
    }

    [Fact]
    public void Painting_data_fills_a_shape_and_stays_on_drawn_pixels()
    {
        var (p, hero, line, h, _, _) = Sample();
        var glow = p.FindChannel("emissive")!;
        var stroke = new StrokeController();
        Assert.Null(stroke.Begin(new FillTool(), hero, hero.Frames[0], line, 180, new ToolSettings(), 2, 0, false, channel: glow));
        stroke.End(h);
        var data = hero.DrawingAt(line, hero.Frames[0])!.Channels[glow.Id];
        Assert.Equal(180, data.Get(15, 1));  // the whole ink shape (two rows)
        Assert.Equal(0, data.Get(0, 2));     // nothing drawn there
        h.Undo();
        Assert.Equal(0, data.Get(15, 1));
        // no drawing on an empty cell: refused
        var empty = DocumentOps.AddLayer(hero, h, 2, "Empty", LayerRole.Line);
        Assert.NotNull(stroke.Begin(new PencilTool(), hero, hero.Frames[0], empty, 50, new ToolSettings(), 0, 0, false, channel: glow));
    }

    [Fact]
    public void Atlas_frames_carry_pivots_points_and_map_data()
    {
        var (p, hero, line, h, hand, pivot) = Sample();
        DocumentOps.SetPoint(hero, h, hand, 0, new PointXY(3, 4));
        DocumentOps.SetPoint(hero, h, pivot, 0, new PointXY(8, 15));
        var normal = p.FindChannel("normal")!;
        var glow = p.FindChannel("emissive")!;
        var d1 = hero.DrawingAt(line, hero.Frames[0])!;
        d1.Channel(glow.Id).Pixels[0] = 99;
        d1.Channel(normal.Id).Pixels[0] = 2; // facing right, tilted 45°
        var config = p.Atlases[0];
        config.Maps.Add(new MapConfig { Name = "light", R = "emissive", G = "normal.x", B = "7", A = "255" });

        var b = AtlasBuilder.Build(p, config);
        var whole = b.Clips.Single(c => c.Asset == hero && c.Name == "");
        Assert.All(whole.Frames, f => Assert.Equal(new PointXY(8, 15), f.Pivot));
        Assert.Equal(new PointXY(3, 4), whole.Frames[0].Points![0]);

        var cell = b.Cells[whole.Frames[0].Cell];
        var map = AtlasExport.MapPixels(p, b, config.Maps[0]);
        uint px = map[cell.Y * b.Width + cell.X];
        Assert.Equal(99u, px & 0xFF);                                     // R: emissive
        Assert.True(((px >> 8) & 0xFF) > 200);                            // G: normal.x, pointing right
        Assert.Equal(7u, (px >> 16) & 0xFF);                              // B: a constant
        Assert.Equal(255u, px >> 24);                                     // A
        Assert.Equal(0u, map[0]);                                         // outside every picture

        var code = FadeBasicBindings.Generate(p, b, AtlasExport.Manifest(b, p.Palette));
        Assert.Contains($"#constant POINT_HERO_HAND {hand.Id}", code);
        Assert.Contains("#constant MAP_LIGHT 0", code);
        Assert.DoesNotContain("POINT_HERO_PIVOT", code);
    }

    [Fact]
    public void A_map_naming_a_missing_channel_says_so()
    {
        var (p, _, _, _, _, _) = Sample();
        var config = p.Atlases[0];
        config.Maps.Add(new MapConfig { Name = "bad", R = "sparkle" });
        var b = AtlasBuilder.Build(p, config);
        var e = Assert.Throws<InvalidOperationException>(() => AtlasExport.MapPixels(p, b, config.Maps[0]));
        Assert.Contains("sparkle", e.Message);
    }

    [Fact]
    public void Keyed_points_move_through_holds_and_split_the_export()
    {
        var (p, hero, line, h, _, _) = Sample();
        var aim = DocumentOps.AddPoint(hero, h, "aim", line, keyed: true);
        // frames 0 and 1 hold drawing 1; the key moves the point across that hold
        DocumentOps.SetPoint(hero, h, aim, 0, new PointXY(0, 0));
        DocumentOps.SetPoint(hero, h, aim, 3, new PointXY(9, 0));
        aim = DocumentOps.SetSmooth(hero, h, aim, true);
        Assert.Equal(new PointXY(3, 0), hero.PointAt(aim, 1, out var own));
        Assert.False(own);
        aim = DocumentOps.SetSmooth(hero, h, aim, false);
        Assert.Equal(new PointXY(0, 0), hero.PointAt(aim, 1, out _));
        aim = DocumentOps.SetSmooth(hero, h, aim, true);

        var b = AtlasBuilder.Build(p, p.Atlases[0]);
        var whole = b.Clips.Single(c => c.Asset == hero && c.Name == "");
        // the 1 1 hold is two frames now (the point moves), but the same picture
        Assert.Equal(4, whole.Frames.Count);
        Assert.Equal(whole.Frames[0].Cell, whole.Frames[1].Cell);
        int k = hero.Points.Where(t => !t.IsPivot).ToList().IndexOf(aim);
        Assert.Equal(new[] { 0, 3, 6, 9 }, whole.Frames.Select(f => f.Points![k].X));

        // keys survive a round trip, by frame
        using var ms = new MemoryStream();
        ProjectFile.Write(p, ms);
        ms.Position = 0;
        var back = ProjectFile.Read(ms).Assets[0];
        var baim = back.Points.Single(t => t.Name == "aim");
        Assert.True(baim.Keyed && baim.Smooth);
        Assert.Equal(new PointXY(6, 0), back.PointAt(baim, 2, out _));

        // keys go with their frames: inserting one before the second key pushes it along
        DocumentOps.InsertFrame(hero, h, 2, NewFrameKind.Hold, line);
        Assert.Equal(new PointXY(9, 0), hero.PointAt(aim, 4, out var keyHere));
        Assert.True(keyHere);
        DocumentOps.MoveKey(hero, h, aim, 4, 2);
        Assert.Equal(new PointXY(9, 0), hero.PointAt(aim, 2, out _));
        h.Undo();
        Assert.Equal(new PointXY(9, 0), hero.PointAt(aim, 4, out _));
    }
}
