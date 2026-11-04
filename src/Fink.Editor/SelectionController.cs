using System.Numerics;
using Fink.Core;

namespace Fink.Editor;

/// <summary>Marks the tools the selection controller handles.</summary>
public interface ISelectionTool { }

public sealed class SelectRectTool : Tool, ISelectionTool
{
    public override string Name => "Select";
    public override bool Allows(LayerRole role) => true;
}

public sealed class LassoTool : Tool, ISelectionTool
{
    public override string Name => "Lasso";
    public override bool Allows(LayerRole role) => true;
}

public sealed class WandTool : Tool, ISelectionTool
{
    public override string Name => "Wand";
    public override bool Allows(LayerRole role) => true;
}

/// <summary>Shows scale/rotate handles on the selection (or the whole layer).</summary>
public sealed class TransformTool : Tool, ISelectionTool
{
    public override string Name => "Transform";
    public override bool Allows(LayerRole role) => true;
}

public enum Handle
{
    None, Move, Rotate,
    TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left,
}

/// <summary>
/// Selections and what you do with them: making them (rectangle, lasso, wand; Shift adds, Alt
/// subtracts, both intersect), picking the pixels up and moving/scaling/rotating/flipping them,
/// copy/cut/paste/clear. Floating pixels are stamped back as one undo step on commit.
/// </summary>
public sealed class SelectionController
{
    private readonly EditorState _ed;

    public Selection Selection { get; private set; }
    public FloatingSelection? Floating { get; private set; }
    public ClipboardContent? Clipboard { get; private set; }

    /// <summary>Lift, move and copy every visible, unlocked line/fill layer, not just the current one.</summary>
    public bool AllLayers { get; set; }
    /// <summary>
    /// Selections on a layer take its linked layers along: a line layer and the fill that
    /// follows its timing move, transform, copy and clear together.
    /// </summary>
    public bool WithLinked { get; set; } = true;

    /// <summary>The layer and the ones linked to it by timing (its source, and everything following that), source first.</summary>
    public List<Layer> LinkedLayers(Layer layer)
    {
        var doc = _ed.Document;
        var timing = doc.TimingLayer(layer);
        return new[] { timing }.Concat(doc.FollowersOf(timing)).ToList();
    }
    public bool WandContiguous { get; set; } = true;

    // the drag in progress
    private enum DragKind { None, Rect, Lasso, Handle }
    private DragKind _drag;
    private Vector2 _dragStart;
    private Vector2 _dragNow;
    private readonly List<Vector2> _lasso = new();
    private SelectMode _dragMode;
    private Handle _handle;
    private TransformParams _startTransform;
    private Vector2 _anchorWorld;
    private bool _moved;

    public SelectionController(EditorState ed)
    {
        _ed = ed;
        Selection = new Selection(ed.Document.Width, ed.Document.Height);
    }

    public void Reset()
    {
        Floating = null;
        _drag = DragKind.None;
        Selection = new Selection(_ed.Document.Width, _ed.Document.Height);
    }

    public bool HasSelection => Floating != null || !Selection.IsEmpty;

    // ------------------------------------------------------------------ display

    /// <summary>The rectangle being dragged out, in document pixels (inclusive corners).</summary>
    public (Vector2 a, Vector2 b)? RectPreview =>
        _drag == DragKind.Rect ? (Floor(_dragStart), Floor(_dragNow)) : null;

    public IReadOnlyList<Vector2>? LassoPreview => _drag == DragKind.Lasso ? _lasso : null;

    private int _overlayVersion = -1;
    private FloatingSelection? _overlayFor;
    private Dictionary<Layer, byte[]>? _overlays;
    private bool[]? _floatMask;

    /// <summary>The floating pixels per layer, document-sized, for the compositor.</summary>
    public Dictionary<Layer, byte[]>? Overlays()
    {
        if (Floating == null) return null;
        RefreshFloatCache();
        return _overlays;
    }

    /// <summary>What the marching ants outline: the floating pixels, or the selection.</summary>
    public bool[] DisplayMask()
    {
        if (Floating == null) return Selection.Mask;
        RefreshFloatCache();
        return _floatMask!;
    }

