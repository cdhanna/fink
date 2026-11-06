using Fink.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

/// <summary>
/// 9-slice on the canvas: four guide lines with tabs outside the picture (drag a tab to move its
/// line, so drawing on the picture is never affected), and a preview in the canvas's corner of
/// the current frame stretched to another size (drag the preview to resize it).
/// </summary>
public sealed partial class FinkGame
{
    private static readonly uint SliceColor = 0xFFE040E0u; // magenta: reads on any palette
    /// <summary>The tab being dragged (0 left, 1 top, 2 right, 3 bottom), and the insets it started from.</summary>
    private int _sliceDrag = -1;
    private NineSlice? _sliceBefore;
    /// <summary>The preview's size in the asset's pixels (0: not chosen yet), and whether it's folded away.</summary>
    private Num.Vector2 _slicePreview;
    private Document? _slicePreviewFor;
    private bool _slicePreviewFolded, _slicePreviewMoved;
    private Num.Vector2 _slicePreviewPress;

    private const float SliceTab = 14;

    /// <summary>Where the guide's tab sits: above the picture for the vertical lines, left of it for the horizontal ones.</summary>
    private static Num.Vector2 SliceTabCenter(int k, NineSlice s, Document doc, Num.Vector2 imgMin, float zoom, float tab)
    {
        float[] at = { s.Left, s.Top, doc.Width - s.Right, doc.Height - s.Bottom };
        return k % 2 == 0
            ? imgMin + new Num.Vector2(at[k] * zoom, -tab / 2 - 4)
            : imgMin + new Num.Vector2(-tab / 2 - 4, at[k] * zoom);
    }

