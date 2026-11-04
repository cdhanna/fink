using Fink.Core;

namespace Fink.Core.Tests;

public class ExportTests
{
    /// <summary>Line drawings 1 1 2 3 3 2 2 1 on 8 frames at 12 fps; a reference layer and a held BG.</summary>
    private static (Document doc, Layer line, History history) Shot()
    {
        var doc = Document.CreateDefault(4, 4);
        var history = new History();
        var line = doc.Layers[1];
        var bg = DocumentOps.AddLayer(doc, history, 0, "BG", LayerRole.Line);
        var reference = DocumentOps.AddLayer(doc, history, 0, "Ref", LayerRole.Reference);
        doc.GetCel(bg, doc.Frames[0]).Pixels[15] = 3;
        Array.Fill(doc.GetCel(reference, doc.Frames[0]).Pixels, (byte)7);
        doc.GetCel(line, doc.Frames[0]).Pixels[0] = 1;
        for (int i = 1; i < 8; i++) DocumentOps.InsertFrame(doc, history, i, NewFrameKind.Hold, line);
        DocumentOps.NewDrawing(doc, history, line, 2, false); // 1 1 2 2 2 2 2 2
        doc.GetCel(line, doc.Frames[2]).Pixels[1] = 1;
        DocumentOps.NewDrawing(doc, history, line, 3, false); // 1 1 2 3 3 3 3 3
        doc.GetCel(line, doc.Frames[3]).Pixels[2] = 1;
        DocumentOps.SetCell(doc, history, line, 5, 2);
        DocumentOps.SetCell(doc, history, line, 6, 2);
        DocumentOps.SetCell(doc, history, line, 7, 1); // 1 1 2 3 3 2 2 1
        return (doc, line, history);
    }

    [Fact]
    public void Holds_merge_and_repeats_share_cells()
    {
        var (doc, _, _) = Shot();
        var r = Exporter.Export(doc, new ExportOptions { Name = "hero" });
        Assert.Equal(3, r.Cells);
        Assert.Equal(new[] { 0, 1, 2, 1, 0 }, r.Anim.Frames.Select(f => f.Cell));
        // 12 fps: 2 frames = 167ms, 1 = 83; the total is exactly 8 frames' worth
        Assert.Equal(new[] { 167, 83, 167, 166, 84 }, r.Anim.Frames.Select(f => f.Ms));
        Assert.Equal(667, r.Anim.Frames.Sum(f => f.Ms));
    }

    [Fact]
    public void Atlas_has_the_pictures_without_reference_layers()
    {
        var (doc, _, _) = Shot();
        var r = Exporter.Export(doc, new ExportOptions { Name = "hero", Columns = 3, Padding = 1 });
        var (px, w, h) = Png.Decode(r.Png);
        Assert.Equal((3 * 4 + 4, 4 + 2), (w, h));
        Assert.Equal((r.AtlasWidth, r.AtlasHeight), (w, h));
        var cell1 = r.Anim.Cells[1];
        Assert.Equal(doc.Palette[1].Packed, px[cell1.Y * w + cell1.X + 1]);   // drawing 2's pixel
        Assert.Equal(0u, px[cell1.Y * w + cell1.X + 0]);                       // drawing 1's isn't there
        Assert.Equal(doc.Palette[3].Packed, px[(cell1.Y + 3) * w + cell1.X + 3]); // held BG on every cell
        Assert.Equal(0u, px[cell1.Y * w + cell1.X + 2]);                       // reference left out
        Assert.Equal(0u, px[0]);                                               // padding
    }

    [Fact]
    public void Tags_split_runs_and_map_to_frames()
    {
        var (doc, _, history) = Shot();
        // "up" starts in the middle of the 3 3 hold, so that hold splits in two
        DocumentOps.AddTag(doc, history, "down", 0, 3);
        DocumentOps.AddTag(doc, history, "up", 4, 7, LoopMode.PingPong);
        var r = Exporter.Export(doc, new ExportOptions { Name = "hero" });
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 0 }, r.Anim.Frames.Select(f => f.Cell));
        var down = r.Anim.Tags.Single(t => t.Name == "down");
        var up = r.Anim.Tags.Single(t => t.Name == "up");
        Assert.Equal((0, 2, "forward"), (down.From, down.To, down.Loop));
        Assert.Equal((3, 5, "pingpong"), (up.From, up.To, up.Loop));
        Assert.Contains("\"pingpong\"", r.Json);
        Assert.Contains("\"ms\"", r.Json);
    }

    [Fact]
    public void Tags_follow_inserted_and_deleted_frames()
    {
        var (doc, line, history) = Shot();
        var tag = DocumentOps.AddTag(doc, history, "walk", 2, 4);
        DocumentOps.InsertFrame(doc, history, 3, NewFrameKind.Hold, line);
        Assert.Equal((2, 5), doc.TagRange(doc.Tags[0]));
        DocumentOps.DeleteFrame(doc, history, 2);
        Assert.Equal((2, 4), doc.TagRange(doc.Tags[0]));
        history.Undo();
        history.Undo();
        Assert.Equal((2, 4), doc.TagRange(doc.Tags[0]));
        DocumentOps.UpdateTag(doc, history, doc.Tags[0], name: "run", loop: LoopMode.Once);
        Assert.Equal("run", doc.Tags[0].Name);
        DocumentOps.RemoveTag(doc, history, doc.Tags[0]);
        Assert.Empty(doc.Tags);
        history.Undo();
        Assert.Equal("run", doc.Tags[0].Name);
        Assert.NotEqual(tag, doc.Tags[0]);
    }

    [Fact]
    public void Png_round_trips()
    {
        var px = new uint[] { 0xFF0000FF, 0x00000000, 0x8000FF00, 0xFFFFFFFF, 0x12345678, 0 };
        var (back, w, h) = Png.Decode(Png.Encode(px, 3, 2));
        Assert.Equal((3, 2), (w, h));
        Assert.Equal(px, back);
    }
}
