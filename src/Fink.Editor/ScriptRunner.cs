using System.Globalization;
using System.Numerics;
using System.Text;
using Fink.Core;

namespace Fink.Editor;

public enum YieldKind
{
    /// <summary>Script finished.</summary>
    Done,
    /// <summary>Let the window draw this many frames before continuing.</summary>
    Wait,
    /// <summary>Capture a screenshot, then continue.</summary>
    Shot,
}

public readonly record struct ScriptYield(YieldKind Kind, int Frames = 0, string? ShotName = null, bool CanvasOnly = false);

/// <summary>
/// Runs a <c>.fink-test</c> automation script against a <see cref="Session"/>: commands, key
/// presses through the real hotkey dispatcher, pointer strokes in document pixels, assertions,
/// and (in the window) screenshots. One instruction per line; <c>#</c> starts a comment.
/// <code>
/// new 32 32                     a new document
/// cmd tool.fill                 run a command by id
/// key Ctrl+Z                    press and release a chord through the hotkeys
/// keydown Alt / keyup Alt       hold and release (hold commands)
/// hover canvas|timeline|none    which scope is active for key presses
/// stroke 1,1 10,1 [right] [shift] [alt]   press at the first point (pixel centers; 1.0,1.0 for exact points), drag, release
/// drag 40,10                    press, move the mouse by a screen delta, release (panning)
/// tool Pencil / color 3 / color2 4 / layer Line / frame 2
/// set size 3 | gap 1 | pixelperfect on | contiguous off | autolayer on
/// bind edit.undo Ctrl+U[@Canvas] / unbind edit.undo Ctrl+Z / resetkeys
/// advance 250                   advance playback by milliseconds
/// assert frames == 2            ==, != or ~= (contains); see Property for the names
/// assert pixel Line 3,4 == 1    palette index on a layer of the current frame
/// wait 2 / shot name [canvas]   window only; ignored headless
/// </code>
/// </summary>
public sealed class ScriptRunner
{
    private readonly Session _s;
    private readonly string[] _lines;
    private int _pc;
    private readonly List<Scope> _scopes = new() { Scope.Global };

    public List<string> Failures { get; } = new();
    public List<string> Log { get; } = new();
    public int AssertCount { get; private set; }
    public bool Passed => Failures.Count == 0;
    public string Name { get; }

    public ScriptRunner(Session session, string text, string name = "script")
    {
        _s = session;
        _lines = text.Replace("\r", "").Split('\n');
        Name = name;
    }

    /// <summary>Runs instructions until the script needs the window (wait/shot) or ends.</summary>
    /// <summary>Set by the window: whether the shader preview has finished compiling. Headless, there is no compiling.</summary>
    public Func<bool>? ShaderReady { get; init; }
    private int _shaderWaits;

    public ScriptYield Next()
    {
        while (_pc < _lines.Length)
        {
            int lineNo = _pc + 1;
            var line = _lines[_pc++];
            var hash = line.IndexOf('#');
            if (hash >= 0 && !InsideQuotes(line, hash)) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            try
            {
                var y = Execute(Tokenize(line), lineNo);
                if (y.HasValue) return y.Value;
            }
            catch (Exception e)
            {
                Failures.Add($"line {lineNo}: {line}\n    error: {e.Message}");
            }
        }
        return new ScriptYield(YieldKind.Done);
    }

    /// <summary>Runs to the end, ignoring waits and screenshots.</summary>
    public void RunHeadless()
    {
        while (Next().Kind != YieldKind.Done) { }
    }

