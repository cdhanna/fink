namespace Fink.Core;

/// <summary>
/// A set of assets (animations) that share one palette and are packed into atlases together.
/// Saved as one .fink file.
/// </summary>
public sealed class Project
{
    public string Name { get; set; } = "untitled";
    public Palette Palette { get; }
    public List<Document> Assets { get; } = new();
    public List<AtlasConfig> Atlases { get; } = new();
    /// <summary>Data painted alongside colors (normal, emissive...), exported as atlas maps.</summary>
    public List<DataChannel> Channels { get; } = new();
    /// <summary>Saving also exports every atlas that has an output folder.</summary>
    public bool ExportOnSave { get; set; } = true;

    /// <summary>The next unused id for assets and tags. Ids are never reused.</summary>
    public int NextIdValue { get; set; } = 1;

    public int NextId() => NextIdValue++;

    /// <summary>Gives every asset and tag an id if it lacks one, and makes sure there's an atlas.</summary>
    public void AssignIds()
    {
        foreach (var doc in Assets)
        {
            doc.Owner = this;
            if (doc.Id == 0) doc.Id = NextId();
            if (doc.TimelineTagId == 0) doc.TimelineTagId = NextId();
            for (int i = 0; i < doc.Tags.Count; i++)
                if (doc.Tags[i].Id == 0) doc.Tags[i] = doc.Tags[i] with { Id = NextId() };
            for (int i = 0; i < doc.Points.Count; i++)
                if (doc.Points[i].Id == 0) doc.Points[i] = doc.Points[i] with { Id = NextId() };
        }
        if (Atlases.Count == 0) Atlases.Add(new AtlasConfig { Name = "atlas" });
    }

    public DataChannel? FindChannel(string name) =>
        Channels.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The assets an atlas packs, in project order.</summary>
    public List<Document> AssetsOf(AtlasConfig atlas) =>
        atlas.IncludeAll ? Assets.ToList() : Assets.Where(a => atlas.AssetIds.Contains(a.Id)).ToList();

    /// <summary>Bumps when assets are added, removed or renamed (pixel edits have their own histories).</summary>
    public int Version { get; private set; }

    public Project(Palette? palette = null) => Palette = palette ?? Palette.CreateDefault();

    public void Touch() => Version++;

    /// <summary>A project with one asset of the given size.</summary>
    public static Project CreateDefault(int width = 128, int height = 128)
    {
        var p = new Project();
        p.Assets.Add(Document.CreateDefault(width, height, p.Palette, "sprite"));
        p.AssignIds();
        p.Channels.Add(new DataChannel(p.NextId(), "normal", ChannelKind.Normal));
        p.Channels.Add(new DataChannel(p.NextId(), "emissive", ChannelKind.Value));
        return p;
    }

    public Document AddAsset(string name, int width, int height)
    {
        var doc = Document.CreateDefault(width, height, Palette, UniqueName(name));
        Assets.Add(doc);
        AssignIds();
        Touch();
        return doc;
    }

    public void RemoveAsset(Document doc)
    {
        if (Assets.Count <= 1) return;
        Assets.Remove(doc);
        Touch();
    }

    public void Rename(Document doc, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == doc.Name) return;
        doc.Name = UniqueName(name, doc);
        Touch();
    }

    /// <summary>The name, or the name with a number added if another asset has it.</summary>
    public string UniqueName(string name, Document? except = null)
    {
        bool Taken(string n) => Assets.Any(a => a != except && a.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        int i = 2;
        while (Taken($"{name} {i}")) i++;
        return $"{name} {i}";
    }
}
