using System.Numerics;
using Fink.Core;

namespace Fink.Editor;

/// <summary>Picks a palette index from the canvas. Not a cel edit, so the editor handles it.</summary>
public sealed class EyedropperTool : Tool
{
    public override string Name => "Eyedropper";
    public override bool Allows(LayerRole role) => true;
}

/// <summary>Places the selected named point (or the pivot) on the current drawing. Not a cel edit.</summary>
public sealed class PointTool : Tool
{
    public override string Name => "Point";
    public override bool Allows(LayerRole role) => true;
}

/// <summary>A tool that is active only while its key is held.</summary>
public enum Momentary
{
    None,
    Eyedropper,
    Pan,
    /// <summary>The pen is flipped to its eraser end (or the eraser hold key is down).</summary>
    Eraser,
}

public enum PointerButton
{
    Primary,
    Secondary,
}

/// <summary>Zoom and pan of the canvas. Pan is the image center's offset from the view center.</summary>
public sealed class CanvasView
{
    public static readonly int[] ZoomLevels = { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64 };

    public int Zoom { get; set; } = 4;
    public Vector2 Pan { get; set; }
    /// <summary>Set to pick a zoom that fits the view on the next draw.</summary>
    public bool FitRequested { get; set; } = true;
    public bool ShowGrid { get; set; } = true;

    /// <summary>
    /// Steps the zoom, keeping the document point under <paramref name="anchor"/> (relative to the
    /// view center) in place.
    /// </summary>
    public void Step(int direction, Vector2 anchor)
    {
        int old = Zoom;
        int i = Array.IndexOf(ZoomLevels, old);
        if (i < 0) i = Array.FindLastIndex(ZoomLevels, z => z <= old);
        int next = ZoomLevels[Math.Clamp(i + Math.Sign(direction), 0, ZoomLevels.Length - 1)];
        if (next == old) return;
        var docPoint = (anchor - Pan) / old;
        Zoom = next;
        Pan = anchor - docPoint * next;
    }
}

/// <summary>
/// Everything the editor knows apart from the window: the document and its history, the current
/// frame/layer/tool/colors, playback, the canvas view, and pointer handling on the canvas.
/// </summary>
public sealed class EditorState
{
    /// <summary>Every asset and the palette they share.</summary>
    public Project Project { get; private set; } = Project.CreateDefault();
    /// <summary>The asset being edited.</summary>
    public Document Document { get; private set; }
    /// <summary>
    /// The project's undo history: one tree for everything (drawing in any asset, palette,
    /// channels, assets, atlas settings). Each step is tagged with the asset it changed.
    /// </summary>
    public History History { get; private set; } = new();
    /// <summary>Where the project was opened from or last saved to; null until saved.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Per asset: where you were in it.</summary>
    private sealed class AssetView
    {
        public int Frame;
        public int Layer = 1;
    }
    private readonly Dictionary<Document, AssetView> _views = new();
    /// <summary>The history step the file on disk matches.</summary>
    private HistoryNode? _savedNode;
    private bool _forceDirty;

    private AssetView ViewOf(Document d)
    {
        if (!_views.TryGetValue(d, out var v)) _views[d] = v = new AssetView();
        return v;
    }

    public IReadOnlyList<Tool> Tools { get; } = new Tool[]
    {
        new PencilTool(), new EraserTool(), new LineTool(), new FillTool(), new EyedropperTool(), new PointTool(),
        new SelectRectTool(), new LassoTool(), new WandTool(), new TransformTool(),
    };

    /// <summary>Selections, floating pixels and the clipboard.</summary>
    public SelectionController Selection { get; }

    private Tool _tool;
    public Tool Tool => _tool;
    public Momentary Momentary { get; private set; }

    /// <summary>The tool that a press on the canvas uses right now (a held eyedropper wins).</summary>
    public string EffectiveToolName => Momentary switch
    {
        Momentary.Eyedropper => "Eyedropper",
        Momentary.Pan => "Pan",
        Momentary.Eraser => "Eraser",
        _ => _tool.Name,
    };

    private int _frameIndex;
    private int _layerIndex = 1;
    /// <summary>Changing frame or layer stamps down any floating selection first.</summary>
    public int FrameIndex
    {
        get => _frameIndex;
        set
        {
            var v = Math.Clamp(value, 0, Document.Frames.Count - 1);
            if (v != _frameIndex) Selection.Commit();
            _frameIndex = v;
        }
    }

    public int LayerIndex
    {
        get => _layerIndex;
        set
        {
            var v = Math.Clamp(value, 0, Document.Layers.Count - 1);
            if (v != _layerIndex) Selection.Commit();
            _layerIndex = v;
        }
    }
    public Frame CurrentFrame => Document.Frames[FrameIndex];
    public Layer CurrentLayer => Document.Layers[LayerIndex];
    /// <summary>The drawing the current layer shows on the current frame, if any.</summary>
    public Drawing? CurrentDrawing => Document.DrawingAt(CurrentLayer, CurrentFrame);

