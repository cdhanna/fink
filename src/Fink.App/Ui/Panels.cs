using Fink.Core;
using Fink.Editor;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

public sealed partial class FinkGame
{
    private static readonly Num.Vector4 RoleLine = new(0.55f, 0.75f, 1f, 1f);
    private static readonly Num.Vector4 RoleFill = new(1f, 0.75f, 0.45f, 1f);
    private static readonly Num.Vector4 RoleRef = new(0.6f, 0.6f, 0.6f, 1f);
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
        var avail = Num.Vector2.Max(ImGui.GetContentRegionAvail(), new Num.Vector2(32, 32));
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
            bool left = ImGui.IsMouseClicked(ImGuiMouseButton.Left), right = ImGui.IsMouseClicked(ImGuiMouseButton.Right);
            if (left || right) Ed.PointerDown(docPos, right ? PointerButton.Secondary : PointerButton.Primary, mods);
        }
        if (_canvasActive) Ed.PointerMove(docPos, mods);
        if (_canvasHovered || _canvasActive)
            Ed.PointerDrag(io.MouseDelta, ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsMouseDown(ImGuiMouseButton.Middle));
        if (ImGui.IsItemDeactivated()) Ed.PointerUp();

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(origin, origin + avail, true);
        dl.AddRectFilled(origin, origin + avail, 0xFF1E1A1Cu);
        dl.AddRectFilled(imgMin + new Num.Vector2(4, 4), imgMax + new Num.Vector2(4, 4), 0x50000000u); // drop shadow
        dl.AddImage(_canvasTextureId, imgMin, imgMax);

        if (view.ShowGrid && view.Zoom >= 8) DrawPixelGrid(dl, imgMin, imgMax, view.Zoom);
        DrawSelectionOverlay(dl, imgMin, view.Zoom, docPos);
        DrawPointsOverlay(dl, imgMin, view.Zoom);

        // brush outline: red when the current tool can't edit the current layer
        bool allowed = Ed.Momentary != Momentary.None || Ed.CanEditCurrentLayer(Ed.Tool);
        if (_canvasHovered && inside && Ed.Momentary == Momentary.None && Ed.Tool is not EyedropperTool and not ISelectionTool)
        {
            int s = Ed.Tool is FillTool ? 1 : Ed.ToolSettings.Size;
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
        dl.AddRectFilled(new Num.Vector2(origin.X, origin.Y + avail.Y - lh - 6), origin + avail, 0xC0141216u);
        var info = $"{Ed.EffectiveToolName}   {Ed.CurrentLayer.Name} ({Ed.CurrentLayer.Role})   frame {Ed.FrameIndex + 1}/{doc.Frames.Count}   {view.Zoom * 100}%   {doc.Width}x{doc.Height}";
        if (inside) info += $"   {px}, {py}";
        dl.AddText(new Num.Vector2(origin.X + 10, origin.Y + avail.Y - lh - 3), 0xFFBBBBBBu, info);

        // messages along the top
        float y = origin.Y + 8;
        if (!allowed && _canvasHovered)
        {
            dl.AddText(new Num.Vector2(origin.X + 10, y), 0xFF6A6AFFu, Ed.Tool.WhyNot(Ed.CurrentLayer.Role));
            y += lh;
        }
        if (Ed.VisibleStatus is { } status) dl.AddText(new Num.Vector2(origin.X + 10, y), 0xFF60D0FFu, status);
        dl.PopClipRect();

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

        ImGui.SeparatorText("Brush");
        var ts = Ed.ToolSettings;
        int size = ts.Size;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##size", ref size, 1, 32, "Size %d")) ts.Size = size;
        Toggle("brush.pixelPerfect");
        Toggle("brush.squareTip");

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
        for (int i = doc.Layers.Count - 1; i >= 0; i--) // top of the stack first
        {
            var layer = doc.Layers[i];
            ImGui.PushID(layer.Id.GetHashCode());

            bool visible = layer.Visible;
            if (ImGui.Checkbox("##vis", ref visible)) layer.Visible = visible;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Visible");
            ImGui.SameLine();
            bool locked = layer.Locked;
            if (ImGui.Checkbox("##lock", ref locked)) layer.Locked = locked;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Locked");
            ImGui.SameLine();

            var roleCol = layer.Role switch { LayerRole.Line => RoleLine, LayerRole.Fill => RoleFill, _ => RoleRef };
            ImGui.TextColored(roleCol, layer.Role switch { LayerRole.Line => "LINE", LayerRole.Fill => "FILL", _ => "REF " });
            ImGui.SameLine();

            bool usable = Ed.Tool.Allows(layer.Role);
            if (!usable) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            var label = doc.IsFollower(layer) ? $"{layer.Name}   (timing of {doc.TimingLayer(layer).Name})" : layer.Name;
            if (ImGui.Selectable(label, i == Ed.LayerIndex)) Ed.LayerIndex = i;
            if (!usable) ImGui.PopStyleColor();

            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.TextDisabled($"Greyed: {Ed.Tool.Name} can't edit it.");
    }

    // ---------------------------------------------------------------- timeline (dope sheet)

    private enum BlockGrabKind { Move, Start, End }
    /// <summary>A block being dragged: which hold (From..To on Layer), by what, and where it was grabbed.</summary>
    private sealed record BlockGrab(BlockGrabKind Kind, Layer Layer, int From, int To, int Offset, int PressFrame);
    private BlockGrab? _grab;
    private (PointTrack track, int from)? _keyDrag;
    private enum TagDragKind { Create, Start, End }
    /// <summary>A tag being made or resized; Other is the frame of the end that stays put.</summary>
    private sealed record TagDrag(TagDragKind Kind, Tag? Tag, int Other);
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
        if (ImGui.SliderInt("##before", ref before, 0, 4, "before %d")) onion.Before = before;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * Ui.UiScale);
        int after = onion.After;
        if (ImGui.SliderInt("##after", ref after, 0, 4, "after %d")) onion.After = after;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * Ui.UiScale);
        float opacity = onion.Opacity;
        if (ImGui.SliderFloat("##opacity", ref opacity, 0.05f, 0.9f, "opacity %.2f")) onion.Opacity = opacity;
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
            ImGui.PushStyleColor(ImGuiCol.Text, layer.Visible ? col : col * new Num.Vector4(1, 1, 1, 0.4f));
            var label = doc.IsFollower(layer) ? $"   {layer.Name}" : layer.Name;
            if (ImGui.Selectable(label, li == Ed.LayerIndex, ImGuiSelectableFlags.None, new Num.Vector2(nameW, rowH - 2)))
                Ed.LayerIndex = li;
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(doc.IsFollower(layer)
                    ? $"{layer.Role} layer. Follows the timing of \"{doc.TimingLayer(layer).Name}\". Right-click to change."
                    : $"{layer.Role} layer. Right-click for timing.");
            if (ImGui.BeginPopupContextItem("timing"))
            {
                Ed.LayerIndex = li;
                DrawTimingMenu(layer);
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
            dl.AddRectFilled(Cell(0, r), Cell(n, r) + new Num.Vector2(0, rowH), cur ? 0x30FFFFFFu : (r % 2 == 0 ? 0x10FFFFFFu : 0x08FFFFFFu));
        }
        for (int f = 0; f <= n; f++)
        {
            bool second = f % doc.Fps == 0;
            bool beat = f % 4 == 0;
            uint lc = second ? 0x70FFFFFFu : beat ? 0x30FFFFFFu : 0x18FFFFFFu;
            dl.AddLine(Cell(f, 0) - new Num.Vector2(0, second ? numH : numH * 0.3f), Cell(f, rows), lc);
        }
        dl.AddRectFilled(Cell(Ed.FrameIndex, 0) - new Num.Vector2(0, numH), Cell(Ed.FrameIndex + 1, rows), 0x2880D0FFu);
        for (int f = 0; f < n; f++)
        {
            bool cur = f == Ed.FrameIndex;
            if (!cur && f % 2 == 1 && f != n - 1) continue; // odd frames only, to fit
            var label = (f + 1).ToString();
            var sz = ImGui.CalcTextSize(label);
            dl.AddText(p0 + new Num.Vector2(f * cellW + (cellW - sz.X) / 2, tagH + (numH - sz.Y) / 2), U(cur ? text : dim), label);
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
                var fillCol = role * new Num.Vector4(0.5f, 0.5f, 0.5f, exists ? (layer.Visible ? 0.9f : 0.35f) : 0.12f);
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
                dl.AddRectFilled(new Num.Vector2(b.X - 3 * sc, a.Y + 4 * sc), new Num.Vector2(b.X - 1 * sc, b.Y - 4 * sc), 0x60FFFFFFu);
                dl.AddRectFilled(new Num.Vector2(a.X + 1 * sc, a.Y + 4 * sc), new Num.Vector2(a.X + 3 * sc, b.Y - 4 * sc), 0x30FFFFFFu);
            }
        }
        // ---- keyed points: diamonds on their keys, a line between (solid when smooth)
        for (int k = 0; k < keyed.Count; k++)
        {
            var t = keyed[k];
            int r = rows + k;
            dl.AddRectFilled(Cell(0, r), Cell(n, r) + new Num.Vector2(0, rowH), 0x14F0D040u);
            var keys = doc.KeysOf(t);
            Num.Vector2? last = null;
            for (int f = 0; f < n; f++)
            {
                if (!keys.ContainsKey(doc.Frames[f])) continue;
                var c = Cell(f, r) + new Num.Vector2(cellW / 2, rowH / 2);
                if (last is { } l)
                    dl.AddLine(l + new Num.Vector2(5 * sc, 0), c - new Num.Vector2(5 * sc, 0), t.Smooth ? 0xC040D0F0u : 0x6040D0F0u, t.Smooth ? 2 * sc : 1 * sc);
                float d = 5 * sc;
                bool dragging = _keyDrag is { } kd && kd.track.Id == t.Id && kd.from == f;
                dl.AddQuadFilled(c + new Num.Vector2(0, -d), c + new Num.Vector2(d, 0), c + new Num.Vector2(0, d), c + new Num.Vector2(-d, 0),
                    dragging ? 0x6040D0F0u : 0xFF40D0F0u);
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
        dl.AddRect(Cell(Ed.FrameIndex, curRow), Cell(Ed.FrameIndex + 1, curRow) + new Num.Vector2(0, rowH), 0xFFFFFFFFu, 2 * sc, ImDrawFlags.None, 2);

        // ---- interaction
        bool inGrid = mf >= 0 && mf < n && mr >= 0 && mr < rows;
        float edge = 6 * sc;

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
                tagStartEdge = mouse.X < ta * cellW + edge;
                tagEndEdge = mouse.X > (tb + 1) * cellW - edge;
            }
        }
        if (_tagDrag is { } td)
        {
            int at = Math.Clamp(mf, 0, n - 1);
            var (ta, tb) = td.Kind switch
            {
                TagDragKind.Start => (Math.Min(at, td.Other), Math.Max(at, td.Other)),
                TagDragKind.End => (Math.Min(at, td.Other), Math.Max(at, td.Other)),
                _ => (Math.Min(at, td.Other), Math.Max(at, td.Other)),
            };
            dl.AddRect(p0 + new Num.Vector2(ta * cellW + 1, 1), p0 + new Num.Vector2((tb + 1) * cellW - 1, tagH - 1), 0xFF40D0FFu, 3 * sc, ImDrawFlags.None, 2);
            ImGui.SetTooltip(td.Kind == TagDragKind.Create ? $"New tag: frames {ta + 1}-{tb + 1}" : $"Frames {ta + 1}-{tb + 1}");
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
                    Ed.UpdateTag(td.Tag, from: ta, to: tb);
                _tagDrag = null;
            }
        }
        else if (hovered && mouse.Y < tagH)
        {
            if (tagUnder != null && (tagStartEdge || tagEndEdge)) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            if (tagUnder != null && _grab == null)
            {
                var (ta, tb) = doc.TagRange(tagUnder);
                ImGui.SetTooltip($"{tagUnder.Name}: frames {ta + 1}-{tb + 1}, {tagUnder.Loop}. Play loops it. Right-click to edit; drag its ends to change it.");
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

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (Ed.Playing) Ed.TogglePlay();
            if (mouse.Y < tagH)
            {
                if (tagUnder != null && (tagStartEdge || tagEndEdge))
                {
                    var (ta, tb) = doc.TagRange(tagUnder);
                    _tagDrag = new TagDrag(tagStartEdge ? TagDragKind.Start : TagDragKind.End, tagUnder, tagStartEdge ? tb : ta);
                }
                else if (tagUnder != null) Ed.FrameIndex = doc.TagRange(tagUnder).from;
                else if (mf >= 0 && mf < n) _tagDrag = new TagDrag(TagDragKind.Create, null, mf);
            }
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
                _grab = Grab();
                _grabMoved = false;
            }
        }
        if (active && _scrubbing && mf >= 0) Ed.FrameIndex = Math.Min(mf, n - 1);
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
                dl.AddRect(Cell(ns, r) + new Num.Vector2(1, 1), Cell(ne + 1, r) + new Num.Vector2(-1, rowH - 1), 0xFF40D0FFu, 3 * sc, ImDrawFlags.None, 2);
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
        else if (hovered && inGrid && _grab == null)
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
