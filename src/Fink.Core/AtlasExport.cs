using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fink.Core;

// ---------------------------------------------------------------- the manifest (<atlas>.atlas.json)
// Fade.MonoGame.Fink reads this. Everything a game looks up is by number (clip ids, cell and
// palette-row indices); names are only for people and tools.

public sealed record FinkAtlasManifest
{
    public string Format { get; init; } = "fink-atlas";
    public int Version { get; init; } = 1;
    public required string Name { get; init; }
    /// <summary>Changes whenever anything in the manifest changes; the generated code checks it.</summary>
    public int Stamp { get; set; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>The index texture: R = palette index, A = 255 where there's a pixel.</summary>
    public required string Image { get; init; }
    /// <summary>The palette texture: 256 wide, one row per variant (row 0 the base colors).</summary>
    public required string Palette { get; init; }
    public required List<string> PaletteRows { get; init; }
    /// <summary>Data maps: textures with the same layout as Image, and which channel is in each of R, G, B, A.</summary>
    public required List<ManifestMap> Maps { get; init; }
    public required List<ManifestCell> Cells { get; init; }
    public required List<ManifestAsset> Assets { get; init; }
    public required List<ManifestClip> Clips { get; init; }
}

public sealed record ManifestCell(int X, int Y, int W, int H);
/// <param name="Slice">9-slice insets (left, top, right, bottom) when the asset stretches; null otherwise.</param>
public sealed record ManifestAsset(int Id, string Name, int Width, int Height, int Fps, List<ManifestPoint> Points, int[]? Slice = null);
/// <param name="Id">The POINT_ constant's value.</param>
public sealed record ManifestPoint(int Id, string Name);
/// <param name="R">What's in red: a Value channel's name, a Normal channel's name.x/.y/.z, a number, or empty (0).</param>
public sealed record ManifestMap(string Name, string Image, string R, string G, string B, string A);

/// <param name="Asset">The asset's id.</param>
/// <param name="Name">The tag's name, or empty for the asset's whole timeline.</param>
/// <param name="Loop">forward, pingpong or once.</param>
public sealed record ManifestClip(int Id, int Asset, string Name, string Loop, List<ManifestFrame> Frames);

/// <param name="Cell">Index into cells; -1 is an empty frame.</param>
/// <param name="X">Where the picture's top-left sits in the asset's canvas.</param>
/// <param name="Px">The pivot, in the asset's canvas: the point that stays still on screen.</param>
/// <param name="Pts">The asset's named points on this frame, x then y, in the order of its points.</param>
public sealed record ManifestFrame(int Cell, int X, int Y, int Ms, int Px, int Py, int[] Pts);

/// <summary>Writes an atlas: index texture, palette texture, manifest and generated FadeBasic.</summary>
public static class AtlasExport
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string LoopName(LoopMode m) => m switch { LoopMode.PingPong => "pingpong", LoopMode.Once => "once", _ => "forward" };

    public static FinkAtlasManifest Manifest(AtlasBuild build, Palette palette)
    {
        var name = build.Config.Name;
        var m = new FinkAtlasManifest
        {
            Name = name,
            Width = build.Width,
            Height = build.Height,
            Image = $"{name}.png",
            Palette = $"{name}.palette.png",
            PaletteRows = new[] { "base" }.Concat(palette.Variants.Select(v => v.Name)).ToList(),
            Maps = build.Config.Maps.Select(m => new ManifestMap(m.Name, $"{name}.{m.Name}.png", m.R, m.G, m.B, m.A)).ToList(),
            Cells = build.Cells.Select(c => new ManifestCell(c.X, c.Y, c.W, c.H)).ToList(),
            Assets = build.Assets.Select(a => new ManifestAsset(a.Id, a.Name, a.Width, a.Height, a.Fps,
                a.Points.Where(p => !p.IsPivot).Select(p => new ManifestPoint(p.Id, p.Name)).ToList(),
                a.Slice is { } sl ? new[] { sl.Left, sl.Top, sl.Right, sl.Bottom } : null)).ToList(),
            Clips = build.Clips.Select(c => new ManifestClip(c.Id, c.Asset.Id, c.Name, LoopName(c.Loop),
                c.Frames.Select(f => new ManifestFrame(f.Cell, f.OffsetX, f.OffsetY, f.Ms, f.Pivot.X, f.Pivot.Y,
                    (f.Points ?? Array.Empty<PointXY>()).SelectMany(p => new[] { p.X, p.Y }).ToArray())).ToList())).ToList(),
        };
        m.Stamp = 0;
        m.Stamp = Stamp(JsonSerializer.Serialize(m, Json));
        return m;
    }