    /// <summary>Which palette variant the canvas shows and the palette panel edits (0: the base colors).</summary>
    public int ViewVariant
    {
        get => Math.Clamp(_viewVariant, 0, Project.Palette.Variants.Count);
        set => _viewVariant = value;
    }
    private int _viewVariant;

    /// <summary>The colors the canvas shows.</summary>
    public IReadOnlyList<Rgba> ViewColors => Project.Palette.ColorsFor(ViewVariant);

    public byte Primary { get; set; } = 1;
    public byte Secondary { get; set; } = 6;
    public ToolSettings ToolSettings { get; } = new();
    public OnionSettings Onion { get; } = new();
    public CanvasView View { get; } = new();
    /// <summary>When a tool can't edit the current layer, switch to the nearest one it can.</summary>
    public bool AutoSelectLayer { get; set; }

    public bool Playing { get; private set; }
    private double _playMs;

    /// <summary>Seconds since start; the host advances it. Used for status message timeouts.</summary>
    public double Time { get; set; }
    public string? Status { get; private set; }
    private double _statusUntil;
    public string? VisibleStatus => Status != null && Time < _statusUntil ? Status : null;

    private readonly StrokeController _stroke = new();
    public bool Stroking => _stroke.Active;
    private bool _panning;
    public bool Panning => _panning;

    public EditorState()
    {
        Document = Project.Assets[0];
        History.DefaultTag = Document;
        _savedNode = History.Current; // a fresh project has nothing to save yet
        _tool = Tools[0];
        Selection = new SelectionController(this);
    }

    public void SetStatus(string text, double seconds = 2.5)
    {
        Status = text;
        _statusUntil = Time + seconds;
    }

    // ------------------------------------------------------------------ document

    /// <summary>A new project with one asset of this size.</summary>
    public void NewDocument(int width, int height) => SetProject(Project.CreateDefault(width, height), null);

    /// <summary>Replaces the project (new, opened, recovered). Starts out not dirty.</summary>
    public void SetProject(Project project, string? path)
    {
        _stroke.Cancel();
        Selection.Cancel();
        Project = project;
        _views.Clear();
        FilePath = path == null ? null : Path.GetFullPath(path);
        Document = project.Assets[0];
        History = new History { DefaultTag = Document };
        _frameIndex = 0;
        _layerIndex = Math.Min(1, Document.Layers.Count - 1);
        Selection.Reset();
        Playing = false;
        View.FitRequested = true;
        MarkSaved();
    }

    // ------------------------------------------------------------------ assets

    public void SwitchAsset(Document doc)
    {
        if (doc == Document || !Project.Assets.Contains(doc)) return;
        _stroke.Cancel();
        Selection.Commit();
        Playing = false;
        var current = ViewOf(Document);
        current.Frame = _frameIndex;
        current.Layer = _layerIndex;
        Document = doc;
        History.DefaultTag = doc;
        var next = ViewOf(doc);
        _frameIndex = next.Frame;
        _layerIndex = next.Layer;
        Clamp();
        Selection.Reset();
        View.FitRequested = true;
    }

    public void StepAsset(int delta)
    {
        int n = Project.Assets.Count, i = Project.Assets.IndexOf(Document);
        SwitchAsset(Project.Assets[((i + delta) % n + n) % n]);
    }

    public Document AddAsset(string name, int width, int height)
    {
        Document doc = null!;
        ProjectChange($"New asset \"{name}\"", () => doc = Project.AddAsset(name, width, height));
        SwitchAsset(doc);
        return doc;
    }

    /// <summary>A copy of an asset: same layers, drawings, timing and tags.</summary>
    public Document DuplicateAsset(Document source)
    {
        Settle();
        using var ms = new MemoryStream();
        var single = new Project(Project.Palette) { Name = Project.Name };
        single.Assets.Add(source);
        ProjectFile.Write(single, ms);
        ms.Position = 0;
        var copy = ProjectFile.Read(ms).Assets[0];
        // read gave it its own palette object: rebuild against ours
        var doc = new Document(copy.Width, copy.Height, Project.Palette) { Name = Project.UniqueName(source.Name + " copy"), Fps = copy.Fps, Owner = Project };
        doc.Frames.AddRange(copy.Frames);
        doc.Layers.AddRange(copy.Layers);
        foreach (var l in copy.Layers)
            if (!copy.IsFollower(l))
                foreach (var f in copy.Frames) doc.SetExposure(l, f, copy.ExposureOf(l, f));
        doc.Tags.AddRange(copy.Tags.Select(t => t with { Id = 0 }));
        ProjectChange($"Duplicate \"{source.Name}\"", () =>
        {
            Project.Assets.Insert(Project.Assets.IndexOf(source) + 1, doc);
            Project.AssignIds();
        });
        SwitchAsset(doc);
        return doc;
    }

