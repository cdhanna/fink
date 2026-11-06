namespace Fink.Core;

/// <summary>What a newly inserted frame shows on the current layer.</summary>
public enum NewFrameKind
{
    /// <summary>Every layer holds what it had on the frame before.</summary>
    Hold,
    /// <summary>The current layer gets a new blank drawing; the others hold.</summary>
    Blank,
    /// <summary>The current layer gets a copy of its drawing; the others hold.</summary>
    Duplicate,
}

/// <summary>
/// Structural edits to a document, each recorded in the history so they undo. They all work the
/// same way: snapshot the structure, change it, snapshot again.
/// </summary>
public static class DocumentOps
{
    private static void Change(Document doc, History history, string name, Action mutate)
    {
        var before = doc.Capture();
        mutate();
        var after = doc.Capture();
        history.Push(new ActionEdit(name, redo: () => doc.Restore(after), undo: () => doc.Restore(before)));
    }

    // ------------------------------------------------------------------ frames

    /// <summary>Inserts a frame at <paramref name="index"/>. Returns its index.</summary>
    public static int InsertFrame(Document doc, History history, int index, NewFrameKind kind, Layer? current)
    {
        index = Math.Clamp(index, 0, doc.Frames.Count);
        var frame = new Frame();
        string name = kind switch { NewFrameKind.Blank => "New frame", NewFrameKind.Duplicate => "Duplicate frame", _ => "Insert held frame" };
        Change(doc, history, name, () =>
        {
            doc.Frames.Insert(index, frame);
            Frame? prev = index > 0 ? doc.Frames[index - 1] : null;
            foreach (var layer in doc.Layers.Where(l => !doc.IsFollower(l)))
                doc.SetExposure(layer, frame, prev == null ? null : doc.ExposureOf(layer, prev));
            if (kind != NewFrameKind.Hold && current != null)
            {
                var timing = doc.TimingLayer(current);
                var d = MakeDrawing(doc, timing, kind == NewFrameKind.Duplicate ? prev : null);
                doc.SetExposure(timing, frame, d);
            }
        });
        return index;
    }

    public static void DeleteFrame(Document doc, History history, int index)
    {
        if (doc.Frames.Count <= 1 || index < 0 || index >= doc.Frames.Count) return;
        Change(doc, history, "Delete frame", () =>
        {
            var frame = doc.Frames[index];
            foreach (var layer in doc.Layers) doc.SetExposure(layer, frame, null);
            // tags that started or ended on it move inward; a one-frame tag goes with it
            for (int i = doc.Tags.Count - 1; i >= 0; i--)
            {
                var t = doc.Tags[i];
                var (a, b) = doc.TagRange(t);
                if (a == b && a == index) { doc.Tags.RemoveAt(i); continue; }
                if (t.From == frame) t = t with { From = doc.Frames[a == index ? a + 1 : a] };
                if (t.To == frame) t = t with { To = doc.Frames[b == index ? b - 1 : b] };
                doc.Tags[i] = t;
            }
            doc.Frames.RemoveAt(index);
        });
    }

