using Fink.Core;

namespace Fink.Core.Tests;

public class DopeSheetTests
{
    private static (Document doc, Layer fill, Layer line, History history) NewDoc()
    {
        var doc = Document.CreateDefault(8, 8);
        return (doc, doc.Layers[0], doc.Layers[1], new History());
    }

    private static string Track(Document doc, Layer layer) =>
        string.Join(' ', doc.Frames.Select(f => doc.DrawingAt(layer, f)?.Number.ToString() ?? "-"));

    private static void Dot(Document doc, History history, Layer layer, int frame, byte color, int x = 1, int y = 1)
    {
        var stroke = new StrokeController();
        Tool tool = layer.Role == LayerRole.Fill ? new FillTool() : new PencilTool();
        Assert.Null(stroke.Begin(tool, doc, doc.Frames[frame], layer, color, new ToolSettings(), x, y, false));
        stroke.End(history);
    }

    [Fact]
    public void Drawing_on_an_empty_cell_makes_a_drawing_and_undo_removes_it()
    {
        var (doc, _, line, history) = NewDoc();
        Assert.Equal("-", Track(doc, line));
        Dot(doc, history, line, 0, 1);
        Assert.Equal("1", Track(doc, line));
        Assert.Single(history.Entries);
        history.Undo();
        Assert.Equal("-", Track(doc, line));
        Assert.Empty(line.Drawings);
        history.Redo();
        Assert.Equal(1, doc.DrawingAt(line, doc.Frames[0])!.Cel.Get(1, 1));
    }

    [Fact]
    public void A_cancelled_stroke_leaves_no_drawing()
    {
        var (doc, _, line, _) = NewDoc();
        var stroke = new StrokeController();
        stroke.Begin(new PencilTool(), doc, doc.Frames[0], line, 1, new ToolSettings(), 1, 1, false);
        stroke.Cancel();
        Assert.Empty(line.Drawings);
    }

    [Fact]
    public void Inserted_frames_hold_and_editing_a_held_drawing_changes_every_frame()
    {
        var (doc, _, line, history) = NewDoc();
        Dot(doc, history, line, 0, 1);
        DocumentOps.InsertFrame(doc, history, 1, NewFrameKind.Hold, line);
        DocumentOps.InsertFrame(doc, history, 2, NewFrameKind.Hold, line);
        Assert.Equal("1 1 1", Track(doc, line));

        Dot(doc, history, line, 2, 5, 3, 3);
        Assert.Equal(5, doc.DrawingAt(line, doc.Frames[0])!.Cel.Get(3, 3));
    }

    [Fact]
    public void New_frame_gives_the_current_layer_a_blank_drawing_while_others_hold()
    {
        var (doc, _, line, history) = NewDoc();
        var bg = DocumentOps.AddLayer(doc, history, 0, "BG", LayerRole.Line);
        Dot(doc, history, bg, 0, 2);
        Dot(doc, history, line, 0, 1);

        DocumentOps.InsertFrame(doc, history, 1, NewFrameKind.Blank, line);
        Assert.Equal("1 2", Track(doc, line));
        Assert.Equal("1 1", Track(doc, bg));
        Assert.Equal(0, doc.DrawingAt(line, doc.Frames[1])!.Cel.Get(1, 1));

        DocumentOps.InsertFrame(doc, history, 2, NewFrameKind.Duplicate, line);
        Assert.Equal("1 2 3", Track(doc, line));
        history.Undo();
        history.Undo();
        Assert.Equal("1", Track(doc, line));
    }

    [Fact]
    public void Fill_follows_line_timing_by_number()
    {
        var (doc, fill, line, history) = NewDoc();
        Dot(doc, history, line, 0, 1);
        Dot(doc, history, fill, 0, 9, 4, 4);
        DocumentOps.InsertFrame(doc, history, 1, NewFrameKind.Duplicate, line);
        DocumentOps.InsertFrame(doc, history, 2, NewFrameKind.Hold, line);
        Assert.Equal("1 2 2", Track(doc, line));
        Assert.Equal("1 2 2", Track(doc, fill));

        // the duplicated fill is its own copy
        Dot(doc, history, fill, 1, 7, 4, 4);
        Assert.Equal(9, doc.DrawingAt(fill, doc.Frames[0])!.Cel.Get(4, 4));
        Assert.Equal(7, doc.DrawingAt(fill, doc.Frames[2])!.Cel.Get(4, 4));

        // reusing line drawing 1 brings fill 1 back with it
        DocumentOps.SetCell(doc, history, line, 2, 1);
        Assert.Equal("1 2 1", Track(doc, fill));
    }

