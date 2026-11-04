using System.Numerics;

namespace Fink.Core;

/// <summary>Pixels picked up from (or pasted onto) one layer.</summary>
public sealed class FloatingLayer
{
    public required Layer Layer { get; init; }
    /// <summary>Palette indices, source-local (W×H). 0 is transparent and never stamps.</summary>
    public required byte[] Pixels { get; init; }
    /// <summary>The edit session on the target cel: holds the lift until commit, so lift + stamp
    /// become one undo step.</summary>
    public required CelEditSession Session { get; init; }
    /// <summary>A drawing made to receive these pixels (the cell was empty); undone if nothing lands.</summary>
    public IEdit? Created { get; init; }
}

/// <summary>
/// A selection that has been picked up: the pixels float above their layers, can be moved,
/// scaled, rotated, flipped and skewed, and are stamped back on commit (nearest neighbor; no
/// blending, so the palette stays exact).
/// </summary>
public sealed class FloatingSelection
{
    public int SrcW { get; }
    public int SrcH { get; }
    /// <summary>Which source pixels are part of the selection (W×H).</summary>
    public bool[] SrcMask { get; }
    public List<FloatingLayer> Layers { get; } = new();
    public TransformParams Transform { get; set; }
    public string Name { get; set; }
    /// <summary>The document size; rasterized output is clipped to it.</summary>
    public int DocW { get; }
    public int DocH { get; }

    public int Version { get; private set; }

    public FloatingSelection(string name, int docW, int docH, PixelRect source, bool[] srcMask)
    {
        Name = name;
        DocW = docW;
        DocH = docH;
        SrcW = source.W;
        SrcH = source.H;
        SrcMask = srcMask;
        Transform = new TransformParams(new Vector2(source.X + source.W / 2f, source.Y + source.H / 2f));
    }

    public void SetTransform(TransformParams t)
    {
        Transform = t;
        Version++;
    }

    public Affine Matrix => Transform.ToAffine(SrcW, SrcH);

    /// <summary>The four corners of the transformed box, in document pixels (TL, TR, BR, BL).</summary>
    public Vector2[] Corners()
    {
        var m = Matrix;
        return new[] { m.Apply(new(0, 0)), m.Apply(new(SrcW, 0)), m.Apply(new(SrcW, SrcH)), m.Apply(new(0, SrcH)) };
    }

    /// <summary>The pixels sampled from: the source itself, or a smoothed upscale of it.</summary>
    private sealed record SampleSource(int Scale, int W, int H, bool[] Mask, Dictionary<FloatingLayer, byte[]> Pixels);

    private SampleSource? _smooth;

    /// <summary>Call after changing <see cref="FloatingLayer.Pixels"/> (cached upscales go stale).</summary>
    public void PixelsChanged()
    {
        _smooth = null;
        Version++;
    }

    /// <summary>
    /// Quarter turns, flips and whole-number scales sample the source directly (pixel-exact). Any
    /// other angle or skew samples an 8x Scale2x upscale (the RotSprite idea), which keeps 1px
    /// lines connected instead of breaking them into dots.
    /// </summary>
    private SampleSource Source()
    {
        if (Transform.IsAxisAligned)
            return new SampleSource(1, SrcW, SrcH, SrcMask, Layers.ToDictionary(l => l, l => l.Pixels));
        return _smooth ??= BuildSmooth();
    }

    private SampleSource BuildSmooth()
    {
        int scale = 8;
        while (scale > 2 && (long)SrcW * SrcH * scale * scale > 8_000_000) scale /= 2;
        int w = SrcW, h = SrcH;
        var mask = SrcMask.Select(m => m ? (byte)1 : (byte)0).ToArray();
        var layers = Layers.ToDictionary(l => l, l => l.Pixels);
        for (int s = 1; s < scale; s *= 2)
        {
            mask = Raster.Scale2x(mask, w, h);
            foreach (var key in layers.Keys.ToList()) layers[key] = Raster.Scale2x(layers[key], w, h);
            w *= 2;
            h *= 2;
        }
        return new SampleSource(scale, w, h, mask.Select(m => m != 0).ToArray(), layers);
    }

