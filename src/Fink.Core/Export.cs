using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fink.Core;

public sealed class ExportOptions
{
    /// <summary>Base file name: writes &lt;Name&gt;.png and &lt;Name&gt;.anim.json.</summary>
    public string Name { get; set; } = "sprite";
    /// <summary>Columns in the atlas grid; 0 picks a roughly square grid.</summary>
    public int Columns { get; set; }
    /// <summary>Transparent pixels between cells (and around the edge).</summary>
    public int Padding { get; set; }
}

/// <summary>What an export produced.</summary>
public sealed class ExportResult
{
    public required byte[] Png { get; init; }
    public required string Json { get; init; }
    public required AnimFile Anim { get; init; }
    public required int AtlasWidth { get; init; }
    public required int AtlasHeight { get; init; }
    /// <summary>Different pictures packed into the atlas.</summary>
    public int Cells => Anim.Cells.Count;

    public string Summary => $"{Anim.Frames.Count} frames, {Cells} unique pictures, {Anim.Tags.Count} tags, {AtlasWidth}x{AtlasHeight}";

    /// <summary>Writes &lt;name&gt;.png and &lt;name&gt;.anim.json into a folder. Returns the paths.</summary>
    public string[] WriteTo(string folder)
    {
        Directory.CreateDirectory(folder);
        var png = Path.Combine(folder, Anim.Image + ".png");
        var json = Path.Combine(folder, Anim.Image + ".anim.json");
        File.WriteAllBytes(png, Png);
        File.WriteAllText(json, Json);
        return new[] { png, json };
    }
}

// ---------------------------------------------------------------- the anim.json schema (version 1)

public sealed record AnimFile
{
    public int Version { get; init; } = 1;
    public required string Image { get; init; }
    public required int Fps { get; init; }
    public required AnimSize Cell { get; init; }
    public required AnimGrid Grid { get; init; }
    public int Padding { get; init; }
    /// <summary>Where each different picture sits in the atlas.</summary>
    public required List<AnimRect> Cells { get; init; }
    /// <summary>The animation in order: a held drawing is one frame with a longer ms.</summary>
    public required List<AnimFrame> Frames { get; init; }
    public required List<AnimTag> Tags { get; init; }
}

public sealed record AnimSize(int W, int H);
public sealed record AnimGrid(int Cols, int Rows);
public sealed record AnimRect(int X, int Y, int W, int H);

/// <param name="Cell">Index into cells; frames that look the same share one.</param>
/// <param name="TimelineFrom">First timeline frame (0-based) this covers, for tools.</param>
public sealed record AnimFrame(int Cell, int X, int Y, int W, int H, int Ms, int TimelineFrom, int TimelineTo);

/// <param name="From">First index into frames.</param>
/// <param name="To">Last index into frames (inclusive).</param>
/// <param name="Loop">forward, pingpong or once.</param>
public sealed record AnimTag(string Name, int From, int To, string Loop);

