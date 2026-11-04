namespace Fink.Core;

public interface IEdit
{
    string Name { get; }
    void Undo();
    void Redo();
}

/// <summary>An edit made of two closures, for structural changes (add frame, add layer...).</summary>
public sealed class ActionEdit : IEdit
{
    private readonly Action _undo, _redo;
    public string Name { get; }

    public ActionEdit(string name, Action redo, Action undo)
    {
        Name = name;
        _redo = redo;
        _undo = undo;
    }

    public void Undo() => _undo();
    public void Redo() => _redo();
}

/// <summary>
/// A pixel edit, stored as before/after copies of only the tiles it touched, so a one-pixel
/// stroke on a big canvas costs one tile, not the canvas.
/// </summary>
public sealed class PixelEdit : IEdit
{
    public const int TileSize = 32;

    internal readonly record struct TilePatch(int TileX, int TileY, byte[] Before, byte[] After);

    private readonly Cel _cel;
    private readonly List<TilePatch> _patches;
    public string Name { get; }
    public int TileCount => _patches.Count;

    internal PixelEdit(string name, Cel cel, List<TilePatch> patches)
    {
        Name = name;
        _cel = cel;
        _patches = patches;
    }

    public void Undo()
    {
        foreach (var p in _patches) WriteTile(_cel, p.TileX, p.TileY, p.Before);
    }

    public void Redo()
    {
        foreach (var p in _patches) WriteTile(_cel, p.TileX, p.TileY, p.After);
    }

    internal static byte[] ReadTile(Cel cel, int tx, int ty)
    {
        var tile = new byte[TileSize * TileSize];
        int x0 = tx * TileSize, y0 = ty * TileSize;
        int w = Math.Min(TileSize, cel.Width - x0), h = Math.Min(TileSize, cel.Height - y0);
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(cel.Pixels, (y0 + y) * cel.Width + x0, tile, y * TileSize, w);
        return tile;
    }

    internal static void WriteTile(Cel cel, int tx, int ty, byte[] tile)
    {
        int x0 = tx * TileSize, y0 = ty * TileSize;
        int w = Math.Min(TileSize, cel.Width - x0), h = Math.Min(TileSize, cel.Height - y0);
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(tile, y * TileSize, cel.Pixels, (y0 + y) * cel.Width + x0, w);
    }
}

/// <summary>
/// Collects the pixel changes of one stroke on one cel. Each tile is copied the first time it is
/// touched; <see cref="Commit"/> turns the changed tiles into a <see cref="PixelEdit"/>.
/// </summary>
public sealed class CelEditSession
{
    private readonly Dictionary<(int, int), byte[]> _before = new();
    public Cel Cel { get; }

    /// <summary>When set, only these pixels (document-sized mask) can change: painting inside a selection.</summary>
    public bool[]? Clip { get; init; }

    public CelEditSession(Cel cel) => Cel = cel;

    public byte Get(int x, int y) => Cel.Get(x, y);

    public void Set(int x, int y, byte index)
    {
        if (!Cel.InBounds(x, y)) return;
        if (Clip != null && !Clip[y * Cel.Width + x]) return;
        var key = (x / PixelEdit.TileSize, y / PixelEdit.TileSize);
        if (!_before.ContainsKey(key)) _before[key] = PixelEdit.ReadTile(Cel, key.Item1, key.Item2);
        Cel.Pixels[y * Cel.Width + x] = index;
    }

    /// <summary>Returns null when the stroke changed nothing.</summary>
    public PixelEdit? Commit(string name)
    {
        var patches = new List<PixelEdit.TilePatch>();
        foreach (var ((tx, ty), before) in _before)
        {
            var after = PixelEdit.ReadTile(Cel, tx, ty);
            if (!before.AsSpan().SequenceEqual(after)) patches.Add(new PixelEdit.TilePatch(tx, ty, before, after));
        }
        _before.Clear();
        return patches.Count == 0 ? null : new PixelEdit(name, Cel, patches);
    }

    /// <summary>Puts every touched tile back as it was (a cancelled stroke).</summary>
    public void Revert()
    {
        foreach (var ((tx, ty), before) in _before) PixelEdit.WriteTile(Cel, tx, ty, before);
        _before.Clear();
    }
}

/// <summary>One step in the history tree: the edit that led here from its parent.</summary>
public sealed class HistoryNode
{
    /// <summary>Order of creation, from 0 (the start).</summary>
    public int Id { get; internal set; }
    /// <summary>The edit from the parent to here; null for the start.</summary>
    public IEdit? Edit { get; internal set; }
    public HistoryNode? Parent { get; internal set; }
    public List<HistoryNode> Children { get; } = new();
    /// <summary>The child Redo goes to: the one most recently made or visited.</summary>
    public HistoryNode? Preferred { get; internal set; }
    public DateTime Time { get; } = DateTime.Now;
    /// <summary>What the edit was on (the editor tags each step with its asset).</summary>
    public object? Tag { get; internal set; }
    /// <summary>A name you gave this point, to find it again.</summary>
    public string? Label { get; set; }