    private ScriptYield? Execute(List<string> t, int lineNo)
    {
        var ed = _s.Editor;
        string Arg(int i) => i < t.Count ? t[i] : throw new FormatException($"\"{t[0]}\" needs more arguments");
        bool Has(string flag) => t.Skip(1).Any(x => x.Equals(flag, StringComparison.OrdinalIgnoreCase));

        switch (t[0].ToLowerInvariant())
        {
            case "new":
                ed.NewDocument(int.Parse(Arg(1)), int.Parse(Arg(2)));
                break;
            case "cmd":
            {
                var c = _s.Commands.Find(Arg(1)) ?? throw new ArgumentException($"no command \"{t[1]}\"");
                if (!c.Enabled) Log.Add($"line {lineNo}: {c.Id} is disabled; skipped");
                else c.Execute();
                break;
            }
            case "key":
            {
                var chord = KeyChord.Parse(Arg(1));
                var mods = chord.Mods | KeyChord.ModOf(chord.Key);
                _s.Hotkeys.Handle(new KeyEvent(chord.Key, true), mods, _scopes);
                _s.Hotkeys.Handle(new KeyEvent(chord.Key, false), mods, _scopes);
                break;
            }
            case "keydown":
            {
                var chord = KeyChord.Parse(Arg(1));
                _s.Hotkeys.Handle(new KeyEvent(chord.Key, true), chord.Mods | KeyChord.ModOf(chord.Key), _scopes);
                break;
            }
            case "keyup":
            {
                var chord = KeyChord.Parse(Arg(1));
                _s.Hotkeys.Handle(new KeyEvent(chord.Key, false), chord.Mods, _scopes);
                break;
            }
            case "hover":
                _scopes.Clear();
                switch (Arg(1).ToLowerInvariant())
                {
                    case "canvas": _scopes.Add(Scope.Canvas); break;
                    case "timeline": _scopes.Add(Scope.Timeline); break;
                    case "none": break;
                    default: throw new FormatException("hover canvas|timeline|none");
                }
                _scopes.Add(Scope.Global);
                break;
            case "stroke":
            {
                var points = t.Skip(1).Where(x => x.Contains(',')).Select(ParseDocPoint).ToList();
                if (points.Count == 0) throw new FormatException("stroke needs at least one x,y point");
                var mods = (Has("shift") ? Mods.Shift : Mods.None) | (Has("alt") ? Mods.Alt : Mods.None) | (Has("ctrl") ? Mods.Ctrl : Mods.None);
                ed.PointerDown(points[0], Has("right") ? PointerButton.Secondary : PointerButton.Primary, mods);
                foreach (var p in points.Skip(1)) ed.PointerMove(p, mods);
                ed.PointerUp();
                break;
            }
            case "drag":
            {
                var d = ParsePoint(Arg(1));
                ed.PointerDown(new Vector2(-1, -1), PointerButton.Primary, Mods.None);
                ed.PointerDrag(new Vector2(d.x, d.y), anyButtonDown: true);
                ed.PointerUp();
                break;
            }
            case "tool":
                ed.SelectTool(ed.FindTool(Arg(1)));
                break;
            case "color":
                ed.Primary = byte.Parse(Arg(1));
                break;
            case "color2":
                ed.Secondary = byte.Parse(Arg(1));
                break;
            case "layer":
                ed.SelectLayer(Arg(1));
                break;
            case "frame":
                ed.FrameIndex = int.Parse(Arg(1)) - 1;
                break;
            case "set":
                Set(Arg(1).ToLowerInvariant(), Arg(2));
                break;
            case "expose":
            {
                // expose <layer> <frame> <number|-> [count]
                var layer = FindLayer(Arg(1));
                int first = int.Parse(Arg(2)) - 1;
                int? number = Arg(3) == "-" ? null : int.Parse(Arg(3));
                int count = t.Count > 4 ? int.Parse(t[4]) : 1;
                for (int i = first; i < first + count; i++) ed.SetCell(i, layer, number);
                break;
            }
            case "holdend":
                // holdend <layer> <start frame> <end frame>: dragging a block's edge
                ed.SetHoldEnd(FindLayer(Arg(1)), int.Parse(Arg(2)) - 1, int.Parse(Arg(3)) - 1);
                break;
            case "tag":
            {
                // tag <name> <from> <to> [forward|pingpong|once]
                var loop = t.Count > 4 ? Enum.Parse<LoopMode>(t[4], ignoreCase: true) : LoopMode.Forward;
                ed.AddTag(Arg(1), int.Parse(Arg(2)) - 1, int.Parse(Arg(3)) - 1, loop);
                break;
            }
            case "export":
                // export <folder>: every atlas, into that folder
                foreach (var a in ed.Project.Atlases) a.OutputFolder = Path.GetFullPath(Arg(1));
                if (!ed.ExportAtlases()) throw new InvalidOperationException(ed.LastAtlasExport);
                break;
            case "atlas":
            {
                // atlas new <name> | atlas <name> | atlas include <asset> | atlas all | atlas maxsize <n> | atlas pow2 on|off | atlas padding <n> | atlas repack
                switch (Arg(1).ToLowerInvariant())
                {
                    case "new": ed.AddAtlas(Arg(2)); break;
                    case "include":
                        ed.CurrentAtlas.IncludeAll = false;
                        ed.CurrentAtlas.AssetIds.Add(ed.FindAsset(Arg(2)).Id);
                        break;
                    case "all": ed.CurrentAtlas.IncludeAll = true; break;
                    case "maxsize": ed.CurrentAtlas.MaxSize = int.Parse(Arg(2)); break;
                    case "pow2": ed.CurrentAtlas.PowerOfTwo = Arg(2) is "on" or "true" or "1"; break;
                    case "padding": ed.CurrentAtlas.Padding = int.Parse(Arg(2)); break;
                    case "repack": ed.BuildAtlas(ed.CurrentAtlas, tight: true); break;
                    case "name": ed.CurrentAtlas.Name = Arg(2); break;
                    case "folder": ed.CurrentAtlas.OutputFolder = Path.GetFullPath(Arg(2)); break;
                    case "code": ed.CurrentAtlas.CodeFolder = Path.GetFullPath(Arg(2)); break;
                    case "loadas": ed.CurrentAtlas.ContentPath = Arg(2); break;
                    case "export": if (!ed.ExportAtlases()) throw new InvalidOperationException(ed.LastAtlasExport); break;
                    default:
                        ed.AtlasIndex = ed.Project.Atlases.FindIndex(a => a.Name.Equals(Arg(1), StringComparison.OrdinalIgnoreCase));
                        if (ed.AtlasIndex < 0) throw new ArgumentException($"no atlas \"{Arg(1)}\"");
                        break;
                }
                break;
            }
            case "variant":
                // variant new <name> | variant <index> (0: base) | variant set <index> <entry> <rrggbb>
                switch (Arg(1).ToLowerInvariant())
                {
                    case "new":
                        ed.Project.Palette.AddVariant(Arg(2));
                        ed.ViewVariant = ed.Project.Palette.Variants.Count;
                        break;
                    case "set":
                        ed.Project.Palette.Set(int.Parse(Arg(2)), int.Parse(Arg(3)), Rgba.FromHex(Arg(4)));
                        break;
                    default: ed.ViewVariant = int.Parse(Arg(1)); break;
                }
                break;
            case "waitshader":
                // waits (up to about 20 seconds) for the window to finish compiling the shader
                if (ShaderReady != null && !ShaderReady() && _shaderWaits++ < 1200)
                {
                    _pc--;
                    return new ScriptYield(YieldKind.Wait, 1);
                }
                _shaderWaits = 0;
                break;
            case "shader":
                // shader <file.fx | none>: the window compiles it; "wait" for it before asserting
                _s.Shader.File = Arg(1).Equals("none", StringComparison.OrdinalIgnoreCase) ? ""
                    : Arg(1).StartsWith("sample:", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(AppContext.BaseDirectory, "shaders", Arg(1)[7..] + ".fx")
                        : Path.GetFullPath(Arg(1));
                break;
            case "shaderparam":
            {
                // shaderparam <name> <v1> [v2 v3 v4] | time | texelsize | texturesize
                var p = _s.Shader.ParamsFor(_s.Shader.File).TryGetValue(Arg(1), out var ps) ? ps : _s.Shader.ParamsFor(_s.Shader.File)[Arg(1)] = new ShaderParamState();
                if (Enum.TryParse<ParamSource>(Arg(2), true, out var src) && !float.TryParse(Arg(2), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) p.Source = src;
                else
                {
                    p.Source = ParamSource.Value;
                    for (int i = 2; i < t.Count && i < 6; i++) p.Values[i - 2] = float.Parse(t[i], CultureInfo.InvariantCulture);
                }
                break;
            }
            case "spritedata":
            {
                // spritedata 1|2 x,y,z,w: the per-sprite values in texcoord1 / texcoord2
                var v = Arg(2).Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                var dest = Arg(1) == "2" ? _s.Shader.TexCoord2 : _s.Shader.TexCoord1;
                for (int i = 0; i < 4 && i < v.Length; i++) dest[i] = v[i];
                break;
            }
            case "save":
                ed.Save(Path.GetFullPath(Arg(1)));
                break;
            case "open":
                ed.Open(Path.GetFullPath(Arg(1)));
                break;
            case "asset":
            {
                // asset new <name> <w> <h> | asset rename <name> <new> | asset delete <name> | asset duplicate | asset <name>
                switch (Arg(1).ToLowerInvariant())
                {
                    case "new": ed.AddAsset(Arg(2), int.Parse(Arg(3)), int.Parse(Arg(4))); break;
                    case "rename": ed.RenameAsset(ed.FindAsset(Arg(2)), Arg(3)); break;
                    case "delete": ed.RemoveAsset(ed.FindAsset(Arg(2))); break;
                    case "duplicate": ed.DuplicateAsset(ed.Document); break;
                    default: ed.SwitchAsset(ed.FindAsset(Arg(1))); break;
                }
                break;
            }
            case "autosave":
                _s.Recovery.Write(ed);
                break;
            case "point":
            {
                // point new <name> | point pivot | point <name> | point set <name|pivot> x,y | point clear <name|pivot>
                PointTrack Track(string n) => n.Equals("pivot", StringComparison.OrdinalIgnoreCase)
                    ? ed.EnsurePivot()
                    : ed.Document.Points.FirstOrDefault(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"no point \"{n}\"");
                switch (Arg(1).ToLowerInvariant())
                {
                    case "new": ed.AddPoint(Arg(2), keyed: t.Count > 3 && t[3].Equals("keyed", StringComparison.OrdinalIgnoreCase)); break;
                    case "smooth": DocumentOps.SetSmooth(ed.Document, ed.History, Track(Arg(2)), Arg(3) is "on" or "true" or "1"); break;
                    case "move":
                        // point move <name> <from frame> <to frame>: drag a key on the timeline
                        DocumentOps.MoveKey(ed.Document, ed.History, Track(Arg(2)), int.Parse(Arg(3)) - 1, int.Parse(Arg(4)) - 1);
                        break;
                    case "set":
                    {
                        var (x, y) = ParsePoint(Arg(3));
                        if (!DocumentOps.SetPoint(ed.Document, ed.History, Track(Arg(2)), ed.FrameIndex, new PointXY(x, y)))
                            throw new InvalidOperationException("no drawing there");
                        break;
                    }
                    case "clear": DocumentOps.SetPoint(ed.Document, ed.History, Track(Arg(2)), ed.FrameIndex, null); break;
                    default: ed.SelectedPointId = Track(Arg(1)).Id; break;
                }
                break;
            }
            case "channel":
                // channel color | channel <name> | channel new <name> value|normal | channel value <n> | channel normal <n>
                switch (Arg(1).ToLowerInvariant())
                {
                    case "color": ed.ActiveChannelId = 0; break;
                    case "new": ed.AddChannel(Arg(2), Arg(3).Equals("normal", StringComparison.OrdinalIgnoreCase) ? ChannelKind.Normal : ChannelKind.Value); break;
                    // "channel normal 8" sets the brush; "channel normal" picks the channel called normal
                    case "value" when t.Count > 2: ed.ValueBrush = byte.Parse(Arg(2)); break;
                    case "normal" when t.Count > 2: ed.NormalBrush = byte.Parse(Arg(2)); break;
                    default: ed.ActiveChannelId = (ed.Project.FindChannel(Arg(1)) ?? throw new ArgumentException($"no channel \"{Arg(1)}\"")).Id; break;
                }
                break;
            case "map":
            {
                // map <name> <r> <g> <b> <a>  (slots; "-" for empty)
                string S(int i) => Arg(i) == "-" ? "" : Arg(i);
                ed.CurrentAtlas.Maps.RemoveAll(m => m.Name == Arg(1));
                ed.CurrentAtlas.Maps.Add(new MapConfig { Name = Arg(1), R = S(2), G = S(3), B = S(4), A = S(5) });
                break;
            }
            case "pen":
                // pen eraser | pen tip: what the window reports when the pen is flipped
                if (Arg(1).Equals("eraser", StringComparison.OrdinalIgnoreCase)) ed.BeginMomentary(Momentary.Eraser);
                else ed.EndMomentary(Momentary.Eraser);
                break;
            case "follow":
                ed.SetFollows(FindLayer(Arg(1)), Arg(2).Equals("none", StringComparison.OrdinalIgnoreCase) ? null : FindLayer(Arg(2)));
                break;
            case "bind":
                _s.Keymap.AddBinding(Arg(1), Binding.Parse(Arg(2)));
                break;
            case "unbind":
                _s.Keymap.RemoveBinding(Arg(1), Binding.Parse(Arg(2)));
                break;
            case "resetkeys":
                _s.Keymap.ResetAll();
                break;
            case "advance":
                ed.Advance(double.Parse(Arg(1), CultureInfo.InvariantCulture));
                break;
            case "echo":
                Log.Add(string.Join(' ', t.Skip(1)));
                break;
            case "assert":
                Assert(t, lineNo);
                break;
            case "wait":
                return new ScriptYield(YieldKind.Wait, t.Count > 1 ? int.Parse(t[1]) : 1);
            case "shot":
                return new ScriptYield(YieldKind.Shot, ShotName: Arg(1), CanvasOnly: Has("canvas"));
            default:
                throw new FormatException($"unknown instruction \"{t[0]}\"");
        }
        return null;
    }

    private void Set(string what, string value)
    {
        var ts = _s.Editor.ToolSettings;
        bool On() => value.ToLowerInvariant() is "on" or "true" or "1" or "yes";
        switch (what)
        {
            case "size": ts.Size = int.Parse(value); break;
            case "gap": ts.GapClose = int.Parse(value); break;
            case "pixelperfect": ts.PixelPerfect = On(); break;
            case "squaretip": ts.SquareTip = On(); break;
            case "contiguous": ts.FillContiguous = On(); break;
            case "autolayer": _s.Editor.AutoSelectLayer = On(); break;
            case "fps": _s.Editor.SetFps(int.Parse(value)); break;
            case "zoom": _s.Editor.View.Zoom = int.Parse(value); _s.Editor.View.FitRequested = false; break;
            default: throw new FormatException($"unknown setting \"{what}\"");
        }
    }

    private void Assert(List<string> t, int lineNo)
    {
        AssertCount++;
        int opAt = t.FindIndex(x => x is "==" or "!=" or "~=");
        if (opAt < 2 || opAt == t.Count - 1) throw new FormatException("assert <what> ==|!=|~= <value>");
        var lhs = t.Skip(1).Take(opAt - 1).ToList();
        string op = t[opAt];
        string expected = string.Join(' ', t.Skip(opAt + 1));

        if (ShaderReady == null && lhs[0].StartsWith("shader", StringComparison.OrdinalIgnoreCase))
        {
            AssertCount--;
            Log.Add($"line {lineNo}: shader asserts need the window; skipped");
            return;
        }

        string actual = lhs[0].Equals("pixel", StringComparison.OrdinalIgnoreCase)
            ? Pixel(lhs)
            : Property(string.Join(' ', lhs));

        bool ok = op switch
        {
            "==" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            _ => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
        };
        if (!ok) Failures.Add($"line {lineNo}: assert {string.Join(' ', lhs)} {op} {expected}\n    actual: {actual}");
    }

    private string Pixel(List<string> lhs)
    {
        if (lhs.Count < 3) throw new FormatException("assert pixel <layer> x,y == n");
        var ed = _s.Editor;
        var layer = FindLayer(lhs[1]);
        var (x, y) = ParsePoint(lhs[2]);
        return ed.Document.TryGetCel(layer, ed.CurrentFrame, out var cel) ? cel.Get(x, y).ToString() : "0";
    }

    private string AtlasSummary()
    {
        var b = _s.Editor.BuildAtlas(_s.Editor.CurrentAtlas);
        return b.Error ?? $"{b.Config.Name} {b.Width}x{b.Height}, {b.Cells.Count} cells, {b.Clips.Count} clips";
    }

    private Layer FindLayer(string nameOrIndex)
    {
        var doc = _s.Editor.Document;
        return int.TryParse(nameOrIndex, out var li)
            ? doc.Layers[li]
            : doc.Layers.FirstOrDefault(l => l.Name.Equals(nameOrIndex, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"no layer \"{nameOrIndex}\"");
    }

    /// <summary>What a layer shows on each frame: drawing numbers, "-" for nothing.</summary>
    private string Track(Layer layer)
    {
        var doc = _s.Editor.Document;
        return string.Join(' ', doc.Frames.Select(f => doc.DrawingAt(layer, f)?.Number.ToString() ?? "-"));
    }

    /// <summary>The names <c>assert</c> can read.</summary>
    public string Property(string name)
    {
        var ed = _s.Editor;
        var ui = _s.Ui;
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("panel.")) return ui.IsOpen(name[6..]).ToString();
        if (lower.StartsWith("binding."))
            return string.Join(", ", _s.Keymap.BindingsOf(name[8..]).Select(b => b.ToString()));
        if (lower.StartsWith("track ")) return Track(FindLayer(name[6..].Trim()));
        if (lower.StartsWith("point "))
        {
            var pn = name[6..].Trim();
            var t = pn.Equals("pivot", StringComparison.OrdinalIgnoreCase) ? ed.Document.Pivot
                : ed.Document.Points.FirstOrDefault(p => p.Name.Equals(pn, StringComparison.OrdinalIgnoreCase));
            if (t == null) return "none";
            var at = ed.Document.PointAt(t, ed.FrameIndex, out var own);
            return at is { } xy ? $"{xy.X},{xy.Y}{(own ? "" : " inherited")}" : "-";
        }
        if (lower.StartsWith("data "))
        {
            // data <channel> <layer> x,y
            var parts = name[5..].Trim().Split(' ');
            var ch = ed.Project.FindChannel(parts[0]) ?? throw new ArgumentException($"no channel \"{parts[0]}\"");
            var dl = FindLayer(parts[1]);
            var (dx, dy) = ParsePoint(parts[2]);
            var d = ed.Document.DrawingAt(dl, ed.CurrentFrame);
            return d != null && d.Channels.TryGetValue(ch.Id, out var dc) ? dc.Get(dx, dy).ToString() : "0";
        }
        if (lower.StartsWith("checked.")) return _s.Commands.Get(name[8..]).Checked.ToString();
        var sel = ed.Selection;
        return lower switch
        {
            "frames" => ed.Document.Frames.Count.ToString(),
            "fps" => ed.Document.Fps.ToString(),
            "channel" => ed.ActiveChannel?.Name ?? "color",
            "channels" => string.Join(", ", ed.Project.Channels.Select(c => c.Name)),
            "points" => string.Join(", ", ed.Document.Points.Select(p => p.Name)),
            "asset" => ed.Document.Name,
            "assets" => string.Join(", ", ed.Project.Assets.Select(a => a.Name)),
            "dirty" => ed.IsDirty.ToString(),
            "file" => ed.FilePath == null ? "" : Path.GetFileName(ed.FilePath),
            "recovery" => File.Exists(_s.Recovery.File).ToString(),
            "ramps" => string.Join(", ", ed.Project.Palette.Ramps.Select(r => $"{r.Name}:{r.Start}+{r.Count}")),
            "shaderstatus" => _s.Shader.Status,
            "export" => ed.LastAtlasExport ?? "",
            "atlas" => AtlasSummary(),
            "variants" => string.Join(", ", ed.Project.Palette.Variants.Select(v => v.Name)),
            "variant" => ed.ViewVariant.ToString(),
            "tags" => string.Join(", ", ed.Document.Tags.Select(tg => { var (a, b) = ed.Document.TagRange(tg); return $"{tg.Name}:{a + 1}-{b + 1}:{tg.Loop}"; })),
            "playingtag" => ed.PlayingTag?.Name ?? "",
            "drawing" => ed.CurrentDrawing?.Number.ToString() ?? "-",
            "drawings" => ed.CurrentLayer.Drawings.Count.ToString(),
            "layers" => ed.Document.Layers.Count.ToString(),
            "frame" => (ed.FrameIndex + 1).ToString(),
            "layer" => ed.CurrentLayer.Name,
            "layerrole" => ed.CurrentLayer.Role.ToString(),
            "tool" => ed.Tool.Name,
            "effectivetool" => ed.EffectiveToolName,
            "momentary" => ed.Momentary.ToString(),
            "color" => ed.Primary.ToString(),
            "color2" => ed.Secondary.ToString(),
            "size" => ed.ToolSettings.Size.ToString(),
            "history" => ed.History.Position.ToString(),
            "historycount" => ed.History.Entries.Count.ToString(),
            "steps" => string.Join(" | ", ed.History.Line().Skip(1).Take(ed.History.Position).Select(n => n.Name)),
            "status" => ed.Status ?? "",
            "zoom" => ed.View.Zoom.ToString(),
            "pan" => $"{ed.View.Pan.X.ToString(CultureInfo.InvariantCulture)},{ed.View.Pan.Y.ToString(CultureInfo.InvariantCulture)}",
            "onion" => ed.Onion.Enabled.ToString(),
            "playing" => ed.Playing.ToString(),
            "focus" => ui.FocusMode.ToString(),
            "fullscreen" => ui.Fullscreen.ToString(),
            "workspace" => ui.CurrentWorkspace,
            "pendingworkspace" => ui.PendingWorkspace ?? "",
            "options" => ui.ShowOptions.ToString(),
            "palette" => ui.ShowCommandPalette.ToString(),
            "lastcommand" => _s.Hotkeys.LastCommandId ?? "",
            "size.document" => $"{ed.Document.Width}x{ed.Document.Height}",
            "selection" => sel.DisplayMask().Count(m => m).ToString(),
            "selectionbounds" => Bounds(sel.DisplayMask()),
            "floating" => (sel.Floating != null).ToString(),
            "clipboard" => sel.Clipboard == null ? "" : $"{sel.Clipboard.Source.W}x{sel.Clipboard.Source.H} x{sel.Clipboard.Layers.Count}",
            "transform" => sel.Floating == null ? "" : FormatTransform(sel.Floating.Transform),
            _ => throw new ArgumentException($"unknown property \"{name}\""),
        };
    }

    public string Report()
    {
        var sb = new StringBuilder();
        foreach (var l in Log) sb.AppendLine($"  {l}");
        foreach (var f in Failures) sb.AppendLine($"  FAIL {f}");
        sb.AppendLine(Passed ? $"PASS {Name} ({AssertCount} asserts)" : $"FAIL {Name} ({Failures.Count} of {AssertCount} asserts failed or errored)");
        return sb.ToString();
    }

    private string Bounds(bool[] mask)
    {
        var tmp = new Selection(_s.Editor.Document.Width, _s.Editor.Document.Height);
        tmp.CopyFrom(mask);
        var b = tmp.Bounds();
        return b.IsEmpty ? "none" : $"{b.X},{b.Y},{b.W},{b.H}";
    }

    private static string FormatTransform(TransformParams t) => string.Join(",",
        new[] { t.Center.X, t.Center.Y, t.ScaleX, t.ScaleY, t.Angle }.Select(v => MathF.Round(v, 3).ToString(CultureInfo.InvariantCulture)));

    /// <summary>A document position: "3,4" is the center of pixel (3,4); "3.0,4.0" is exactly that point.</summary>
    private static Vector2 ParseDocPoint(string s)
    {
        var parts = s.Split(',');
        float F(string v) => float.Parse(v, CultureInfo.InvariantCulture) + (v.Contains('.') ? 0f : 0.5f);
        return new Vector2(F(parts[0]), F(parts[1]));
    }

    private static (int x, int y) ParsePoint(string s)
    {
        var parts = s.Split(',');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static bool InsideQuotes(string line, int index) => line[..index].Count(c => c == '"') % 2 == 1;

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }
}
