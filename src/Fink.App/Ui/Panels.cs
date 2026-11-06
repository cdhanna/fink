using Fink.Core;
using Fink.Editor;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

public sealed partial class FinkGame
{
    private Num.Vector4 RoleLine => Vec(Colors.Line);
    private Num.Vector4 RoleFill => Vec(Colors.Fill);
    private Num.Vector4 RoleRef => Vec(Colors.Reference);
    private int _fitFrames;

    private void DrawUi()
    {
        DrawMenuBar();
        DrawDockspace();
        DrawCanvasPanel();
        Panel("Tools", DrawToolsPanel);
        Panel("Palette", () =>
        {
            Ed.WatchProject();
            DrawPalettePanel();
            Ed.SettleProject("Edit palette", ImGui.IsAnyItemActive());
        });
        Panel("Layers", DrawLayersPanel);
        _timelineActive = false;
        Panel("Timeline", () =>
        {
            _timelineActive = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) || ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
            DrawTimelinePanel();
        });
        Panel("History", DrawHistoryPanel);
        Panel("Shader", DrawShaderPanel);
        Panel("Atlas", () =>
        {
            Ed.WatchProject();
            DrawAtlasPanel();
            Ed.SettleProject("Edit atlas settings", ImGui.IsAnyItemActive());
        });
        Panel("Assets", DrawAssetsPanel);