    public string Name => Edit?.Name ?? "(start)";

    public int Depth
    {
        get
        {
            int d = 0;
            for (var n = Parent; n != null; n = n.Parent) d++;
            return d;
        }
    }

    public bool IsAncestorOf(HistoryNode other)
    {
        for (var n = other; n != null; n = n.Parent) if (n == this) return true;
        return false;
    }
}

/// <summary>
/// An undo history that branches instead of forgetting. Undoing some steps and then making a
/// change starts a new branch from there; the steps you undid stay on their own branch, and
/// <see cref="JumpTo(HistoryNode)"/> can go back to any of them (undoing to where the branches
/// split, then redoing down the other one). Redo follows the branch you were on last.
///
/// <see cref="Position"/> and <see cref="Entries"/> describe the current line: the path from
/// the start to here, then on along the steps Redo would take.
/// </summary>
public sealed class History
{
    private readonly List<HistoryNode> _nodes = new();
    private int _nextId;

    public HistoryNode Root { get; private set; }
    public HistoryNode Current { get; private set; }

    /// <summary>Every step there is, in the order they were made (the start first).</summary>
    public IReadOnlyList<HistoryNode> Nodes => _nodes;

    /// <summary>When there are more steps than this, the oldest ones are folded into the start.</summary>
    public int Limit { get; set; } = 2000;

    /// <summary>Tag given to new steps (the editor sets it to the asset being edited).</summary>
    public object? DefaultTag { get; set; }

    /// <summary>Bumps on every change, so views know when to redraw.</summary>
    public int Version { get; private set; }

    public History()
    {
        Root = Current = NewNode(null, null);
    }

    private HistoryNode NewNode(IEdit? edit, HistoryNode? parent)
    {
        var n = new HistoryNode { Id = _nextId++, Edit = edit, Parent = parent, Tag = DefaultTag };
        _nodes.Add(n);
        return n;
    }

    public bool CanUndo => Current.Parent != null;
    public bool CanRedo => Current.Preferred != null;

    /// <summary>How many steps from the start to here.</summary>
    public int Position => Current.Depth;

    /// <summary>The current line's edits: start to here, then on along the redo steps.</summary>
    public IReadOnlyList<IEdit> Entries => Line().Skip(1).Select(n => n.Edit!).ToList();

    /// <summary>The current line's nodes, the start first.</summary>
    public List<HistoryNode> Line()
    {
        var path = new List<HistoryNode>();
        for (var n = Current; n != null; n = n.Parent) path.Add(n);
        path.Reverse();
        for (var n = Current.Preferred; n != null; n = n.Preferred) path.Add(n);
        return path;
    }

    /// <summary>Records an edit that has already been applied. Steps ahead of here stay, on their own branch.</summary>
    public void Push(IEdit edit)
    {
        var node = NewNode(edit, Current);
        Current.Children.Add(node);
        Current.Preferred = node;
        Current = node;
        Trim();
        Version++;
    }

    /// <summary>Applies an edit for the first time and records it.</summary>
    public void Do(IEdit edit)
    {
        edit.Redo();
        Push(edit);
    }

    public void Undo()
    {
        if (Current.Parent is not { } parent) return;
        Current.Edit!.Undo();
        parent.Preferred = Current;
        Current = parent;
        Version++;
    }

    public void Redo()
    {
        if (Current.Preferred is not { } next) return;
        next.Edit!.Redo();
        Current = next;
        Version++;
    }

    /// <summary>Goes to any step, on any branch: back to where the branches meet, then down.</summary>
    public void JumpTo(HistoryNode target)
    {
        if (!_nodes.Contains(target)) return;
        while (!Current.IsAncestorOf(target)) Undo();
        var down = new List<HistoryNode>();
        for (var n = target; n != Current; n = n.Parent!) down.Add(n);
        down.Reverse();
        foreach (var n in down)
        {
            Current.Preferred = n;
            Redo();
        }
    }

    /// <summary>Goes to a step of the current line (0: the start).</summary>
    public void JumpTo(int position)
    {
        var line = Line();
        JumpTo(line[Math.Clamp(position, 0, line.Count - 1)]);
    }

    /// <summary>The steps not on the current line, newest first: branches you left.</summary>
    public IEnumerable<HistoryNode> OtherBranches()
    {
        var line = Line().ToHashSet();
        return _nodes.Where(n => !line.Contains(n) && n.Children.Count == 0).Reverse();
    }

    /// <summary>
    /// Over the limit, the start moves forward: while it has a single step after it, that step
    /// becomes the start (it can't be undone any more). Branches keep everything they need.
    /// </summary>
    private void Trim()
    {
        while (_nodes.Count > Limit && Root.Children.Count == 1 && Root != Current)
        {
            var next = Root.Children[0];
            _nodes.Remove(Root);
            next.Parent = null;
            next.Edit = null;
            next.Label ??= Root.Label;
            Root = next;
        }
    }
}
