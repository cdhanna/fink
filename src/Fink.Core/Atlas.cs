namespace Fink.Core;

/// <summary>
/// One texture atlas: which assets it packs and how. Saved in the project, including where each
/// picture was placed, so packing stays stable between sessions.
/// </summary>
public sealed class AtlasConfig
{
    public string Name { get; set; } = "atlas";
    /// <summary>Pack every asset in the project (new ones too); otherwise only <see cref="AssetIds"/>.</summary>
    public bool IncludeAll { get; set; } = true;
    public List<int> AssetIds { get; set; } = new();
    public int MaxSize { get; set; } = 2048;
    public bool PowerOfTwo { get; set; } = true;
    /// <summary>Empty pixels between pictures.</summary>
    public int Padding { get; set; } = 1;
    /// <summary>Where the textures and atlas.json go, e.g. a game's Assets/Textures folder.</summary>
    public string OutputFolder { get; set; } = "";
    /// <summary>Where the generated .fbasic goes (empty: next to the textures).</summary>
    public string CodeFolder { get; set; } = "";
    /// <summary>The atlas's path as the game loads it (content root relative, no extension).</summary>
    public string ContentPath { get; set; } = "";

    /// <summary>Data maps: textures beside the index texture, with the same layout.</summary>
    public List<MapConfig> Maps { get; set; } = new();

    /// <summary>Last placement of each picture (by content key), for stable packing.</summary>
    public Dictionary<string, int[]> Placements { get; set; } = new();
    public int LastWidth { get; set; }
    public int LastHeight { get; set; }

    public string EffectiveContentPath => string.IsNullOrWhiteSpace(ContentPath) ? $"Textures/{Name}" : ContentPath.Trim().Replace('\\', '/');
}

