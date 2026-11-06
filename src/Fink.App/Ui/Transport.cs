using Fink.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

/// <summary>
/// The transport under the canvas: big buttons for first / previous / play / next / last, and a
/// scrubber the width of the canvas, so the timeline isn't needed just to flip through frames.
/// </summary>
public sealed partial class FinkGame
{
    private float TransportHeight => Ui.ShowTransport ? 46 * Ui.UiScale : 0;

    private void DrawTransport(Num.Vector2 pos, float width, float height)
    {
        if (height <= 0) return;
        var doc = Ed.Document;
        float sc = Ui.UiScale;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Num.Vector2(width, height), ImGui.GetColorU32(ImGuiCol.WindowBg));
        dl.AddLine(pos, pos + new Num.Vector2(width, 0), ImGui.GetColorU32(ImGuiCol.Separator));

        float pad = 6 * sc, b = height - 2 * pad;
        var p = pos + new Num.Vector2(pad, pad);
        var buttons = new[] { ("frame.first", 0), ("frame.previous", 1), ("playback.toggle", 2), ("frame.next", 3), ("frame.last", 4) };
        foreach (var (id, icon) in buttons)
        {
            float w = icon == 2 ? b * 1.5f : b;
            ImGui.SetCursorScreenPos(p);
            ImGui.PushID(id);
            if (ImGui.InvisibleButton("t", new Num.Vector2(w, b))) _session.Commands.Run(id);
            bool hov = ImGui.IsItemHovered(), act = ImGui.IsItemActive();
            bool lit = icon == 2 && Ed.Playing;
            uint bg = lit ? Colors.Accent : ImGui.GetColorU32(act ? ImGuiCol.ButtonActive : hov ? ImGuiCol.ButtonHovered : ImGuiCol.Button);
            dl.AddRectFilled(p, p + new Num.Vector2(w, b), bg, ImGui.GetStyle().FrameRounding);
            TransportIcon(dl, p + new Num.Vector2(w, b) / 2, b * 0.3f, icon, lit ? 0xFF1A1A1Au : ImGui.GetColorU32(ImGuiCol.Text));
            if (hov) ImGui.SetTooltip(TooltipFor(_session.Commands.Get(id)));
            ImGui.PopID();
            p.X += w + (icon is 1 or 2 ? 8 : 4) * sc;
        }

        // ---- the scrubber: tags along the top, a cell per frame, the playhead
        int n = doc.Frames.Count;
        var label = $"{Ed.FrameIndex + 1} / {n}";
        float lw = ImGui.CalcTextSize($"{n} / {n}").X + 14 * sc;
        float x0 = p.X + 6 * sc, x1 = pos.X + width - pad - lw;
        if (x1 - x0 < 40 * sc) return;
        float cw = (x1 - x0) / n, tagH = b * 0.28f, top = p.Y, trackTop = p.Y + tagH + 2 * sc, bottom = p.Y + b;