    public void RemoveAsset(Document doc)
    {
        if (Project.Assets.Count <= 1 || !Project.Assets.Contains(doc)) return;
        if (doc == Document)
        {
            int i = Project.Assets.IndexOf(doc);
            SwitchAsset(Project.Assets[i > 0 ? i - 1 : 1]);
        }
        // its view stays: undo brings the asset back as it was
        ProjectChange($"Delete \"{doc.Name}\"", () => Project.RemoveAsset(doc));
    }

    public void RenameAsset(Document doc, string name) =>
        ProjectChange($"Rename \"{doc.Name}\" to \"{name.Trim()}\"", () => Project.Rename(doc, name));

    // ------------------------------------------------------------------ project changes (undoable)

    private ProjectState? _pendingChange;

    /// <summary>
    /// Makes a change to the project outside any asset's timeline (palette, channels, assets,
    /// atlas settings) one undoable step. Nothing is recorded if nothing changed.
    /// </summary>
    public void ProjectChange(string name, Action change)
    {
        Settle();
        var before = _pendingChange ?? ProjectState.Capture(Project);
        _pendingChange = null;
        change();
        Record(name, before);
    }

    /// <summary>
    /// For something changed bit by bit (dragging a color picker, typing a name): call when it
    /// starts, then <see cref="EndProjectChange"/> when it's done, for one step for the lot.
    /// </summary>
    public void BeginProjectChange() => _pendingChange ??= ProjectState.Capture(Project);

    public void EndProjectChange(string name)
    {
        if (_pendingChange is not { } before) return;
        _pendingChange = null;
        Record(name, before);
    }

    private ProjectState? _watch;

    /// <summary>
    /// For a panel that edits the project directly: call before drawing it. Remembers the
    /// project as it is (unless an edit there is still going on).
    /// </summary>
    public void WatchProject() => _watch ??= ProjectState.Capture(Project);

    /// <summary>
    /// Call after drawing the panel. Once nothing is being edited, whatever changed since
    /// <see cref="WatchProject"/> becomes one undoable step.
    /// </summary>
    public void SettleProject(string name, bool stillEditing)
    {
        if (_watch is not { } before || stillEditing) return;
        _watch = null;
        Record(name, before);
    }

    private void Record(string name, ProjectState before)
    {
        _watch = null; // an explicit change was just recorded; start watching afresh
        var after = ProjectState.Capture(Project);
        if (after.SameAs(before)) return;
        var tag = History.DefaultTag;
        History.DefaultTag = null; // a project step, not an asset's
        History.Push(new ActionEdit(name,
            redo: () => { after.Restore(Project); AfterProjectRestore(); },
            undo: () => { before.Restore(Project); AfterProjectRestore(); }));
        History.DefaultTag = tag;
    }

    /// <summary>After undo/redo of a project step, the current asset may be gone.</summary>
    private void AfterProjectRestore()
    {
        if (!Project.Assets.Contains(Document))
        {
            _stroke.Cancel();
            Selection.Cancel();
            Document = Project.Assets[0];
            History.DefaultTag = Document;
            var v = ViewOf(Document);
            _frameIndex = v.Frame;
            _layerIndex = v.Layer;
            Clamp();
            Selection.Reset();
            View.FitRequested = true;
        }
        if (ActiveChannelId != 0 && Project.Channels.All(c => c.Id != ActiveChannelId)) ActiveChannelId = 0;
    }

