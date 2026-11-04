using Fink.Core;
using Fink.Editor;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

/// <summary>Tools-panel sections for points and data channels, and the point markers on the canvas.</summary>
public sealed partial class FinkGame
{
    private string _newPointName = "";
    private bool _newPointKeyed;
    private string _newChannelName = "";
    private ChannelKind _newChannelKind;

    // ---------------------------------------------------------------- what painting goes to

    private void DrawPaintSection()
    {
        var project = Ed.Project;
        float sc = Ui.UiScale;
        ImGui.SeparatorText("Paint");
        var active = Ed.ActiveChannel;
        ImGui.SetNextItemWidth(-60 * sc);
        if (ImGui.BeginCombo("##paint", active == null ? "Colors" : $"{active.Name} ({active.Kind.ToString().ToLowerInvariant()})"))
        {
            if (ImGui.Selectable("Colors", active == null)) Ed.ActiveChannelId = 0;
            foreach (var ch in project.Channels)
                if (ImGui.Selectable($"{ch.Name} ({ch.Kind.ToString().ToLowerInvariant()})##{ch.Id}", ch == active)) Ed.ActiveChannelId = ch.Id;
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Paint colors, or data on the drawings (C switches). Data exports as atlas maps.");
        if (active != null && ImGui.BeginPopupContextItem("channelmenu"))
        {
            if (ImGui.MenuItem($"Remove \"{active.Name}\" from the project")) Ed.RemoveChannel(active);
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("+##channel")) { _newChannelName = ""; ImGui.OpenPopup("newchannel"); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("New data channel");
        if (ImGui.BeginPopup("newchannel"))
        {
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(160 * sc);
            ImGui.InputTextWithHint("##name", "name, e.g. glow", ref _newChannelName, 32);
            if (ImGui.RadioButton("value", _newChannelKind == ChannelKind.Value)) _newChannelKind = ChannelKind.Value;
            ImGui.SameLine();
            if (ImGui.RadioButton("normal", _newChannelKind == ChannelKind.Normal)) _newChannelKind = ChannelKind.Normal;
            if ((ImGui.Button("Add") || ImGui.IsKeyPressed(ImGuiKey.Enter)) && _newChannelName.Trim().Length > 0)
            {
                Ed.AddChannel(_newChannelName, _newChannelKind);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (active == null) return;
        if (active.Kind == ChannelKind.Value)
        {
            int v = Ed.ValueBrush;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderInt("##value", ref v, 0, 255, "value %d")) Ed.ValueBrush = (byte)v;
            foreach (var preset in new[] { 64, 128, 192, 255 })
            {
                if (ImGui.SmallButton($"{preset}")) Ed.ValueBrush = (byte)preset;
                ImGui.SameLine();
            }
            ImGui.NewLine();
            ImGui.TextDisabled("Right-click paints 0 (none). Fill fills one shape.");
        }
        else
        {
            // a compass of directions: middle faces you, the ring tilts 45°, the outer ring 75°
            ImGui.TextDisabled($"Direction: {Normals.NameOf(Ed.NormalBrush)}");
            float cell = 22 * sc;
            // indices: 2 right, 3 down-right, 4 down, 5 down-left, 6 left, 7 up-left, 8 up, 9 up-right (+8 steep)
            int[,] map =
            {
                { 15, 0, 16, 0, 17 },
                { 0, 7, 8, 9, 0 },
                { 14, 6, 1, 2, 10 },
                { 0, 5, 4, 3, 0 },
                { 13, 0, 12, 0, 11 },
            };
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Num.Vector2(2 * sc, 2 * sc));
            for (int r = 0; r < 5; r++)
            {
                for (int c = 0; c < 5; c++)
                {
                    if (c > 0) ImGui.SameLine();
                    int idx = map[r, c];
                    if (idx == 0) { ImGui.Dummy(new Num.Vector2(cell, cell)); continue; }
                    var col = Normals.Color(idx);
                    var pos = ImGui.GetCursorScreenPos();
                    if (ImGui.ColorButton($"##n{idx}", new Num.Vector4(col.R / 255f, col.G / 255f, col.B / 255f, 1),
                            ImGuiColorEditFlags.NoTooltip, new Num.Vector2(cell, cell)))
                        Ed.NormalBrush = (byte)idx;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Normals.NameOf(idx));
                    if (idx == Ed.NormalBrush)
                        ImGui.GetWindowDrawList().AddRect(pos - Num.Vector2.One, pos + new Num.Vector2(cell + 1, cell + 1), 0xFFFFFFFFu, 0, ImDrawFlags.None, 2);
                }
            }
            ImGui.PopStyleVar();
            ImGui.TextDisabled("Pick where a surface faces. Right-click clears.");
        }
    }

    // ---------------------------------------------------------------- points

    private void DrawPointsSection()
    {
        var doc = Ed.Document;
        if (Ed.Tool is not PointTool && doc.Points.Count == 0) return;
        float sc = Ui.UiScale;
        ImGui.SeparatorText("Points");
        var selected = Ed.SelectedPoint;

        void Row(PointTrack t)
        {
            ImGui.PushID(t.Id);
            var at = doc.PointAt(t, Ed.FrameIndex, out bool own);
            var where = at is { } p
                ? $"{p.X},{p.Y}{(own ? (t.Keyed ? " (key)" : "") : t.Keyed ? (t.Smooth ? " (between keys)" : " (held)") : " (inherited)")}"
                : "not placed";
            if (ImGui.RadioButton($"{(t.IsPivot ? "pivot" : t.Name)}##r", t == selected)) Ed.SelectedPointId = t.Id;
            ImGui.SameLine();
            ImGui.TextDisabled(where);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(t.IsPivot
                    ? "The point that stays still on screen while frames change (feet, a hinge). Without one, the canvas middle."
                    : $"Rides on \"{t.Layer.Name}\": each drawing can put it somewhere; others inherit it from the nearest frame that does.");
            if (!t.IsPivot && ImGui.BeginPopupContextItem("pt"))
            {
                if (t.Keyed)
                {
                    bool smooth = t.Smooth;
                    if (ImGui.Checkbox("Smooth between keys", ref smooth)) DocumentOps.SetSmooth(doc, Ed.History, t, smooth);
                }
                var name = t.Name;
                ImGui.SetNextItemWidth(140 * sc);
                if (ImGui.InputText("Name", ref name, 32, ImGuiInputTextFlags.EnterReturnsTrue)) Ed.RenamePoint(t, name);
                if (ImGui.MenuItem("Delete point")) Ed.RemovePoint(t);
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }

        if (doc.Pivot is { } pivot) Row(pivot);
        else if (ImGui.RadioButton("pivot (not set)", selected == null)) Ed.SelectedPointId = 0;
        foreach (var t in doc.Points.Where(p => !p.IsPivot).ToList()) Row(t);

        ImGui.SetNextItemWidth(-60 * sc);
        bool add = ImGui.InputTextWithHint("##newpoint", "new point, e.g. hand", ref _newPointName, 32, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("+##point") || add) && _newPointName.Trim().Length > 0)
        {
            Ed.AddPoint(_newPointName, _newPointKeyed);
            _newPointName = "";
        }
        if (ImGui.RadioButton("on drawings", !_newPointKeyed)) _newPointKeyed = false;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Each drawing puts it somewhere; it moves when the drawing changes (a hand).");
        ImGui.SameLine();
        if (ImGui.RadioButton("keyed", _newPointKeyed)) _newPointKeyed = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Keys on frames, in a row of the timeline; it moves between them, even through a held drawing (a target, a muzzle flash drifting).");
        if (Ed.Tool is PointTool) ImGui.TextDisabled("Click to place it on this drawing; right-click to clear it there.");
    }

    /// <summary>Every point on this frame: the pivot as a crosshair, named points as diamonds.</summary>
    private void DrawPointsOverlay(ImDrawListPtr dl, Num.Vector2 imgMin, int zoom)
    {
        var doc = Ed.Document;
        if (doc.Points.Count == 0) return;
        bool editing = Ed.Tool is PointTool;
        var selected = Ed.SelectedPoint;
        foreach (var t in doc.Points)
        {
            if (doc.PointAt(t, Ed.FrameIndex, out bool own) is not { } at) continue;
            var c = imgMin + new Num.Vector2(at.X + 0.5f, at.Y + 0.5f) * zoom;
            uint alpha = own ? 0xFF000000u : 0x80000000u;
            float r = Math.Max(4, zoom * 0.6f);
            if (t.IsPivot)
            {
                uint col = 0x0040A0FFu | alpha;
                dl.AddLine(c - new Num.Vector2(r * 2, 0), c + new Num.Vector2(r * 2, 0), col, 2);
                dl.AddLine(c - new Num.Vector2(0, r * 2), c + new Num.Vector2(0, r * 2), col, 2);
                dl.AddCircle(c, r, col, 16, 2);
            }
            else
            {
                uint col = 0x00E0D040u | alpha;
                dl.AddQuadFilled(c + new Num.Vector2(0, -r), c + new Num.Vector2(r, 0), c + new Num.Vector2(0, r), c + new Num.Vector2(-r, 0), col);
                // a shadow, so the name reads on light pixels too
                dl.AddText(c + new Num.Vector2(r + 4, -r - 1), 0x00000000u | alpha, t.Name);
                dl.AddText(c + new Num.Vector2(r + 3, -r - 2), 0x00FFFFFFu | alpha, t.Name);
            }
            if (editing && t == selected) dl.AddCircle(c, r + 4, 0xFFFFFFFFu, 20, 1.5f);
        }
    }
}
