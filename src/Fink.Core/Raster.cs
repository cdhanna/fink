namespace Fink.Core;

/// <summary>Pixel algorithms: lines, brush stamps, flood fill with gap closing.</summary>
public static class Raster
{
    /// <summary>Bresenham, including both ends.</summary>
    public static IEnumerable<(int x, int y)> Line(int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            yield return (x0, y0);
            if (x0 == x1 && y0 == y1) yield break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// Snaps an end point so the line from (x0,y0) runs at a pixel-art-friendly angle:
    /// 0°, 90°, 45°, or the 2:1 / 1:2 slopes.
    /// </summary>
    public static (int x, int y) SnapAngle(int x0, int y0, int x1, int y1)
    {
        int dx = x1 - x0, dy = y1 - y0;
        if (dx == 0 && dy == 0) return (x1, y1);
        (int rx, int ry)[] ratios = { (1, 0), (0, 1), (1, 1), (2, 1), (1, 2) };
        double best = double.MaxValue;
        (int x, int y) result = (x1, y1);
        foreach (var (rx, ry) in ratios)
        {
            // project (dx,dy) onto the ratio direction, keeping the signs of dx/dy
            int sx = Math.Sign(dx) == 0 ? 1 : Math.Sign(dx), sy = Math.Sign(dy) == 0 ? 1 : Math.Sign(dy);
            double len = (Math.Abs(dx) * rx + Math.Abs(dy) * ry) / (double)(rx * rx + ry * ry);
            int steps = (int)Math.Round(len);
            int px = x0 + sx * rx * steps, py = y0 + sy * ry * steps;
            double d = (px - x1) * (px - x1) + (py - y1) * (py - y1);
            if (d < best) { best = d; result = (px, py); }
        }
        return result;
    }

    /// <summary>The pixels of a brush tip of the given size centered on (cx, cy).</summary>
    public static IEnumerable<(int x, int y)> Stamp(int cx, int cy, int size, bool square)
    {
        if (size <= 1) { yield return (cx, cy); yield break; }
        int r0 = -(size - 1) / 2, r1 = size / 2;
        double c = (r0 + r1) / 2.0, rad = size / 2.0;
        for (int y = r0; y <= r1; y++)
        for (int x = r0; x <= r1; x++)
        {
            if (square) { yield return (cx + x, cy + y); continue; }
            double ddx = x - c, ddy = y - c;
            if (ddx * ddx + ddy * ddy <= rad * rad - 0.25) yield return (cx + x, cy + y);
        }
    }

    /// <summary>
    /// Fill on a fill-layer cel, bounded by the given line cels.
    /// <para>Matches the exact palette index at the seed (no tolerance). With <paramref name="gapClose"/>
    /// &gt; 0, gaps in the line art up to about 2×gapClose pixels wide are treated as closed: the line
    /// art is grown by that many pixels for the flood, and the fill is then grown back to meet the
    /// real lines.</para>
    /// </summary>
    /// <returns>The number of pixels changed.</returns>
    public static int Fill(CelEditSession session, IEnumerable<Cel> lineCels, int sx, int sy, byte color,
        int gapClose, bool contiguous)
    {
        var cel = session.Cel;
        int w = cel.Width, h = cel.Height, n = w * h;
        if (!cel.InBounds(sx, sy)) return 0;

        var barrier = new bool[n];
        foreach (var line in lineCels)
            for (int i = 0; i < n; i++)
                if (line.Pixels[i] != Palette.Transparent) barrier[i] = true;

        int seed = sy * w + sx;
        if (barrier[seed]) return 0;
        byte target = cel.Pixels[seed];
        if (target == color) return 0;

        bool Match(int i) => !barrier[i] && cel.Pixels[i] == target;

        var region = new bool[n];
        if (!contiguous)
        {
            for (int i = 0; i < n; i++) region[i] = Match(i);
        }
        else
        {
            bool[]? band = gapClose > 0 ? Dilate(barrier, w, h, gapClose) : null;
            if (band != null && band[seed]) band = null; // clicked inside the band: plain fill

            Flood(region, w, h, seed, i => Match(i) && (band == null || !band[i]));

            if (band != null)
            {
                // grow back into the band so the fill meets the lines; 4-connected so it can't slip
                // diagonally between the steps of a pixel line. Manhattan 2r covers Chebyshev r.
                var frontier = new List<int>();
                for (int i = 0; i < n; i++) if (region[i]) frontier.Add(i);
                for (int step = 0; step < gapClose * 2 && frontier.Count > 0; step++)
                {
                    var next = new List<int>();
                    foreach (var i in frontier)
                    {
                        int x = i % w, y = i / w;
                        TryGrow(x - 1, y); TryGrow(x + 1, y); TryGrow(x, y - 1); TryGrow(x, y + 1);
                    }
                    frontier = next;

                    void TryGrow(int x, int y)
                    {
                        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
                        int j = y * w + x;
                        if (region[j] || !band[j] || !Match(j)) return;
                        region[j] = true;
                        next.Add(j);
                    }
                }
            }
        }

        int changed = 0;
        for (int i = 0; i < n; i++)
        {
            if (!region[i]) continue;
            session.Set(i % w, i / w, color);
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Fills a data channel over one shape of a drawing: the pixels of the clicked color
    /// (connected to the click, or all of them when not contiguous). Empty pixels are skipped.
    /// </summary>
    public static int FillShape(CelEditSession session, Cel colors, int sx, int sy, byte value, bool contiguous)
    {
        if (!colors.InBounds(sx, sy)) return 0;
        int w = colors.Width, h = colors.Height, seed = sy * w + sx;
        byte target = colors.Pixels[seed];
        if (target == Palette.Transparent) return 0;
        var region = new bool[w * h];
        if (contiguous) Flood(region, w, h, seed, i => colors.Pixels[i] == target);
        else for (int i = 0; i < region.Length; i++) region[i] = colors.Pixels[i] == target;
        int changed = 0;
        for (int i = 0; i < region.Length; i++)
        {
            if (!region[i]) continue;
            session.Set(i % w, i / w, value);
            changed++;
        }
        return changed;
    }

    /// <summary>4-connected flood over <paramref name="canEnter"/>, marking <paramref name="region"/>.</summary>
    private static void Flood(bool[] region, int w, int h, int seed, Func<int, bool> canEnter)
    {
        if (!canEnter(seed)) return;
        var stack = new Stack<int>();
        stack.Push(seed);
        region[seed] = true;
        while (stack.Count > 0)
        {
            int i = stack.Pop(), x = i % w, y = i / w;
            if (x > 0) Visit(i - 1);
            if (x < w - 1) Visit(i + 1);
            if (y > 0) Visit(i - w);
            if (y < h - 1) Visit(i + w);
        }

        void Visit(int j)
        {
            if (region[j] || !canEnter(j)) return;
            region[j] = true;
            stack.Push(j);
        }
    }

    /// <summary>
    /// Scale2x (EPX): doubles an image, rounding diagonal steps into smooth runs instead of
    /// blocks. Exact on palette indices, since it only compares for equality.
    /// </summary>
    public static byte[] Scale2x(byte[] src, int w, int h)
    {
        var dst = new byte[w * h * 4];
        int dw = w * 2;
        byte At(int x, int y) => src[Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            byte p = src[y * w + x];
            byte a = At(x, y - 1), b = At(x + 1, y), c = At(x - 1, y), d = At(x, y + 1);
            byte e0 = p, e1 = p, e2 = p, e3 = p;
            if (c == a && c != d && a != b) e0 = a;
            if (a == b && a != c && b != d) e1 = b;
            if (d == c && d != b && c != a) e2 = c;
            if (b == d && b != a && d != c) e3 = d;
            int o = (y * 2) * dw + x * 2;
            dst[o] = e0;
            dst[o + 1] = e1;
            dst[o + dw] = e2;
            dst[o + dw + 1] = e3;
        }
        return dst;
    }

    /// <summary>Grows a mask by a square of radius r (separable max filter).</summary>
    public static bool[] Dilate(bool[] mask, int w, int h, int r)
    {
        var tmp = new bool[mask.Length];
        var result = new bool[mask.Length];
        for (int y = 0; y < h; y++)
        {
            int last = int.MinValue / 2;
            for (int x = 0; x < w; x++) { if (mask[y * w + x]) last = x; tmp[y * w + x] = x - last <= r; }
            last = int.MaxValue / 2;
            for (int x = w - 1; x >= 0; x--) { if (mask[y * w + x]) last = x; if (last - x <= r) tmp[y * w + x] = true; }
        }
        for (int x = 0; x < w; x++)
        {
            int last = int.MinValue / 2;
            for (int y = 0; y < h; y++) { if (tmp[y * w + x]) last = y; result[y * w + x] = y - last <= r; }
            last = int.MaxValue / 2;
            for (int y = h - 1; y >= 0; y--) { if (tmp[y * w + x]) last = y; if (last - y <= r) result[y * w + x] = true; }
        }
        return result;
    }
}