    private static int Stamp(string text)
    {
        uint h = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(text)) { h ^= b; h *= 16777619; }
        return (int)(h & 0x7FFFFFFF);
    }

    /// <summary>R = index, A = 255 where there's a pixel. Nothing else, so filtering/premultiplying can't change an index.</summary>
    public static byte[] IndexPng(AtlasBuild build)
    {
        var px = new uint[build.Width * build.Height];
        for (int i = 0; i < px.Length && i < build.Indices.Length; i++)
            if (build.Indices[i] != 0) px[i] = build.Indices[i] | 0xFF000000u;
        return Png.Encode(px, build.Width, build.Height);
    }

    /// <summary>256 wide, one row per variant. Unused entries are transparent.</summary>
    public static byte[] PalettePng(Palette palette)
    {
        int rows = 1 + palette.Variants.Count;
        var px = new uint[256 * rows];
        for (int r = 0; r < rows; r++)
        {
            var colors = palette.ColorsFor(r);
            for (int i = 1; i < colors.Count && i < 256; i++) px[r * 256 + i] = colors[i].Packed;
        }
        return Png.Encode(px, 256, rows);
    }

    /// <summary>
    /// A data map: for every pixel that has color, each of R, G, B, A from its slot (a channel's
    /// value, a normal component, or a constant). Same layout as the index texture.
    /// </summary>
    public static uint[] MapPixels(Project project, AtlasBuild build, MapConfig map)
    {
        var slots = map.Slots.Select(s => Slot(project, s)).ToArray();
        var px = new uint[build.Width * build.Height];
        foreach (var c in build.Cells)
            for (int y = 0; y < c.H; y++)
            for (int x = 0; x < c.W; x++)
            {
                int i = y * c.W + x;
                if (c.Pixels[i] == 0) continue;
                uint v = 0;
                for (int k = 0; k < 4; k++) v |= (uint)slots[k](c, i) << (8 * k);
                px[(c.Y + y) * build.Width + c.X + x] = v;
            }
        return px;
    }

    /// <summary>What a slot reads, as a function of (picture, pixel). Throws for a name that isn't a channel.</summary>
    public static Func<AtlasCell, int, byte> Slot(Project project, string slot)
    {
        slot = slot.Trim();
        if (slot.Length == 0) return (_, _) => 0;
        if (int.TryParse(slot, out var constant)) { byte b = (byte)Math.Clamp(constant, 0, 255); return (_, _) => b; }
        var dot = slot.LastIndexOf('.');
        var name = dot > 0 && slot[(dot + 1)..] is "x" or "y" or "z" ? slot[..dot] : slot;
        var ch = project.FindChannel(name) ?? throw new InvalidOperationException($"No data channel called \"{name}\" (in a map's slot \"{slot}\").");
        if (ch.Kind == ChannelKind.Value)
            return (c, i) => c.Channels.TryGetValue(ch.Id, out var d) ? d[i] : (byte)0;
        if (name == slot) throw new InvalidOperationException($"\"{slot}\" is a normal: say which part, {slot}.x, {slot}.y or {slot}.z.");
        int comp = slot[^1] - 'x';
        var encoded = Normals.Directions.Select(n => comp switch
        {
            0 => Normals.Encode(n.x),
            1 => Normals.Encode(-n.y), // up is +y in a normal map
            _ => Normals.Encode(n.z),
        }).ToArray();
        return (c, i) => encoded[Math.Min(c.Channels.TryGetValue(ch.Id, out var d) ? d[i] : 0, encoded.Length - 1)];
    }

    /// <summary>A palette color for each atlas pixel, for showing the atlas in the editor.</summary>
    public static uint[] Colorize(AtlasBuild build, IReadOnlyList<Rgba> colors)
    {
        var px = new uint[build.Width * build.Height];
        for (int i = 0; i < px.Length && i < build.Indices.Length; i++)
        {
            int idx = build.Indices[i];
            if (idx != 0 && idx < colors.Count) px[i] = colors[idx].Packed;
        }
        return px;
    }

    /// <summary>
    /// A folder as written in an atlas's settings, made absolute: a relative one is relative to the
    /// project file's folder (so a project and its game can move together), and needs one.
    /// </summary>
    public static string ResolveFolder(string folder, string? projectDir)
    {
        if (Path.IsPathRooted(folder)) return Path.GetFullPath(folder);
        if (projectDir == null)
            throw new InvalidOperationException($"\"{folder}\" is relative to the project file: save the project first, or use a full path.");
        return Path.GetFullPath(Path.Combine(projectDir, folder));
    }

    /// <summary>Writes everything. Returns the files written. Relative folders are relative to <paramref name="projectDir"/>.</summary>
    public static List<string> Write(Project project, AtlasBuild build, string? projectDir = null)
    {
        var config = build.Config;
        if (build.Error != null) throw new InvalidOperationException(build.Error);
        if (string.IsNullOrWhiteSpace(config.OutputFolder)) throw new InvalidOperationException($"Atlas \"{config.Name}\" has no output folder.");
        var dir = ResolveFolder(config.OutputFolder, projectDir);
        Directory.CreateDirectory(dir);
        var manifest = Manifest(build, project.Palette);
        var code = FadeBasicBindings.Generate(project, build, manifest);   // first: it can refuse (name clashes)

        var files = new List<string>();
        void Put(string path, byte[] bytes) { File.WriteAllBytes(path, bytes); files.Add(path); }
        Put(Path.Combine(dir, manifest.Image), IndexPng(build));
        Put(Path.Combine(dir, manifest.Palette), PalettePng(project.Palette));
        foreach (var map in config.Maps)
            Put(Path.Combine(dir, $"{config.Name}.{map.Name}.png"), Png.Encode(MapPixels(project, build, map), build.Width, build.Height));
        Put(Path.Combine(dir, $"{config.Name}.atlas.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Json)));
        Put(Path.Combine(dir, FadeBasicBindings.ShaderFileName(config)), Encoding.UTF8.GetBytes(FadeBasicBindings.PaletteShader(config)));
        var codeDir = string.IsNullOrWhiteSpace(config.CodeFolder) ? dir : ResolveFolder(config.CodeFolder, projectDir);
        Directory.CreateDirectory(codeDir);
        Put(Path.Combine(codeDir, FadeBasicBindings.FileName(config)), Encoding.UTF8.GetBytes(code));
        return files;
    }
}