        ImGui.SetCursorScreenPos(new Num.Vector2(x0, top));
        ImGui.InvisibleButton("scrub", new Num.Vector2(x1 - x0, b));
        int at = Math.Clamp((int)MathF.Floor((ImGui.GetMousePos().X - x0) / cw), 0, n - 1);
        if (ImGui.IsItemActivated() && Ed.Playing) Ed.TogglePlay();
        if (ImGui.IsItemActive()) Ed.FrameIndex = at;
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            var tag = doc.TagAt(at);
            ImGui.SetTooltip($"Frame {at + 1}{(tag != null ? $"  ({tag.Name})" : "")}" + (ImGui.IsItemActive() ? "" : "\nClick or drag to scrub."));
        }

        float rounding = ImGui.GetStyle().FrameRounding;
        dl.AddRectFilled(new Num.Vector2(x0, trackTop), new Num.Vector2(x1, bottom), ImGui.GetColorU32(ImGuiCol.FrameBg), rounding);
        foreach (var tag in doc.Tags)
        {
            var (ta, tb) = doc.TagRange(tag);
            if (ta < 0) continue;
            dl.AddRectFilled(new Num.Vector2(x0 + ta * cw + 1, top), new Num.Vector2(x0 + (tb + 1) * cw - 1, top + tagH),
                ImGui.GetColorU32(TagColor(tag.Name) * new Num.Vector4(1, 1, 1, 0.75f)), 2 * sc);
        }
        // ticks: every frame when there's room, seconds always
        for (int f = 1; f < n; f++)
        {
            bool second = f % doc.Fps == 0;
            if (!second && cw < 5 * sc) continue;
            float x = x0 + f * cw;
            dl.AddLine(new Num.Vector2(x, second ? trackTop : bottom - (bottom - trackTop) * 0.35f), new Num.Vector2(x, bottom), Faint(second ? 0x70u : 0x28u));
        }
        for (int f = 0; f < n; f++)
            if (doc.IsPinned(f))
            {
                var c = new Num.Vector2(x0 + (f + 0.5f) * cw, trackTop + 1);
                dl.AddTriangleFilled(c + new Num.Vector2(-4 * sc, 0), c + new Num.Vector2(4 * sc, 0), c + new Num.Vector2(0, 5 * sc), Ed.Onion.PinTint.Packed);
            }
        // the playhead: the current frame's cell, and a handle
        float hx0 = x0 + Ed.FrameIndex * cw, hx1 = Math.Max(hx0 + 3 * sc, x0 + (Ed.FrameIndex + 1) * cw);
        dl.AddRectFilled(new Num.Vector2(hx0, trackTop), new Num.Vector2(hx1, bottom), WithAlpha(Colors.Accent, 0x90), rounding);
        float hc = (hx0 + hx1) / 2;
        dl.AddLine(new Num.Vector2(hc, top), new Num.Vector2(hc, bottom), Colors.Accent, 2 * sc);

        var ls = ImGui.CalcTextSize(label);
        dl.AddText(new Num.Vector2(x1 + 10 * sc, pos.Y + (height - ls.Y) / 2), ImGui.GetColorU32(ImGuiCol.Text), label);
    }

    /// <summary>0 first, 1 previous, 2 play/pause, 3 next, 4 last.</summary>
    private void TransportIcon(ImDrawListPtr dl, Num.Vector2 c, float r, int icon, uint col)
    {
        float bar = Math.Max(2, r * 0.28f);
        void Tri(float cx, int dir, float s) =>
            dl.AddTriangleFilled(new Num.Vector2(cx - dir * s * 0.6f, c.Y - s), new Num.Vector2(cx - dir * s * 0.6f, c.Y + s), new Num.Vector2(cx + dir * s * 0.75f, c.Y), col);
        void Bar(float x) => dl.AddRectFilled(new Num.Vector2(x - bar / 2, c.Y - r), new Num.Vector2(x + bar / 2, c.Y + r), col);
        switch (icon)
        {
            case 0: Bar(c.X - r); Tri(c.X - r * 0.15f, -1, r * 0.8f); Tri(c.X + r * 0.75f, -1, r * 0.8f); break;
            case 1: Bar(c.X - r * 0.7f); Tri(c.X + r * 0.2f, -1, r); break;
            case 2:
                if (Ed.Playing)
                    foreach (var x in new[] { c.X - r * 0.45f, c.X + r * 0.45f })
                        dl.AddRectFilled(new Num.Vector2(x - r * 0.22f, c.Y - r), new Num.Vector2(x + r * 0.22f, c.Y + r), col);
                else Tri(c.X, +1, r * 1.05f);
                break;
            case 3: Bar(c.X + r * 0.7f); Tri(c.X - r * 0.2f, +1, r); break;
            case 4: Bar(c.X + r); Tri(c.X + r * 0.15f, +1, r * 0.8f); Tri(c.X - r * 0.75f, +1, r * 0.8f); break;
        }
    }
}