/// <summary>A picture in the atlas: palette indices trimmed to their content.</summary>
public sealed class AtlasCell
{
    public required string Key { get; init; }
    public required int W { get; init; }
    public required int H { get; init; }
    public required byte[] Pixels { get; init; }
    /// <summary>Each data channel's values over the same trimmed area (by channel id).</summary>
    public Dictionary<int, byte[]> Channels { get; init; } = new();
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>One step of a clip: a picture, where it sits in the asset's canvas, and for how long.</summary>
/// <param name="Cell">Index into the cells, or -1 for an empty frame.</param>
/// <param name="Pivot">Where the pivot is in the asset's canvas on this frame.</param>
/// <param name="Points">The asset's named points on this frame, in the order of its point tracks.</param>
public readonly record struct AtlasFrame(int Cell, int OffsetX, int OffsetY, int Ms, int TimelineFrom, int TimelineTo,
    PointXY Pivot = default, PointXY[]? Points = null);

/// <summary>A tag (or an asset's whole timeline) as the game plays it.</summary>
public sealed class AtlasClip
{
    public required int Id { get; init; }
    public required Document Asset { get; init; }
    /// <summary>The tag's name, or empty for the whole timeline.</summary>
    public required string Name { get; init; }
    public required LoopMode Loop { get; init; }
    public required List<AtlasFrame> Frames { get; init; }
}

/// <summary>A packed atlas, ready to show or export.</summary>
public sealed class AtlasBuild
{
    public required AtlasConfig Config { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Palette index per pixel (0: empty).</summary>
    public required byte[] Indices { get; init; }
    public required List<AtlasCell> Cells { get; init; }
    public required List<Document> Assets { get; init; }
    public required List<AtlasClip> Clips { get; init; }
    /// <summary>Which cells each asset uses (for highlighting).</summary>
    public required Dictionary<Document, HashSet<int>> CellsOf { get; init; }
    /// <summary>Why it couldn't pack everything, if it couldn't.</summary>
    public string? Error { get; init; }

    public int UsedPixels => Cells.Sum(c => c.W * c.H);
}

/// <summary>Builds atlases: finds every different picture of the assets, trims and packs them.</summary>
public static class AtlasBuilder
{
    /// <param name="tight">Repack from scratch, smallest size first, instead of keeping places.</param>
    public static AtlasBuild Build(Project project, AtlasConfig config, bool tight = false)
    {
        project.AssignIds();
        var assets = project.AssetsOf(config);
        var cells = new List<AtlasCell>();
        var cellIndex = new Dictionary<string, int>();
        var clips = new List<AtlasClip>();
        var cellsOf = new Dictionary<Document, HashSet<int>>();

        foreach (var doc in assets)
        {
            var used = cellsOf[doc] = new HashSet<int>();
            var runs = Runs(doc);
            var runFrames = new List<AtlasFrame>();
            var layers = doc.Layers.Where(l => l.Visible && l.Role != LayerRole.Reference).ToList();
            var flat = new byte[doc.Width * doc.Height];
            var from_ = new Layer?[doc.Width * doc.Height];
            var named = doc.Points.Where(p => !p.IsPivot).ToList();
            foreach (var (from, to) in runs)
            {
                FlattenIndices(doc, layers, from, flat, from_);
                int ms = Ms(doc, to + 1) - Ms(doc, from);
                var pivot = doc.PivotAt(from);
                var points = named.Select(t => doc.PointAt(t, from, out _) ?? new PointXY(doc.Width / 2, doc.Height / 2)).ToArray();
                var trimmed = Trim(flat, doc.Width, doc.Height, out int ox, out int oy, out int w, out int h);
                if (trimmed == null)
                {
                    runFrames.Add(new AtlasFrame(-1, 0, 0, ms, from, to, pivot, points));
                    continue;
                }
                // each channel's value at a pixel comes from the layer whose color is on top there
                var channels = new Dictionary<int, byte[]>();
                foreach (var ch in project.Channels)
                {
                    byte[]? data = null;
                    for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = (oy + y) * doc.Width + ox + x;
                        if (from_[i] is not { } src) continue;
                        var d = doc.DrawingAt(src, doc.Frames[from]);
                        if (d == null || !d.Channels.TryGetValue(ch.Id, out var c) || c.Pixels[i] == 0) continue;
                        (data ??= new byte[w * h])[y * w + x] = c.Pixels[i];
                    }
                    if (data != null) channels[ch.Id] = data;
                }
                var key = Key(trimmed, w, h, channels);
                if (!cellIndex.TryGetValue(key, out int ci))
                {
                    ci = cells.Count;
                    cellIndex[key] = ci;
                    cells.Add(new AtlasCell { Key = key, W = w, H = h, Pixels = trimmed, Channels = channels });
                }
                used.Add(ci);
                runFrames.Add(new AtlasFrame(ci, ox, oy, ms, from, to, pivot, points));
            }

            // the whole timeline, then each tag: the runs inside it
            clips.Add(new AtlasClip { Id = doc.TimelineTagId, Asset = doc, Name = "", Loop = LoopMode.Forward, Frames = runFrames });
            foreach (var tag in doc.Tags)
            {
                var (a, b) = doc.TagRange(tag);
                if (a < 0) continue;
                clips.Add(new AtlasClip
                {
                    Id = tag.Id, Asset = doc, Name = tag.Name, Loop = tag.Loop,
                    Frames = runFrames.Where(f => f.TimelineFrom >= a && f.TimelineTo <= b).ToList(),
                });
            }
        }

        var (width, height, error) = Pack(cells, config, tight);
        var indices = new byte[Math.Max(1, width * height)];
        if (error == null)
            foreach (var c in cells)
                for (int y = 0; y < c.H; y++)
                    Array.Copy(c.Pixels, y * c.W, indices, (c.Y + y) * width + c.X, c.W);

        return new AtlasBuild
        {
            Config = config, Width = width, Height = height, Indices = indices, Cells = cells,
            Assets = assets, Clips = clips, CellsOf = cellsOf, Error = error,
        };
    }

    /// <summary>Runs of identical frames, split where tags start and end (as in the timeline export).</summary>
    public static List<(int from, int to)> Runs(Document doc)
    {
        var layers = doc.Layers.Where(l => l.Visible && l.Role != LayerRole.Reference).ToList();
        var keyed = doc.Points.Where(p => p.Keyed).ToList();
        string Sig(int f) => string.Join(",", layers.Select(l => doc.DrawingAt(l, doc.Frames[f])?.Id.ToString() ?? "-"))
            + "|" + string.Join(",", keyed.Select(t => doc.PointAt(t, f, out _)?.ToString() ?? "-"));
        var breaks = new HashSet<int>();
        foreach (var tag in doc.Tags)
        {
            var (a, b) = doc.TagRange(tag);
            breaks.Add(a);
            breaks.Add(b + 1);
        }
        var runs = new List<(int from, int to)>();
        string? last = null;
        for (int f = 0; f < doc.Frames.Count; f++)
        {
            var sig = Sig(f);
            if (runs.Count > 0 && sig == last && !breaks.Contains(f)) runs[^1] = (runs[^1].from, f);
            else runs.Add((f, f));
            last = sig;
        }
        return runs;
    }