    /// <summary>Changes whenever <see cref="DisplayMask"/> does.</summary>
    public int DisplayVersion => HashCode.Combine(Selection, Selection.Version, Floating, Floating?.Version ?? -1);

    private void RefreshFloatCache()
    {
        if (_overlayFor == Floating && _overlayVersion == Floating!.Version) return;
        _overlays = Floating!.Rasterize(out var mask);
        _floatMask = mask;
        _overlayFor = Floating;
        _overlayVersion = Floating.Version;
    }

    // ------------------------------------------------------------------ targets

    /// <summary>The layers a lift/copy/clear acts on, or why there are none.</summary>
    public List<Layer> TargetLayers(out string? why)
    {
        why = null;
        if (AllLayers)
        {
            var all = _ed.Document.Layers.Where(Editable).ToList();
            if (all.Count == 0) why = "No visible, unlocked line or fill layers.";
            return all;
        }
        var layer = _ed.CurrentLayer;
        if (Editable(layer))
        {
            if (!WithLinked) return new List<Layer> { layer };
            // the current layer, plus its linked ones that can be edited and have something
            // inside the selection (or anything at all, with nothing selected); drawing sources first
            return LinkedLayers(layer).Where(l => l == layer || (Editable(l) && HasPixelsInSelection(l))).ToList();
        }
        why = layer.Role == LayerRole.Reference ? "Reference layers can't be edited." : $"\"{layer.Name}\" is locked or hidden.";
        return new List<Layer>();
    }

    private bool HasPixelsInSelection(Layer layer)
    {
        if (!_ed.Document.TryGetCel(layer, _ed.CurrentFrame, out var cel)) return false;
        var mask = Selection.IsEmpty ? null : Selection.Mask;
        for (int i = 0; i < cel.Pixels.Length; i++)
            if (cel.Pixels[i] != Palette.Transparent && (mask == null || mask[i])) return true;
        return false;
    }

    private static bool Editable(Layer l) => l.Visible && !l.Locked && l.Role != LayerRole.Reference;

    // ------------------------------------------------------------------ pointer

    public void PointerDown(Vector2 p, Mods mods, Tool tool)
    {
        _moved = false;
        if (tool is EyedropperTool) return;

        if (Floating != null)
        {
            var h = HitTest(p, _ed.View.Zoom, tool is TransformTool);
            if (h != Handle.None) { BeginHandle(h, p); return; }
            Commit();
        }

        var mode = ModeFor(mods);
        bool inside = Selection.Contains((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));

        if (tool is TransformTool)
        {
            if (Selection.IsEmpty) SelectLayerContent();
            if (Selection.IsEmpty) return;
            if (!Lift("Transform")) return;
            var h = HitTest(p, _ed.View.Zoom, true);
            if (h != Handle.None) BeginHandle(h, p);
            return;
        }

        // drag inside the selection with no modifier: pick it up and move it
        if (inside && mode == SelectMode.Replace && tool is SelectRectTool or LassoTool or WandTool)
        {
            if (Lift("Move selection")) BeginHandle(Handle.Move, p);
            return;
        }

        _dragMode = mode;
        _dragStart = _dragNow = p;
        switch (tool)
        {
            case SelectRectTool:
                _drag = DragKind.Rect;
                break;
            case LassoTool:
                _drag = DragKind.Lasso;
                _lasso.Clear();
                _lasso.Add(p);
                break;
            case WandTool:
            {
                if (!_ed.Document.TryGetCel(_ed.CurrentLayer, _ed.CurrentFrame, out var cel))
                    cel = new Cel(_ed.Document.Width, _ed.Document.Height);
                Selection.Apply(Selection.WandShape(cel, (int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), WandContiguous), mode);
                break;
            }
        }
    }

    public void PointerMove(Vector2 p, Mods mods)
    {
        if (Vector2.DistanceSquared(p, _dragStart) > 0.25f) _moved = true;
        _dragNow = p;
        switch (_drag)
        {
            case DragKind.Lasso:
                if (Vector2.DistanceSquared(p, _lasso[^1]) >= 0.25f) _lasso.Add(p);
                break;
            case DragKind.Handle:
                DragHandle(p, mods.HasFlag(Mods.Shift));
                break;
        }
    }