    public Document FindAsset(string name) =>
        Project.Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No asset \"{name}\"");

    // ------------------------------------------------------------------ saving

    /// <summary>Changed since it was opened or saved.</summary>
    public bool IsDirty => _forceDirty || History.Current != _savedNode;

    /// <summary>Changes whenever anything that is saved changes; for autosave and redrawing.</summary>
    public long ChangeStamp => Project.Version * 1_000_003L + Project.Palette.Version * 1009L
        + History.Version + (_forceDirty ? 7 : 0);

    public string DisplayName => FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : "untitled";

    public void MarkSaved()
    {
        _forceDirty = false;
        _savedNode = History.Current;
    }

    /// <summary>For recovered work: it's not saved anywhere yet.</summary>
    public void MarkDirty() => _forceDirty = true;

    public void Save(string path)
    {
        Settle();
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) full += ProjectFile.Extension;
        Project.Name = Path.GetFileNameWithoutExtension(full);
        // export first: it can move pictures in the atlas, and those places are saved too.
        // An export problem is reported; the save still happens.
        bool exporting = Project.ExportOnSave && Project.Atlases.Any(a => !string.IsNullOrWhiteSpace(a.OutputFolder));
        bool exported = exporting && ExportAtlases(onlyWithFolder: true);
        ProjectFile.Save(Project, full);
        FilePath = full;
        MarkSaved();
        if (!exporting) SetStatus($"Saved {Path.GetFileName(full)}", 2);
        else if (exported) SetStatus($"Saved {Path.GetFileName(full)} and exported its atlases", 3);
        else SetStatus($"Saved {Path.GetFileName(full)}, but the export failed: {LastAtlasExport}", 6);
    }

    public void Open(string path)
    {
        var project = ProjectFile.Load(path);
        SetProject(project, path);
        SetStatus($"Opened {Path.GetFileName(path)}", 2);
    }

    /// <summary>Keeps indices valid after undo/redo changed the frame or layer lists.</summary>
    public void Clamp()
    {
        _frameIndex = Math.Clamp(_frameIndex, 0, Document.Frames.Count - 1);
        _layerIndex = Math.Clamp(_layerIndex, 0, Document.Layers.Count - 1);
    }

    /// <summary>Stamps a floating selection down before a structural change.</summary>
    public void Settle() => Selection.Commit();

    public void Undo()
    {
        // undo while something floats puts it back where it came from
        if (Selection.Floating != null) { Selection.Cancel(); return; }
        var step = History.Current;
        History.Undo();
        ShowAssetOf(step);
    }

    public void Redo()
    {
        Settle();
        History.Redo();
        ShowAssetOf(History.Current);
    }

    /// <summary>Goes to any step of the history, on any branch.</summary>
    public void JumpTo(HistoryNode node)
    {
        Settle();
        _stroke.Cancel();
        History.JumpTo(node);
        ShowAssetOf(node.Edit != null ? node : History.Current);
    }

    /// <summary>After undo or redo: shows the asset the step changed, so you see what happened.</summary>
    private void ShowAssetOf(HistoryNode step)
    {
        if (step.Tag is Document d && d != Document && Project.Assets.Contains(d)) SwitchAsset(d);
        Clamp();
    }

    // ------------------------------------------------------------------ tools + layers

    public Tool FindTool(string name) => Tools.First(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void SelectTool(Tool tool)
    {
        if (tool is not ISelectionTool) Settle();
        _tool = tool;
        if (AutoSelectLayer && !tool.Allows(CurrentLayer.Role)) SelectLayerFor(tool);
    }

    public bool CanEditCurrentLayer(Tool tool) =>
        tool is EyedropperTool || (tool.Allows(CurrentLayer.Role) && !CurrentLayer.Locked && CurrentLayer.Visible);

    /// <summary>Moves to the nearest layer (searching outward) the tool can edit.</summary>
    public bool SelectLayerFor(Tool tool)
    {
        for (int d = 1; d < Document.Layers.Count; d++)
        foreach (var i in new[] { LayerIndex + d, LayerIndex - d })
        {
            if (i < 0 || i >= Document.Layers.Count) continue;
            var l = Document.Layers[i];
            if (tool.Allows(l.Role) && !l.Locked && l.Visible) { LayerIndex = i; return true; }
        }
        return false;
    }

    public void SelectLayer(string nameOrIndex)
    {
        if (int.TryParse(nameOrIndex, out var i)) { LayerIndex = i; return; }
        var idx = Document.Layers.FindIndex(l => l.Name.Equals(nameOrIndex, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) throw new ArgumentException($"No layer \"{nameOrIndex}\"");
        LayerIndex = idx;
    }

    public void AddLayer(LayerRole role)
    {
        Settle();
        // line layers go above the current one; fill layers below it (fill sits under line art);
        // reference layers at the bottom
        switch (role)
        {
            case LayerRole.Line:
                DocumentOps.AddLayer(Document, History, LayerIndex + 1, NextLayerName("Line"), role);
                LayerIndex++;
                break;
            case LayerRole.Fill:
                // a fill under a line layer follows that layer's timing
                var line = CurrentLayer.Role == LayerRole.Line && !Document.IsFollower(CurrentLayer) ? CurrentLayer : null;
                DocumentOps.AddLayer(Document, History, LayerIndex, NextLayerName("Fill"), role, line);
                break;
            default:
                DocumentOps.AddLayer(Document, History, 0, NextLayerName("Reference"), role);
                LayerIndex++;
                break;
        }
    }

    public void MoveLayer(int delta)
    {
        Settle();
        int to = LayerIndex + delta;
        if (to < 0 || to >= Document.Layers.Count) return;
        DocumentOps.MoveLayer(Document, History, LayerIndex, to);
        LayerIndex = to;
    }

    private string NextLayerName(string prefix)
    {
        int n = 1;
        while (Document.Layers.Any(l => l.Name == $"{prefix} {n}")) n++;
        return $"{prefix} {n}";
    }

    // ------------------------------------------------------------------ frames + playback

    public void StepFrame(int delta)
    {
        Playing = false;
        int n = Document.Frames.Count;
        FrameIndex = ((FrameIndex + delta) % n + n) % n;
    }

    /// <summary>Inserts a frame after this one and goes to it.</summary>
    public void AddFrame(NewFrameKind kind)
    {
        Settle();
        Playing = false;
        FrameIndex = DocumentOps.InsertFrame(Document, History, FrameIndex + 1, kind, CurrentLayer);
    }

    /// <summary>A new (or copied) drawing on the current layer, from this frame to the end of its hold.</summary>
    public void NewDrawing(bool duplicate)
    {
        Settle();
        if (!DocumentOps.NewDrawing(Document, History, CurrentLayer, FrameIndex, duplicate))
            SetStatus("Nothing here to duplicate.");
    }

    /// <summary>Holds the current drawing one frame longer or shorter, sliding the rest of the layer's timing.</summary>
    public void Retime(int delta)
    {
        Settle();
        if (!DocumentOps.Retime(Document, History, CurrentLayer, FrameIndex, delta))
            SetStatus(CurrentDrawing == null && Document.NumberAt(CurrentLayer, CurrentFrame) == null
                ? "Nothing exposed here to hold."
                : "It's already one frame long.");
    }

    public void SetCell(int frameIndex, Layer layer, int? number)
    {
        Settle();
        DocumentOps.SetCell(Document, History, layer, frameIndex, number);
    }

    public void SetHoldEnd(Layer layer, int start, int end)
    {
        Settle();
        DocumentOps.SetHoldEnd(Document, History, layer, start, end);
    }

    /// <summary>Moves, resizes or copies a hold (dragging a block in the dope sheet).</summary>
    public void PlaceHold(Layer layer, int from, int to, int newFrom, int newTo, bool copy)
    {
        Settle();
        if (DocumentOps.PlaceHold(Document, History, layer, from, to, newFrom, newTo, copy))
            FrameIndex = newFrom;
    }

    public void SetFollows(Layer layer, Layer? source)
    {
        Settle();
        if (DocumentOps.SetFollows(Document, History, layer, source) is { } why) SetStatus(why);
    }

    public Tag AddTag(string name, int from, int to, LoopMode loop = LoopMode.Forward)
    {
        Settle();
        return DocumentOps.AddTag(Document, History, name, from, to, loop);
    }

    public Tag UpdateTag(Tag tag, string? name = null, int? from = null, int? to = null, LoopMode? loop = null)
    {
        Settle();
        return DocumentOps.UpdateTag(Document, History, tag, name, from, to, loop);
    }

    public void RemoveTag(Tag tag)
    {
        Settle();
        DocumentOps.RemoveTag(Document, History, tag);
    }

    // ------------------------------------------------------------------ export

    public ExportOptions ExportOptions { get; } = new();
    /// <summary>Where exports go, usually a game's Assets/Textures folder.</summary>
    public string ExportFolder { get; set; } = "";
    public ExportResult? LastExport { get; private set; }
    public string? LastExportMessage { get; private set; }

    /// <summary>Writes the sprite sheet and anim.json. Returns false (with a message) on failure.</summary>
    public bool Export()
    {
        Settle();
        try
        {
            if (string.IsNullOrWhiteSpace(ExportFolder)) throw new InvalidOperationException("Choose a folder to export to.");
            if (string.IsNullOrWhiteSpace(ExportOptions.Name) || ExportOptions.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("The name can't be empty or contain characters like / : * ?");
            var result = Exporter.Export(Document, ExportOptions);
            var files = result.WriteTo(ExportFolder);
            LastExport = result;
            LastExportMessage = $"Exported {result.Summary} to {string.Join(" and ", files.Select(Path.GetFileName))}";
            SetStatus(LastExportMessage, 4);
            return true;
        }
        catch (Exception e)
        {
            LastExportMessage = $"Export failed: {e.Message}";
            SetStatus(LastExportMessage, 6);
            return false;
        }
    }

    // ------------------------------------------------------------------ atlases

    /// <summary>The atlas shown in the Atlas panel.</summary>
    public int AtlasIndex
    {
        get => Math.Clamp(_atlasIndex, 0, Math.Max(0, Project.Atlases.Count - 1));
        set => _atlasIndex = value;
    }
    private int _atlasIndex;

    public AtlasConfig CurrentAtlas
    {
        get
        {
            Project.AssignIds();
            return Project.Atlases[AtlasIndex];
        }
    }

    public AtlasConfig AddAtlas(string name)
    {
        Project.AssignIds();
        var unique = name;
        for (int i = 2; Project.Atlases.Any(a => a.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); i++) unique = $"{name}{i}";
        var config = new AtlasConfig { Name = unique, IncludeAll = false, OutputFolder = CurrentAtlas.OutputFolder };
        ProjectChange($"Add atlas \"{unique}\"", () => Project.Atlases.Add(config));
        AtlasIndex = Project.Atlases.Count - 1;
        return Project.Atlases[^1];
    }

    public void RemoveAtlas(AtlasConfig config)
    {
        if (Project.Atlases.Count <= 1) return;
        ProjectChange($"Delete atlas \"{config.Name}\"", () => Project.Atlases.Remove(config));
    }

    /// <summary>Packs an atlas (stable unless tight). Packing remembers places, so it marks the project changed.</summary>
    public AtlasBuild BuildAtlas(AtlasConfig config, bool tight = false)
    {
        var before = (config.LastWidth, config.LastHeight, config.Placements.Count);
        var build = AtlasBuilder.Build(Project, config, tight);
        if (tight || before != (config.LastWidth, config.LastHeight, config.Placements.Count)) Project.Touch();
        return build;
    }

    public string? LastAtlasExport { get; private set; }

    /// <summary>Exports every atlas. Returns false with a status when one can't be.</summary>
    public bool ExportAtlases(bool onlyWithFolder = false)
    {
        Settle();
        Project.AssignIds();
        var written = new List<string>();
        foreach (var config in Project.Atlases.Where(a => !onlyWithFolder || !string.IsNullOrWhiteSpace(a.OutputFolder)))
        {
            try
            {
                var build = AtlasBuilder.Build(Project, config);
                written.AddRange(AtlasExport.Write(Project, build));
            }
            catch (Exception e)
            {
                LastAtlasExport = $"Atlas \"{config.Name}\": {e.Message}";
                SetStatus(LastAtlasExport, 6);
                return false;
            }
        }
        Project.Touch(); // placements may have moved
        LastAtlasExport = $"Exported {Project.Atlases.Count} atlas{(Project.Atlases.Count == 1 ? "" : "es")}: {string.Join(", ", written.Select(Path.GetFileName))}";
        SetStatus(LastAtlasExport, 4);
        return true;
    }

    public void SetFps(int fps) => DocumentOps.SetFps(Document, History, fps);

    public void MoveFrame(int delta)
    {
        Settle();
        int to = FrameIndex + delta;
        if (to < 0 || to >= Document.Frames.Count) return;
        DocumentOps.MoveFrame(Document, History, FrameIndex, to);
        FrameIndex = to;
    }

    /// <summary>Playback loops the tag under the playhead (in its loop mode) instead of the whole timeline.</summary>
    public bool PlayTag { get; set; } = true;
    /// <summary>The tag being played, if any.</summary>
    public Tag? PlayingTag { get; private set; }
    private int _pingPong = 1;

    public void TogglePlay()
    {
        Settle();
        Playing = !Playing;
        _playMs = 0;
        _pingPong = 1;
        PlayingTag = Playing && PlayTag ? Document.TagAt(FrameIndex) : null;
    }

    private int NextPlayFrame()
    {
        int i = FrameIndex, n = Document.Frames.Count;
        if (PlayingTag == null || !Document.Tags.Contains(PlayingTag)) return (i + 1) % n;
        var (a, b) = Document.TagRange(PlayingTag);
        if (i < a || i > b) return a;
        switch (PlayingTag.Loop)
        {
            case LoopMode.Once:
                if (i >= b) Playing = false;
                return Math.Min(i + 1, b);
            case LoopMode.PingPong:
                if (a == b) return a;
                if (i + _pingPong > b || i + _pingPong < a) _pingPong = -_pingPong;
                return i + _pingPong;
            default:
                return i + 1 > b ? a : i + 1;
        }
    }

    public void Advance(double elapsedMs)
    {
        if (!Playing || Stroking) return;
        _playMs += elapsedMs;
        double frameMs = 1000.0 / Document.Fps;
        while (_playMs >= frameMs)
        {
            _playMs -= frameMs;
            FrameIndex = NextPlayFrame();
            if (!Playing) break;
        }
    }

    // ------------------------------------------------------------------ momentary tools

    public void BeginMomentary(Momentary m)
    {
        if (Stroking) return;
        Momentary = m;
    }

    public void EndMomentary(Momentary m)
    {
        if (Momentary != m) return;
        Momentary = Momentary.None;
        _panning = false;
    }

    // ------------------------------------------------------------------ pointer on the canvas

    private enum PointerOwner { None, Stroke, Pan, Eyedrop, Selection, Point }

    // ------------------------------------------------------------------ data channels

    /// <summary>Which data channel painting goes to (0: colors).</summary>
    public int ActiveChannelId { get; set; }
    public DataChannel? ActiveChannel => ActiveChannelId == 0 ? null : Project.Channels.FirstOrDefault(c => c.Id == ActiveChannelId);
    /// <summary>The value painted into Value channels (1..255; 0 erases).</summary>
    public byte ValueBrush { get; set; } = 255;
    /// <summary>The direction painted into Normal channels (an index into <see cref="Normals.Directions"/>).</summary>
    public byte NormalBrush { get; set; } = 1;
    public byte ChannelBrush => ActiveChannel?.Kind == ChannelKind.Normal ? NormalBrush : ValueBrush;

    /// <summary>Colors, then each channel, then colors again.</summary>
    public void CycleChannel()
    {
        var ids = new[] { 0 }.Concat(Project.Channels.Select(c => c.Id)).ToList();
        ActiveChannelId = ids[(ids.IndexOf(ActiveChannelId) + 1) % ids.Count];
        SetStatus(ActiveChannel == null ? "Painting colors" : $"Painting {ActiveChannel.Name}", 1.5);
    }

    public DataChannel AddChannel(string name, ChannelKind kind)
    {
        Project.AssignIds();
        var unique = name.Trim();
        for (int i = 2; Project.FindChannel(unique) != null; i++) unique = $"{name.Trim()} {i}";
        var ch = new DataChannel(Project.NextId(), unique, kind);
        ProjectChange($"Add channel \"{unique}\"", () => Project.Channels.Add(ch));
        ActiveChannelId = ch.Id;
        return ch;
    }

    /// <summary>Removes a channel from the project. Drawings keep their data, so undo brings it all back.</summary>
    public void RemoveChannel(DataChannel ch)
    {
        ProjectChange($"Remove channel \"{ch.Name}\"", () => Project.Channels.Remove(ch));
        if (ActiveChannelId == ch.Id) ActiveChannelId = 0;
    }

    // ------------------------------------------------------------------ points

    /// <summary>The point the Point tool places (by track id; the pivot when 0 or gone).</summary>
    public int SelectedPointId { get; set; }

    public PointTrack? SelectedPoint =>
        Document.Points.FirstOrDefault(p => p.Id == SelectedPointId) ?? Document.Pivot;

    /// <summary>The asset's pivot, made (on the current layer's timing layer) if it has none.</summary>
    public PointTrack EnsurePivot() =>
        Document.Pivot ?? DocumentOps.AddPoint(Document, History, "pivot", LayerForPoints(), pivot: true);

    /// <param name="keyed">Keys on the timeline instead of a place on each drawing.</param>
    public PointTrack AddPoint(string name, bool keyed = false)
    {
        Settle();
        Project.AssignIds();
        var unique = name.Trim();
        for (int i = 2; Document.Points.Any(p => p.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); i++) unique = $"{name.Trim()} {i}";
        var t = DocumentOps.AddPoint(Document, History, unique, LayerForPoints(), keyed: keyed);
        SelectedPointId = t.Id;
        return t;
    }

    public void RenamePoint(PointTrack t, string name) => DocumentOps.RenamePoint(Document, History, t, name);

    public void RemovePoint(PointTrack t)
    {
        DocumentOps.RemovePoint(Document, History, t);
        if (SelectedPointId == t.Id) SelectedPointId = 0;
    }

    /// <summary>Points ride on line art: the current layer's timing layer, or the top line layer.</summary>
    private Layer LayerForPoints()
    {
        var timing = Document.TimingLayer(CurrentLayer);
        if (timing.Role != LayerRole.Reference) return timing;
        return Document.Layers.LastOrDefault(l => l.Role == LayerRole.Line) ?? timing;
    }

    // a point being placed: where it's stored (a drawing's points, or a keyed point's keys)
    private PointTrack? _pointDrag;
    private Func<PointXY?>? _pointGet;
    private Action<PointXY?>? _pointPut;
    private PointXY? _pointBefore;
    private string _pointWhat = "";

    private void PointDown(int x, int y, PointerButton button)
    {
        Settle();
        Playing = false;
        var track = SelectedPoint ?? EnsurePivot();
        if (button == PointerButton.Secondary)
        {
            if (!DocumentOps.SetPoint(Document, History, track, FrameIndex, null))
                SetStatus($"\"{track.Layer.Name}\" has no drawing here.");
            return;
        }
        if (track.Keyed)
        {
            var keys = Document.KeysOf(track);
            var frame = CurrentFrame;
            _pointGet = () => keys.TryGetValue(frame, out var v) ? v : null;
            _pointPut = v => { if (v is { } p) keys[frame] = p; else keys.Remove(frame); };
            _pointWhat = $"Key {track.Name} on frame {FrameIndex + 1}";
        }
        else
        {
            var drawing = Document.DrawingAt(track.Layer, CurrentFrame);
            if (drawing == null)
            {
                SetStatus($"\"{track.Layer.Name}\" has no drawing here to put {track.Name} on.");
                return;
            }
            _pointGet = () => drawing.Points.TryGetValue(track.Id, out var v) ? v : null;
            _pointPut = v => { if (v is { } p) drawing.Points[track.Id] = p; else drawing.Points.Remove(track.Id); };
            _pointWhat = $"Place {track.Name} on #{drawing.Number}";
        }
        _pointDrag = track;
        _pointBefore = _pointGet();
        _pointPut(new PointXY(x, y));
        _owner = PointerOwner.Point;
    }

    private void PointMove(int x, int y) => _pointPut?.Invoke(new PointXY(x, y));

    private void PointUp()
    {
        if (_pointDrag == null || _pointGet == null || _pointPut == null) return;
        var after = _pointGet();
        var before = _pointBefore;
        var put = _pointPut;
        _pointDrag = null;
        _pointGet = null;
        _pointPut = null;
        if (after == before) return;
        History.Push(new ActionEdit(_pointWhat, () => put(after), () => put(before)));
    }
    private PointerOwner _owner;
    private bool _eyedropSecondary;

    private static bool Inside(Document d, int x, int y) => x >= 0 && y >= 0 && x < d.Width && y < d.Height;

    /// <summary>
    /// A press on the canvas at <paramref name="p"/>, in document pixels (fractional; the pixel
    /// is its floor). Strokes may start off the canvas (a line can); fills and eyedrops can't.
    /// </summary>
    public void PointerDown(Vector2 p, PointerButton button, Mods mods)
    {
        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
        if (Momentary == Momentary.Pan) { _panning = true; _owner = PointerOwner.Pan; return; }
        // the pen's eraser end (or the eraser hold key) erases, whatever tool is chosen
        var tool = Momentary == Momentary.Eraser ? FindTool("Eraser") : _tool;

        if (Momentary == Momentary.Eyedropper || tool is EyedropperTool)
        {
            _owner = PointerOwner.Eyedrop;
            _eyedropSecondary = button == PointerButton.Secondary;
            if (Inside(Document, x, y)) Eyedrop(x, y, _eyedropSecondary);
            return;
        }

        if (tool is PointTool)
        {
            PointDown(x, y, button);
            return;
        }

        if (tool is ISelectionTool)
        {
            _owner = PointerOwner.Selection;
            Playing = false;
            Selection.PointerDown(p, mods, tool);
            return;
        }

        if (!Inside(Document, x, y) && tool is FillTool) return;
        Settle();
        Playing = false;
        if (AutoSelectLayer && !tool.Allows(CurrentLayer.Role)) SelectLayerFor(tool);
        var clip = Selection.Selection.IsEmpty ? null : Selection.Selection.Mask;
        var channel = ActiveChannel;
        byte paint = channel != null
            ? (button == PointerButton.Secondary ? (byte)0 : ChannelBrush)
            : button == PointerButton.Secondary ? Secondary : Primary;
        var why = _stroke.Begin(tool, Document, CurrentFrame, CurrentLayer,
            paint, ToolSettings, x, y, mods.HasFlag(Mods.Shift), clip, channel);
        if (why != null) SetStatus(why);
        else _owner = PointerOwner.Stroke;
    }

    public void PointerMove(Vector2 p, Mods mods)
    {
        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
        switch (_owner)
        {
            case PointerOwner.Stroke: _stroke.Drag(x, y, mods.HasFlag(Mods.Shift)); break;
            case PointerOwner.Point: PointMove(x, y); break;
            case PointerOwner.Eyedrop: if (Inside(Document, x, y)) Eyedrop(x, y, _eyedropSecondary); break;
            case PointerOwner.Selection: Selection.PointerMove(p, mods); break;
        }
    }

    /// <summary>Screen-space mouse movement while a button is down; pans when panning.</summary>
    public void PointerDrag(Vector2 screenDelta, bool anyButtonDown)
    {
        if (Momentary == Momentary.Pan && anyButtonDown) View.Pan += screenDelta;
    }

    public void PointerUp()
    {
        switch (_owner)
        {
            case PointerOwner.Stroke: _stroke.End(History); break;
            case PointerOwner.Point: PointUp(); break;
            case PointerOwner.Selection: Selection.PointerUp(); break;
        }
        _owner = PointerOwner.None;
        _panning = false;
    }

    /// <summary>Picks the palette index from the topmost visible layer that has one at (x, y).</summary>
    public void Eyedrop(int x, int y, bool secondary)
    {
        for (int i = Document.Layers.Count - 1; i >= 0; i--)
        {
            var l = Document.Layers[i];
            if (!l.Visible || !Document.TryGetCel(l, CurrentFrame, out var cel)) continue;
            byte idx = cel.Get(x, y);
            if (idx == Palette.Transparent) continue;
            if (ActiveChannel is { } ch)
            {
                // picks the data there instead of the color
                byte v = Document.DrawingAt(l, CurrentFrame)!.Channels.TryGetValue(ch.Id, out var data) ? data.Get(x, y) : (byte)0;
                if (ch.Kind == ChannelKind.Normal) NormalBrush = v; else ValueBrush = v;
                return;
            }
            if (secondary) Secondary = idx; else Primary = idx;
            return;
        }
    }

    public void CancelStroke() => _stroke.Cancel();
}
