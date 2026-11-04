using Fink.Core;

namespace Fink.Core.Tests;

public class CoreTests
{
    private static (Document doc, Layer fill, Layer line, Frame frame) NewDoc(int w = 16, int h = 16)
    {
        var doc = Document.CreateDefault(w, h);
        return (doc, doc.Layers[0], doc.Layers[1], doc.Frames[0]);
    }

    private static void Draw(Document doc, History history, Tool tool, Layer layer, byte color,
        params (int x, int y)[] points)
    {
        var stroke = new StrokeController();
        var settings = new ToolSettings();
        Assert.Null(stroke.Begin(tool, doc, doc.Frames[0], layer, color, settings, points[0].x, points[0].y, false));
        foreach (var p in points.Skip(1)) stroke.Drag(p.x, p.y, false);
        stroke.End(history);
    }

    /// <summary>Draws a closed square outline of line art from (x0,y0) to (x1,y1).</summary>
    private static void Box(Cel line, int x0, int y0, int x1, int y1, byte c = 1)
    {
        for (int x = x0; x <= x1; x++) { line.Pixels[y0 * line.Width + x] = c; line.Pixels[y1 * line.Width + x] = c; }
        for (int y = y0; y <= y1; y++) { line.Pixels[y * line.Width + x0] = c; line.Pixels[y * line.Width + x1] = c; }
    }

    [Fact]
    public void Pencil_refuses_fill_layer_and_fill_refuses_line_layer()
    {
        var (doc, fill, line, frame) = NewDoc();
        var s = new StrokeController();
        Assert.NotNull(s.Begin(new PencilTool(), doc, frame, fill, 1, new ToolSettings(), 0, 0, false));
        Assert.NotNull(s.Begin(new FillTool(), doc, frame, line, 1, new ToolSettings(), 0, 0, false));
        Assert.False(s.Active);
    }

    [Fact]
    public void Fill_stays_inside_line_art_on_another_layer()
    {
        var (doc, fill, line, frame) = NewDoc();
        Box(doc.GetCel(line, frame), 2, 2, 8, 8);
        var history = new History();
        Draw(doc, history, new FillTool(), fill, 5, (5, 5));

        var cel = doc.GetCel(fill, frame);
        Assert.Equal(5, cel.Get(5, 5));
        Assert.Equal(5, cel.Get(3, 3));      // reaches right up to the line
        Assert.Equal(0, cel.Get(12, 12));    // outside the box untouched
        Assert.Equal(0, doc.GetCel(line, frame).Get(5, 5)); // line layer untouched
    }

    [Fact]
    public void Gap_close_seals_a_one_pixel_gap_but_plain_fill_leaks()
    {
        var (doc, fill, line, frame) = NewDoc();
        var lineCel = doc.GetCel(line, frame);
        Box(lineCel, 2, 2, 10, 10);
        lineCel.Pixels[2 * lineCel.Width + 6] = 0; // 1px gap in the top edge

        var settings = new ToolSettings { GapClose = 1 };
        var history = new History();
        var s = new StrokeController();
        s.Begin(new FillTool(), doc, frame, fill, 5, settings, 6, 6, false);
        s.End(history);
        var fillCel = doc.GetCel(fill, frame);
        Assert.Equal(5, fillCel.Get(3, 3));
        Assert.Equal(0, fillCel.Get(0, 0));

        history.Undo();
        settings.GapClose = 0;
        s.Begin(new FillTool(), doc, frame, fill, 5, settings, 6, 6, false);
        s.End(history);
        // undo took the fill drawing away with the fill, so this is a new one
        Assert.Equal(5, doc.GetCel(fill, frame).Get(0, 0)); // leaked
    }

    [Fact]
    public void Undo_and_redo_restore_pixels()
    {
        var (doc, _, line, frame) = NewDoc(64, 64);
        var history = new History();
        Draw(doc, history, new PencilTool(), line, 3, (1, 1), (40, 40));
        var cel = doc.GetCel(line, frame);
        Assert.Equal(3, cel.Get(40, 40));

        history.Undo();
        Assert.Equal(0, cel.Get(40, 40));
        Assert.Equal(0, cel.Get(1, 1));
        history.Redo();
        Assert.Equal(3, cel.Get(40, 40));
    }

    [Fact]
    public void Pixel_perfect_removes_L_corners()
    {
        var (doc, _, line, frame) = NewDoc();
        var history = new History();
        // a staircase drawn as orthogonal steps: (0,0) (1,0) (1,1) — the corner (1,0) goes
        Draw(doc, history, new PencilTool(), line, 1, (0, 0), (1, 0), (1, 1));
        var cel = doc.GetCel(line, frame);
        Assert.Equal(1, cel.Get(0, 0));
        Assert.Equal(0, cel.Get(1, 0));
        Assert.Equal(1, cel.Get(1, 1));
    }

    [Fact]
    public void History_jump_moves_through_steps()
    {
        var (doc, _, line, frame) = NewDoc();
        var history = new History();
        Draw(doc, history, new PencilTool(), line, 1, (0, 0));
        Draw(doc, history, new PencilTool(), line, 2, (5, 5));
        Draw(doc, history, new PencilTool(), line, 3, (9, 9));
        var cel = doc.GetCel(line, frame);

        history.JumpTo(1);
        Assert.Equal(1, cel.Get(0, 0));
        Assert.Equal(0, cel.Get(5, 5));
        history.JumpTo(3);
        Assert.Equal(3, cel.Get(9, 9));
    }
}