    public void PointerUp()
    {
        switch (_drag)
        {
            case DragKind.Rect:
            {
                var a = Floor(_dragStart);
                var b = Floor(_dragNow);
                if (!_moved && _dragMode == SelectMode.Replace) Selection.Clear(); // a click deselects
                else Selection.Apply(Selection.RectShape((int)a.X, (int)a.Y, (int)b.X, (int)b.Y), _dragMode);
                break;
            }
            case DragKind.Lasso:
                if (!_moved && _dragMode == SelectMode.Replace) Selection.Clear();
                else Selection.Apply(Selection.PolygonShape(_lasso), _dragMode);
                break;
        }
        _drag = DragKind.None;
    }

    private static SelectMode ModeFor(Mods mods) =>
        (mods.HasFlag(Mods.Shift), mods.HasFlag(Mods.Alt)) switch
        {
            (true, true) => SelectMode.Intersect,
            (true, false) => SelectMode.Add,
            (false, true) => SelectMode.Subtract,
            _ => SelectMode.Replace,
        };

    private static Vector2 Floor(Vector2 v) => new(MathF.Floor(v.X), MathF.Floor(v.Y));

    // ------------------------------------------------------------------ handles

    /// <summary>Which part of the floating box is at <paramref name="p"/> (document pixels).</summary>
    public Handle HitTest(Vector2 p, int zoom, bool withHandles)
    {
        if (Floating == null) return Handle.None;
        var c = Floating.Corners();
        float r = 7f / zoom; // handle radius: 7 screen pixels
        if (withHandles)
        {
            var handles = HandlePositions(c);
            foreach (var (h, pos) in handles)
                if (Vector2.Distance(p, pos) <= r) return h;
        }
        if (InsideQuad(p, c)) return Handle.Move;
        if (withHandles)
            foreach (var corner in c)
                if (Vector2.Distance(p, corner) <= r * 4) return Handle.Rotate;
        return Handle.None;
    }

    public static List<(Handle, Vector2)> HandlePositions(Vector2[] c) => new()
    {
        (Handle.TopLeft, c[0]), (Handle.Top, (c[0] + c[1]) / 2), (Handle.TopRight, c[1]), (Handle.Right, (c[1] + c[2]) / 2),
        (Handle.BottomRight, c[2]), (Handle.Bottom, (c[2] + c[3]) / 2), (Handle.BottomLeft, c[3]), (Handle.Left, (c[3] + c[0]) / 2),
    };