/// <summary>
/// Turns a document into a sprite sheet and its anim.json. Reference layers and hidden layers
/// are left out. Frames that look the same are packed once; a run of identical frames (a hold
/// across every layer) becomes one frame with its time added up. Tags never share a frame with
/// what's outside them, so a run is split where a tag starts or ends.
/// </summary>
public static class Exporter
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static ExportResult Export(Document doc, ExportOptions options)
    {
        int n = doc.Frames.Count, w = doc.Width, h = doc.Height;
        var layers = doc.Layers.Where(l => l.Visible && l.Role != LayerRole.Reference).ToList();

        // what each frame looks like, as the drawings shown on it
        string Signature(int f) => string.Join(",", layers.Select(l => doc.DrawingAt(l, doc.Frames[f])?.Id.ToString() ?? "-"));
        var signatures = Enumerable.Range(0, n).Select(Signature).ToArray();

        // runs of identical frames, broken at tag edges
        var breaks = new HashSet<int>();
        foreach (var tag in doc.Tags)
        {
            var (a, b) = doc.TagRange(tag);
            breaks.Add(a);
            breaks.Add(b + 1);
        }
        var runs = new List<(int from, int to)>();
        for (int f = 0; f < n; f++)
        {
            if (runs.Count > 0 && signatures[f] == signatures[runs[^1].to] && !breaks.Contains(f))
                runs[^1] = (runs[^1].from, f);
            else
                runs.Add((f, f));
        }

        // one cell per different picture
        var cellOf = new Dictionary<string, int>();
        var cellFrames = new List<int>();
        foreach (var (from, _) in runs)
        {
            if (cellOf.ContainsKey(signatures[from])) continue;
            cellOf[signatures[from]] = cellFrames.Count;
            cellFrames.Add(from);
        }

        int count = cellFrames.Count;
        int cols = options.Columns > 0 ? Math.Min(options.Columns, count) : (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (count + cols - 1) / cols;
        int pad = Math.Max(0, options.Padding);
        int atlasW = cols * w + (cols + 1) * pad, atlasH = rows * h + (rows + 1) * pad;
        var atlas = new uint[atlasW * atlasH];
        var cells = new List<AnimRect>();
        var picture = new uint[w * h];
        for (int c = 0; c < count; c++)
        {
            int cx = pad + (c % cols) * (w + pad), cy = pad + (c / cols) * (h + pad);
            Flatten(doc, layers, cellFrames[c], picture);
            for (int y = 0; y < h; y++)
                Array.Copy(picture, y * w, atlas, (cy + y) * atlasW + cx, w);
            cells.Add(new AnimRect(cx, cy, w, h));
        }

        // times: rounded on the running total so they add up exactly
        int MsAt(int frame) => (int)Math.Round(frame * 1000.0 / doc.Fps);
        var frames = runs.Select(r =>
        {
            int cell = cellOf[signatures[r.from]];
            var rect = cells[cell];
            return new AnimFrame(cell, rect.X, rect.Y, rect.W, rect.H, MsAt(r.to + 1) - MsAt(r.from), r.from, r.to);
        }).ToList();

        var tags = doc.Tags.Select(tag =>
        {
            var (a, b) = doc.TagRange(tag);
            int from = runs.FindIndex(r => r.from == a);
            int to = runs.FindLastIndex(r => r.to == b);
            return new AnimTag(tag.Name, from, to, tag.Loop switch
            {
                LoopMode.PingPong => "pingpong",
                LoopMode.Once => "once",
                _ => "forward",
            });
        }).ToList();

        var anim = new AnimFile
        {
            Image = options.Name,
            Fps = doc.Fps,
            Cell = new AnimSize(w, h),
            Grid = new AnimGrid(cols, rows),
            Padding = pad,
            Cells = cells,
            Frames = frames,
            Tags = tags,
        };
        return new ExportResult
        {
            Png = Png.Encode(atlas, atlasW, atlasH),
            Json = JsonSerializer.Serialize(anim, Json),
            Anim = anim,
            AtlasWidth = atlasW,
            AtlasHeight = atlasH,
        };
    }

    /// <summary>One frame of the given layers over transparency, palette resolved to RGBA.</summary>
    public static void Flatten(Document doc, IReadOnlyList<Layer> layers, int frameIndex, uint[] dest)
    {
        Array.Clear(dest);
        var frame = doc.Frames[frameIndex];
        foreach (var layer in layers)
        {
            if (!doc.TryGetCel(layer, frame, out var cel)) continue;
            var px = cel.Pixels;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i] == Palette.Transparent) continue;
                var c = doc.Palette[px[i]];
                dest[i] = c.A == 255 ? c.Packed : Over(dest[i], c);
            }
        }
    }

    /// <summary>Straight-alpha "over".</summary>
    private static uint Over(uint dst, Rgba src)
    {
        float sa = src.A / 255f, da = (dst >> 24) / 255f;
        float oa = sa + da * (1 - sa);
        if (oa <= 0) return 0;
        byte Mix(int s, int d) => (byte)Math.Round((s * sa + d * da * (1 - sa)) / oa);
        byte r = Mix(src.R, (int)(dst & 0xFF)), g = Mix(src.G, (int)((dst >> 8) & 0xFF)), b = Mix(src.B, (int)((dst >> 16) & 0xFF));
        return (uint)(r | (g << 8) | (b << 16) | ((byte)Math.Round(oa * 255) << 24));
    }
}