    /// <summary>
    /// For every transformed pixel inside the document: an index into the sample source (or -1).
    /// Sampling at pixel centers through the inverse matrix.
    /// </summary>
    private int[] SampleMap(SampleSource src)
    {
        var map = new int[DocW * DocH];
        Array.Fill(map, -1);
        var corners = Corners();
        int x0 = Math.Max(0, (int)MathF.Floor(corners.Min(c => c.X)));
        int y0 = Math.Max(0, (int)MathF.Floor(corners.Min(c => c.Y)));
        int x1 = Math.Min(DocW, (int)MathF.Ceiling(corners.Max(c => c.X)));
        int y1 = Math.Min(DocH, (int)MathF.Ceiling(corners.Max(c => c.Y)));
        var inv = Matrix.Inverse();
        const float eps = 1e-3f; // keeps exact quarter turns from landing on pixel borders
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            var s = inv.Apply(new Vector2(x + 0.5f, y + 0.5f)) * src.Scale;
            int sx = (int)MathF.Floor(s.X + eps), sy = (int)MathF.Floor(s.Y + eps);
            if ((uint)sx >= (uint)src.W || (uint)sy >= (uint)src.H) continue;
            int si = sy * src.W + sx;
            if (src.Mask[si]) map[y * DocW + x] = si;
        }
        return map;
    }

    /// <summary>Document-sized pixels per layer (0 where nothing lands), for display.</summary>
    public Dictionary<Layer, byte[]> Rasterize(out bool[] mask)
    {
        var src = Source();
        var map = SampleMap(src);
        mask = new bool[map.Length];
        var result = new Dictionary<Layer, byte[]>();
        foreach (var fl in Layers) result[fl.Layer] = new byte[map.Length];
        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] < 0) continue;
            mask[i] = true;
            foreach (var fl in Layers) result[fl.Layer][i] = src.Pixels[fl][map[i]];
        }
        return result;
    }

    /// <summary>Moves the center so the transformed box starts on whole pixels (for 90° turns and
    /// whole-number scales), keeping the result pixel-exact.</summary>
    public void SnapToGrid()
    {
        if (!Transform.IsAxisAligned) return;
        var corners = Corners();
        float minX = corners.Min(c => c.X), minY = corners.Min(c => c.Y);
        var t = Transform;
        t.Center += new Vector2(MathF.Round(minX) - minX, MathF.Round(minY) - minY);
        SetTransform(t);
    }

    /// <summary>Stamps the pixels into their cels and closes the sessions as one undo step.</summary>
    public IEdit? Commit(out bool[] resultMask)
    {
        var src = Source();
        var map = SampleMap(src);
        resultMask = new bool[map.Length];
        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] < 0) continue;
            resultMask[i] = true;
            int x = i % DocW, y = i / DocW;
            foreach (var fl in Layers)
            {
                byte v = src.Pixels[fl][map[i]];
                if (v != Palette.Transparent) fl.Session.Set(x, y, v);
            }
        }
        var edits = Layers.Select(fl => StrokeController.WithCreation(Name, fl.Session.Commit(Name), fl.Created)).OfType<IEdit>().ToList();
        return edits.Count switch
        {
            0 => null,
            1 => edits[0],
            _ => new CompositeEdit(Name, edits),
        };
    }

    /// <summary>Puts everything back as it was before the lift.</summary>
    public void Cancel()
    {
        foreach (var fl in Layers)
        {
            fl.Session.Revert();
            fl.Created?.Undo();
        }
    }

    /// <summary>
    /// Picks up the selected pixels from each layer's cel (clearing them there) into a new
    /// floating selection.
    /// </summary>
    public static FloatingSelection Lift(string name, Document doc, Frame frame, IEnumerable<Layer> layers, Selection selection)
    {
        var b = selection.Bounds();
        var srcMask = new bool[b.W * b.H];
        for (int y = 0; y < b.H; y++)
        for (int x = 0; x < b.W; x++)
            srcMask[y * b.W + x] = selection.Contains(b.X + x, b.Y + y);

        var f = new FloatingSelection(name, doc.Width, doc.Height, b, srcMask);
        foreach (var layer in layers)
        {
            // nothing to pick up from an empty cell; that layer just isn't carried along
            if (!doc.TryGetCel(layer, frame, out var cel)) continue;
            var session = new CelEditSession(cel);
            var px = new byte[b.W * b.H];
            for (int y = 0; y < b.H; y++)
            for (int x = 0; x < b.W; x++)
            {
                if (!srcMask[y * b.W + x]) continue;
                px[y * b.W + x] = session.Get(b.X + x, b.Y + y);
                session.Set(b.X + x, b.Y + y, Palette.Transparent);
            }
            f.Layers.Add(new FloatingLayer { Layer = layer, Pixels = px, Session = session });
        }
        return f;
    }
}

/// <summary>Copied pixels, per layer, with where they came from (pastes land in place).</summary>
public sealed class ClipboardContent
{
    public required PixelRect Source { get; init; }
    public required bool[] Mask { get; init; }
    public required List<(string LayerName, LayerRole Role, byte[] Pixels)> Layers { get; init; }
}

/// <summary>Several edits undone and redone as one step.</summary>
public sealed class CompositeEdit : IEdit
{
    private readonly List<IEdit> _edits;
    public string Name { get; }

    public CompositeEdit(string name, List<IEdit> edits)
    {
        Name = name;
        _edits = edits;
    }

    public void Undo()
    {
        for (int i = _edits.Count - 1; i >= 0; i--) _edits[i].Undo();
    }

    public void Redo()
    {
        foreach (var e in _edits) e.Redo();
    }
}
