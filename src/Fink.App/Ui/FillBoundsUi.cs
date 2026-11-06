using Fink.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

/// <summary>
/// Which Line layers each Fill layer's fills stop at: a matrix in the Layers panel (fill layers
/// down, line layers across) and the same choices in a fill layer's timeline menu.
/// </summary>
public sealed partial class FinkGame
{
    private void DrawFillBoundsMatrix()
    {
        var doc = Ed.Document;
        var fills = doc.Layers.Where(l => l.Role == LayerRole.Fill).Reverse().ToList(); // top first, as listed
        var lines = doc.Layers.Where(l => l.Role == LayerRole.Line).Reverse().ToList();
        if (fills.Count == 0 || lines.Count == 0) return;

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Fill boundaries", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Which line layers each fill layer's fills stop at.\n" +
                             "\"Visible\": every visible line layer (the default). Ticking line layers yourself picks\n" +
                             "exactly those, and they count even while hidden.");

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollX | ImGuiTableFlags.NoHostExtendX;
        float h = (fills.Count + 1) * ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ScrollbarSize + 8;
        if (!ImGui.BeginTable("fillbounds", lines.Count + 2, flags, new Num.Vector2(0, h))) return;
        ImGui.TableSetupColumn("Fill layer");
        ImGui.TableSetupColumn("Visible");
        foreach (var l in lines) ImGui.TableSetupColumn(l.Name);
        ImGui.TableHeadersRow();

        foreach (var fill in fills)
        {
            ImGui.PushID(fill.Id.GetHashCode());
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(RoleFill, fill.Name);
            ImGui.TableNextColumn();
            FillBoundsCells(fill, lines);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    /// <summary>The "Visible" checkbox and a checkbox per line layer, each in its own table column.</summary>
    private void FillBoundsCells(Layer fill, List<Layer> lines)
    {
        var doc = Ed.Document;
        bool auto = fill.FillBounds == null;
        if (ImGui.Checkbox("##auto", ref auto))
            Ed.SetFillBounds(fill, auto ? null : doc.FillBoundaryLayers(fill).ToList());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(fill.FillBounds == null ? $"{fill.Name} stops at every visible line layer. Untick to choose them yourself."
                : $"{fill.Name} stops at the line layers ticked here, shown or hidden. Tick to go back to every visible one.");
        var now = doc.FillBoundaryLayers(fill).ToList();
        foreach (var line in lines)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(line.Id.GetHashCode());
            bool on = now.Contains(line);
            // decided once: the click below changes FillBounds, and Push/Pop must still pair up
            bool dim = fill.FillBounds == null;
            if (dim) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
            if (ImGui.Checkbox("##b", ref on))
            {
                var set = now.Where(l => l != line).ToList();
                if (on) set.Add(line);
                Ed.SetFillBounds(fill, set);
            }
            if (dim) ImGui.PopStyleVar();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{fill.Name} fills {(on ? "stop" : "don't stop")} at \"{line.Name}\"" +
                                 (fill.FillBounds == null ? " (it's visible / hidden). Click to choose for yourself." : "."));
            ImGui.PopID();
        }
    }

    /// <summary>The same choices as a menu, for a fill layer's right-click menu.</summary>
    private void DrawFillBoundsMenu(Layer fill)
    {
        var doc = Ed.Document;
        if (fill.Role != LayerRole.Fill) return;
        var lines = doc.Layers.Where(l => l.Role == LayerRole.Line).Reverse().ToList();
        if (lines.Count == 0 || !ImGui.BeginMenu("Fills Stop At")) return;
        if (ImGui.MenuItem("Every visible line layer", "", fill.FillBounds == null))
            Ed.SetFillBounds(fill, fill.FillBounds == null ? doc.FillBoundaryLayers(fill).ToList() : null);
        ImGui.Separator();
        var now = doc.FillBoundaryLayers(fill).ToList();
        foreach (var line in lines)
            if (ImGui.MenuItem(line.Name, line.Visible ? "" : "hidden", now.Contains(line)))
            {
                var set = now.Where(l => l != line).ToList();
                if (!now.Contains(line)) set.Add(line);
                Ed.SetFillBounds(fill, set);
            }
        ImGui.EndMenu();
    }
}
