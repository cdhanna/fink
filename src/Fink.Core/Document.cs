namespace Fink.Core;

/// <summary>
/// What a layer is for. The tools enforce it: line tools only draw on Line layers, and fill tools
/// only paint Fill layers. Reference layers are shown but never edited or exported.
/// </summary>
public enum LayerRole
{
    Line,
    Fill,
    Reference,
}

public sealed class Layer
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; set; }
    public LayerRole Role { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }

    /// <summary>Every drawing this layer owns, exposed or not. Numbers are unique within the layer.</summary>
    public List<Drawing> Drawings { get; } = new();

    /// <summary>
    /// When set, this layer has no timing of its own: on each frame it shows its drawing with the
    /// same number as the drawing <see cref="Follows"/> shows there. A Fill layer follows its Line
    /// layer, so fill 3 always goes with line 3, through holds, reuses and cycles.
    /// </summary>
    public Layer? Follows { get; set; }

    public Layer(string name, LayerRole role)
    {
        Name = name;
        Role = role;
    }

    public Drawing? DrawingNumbered(int number) => Drawings.FirstOrDefault(d => d.Number == number);

    public int NextNumber() => Drawings.Count == 0 ? 1 : Drawings.Max(d => d.Number) + 1;
}

/// <summary>One picture on one layer. Exposed on any number of frames (a hold, or reuse).</summary>
public sealed class Drawing
{
    public Guid Id { get; } = Guid.NewGuid();
    public int Number { get; }
    public Cel Cel { get; }
    /// <summary>Where this drawing puts each point track (by track id), in canvas pixels.</summary>
    public Dictionary<int, PointXY> Points { get; } = new();
    /// <summary>Data channels painted on this drawing (by channel id): one byte per pixel each.</summary>
    public Dictionary<int, Cel> Channels { get; } = new();

    public Drawing(int number, Cel cel)
    {
        Number = number;
        Cel = cel;
    }

    /// <summary>A copy with its own pixels, points and channels.</summary>
    public Drawing CloneAs(int number)
    {
        var d = new Drawing(number, Cel.Clone());
        foreach (var (k, v) in Points) d.Points[k] = v;
        foreach (var (k, v) in Channels) d.Channels[k] = v.Clone();
        return d;
    }

    /// <summary>The channel's values on this drawing, made (all zero) if it has none yet.</summary>
    public Cel Channel(int channelId)
    {
        if (!Channels.TryGetValue(channelId, out var c)) Channels[channelId] = c = new Cel(Cel.Width, Cel.Height);
        return c;
    }

    public override string ToString() => $"#{Number}";
}

/// <summary>A point in a drawing's canvas, in pixels (the pixel's top-left corner; +0.5 for its middle).</summary>
public readonly record struct PointXY(int X, int Y);

/// <summary>
/// A named point that rides on a layer's drawings (a hand, a muzzle), or the asset's pivot: the
/// point that stays still on screen while frames change. Each drawing of <see cref="Layer"/> can
/// put it somewhere; a drawing that doesn't inherits it from the nearest frame that does.
/// </summary>
/// <param name="Id">Stable across exports (POINT_ constants in a game).</param>
/// <param name="Keyed">
/// A keyed point isn't tied to drawings: it has keys on frames (a row of its own in the
/// timeline) and moves between them, held or, with <paramref name="Smooth"/>, in a straight line.
/// </param>
public sealed record PointTrack(int Id, string Name, bool IsPivot, Layer Layer, bool Keyed = false, bool Smooth = false);

/// <summary>A column of the timeline. Frames all last 1/fps of a second; holds make things last longer.</summary>
public sealed class Frame
{
    public Guid Id { get; } = Guid.NewGuid();
}

public enum LoopMode
{
    Forward,
    PingPong,
    Once,
}

/// <summary>
/// A named range of frames ("walk", "idle"), exported as an animation clip. It holds the frames
/// themselves, not their indices, so inserting frames inside a tag makes it longer.
/// </summary>
/// <param name="Id">Stable across exports and unique in the project, so a game's TAG_ constants
/// keep pointing at the same animation. 0 until the tag joins a project.</param>
public sealed record Tag(string Name, Frame From, Frame To, LoopMode Loop = LoopMode.Forward, int Id = 0);