    private static bool InsideQuad(Vector2 p, Vector2[] c)
    {
        // works for either winding (flips make it clockwise or counter-clockwise)
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = c[i];
            var b = c[(i + 1) % 4];
            float cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            int s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    private static (int dx, int dy) DirOf(Handle h) => h switch
    {
        Handle.TopLeft => (-1, -1), Handle.Top => (0, -1), Handle.TopRight => (1, -1), Handle.Right => (1, 0),
        Handle.BottomRight => (1, 1), Handle.Bottom => (0, 1), Handle.BottomLeft => (-1, 1), Handle.Left => (-1, 0),
        _ => (0, 0),
    };

    private void BeginHandle(Handle h, Vector2 p)
    {
        _drag = DragKind.Handle;
        _handle = h;
        _dragStart = _dragNow = p;
        _startTransform = Floating!.Transform;
        var (dx, dy) = DirOf(h);
        // the opposite handle stays put while scaling
        var anchorLocal = new Vector2((1 - dx) / 2f * Floating.SrcW, (1 - dy) / 2f * Floating.SrcH);
        _anchorWorld = Floating.Matrix.Apply(anchorLocal);
    }

    private void DragHandle(Vector2 p, bool shift)
    {
        var f = Floating!;
        var t = _startTransform;
        switch (_handle)
        {
            case Handle.Move:
            {
                var d = p - _dragStart;
                t.Center = _startTransform.Center + new Vector2(MathF.Round(d.X), MathF.Round(d.Y));
                f.SetTransform(t);
                return;
            }
            case Handle.Rotate:
            {
                float a0 = MathF.Atan2(_dragStart.Y - t.Center.Y, _dragStart.X - t.Center.X);
                float a1 = MathF.Atan2(p.Y - t.Center.Y, p.X - t.Center.X);
                float angle = _startTransform.Angle + (a1 - a0) * 180f / MathF.PI;
                angle = shift ? MathF.Round(angle / 15f) * 15f : angle;
                t.Angle = Normalize(angle);
                f.SetTransform(t);
                f.SnapToGrid();
                return;
            }
        }

        // scale: work in the box's own frame (rotation and skew removed)
        var (dx, dy) = DirOf(_handle);
        var frame = _startTransform.Frame();
        var inv = frame.Inverse();
        var v = inv.Apply(p - _anchorWorld);
        float sx = _startTransform.ScaleX, sy = _startTransform.ScaleY;
        if (dx != 0) sx = v.X / (dx * f.SrcW);
        if (dy != 0) sy = v.Y / (dy * f.SrcH);
        if (shift && dx != 0 && dy != 0)
        {
            float rx = sx / _startTransform.ScaleX, ry = sy / _startTransform.ScaleY;
            float k = MathF.Abs(rx) > MathF.Abs(ry) ? rx : ry;
            sx = _startTransform.ScaleX * k;
            sy = _startTransform.ScaleY * k;
        }
        // whole-pixel sizes, never zero
        sx = RoundScale(sx, f.SrcW);
        sy = RoundScale(sy, f.SrcH);
        t.ScaleX = sx;
        t.ScaleY = sy;
        t.Center = _anchorWorld + frame.Apply(new Vector2(dx * f.SrcW * sx / 2f, dy * f.SrcH * sy / 2f));
        f.SetTransform(t);
    }

    private static float RoundScale(float s, int size)
    {
        float px = MathF.Round(s * size);
        if (MathF.Abs(px) < 1) px = s < 0 ? -1 : 1;
        return px / size;
    }

    private static float Normalize(float angle)
    {
        angle %= 360f;
        return angle > 180 ? angle - 360 : angle <= -180 ? angle + 360 : angle;
    }

    // ------------------------------------------------------------------ lift / commit

    /// <summary>Selects every non-transparent pixel on the target layers.</summary>
    public void SelectLayerContent()
    {
        var mask = new bool[Selection.Mask.Length];
        foreach (var layer in TargetLayers(out _))
            if (_ed.Document.TryGetCel(layer, _ed.CurrentFrame, out var cel))
                for (int i = 0; i < mask.Length; i++)
                    if (cel.Pixels[i] != Palette.Transparent) mask[i] = true;
        Selection.CopyFrom(mask);
    }

    /// <summary>Picks up the selected pixels. Returns false (with a status) when it can't.</summary>
    public bool Lift(string name)
    {
        if (Floating != null) return true;
        if (Selection.IsEmpty) return false;
        var layers = TargetLayers(out var why);
        if (layers.Count == 0) { _ed.SetStatus(why!); return false; }
        var f = FloatingSelection.Lift(name, _ed.Document, _ed.CurrentFrame, layers, Selection);
        if (f.Layers.Count == 0) { _ed.SetStatus("Nothing drawn on this frame to pick up."); return false; }
        Floating = f;
        return true;
    }

    /// <summary>Lifts if needed (the whole layer's content when nothing is selected), for flips and turns.</summary>
    private bool EnsureFloating(string name)
    {
        if (Floating != null) return true;
        if (Selection.IsEmpty) SelectLayerContent();
        return Lift(name);
    }

    public void Commit()
    {
        if (Floating == null) return;
        var f = Floating;
        Floating = null;
        var edit = f.Commit(out var mask);
        if (edit != null) _ed.History.Push(edit);
        Selection.CopyFrom(mask);
    }

    public void Cancel()
    {
        if (Floating == null) return;
        Floating.Cancel();
        Floating = null;
        Selection.Touch();
    }

    // ------------------------------------------------------------------ transforms

    public void Nudge(int dx, int dy)
    {
        if (!EnsureFloatingFromSelection("Move selection")) return;
        var t = Floating!.Transform;
        t.Center += new Vector2(dx, dy);
        Floating.SetTransform(t);
    }

    /// <summary>Nudging only makes sense with a selection; it never lifts a whole layer.</summary>
    private bool EnsureFloatingFromSelection(string name) => Floating != null || (!Selection.IsEmpty && Lift(name));

    public void Flip(bool horizontal)
    {
        if (!EnsureFloating(horizontal ? "Flip horizontal" : "Flip vertical")) return;
        var t = Floating!.Transform;
        // A flip in screen space, F·R(a)·K(k)·S = R(-a)·K(-k)·F·S: the angle and skew mirror,
        // and the scale on the flipped axis negates.
        t.Angle = Normalize(-t.Angle);
        t.Skew = -t.Skew;
        if (horizontal) t.ScaleX = -t.ScaleX; else t.ScaleY = -t.ScaleY;
        Floating.SetTransform(t);
        Floating.SnapToGrid();
    }

    public void Rotate(float degrees)
    {
        if (!EnsureFloating("Rotate")) return;
        var t = Floating!.Transform;
        t.Angle = Normalize(t.Angle + degrees);
        Floating.SetTransform(t);
        Floating.SnapToGrid();
    }

    /// <summary>Sets the transform from the numeric fields.</summary>
    public void SetTransform(TransformParams t)
    {
        if (Floating == null) return;
        Floating.SetTransform(t);
        Floating.SnapToGrid();
    }

    // ------------------------------------------------------------------ clipboard

    public void Copy()
    {
        var layers = Floating?.Layers.Select(l => l.Layer).ToList() ?? TargetLayers(out _);
        if (layers.Count == 0) return;

        bool[] mask;
        Func<Layer, int, byte> pixelAt;
        if (Floating != null)
        {
            var raster = Floating.Rasterize(out mask);
            pixelAt = (layer, i) => raster[layer][i];
        }
        else
        {
            mask = Selection.IsEmpty ? Enumerable.Repeat(true, Selection.Mask.Length).ToArray() : (bool[])Selection.Mask.Clone();
            var frame = _ed.CurrentFrame;
            pixelAt = (layer, i) => _ed.Document.TryGetCel(layer, frame, out var cel) ? cel.Pixels[i] : Palette.Transparent;
        }

        var tmp = new Selection(Selection.Width, Selection.Height);
        tmp.CopyFrom(mask);
        var b = tmp.Bounds();
        if (b.IsEmpty) return;
        var cropMask = new bool[b.W * b.H];
        var content = new List<(string, LayerRole, byte[])>();
        foreach (var layer in layers)
        {
            var px = new byte[b.W * b.H];
            for (int y = 0; y < b.H; y++)
            for (int x = 0; x < b.W; x++)
            {
                int i = (b.Y + y) * Selection.Width + b.X + x;
                if (!mask[i]) continue;
                cropMask[y * b.W + x] = true;
                px[y * b.W + x] = pixelAt(layer, i);
            }
            content.Add((layer.Name, layer.Role, px));
        }
        Clipboard = new ClipboardContent { Source = b, Mask = cropMask, Layers = content };
        _ed.SetStatus(content.Count == 1 ? $"Copied {b.W}x{b.H}" : $"Copied {b.W}x{b.H} from {content.Count} layers", 1.5);
    }

    public void Cut()
    {
        if (Floating == null && Selection.IsEmpty) return;
        Copy();
        Clear("Cut");
    }

    /// <summary>Erases the selected pixels (or drops the floating ones).</summary>
    public void Clear(string name = "Clear")
    {
        if (Floating != null)
        {
            foreach (var fl in Floating.Layers) Array.Clear(fl.Pixels);
            Floating.PixelsChanged();
            Floating.Name = name;
            Commit();
            return;
        }
        if (Selection.IsEmpty) return;
        var edits = new List<IEdit>();
        foreach (var layer in TargetLayers(out var why))
        {
            if (!_ed.Document.TryGetCel(layer, _ed.CurrentFrame, out var cel)) continue;
            var session = new CelEditSession(cel);
            for (int i = 0; i < Selection.Mask.Length; i++)
                if (Selection.Mask[i]) session.Set(i % Selection.Width, i / Selection.Width, Palette.Transparent);
            if (session.Commit(name) is { } e) edits.Add(e);
        }
        if (edits.Count == 1) _ed.History.Push(edits[0]);
        else if (edits.Count > 1) _ed.History.Push(new CompositeEdit(name, edits));
    }

    /// <summary>
    /// When the clipboard holds one layer of each role and the current layer's linked group has
    /// one editable layer of each of those roles, pairs them up by role.
    /// </summary>
    private List<(Layer, byte[])>? RoleMatch()
    {
        if (AllLayers || !WithLinked || Clipboard == null) return null;
        var roles = Clipboard.Layers.Select(l => l.Role).ToList();
        if (roles.Distinct().Count() != roles.Count) return null;
        var group = LinkedLayers(_ed.CurrentLayer).Where(Editable).ToList();
        var result = new List<(Layer, byte[])>();
        foreach (var (_, role, px) in Clipboard.Layers)
        {
            var matches = group.Where(l => l.Role == role).ToList();
            if (matches.Count != 1) return null;
            result.Add((matches[0], px));
        }
        return result;
    }

    /// <summary>
    /// Pastes in place, floating: one copied layer goes onto the current layer; several go onto
    /// the layers with the same names.
    /// </summary>
    public void Paste()
    {
        if (Clipboard == null) return;
        Commit();
        var doc = _ed.Document;
        var targets = new List<(Layer layer, byte[] px)>();
        if (Clipboard.Layers.Count == 1)
        {
            var layer = _ed.CurrentLayer;
            if (!Editable(layer)) { _ed.SetStatus($"Can't paste onto \"{layer.Name}\": it is locked, hidden or a reference layer."); return; }
            targets.Add((layer, Clipboard.Layers[0].Pixels));
        }
        else if (RoleMatch() is { } byRole)
        {
            // a copied line + fill goes onto the current line + fill, whatever they're called
            targets.AddRange(byRole);
        }
        else
        {
            foreach (var (name, _, px) in Clipboard.Layers)
            {
                var layer = doc.Layers.FirstOrDefault(l => l.Name == name && Editable(l));
                if (layer != null) targets.Add((layer, px));
            }
            if (targets.Count == 0) { _ed.SetStatus("No layers here match the copied ones."); return; }
            if (targets.Count < Clipboard.Layers.Count) _ed.SetStatus($"Pasted onto {targets.Count} of {Clipboard.Layers.Count} layers (matched by name).");
        }
        // sources before followers, so the line drawing exists before its fill is made
        targets = targets.OrderBy(t => doc.IsFollower(t.layer) ? 1 : 0).ToList();

        // keep the paste inside the canvas when it came from a bigger one
        var src = Clipboard.Source;
        var rect = src with
        {
            X = Math.Clamp(src.X, 0, Math.Max(0, doc.Width - src.W)),
            Y = Math.Clamp(src.Y, 0, Math.Max(0, doc.Height - src.H)),
        };
        var f = new FloatingSelection("Paste", doc.Width, doc.Height, rect, (bool[])Clipboard.Mask.Clone());
        foreach (var (layer, px) in targets)
        {
            var cel = doc.CelForEdit(layer, _ed.CurrentFrame, out var created);
            if (cel == null) continue; // a fill whose line layer has nothing here
            f.Layers.Add(new FloatingLayer
            {
                Layer = layer,
                Pixels = (byte[])px.Clone(),
                Session = new CelEditSession(cel),
                Created = created,
            });
        }
        if (f.Layers.Count == 0)
        {
            _ed.SetStatus($"Can't paste here: \"{_ed.CurrentLayer.Name}\" follows a layer with nothing on this frame.");
            return;
        }
        Floating = f;
        Selection.Clear();
    }
}