    [Fact]
    public void Fill_cannot_paint_where_its_line_layer_has_nothing()
    {
        var (doc, fill, _, _) = NewDoc();
        var stroke = new StrokeController();
        Assert.NotNull(stroke.Begin(new EraserTool(), doc, doc.Frames[0], fill, 0, new ToolSettings(), 1, 1, false));
        Assert.Empty(fill.Drawings);
    }

    [Fact]
    public void Retime_slides_the_rest_of_the_layer()
    {
        var (doc, _, line, history) = NewDoc();
        Dot(doc, history, line, 0, 1);
        DocumentOps.InsertFrame(doc, history, 1, NewFrameKind.Blank, line);
        DocumentOps.InsertFrame(doc, history, 2, NewFrameKind.Blank, line);
        Assert.Equal("1 2 3", Track(doc, line));

        Assert.True(DocumentOps.Retime(doc, history, line, 0, +1));
        Assert.Equal("1 1 2 3", Track(doc, line)); // grew the timeline
        Assert.Equal(4, doc.Frames.Count);

        Assert.True(DocumentOps.Retime(doc, history, line, 1, -1));
        Assert.Equal("1 2 3 -", Track(doc, line));
        Assert.False(DocumentOps.Retime(doc, history, line, 0, -1));

        history.Undo();
        history.Undo();
        Assert.Equal("1 2 3", Track(doc, line));
    }

    [Fact]
    public void Dragging_a_hold_edge_covers_or_clears_cells()
    {
        var (doc, _, line, history) = NewDoc();
        Dot(doc, history, line, 0, 1);
        for (int i = 1; i < 5; i++) DocumentOps.InsertFrame(doc, history, i, NewFrameKind.Blank, line);
        Assert.Equal("1 2 3 4 5", Track(doc, line));

        DocumentOps.SetHoldEnd(doc, history, line, 0, 2);
        Assert.Equal("1 1 1 4 5", Track(doc, line));
        DocumentOps.SetHoldEnd(doc, history, line, 0, 1);
        Assert.Equal("1 1 - 4 5", Track(doc, line));
    }

    [Fact]
    public void New_drawing_replaces_the_rest_of_the_hold()
    {
        var (doc, _, line, history) = NewDoc();
        Dot(doc, history, line, 0, 1);
        for (int i = 1; i < 4; i++) DocumentOps.InsertFrame(doc, history, i, NewFrameKind.Hold, line);
        Assert.Equal("1 1 1 1", Track(doc, line));
        DocumentOps.NewDrawing(doc, history, line, 2, duplicate: false);
        Assert.Equal("1 1 2 2", Track(doc, line));
    }

    [Fact]
    public void Onion_skin_steps_by_drawing()
    {
        var (doc, _, line, history) = NewDoc();
        Dot(doc, history, line, 0, 1);
        DocumentOps.InsertFrame(doc, history, 1, NewFrameKind.Hold, line);
        DocumentOps.InsertFrame(doc, history, 2, NewFrameKind.Blank, line);
        DocumentOps.InsertFrame(doc, history, 3, NewFrameKind.Hold, line);
        Assert.Equal("1 1 2 2", Track(doc, line));

        Assert.Equal(1, doc.NeighborDrawingFrame(line, 3, -1, 1)); // not frame 2, which is the same drawing
        Assert.Null(doc.NeighborDrawingFrame(line, 3, -1, 2));
        Assert.Equal(2, doc.NeighborDrawingFrame(line, 0, +1, 1));
    }

    [Fact]
    public void Deleting_a_line_layer_leaves_its_fill_with_the_same_timing()
    {
        var (doc, fill, line, history) = NewDoc();
        Dot(doc, history, line, 0, 1);
        Dot(doc, history, fill, 0, 9, 4, 4);
        DocumentOps.InsertFrame(doc, history, 1, NewFrameKind.Hold, line);
        DocumentOps.DeleteLayer(doc, history, doc.Layers.IndexOf(line));
        Assert.False(doc.IsFollower(fill));
        Assert.Equal("1 1", Track(doc, fill));
        history.Undo();
        Assert.True(doc.IsFollower(fill));
    }