/// <summary>A grid of palette indices.</summary>
public sealed class Cel
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public Cel(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height];
    }

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    public byte Get(int x, int y) => InBounds(x, y) ? Pixels[y * Width + x] : Palette.Transparent;

    public Cel Clone()
    {
        var c = new Cel(Width, Height);
        Buffer.BlockCopy(Pixels, 0, c.Pixels, 0, Pixels.Length);
        return c;
    }
}

/// <summary>
/// An animation laid out like a dope sheet: a stack of layers (index 0 is the bottom), a row of
/// frames at a fixed rate, and for each layer which of its drawings is exposed on each frame.
/// A drawing exposed on several frames in a row is a hold; the same drawing exposed again later
/// is reuse (a cycle). Pixels belong to drawings, so editing a held or reused drawing changes it
/// everywhere it appears.
/// </summary>
public sealed class Document
{
    /// <summary>The asset's name in its project (and its default export name).</summary>
    public string Name { get; set; } = "sprite";
    /// <summary>Stable id within the project (0 until it joins one).</summary>
    public int Id { get; set; }
    /// <summary>Id of the asset's whole-timeline clip (TAG_&lt;ASSET&gt; in a game).</summary>
    public int TimelineTagId { get; set; }
    /// <summary>The project this asset belongs to, which hands out ids.</summary>
    public Project? Owner { get; set; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>The project's palette, shared with every other asset.</summary>
    public Palette Palette { get; }
    public List<Layer> Layers { get; } = new();
    public List<Frame> Frames { get; } = new();
    public int Fps { get; set; } = 12;
    public List<Tag> Tags { get; } = new();
    /// <summary>Named points and the pivot.</summary>
    public List<PointTrack> Points { get; } = new();

    public PointTrack? Pivot => Points.FirstOrDefault(p => p.IsPivot);

    /// <summary>Keys of keyed points: per track id, per frame.</summary>
    public Dictionary<int, Dictionary<Frame, PointXY>> PointKeys { get; private set; } = new();

    public Dictionary<Frame, PointXY> KeysOf(PointTrack track)
    {
        if (!PointKeys.TryGetValue(track.Id, out var keys)) PointKeys[track.Id] = keys = new Dictionary<Frame, PointXY>();
        return keys;
    }

    /// <summary>A keyed point on a frame: its key, or between the keys around it (held, or a straight line).</summary>
    private PointXY? KeyedAt(PointTrack track, int frameIndex, out bool own)
    {
        own = false;
        if (!PointKeys.TryGetValue(track.Id, out var keys) || keys.Count == 0) return null;
        if (keys.TryGetValue(Frames[frameIndex], out var here)) { own = true; return here; }
        int before = -1, after = -1;
        for (int i = frameIndex - 1; i >= 0; i--) if (keys.ContainsKey(Frames[i])) { before = i; break; }
        for (int i = frameIndex + 1; i < Frames.Count; i++) if (keys.ContainsKey(Frames[i])) { after = i; break; }
        if (before < 0) return after < 0 ? null : keys[Frames[after]];
        var a = keys[Frames[before]];
        if (after < 0 || !track.Smooth) return a;
        var b = keys[Frames[after]];
        float t = (frameIndex - before) / (float)(after - before);
        return new PointXY((int)MathF.Round(a.X + (b.X - a.X) * t), (int)MathF.Round(a.Y + (b.Y - a.Y) * t));
    }

    /// <summary>
    /// Where a point is on a frame: on the drawing its layer shows there, or else the nearest
    /// frame before (then after) whose drawing has it. Null if no drawing has it at all.
    /// </summary>
    public PointXY? PointAt(PointTrack track, int frameIndex, out bool own)
    {
        own = false;
        if (frameIndex < 0 || frameIndex >= Frames.Count) return null;
        if (track.Keyed) return KeyedAt(track, frameIndex, out own);
        if (DrawingAt(track.Layer, Frames[frameIndex])?.Points.TryGetValue(track.Id, out var p) == true) { own = true; return p; }
        for (int i = frameIndex - 1; i >= 0; i--)
            if (DrawingAt(track.Layer, Frames[i])?.Points.TryGetValue(track.Id, out p) == true) return p;
        for (int i = frameIndex + 1; i < Frames.Count; i++)
            if (DrawingAt(track.Layer, Frames[i])?.Points.TryGetValue(track.Id, out p) == true) return p;
        return null;
    }

    /// <summary>The pivot on a frame; the canvas middle when there's none.</summary>
    public PointXY PivotAt(int frameIndex) =>
        Pivot is { } t && PointAt(t, frameIndex, out _) is { } p ? p : new PointXY(Width / 2, Height / 2);

    /// <summary>A tag's first and last frame index (in order, even if its frames were moved past each other).</summary>
    public (int from, int to) TagRange(Tag tag)
    {
        int a = Frames.IndexOf(tag.From), b = Frames.IndexOf(tag.To);
        return a <= b ? (a, b) : (b, a);
    }

    /// <summary>The innermost tag covering a frame, if any.</summary>
    public Tag? TagAt(int index) => Tags
        .Where(t => { var (a, b) = TagRange(t); return a >= 0 && index >= a && index <= b; })
        .OrderBy(t => { var (a, b) = TagRange(t); return b - a; })
        .FirstOrDefault();

    /// <summary>Exposures of layers with their own timing. Followers are never in here.</summary>
    private Dictionary<(Guid layer, Guid frame), Drawing> _exposures = new();

    public Document(int width, int height, Palette? palette = null)
    {
        Width = width;
        Height = height;
        Palette = palette ?? Palette.CreateDefault();
    }

    /// <summary>A new document with a Fill layer (following its timing) under a Line layer, and one frame.</summary>
    public static Document CreateDefault(int width = 128, int height = 128, Palette? palette = null, string name = "sprite")
    {
        var doc = new Document(width, height, palette) { Name = name };
        var line = new Layer("Line", LayerRole.Line);
        doc.Layers.Add(new Layer("Fill", LayerRole.Fill) { Follows = line });
        doc.Layers.Add(line);
        doc.Frames.Add(new Frame());
        return doc;
    }

    // ------------------------------------------------------------------ timing

    /// <summary>The layer whose exposures decide what <paramref name="layer"/> shows.</summary>
    public Layer TimingLayer(Layer layer) =>
        layer.Follows is { } src && src != layer && Layers.Contains(src) ? src : layer;

    public bool IsFollower(Layer layer) => TimingLayer(layer) != layer;

    public IEnumerable<Layer> FollowersOf(Layer layer) => Layers.Where(l => l != layer && TimingLayer(l) == layer);

    /// <summary>What an own-timing layer has exposed on a frame (ignores following).</summary>
    public Drawing? ExposureOf(Layer layer, Frame frame) =>
        _exposures.TryGetValue((layer.Id, frame.Id), out var d) ? d : null;

    /// <summary>Sets one cell of an own-timing layer. Not recorded; go through <see cref="DocumentOps"/>.</summary>
    public void SetExposure(Layer layer, Frame frame, Drawing? drawing)
    {
        if (drawing == null) _exposures.Remove((layer.Id, frame.Id));
        else _exposures[(layer.Id, frame.Id)] = drawing;
    }

    /// <summary>The drawing number shown on a frame (for a follower: its source's), or null for an empty cell.</summary>
    public int? NumberAt(Layer layer, Frame frame) => ExposureOf(TimingLayer(layer), frame)?.Number;

    /// <summary>The drawing a layer shows on a frame, or null.</summary>
    public Drawing? DrawingAt(Layer layer, Frame frame)
    {
        var timing = TimingLayer(layer);
        var d = ExposureOf(timing, frame);
        if (timing == layer || d == null) return d;
        return layer.DrawingNumbered(d.Number);
    }

    public bool TryGetCel(Layer layer, Frame frame, out Cel cel)
    {
        cel = DrawingAt(layer, frame)?.Cel!;
        return cel != null;
    }

    /// <summary>
    /// The cel to edit at (layer, frame). With nothing there, makes a new drawing and exposes it
    /// on this frame; <paramref name="created"/> is that change, already applied, to put in the
    /// history with the pixels (or undo if nothing was drawn). Returns null when the layer
    /// follows another that has nothing on this frame.
    /// </summary>
    public Cel? CelForEdit(Layer layer, Frame frame, out IEdit? created)
    {
        created = null;
        var existing = DrawingAt(layer, frame);
        if (existing != null) return existing.Cel;

        var timing = TimingLayer(layer);
        if (timing != layer)
        {
            var src = ExposureOf(timing, frame);
            if (src == null) return null;
            var d = new Drawing(src.Number, new Cel(Width, Height));
            created = new ActionEdit($"New drawing {layer.Name} #{d.Number}",
                redo: () => layer.Drawings.Add(d),
                undo: () => layer.Drawings.Remove(d));
            created.Redo();
            return d.Cel;
        }
        else
        {
            var d = new Drawing(layer.NextNumber(), new Cel(Width, Height));
            created = new ActionEdit($"New drawing {layer.Name} #{d.Number}",
                redo: () => { layer.Drawings.Add(d); SetExposure(layer, frame, d); },
                undo: () => { layer.Drawings.Remove(d); SetExposure(layer, frame, null); });
            created.Redo();
            return d.Cel;
        }
    }

    /// <summary>Test and script helper: the cel at (layer, frame), made if missing, without history.</summary>
    public Cel GetCel(Layer layer, Frame frame) =>
        CelForEdit(layer, frame, out _) ?? throw new InvalidOperationException($"{layer.Name} follows a layer with nothing on this frame.");

    /// <summary>The frames, from <paramref name="index"/>, that show the same thing on this layer (a hold, or a run of empty cells).</summary>
    public (int start, int end) RunAt(Layer layer, int index)
    {
        var timing = TimingLayer(layer);
        var d = ExposureOf(timing, Frames[index]);
        int s = index, e = index;
        while (s > 0 && ExposureOf(timing, Frames[s - 1]) == d) s--;
        while (e < Frames.Count - 1 && ExposureOf(timing, Frames[e + 1]) == d) e++;
        return (s, e);
    }

    /// <summary>
    /// For onion skin: the frame showing the k-th different drawing before (direction -1) or after
    /// (+1) the one on <paramref name="index"/>, skipping holds and empty cells. Null if none.
    /// </summary>
    public int? NeighborDrawingFrame(Layer layer, int index, int direction, int k)
    {
        var timing = TimingLayer(layer);
        var last = ExposureOf(timing, Frames[index]);
        int found = 0;
        for (int i = index + direction; i >= 0 && i < Frames.Count; i += direction)
        {
            var d = ExposureOf(timing, Frames[i]);
            if (d == null || d == last) continue;
            last = d;
            if (++found == k) return i;
        }
        return null;
    }

    /// <summary>The visible Line layers' cels on a frame; the boundary that fills respect.</summary>
    public IEnumerable<Cel> VisibleLineCels(Frame frame)
    {
        foreach (var layer in Layers)
        {
            if (layer.Role == LayerRole.Line && layer.Visible && TryGetCel(layer, frame, out var cel))
                yield return cel;
        }
    }

    // ------------------------------------------------------------------ snapshots (for undo)

    /// <summary>Everything structural: frames, layers, exposures, which drawings each layer has, following, fps.</summary>
    public sealed class Structure
    {
        internal List<Frame> Frames { get; init; } = null!;
        internal List<Layer> Layers { get; init; } = null!;
        internal Dictionary<(Guid, Guid), Drawing> Exposures { get; init; } = null!;
        internal Dictionary<Layer, (List<Drawing> drawings, Layer? follows)> LayerState { get; init; } = null!;
        internal int Fps { get; init; }
        internal List<Tag> Tags { get; init; } = null!;
        internal List<PointTrack> Points { get; init; } = null!;
        internal Dictionary<int, Dictionary<Frame, PointXY>> PointKeys { get; init; } = null!;
    }

    public Structure Capture() => new()
    {
        Frames = Frames.ToList(),
        Layers = Layers.ToList(),
        Exposures = new Dictionary<(Guid, Guid), Drawing>(_exposures),
        LayerState = Layers.ToDictionary(l => l, l => (l.Drawings.ToList(), l.Follows)),
        Fps = Fps,
        Tags = Tags.ToList(),
        Points = Points.ToList(),
        PointKeys = PointKeys.ToDictionary(kv => kv.Key, kv => new Dictionary<Frame, PointXY>(kv.Value)),
    };

    public void Restore(Structure s)
    {
        Frames.Clear();
        Frames.AddRange(s.Frames);
        Layers.Clear();
        Layers.AddRange(s.Layers);
        _exposures = new Dictionary<(Guid, Guid), Drawing>(s.Exposures);
        foreach (var (layer, (drawings, follows)) in s.LayerState)
        {
            layer.Drawings.Clear();
            layer.Drawings.AddRange(drawings);
            layer.Follows = follows;
        }
        Fps = s.Fps;
        Tags.Clear();
        Tags.AddRange(s.Tags);
        Points.Clear();
        Points.AddRange(s.Points);
        PointKeys = s.PointKeys.ToDictionary(kv => kv.Key, kv => new Dictionary<Frame, PointXY>(kv.Value));
    }
}