        DrawOptionsWindow();
        DrawCommandPalette();
        DrawNewDialog();
        DrawProjectDialogs();
        DrawSaveWorkspaceDialog();
    }

    /// <summary>A closable, dockable panel, drawn only while it is open.</summary>
    private void Panel(string name, Action draw)
    {
        if (!Ui.IsOpen(name)) return;
        bool open = true;
        if (ImGui.Begin(name, ref open)) draw();
        ImGui.End();
        if (!open) Ui.Panels[name] = false;
    }

    // ---------------------------------------------------------------- menu bar

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMainMenuBar()) return;
        if (ImGui.BeginMenu("File"))
        {
            MenuCommand("file.new");
            MenuCommand("file.open");
            if (ImGui.BeginMenu("Open Recent", Ui.RecentFiles.Count > 0))
            {
                foreach (var path in Ui.RecentFiles.ToList())
                    if (ImGui.MenuItem(Path.GetFileName(path) + "##" + path))
                    {
                        var p = path;
                        Ui.Guard(Ed, () => OpenFile(p));
                    }
                ImGui.EndMenu();
            }
            MenuCommand("file.save");
            MenuCommand("file.saveAs");
            ImGui.Separator();
            MenuCommand("file.export");
            MenuCommand("atlas.repack");
            ImGui.Separator();
            MenuCommand("app.options");
            ImGui.Separator();
            MenuCommand("app.quit");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Asset"))
        {
            MenuCommand("asset.new");
            MenuCommand("asset.goto");
            MenuCommand("asset.duplicate");
            MenuCommand("asset.rename");
            MenuCommand("asset.nineSlice");
            MenuCommand("asset.delete");
            ImGui.Separator();
            MenuCommand("asset.previous");
            MenuCommand("asset.next");
            ImGui.Separator();
            foreach (var asset in Ed.Project.Assets.Take(15))
                if (ImGui.MenuItem($"{asset.Name}##{asset.GetHashCode()}", "", asset == Ed.Document)) Ed.SwitchAsset(asset);
            if (Ed.Project.Assets.Count > 15) ImGui.TextDisabled($"... {Ed.Project.Assets.Count - 15} more: Ctrl+P");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            MenuCommand("edit.undo");
            MenuCommand("edit.redo");
            ImGui.Separator();
            MenuCommand("edit.cut");
            MenuCommand("edit.copy");
            MenuCommand("edit.paste");
            MenuCommand("edit.clear");
            ImGui.Separator();
            MenuCommand("app.commandPalette");
            ImGui.EndMenu();
        }
        DrawSelectMenu();
        if (ImGui.BeginMenu("Tools"))
        {
            foreach (var t in Ed.Tools.Where(t => t is not ISelectionTool)) MenuCommand($"tool.{t.Name.ToLowerInvariant()}");
            ImGui.Separator();
            MenuCommand("brush.pixelPerfect");
            MenuCommand("brush.squareTip");
            MenuCommand("fill.contiguous");
            MenuCommand("layer.autoSelect");
            ImGui.Separator();
            MenuCommand("color.swap");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Frame"))
        {
            MenuCommand("playback.toggle");
            MenuCommand("frame.previous");
            MenuCommand("frame.next");
            MenuCommand("frame.first");
            MenuCommand("frame.last");
            ImGui.Separator();
            MenuCommand("frame.new");
            MenuCommand("frame.duplicate");
            MenuCommand("frame.delete");
            MenuCommand("frame.moveLeft");
            MenuCommand("frame.moveRight");
            ImGui.Separator();
            MenuCommand("onion.toggle");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Layer"))
        {
            MenuCommand("layer.addLine");
            MenuCommand("layer.addFill");
            MenuCommand("layer.addReference");
            MenuCommand("layer.rename");
            MenuCommand("layer.delete");
            ImGui.Separator();
            MenuCommand("layer.selectAbove");
            MenuCommand("layer.selectBelow");
            MenuCommand("layer.moveUp");
            MenuCommand("layer.moveDown");
            ImGui.Separator();
            MenuCommand("layer.toggleVisible");
            MenuCommand("layer.toggleLock");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            MenuCommand("view.fit");
            MenuCommand("view.zoomIn");
            MenuCommand("view.zoomOut");
            MenuCommand("view.grid");
            ImGui.Separator();
            foreach (var p in UiState.PanelNames) MenuCommand($"panel.{p.ToLowerInvariant()}");
            ImGui.Separator();
            MenuCommand("view.focus");
            MenuCommand("view.fullscreen");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Workspace"))
        {
            DrawWorkspaceMenuItems();
            ImGui.EndMenu();
        }

        // the workspace picker, on the right of the bar
        var label = $"Workspace: {Ui.CurrentWorkspace}";
        float w = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight() + 12;
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - w - 8);
        ImGui.SetNextItemWidth(w);
        if (ImGui.BeginCombo("##workspace", label))
        {
            DrawWorkspaceMenuItems();
            ImGui.EndCombo();
        }
        ImGui.EndMainMenuBar();
    }

    private void DrawWorkspaceMenuItems()
    {
        foreach (var name in Workspaces.BuiltIn) MenuCommand($"workspace.{name.ToLowerInvariant()}", name);
        var saved = _session.SavedWorkspaceNames().ToList();
        if (saved.Count > 0)
        {
            ImGui.Separator();
            foreach (var name in saved)
                if (ImGui.MenuItem(name, "", Ui.CurrentWorkspace == name)) Ui.PendingWorkspace = name;
        }
        ImGui.Separator();
        MenuCommand("workspace.save");
        MenuCommand("workspace.reset");
    }

    // ---------------------------------------------------------------- canvas

    private void DrawCanvasPanel()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Num.Vector2.Zero);
        bool open = ImGui.Begin(UiState.Canvas, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse);
        ImGui.PopStyleVar();
        if (!open)
        {
            _canvasHovered = _canvasActive = false;
            ImGui.End();
            return;
        }

        DrawAssetBar();
        var io = ImGui.GetIO();
        var doc = Ed.Document;
        var view = Ed.View;
        var origin = ImGui.GetCursorScreenPos();
        float transportH = TransportHeight;
        var avail = Num.Vector2.Max(ImGui.GetContentRegionAvail() - new Num.Vector2(0, transportH), new Num.Vector2(32, 32));
        var center = origin + avail / 2;

        // A fit keeps refitting for a few frames: right after a workspace is built the canvas
        // window's size is still settling.
        if (view.FitRequested) { _fitFrames = 4; view.FitRequested = false; }
        if (_fitFrames > 0 && avail.X > 64)
        {
            _fitFrames--;
            float fit = Math.Min((avail.X - 48) / doc.Width, (avail.Y - 48 - ImGui.GetTextLineHeightWithSpacing()) / doc.Height);
            view.Zoom = CanvasView.ZoomLevels.LastOrDefault(z => z <= fit, 1);
            view.Pan = Num.Vector2.Zero;
        }

        var size = new Num.Vector2(doc.Width, doc.Height) * view.Zoom;
        var imgMin = Floor(center - size / 2 + view.Pan);
        var imgMax = imgMin + size;

        SliceInput(doc, imgMin, view.Zoom, origin, avail); // its tabs and preview take the mouse first
        ImGui.InvisibleButton("##canvas", avail,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        _canvasHovered = ImGui.IsItemHovered();
        _canvasActive = ImGui.IsItemActive();

        var mouse = io.MousePos;
        var docPos = (mouse - imgMin) / view.Zoom;
        int px = (int)Math.Floor(docPos.X);
        int py = (int)Math.Floor(docPos.Y);
        bool inside = px >= 0 && py >= 0 && px < doc.Width && py < doc.Height;
        var mods = CurrentMods();

        if (_canvasHovered && io.MouseWheel != 0) view.Step(io.MouseWheel > 0 ? +1 : -1, mouse - center);

        if (ImGui.IsItemActivated())
        {
            // a pen touching down with its barrel button held arrives here as a right-click
            // (PenAwareImGuiRenderer), so ImGui and the canvas always agree
            bool left = ImGui.IsMouseClicked(ImGuiMouseButton.Left), right = ImGui.IsMouseClicked(ImGuiMouseButton.Right);
            if (left || right) Ed.PointerDown(docPos, right ? PointerButton.Secondary : PointerButton.Primary, mods);
        }
        if (_canvasActive) Ed.PointerMove(docPos, mods);
        if (_canvasHovered || _canvasActive)
            Ed.PointerDrag(io.MouseDelta, ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsMouseDown(ImGuiMouseButton.Middle));
        if (ImGui.IsItemDeactivated()) Ed.PointerUp();

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(origin, origin + avail, true);
        dl.AddRectFilled(origin, origin + avail, Colors.Background);
        dl.AddRectFilled(imgMin + new Num.Vector2(4, 4), imgMax + new Num.Vector2(4, 4), 0x50000000u); // drop shadow
        dl.AddImage(_canvasTextureId, imgMin, imgMax);

        if (view.ShowGrid && view.Zoom >= 8) DrawPixelGrid(dl, imgMin, imgMax, view.Zoom);
        DrawSelectionOverlay(dl, imgMin, view.Zoom, docPos);
        DrawPointsOverlay(dl, imgMin, view.Zoom);
        DrawSliceOverlay(dl, doc, imgMin, view.Zoom, origin, avail);

        // brush outline: red when the current tool can't edit the current layer
        bool allowed = Ed.Momentary != Momentary.None || Ed.CanEditCurrentLayer(Ed.Tool);
        if (_canvasHovered && inside && (Ed.Momentary == Momentary.None || Ed.Momentary == Momentary.Eraser)
            && (Ed.Erasing || Ed.Tool is not EyedropperTool and not ISelectionTool and not PointTool))
        {
            int s = !Ed.Erasing && Ed.Tool is FillTool ? 1 : Ed.Brush.Size;
            int r0 = -(s - 1) / 2;
            var a = imgMin + new Num.Vector2(px + r0, py + r0) * view.Zoom;
            var b = a + new Num.Vector2(s, s) * view.Zoom;
            dl.AddRect(a, b, allowed ? 0xFFFFFFFFu : 0xFF4040FFu, 0, ImDrawFlags.None, allowed ? 1f : 2f);
        }
        // eyedropper: a swatch of the color under the cursor
        if (_canvasHovered && inside && Ed.EffectiveToolName == "Eyedropper")
        {
            var under = ColorUnder(px, py);
            var p0 = mouse + new Num.Vector2(14, 14);
            dl.AddRectFilled(p0, p0 + new Num.Vector2(28, 28), under.HasValue ? Ed.Document.Palette[under.Value].Packed | 0xFF000000u : 0x40FFFFFFu);
            dl.AddRect(p0, p0 + new Num.Vector2(28, 28), 0xFFFFFFFFu);
        }

        // status line along the bottom
        float lh = ImGui.GetTextLineHeightWithSpacing();
        dl.AddRectFilled(new Num.Vector2(origin.X, origin.Y + avail.Y - lh - 6), origin + avail, (ImGui.GetColorU32(ImGuiCol.WindowBg) & 0x00FFFFFFu) | 0xC0000000u);
        var info = $"{Ed.EffectiveToolName}   {Ed.CurrentLayer.Name} ({Ed.CurrentLayer.Role})   frame {Ed.FrameIndex + 1}/{doc.Frames.Count}   {view.Zoom * 100}%   {doc.Width}x{doc.Height}";
        if (inside) info += $"   {px}, {py}";
        dl.AddText(new Num.Vector2(origin.X + 10, origin.Y + avail.Y - lh - 3), ImGui.GetColorU32(ImGuiCol.Text), info);

        // messages along the top
        float y = origin.Y + 8;
        if (!allowed && _canvasHovered)
        {
            dl.AddText(new Num.Vector2(origin.X + 10, y), 0xFF6A6AFFu, Ed.Tool.WhyNot(Ed.CurrentLayer.Role));
            y += lh;
        }
        if (Ed.VisibleStatus is { } status) dl.AddText(new Num.Vector2(origin.X + 10, y), Colors.Accent, status);
        dl.PopClipRect();

        DrawTransport(origin + new Num.Vector2(0, avail.Y), avail.X, transportH);
        ImGui.End();
    }

    private byte? ColorUnder(int x, int y)
    {
        for (int i = Ed.Document.Layers.Count - 1; i >= 0; i--)
        {
            var l = Ed.Document.Layers[i];
            if (!l.Visible || !Ed.Document.TryGetCel(l, Ed.CurrentFrame, out var cel)) continue;
            var idx = cel.Get(x, y);
            if (idx != Palette.Transparent) return idx;
        }
        return null;
    }

    private void DrawPixelGrid(ImDrawListPtr dl, Num.Vector2 min, Num.Vector2 max, int zoom)
    {
        const uint col = 0x22000000;
        var doc = Ed.Document;
        for (int x = 1; x < doc.Width; x++)
        {
            float sx = min.X + x * zoom;
            dl.AddLine(new Num.Vector2(sx, min.Y), new Num.Vector2(sx, max.Y), col);
        }
        for (int y = 1; y < doc.Height; y++)
        {
            float sy = min.Y + y * zoom;
            dl.AddLine(new Num.Vector2(min.X, sy), new Num.Vector2(max.X, sy), col);
        }
    }

    private static Num.Vector2 Floor(Num.Vector2 v) => new(MathF.Floor(v.X), MathF.Floor(v.Y));

    // ---------------------------------------------------------------- tools

    private void DrawToolsPanel()
    {
        float bw = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        int i = 0;
        foreach (var t in Ed.Tools)
        {
            if (i++ % 2 == 1) ImGui.SameLine();
            var id = $"tool.{t.Name.ToLowerInvariant()}";
            var key = _session.Keymap.BindingsOf(id).FirstOrDefault();
            var label = key.Chord.Key != ImGuiKey.None ? $"{t.Name}  {key.Chord}" : t.Name;
            CommandButton(id, label, new Num.Vector2(bw, 0));
        }

        DrawSelectionTools();
        DrawPointsSection();
        DrawPaintSection();

        var ts = Ed.ToolSettings;
        DrawBrush("Brush", ts, !Ed.Erasing, "Pencil and Line.");
        DrawBrush("Eraser", Ed.EraserSettings, Ed.Erasing, "The Eraser tool, the pen's eraser end and Eraser (hold).");

        ImGui.SeparatorText("Fill");
        int gap = ts.GapClose;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##gap", ref gap, 0, 4, "Gap close %d px")) ts.GapClose = gap;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Treats gaps in the line art up to about 2x this many pixels as closed.");
        Toggle("fill.contiguous");
        Toggle("layer.autoSelect");

        ImGui.SeparatorText("Colors");
        Swatch("Primary", Ed.Primary, 40);
        ImGui.SameLine();
        Swatch("Secondary", Ed.Secondary, 28);
        ImGui.SameLine();
        CommandButton("color.swap", "Swap", small: true);
    }

    /// <summary>One brush's settings. The one in use is marked; [ and ] change its size.</summary>
    private void DrawBrush(string name, ToolSettings b, bool inUse, string usedBy)
    {
        ImGui.PushID(name);
        ImGui.SeparatorText(inUse ? $"{name}  (in use)" : name);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(usedBy + " [ and ] change the size of the brush in use.");
        int size = b.Size;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##size", ref size, 1, 32, "Size %d")) b.Size = size;
        bool pp = b.PixelPerfect, sq = b.SquareTip;
        if (ImGui.Checkbox("Pixel Perfect", ref pp)) b.PixelPerfect = pp;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove L-shaped corner pixels from 1px strokes.");
        ImGui.SameLine();
        if (ImGui.Checkbox("Square", ref sq)) b.SquareTip = sq;
        ImGui.PopID();
    }

    /// <summary>A checkbox bound to a toggle command, with the shortcut in its tooltip.</summary>
    private void Toggle(string id)
    {
        var c = _session.Commands.Get(id);
        bool v = c.Checked;
        if (ImGui.Checkbox(c.Title, ref v)) c.Execute();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(TooltipFor(c));
    }

    private void Swatch(string id, byte index, float size)
    {
        var c = Ed.Document.Palette[index];
        ImGui.ColorButton(id, ToVec4(c), ImGuiColorEditFlags.AlphaPreview, new Num.Vector2(size, size));
    }

    private static Num.Vector4 ToVec4(Rgba c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    // ---------------------------------------------------------------- palette

    private int _editingIndex = -1;

    private void DrawPalettePanel()
    {
        DrawVariantPicker();
        ImGui.TextDisabled("Left: primary   Right: secondary   Double-click: edit");
        float avail = ImGui.GetContentRegionAvail().X;
        float cell = 24 * Ui.UiScale;
        float spacing = 4 * Ui.UiScale;
        int perRow = Math.Max(1, (int)((avail + spacing) / (cell + spacing)));

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Num.Vector2(spacing, spacing));
        var pal = Ed.Document.Palette;
        // grouped by ramp, in palette order; entries outside every ramp are "Loose"
        var groups = new List<(PaletteRamp? ramp, List<int> entries)>();
        for (int i = 1; i < pal.Count; i++)
        {
            var ramp = pal.RampOf(i);
            if (groups.Count == 0 || groups[^1].ramp != ramp) groups.Add((ramp, new List<int>()));
            groups[^1].entries.Add(i);
        }
        foreach (var (ramp, entries) in groups)
        {
            ImGui.PushID(ramp?.GetHashCode() ?? entries[0]);
            ImGui.TextDisabled(ramp?.Name ?? "Loose");
            if (ramp != null && ImGui.BeginPopupContextItem("ramp"))
            {
                var name = ramp.Name;
                if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(160 * Ui.UiScale);
                if (ImGui.InputText("Name", ref name, 48) && name.Trim().Length > 0) { ramp.Name = name.Trim(); pal.Touch(); }
                ImGui.TextDisabled($"Entries {ramp.Start}-{ramp.Start + ramp.Count - 1}");
                ImGui.EndPopup();
            }
            if (ramp != null && ImGui.IsItemHovered()) ImGui.SetTooltip("Right-click to rename");
            DrawSwatches(pal, entries, cell, perRow);
            ImGui.PopID();
        }
        ImGui.PopStyleVar();

        if (pal.Count < Palette.MaxEntries && ImGui.Button("+ Color"))
        {
            _editingIndex = pal.Add(Ed.ViewColors[Ed.Primary]);
            Ed.Primary = (byte)_editingIndex;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add one color at the end (outside any ramp).");
        ImGui.SameLine();
        if (pal.Count + 5 <= Palette.MaxEntries && ImGui.Button("+ Ramp"))
        {
            // five shades of the primary color, dark to light
            var c = Ed.ViewColors[Ed.Primary];
            int start = pal.Count;
            foreach (var t in new[] { -0.55f, -0.28f, 0f, 0.3f, 0.6f })
            {
                byte Shade(byte v) => (byte)Math.Clamp(t < 0 ? v * (1 + t) : v + (255 - v) * t, 0, 255);
                pal.Add(new Rgba(Shade(c.R), Shade(c.G), Shade(c.B)));
            }
            int n = 1;
            while (pal.Ramps.Any(r => r.Name == $"Ramp {n}")) n++;
            pal.Ramps.Add(new PaletteRamp($"Ramp {n}", start, 5));
            pal.Touch();
            Ed.Primary = (byte)(start + 2);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add a named ramp of 5 shades made from the primary color.");

        if (_editingIndex > 0 && _editingIndex < pal.Count)
        {
            ImGui.SeparatorText($"Edit #{_editingIndex}");
            var c = Ed.ViewColors[_editingIndex];
            var v = new Num.Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.ColorPicker3("##edit", ref v, ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.DisplayHex))
                pal.Set(Ed.ViewVariant, _editingIndex, new Rgba((byte)(v.X * 255), (byte)(v.Y * 255), (byte)(v.Z * 255)));
            if (ImGui.Button("Done")) _editingIndex = -1;
        }
    }

    private void DrawSwatches(Palette pal, List<int> entries, float cell, int perRow)
    {
        for (int k = 0; k < entries.Count; k++)
        {
            int i = entries[k];
            if (k % perRow != 0) ImGui.SameLine();
            byte idx = (byte)i;
            var pos = ImGui.GetCursorScreenPos();
            ImGui.ColorButton($"##p{i}", ToVec4(Ed.ViewColors[idx]), ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoBorder, new Num.Vector2(cell, cell));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) Ed.Primary = idx;
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) Ed.Secondary = idx;
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) _editingIndex = i;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"#{i}");

            var dl = ImGui.GetWindowDrawList();
            if (idx == Ed.Secondary) dl.AddRect(pos + Num.Vector2.One * 3, pos + new Num.Vector2(cell - 3, cell - 3), 0xFF000000, 0, ImDrawFlags.None, 2);
            if (idx == Ed.Primary) dl.AddRect(pos - Num.Vector2.One, pos + new Num.Vector2(cell + 1, cell + 1), 0xFFFFFFFF, 0, ImDrawFlags.None, 2);
        }
    }

    // ---------------------------------------------------------------- layers

    /// <summary>A layer being dragged to a new place in the stack, and which list it started in.</summary>
    private Layer? _layerDrag;
    private string _layerDragList = "";

    /// <summary>Call right after a layer's name item: dragging it starts moving the layer.</summary>
    private void LayerDragSource(string list, Layer layer)
    {
        if (_layerDrag == null && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 4 * Ui.UiScale))
        {
            _layerDrag = layer;
            _layerDragList = list;
            Ed.LayerIndex = Ed.Document.Layers.IndexOf(layer);
        }
    }

    /// <summary>
    /// Call after a list of layers has been drawn, with each row's layer and top/bottom on screen:
    /// shows where a dragged layer would land, and moves it there on release.
    /// </summary>
    private void LayerDragTarget(string list, List<(Layer layer, float y0, float y1)> rows, float x0, float x1)
    {
        if (_layerDrag is not { } dragged || _layerDragList != list || rows.Count == 0) return;
        var doc = Ed.Document;
        if (!doc.Layers.Contains(dragged)) { _layerDrag = null; return; }
        float my = ImGui.GetMousePos().Y;
        var target = rows.FirstOrDefault(r => my < r.y1);
        if (target.layer == null) target = rows[^1];
        if (my < rows[0].y0) target = rows[0];
        int from = doc.Layers.IndexOf(dragged), to = doc.Layers.IndexOf(target.layer);
        var dl = ImGui.GetWindowDrawList();
        var self = rows.First(r => r.layer == dragged);
        dl.AddRectFilled(new Num.Vector2(x0, self.y0), new Num.Vector2(x1, self.y1), WithAlpha(Colors.Accent, 0x30));
        if (to != from)
        {
            // the stack is drawn top first: moving up the stack lands above the target row
            float y = to > from ? target.y0 : target.y1;
            dl.AddLine(new Num.Vector2(x0, y), new Num.Vector2(x1, y), Colors.Accent, 2 * Ui.UiScale);
        }
        ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
        ImGui.SetTooltip(to == from ? $"Move {dragged.Name}" : $"Move {dragged.Name} {(to > from ? "above" : "below")} {target.layer.Name}");
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _layerDrag = null;
            if (to != from) Ed.MoveLayerTo(dragged, to);
        }
    }

    private void DrawLayersPanel()
    {
        CommandButton("layer.addLine", "+ Line");
        ImGui.SameLine();
        CommandButton("layer.addFill", "+ Fill");
        ImGui.SameLine();
        CommandButton("layer.addReference", "+ Ref");
        CommandButton("layer.moveUp", "Up");
        ImGui.SameLine();
        CommandButton("layer.moveDown", "Down");
        ImGui.SameLine();
        CommandButton("layer.delete", "Delete");
        ImGui.Separator();

        var doc = Ed.Document;
        var rowRects = new List<(Layer, float, float)>();
        float listX0 = ImGui.GetCursorScreenPos().X, listX1 = listX0 + ImGui.GetContentRegionAvail().X;
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        for (int i = doc.Layers.Count - 1; i >= 0; i--) // top of the stack first
        {
            var layer = doc.Layers[i];
            float rowTop = ImGui.GetCursorScreenPos().Y;
            ImGui.PushID(layer.Id.GetHashCode());

            bool visible = layer.Visible;
            if (ImGui.Checkbox("##vis", ref visible)) layer.Visible = visible;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Visible");
            ImGui.SameLine();
            bool locked = layer.Locked;
            if (ImGui.Checkbox("##lock", ref locked)) layer.Locked = locked;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Locked");
            ImGui.SameLine();
            LayerOpacitySlider(layer, 56 * Ui.UiScale);
            ImGui.SameLine();

            var roleCol = layer.Role switch { LayerRole.Line => RoleLine, LayerRole.Fill => RoleFill, _ => RoleRef };
            ImGui.TextColored(roleCol, layer.Role switch { LayerRole.Line => "LINE", LayerRole.Fill => "FILL", _ => "REF " });
            ImGui.SameLine();

            bool usable = Ed.Tool.Allows(layer.Role);
            if (!usable) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            var label = doc.IsFollower(layer) ? $"{layer.Name}   (timing of {doc.TimingLayer(layer).Name})" : layer.Name;
            if (!LayerRenameField(layer, ImGui.GetContentRegionAvail().X))
            {
                if (ImGui.Selectable(label, i == Ed.LayerIndex)) Ed.LayerIndex = i;
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) StartLayerRename(layer);
                LayerDragSource("panel", layer);
                if (ImGui.IsItemHovered() && _layerDrag == null) ImGui.SetTooltip("Drag up or down to move the layer; double-click to rename it.");
            }
            if (!usable) ImGui.PopStyleColor();

            ImGui.PopID();
            rowRects.Add((layer, rowTop - spacing / 2, ImGui.GetCursorScreenPos().Y - spacing / 2));
        }
        LayerDragTarget("panel", rowRects, listX0, listX1);

        ImGui.Spacing();
        ImGui.TextDisabled($"Greyed: {Ed.Tool.Name} can't edit it.");
        DrawFillBoundsMatrix();
    }

    // ---------------------------------------------------------------- timeline (dope sheet)

    private enum BlockGrabKind { Move, Start, End }
    /// <summary>A block being dragged: which hold (From..To on Layer), by what, and where it was grabbed.</summary>
    private sealed record BlockGrab(BlockGrabKind Kind, Layer Layer, int From, int To, int Offset, int PressFrame);
    private BlockGrab? _grab;
    /// <summary>Where the dope sheet's cells were last drawn, for scripts that use the mouse.</summary>
    private (Num.Vector2 P0, float CellW, float RowH, float TagH, float NumH)? _sheet;
    /// <summary>A rectangle of cells picked in the dope sheet: frames From..To on some layers.</summary>
    private sealed record CellSelection(List<Layer> Layers, int From, int To);
    private CellSelection? _cellSel;
    /// <summary>A selection rectangle being dragged out from this cell (Plus: it started on a "+").</summary>
    private sealed record Marquee(int Frame, int Row, bool Plus);
    private Marquee? _marquee;
    /// <summary>The selection being dragged sideways, grabbed on this frame.</summary>
    private int? _selDrag;
    private bool _selMoved;
    /// <summary>Dragging an onion bracket: -1 the before end, +1 the after end.</summary>
    private int? _onionDrag;

    /// <summary>Where the brackets go: the outside of the farthest ghost each way (or the playhead).</summary>
    private (float before, float after) OnionBracketX(Document doc, Layer layer, List<OnionGhost> ghosts, float cellW)
    {
        int b = ghosts.Where(g => g.Kind == OnionKind.Before).Select(g => g.Frame).DefaultIfEmpty(Ed.FrameIndex).Min();
        int a = ghosts.Where(g => g.Kind == OnionKind.After).Select(g => g.Frame).DefaultIfEmpty(Ed.FrameIndex).Max();
        return (b * cellW + 1, (a + 1) * cellW - 1);
    }

    private static int? OnionBracketHit(float x, float before, float after, float sc) =>
        Math.Abs(x - before) <= 6 * sc ? -1 : Math.Abs(x - after) <= 6 * sc ? +1 : null;

    /// <summary>How many drawings, going one way from the playhead, reach as far as <paramref name="frame"/>.</summary>
    private int OnionCountTo(Document doc, Layer layer, int dir, int frame)
    {
        int count = 0;
        for (int k = 1; k <= OnionSettings.MaxCount; k++)
        {
            if (doc.NeighborDrawingFrame(layer, Ed.FrameIndex, dir, k) is not { } f) break;
            if (dir < 0 ? f < frame : f > frame) break;
            count = k;
        }
        return count;
    }

    private Layer? _renamingLayer;
    private string _layerRenameText = "";
    private bool _layerRenameFocus;

    private void StartLayerRename(Layer layer)
    {
        _renamingLayer = layer;
        _layerRenameText = layer.Name;
        _layerRenameFocus = true;
    }

    /// <summary>
    /// While <paramref name="layer"/> is being renamed: a text field in place of its name (Enter
    /// or clicking away keeps it, Esc cancels). Returns false, drawing nothing, otherwise.
    /// </summary>
    private bool LayerRenameField(Layer layer, float width)
    {
        if (Ui.RenameLayer)
        {
            Ui.RenameLayer = false;
            StartLayerRename(Ed.CurrentLayer);
        }
        if (_renamingLayer != layer) return false;
        if (!Ed.Document.Layers.Contains(layer)) { _renamingLayer = null; return false; }
        ImGui.SetNextItemWidth(width);
        if (_layerRenameFocus)
        {
            ImGui.SetKeyboardFocusHere();
            _layerRenameFocus = false;
        }
        bool enter = ImGui.InputText("##rename", ref _layerRenameText, 64, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) _renamingLayer = null;
        else if (enter || ImGui.IsItemDeactivated())
        {
            Ed.RenameLayer(layer, _layerRenameText);
            _renamingLayer = null;
        }
        return true;
    }

    /// <summary>
    /// The layer's working opacity on the canvas (see <see cref="Layer.ViewOpacity"/>): never
    /// exported. Right-click puts it back to 100%.
    /// </summary>
    private void LayerOpacitySlider(Layer layer, float width)
    {
        int pct = (int)MathF.Round(layer.ViewOpacity * 100);
        ImGui.SetNextItemWidth(width);
        bool dimmed = pct < 100;
        if (dimmed) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.ColorConvertU32ToFloat4(Colors.Accent));
        if (ImGui.SliderInt("##viewop", ref pct, 0, 100, "%d%%")) layer.ViewOpacity = pct / 100f;
        if (dimmed) ImGui.PopStyleColor();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) layer.ViewOpacity = 1f;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("View opacity: how see-through this layer is on the canvas while you work.\n" +
                             "Only for you: exports and atlases always show it fully. Right-click: back to 100%.");
    }

    /// <summary>The layer menu's onion part: on or off, and its own counts.</summary>
    private void DrawLayerOnionMenu(Layer layer)
    {
        if (layer.Role == LayerRole.Reference) { ImGui.TextDisabled("Reference layers have no onion skin."); return; }
        bool on = !layer.OnionOff;
        if (ImGui.Checkbox("Onion skin", ref on)) layer.OnionOff = !on;
        bool own = layer.OnionBefore != null || layer.OnionAfter != null;
        if (ImGui.Checkbox("Own counts", ref own))
        {
            layer.OnionBefore = own ? Ed.Onion.Before : null;
            layer.OnionAfter = own ? Ed.Onion.After : null;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Off: this layer uses the counts in the timeline's toolbar.");
        if (!own) return;
        int b = layer.OnionBefore ?? Ed.Onion.Before, a = layer.OnionAfter ?? Ed.Onion.After;
        ImGui.SetNextItemWidth(140 * Ui.UiScale);
        if (ImGui.SliderInt("before", ref b, 0, OnionSettings.MaxCount)) layer.OnionBefore = b;
        ImGui.SetNextItemWidth(140 * Ui.UiScale);
        if (ImGui.SliderInt("after", ref a, 0, OnionSettings.MaxCount)) layer.OnionAfter = a;
    }
    private (PointTrack track, int from)? _keyDrag;
    private enum TagDragKind { Create, Start, End, Move }
    /// <summary>
    /// A tag being made, stretched or slid. Other is the frame of the end that stays put (for a
    /// slide, where it started); Press is the frame it was grabbed on.
    /// </summary>
    private sealed record TagDrag(TagDragKind Kind, Tag? Tag, int Other, int Press = -1);
    private TagDrag? _tagDrag;
    private Tag? _tagMenu;
    private string _tagMenuName = "";
    private bool _openTagMenu;

    private static readonly Num.Vector4[] TagColors =
    {
        new(0.85f, 0.45f, 0.35f, 1), new(0.40f, 0.70f, 0.45f, 1), new(0.45f, 0.55f, 0.90f, 1),
        new(0.80f, 0.65f, 0.30f, 1), new(0.70f, 0.45f, 0.80f, 1), new(0.35f, 0.70f, 0.75f, 1),
    };

    private static Num.Vector4 TagColor(string name)
    {
        int h = 0;
        foreach (var c in name) h = h * 31 + c;
        return TagColors[(h & 0x7FFFFFFF) % TagColors.Length];
    }

    private string NextTagName()
    {
        int i = 1;
        while (Ed.Document.Tags.Any(t => t.Name == $"tag {i}")) i++;
        return $"tag {i}";
    }

    /// <summary>Right-click on a tag: name, loop, play, delete.</summary>
    private void DrawTagMenu()
    {
        var doc = Ed.Document;
        if (_tagMenu == null || !doc.Tags.Contains(_tagMenu))
        {
            ImGui.CloseCurrentPopup();
            return;
        }
        var (a, b) = doc.TagRange(_tagMenu);
        ImGui.TextDisabled($"Tag, frames {a + 1}-{b + 1}");
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(180 * Ui.UiScale);
        ImGui.InputText("Name", ref _tagMenuName, 64);
        if (ImGui.IsItemDeactivatedAfterEdit() && _tagMenuName.Trim().Length > 0)
            _tagMenu = Ed.UpdateTag(_tagMenu, name: _tagMenuName.Trim());
        foreach (var mode in Enum.GetValues<LoopMode>())
        {
            var label = mode switch { LoopMode.PingPong => "Ping-pong", LoopMode.Once => "Play once", _ => "Loop" };
            if (ImGui.RadioButton(label, _tagMenu.Loop == mode)) _tagMenu = Ed.UpdateTag(_tagMenu, loop: mode);
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.Separator();
        if (ImGui.MenuItem("Play"))
        {
            Ed.FrameIndex = a;
            if (Ed.Playing) Ed.TogglePlay();
            Ed.TogglePlay();
        }
        if (ImGui.MenuItem("Delete Tag"))
        {
            Ed.RemoveTag(_tagMenu);
            _tagMenu = null;
            ImGui.CloseCurrentPopup();
        }
    }
    private bool _grabMoved;
    private bool _scrubbing;
    private int _shownFrame = -1;
    private int _fpsEdit;
    private bool _fpsEditing;

    private void DrawTimelinePanel()
    {
        CommandButton("playback.toggle", Ed.Playing ? "Stop" : "Play");
        ImGui.SameLine();
        CommandButton("frame.previous", "<");
        ImGui.SameLine();
        CommandButton("frame.next", ">");
        ImGui.SameLine();
        Toggle("playback.tag");
        ImGui.SameLine();
        if (!_fpsEditing) _fpsEdit = Ed.Document.Fps;
        ImGui.SetNextItemWidth(90 * Ui.UiScale);
        bool stepped = ImGui.InputInt("fps", ref _fpsEdit, 1, 4);
        _fpsEditing = ImGui.IsItemActive();
        // +/- buttons apply at once; typing applies when done
        if ((stepped && !ImGui.IsItemActive()) || ImGui.IsItemDeactivatedAfterEdit()) Ed.SetFps(_fpsEdit);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Frames per second. Every frame lasts the same; holds make drawings last longer.");
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        CommandButton("frame.new", "+ New");
        ImGui.SameLine();
        CommandButton("frame.duplicate", "+ Dup");
        ImGui.SameLine();
        CommandButton("frame.hold", "+ Hold");
        ImGui.SameLine();
        CommandButton("frame.delete", "Delete");
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        CommandButton("drawing.new", "New drawing");
        ImGui.SameLine();
        CommandButton("drawing.duplicate", "Dup drawing");
        ImGui.SameLine();
        CommandButton("hold.shorter", "Hold -");
        ImGui.SameLine();
        CommandButton("hold.longer", "Hold +");

        Toggle("onion.toggle");
        ImGui.SameLine();
        var onion = Ed.Onion;
        ImGui.SetNextItemWidth(90 * Ui.UiScale);
        int before = onion.Before;
        if (ImGui.SliderInt("##before", ref before, 0, OnionSettings.MaxCount, "before %d")) onion.Before = before;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * Ui.UiScale);
        int after = onion.After;
        if (ImGui.SliderInt("##after", ref after, 0, OnionSettings.MaxCount, "after %d")) onion.After = after;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * Ui.UiScale);
        float opacity = onion.Opacity;
        if (ImGui.SliderFloat("##opacity", ref opacity, 0.05f, 0.9f, "opacity %.2f")) onion.Opacity = opacity;
        ImGui.SameLine();
        Toggle("onion.pins");
        ImGui.SameLine();
        var d = Ed.CurrentDrawing;
        var num = Ed.Document.NumberAt(Ed.CurrentLayer, Ed.CurrentFrame);
        ImGui.TextDisabled(num == null ? "   no drawing here"
            : d == null ? $"   #{num} (nothing drawn on this layer yet)"
            : $"   drawing #{d.Number}, held {HoldLength()} frame(s)");

        if (ImGui.BeginChild("xsheet", Num.Vector2.Zero, ImGuiChildFlags.Borders)) DrawDopeSheet();
        ImGui.EndChild();
    }

    private int HoldLength()
    {
        var (s, e) = Ed.Document.RunAt(Ed.CurrentLayer, Ed.FrameIndex);
        return e - s + 1;
    }

    private void DrawDopeSheet()
    {
        var doc = Ed.Document;
        float sc = Ui.UiScale;
        float cellW = 26 * sc, rowH = 24 * sc, tagH = 20 * sc, numH = 20 * sc, headerH = tagH + numH, nameW = 130 * sc;
        int n = doc.Frames.Count, rows = doc.Layers.Count;
        var keyed = doc.Points.Where(t => t.Keyed).ToList();
        Layer LayerOfRow(int r) => doc.Layers[rows - 1 - r]; // top of the stack first
        int RowOf(Layer l) => rows - 1 - doc.Layers.IndexOf(l);
        if (_cellSel is { } stale && (stale.To >= n || stale.Layers.Any(l => !doc.Layers.Contains(l)))) _cellSel = null;

        // ---- layer names
        var namesTop = ImGui.GetCursorScreenPos();
        ImGui.BeginGroup();
        ImGui.Dummy(new Num.Vector2(nameW, headerH));
        for (int r = 0; r < rows; r++)
        {
            var layer = LayerOfRow(r);
            int li = rows - 1 - r;
            ImGui.PushID(layer.Id.GetHashCode());
            ImGui.SetCursorScreenPos(namesTop + new Num.Vector2(0, headerH + r * rowH));
            var col = layer.Role switch { LayerRole.Line => RoleLine, LayerRole.Fill => RoleFill, _ => RoleRef };
            // the onion toggle: filled = on, hollow = off, ringed = own counts
            float ow = rowH - 4;
            var op = ImGui.GetCursorScreenPos();
            if (layer.Role != LayerRole.Reference)
            {
                if (ImGui.InvisibleButton("onion", new Num.Vector2(ow, rowH - 2))) layer.OnionOff = !layer.OnionOff;
                var oc = op + new Num.Vector2(ow / 2, (rowH - 2) / 2);
                var ocol = ImGui.GetColorU32(layer.OnionOff ? ImGuiCol.TextDisabled : ImGuiCol.Text);
                if (layer.OnionOff) ImGui.GetWindowDrawList().AddCircle(oc, ow * 0.22f, ocol, 12, 1.5f);
                else ImGui.GetWindowDrawList().AddCircleFilled(oc, ow * 0.22f, Ed.Onion.BeforeTint.Packed);
                if (layer.OnionBefore != null || layer.OnionAfter != null) ImGui.GetWindowDrawList().AddCircle(oc, ow * 0.36f, ocol, 12, 1f);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(layer.OnionOff ? "Onion skin off for this layer. Click to turn it on."
                        : $"Onion skin on: {Onion.BeforeFor(layer, Ed.Onion)} before, {Onion.AfterFor(layer, Ed.Onion)} after" +
                          (layer.OnionBefore != null || layer.OnionAfter != null ? " (this layer's own)" : "") +
                          ". Click to turn it off; right-click the name for this layer's own counts.");
            }
            else ImGui.Dummy(new Num.Vector2(ow, rowH - 2));
            ImGui.SameLine(0, 0);
            ImGui.PushStyleColor(ImGuiCol.Text, layer.Visible ? col : col * new Num.Vector4(1, 1, 1, 0.4f));
            var label = doc.IsFollower(layer) ? $"   {layer.Name}" : layer.Name;
            bool renaming = LayerRenameField(layer, nameW - ow);
            if (!renaming && ImGui.Selectable(label, li == Ed.LayerIndex, ImGuiSelectableFlags.None, new Num.Vector2(nameW - ow, rowH - 2)))
                Ed.LayerIndex = li;
            if (!renaming && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) StartLayerRename(layer);
            ImGui.PopStyleColor();
            if (!renaming) LayerDragSource("timeline", layer);
            if (ImGui.IsItemHovered() && _layerDrag == null)
                ImGui.SetTooltip(doc.IsFollower(layer)
                    ? $"{layer.Role} layer. Follows the timing of \"{doc.TimingLayer(layer).Name}\". Right-click to change; drag to move it."
                    : $"{layer.Role} layer. Right-click for timing and onion skin; drag up or down to move it; double-click to rename it.");
            if (ImGui.BeginPopupContextItem("timing"))
            {
                Ed.LayerIndex = li;
                if (ImGui.MenuItem("Rename...", "F2")) StartLayerRename(layer);
                ImGui.Separator();
                DrawTimingMenu(layer);
                DrawFillBoundsMenu(layer);
                ImGui.Separator();
                ImGui.TextDisabled("View opacity (canvas only, not exported)");
                LayerOpacitySlider(layer, 140 * Ui.UiScale);
                ImGui.Separator();
                DrawLayerOnionMenu(layer);
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
        // keyed points: a row each, under the layers
        for (int k = 0; k < keyed.Count; k++)
        {
            var t = keyed[k];
            ImGui.PushID(t.Id);
            ImGui.SetCursorScreenPos(namesTop + new Num.Vector2(0, headerH + (rows + k) * rowH));
            ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.9f, 0.82f, 0.3f, 1));
            if (ImGui.Selectable($"* {t.Name}", Ed.SelectedPointId == t.Id, ImGuiSelectableFlags.None, new Num.Vector2(nameW, rowH - 2)))
                Ed.SelectedPointId = t.Id;
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Keyed point \"{t.Name}\": {(t.Smooth ? "moves smoothly" : "holds")} between keys. With the Point tool, a click sets a key on the current frame. Drag a key to retime it; right-click for more.");
            if (ImGui.BeginPopupContextItem("keyedmenu"))
            {
                bool smooth = t.Smooth;
                if (ImGui.Checkbox("Smooth between keys", ref smooth)) DocumentOps.SetSmooth(doc, Ed.History, t, smooth);
                if (ImGui.MenuItem("Delete point")) Ed.RemovePoint(t);
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
        ImGui.EndGroup();
        LayerDragTarget("timeline",
            Enumerable.Range(0, rows).Select(r => (LayerOfRow(r), namesTop.Y + headerH + r * rowH, namesTop.Y + headerH + (r + 1) * rowH)).ToList(),
            namesTop.X, namesTop.X + nameW);
        ImGui.SetCursorScreenPos(namesTop + new Num.Vector2(nameW, 0));

        // ---- the grid
        float gridH = headerH + (rows + keyed.Count) * rowH;
        if (!ImGui.BeginChild("grid", new Num.Vector2(0, gridH + ImGui.GetStyle().ScrollbarSize + 4), ImGuiChildFlags.None,
                ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }
        var p0 = ImGui.GetCursorScreenPos();
        _sheet = (p0, cellW, rowH, tagH, numH);
        float width = (n + 4) * cellW;
        ImGui.InvisibleButton("cells", new Num.Vector2(width, gridH), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
        var mouse = ImGui.GetMousePos() - p0;
        int mf = (int)MathF.Floor(mouse.X / cellW);
        int mr = mouse.Y < headerH ? -1 : (int)MathF.Floor((mouse.Y - headerH) / rowH);
        var dl = ImGui.GetWindowDrawList();

        uint U(Num.Vector4 v) => ImGui.GetColorU32(v);
        var text = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var dim = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
        Num.Vector2 Cell(int f, int r) => p0 + new Num.Vector2(f * cellW, headerH + r * rowH);

        // ---- background: rows, seconds and beats, the current frame
        for (int r = 0; r < rows; r++)
        {
            bool cur = rows - 1 - r == Ed.LayerIndex;
            dl.AddRectFilled(Cell(0, r), Cell(n, r) + new Num.Vector2(0, rowH), cur ? Faint(0x30) : (r % 2 == 0 ? Faint(0x10) : Faint(0x08)));
        }
        for (int f = 0; f <= n; f++)
        {
            bool second = f % doc.Fps == 0;
            bool beat = f % 4 == 0;
            uint lc = second ? Faint(0x70) : beat ? Faint(0x30) : Faint(0x18);
            dl.AddLine(Cell(f, 0) - new Num.Vector2(0, second ? numH : numH * 0.3f), Cell(f, rows), lc);
        }
        dl.AddRectFilled(Cell(Ed.FrameIndex, 0) - new Num.Vector2(0, numH), Cell(Ed.FrameIndex + 1, rows), Colors.Playhead);
        for (int f = 0; f < n; f++)
        {
            bool cur = f == Ed.FrameIndex;
            if (!cur && f % 2 == 1 && f != n - 1 && !doc.IsPinned(f)) continue; // odd frames only, to fit
            var label = (f + 1).ToString();
            var sz = ImGui.CalcTextSize(label);
            dl.AddText(p0 + new Num.Vector2(f * cellW + (cellW - sz.X) / 2, tagH + (numH - sz.Y) / 2), U(cur ? text : dim), label);
        }
        var onionLayer = Ed.CurrentLayer;
        var ghosts = Onion.Plan(doc, onionLayer, Ed.FrameIndex, Ed.Onion);
        float barY = tagH + numH - 4 * sc;
        foreach (var gh in ghosts)
        {
            // a bar under the frame number, as strong as the ghost is
            uint gc = WithAlpha(gh.Tint.Packed, (uint)Math.Clamp(80 + gh.Opacity * 400, 60, 255));
            dl.AddRectFilled(p0 + new Num.Vector2(gh.Frame * cellW + 3 * sc, barY), p0 + new Num.Vector2((gh.Frame + 1) * cellW - 3 * sc, barY + 3 * sc), gc, 1);
        }
        for (int f = 0; f < n; f++)
            if (doc.IsPinned(f))
            {
                // a pin: a little flag at the top of the number
                var pc = p0 + new Num.Vector2(f * cellW + cellW / 2, tagH + 2 * sc);
                dl.AddTriangleFilled(pc + new Num.Vector2(-4 * sc, 0), pc + new Num.Vector2(4 * sc, 0), pc + new Num.Vector2(0, 5 * sc), Ed.Onion.PinTint.Packed);
            }
        // brackets at the ends of the onion range: drag them to show more or fewer drawings
        var (bx0, bx1) = OnionBracketX(doc, onionLayer, ghosts, cellW);
        if (Ed.Onion.Enabled && !onionLayer.OnionOff && onionLayer.Role != LayerRole.Reference)
        {
            float y0 = tagH + 2 * sc, y1 = tagH + numH - 1;
            void Bracket(float x, int dir, Rgba tint)
            {
                uint c = tint.Packed;
                dl.AddLine(p0 + new Num.Vector2(x, y0), p0 + new Num.Vector2(x, y1), c, 2 * sc);
                dl.AddLine(p0 + new Num.Vector2(x, y0), p0 + new Num.Vector2(x + dir * 4 * sc, y0), c, 2 * sc);
                dl.AddLine(p0 + new Num.Vector2(x, y1), p0 + new Num.Vector2(x + dir * 4 * sc, y1), c, 2 * sc);
            }
            Bracket(bx0, +1, Ed.Onion.BeforeTint);
            Bracket(bx1, -1, Ed.Onion.AfterTint);
        }

        // ---- exposures, as blocks: one per hold
        for (int r = 0; r < rows; r++)
        {
            var layer = LayerOfRow(r);
            bool follower = doc.IsFollower(layer);
            var role = layer.Role switch { LayerRole.Line => RoleLine, LayerRole.Fill => RoleFill, _ => RoleRef };
            int f = 0;
            while (f < n)
            {
                var (s, e) = doc.RunAt(layer, f);
                f = e + 1;
                if (doc.NumberAt(layer, doc.Frames[s]) is not { } number) continue;
                bool exists = doc.DrawingAt(layer, doc.Frames[s]) != null;
                var a = Cell(s, r) + new Num.Vector2(1, 2);
                var b = Cell(e + 1, r) + new Num.Vector2(-1, rowH - 2);
                var fillCol = new Num.Vector4(role.X, role.Y, role.Z, exists ? (layer.Visible ? 0.42f : 0.18f) : 0.08f); // reads on dark and light
                dl.AddRectFilled(a, b, U(fillCol), 3 * sc);
                dl.AddRect(a, b, U(role * new Num.Vector4(1, 1, 1, follower ? 0.45f : 0.9f)), 3 * sc);
                var label = number.ToString();
                var sz = ImGui.CalcTextSize(label);
                dl.AddText(new Num.Vector2(a.X + 5 * sc, a.Y + (b.Y - a.Y - sz.Y) / 2), U(exists ? text : dim), label);
                // the traditional hold line through the held frames
                if (e > s)
                {
                    float y = (a.Y + b.Y) / 2;
                    dl.AddLine(new Num.Vector2(a.X + 8 * sc + sz.X, y), new Num.Vector2(b.X - 6 * sc, y), U(text * new Num.Vector4(1, 1, 1, 0.35f)));
                }
                // grips on both ends: drag to change where the hold starts or ends
                dl.AddRectFilled(new Num.Vector2(b.X - 3 * sc, a.Y + 4 * sc), new Num.Vector2(b.X - 1 * sc, b.Y - 4 * sc), Faint(0x60));
                dl.AddRectFilled(new Num.Vector2(a.X + 1 * sc, a.Y + 4 * sc), new Num.Vector2(a.X + 3 * sc, b.Y - 4 * sc), Faint(0x30));
            }
        }
        // ---- keyed points: diamonds on their keys, a line between (solid when smooth)
        for (int k = 0; k < keyed.Count; k++)
        {
            var t = keyed[k];
            int r = rows + k;
            dl.AddRectFilled(Cell(0, r), Cell(n, r) + new Num.Vector2(0, rowH), WithAlpha(Colors.Accent, 0x14));
            var keys = doc.KeysOf(t);
            Num.Vector2? last = null;
            for (int f = 0; f < n; f++)
            {
                if (!keys.ContainsKey(doc.Frames[f])) continue;
                var c = Cell(f, r) + new Num.Vector2(cellW / 2, rowH / 2);
                if (last is { } l)
                    dl.AddLine(l + new Num.Vector2(5 * sc, 0), c - new Num.Vector2(5 * sc, 0), t.Smooth ? WithAlpha(Colors.Accent, 0xC0) : WithAlpha(Colors.Accent, 0x60), t.Smooth ? 2 * sc : 1 * sc);
                float d = 5 * sc;
                bool dragging = _keyDrag is { } kd && kd.track.Id == t.Id && kd.from == f;
                dl.AddQuadFilled(c + new Num.Vector2(0, -d), c + new Num.Vector2(d, 0), c + new Num.Vector2(0, d), c + new Num.Vector2(-d, 0),
                    dragging ? WithAlpha(Colors.Accent, 0x60) : Colors.Accent);
                last = c;
            }
            if (_keyDrag is { } drag && drag.track.Id == t.Id && mf >= 0 && mf < n && mf != drag.from)
            {
                var c = Cell(mf, r) + new Num.Vector2(cellW / 2, rowH / 2);
                float d = 5 * sc;
                dl.AddQuad(c + new Num.Vector2(0, -d), c + new Num.Vector2(d, 0), c + new Num.Vector2(0, d), c + new Num.Vector2(-d, 0), 0xFFFFFFFFu, 2);
            }
        }

        int curRow = rows - 1 - Ed.LayerIndex;
        dl.AddRect(Cell(Ed.FrameIndex, curRow), Cell(Ed.FrameIndex + 1, curRow) + new Num.Vector2(0, rowH), Faint(0xFF), 2 * sc, ImDrawFlags.None, 2);

        // ---- the cell selection: a tinted box on each of its layers
        void SelectionBox(IEnumerable<Layer> layers, int from, int to, bool fill)
        {
            foreach (var l in layers)
            {
                int r = RowOf(l);
                var a = Cell(from, r) + new Num.Vector2(0, 1);
                var b = Cell(to + 1, r) + new Num.Vector2(0, rowH - 1);
                if (fill) dl.AddRectFilled(a, b, WithAlpha(Colors.Accent, 0x38));
                dl.AddRect(a, b, Colors.Accent, 0, ImDrawFlags.None, 2 * sc);
            }
        }
        if (_cellSel is { } shownSel) SelectionBox(shownSel.Layers, shownSel.From, shownSel.To, true);

        // ---- interaction
        bool inGrid = mf >= 0 && mf < n && mr >= 0 && mr < rows;
        float edge = 6 * sc;

        // ---- a "+" on an empty cell, or just past the last frame: click it for a new drawing there
        bool busy = _grab != null || _tagDrag != null || _onionDrag != null || _keyDrag != null || _scrubbing || _layerDrag != null
                    || _marquee != null || _selDrag != null;
        bool plusHit = false;
        if (hovered && !busy && mr >= 0 && mr < rows && mf >= 0 && mf < n + 4 && !ImGui.IsPopupOpen("cell"))
        {
            var layer = LayerOfRow(mr);
            if (!layer.Locked && (mf >= n || doc.ExposureOf(doc.TimingLayer(layer), doc.Frames[mf]) == null))
            {
                var c = Cell(mf, mr) + new Num.Vector2(cellW / 2, rowH / 2);
                float rad = Math.Min(cellW, rowH) * 0.36f, arm = rad * 0.55f;
                plusHit = Num.Vector2.DistanceSquared(ImGui.GetMousePos(), c) <= rad * rad * 1.7f;
                dl.AddCircleFilled(c, rad, plusHit ? Colors.Accent : Faint(0x38));
                uint pc = plusHit ? 0xFF1A1A1Au : Faint(0xD0);
                dl.AddLine(c - new Num.Vector2(arm, 0), c + new Num.Vector2(arm, 0), pc, 2 * sc);
                dl.AddLine(c - new Num.Vector2(0, arm), c + new Num.Vector2(0, arm), pc, 2 * sc);
                if (plusHit)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip(mf < n ? $"New drawing on {layer.Name}, frame {mf + 1}\n(Drag from here to select cells.)"
                        : $"New frame{(mf > n ? "s" : "")} up to {mf + 1}, with a new drawing on {layer.Name}" +
                          (mf > n ? "\nThe frames between hold what each layer shows now." : ""));
                }
            }
        }

        // ---- tags, in the top row
        Tag? tagUnder = null;
        bool tagStartEdge = false, tagEndEdge = false;
        foreach (var tag in doc.Tags)
        {
            var (ta, tb) = doc.TagRange(tag);
            if (ta < 0) continue;
            var a = p0 + new Num.Vector2(ta * cellW + 1, 2 * sc);
            var b = p0 + new Num.Vector2((tb + 1) * cellW - 1, tagH - 2 * sc);
            var col = TagColor(tag.Name);
            bool playing = Ed.Playing && Ed.PlayingTag == tag;
            dl.AddRectFilled(a, b, U(col * new Num.Vector4(1, 1, 1, playing ? 0.95f : 0.6f)), 3 * sc);
            dl.AddRect(a, b, U(col), 3 * sc);
            var loopMark = tag.Loop switch { LoopMode.PingPong => " <>", LoopMode.Once => " >|", _ => "" };
            dl.PushClipRect(a, b, true);
            dl.AddText(new Num.Vector2(a.X + 4 * sc, a.Y + (b.Y - a.Y - ImGui.GetTextLineHeight()) / 2), 0xFFFFFFFFu, tag.Name + loopMark);
            dl.PopClipRect();
            if (mouse.Y >= 0 && mouse.Y < tagH && mouse.X >= ta * cellW && mouse.X < (tb + 1) * cellW)
            {
                tagUnder = tag;
                // generous ends (a pen is not a mouse), but always some middle left to slide it by
                float tagEdge = Math.Min(10 * sc, (b.X - a.X) / 3);
                tagStartEdge = mouse.X < ta * cellW + tagEdge;
                tagEndEdge = !tagStartEdge && mouse.X > (tb + 1) * cellW - tagEdge;
                if (_tagDrag == null && _grab == null)
                {
                    // grips on both ends, so it's clear they can be dragged
                    uint grip = 0xD0FFFFFFu;
                    dl.AddRectFilled(new Num.Vector2(a.X + 2 * sc, a.Y + 3 * sc), new Num.Vector2(a.X + 4 * sc, b.Y - 3 * sc), grip);
                    dl.AddRectFilled(new Num.Vector2(b.X - 4 * sc, a.Y + 3 * sc), new Num.Vector2(b.X - 2 * sc, b.Y - 3 * sc), grip);
                }
            }
        }
        if (_tagDrag is { } td)
        {
            // stretching a tag's end past the last frame adds frames (up to the "+" columns)
            int at = Math.Clamp(mf, 0, td.Kind is TagDragKind.Start or TagDragKind.End ? n + 3 : n - 1);
            int ta, tb;
            if (td.Kind == TagDragKind.Move && td.Tag != null)
            {
                var (oa, ob) = doc.TagRange(td.Tag);
                ta = Math.Clamp(td.Other + at - td.Press, 0, n - 1 - (ob - oa));
                tb = ta + (ob - oa);
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            else (ta, tb) = (Math.Min(at, td.Other), Math.Max(at, td.Other));
            bool moved = td.Kind != TagDragKind.Move || at != td.Press;
            if (moved)
            {
                dl.AddRect(p0 + new Num.Vector2(ta * cellW + 1, 1), p0 + new Num.Vector2((tb + 1) * cellW - 1, tagH - 1), Colors.Accent, 3 * sc, ImDrawFlags.None, 2);
                ImGui.SetTooltip((td.Kind == TagDragKind.Create ? "New tag: " : td.Kind == TagDragKind.Move ? $"Move {td.Tag?.Name} to " : "")
                                 + $"frames {ta + 1}-{tb + 1}" + (tb >= n ? "   (adds frames)" : ""));
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (td.Kind == TagDragKind.Create)
                {
                    if (tb > ta || mf != td.Other)
                    {
                        _tagMenu = Ed.AddTag(NextTagName(), ta, tb);
                        _tagMenuName = _tagMenu.Name;
                        _openTagMenu = true;
                    }
                }
                else if (td.Tag != null && doc.Tags.Contains(td.Tag))
                {
                    if (moved) Ed.UpdateTag(td.Tag, from: ta, to: tb);
                    else Ed.FrameIndex = doc.TagRange(td.Tag).from; // a tap: go to its first frame
                }
                _tagDrag = null;
            }
        }
        else if (hovered && mouse.Y < tagH)
        {
            if (tagUnder != null && (tagStartEdge || tagEndEdge)) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            if (tagUnder != null && _grab == null)
            {
                var (ta, tb) = doc.TagRange(tagUnder);
                ImGui.SetTooltip($"{tagUnder.Name}: frames {ta + 1}-{tb + 1}, {tagUnder.Loop}. Play loops it.\n" +
                                 "Drag it to slide it; drag an end to stretch it (past the last frame adds frames). Right-click to edit.");
            }
            else if (tagUnder == null)
                ImGui.SetTooltip("Drag here to tag frames as an animation (walk, idle...).");
        }

        // what a press here would grab: a block's start edge, end edge, or middle
        BlockGrab? Grab()
        {
            if (!inGrid) return null;
            var layer = LayerOfRow(mr);
            var timing = doc.TimingLayer(layer);
            if (doc.ExposureOf(timing, doc.Frames[mf]) == null) return null;
            var (s, e) = doc.RunAt(timing, mf);
            float inCell = mouse.X - mf * cellW;
            var kind = mf == e && inCell > cellW - edge ? BlockGrabKind.End
                : mf == s && inCell < edge ? BlockGrabKind.Start
                : BlockGrabKind.Move;
            return new BlockGrab(kind, layer, s, e, mf - s, mf);
        }

        if (hovered && _grab == null && Grab() is { } hover)
            ImGui.SetMouseCursor(hover.Kind == BlockGrabKind.Move ? ImGuiMouseCursor.Hand : ImGuiMouseCursor.ResizeEW);

        // double-tap the frames or cells: onion skin on/off
        bool doubleTap = hovered && mouse.Y >= tagH && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)
                         && OnionBracketHit(mouse.X, bx0, bx1, sc) == null && _marquee?.Plus != true;
        if (doubleTap)
        {
            _grab = null;
            _marquee = null;
            _selDrag = null;
            _scrubbing = false;
            _session.Commands.Run("onion.toggle");
            Ed.SetStatus(Ed.Onion.Enabled ? "Onion skin on" : "Onion skin off", 1.5);
        }
        else if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (Ed.Playing) Ed.TogglePlay();
            // the "+" acts on release, so a drag from it can select cells instead
            if (plusHit) _marquee = new Marquee(mf, mr, Plus: true);
            else if (mouse.Y < tagH)
            {
                if (tagUnder != null && (tagStartEdge || tagEndEdge))
                {
                    var (ta, tb) = doc.TagRange(tagUnder);
                    _tagDrag = new TagDrag(tagStartEdge ? TagDragKind.Start : TagDragKind.End, tagUnder, tagStartEdge ? tb : ta);
                }
                else if (tagUnder != null) _tagDrag = new TagDrag(TagDragKind.Move, tagUnder, doc.TagRange(tagUnder).from, Math.Clamp(mf, 0, n - 1));
                else if (mf >= 0 && mf < n) _tagDrag = new TagDrag(TagDragKind.Create, null, mf);
            }
            else if (mr < 0 && OnionBracketHit(mouse.X, bx0, bx1, sc) is { } side && Ed.Onion.Enabled && !onionLayer.OnionOff)
                _onionDrag = side;
            else if (mr < 0) _scrubbing = true;
            else if (mr >= rows && mr < rows + keyed.Count && mf >= 0 && mf < n)
            {
                var t = keyed[mr - rows];
                Ed.FrameIndex = mf;
                Ed.SelectedPointId = t.Id;
                if (doc.KeysOf(t).ContainsKey(doc.Frames[mf])) _keyDrag = (t, mf);
            }
            else if (inGrid)
            {
                Ed.FrameIndex = mf;
                Ed.LayerIndex = rows - 1 - mr;
                bool shift = ImGui.GetIO().KeyShift;
                if (!shift && _cellSel is { } cs && cs.Layers.Contains(LayerOfRow(mr)) && mf >= cs.From && mf <= cs.To)
                {
                    _selDrag = mf; // drag the selection sideways
                    _selMoved = false;
                }
                else
                {
                    _cellSel = null;
                    // a block's middle or ends: move or stretch it; empty cells (or Shift): select
                    _grab = shift ? null : Grab();
                    _grabMoved = false;
                    if (_grab == null) _marquee = new Marquee(mf, mr, Plus: false);
                }
            }
        }
        if (active && _scrubbing && mf >= 0) Ed.FrameIndex = Math.Min(mf, n - 1);
        if (_onionDrag is { } od)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            int count = OnionCountTo(doc, onionLayer, od < 0 ? -1 : +1, Math.Clamp(mf, 0, n - 1));
            bool own = od < 0 ? onionLayer.OnionBefore != null : onionLayer.OnionAfter != null;
            if (od < 0) { if (own) onionLayer.OnionBefore = count; else Ed.Onion.Before = count; }
            else { if (own) onionLayer.OnionAfter = count; else Ed.Onion.After = count; }
            ImGui.SetTooltip($"{count} drawing{(count == 1 ? "" : "s")} {(od < 0 ? "before" : "after")}{(own ? $" (\"{onionLayer.Name}\" only)" : "")}");
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _onionDrag = null;
        }
        else if (hovered && mr < 0 && mouse.Y >= tagH && OnionBracketHit(mouse.X, bx0, bx1, sc) != null && Ed.Onion.Enabled)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            ImGui.SetTooltip("Drag to onion-skin more or fewer drawings.");
        }
        // right-click a frame number: pin it
        if (hovered && mr < 0 && mouse.Y >= tagH && mf >= 0 && mf < n && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            doc.TogglePin(mf);
            Ed.SetStatus(doc.IsPinned(mf) ? $"Frame {mf + 1} pinned: it shows (tinted) from every frame" : $"Frame {mf + 1} unpinned", 2);
        }
        else if (hovered && mr < 0 && mouse.Y >= tagH && _onionDrag == null && OnionBracketHit(mouse.X, bx0, bx1, sc) == null && mf >= 0 && mf < n)
            ImGui.SetTooltip(doc.IsPinned(mf) ? $"Frame {mf + 1} is pinned. Right-click to unpin." : $"Frame {mf + 1}. Right-click to pin it for onion skin.");
        if (_keyDrag is { } kdrag && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _keyDrag = null;
            int to = Math.Clamp(mf, 0, n - 1);
            if (to != kdrag.from && doc.Points.Contains(kdrag.track) &&
                DocumentOps.MoveKey(doc, Ed.History, kdrag.track, kdrag.from, to)) Ed.FrameIndex = to;
        }
        if (hovered && mr >= rows && mr < rows + keyed.Count && mf >= 0 && mf < n)
        {
            var t = keyed[mr - rows];
            bool isKey = doc.KeysOf(t).ContainsKey(doc.Frames[mf]);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && isKey)
                DocumentOps.SetPoint(doc, Ed.History, t, mf, null);
            else if (_keyDrag == null && _grab == null)
                ImGui.SetTooltip(isKey ? $"{t.Name} key on frame {mf + 1}: drag to move it, right-click to delete it."
                    : $"{t.Name}, frame {mf + 1}: Point tool + click on the canvas sets a key here.");
        }

        // a block grabbed by its middle, then dragged up or down before sideways: select cells instead
        if (_grab is { Kind: BlockGrabKind.Move } g0 && !_grabMoved && mr >= 0 && mr < rows && doc.Layers.Contains(g0.Layer)
            && LayerOfRow(mr) != g0.Layer && Math.Max(0, mf) == g0.PressFrame)
        {
            _marquee = new Marquee(g0.PressFrame, RowOf(g0.Layer), Plus: false);
            _grab = null;
        }
        if (_marquee is { } mq)
        {
            int f1 = Math.Clamp(mf, 0, n - 1), r1 = Math.Clamp(mr, 0, rows - 1);
            int f0 = Math.Clamp(mq.Frame, 0, n - 1);
            int fa = Math.Min(f0, f1), fb = Math.Max(f0, f1), ra = Math.Min(mq.Row, r1), rb = Math.Max(mq.Row, r1);
            bool tap = mf == mq.Frame && mr == mq.Row;
            if (!tap)
            {
                var a = Cell(fa, ra);
                var b = Cell(fb + 1, rb) + new Num.Vector2(0, rowH);
                dl.AddRectFilled(a, b, WithAlpha(Colors.Accent, 0x28));
                dl.AddRect(a, b, Colors.Accent, 0, ImDrawFlags.None, 2 * sc);
                ImGui.SetTooltip($"{fb - fa + 1} frame{(fb > fa ? "s" : "")} on {rb - ra + 1} layer{(rb > ra ? "s" : "")}. Drag the selection to move it (Ctrl: copy).");
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (tap && mq.Plus) Ed.NewDrawingAt(LayerOfRow(mq.Row), mq.Frame);
                else if (!tap) _cellSel = new CellSelection(Enumerable.Range(ra, rb - ra + 1).Select(LayerOfRow).ToList(), fa, fb);
                _marquee = null;
            }
        }
        if (_selDrag is { } press && _cellSel is { } sel)
        {
            int at = Math.Max(0, mf);
            _selMoved |= at != press;
            bool copy = ImGui.GetIO().KeyCtrl;
            int nf = Math.Max(0, sel.From + at - press), len = sel.To - sel.From;
            if (_selMoved)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                SelectionBox(sel.Layers, nf, nf + len, false);
                ImGui.SetTooltip($"{(copy ? "Copy" : "Move")} frames {sel.From + 1}-{sel.To + 1} to {nf + 1}-{nf + len + 1}"
                                 + (nf + len >= n ? "   (adds frames)" : "") + (copy ? "" : "   (Ctrl: copy)"));
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (!_selMoved) _cellSel = null; // a tap inside it: back to one cell
                else if (Ed.MoveCells(sel.Layers, sel.From, sel.To, nf, copy)) _cellSel = sel with { From = nf, To = nf + len };
                _selDrag = null;
            }
        }
        else _selDrag = null;
        if (_cellSel != null && hovered && ImGui.IsKeyPressed(ImGuiKey.Escape)) _cellSel = null;

        if (_grab is { } g)
        {
            int at = Math.Max(0, mf);
            if (at != g.PressFrame || g.Kind != BlockGrabKind.Move) _grabMoved |= at != g.PressFrame;
            bool copy = ImGui.GetIO().KeyCtrl && g.Kind == BlockGrabKind.Move;
            var (ns, ne) = g.Kind switch
            {
                BlockGrabKind.End => (g.From, Math.Max(g.From, at)),
                BlockGrabKind.Start => (Math.Min(g.To, at), g.To),
                _ => (Math.Max(0, at - g.Offset), Math.Max(0, at - g.Offset) + (g.To - g.From)),
            };
            if (_grabMoved && doc.Layers.Contains(g.Layer))
            {
                int r = rows - 1 - doc.Layers.IndexOf(g.Layer);
                if (g.Kind == BlockGrabKind.Move && !copy)
                    dl.AddRectFilled(Cell(g.From, r) + new Num.Vector2(1, 2), Cell(g.To + 1, r) + new Num.Vector2(-1, rowH - 2), 0xA0202020u, 3 * sc);
                dl.AddRect(Cell(ns, r) + new Num.Vector2(1, 1), Cell(ne + 1, r) + new Num.Vector2(-1, rowH - 1), Colors.Accent, 3 * sc, ImDrawFlags.None, 2);
                var num = doc.NumberAt(g.Layer, doc.Frames[g.From]);
                ImGui.SetTooltip(g.Kind == BlockGrabKind.Move
                    ? $"{(copy ? "Copy" : "Move")} #{num} to frames {ns + 1}-{ne + 1}" + (copy ? "" : "   (Ctrl: copy)")
                    : $"Hold #{num} for {ne - ns + 1} frame(s)" + (ne >= n ? "   (adds frames)" : ""));
                ImGui.SetMouseCursor(g.Kind == BlockGrabKind.Move ? ImGuiMouseCursor.Hand : ImGuiMouseCursor.ResizeEW);
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (_grabMoved && doc.Layers.Contains(g.Layer))
                {
                    Ed.PlaceHold(g.Layer, g.From, g.To, ns, ne, copy);
                    Ed.LayerIndex = doc.Layers.IndexOf(g.Layer);
                }
                _grab = null;
            }
        }
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _scrubbing = false;

        if (hovered && tagUnder != null && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _tagMenu = tagUnder;
            _tagMenuName = tagUnder.Name;
            _openTagMenu = true;
        }
        if (_openTagMenu)
        {
            ImGui.OpenPopup("tag");
            _openTagMenu = false;
        }
        if (ImGui.BeginPopup("tag"))
        {
            DrawTagMenu();
            ImGui.EndPopup();
        }

        if (hovered && inGrid && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            Ed.FrameIndex = mf;
            Ed.LayerIndex = rows - 1 - mr;
            ImGui.OpenPopup("cell");
        }
        if (ImGui.BeginPopup("cell"))
        {
            DrawCellMenu();
            ImGui.EndPopup();
        }
        else if (hovered && inGrid && _grab == null && _marquee == null && _selDrag == null && !plusHit)
        {
            var layer = LayerOfRow(mr);
            var number = doc.NumberAt(layer, doc.Frames[mf]);
            var (s, e) = doc.RunAt(layer, mf);
            ImGui.SetTooltip(number == null
                ? $"{layer.Name}, frame {mf + 1}: empty"
                : $"{layer.Name}, frame {mf + 1}: drawing #{number}" + (e > s ? $" (held frames {s + 1}-{e + 1})" : ""));
        }

        // keep the current frame in view (playback, keys)
        if (_shownFrame != Ed.FrameIndex)
        {
            _shownFrame = Ed.FrameIndex;
            float x = Ed.FrameIndex * cellW, scroll = ImGui.GetScrollX(), view = ImGui.GetWindowWidth();
            if (x < scroll) ImGui.SetScrollX(x);
            else if (x + cellW > scroll + view) ImGui.SetScrollX(x + cellW - view);
        }
        ImGui.EndChild();
    }

    /// <summary>Right-click on a cell: drawings, reuse, clearing, timing.</summary>
    private void DrawCellMenu()
    {
        var doc = Ed.Document;
        var layer = Ed.CurrentLayer;
        var timing = doc.TimingLayer(layer);
        var number = doc.NumberAt(layer, Ed.CurrentFrame);
        ImGui.TextDisabled($"{layer.Name}, frame {Ed.FrameIndex + 1}");
        ImGui.Separator();
        MenuCommand("drawing.new");
        MenuCommand("drawing.duplicate");
        if (ImGui.BeginMenu("Show Drawing", timing.Drawings.Count > 0))
        {
            foreach (var d in timing.Drawings.OrderBy(d => d.Number))
                if (ImGui.MenuItem($"#{d.Number}", "", d.Number == number))
                    Ed.SetCell(Ed.FrameIndex, layer, d.Number);
            ImGui.EndMenu();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reuse a drawing on this frame, for cycles and repeats.");
        MenuCommand("cell.clear");
        ImGui.Separator();
        MenuCommand("hold.longer");
        MenuCommand("hold.shorter");
        MenuCommand("frame.hold");
        ImGui.Separator();
        if (ImGui.BeginMenu("Timing"))
        {
            DrawTimingMenu(layer);
            ImGui.EndMenu();
        }
    }

    /// <summary>Own timing, or follow another layer's (fill drawing N goes with line drawing N).</summary>
    private void DrawTimingMenu(Layer layer)
    {
        var doc = Ed.Document;
        if (ImGui.MenuItem("Own timing", "", !doc.IsFollower(layer))) Ed.SetFollows(layer, null);
        foreach (var other in doc.Layers.Where(l => l != layer && !doc.IsFollower(l)).Reverse())
            if (ImGui.MenuItem($"Follow {other.Name}", "", doc.TimingLayer(layer) == other))
                Ed.SetFollows(layer, other);
    }
}
