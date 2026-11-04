using Fink.Core;
using Fink.Editor;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

public sealed partial class FinkGame
{
    private int _outlineVersion = int.MinValue;
    private List<(int x0, int y0, int x1, int y1)> _outline = new();

    /// <summary>Marching ants, the shape being dragged out, and the transform box with its handles.</summary>
    private void DrawSelectionOverlay(ImDrawListPtr dl, Num.Vector2 imgMin, int zoom, Num.Vector2 docPos)
    {
        var sel = Ed.Selection;
        Num.Vector2 S(Num.Vector2 p) => imgMin + p * zoom;

        // marching ants: unit edges alternating black/white, shifting over time
        if (_outlineVersion != sel.DisplayVersion)
        {
            _outlineVersion = sel.DisplayVersion;
            _outline = Selection.Outline(sel.DisplayMask(), Ed.Document.Width, Ed.Document.Height);
        }
        int phase = (int)(ImGui.GetTime() * 8);
        foreach (var (x0, y0, x1, y1) in _outline)
        {
            bool white = ((x0 + y0 + phase / 2) & 1) == 0;
            dl.AddLine(S(new(x0, y0)), S(new(x1, y1)), white ? 0xFFFFFFFFu : 0xFF000000u, 1.5f);
        }

        // the rectangle or lasso being dragged
        if (sel.RectPreview is var (a, b))
        {
            var min = Num.Vector2.Min(a, b);
            var max = Num.Vector2.Max(a, b) + Num.Vector2.One;
            dl.AddRect(S(min), S(max), 0xFFFFFFFFu, 0, ImDrawFlags.None, 1.5f);
            dl.AddRect(S(min) + Num.Vector2.One, S(max) - Num.Vector2.One, 0xFF000000u);
            var size = $"{(int)(max.X - min.X)} x {(int)(max.Y - min.Y)}";
            dl.AddText(S(max) + new Num.Vector2(6, 2), 0xFFFFFFFFu, size);
        }
        if (sel.LassoPreview is { Count: > 1 } lasso)
        {
            for (int i = 1; i < lasso.Count; i++) dl.AddLine(S(lasso[i - 1]), S(lasso[i]), 0xFFFFFFFFu, 1.5f);
            dl.AddLine(S(lasso[^1]), S(lasso[0]), 0x80FFFFFFu);
        }

        // the floating box and, for the Transform tool, its handles
        if (sel.Floating is { } f)
        {
            var c = f.Corners().Select(S).ToArray();
            for (int i = 0; i < 4; i++) dl.AddLine(c[i], c[(i + 1) % 4], 0xFFFFC080u, 1.5f);
            if (Ed.Tool is TransformTool)
            {
                float r = 5;
                foreach (var (_, pos) in SelectionController.HandlePositions(f.Corners()))
                {
                    var p = S(pos);
                    dl.AddRectFilled(p - new Num.Vector2(r, r), p + new Num.Vector2(r, r), 0xFFFFFFFFu);
                    dl.AddRect(p - new Num.Vector2(r, r), p + new Num.Vector2(r, r), 0xFF000000u);
                }
                var center = S(f.Transform.Center);
                dl.AddCircle(center, 4, 0xFFFFC080u);
            }

            if (_canvasHovered && !_canvasActive)
            {
                var h = sel.HitTest(docPos, zoom, Ed.Tool is TransformTool);
                var hint = h switch
                {
                    Handle.None => "click outside: apply",
                    Handle.Move => "drag: move",
                    Handle.Rotate => "drag: rotate (Shift: 15°)",
                    _ => "drag: scale (Shift: keep shape)",
                };
                dl.AddText(ImGui.GetIO().MousePos + new Num.Vector2(16, 18), 0xFFFFFFFFu, hint);
            }
        }
    }

    /// <summary>The selection section of the Tools panel: options, quick transforms, numeric fields.</summary>
    private void DrawSelectionTools()
    {
        var sel = Ed.Selection;
        if (Ed.Tool is not ISelectionTool && sel.Floating == null) return;

        ImGui.SeparatorText("Selection");
        ImGui.TextDisabled("Shift add, Alt subtract, both intersect");
        Toggle("selection.withLinked");
        Toggle("selection.allLayers");
        if (Ed.Tool is WandTool) Toggle("selection.wandContiguous");

        CommandButton("selection.flipH", "Flip H", small: true);
        ImGui.SameLine();
        CommandButton("selection.flipV", "Flip V", small: true);
        ImGui.SameLine();
        CommandButton("selection.rotateCCW", "-90", small: true);
        ImGui.SameLine();
        CommandButton("selection.rotateCW", "+90", small: true);

        var f = sel.Floating;
        if (f == null)
        {
            ImGui.TextDisabled(sel.Selection.IsEmpty ? "Nothing selected." : "Drag inside to move it.");
            return;
        }

        var t = f.Transform;
        bool changed = false;
        var pos = t.Center;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat2("##center", ref pos, 0.25f, 0, 0, "%.1f")) { t.Center = pos; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Center X, Y (pixels)");

        int w = (int)MathF.Round(f.SrcW * t.ScaleX), h = (int)MathF.Round(f.SrcH * t.ScaleY);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X / 2 - 4);
        if (ImGui.DragInt("##w", ref w, 0.25f, -4096, 4096, "W %d px") && w != 0) { t.ScaleX = (float)w / f.SrcW; changed = true; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragInt("##h", ref h, 0.25f, -4096, 4096, "H %d px") && h != 0) { t.ScaleY = (float)h / f.SrcH; changed = true; }

        float angle = t.Angle;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X / 2 - 4);
        if (ImGui.DragFloat("##angle", ref angle, 0.5f, -180, 180, "%.1f deg")) { t.Angle = angle; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Rotation");
        ImGui.SameLine();
        float skew = t.Skew;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat("##skew", ref skew, 0.5f, -80, 80, "skew %.1f")) { t.Skew = skew; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Horizontal skew");
        if (changed) sel.SetTransform(t);

        CommandButton("selection.commit", "Apply");
        ImGui.SameLine();
        CommandButton("selection.cancel", "Cancel");
    }

    private void DrawSelectMenu()
    {
        if (!ImGui.BeginMenu("Select")) return;
        MenuCommand("tool.select");
        MenuCommand("tool.lasso");
        MenuCommand("tool.wand");
        MenuCommand("tool.transform");
        ImGui.Separator();
        MenuCommand("selection.all");
        MenuCommand("selection.deselect");
        MenuCommand("selection.invert");
        MenuCommand("selection.layerContent");
        ImGui.Separator();
        MenuCommand("selection.flipH");
        MenuCommand("selection.flipV");
        MenuCommand("selection.rotateCW");
        MenuCommand("selection.rotateCCW");
        MenuCommand("selection.commit");
        MenuCommand("selection.cancel");
        ImGui.Separator();
        MenuCommand("selection.withLinked");
        MenuCommand("selection.allLayers");
        MenuCommand("selection.wandContiguous");
        ImGui.EndMenu();
    }
}