    private static int Ms(Document doc, int frame) => (int)Math.Round(frame * 1000.0 / doc.Fps);

    /// <summary>The palette index on top at each pixel, over the given layers.</summary>
    /// <param name="sources">When given, which layer each pixel's color came from.</param>
    public static void FlattenIndices(Document doc, IReadOnlyList<Layer> layers, int frameIndex, byte[] dest, Layer?[]? sources = null)
    {
        Array.Clear(dest);
        if (sources != null) Array.Clear(sources);
        var frame = doc.Frames[frameIndex];
        foreach (var layer in layers)
        {
            if (!doc.TryGetCel(layer, frame, out var cel)) continue;
            var px = cel.Pixels;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i] == Palette.Transparent) continue;
                dest[i] = px[i];
                if (sources != null) sources[i] = layer;
            }
        }
    }

    private static byte[]? Trim(byte[] px, int w, int h, out int ox, out int oy, out int tw, out int th)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (px[y * w + x] == 0) continue;
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }
        ox = oy = tw = th = 0;
        if (x1 < 0) return null;
        ox = x0; oy = y0; tw = x1 - x0 + 1; th = y1 - y0 + 1;
        var result = new byte[tw * th];
        for (int y = 0; y < th; y++) Array.Copy(px, (y0 + y) * w + x0, result, y * tw, tw);
        return result;
    }

    /// <summary>A content key: the same picture always gets the same key (FNV-1a over size and pixels).</summary>
    private static string Key(byte[] px, int w, int h, Dictionary<int, byte[]>? channels = null)
    {
        ulong hash = 14695981039346656037UL;
        void Mix(byte b) { hash ^= b; hash *= 1099511628211UL; }
        foreach (var b in BitConverter.GetBytes(w)) Mix(b);
        foreach (var b in BitConverter.GetBytes(h)) Mix(b);
        foreach (var b in px) Mix(b);
        // the same colors with different data are different pictures
        if (channels != null)
            foreach (var (id, data) in channels.OrderBy(kv => kv.Key))
            {
                foreach (var b in BitConverter.GetBytes(id)) Mix(b);
                foreach (var b in data) Mix(b);
            }
        return hash.ToString("x16");
    }

    // ---------------------------------------------------------------- packing

    private static (int w, int h, string? error) Pack(List<AtlasCell> cells, AtlasConfig config, bool tight)
    {
        int pad = Math.Max(0, config.Padding);
        int max = Math.Clamp(config.MaxSize, 16, 16384);
        if (cells.Count == 0) return (Math.Max(1, pad * 2), Math.Max(1, pad * 2), null);
        var big = cells.FirstOrDefault(c => c.W + pad * 2 > max || c.H + pad * 2 > max);
        if (big != null) return (1, 1, $"A {big.W}x{big.H} picture doesn't fit in {max}x{max}.");

        var sizes = new List<(int w, int h)>();
        if (!tight && config.LastWidth > 0 && config.LastHeight > 0) sizes.Add((config.LastWidth, config.LastHeight));
        long area = cells.Sum(c => (long)(c.W + pad) * (c.H + pad));
        int side = Math.Max(cells.Max(c => Math.Max(c.W, c.H)) + pad * 2, (int)Math.Ceiling(Math.Sqrt(area)));
        int w = Round(config, side), h = Round(config, side);
        // smaller shapes first, growing the shorter side
        int sw = Round(config, cells.Max(c => c.W) + pad * 2), sh = Round(config, cells.Max(c => c.H) + pad * 2);
        for (int tw = sw; tw <= max; tw = Grow(config, tw))
        for (int th = sh; th <= max; th = Grow(config, th))
            if ((long)tw * th >= area) sizes.Add((tw, th));
        sizes = sizes.Take(1).Concat(sizes.Skip(1).OrderBy(s => (long)s.w * s.h).ThenBy(s => Math.Abs(s.w - s.h))).Distinct().ToList();

        foreach (var (tw, th) in sizes)
        {
            if (tw > max || th > max) continue;
            if (TryPack(cells, config, tw, th, pad, keep: !tight))
            {
                config.LastWidth = tw;
                config.LastHeight = th;
                config.Placements = cells.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => new[] { g.First().X, g.First().Y });
                return (tw, th, null);
            }
        }
        return (1, 1, $"Everything doesn't fit in {max}x{max}. Raise the max size or split the assets into another atlas.");
    }

    private static int Round(AtlasConfig c, int v)
    {
        if (!c.PowerOfTwo) return Math.Max(1, v);
        int p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    private static int Grow(AtlasConfig c, int v) => c.PowerOfTwo ? v * 2 : v + Math.Max(16, v / 4);

    private static bool TryPack(List<AtlasCell> cells, AtlasConfig config, int w, int h, int pad, bool keep)
    {
        // pictures take (w + pad) x (h + pad) of an area inset by pad, so every picture has pad
        // empty pixels on all sides
        var rects = new MaxRects(w - pad, h - pad);
        var placed = new HashSet<AtlasCell>();
        if (keep)
        {
            foreach (var c in cells)
            {
                if (!config.Placements.TryGetValue(c.Key, out var at)) continue;
                var r = new PixelRect(at[0] - pad, at[1] - pad, c.W + pad, c.H + pad);
                if (r.X < 0 || r.Y < 0 || !rects.TryPlace(r)) continue;
                c.X = at[0];
                c.Y = at[1];
                placed.Add(c);
            }
        }
        foreach (var c in cells.Where(c => !placed.Contains(c)).OrderByDescending(c => Math.Max(c.W, c.H)).ThenByDescending(c => c.W * c.H))
        {
            if (rects.Insert(c.W + pad, c.H + pad) is not { } r) return false;
            c.X = r.X + pad;
            c.Y = r.Y + pad;
        }
        return true;
    }

    /// <summary>MaxRects bin packing (best short side fit).</summary>
    private sealed class MaxRects
    {
        private List<PixelRect> _free;

        public MaxRects(int w, int h) => _free = new List<PixelRect> { new(0, 0, w, h) };

        public bool TryPlace(PixelRect r)
        {
            if (!_free.Any(f => Contains(f, r))) return false;
            Use(r);
            return true;
        }

        public PixelRect? Insert(int w, int h)
        {
            PixelRect? best = null;
            int bestShort = int.MaxValue, bestLong = int.MaxValue;
            foreach (var f in _free)
            {
                if (f.W < w || f.H < h) continue;
                int dx = f.W - w, dy = f.H - h;
                int s = Math.Min(dx, dy), l = Math.Max(dx, dy);
                if (s < bestShort || (s == bestShort && l < bestLong))
                {
                    best = new PixelRect(f.X, f.Y, w, h);
                    bestShort = s;
                    bestLong = l;
                }
            }
            if (best is { } b) Use(b);
            return best;
        }

        private void Use(PixelRect u)
        {
            var next = new List<PixelRect>();
            foreach (var f in _free)
            {
                if (u.X >= f.X + f.W || u.X + u.W <= f.X || u.Y >= f.Y + f.H || u.Y + u.H <= f.Y)
                {
                    next.Add(f);
                    continue;
                }
                if (u.X > f.X) next.Add(new PixelRect(f.X, f.Y, u.X - f.X, f.H));
                if (u.X + u.W < f.X + f.W) next.Add(new PixelRect(u.X + u.W, f.Y, f.X + f.W - (u.X + u.W), f.H));
                if (u.Y > f.Y) next.Add(new PixelRect(f.X, f.Y, f.W, u.Y - f.Y));
                if (u.Y + u.H < f.Y + f.H) next.Add(new PixelRect(f.X, u.Y + u.H, f.W, f.Y + f.H - (u.Y + u.H)));
            }
            // drop free rects inside others
            _free = next.Where((a, i) => !next.Where((b, j) => j != i && Contains(b, a) && (b != a || j < i)).Any()).ToList();
        }

        private static bool Contains(PixelRect outer, PixelRect inner) =>
            inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.W <= outer.X + outer.W && inner.Y + inner.H <= outer.Y + outer.H;
    }
}