    /// <summary>Before the canvas's own button, so the tabs and the preview get the mouse first.</summary>
    private void SliceInput(Document doc, Num.Vector2 imgMin, float zoom, Num.Vector2 origin, Num.Vector2 avail)
    {
        if (doc.Slice is not { } s) { _sliceDrag = -1; return; }
        var save = ImGui.GetCursorScreenPos();
        float tab = SliceTab * Ui.UiScale;
        var mouse = ImGui.GetIO().MousePos;

        for (int k = 0; k < 4; k++)
        {
            var c = SliceTabCenter(k, s, doc, imgMin, zoom, tab);
            ImGui.SetCursorScreenPos(c - new Num.Vector2(tab / 2));
            ImGui.InvisibleButton($"##slice{k}", new Num.Vector2(tab));
            if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            {
                ImGui.SetMouseCursor(k % 2 == 0 ? ImGuiMouseCursor.ResizeEW : ImGuiMouseCursor.ResizeNS);
                string[] names = { "left", "top", "right", "bottom" };
                int[] px = { s.Left, s.Top, s.Right, s.Bottom };
                ImGui.SetTooltip($"9-slice {names[k]}: {px[k]} px from the edge. Drag to move it.");
            }
            if (ImGui.IsItemActivated())
            {
                _sliceDrag = k;
                _sliceBefore = s;
            }
            if (ImGui.IsItemActive() && _sliceDrag == k)
            {
                var p = (mouse - imgMin) / zoom;
                int v = (int)MathF.Round(k % 2 == 0 ? p.X : p.Y);
                var n = k switch
                {
                    0 => s with { Left = v },
                    1 => s with { Top = v },
                    2 => s with { Right = doc.Width - v },
                    _ => s with { Bottom = doc.Height - v },
                };
                doc.Slice = n.Clamp(doc.Width, doc.Height); // live; one undo step on release
                s = doc.Slice.Value;
            }
            if (ImGui.IsItemDeactivated() && _sliceDrag == k)
            {
                var after = doc.Slice;
                doc.Slice = _sliceBefore;
                Ed.SetSlice(after);
                _sliceDrag = -1;
            }
        }

        // the preview: drag it to resize, tap it to fold it away (or back)
        var (pMin, pMax, _) = SlicePreviewRect(doc, s, origin, avail);
        ImGui.SetCursorScreenPos(pMin);
        ImGui.InvisibleButton("##slicepreview", pMax - pMin);
        if (ImGui.IsItemActivated())
        {
            _slicePreviewPress = mouse;
            _slicePreviewMoved = false;
        }
        if (ImGui.IsItemActive() && !_slicePreviewFolded)
        {
            var d = ImGui.GetIO().MouseDelta;
            _slicePreviewMoved |= Num.Vector2.Distance(mouse, _slicePreviewPress) > 3;
            if (_slicePreviewMoved && d != Num.Vector2.Zero)
            {
                float z = SlicePreviewZoom(doc, avail);
                // anchored bottom-right: dragging left/up grows it
                _slicePreview -= d / z;
            }
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNWSE);
        }
        if (ImGui.IsItemDeactivated() && !_slicePreviewMoved) _slicePreviewFolded = !_slicePreviewFolded;
        if (ImGui.IsItemHovered() && !ImGui.IsItemActive())
            ImGui.SetTooltip(_slicePreviewFolded ? "9-slice preview. Click to show it."
                : $"This frame stretched to {(int)_slicePreview.X}x{(int)_slicePreview.Y}. Drag to resize; click to fold it away.");
        ImGui.SetCursorScreenPos(save);
    }

    /// <summary>A whole-number zoom for the preview that fits a canvas-sized area of the panel.</summary>
    private static float SlicePreviewZoom(Document doc, Num.Vector2 avail) =>
        Math.Max(1, MathF.Floor(Math.Min(avail.X * 0.12f / doc.Width, avail.Y * 0.12f / doc.Height)));

    /// <summary>The preview's box (bottom-right of the canvas area) and its zoom; keeps its size sensible.</summary>
    private (Num.Vector2 min, Num.Vector2 max, float zoom) SlicePreviewRect(Document doc, NineSlice s, Num.Vector2 origin, Num.Vector2 avail)
    {
        float z = SlicePreviewZoom(doc, avail);
        if (_slicePreviewFor != doc || _slicePreview == Num.Vector2.Zero)
        {
            _slicePreview = new Num.Vector2(doc.Width * 2, doc.Height * 1.5f);
            _slicePreviewFor = doc;
        }
        float maxW = Math.Max(s.Left + s.Right + 1, avail.X * 0.45f / z), maxH = Math.Max(s.Top + s.Bottom + 1, avail.Y * 0.45f / z);
        _slicePreview = new Num.Vector2(Math.Clamp(_slicePreview.X, s.Left + s.Right + 1, maxW), Math.Clamp(_slicePreview.Y, s.Top + s.Bottom + 1, maxH));
        float pad = 8 * Ui.UiScale;
        var size = _slicePreviewFolded ? ImGui.CalcTextSize("9-slice") + new Num.Vector2(pad * 2, pad) : new Num.Vector2(MathF.Round(_slicePreview.X), MathF.Round(_slicePreview.Y)) * z;
        var max = origin + avail - new Num.Vector2(pad);
        return (max - size, max, z);
    }

    /// <summary>After the picture: the guide lines, their tabs, and the preview.</summary>
    private void DrawSliceOverlay(ImDrawListPtr dl, Document doc, Num.Vector2 imgMin, float zoom, Num.Vector2 origin, Num.Vector2 avail)
    {
        if (doc.Slice is not { } s) return;
        float sc = Ui.UiScale, tab = SliceTab * sc;
        var imgMax = imgMin + new Num.Vector2(doc.Width, doc.Height) * zoom;
        float x0 = imgMin.X + s.Left * zoom, x1 = imgMax.X - s.Right * zoom;
        float y0 = imgMin.Y + s.Top * zoom, y1 = imgMax.Y - s.Bottom * zoom;
        void Dashed(Num.Vector2 a, Num.Vector2 b)
        {
            float len = Num.Vector2.Distance(a, b), dash = 6 * sc;
            var dir = (b - a) / Math.Max(1, len);
            for (float t = 0; t < len; t += dash * 2)
                dl.AddLine(a + dir * t, a + dir * Math.Min(len, t + dash), SliceColor, 1.5f * sc);
        }
        float reach = tab + 4;
        Dashed(new(x0, imgMin.Y - reach), new(x0, imgMax.Y));
        Dashed(new(x1, imgMin.Y - reach), new(x1, imgMax.Y));
        Dashed(new(imgMin.X - reach, y0), new(imgMax.X, y0));
        Dashed(new(imgMin.X - reach, y1), new(imgMax.X, y1));
        for (int k = 0; k < 4; k++)
        {
            var c = SliceTabCenter(k, s, doc, imgMin, zoom, tab);
            bool hot = _sliceDrag == k;
            dl.AddRectFilled(c - new Num.Vector2(tab / 2), c + new Num.Vector2(tab / 2), hot ? 0xFFFFFFFFu : SliceColor, 3 * sc);
            dl.AddRect(c - new Num.Vector2(tab / 2), c + new Num.Vector2(tab / 2), 0xFF000000u, 3 * sc);
        }

        // the preview
        var (pMin, pMax, z) = SlicePreviewRect(doc, s, origin, avail);
        dl.AddRectFilled(pMin - new Num.Vector2(3), pMax + new Num.Vector2(3), Colors.Background);
        dl.AddRect(pMin - new Num.Vector2(3), pMax + new Num.Vector2(3), SliceColor, 0, ImDrawFlags.None, 1.5f * sc);
        if (_slicePreviewFolded)
        {
            dl.AddText(pMin + new Num.Vector2(8 * sc, 4 * sc), 0xFFFFFFFFu, "9-slice");
            return;
        }
        var tex = ThumbFor(doc);
        float W = doc.Width, H = doc.Height, pw = MathF.Round(_slicePreview.X), ph = MathF.Round(_slicePreview.Y);
        float[] sx = { 0, s.Left, W - s.Right, W }, dx = { 0, s.Left, pw - s.Right, pw };
        float[] sy = { 0, s.Top, H - s.Bottom, H }, dy = { 0, s.Top, ph - s.Bottom, ph };
        // a checkerboard under it, as on the canvas
        float cell = 8 * sc;
        for (float yy = pMin.Y; yy < pMax.Y; yy += cell)
            for (float xx = pMin.X; xx < pMax.X; xx += cell)
            {
                bool dark = ((int)((xx - pMin.X) / cell) + (int)((yy - pMin.Y) / cell)) % 2 == 1;
                dl.AddRectFilled(new Num.Vector2(xx, yy), new Num.Vector2(Math.Min(xx + cell, pMax.X), Math.Min(yy + cell, pMax.Y)), dark ? Colors.CheckerDark : Colors.CheckerLight);
            }
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
            {
                if (sx[i + 1] <= sx[i] || sy[j + 1] <= sy[j]) continue;
                dl.AddImage(tex, pMin + new Num.Vector2(dx[i], dy[j]) * z, pMin + new Num.Vector2(dx[i + 1], dy[j + 1]) * z,
                    new Num.Vector2(sx[i] / W, sy[j] / H), new Num.Vector2(sx[i + 1] / W, sy[j + 1] / H));
            }
        dl.AddText(pMin + new Num.Vector2(4 * sc, 2 * sc), 0xA0FFFFFFu, $"{(int)pw}x{(int)ph}");
    }
}