    [Fact]
    public void Deleting_a_frame_undoes_with_its_exposures()
    {
        var (doc, _, line, history) = NewDoc();
        Dot(doc, history, line, 0, 1);
        DocumentOps.InsertFrame(doc, history, 1, NewFrameKind.Blank, line);
        DocumentOps.DeleteFrame(doc, history, 1);
        Assert.Equal("1", Track(doc, line));
        history.Undo();
        Assert.Equal("1 2", Track(doc, line));
    }

    [Fact]
    public void Onion_skin_shows_above_an_opaque_layer_underneath()
    {
        var (doc, _, line, history) = NewDoc();
        // a solid background on a line layer at the bottom, held on both frames
        var bg = DocumentOps.AddLayer(doc, history, 0, "BG", LayerRole.Line);
        Array.Fill(doc.GetCel(bg, doc.Frames[0]).Pixels, (byte)5);
        Dot(doc, history, line, 0, 1, 2, 2);
        DocumentOps.InsertFrame(doc, history, 1, NewFrameKind.Blank, line);
        Dot(doc, history, line, 1, 1, 6, 6);

        var dest = new uint[doc.Width * doc.Height];
        var onion = new OnionSettings();
        Compositor.Render(doc, 1, dest, onion);
        uint bgColor = doc.Palette[5].Packed;
        Assert.NotEqual(bgColor, dest[2 * doc.Width + 2]); // drawing 1's ghost
        Assert.Equal(bgColor, dest[4 * doc.Width + 4]);
        onion.Enabled = false;
        Compositor.Render(doc, 1, dest, onion);
        Assert.Equal(bgColor, dest[2 * doc.Width + 2]);
    }

    [Fact]
    public void View_opacity_shows_on_the_canvas_only_and_is_saved()
    {
        var (doc, _, line, history) = NewDoc();
        Dot(doc, history, line, 0, 1, 2, 2);
        line.ViewOpacity = 0.5f;
        var dest = new uint[doc.Width * doc.Height];
        uint full = doc.Palette[1].Packed;

        // anything that isn't the canvas (thumbnails, export, atlases) sees the layer as drawn
        Compositor.Render(doc, 0, dest, null, checker: false);
        Assert.Equal(full, dest[2 * doc.Width + 2]);
        // the canvas blends it over what's under it (here, nothing: black)
        Compositor.Render(doc, 0, dest, null, checker: false, viewOpacity: true);
        Assert.NotEqual(full, dest[2 * doc.Width + 2]);
        line.ViewOpacity = 0f;
        Compositor.Render(doc, 0, dest, null, checker: false, viewOpacity: true);
        Assert.Equal(0xFF000000u, dest[2 * doc.Width + 2] | 0xFF000000u);

        line.ViewOpacity = 0.3f;
        var project = new Project(doc.Palette);
        project.Assets.Add(doc);
        using var ms = new MemoryStream();
        ProjectFile.Write(project, ms);
        ms.Position = 0;
        var back = ProjectFile.Read(ms).Assets[0];
        Assert.Equal(0.3f, back.Layers.Single(l => l.Name == line.Name).ViewOpacity, 3);
    }

    [Fact]
    public void Blocks_move_resize_from_either_end_and_copy()
    {
        var (doc, _, line, history) = NewDoc();
        Dot(doc, history, line, 0, 1);
        for (int i = 1; i < 5; i++) DocumentOps.InsertFrame(doc, history, i, NewFrameKind.Blank, line);
        DocumentOps.SetHoldEnd(doc, history, line, 0, 1);
        Assert.Equal("1 1 3 4 5", Track(doc, line));

        // drag the middle of the 1 1 block two frames right: covers 3 and 4, leaves empties
        DocumentOps.PlaceHold(doc, history, line, 0, 1, 2, 3, copy: false);
        Assert.Equal("- - 1 1 5", Track(doc, line));
        // drag its start edge back to frame 1
        DocumentOps.PlaceHold(doc, history, line, 2, 3, 0, 3, copy: false);
        Assert.Equal("1 1 1 1 5", Track(doc, line));
        // ctrl-drag a copy past the end: the timeline grows
        DocumentOps.PlaceHold(doc, history, line, 4, 4, 5, 6, copy: true);
        Assert.Equal("1 1 1 1 5 5 5", Track(doc, line));
        Assert.Equal(7, doc.Frames.Count);
        history.Undo();
        history.Undo();
        history.Undo();
        Assert.Equal("1 1 3 4 5", Track(doc, line));
    }
}
