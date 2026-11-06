using Fink.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

/// <summary>
/// The history chart: every step, in the order it was made, with branches in their own lanes.
/// Undo some steps and make a change, and the steps you undid stay, on a branch of their own;
/// click any step, on any branch, to go back to it.
/// </summary>
public sealed partial class FinkGame
{
    private int _historyScrollVersion = -1;
    private string _labelEdit = "";
    private HistoryNode? _labelNode;

    private void DrawHistoryPanel()
    {
        var history = Ed.History;
        float sc = Ui.UiScale;
        CommandButton("edit.undo", "Undo");
        ImGui.SameLine();
        CommandButton("edit.redo", "Redo");
        ImGui.SameLine();
        int branches = history.Nodes.Count(n => n.Children.Count == 0);
        ImGui.TextDisabled($"{history.Nodes.Count - 1} steps{(branches > 1 ? $", {branches} branches" : "")}");
        ImGui.TextDisabled("Click any step to go back to it. Change something there and it branches; the old way stays.");
        ImGui.Separator();

        if (!ImGui.BeginChild("chart")) { ImGui.EndChild(); return; }

        // lanes: a step continues its parent's lane if it's the parent's first step after it;
        // every later branch from there gets a new lane
        var nodes = history.Nodes;
        var lane = new Dictionary<HistoryNode, int>();
        var row = new Dictionary<HistoryNode, int>();
        int lanes = 1;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            row[n] = i;
            lane[n] = n.Parent == null || !lane.ContainsKey(n.Parent) ? 0
                : n.Parent.Children[0] == n ? lane[n.Parent] : lanes++;
        }
        var line = history.Line().ToHashSet();
        var path = new HashSet<HistoryNode>();
        for (var n = history.Current; n != null; n = n.Parent) path.Add(n);

        float rowH = ImGui.GetTextLineHeightWithSpacing() + 2 * sc, laneW = 14 * sc;
        float graphW = Math.Min(lanes, 12) * laneW + 8 * sc;
        var origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var accent = ImGui.GetColorU32(ImGuiCol.CheckMark);
        uint ahead = Faint(0xB0), other = WithAlpha(ImGui.GetColorU32(ImGuiCol.TextDisabled), 0x70);
        Num.Vector2 Dot(HistoryNode n) => origin + new Num.Vector2(8 * sc + Math.Min(lane[n], 11) * laneW, row[n] * rowH + rowH / 2);

        HistoryNode? clicked = null;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var p = Dot(n);
            uint col = path.Contains(n) ? accent : line.Contains(n) ? ahead : other;
            if (n.Parent is { } parent && row.ContainsKey(parent))
            {
                var q = Dot(parent);
                // down the parent's lane, then across to this one
                dl.AddLine(q, new Num.Vector2(q.X, p.Y - rowH / 2), col, 2 * sc);
                dl.AddLine(new Num.Vector2(q.X, p.Y - rowH / 2), p, col, 2 * sc);
            }
            ImGui.SetCursorScreenPos(origin + new Num.Vector2(0, i * rowH));
            ImGui.PushID(n.Id);
            bool here = n == history.Current;
            if (ImGui.Selectable("##step", here, ImGuiSelectableFlags.None, new Num.Vector2(0, rowH - 2 * sc))) clicked = n;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{n.Name}\n{n.Time:T}" + (n.Tag is Document d ? $"  on {d.Name}" : n.Edit != null ? "  (project)" : "") +
                                 (path.Contains(n) ? "" : line.Contains(n) ? "\nAhead: Redo goes here." : "\nOn another branch: click to go there.") +
                                 "\nRight-click to name this point.");
            if (ImGui.BeginPopupContextItem("label"))
            {
                if (_labelNode != n) { _labelNode = n; _labelEdit = n.Label ?? ""; }
                if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(180 * sc);
                if (ImGui.InputTextWithHint("##label", "name this point", ref _labelEdit, 48, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    n.Label = _labelEdit.Trim().Length == 0 ? null : _labelEdit.Trim();
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.MenuItem("Go here")) clicked = n;
                ImGui.EndPopup();
            }
            ImGui.PopID();

            if (here) dl.AddCircleFilled(p, 5 * sc, accent);
            else dl.AddCircleFilled(p, 3.5f * sc, col);
            if (n.Label != null) dl.AddCircle(p, 7 * sc, Colors.Accent, 12, 1.5f * sc);

            var text = n.Name + (n.Tag is Document doc && Ed.Project.Assets.Count > 1 ? $"   {doc.Name}" : "");
            var tc = path.Contains(n) ? ImGui.GetColorU32(ImGuiCol.Text) : line.Contains(n) ? ahead : other;
            var tp = new Num.Vector2(origin.X + graphW, origin.Y + i * rowH + (rowH - ImGui.GetTextLineHeight()) / 2 - 1);
            if (n.Label != null)
            {
                dl.AddText(tp, Colors.Accent, n.Label);
                tp.X += ImGui.CalcTextSize(n.Label).X + 8 * sc;
            }
            dl.AddText(tp, tc, text);
        }
        ImGui.SetCursorScreenPos(origin + new Num.Vector2(0, nodes.Count * rowH));
        ImGui.Dummy(new Num.Vector2(1, 1));

        if (_historyScrollVersion != history.Version)
        {
            _historyScrollVersion = history.Version;
            float y = row.TryGetValue(history.Current, out var r) ? r * rowH : 0;
            if (y < ImGui.GetScrollY() || y > ImGui.GetScrollY() + ImGui.GetWindowHeight() - rowH)
                ImGui.SetScrollY(Math.Max(0, y - ImGui.GetWindowHeight() / 2));
        }
        ImGui.EndChild();

        if (clicked != null && clicked != history.Current) Ed.JumpTo(clicked);
    }
}