    public static void MoveFrame(Document doc, History history, int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= doc.Frames.Count || to >= doc.Frames.Count) return;
        Change(doc, history, "Move frame", () => Move(doc.Frames, from, to));
    }

    /// <summary>Sets (or, with null, removes) the asset's 9-slice insets. Kept inside the canvas.</summary>
    public static bool SetSlice(Document doc, History history, NineSlice? slice)
    {
        slice = slice?.Clamp(doc.Width, doc.Height);
        if (slice == doc.Slice) return false;
        Change(doc, history, slice == null ? "Remove 9-slice" : doc.Slice == null ? "Add 9-slice" : $"9-slice {slice}", () => doc.Slice = slice);
        return true;
    }

    public static void SetFps(Document doc, History history, int fps)
    {
        fps = Math.Clamp(fps, 1, 120);
        if (fps == doc.Fps) return;
        Change(doc, history, $"Frame rate {fps} fps", () => doc.Fps = fps);
    }

    // ------------------------------------------------------------------ drawings and exposures

    /// <summary>
    /// Puts a new drawing (blank, or a copy of the current one) on the current frame and the rest
    /// of the hold it is in. Returns false when there is nothing to copy.
    /// </summary>
    public static bool NewDrawing(Document doc, History history, Layer layer, int index, bool duplicate)
    {
        var timing = doc.TimingLayer(layer);
        var frame = doc.Frames[index];
        if (duplicate && doc.ExposureOf(timing, frame) == null) return false;
        var (_, end) = doc.ExposureOf(timing, frame) == null ? (index, index) : doc.RunAt(timing, index);
        Change(doc, history, duplicate ? "Duplicate drawing" : "New drawing", () =>
        {
            var d = MakeDrawing(doc, timing, duplicate ? frame : null);
            for (int i = index; i <= end; i++) doc.SetExposure(timing, doc.Frames[i], d);
        });
        return true;
    }

    /// <summary>
    /// A new blank drawing on one empty cell (the timeline's "+"). Past the last frame, frames are
    /// added up to it first, every layer holding what it showed at the end. Returns false when
    /// the cell already shows something.
    /// </summary>
    public static bool NewDrawingAt(Document doc, History history, Layer layer, int index)
    {
        if (index < 0) return false;
        var timing = doc.TimingLayer(layer);
        if (index < doc.Frames.Count && doc.ExposureOf(timing, doc.Frames[index]) != null) return false;
        Change(doc, history, index >= doc.Frames.Count ? "New frame" : "New drawing", () =>
        {
            while (doc.Frames.Count <= index)
            {
                var prev = doc.Frames[^1];
                var frame = new Frame();
                doc.Frames.Add(frame);
                foreach (var l in doc.Layers.Where(l => !doc.IsFollower(l)))
                    doc.SetExposure(l, frame, doc.ExposureOf(l, prev));
            }
            doc.SetExposure(timing, doc.Frames[index], MakeDrawing(doc, timing, null));
        });
        return true;
    }

    /// <summary>Shows drawing <paramref name="number"/> (or nothing) on one cell. Reusing numbers makes cycles.</summary>
    public static void SetCell(Document doc, History history, Layer layer, int index, int? number)
    {
        var timing = doc.TimingLayer(layer);
        var d = number is { } n ? timing.DrawingNumbered(n) ?? throw new ArgumentException($"{timing.Name} has no drawing #{n}") : null;
        var frame = doc.Frames[index];
        if (doc.ExposureOf(timing, frame) == d) return;
        Change(doc, history, d == null ? "Clear cell" : $"Expose #{d.Number}", () => doc.SetExposure(timing, frame, d));
    }

    /// <summary>
    /// Makes the hold that starts at <paramref name="start"/> end at <paramref name="end"/>,
    /// covering the cells after it or leaving them empty (dragging a block's edge).
    /// </summary>
    public static void SetHoldEnd(Document doc, History history, Layer layer, int start, int end)
    {
        var (s, e) = doc.RunAt(doc.TimingLayer(layer), start);
        PlaceHold(doc, history, layer, s, e, s, Math.Max(s, end), copy: false);
    }

    /// <summary>
    /// Moves (or copies) the hold on frames <paramref name="from"/>..<paramref name="to"/> so it
    /// covers <paramref name="newFrom"/>..<paramref name="newTo"/> instead: dragging a block in the
    /// dope sheet, by its middle or either end. Cells it lands on are covered; cells it leaves
    /// are emptied. Going past the last frame adds frames, where the other layers hold.
    /// </summary>
    public static bool PlaceHold(Document doc, History history, Layer layer, int from, int to, int newFrom, int newTo, bool copy)
    {
        var timing = doc.TimingLayer(layer);
        if (from < 0 || to >= doc.Frames.Count || from > to) return false;
        var d = doc.ExposureOf(timing, doc.Frames[from]);
        if (d == null) return false;
        newFrom = Math.Max(0, newFrom);
        newTo = Math.Max(newFrom, newTo);
        if (newFrom == from && newTo == to && !copy) return false;

        string name = copy ? $"Copy #{d.Number}"
            : newTo - newFrom != to - from ? $"Hold #{d.Number} for {newTo - newFrom + 1}"
            : $"Move #{d.Number}";
        Change(doc, history, name, () =>
        {
            while (doc.Frames.Count <= newTo)
            {
                var last = doc.Frames[^1];
                var frame = new Frame();
                doc.Frames.Add(frame);
                foreach (var l in doc.Layers.Where(l => !doc.IsFollower(l) && l != timing))
                    doc.SetExposure(l, frame, doc.ExposureOf(l, last));
            }
            if (!copy)
                for (int i = from; i <= to; i++) doc.SetExposure(timing, doc.Frames[i], null);
            for (int i = newFrom; i <= newTo; i++) doc.SetExposure(timing, doc.Frames[i], d);
        });
        return true;
    }

    /// <summary>
    /// Moves (or copies) a rectangle of cells, frames <paramref name="from"/>..<paramref name="to"/>
    /// on several layers, so it starts at <paramref name="newFrom"/> (a timeline selection dragged
    /// sideways). The block goes as it is, empty cells included: what it lands on is replaced, what
    /// it leaves is emptied. Followers move with their timing layer. Past the last frame, frames
    /// are added, where the other layers hold.
    /// </summary>
    public static bool MoveCells(Document doc, History history, IEnumerable<Layer> layers, int from, int to, int newFrom, bool copy)
    {
        var timings = layers.Where(doc.Layers.Contains).Select(doc.TimingLayer).Distinct().ToList();
        if (timings.Count == 0 || from < 0 || to >= doc.Frames.Count || from > to) return false;
        newFrom = Math.Max(0, newFrom);
        if (newFrom == from && !copy) return false;
        int len = to - from + 1;
        string what = len == 1 ? "1 frame" : $"{len} frames";
        Change(doc, history, copy ? $"Copy {what}" : $"Move {what}", () =>
        {
            var grabbed = timings.ToDictionary(l => l, l => Enumerable.Range(from, len).Select(i => doc.ExposureOf(l, doc.Frames[i])).ToList());
            GrowTo(doc, newFrom + len - 1, keep: timings);
            if (!copy)
                foreach (var l in timings)
                    for (int i = from; i <= to; i++) doc.SetExposure(l, doc.Frames[i], null);
            foreach (var l in timings)
                for (int k = 0; k < len; k++) doc.SetExposure(l, doc.Frames[newFrom + k], grabbed[l][k]);
        });
        return true;
    }

    /// <summary>Adds frames until <paramref name="last"/> exists; own-timing layers (but <paramref name="keep"/>) hold their last cell.</summary>
    private static void GrowTo(Document doc, int last, IReadOnlyCollection<Layer>? keep = null)
    {
        while (doc.Frames.Count <= last)
        {
            var prev = doc.Frames[^1];
            var frame = new Frame();
            doc.Frames.Add(frame);
            foreach (var l in doc.Layers.Where(l => !doc.IsFollower(l) && (keep == null || !keep.Contains(l))))
                doc.SetExposure(l, frame, doc.ExposureOf(l, prev));
        }
    }

    /// <summary>
    /// Holds the drawing on <paramref name="index"/> one frame longer (+1) or shorter (-1),
    /// sliding the rest of this layer's timing along. Other layers keep theirs. Holding longer
    /// adds a frame at the end when the layer has no empty cell there to absorb it.
    /// </summary>
    public static bool Retime(Document doc, History history, Layer layer, int index, int delta)
    {
        var timing = doc.TimingLayer(layer);
        var cells = doc.Frames.Select(f => doc.ExposureOf(timing, f)).ToList();
        var d = cells[index];
        if (d == null) return false;
        var (start, end) = doc.RunAt(timing, index);
        if (delta < 0 && start == end) return false;

        Change(doc, history, delta > 0 ? $"Hold #{d.Number} longer" : $"Hold #{d.Number} shorter", () =>
        {
            if (delta > 0)
            {
                cells.Insert(end + 1, d);
                if (cells[^1] == null) cells.RemoveAt(cells.Count - 1);
                else
                {
                    // grow the timeline; everyone else holds their last cell
                    var last = doc.Frames[^1];
                    var frame = new Frame();
                    doc.Frames.Add(frame);
                    foreach (var l in doc.Layers.Where(l => !doc.IsFollower(l)))
                        doc.SetExposure(l, frame, doc.ExposureOf(l, last));
                }
            }
            else
            {
                cells.RemoveAt(end);
                cells.Add(null);
            }
            for (int i = 0; i < doc.Frames.Count; i++) doc.SetExposure(timing, doc.Frames[i], cells[i]);
        });
        return true;
    }

    /// <summary>
    /// A new drawing on an own-timing layer, blank or copied from what it shows on
    /// <paramref name="copyFrom"/>. When copying, its followers' drawings are copied too, so a
    /// duplicated line drawing keeps its fill.
    /// </summary>
    private static Drawing MakeDrawing(Document doc, Layer timing, Frame? copyFrom)
    {
        var followers = doc.FollowersOf(timing).ToList();
        int number = followers.Select(f => f.NextNumber()).Append(timing.NextNumber()).Max();
        var source = copyFrom == null ? null : doc.ExposureOf(timing, copyFrom);
        var d = source?.CloneAs(number) ?? new Drawing(number, new Cel(doc.Width, doc.Height));
        timing.Drawings.Add(d);
        if (source != null)
            foreach (var f in followers)
                if (f.DrawingNumbered(source.Number) is { } fd)
                    f.Drawings.Add(fd.CloneAs(number));
        return d;
    }

    // ------------------------------------------------------------------ tags

    public static Tag AddTag(Document doc, History history, string name, int from, int to, LoopMode loop = LoopMode.Forward)
    {
        (from, to) = (Math.Clamp(Math.Min(from, to), 0, doc.Frames.Count - 1), Math.Clamp(Math.Max(from, to), 0, doc.Frames.Count - 1));
        var tag = new Tag(name, doc.Frames[from], doc.Frames[to], loop, doc.Owner?.NextId() ?? 0);
        Change(doc, history, $"Add tag \"{name}\"", () => doc.Tags.Add(tag));
        return tag;
    }

    /// <summary>
    /// Renames, re-ranges (moves, stretches) or changes the loop of a tag. Returns the new tag.
    /// Stretching it past the last frame adds frames, where every layer holds.
    /// </summary>
    public static Tag UpdateTag(Document doc, History history, Tag tag, string? name = null, int? from = null, int? to = null, LoopMode? loop = null)
    {
        int i = doc.Tags.IndexOf(tag);
        if (i < 0) return tag;
        var (a, b) = doc.TagRange(tag);
        int na = Math.Max(0, from ?? a), nb = Math.Max(0, to ?? b);
        if (na > nb) (na, nb) = (nb, na);
        na = Math.Min(na, doc.Frames.Count - 1);
        bool grows = nb >= doc.Frames.Count;
        if (!grows)
        {
            var same = tag with { Name = name ?? tag.Name, From = doc.Frames[na], To = doc.Frames[nb], Loop = loop ?? tag.Loop };
            if (same == tag) return tag;
        }
        string what = name != null && name != tag.Name ? $"Rename tag \"{tag.Name}\" to \"{name}\""
            : loop != null && loop != tag.Loop ? $"Tag \"{tag.Name}\": {loop}"
            : $"Tag \"{tag.Name}\" frames {na + 1}-{nb + 1}";
        Tag updated = tag;
        Change(doc, history, what, () =>
        {
            GrowTo(doc, nb);
            updated = tag with { Name = name ?? tag.Name, From = doc.Frames[na], To = doc.Frames[nb], Loop = loop ?? tag.Loop };
            doc.Tags[i] = updated;
        });
        return updated;
    }

    public static void RemoveTag(Document doc, History history, Tag tag)
    {
        if (!doc.Tags.Contains(tag)) return;
        Change(doc, history, $"Delete tag \"{tag.Name}\"", () => doc.Tags.Remove(tag));
    }

    // ------------------------------------------------------------------ points

    /// <summary>A new named point (or the pivot), riding on <paramref name="layer"/>'s drawings.</summary>
    public static PointTrack AddPoint(Document doc, History history, string name, Layer layer, bool pivot = false, bool keyed = false)
    {
        var timing = doc.TimingLayer(layer);
        var track = new PointTrack(doc.Owner?.NextId() ?? 0, pivot ? "pivot" : name, pivot, timing, Keyed: keyed && !pivot);
        Change(doc, history, pivot ? "Add pivot" : $"Add point \"{name}\"", () =>
        {
            if (pivot) doc.Points.Insert(0, track); else doc.Points.Add(track);
        });
        return track;
    }

    public static PointTrack RenamePoint(Document doc, History history, PointTrack track, string name)
    {
        int i = doc.Points.IndexOf(track);
        name = name.Trim();
        if (i < 0 || track.IsPivot || name.Length == 0 || name == track.Name) return track;
        var renamed = track with { Name = name };
        Change(doc, history, $"Rename point \"{track.Name}\" to \"{name}\"", () => doc.Points[i] = renamed);
        return renamed;
    }

    /// <summary>Removes a point track. Drawings keep their values, so undo brings it back whole.</summary>
    public static void RemovePoint(Document doc, History history, PointTrack track)
    {
        if (!doc.Points.Contains(track)) return;
        Change(doc, history, $"Delete point \"{track.Name}\"", () => doc.Points.Remove(track));
    }

    /// <summary>
    /// Puts a point on the drawing shown at a frame (or clears it there, with null). Returns false
    /// when the point's layer shows no drawing on that frame.
    /// </summary>
    public static bool SetPoint(Document doc, History history, PointTrack track, int frameIndex, PointXY? at)
    {
        if (track.Keyed)
        {
            var keys = doc.KeysOf(track);
            var frame = doc.Frames[frameIndex];
            PointXY? had = keys.TryGetValue(frame, out var h) ? h : null;
            if (had == at) return true;
            void PutKey(PointXY? v) { if (v is { } p) doc.KeysOf(track)[frame] = p; else doc.KeysOf(track).Remove(frame); }
            history.Do(new ActionEdit(at == null ? $"Delete {track.Name} key" : $"Key {track.Name} on frame {frameIndex + 1}",
                redo: () => PutKey(at), undo: () => PutKey(had)));
            return true;
        }
        var drawing = doc.DrawingAt(track.Layer, doc.Frames[frameIndex]);
        if (drawing == null) return false;
        PointXY? before = drawing.Points.TryGetValue(track.Id, out var b) ? b : null;
        if (before == at) return true;
        void Put(PointXY? v) { if (v is { } p) drawing.Points[track.Id] = p; else drawing.Points.Remove(track.Id); }
        var name = at == null ? $"Clear {track.Name} on #{drawing.Number}" : $"Move {track.Name} on #{drawing.Number}";
        history.Do(new ActionEdit(name, redo: () => Put(at), undo: () => Put(before)));
        return true;
    }

    /// <summary>Moves a keyed point's key to another frame (replacing any key there).</summary>
    public static bool MoveKey(Document doc, History history, PointTrack track, int from, int to)
    {
        if (!track.Keyed || from == to || to < 0 || to >= doc.Frames.Count) return false;
        if (!doc.KeysOf(track).ContainsKey(doc.Frames[from])) return false;
        Change(doc, history, $"Move {track.Name} key to frame {to + 1}", () =>
        {
            var keys = doc.KeysOf(track);
            var v = keys[doc.Frames[from]];
            keys.Remove(doc.Frames[from]);
            keys[doc.Frames[to]] = v;
        });
        return true;
    }

    /// <summary>Held between keys, or a straight line.</summary>
    public static PointTrack SetSmooth(Document doc, History history, PointTrack track, bool smooth)
    {
        int i = doc.Points.IndexOf(track);
        if (i < 0 || !track.Keyed || track.Smooth == smooth) return track;
        var changed = track with { Smooth = smooth };
        Change(doc, history, $"{track.Name}: {(smooth ? "smooth" : "held")} between keys", () => doc.Points[i] = changed);
        return changed;
    }

    // ------------------------------------------------------------------ layers

    public static Layer AddLayer(Document doc, History history, int index, string name, LayerRole role, Layer? follows = null)
    {
        index = Math.Clamp(index, 0, doc.Layers.Count);
        var layer = new Layer(name, role) { Follows = follows };
        Change(doc, history, $"Add {role} layer", () => doc.Layers.Insert(index, layer));
        return layer;
    }

    public static void DeleteLayer(Document doc, History history, int index)
    {
        if (doc.Layers.Count <= 1 || index < 0 || index >= doc.Layers.Count) return;
        Change(doc, history, "Delete layer", () =>
        {
            var layer = doc.Layers[index];
            // layers following this one keep the timing they had
            foreach (var f in doc.FollowersOf(layer).ToList()) Unfollow(doc, f);
            doc.Layers.RemoveAt(index);
        });
    }

    /// <summary>
    /// Which Line layers a Fill layer's fills stop at (null: every visible one, the default).
    /// Only Line layers of this document are kept.
    /// </summary>
    public static void SetFillBounds(Document doc, History history, Layer fill, IEnumerable<Layer>? lines)
    {
        var list = lines?.Where(l => l.Role == LayerRole.Line && doc.Layers.Contains(l)).Distinct().ToList();
        bool same = list == null ? fill.FillBounds == null
            : fill.FillBounds != null && fill.FillBounds.Count == list.Count && list.All(fill.FillBounds.Contains);
        if (same) return;
        string what = list == null ? "every visible line layer" : list.Count == 0 ? "nothing" : string.Join(", ", list.Select(l => l.Name));
        Change(doc, history, $"{fill.Name} fills stop at {what}", () => fill.FillBounds = list);
    }

    /// <summary>
    /// Renames a layer. Names must be unique in the asset (ignoring case): paste and scripts find
    /// layers by name. Returns why it can't, or null when done (or nothing changed).
    /// </summary>
    public static string? RenameLayer(Document doc, History history, Layer layer, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "A layer needs a name.";
        if (name == layer.Name) return null;
        if (doc.Layers.Any(l => l != layer && l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return $"There's already a layer called \"{name}\".";
        var old = layer.Name;
        var edit = new ActionEdit($"Rename layer \"{old}\" to \"{name}\"",redo: () => layer.Name = name, undo: () => layer.Name = old);
        edit.Redo();
        history.Push(edit);
        return null;
    }

    public static void MoveLayer(Document doc, History history, int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= doc.Layers.Count || to >= doc.Layers.Count) return;
        Change(doc, history, "Move layer", () => Move(doc.Layers, from, to));
    }

    /// <summary>
    /// Makes <paramref name="layer"/> follow <paramref name="source"/>'s timing, or gives it its
    /// own (keeping what it currently shows) when source is null. Returns why not, or null.
    /// </summary>
    public static string? SetFollows(Document doc, History history, Layer layer, Layer? source)
    {
        if (source != null)
        {
            if (source == layer) return "A layer can't follow itself.";
            if (doc.IsFollower(source)) return $"\"{source.Name}\" follows another layer; follow that one.";
            if (doc.FollowersOf(layer).Any()) return $"Other layers follow \"{layer.Name}\".";
        }
        if (doc.TimingLayer(layer) == (source ?? layer)) return null;
        Change(doc, history, source == null ? $"{layer.Name}: own timing" : $"{layer.Name} follows {source.Name}", () =>
        {
            if (source == null) { Unfollow(doc, layer); return; }
            foreach (var f in doc.Frames) doc.SetExposure(layer, f, null);
            layer.Follows = source;
        });
        return null;
    }

    /// <summary>Turns following into the same exposures of the layer's own.</summary>
    private static void Unfollow(Document doc, Layer layer)
    {
        var shown = doc.Frames.Select(f => doc.DrawingAt(layer, f)).ToList();
        layer.Follows = null;
        for (int i = 0; i < doc.Frames.Count; i++) doc.SetExposure(layer, doc.Frames[i], shown[i]);
    }

    private static void Move<T>(List<T> list, int from, int to)
    {
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
    }
}
