using System.Numerics;

namespace Fink.Core;

public enum SelectMode
{
    Replace,
    Add,
    Subtract,
    Intersect,
}

public readonly record struct PixelRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool IsEmpty => W <= 0 || H <= 0;
}

/// <summary>
/// Which pixels are selected: a mask the size of the document. Selections are shared by all
/// layers and frames; what they act on (one layer or all) is decided when pixels are lifted.
/// </summary>
public sealed class Selection
{
    public int Width { get; }
    public int Height { get; }
    public bool[] Mask { get; }
    /// <summary>Bumps on every change, so views can cache outlines.</summary>
    public int Version { get; private set; }

    public Selection(int width, int height)
    {
        Width = width;
        Height = height;
        Mask = new bool[width * height];
    }

    public bool IsEmpty => Array.IndexOf(Mask, true) < 0;
    public int Count => Mask.Count(m => m);
    public bool Contains(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height && Mask[y * Width + x];

    public void Touch() => Version++;

    public void Clear()
    {
        Array.Clear(Mask);
        Version++;
    }

    public void SelectAll()
    {
        Array.Fill(Mask, true);
        Version++;
    }

    public void Invert()
    {
        for (int i = 0; i < Mask.Length; i++) Mask[i] = !Mask[i];
        Version++;
    }

    public void CopyFrom(bool[] mask)
    {
        Array.Copy(mask, Mask, Mask.Length);
        Version++;
    }

    /// <summary>The smallest rectangle holding every selected pixel; empty when nothing is selected.</summary>
    public PixelRect Bounds()
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (!Mask[y * Width + x]) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return maxX < 0 ? default : new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Combines a shape (a full-size mask) into the selection.</summary>
    public void Apply(bool[] shape, SelectMode mode)
    {
        for (int i = 0; i < Mask.Length; i++)
        {
            Mask[i] = mode switch
            {
                SelectMode.Replace => shape[i],
                SelectMode.Add => Mask[i] || shape[i],
                SelectMode.Subtract => Mask[i] && !shape[i],
                _ => Mask[i] && shape[i],
            };
        }
        Version++;
    }

    public bool[] RectShape(int x0, int y0, int x1, int y1)
    {
        var shape = new bool[Mask.Length];
        int ax = Math.Clamp(Math.Min(x0, x1), 0, Width - 1), bx = Math.Clamp(Math.Max(x0, x1), 0, Width - 1);
        int ay = Math.Clamp(Math.Min(y0, y1), 0, Height - 1), by = Math.Clamp(Math.Max(y0, y1), 0, Height - 1);
        if (Math.Max(x0, x1) < 0 || Math.Min(x0, x1) >= Width || Math.Max(y0, y1) < 0 || Math.Min(y0, y1) >= Height) return shape;
        for (int y = ay; y <= by; y++)
        for (int x = ax; x <= bx; x++)
            shape[y * Width + x] = true;
        return shape;
    }

    /// <summary>Pixels whose centers are inside the polygon (even-odd rule), plus the outline itself.</summary>
    public bool[] PolygonShape(IReadOnlyList<Vector2> points)
    {
        var shape = new bool[Mask.Length];
        if (points.Count < 3) return shape;
        var xs = new List<float>();
        for (int y = 0; y < Height; y++)
        {
            float cy = y + 0.5f;
            xs.Clear();
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                if ((a.Y <= cy && b.Y > cy) || (b.Y <= cy && a.Y > cy))
                    xs.Add(a.X + (cy - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                int from = Math.Max(0, (int)MathF.Ceiling(xs[i] - 0.5f));
                int to = Math.Min(Width - 1, (int)MathF.Floor(xs[i + 1] - 0.5f));
                for (int x = from; x <= to; x++) shape[y * Width + x] = true;
            }
        }
        // the outline too, so thin lassos still select what they were drawn over
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            foreach (var (x, y) in Raster.Line((int)MathF.Floor(a.X), (int)MathF.Floor(a.Y), (int)MathF.Floor(b.X), (int)MathF.Floor(b.Y)))
                if ((uint)x < (uint)Width && (uint)y < (uint)Height) shape[y * Width + x] = true;
        }
        return shape;
    }

    /// <summary>
    /// Magic wand: pixels with the same palette index as (x, y) in <paramref name="source"/>,
    /// 4-connected when contiguous, or everywhere.
    /// </summary>
    public bool[] WandShape(Cel source, int x, int y, bool contiguous)
    {
        var shape = new bool[Mask.Length];
        if (!source.InBounds(x, y)) return shape;
        byte target = source.Get(x, y);
        if (!contiguous)
        {
            for (int i = 0; i < shape.Length; i++) shape[i] = source.Pixels[i] == target;
            return shape;
        }
        var stack = new Stack<int>();
        stack.Push(y * Width + x);
        shape[y * Width + x] = true;
        while (stack.Count > 0)
        {
            int i = stack.Pop(), px = i % Width, py = i / Width;
            Visit(px - 1, py); Visit(px + 1, py); Visit(px, py - 1); Visit(px, py + 1);
        }
        return shape;

        void Visit(int vx, int vy)
        {
            if ((uint)vx >= (uint)Width || (uint)vy >= (uint)Height) return;
            int j = vy * Width + vx;
            if (shape[j] || source.Pixels[j] != target) return;
            shape[j] = true;
            stack.Push(j);
        }
    }

    /// <summary>The selection outline as unit edges in pixel coordinates, for drawing marching ants.</summary>
    public static List<(int x0, int y0, int x1, int y1)> Outline(bool[] mask, int w, int h)
    {
        var edges = new List<(int, int, int, int)>();
        bool At(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h && mask[y * w + x];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (!mask[y * w + x]) continue;
            if (!At(x, y - 1)) edges.Add((x, y, x + 1, y));
            if (!At(x, y + 1)) edges.Add((x, y + 1, x + 1, y + 1));
            if (!At(x - 1, y)) edges.Add((x, y, x, y + 1));
            if (!At(x + 1, y)) edges.Add((x + 1, y, x + 1, y + 1));
        }
        return edges;
    }
}
