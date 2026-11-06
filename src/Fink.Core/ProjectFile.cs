using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fink.Core;

/// <summary>
/// Reads and writes .fink files: a zip holding
/// <code>
///   fink.json                   format, version, project name, palette (entries, ramps, variants), asset order
///   assets/&lt;n&gt;/asset.json      size, fps, layers, which drawing each layer shows on each frame, tags
///   assets/&lt;n&gt;/L&lt;layer&gt;_&lt;drawing&gt;.bin   a drawing's palette indices, one byte per pixel
/// </code>
/// Saving writes a temporary file and then swaps it in, so a crash mid-save never leaves a
/// half-written file where the last good one was.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".fink";
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---------------------------------------------------------------- the json shapes

    private sealed class ProjectJson
    {
        public string Format { get; set; } = "fink";
        public int Version { get; set; } = FormatVersion;
        public string Name { get; set; } = "";
        public PaletteJson Palette { get; set; } = new();
        public List<string> Assets { get; set; } = new();
        public int NextId { get; set; } = 1;
        public bool ExportOnSave { get; set; } = true;
        public List<ChannelJson>? Channels { get; set; }
        public List<AtlasConfig> Atlases { get; set; } = new();
    }

    private sealed class PaletteJson
    {
        /// <summary>#rrggbbaa, index 0 first.</summary>
        public List<string> Entries { get; set; } = new();
        public List<RampJson> Ramps { get; set; } = new();
        public List<VariantJson> Variants { get; set; } = new();
    }

    private sealed record RampJson(string Name, int Start, int Count);
    private sealed record ChannelJson(int Id, string Name, string Kind);
    private sealed record PointJson(int Id, string Name, bool Pivot, int Layer,
        bool Keyed = false, bool Smooth = false, Dictionary<string, int[]>? Keys = null);
    private sealed record VariantJson(string Name, List<string> Entries);

    private sealed class AssetJson
    {
        public string Name { get; set; } = "";
        public int Id { get; set; }
        public int TimelineTagId { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Fps { get; set; } = 12;
        public int Frames { get; set; } = 1;
        /// <summary>Bottom of the stack first.</summary>
        public List<LayerJson> Layers { get; set; } = new();
        public List<TagJson> Tags { get; set; } = new();
        public List<PointJson> Points { get; set; } = new();
        public List<int>? OnionPins { get; set; }
        /// <summary>9-slice insets: left, top, right, bottom. Absent when the asset doesn't stretch.</summary>
        public int[]? Slice { get; set; }
    }

    private sealed class LayerJson
    {
        public string Name { get; set; } = "";
        public string Role { get; set; } = "Line";
        public bool Visible { get; set; } = true;
        public bool Locked { get; set; }
        /// <summary>Editor-only: how see-through the layer is on the canvas. Never exported.</summary>
        public float ViewOpacity { get; set; } = 1f;
        public bool OnionOff { get; set; }
        public int? OnionBefore { get; set; }
        public int? OnionAfter { get; set; }
        /// <summary>Index of the layer whose timing this one follows.</summary>
        public int? Follows { get; set; }
        /// <summary>Fill layers: indices of the Line layers fills stop at. Absent: every visible one.</summary>
        public List<int>? FillBounds { get; set; }
        public List<int> Drawings { get; set; } = new();
        /// <summary>The drawing number shown on each frame (null: nothing). Absent for followers.</summary>
        public List<int?>? Exposures { get; set; }
        /// <summary>Per drawing number: point track id to [x, y].</summary>
        public Dictionary<string, Dictionary<string, int[]>>? Points { get; set; }
        /// <summary>Per drawing number: the channel ids it has data for (in L&lt;layer&gt;_&lt;n&gt;.c&lt;id&gt;.bin).</summary>
        public Dictionary<string, List<int>>? Channels { get; set; }
    }

    private sealed record TagJson(string Name, int From, int To, string Loop, int Id = 0);

    // ---------------------------------------------------------------- save

    public static void Save(Project project, string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".saving";
        using (var file = File.Create(temp)) Write(project, file);
        File.Move(temp, full, overwrite: true);
    }

    public static void Write(Project project, Stream stream)
    {
        project.AssignIds();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var pal = project.Palette;
        var pj = new ProjectJson
        {
            Name = project.Name,
            Palette = new PaletteJson
            {
                Entries = pal.Entries.Select(Hex).ToList(),
                Ramps = pal.Ramps.Select(r => new RampJson(r.Name, r.Start, r.Count)).ToList(),
                Variants = pal.Variants.Select(v => new VariantJson(v.Name, v.Entries.Select(Hex).ToList())).ToList(),
            },
            Assets = project.Assets.Select(a => a.Name).ToList(),
            NextId = project.NextIdValue,
            ExportOnSave = project.ExportOnSave,
            Channels = project.Channels.Select(c => new ChannelJson(c.Id, c.Name, c.Kind.ToString())).ToList(),
            Atlases = project.Atlases,
        };
        WriteText(zip, "fink.json", JsonSerializer.Serialize(pj, Json));

        for (int ai = 0; ai < project.Assets.Count; ai++)
        {
            var doc = project.Assets[ai];
            var dir = $"assets/{ai}/";
            var aj = new AssetJson
            {
                Name = doc.Name,
                Id = doc.Id,
                TimelineTagId = doc.TimelineTagId,
                Width = doc.Width,
                Height = doc.Height,
                Fps = doc.Fps,
                Frames = doc.Frames.Count,
                Slice = doc.Slice is { } sl ? new[] { sl.Left, sl.Top, sl.Right, sl.Bottom } : null,
            };
            for (int li = 0; li < doc.Layers.Count; li++)
            {
                var layer = doc.Layers[li];
                var follows = doc.IsFollower(layer) ? doc.Layers.IndexOf(doc.TimingLayer(layer)) : (int?)null;
                aj.Layers.Add(new LayerJson
                {
                    Name = layer.Name,
                    Role = layer.Role.ToString(),
                    Visible = layer.Visible,
                    Locked = layer.Locked,
                    ViewOpacity = layer.ViewOpacity,
                    OnionOff = layer.OnionOff,
                    OnionBefore = layer.OnionBefore,
                    OnionAfter = layer.OnionAfter,
                    Follows = follows,
                    FillBounds = layer.FillBounds?.Select(l => doc.Layers.IndexOf(l)).Where(i => i >= 0).ToList(),
                    Drawings = layer.Drawings.Select(d => d.Number).ToList(),
                    Exposures = follows != null ? null : doc.Frames.Select(f => doc.ExposureOf(layer, f)?.Number).ToList(),
                    Points = layer.Drawings.Any(d => d.Points.Count > 0)
                        ? layer.Drawings.Where(d => d.Points.Count > 0).ToDictionary(d => d.Number.ToString(),
                            d => d.Points.ToDictionary(kv => kv.Key.ToString(), kv => new[] { kv.Value.X, kv.Value.Y }))
                        : null,
                    Channels = layer.Drawings.Any(d => d.Channels.Count > 0)
                        ? layer.Drawings.Where(d => d.Channels.Count > 0).ToDictionary(d => d.Number.ToString(), d => d.Channels.Keys.ToList())
                        : null,
                });
                foreach (var d in layer.Drawings)
                {
                    var entry = zip.CreateEntry($"{dir}L{li}_{d.Number}.bin", CompressionLevel.Optimal);
                    using (var s = entry.Open()) s.Write(d.Cel.Pixels);
                    foreach (var (ch, cel) in d.Channels)
                    {
                        var ce = zip.CreateEntry($"{dir}L{li}_{d.Number}.c{ch}.bin", CompressionLevel.Optimal);
                        using var cs = ce.Open();
                        cs.Write(cel.Pixels);
                    }
                }
            }
            foreach (var tag in doc.Tags)
            {
                var (a, b) = doc.TagRange(tag);
                if (a >= 0) aj.Tags.Add(new TagJson(tag.Name, a, b, tag.Loop.ToString(), tag.Id));
            }
            aj.OnionPins = doc.OnionPins.Select(f => doc.Frames.IndexOf(f)).Where(i => i >= 0).ToList();
            aj.Points = doc.Points.Select(p => new PointJson(p.Id, p.Name, p.IsPivot, doc.Layers.IndexOf(p.Layer), p.Keyed, p.Smooth,
                p.Keyed ? doc.KeysOf(p).Where(kv => doc.Frames.Contains(kv.Key))
                    .ToDictionary(kv => doc.Frames.IndexOf(kv.Key).ToString(), kv => new[] { kv.Value.X, kv.Value.Y }) : null)).ToList();
            WriteText(zip, dir + "asset.json", JsonSerializer.Serialize(aj, Json));
        }
    }

    // ---------------------------------------------------------------- load

    public static Project Load(string path)
    {
        using var file = File.OpenRead(path);
        var project = Read(file);
        return project;
    }

    public static Project Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var pj = JsonSerializer.Deserialize<ProjectJson>(ReadText(zip, "fink.json"), Json)
                 ?? throw new InvalidDataException("fink.json is empty.");
        if (pj.Format != "fink") throw new InvalidDataException("Not a Fink file.");
        if (pj.Version > FormatVersion)
            throw new InvalidDataException($"Saved by a newer Fink (format {pj.Version}; this one reads up to {FormatVersion}).");

        var palette = new Palette();
        palette.Entries.Clear();
        palette.Entries.AddRange(pj.Palette.Entries.Select(Rgba.FromHex));
        if (palette.Entries.Count == 0) palette.Entries.Add(new Rgba(0, 0, 0, 0));
        palette.Ramps.AddRange(pj.Palette.Ramps.Select(r => new PaletteRamp(r.Name, r.Start, r.Count)));
        palette.Variants.AddRange(pj.Palette.Variants.Select(v => new PaletteVariant(v.Name, v.Entries.Select(Rgba.FromHex))));

        var project = new Project(palette) { Name = pj.Name, NextIdValue = Math.Max(1, pj.NextId), ExportOnSave = pj.ExportOnSave };
        project.Atlases.AddRange(pj.Atlases);
        if (pj.Channels != null)
            project.Channels.AddRange(pj.Channels.Select(c => new DataChannel(c.Id, c.Name, Enum.TryParse<ChannelKind>(c.Kind, true, out var k) ? k : ChannelKind.Value)));
        for (int ai = 0; ai < pj.Assets.Count; ai++)
        {
            var dir = $"assets/{ai}/";
            var aj = JsonSerializer.Deserialize<AssetJson>(ReadText(zip, dir + "asset.json"), Json)
                     ?? throw new InvalidDataException($"{dir}asset.json is empty.");
            var doc = new Document(aj.Width, aj.Height, palette)
            {
                Name = aj.Name, Fps = Math.Clamp(aj.Fps, 1, 120), Id = aj.Id, TimelineTagId = aj.TimelineTagId, Owner = project,
                Slice = aj.Slice is { Length: 4 } s4 ? new NineSlice(s4[0], s4[1], s4[2], s4[3]).Clamp(aj.Width, aj.Height) : null,
            };
            for (int f = 0; f < Math.Max(1, aj.Frames); f++) doc.Frames.Add(new Frame());

            var layers = aj.Layers.Select(lj => new Layer(lj.Name, Enum.Parse<LayerRole>(lj.Role, true))
            {
                Visible = lj.Visible,
                Locked = lj.Locked,
                ViewOpacity = Math.Clamp(lj.ViewOpacity, 0f, 1f),
                OnionOff = lj.OnionOff,
                OnionBefore = lj.OnionBefore,
                OnionAfter = lj.OnionAfter,
            }).ToList();
            doc.Layers.AddRange(layers);
            for (int li = 0; li < layers.Count; li++)
            {
                var lj = aj.Layers[li];
                var layer = layers[li];
                if (lj.Follows is { } fi && fi >= 0 && fi < layers.Count && fi != li) layer.Follows = layers[fi];
                if (lj.FillBounds is { } fb)
                    layer.FillBounds = fb.Where(i => i >= 0 && i < layers.Count && i != li).Select(i => layers[i]).ToList();
                foreach (var number in lj.Drawings)
                {
                    var cel = new Cel(doc.Width, doc.Height);
                    var entry = zip.GetEntry($"{dir}L{li}_{number}.bin");
                    if (entry != null)
                    {
                        using var s = entry.Open();
                        s.ReadExactly(cel.Pixels);
                    }
                    var drawing = new Drawing(number, cel);
                    if (lj.Points?.TryGetValue(number.ToString(), out var pts) == true)
                        foreach (var (id, xy) in pts)
                            if (int.TryParse(id, out var pid) && xy.Length == 2) drawing.Points[pid] = new PointXY(xy[0], xy[1]);
                    if (lj.Channels?.TryGetValue(number.ToString(), out var chans) == true)
                        foreach (var ch in chans)
                        {
                            var ce = zip.GetEntry($"{dir}L{li}_{number}.c{ch}.bin");
                            if (ce == null) continue;
                            var data = new Cel(doc.Width, doc.Height);
                            using var cs = ce.Open();
                            cs.ReadExactly(data.Pixels);
                            drawing.Channels[ch] = data;
                        }
                    layer.Drawings.Add(drawing);
                }
                if (lj.Exposures != null)
                    for (int f = 0; f < lj.Exposures.Count && f < doc.Frames.Count; f++)
                        if (lj.Exposures[f] is { } n && layer.DrawingNumbered(n) is { } d)
                            doc.SetExposure(layer, doc.Frames[f], d);
            }
            foreach (var tj in aj.Tags)
            {
                int a = Math.Clamp(tj.From, 0, doc.Frames.Count - 1), b = Math.Clamp(tj.To, 0, doc.Frames.Count - 1);
                doc.Tags.Add(new Tag(tj.Name, doc.Frames[a], doc.Frames[b],
                    Enum.TryParse<LoopMode>(tj.Loop, true, out var loop) ? loop : LoopMode.Forward, tj.Id));
            }
            foreach (var pin in aj.OnionPins ?? new())
                if (pin >= 0 && pin < doc.Frames.Count) doc.OnionPins.Add(doc.Frames[pin]);
            foreach (var pt in aj.Points)
            {
                if (pt.Layer < 0 || pt.Layer >= layers.Count) continue;
                var track = new PointTrack(pt.Id, pt.Name, pt.Pivot, layers[pt.Layer], pt.Keyed, pt.Smooth);
                doc.Points.Add(track);
                if (pt.Keys != null)
                    foreach (var (f, xy) in pt.Keys)
                        if (int.TryParse(f, out var fi) && fi >= 0 && fi < doc.Frames.Count && xy.Length == 2)
                            doc.KeysOf(track)[doc.Frames[fi]] = new PointXY(xy[0], xy[1]);
            }
            project.Assets.Add(doc);
        }
        if (project.Assets.Count == 0) project.Assets.Add(Document.CreateDefault(64, 64, palette));
        // files from before ids: number everything now, past any id already in use
        int highest = project.Assets.SelectMany(a => a.Tags.Select(t => t.Id).Concat(a.Points.Select(p => p.Id)).Append(a.Id).Append(a.TimelineTagId))
            .Concat(project.Channels.Select(c => c.Id)).DefaultIfEmpty(0).Max();
        project.NextIdValue = Math.Max(project.NextIdValue, highest + 1);
        project.AssignIds();
        return project;
    }

    // ---------------------------------------------------------------- helpers

    private static string Hex(Rgba c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}{c.A:x2}";

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open());
        w.Write(text);
    }

    private static string ReadText(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"{name} is missing.");
        using var r = new StreamReader(entry.Open());
        return r.ReadToEnd();
    }
}
