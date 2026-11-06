namespace Fink.Core;

public sealed class ToolSettings
{
    public int Size { get; set; } = 1;
    public bool SquareTip { get; set; }
    /// <summary>Removes the L-shaped corner pixels from 1px freehand strokes.</summary>
    public bool PixelPerfect { get; set; } = true;
    public int GapClose { get; set; } = 1;
    public bool FillContiguous { get; set; } = true;
}

/// <summary>Everything a tool needs during one stroke.</summary>
public sealed class ToolContext
{
    public required Document Document { get; init; }
    public required Frame Frame { get; init; }
    public required Layer Layer { get; init; }
    public required CelEditSession Session { get; init; }
    public required byte Color { get; init; }
    public required ToolSettings Settings { get; init; }
    /// <summary>Held modifier (Shift): snap angles for the line tool.</summary>
    public bool Constrain { get; set; }
    /// <summary>When painting a data channel: the drawing's colors, which say where its shapes are.</summary>
    public Cel? RegionCel { get; init; }
}

public abstract class Tool
{
    public abstract string Name { get; }
    /// <summary>The layer roles this tool may edit; the line/fill separation lives here.</summary>
    public abstract bool Allows(LayerRole role);
    public virtual void Begin(ToolContext ctx, int x, int y) { }
    public virtual void Drag(ToolContext ctx, int x, int y) { }
    public virtual void End(ToolContext ctx) { }

    public string WhyNot(LayerRole role) => role switch
    {
        LayerRole.Reference => "Reference layers can't be edited.",
        LayerRole.Line => $"{Name} paints Fill layers. Select a Fill layer.",
        LayerRole.Fill => $"{Name} draws on Line layers. Select a Line layer.",
        _ => $"{Name} can't be used here.",
    };
}

/// <summary>Freehand hard-edged drawing on Line layers.</summary>
public class PencilTool : Tool
{
    public override string Name => "Pencil";
    public override bool Allows(LayerRole role) => role == LayerRole.Line;

    protected virtual byte ColorFor(ToolContext ctx) => ctx.Color;

    private int _lastX, _lastY;
    // the 1px path drawn so far, and each pixel's value before the stroke, for pixel-perfect
    private readonly List<(int x, int y)> _path = new();
    private readonly Dictionary<(int, int), byte> _original = new();

    public override void Begin(ToolContext ctx, int x, int y)
    {
        _path.Clear();
        _original.Clear();
        _lastX = x;
        _lastY = y;
        Plot(ctx, x, y);
    }

    public override void Drag(ToolContext ctx, int x, int y)
    {
        if (x == _lastX && y == _lastY) return;
        bool first = true;
        foreach (var p in Raster.Line(_lastX, _lastY, x, y))
        {
            if (first) { first = false; continue; } // already drawn
            Plot(ctx, p.x, p.y);
        }
        _lastX = x;
        _lastY = y;
    }

    private void Plot(ToolContext ctx, int x, int y)
    {
        var s = ctx.Settings;
        byte color = ColorFor(ctx);
        if (s.Size > 1 || !s.PixelPerfect)
        {
            foreach (var p in Raster.Stamp(x, y, s.Size, s.SquareTip)) ctx.Session.Set(p.x, p.y, color);
            return;
        }

        if (!_original.ContainsKey((x, y))) _original[(x, y)] = ctx.Session.Get(x, y);
        ctx.Session.Set(x, y, color);
        _path.Add((x, y));

        // a-b-c where a and c touch diagonally and b is the corner between them: drop b
        if (_path.Count >= 3)
        {
            var c = _path[^1];
            var b = _path[^2];
            var a = _path[^3];
            if (Math.Abs(a.x - c.x) == 1 && Math.Abs(a.y - c.y) == 1 && (b.x == a.x || b.y == a.y)
                && b != a && b != c)
            {
                ctx.Session.Set(b.x, b.y, _original[b]);
                _path.RemoveAt(_path.Count - 2);
            }
        }
    }
}

/// <summary>Writes transparent. Works on Line and Fill layers.</summary>
public sealed class EraserTool : PencilTool
{
    public override string Name => "Eraser";
    public override bool Allows(LayerRole role) => role is LayerRole.Line or LayerRole.Fill;
    protected override byte ColorFor(ToolContext ctx) => Palette.Transparent;
}

/// <summary>A straight line from press to release, previewed while dragging. Shift snaps the angle.</summary>
public sealed class LineTool : Tool
{
    public override string Name => "Line";
    public override bool Allows(LayerRole role) => role == LayerRole.Line;

    private int _x0, _y0;

    public override void Begin(ToolContext ctx, int x, int y)
    {
        _x0 = x;
        _y0 = y;
        Draw(ctx, x, y);
    }

    public override void Drag(ToolContext ctx, int x, int y)
    {
        ctx.Session.Revert();
        Draw(ctx, x, y);
    }

