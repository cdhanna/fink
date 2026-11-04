using System.Text.Json;

namespace Fink.Core;

/// <summary>
/// Everything about a project that isn't inside an asset's timeline: the palette (colors, ramps,
/// variants), data channels, which assets there are (in order) and their names, atlas settings,
/// export-on-save. Captured before and after a project change, it makes that change undoable.
///
/// Asset objects themselves are kept, not copied: deleting an asset and undoing brings back the
/// same asset, history and all. Where atlases put their pictures isn't part of it (packing moves
/// those on its own), so undo never shuffles the atlas.
/// </summary>
public sealed class ProjectState
{
    private List<Rgba> _entries = null!;
    private List<(string name, int start, int count)> _ramps = null!;
    private List<(string name, List<Rgba> entries)> _variants = null!;
    private List<DataChannel> _channels = null!;
    private List<(Document doc, string name)> _assets = null!;
    private string _atlases = null!;
    private bool _exportOnSave;

    private static readonly JsonSerializerOptions Json = new();

    public static ProjectState Capture(Project p) => new()
    {
        _entries = p.Palette.Entries.ToList(),
        _ramps = p.Palette.Ramps.Select(r => (r.Name, r.Start, r.Count)).ToList(),
        _variants = p.Palette.Variants.Select(v => (v.Name, v.Entries.ToList())).ToList(),
        _channels = p.Channels.ToList(),
        _assets = p.Assets.Select(a => (a, a.Name)).ToList(),
        _atlases = JsonSerializer.Serialize(p.Atlases.Select(a => Settings(a)).ToList(), Json),
        _exportOnSave = p.ExportOnSave,
    };

    /// <summary>An atlas's settings without its placements (those belong to packing, not to undo).</summary>
    private static AtlasConfig Settings(AtlasConfig a) => new()
    {
        Name = a.Name, IncludeAll = a.IncludeAll, AssetIds = a.AssetIds.ToList(), MaxSize = a.MaxSize,
        PowerOfTwo = a.PowerOfTwo, Padding = a.Padding, OutputFolder = a.OutputFolder, CodeFolder = a.CodeFolder,
        ContentPath = a.ContentPath,
        Maps = a.Maps.Select(m => new MapConfig { Name = m.Name, R = m.R, G = m.G, B = m.B, A = m.A }).ToList(),
    };

    public void Restore(Project p)
    {
        var pal = p.Palette;
        pal.Entries.Clear();
        pal.Entries.AddRange(_entries);
        pal.Ramps.Clear();
        pal.Ramps.AddRange(_ramps.Select(r => new PaletteRamp(r.name, r.start, r.count)));
        pal.Variants.Clear();
        pal.Variants.AddRange(_variants.Select(v => new PaletteVariant(v.name, v.entries)));
        pal.Touch();

        p.Channels.Clear();
        p.Channels.AddRange(_channels);

        p.Assets.Clear();
        foreach (var (doc, name) in _assets)
        {
            doc.Name = name;
            p.Assets.Add(doc);
        }

        // settings come back; each atlas keeps where it had put its pictures
        var restored = JsonSerializer.Deserialize<List<AtlasConfig>>(_atlases, Json) ?? new();
        var old = p.Atlases.ToList();
        for (int i = 0; i < restored.Count; i++)
        {
            var match = old.FirstOrDefault(o => o.Name == restored[i].Name) ?? (i < old.Count ? old[i] : null);
            if (match == null) continue;
            restored[i].Placements = match.Placements;
            restored[i].LastWidth = match.LastWidth;
            restored[i].LastHeight = match.LastHeight;
        }
        p.Atlases.Clear();
        p.Atlases.AddRange(restored);
        p.ExportOnSave = _exportOnSave;
        p.Touch();
    }

    /// <summary>Same palette, channels, assets, names and settings?</summary>
    public bool SameAs(ProjectState other) =>
        _entries.SequenceEqual(other._entries)
        && _ramps.SequenceEqual(other._ramps)
        && _variants.Count == other._variants.Count
        && _variants.Zip(other._variants).All(v => v.First.name == v.Second.name && v.First.entries.SequenceEqual(v.Second.entries))
        && _channels.SequenceEqual(other._channels)
        && _assets.SequenceEqual(other._assets)
        && _atlases == other._atlases
        && _exportOnSave == other._exportOnSave;
}