    private void Draw(ToolContext ctx, int x, int y)
    {
        if (ctx.Constrain) (x, y) = Raster.SnapAngle(_x0, _y0, x, y);
        foreach (var p in Raster.Line(_x0, _y0, x, y))
        foreach (var q in Raster.Stamp(p.x, p.y, ctx.Settings.Size, ctx.Settings.SquareTip))
            ctx.Session.Set(q.x, q.y, ctx.Color);
    }
}

/// <summary>Bucket fill on Fill layers, bounded by the visible Line layers of the same frame.</summary>
public sealed class FillTool : Tool
{
    public override string Name => "Fill";
    public override bool Allows(LayerRole role) => role == LayerRole.Fill;

    public override void Begin(ToolContext ctx, int x, int y)
    {
        if (ctx.RegionCel is { } colors)
        {
            Raster.FillShape(ctx.Session, colors, x, y, ctx.Color, ctx.Settings.FillContiguous);
            return;
        }
        Raster.Fill(ctx.Session, ctx.Document.FillBoundaryCels(ctx.Layer, ctx.Frame), x, y, ctx.Color,
            ctx.Settings.GapClose, ctx.Settings.FillContiguous);
    }
}

/// <summary>
/// Runs a tool for one stroke: checks the layer role, opens an edit session on the cel, and
/// commits the result to the history.
/// </summary>
public sealed class StrokeController
{
    private Tool? _tool;
    private ToolContext? _ctx;
    private IEdit? _created;

    public bool Active => _ctx != null;

    /// <summary>Starts a stroke, or returns why it can't (wrong layer role, locked layer).</summary>
    public string? Begin(Tool tool, Document doc, Frame frame, Layer layer, byte color, ToolSettings settings,
        int x, int y, bool constrain, bool[]? clip = null, DataChannel? channel = null)
    {
        if (channel != null) return BeginData(tool, doc, frame, layer, color, settings, x, y, constrain, clip, channel);
        if (!tool.Allows(layer.Role)) return tool.WhyNot(layer.Role);
        if (layer.Locked) return $"\"{layer.Name}\" is locked.";
        if (!layer.Visible) return $"\"{layer.Name}\" is hidden.";

        var cel = doc.CelForEdit(layer, frame, out _created);
        if (cel == null)
            return $"\"{layer.Name}\" follows \"{doc.TimingLayer(layer).Name}\", which has no drawing on this frame.";

        _tool = tool;
        _ctx = new ToolContext
        {
            Document = doc, Frame = frame, Layer = layer,
            Session = new CelEditSession(cel) { Clip = clip },
            Color = color, Settings = settings, Constrain = constrain,
        };
        tool.Begin(_ctx, x, y);
        return null;
    }

    /// <summary>
    /// Painting a data channel: any non-reference layer, on the drawing it shows here (data
    /// says something about pixels that are drawn, so there must be a drawing).
    /// </summary>
    private string? BeginData(Tool tool, Document doc, Frame frame, Layer layer, byte value, ToolSettings settings,
        int x, int y, bool constrain, bool[]? clip, DataChannel channel)
    {
        if (layer.Role == LayerRole.Reference) return "Reference layers can't hold data.";
        if (layer.Locked) return $"\"{layer.Name}\" is locked.";
        if (!layer.Visible) return $"\"{layer.Name}\" is hidden.";
        var drawing = doc.DrawingAt(layer, frame);
        if (drawing == null) return $"Nothing is drawn on \"{layer.Name}\" here to paint {channel.Name} on.";
        _created = null;
        _tool = tool;
        _ctx = new ToolContext
        {
            Document = doc, Frame = frame, Layer = layer,
            Session = new CelEditSession(drawing.Channel(channel.Id)) { Clip = clip },
            Color = value, Settings = settings, Constrain = constrain, RegionCel = drawing.Cel,
        };
        tool.Begin(_ctx, x, y);
        return null;
    }

    public void Drag(int x, int y, bool constrain)
    {
        if (_ctx == null) return;
        _ctx.Constrain = constrain;
        _tool!.Drag(_ctx, x, y);
    }

    public void End(History history)
    {
        if (_ctx == null) return;
        _tool!.End(_ctx);
        var name = $"{_tool.Name} on {_ctx.Layer.Name}";
        if (WithCreation(name, _ctx.Session.Commit(name), _created) is { } edit) history.Push(edit);
        _ctx = null;
        _tool = null;
        _created = null;
    }

    public void Cancel()
    {
        _ctx?.Session.Revert();
        _created?.Undo();
        _ctx = null;
        _tool = null;
        _created = null;
    }

    /// <summary>
    /// Joins the making of a new drawing with what was drawn on it, so they undo together; or
    /// takes the new drawing back when nothing was drawn.
    /// </summary>
    public static IEdit? WithCreation(string name, IEdit? pixels, IEdit? created)
    {
        if (created == null) return pixels;
        if (pixels == null) { created.Undo(); return null; }
        return new CompositeEdit(name, new List<IEdit> { created, pixels });
    }
}
