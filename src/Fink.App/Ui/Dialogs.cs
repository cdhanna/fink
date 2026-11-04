using Fink.Core;
using Fink.Editor;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

public sealed partial class FinkGame
{
    // ---------------------------------------------------------------- command palette

    private string _paletteQuery = "";
    private int _paletteSelection;
    private bool _paletteWasOpen;
    private int _paletteFrames;

    private void DrawCommandPalette()
    {
        if (!Ui.ShowCommandPalette) { _paletteWasOpen = false; return; }
        if (!_paletteWasOpen)
        {
            _paletteQuery = "";
            _paletteSelection = 0;
            _paletteFrames = 0;
            _paletteWasOpen = true;
        }

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos + new Num.Vector2(vp.WorkSize.X / 2, 60), ImGuiCond.Always, new Num.Vector2(0.5f, 0));
        ImGui.SetNextWindowSize(new Num.Vector2(640, 440) * Ui.UiScale, ImGuiCond.Always);
        ImGui.SetNextWindowFocus();
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking
                    | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse;
        ImGui.Begin("Command Palette", flags);

        if (_paletteFrames++ < 2) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##query", "Type a command...", ref _paletteQuery, 200)) _paletteSelection = 0;

        var words = _paletteQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _session.Commands.All
            .Where(c => c.Id != "app.commandPalette")
            .Where(c => words.All(w => $"{c.Category} {c.Title} {c.Id}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        _paletteSelection = Math.Clamp(_paletteSelection, 0, Math.Max(0, matches.Count - 1));

        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) _paletteSelection = Math.Min(matches.Count - 1, _paletteSelection + 1);
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) _paletteSelection = Math.Max(0, _paletteSelection - 1);
        Command? run = null;
        if ((ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) && matches.Count > 0) run = matches[_paletteSelection];
        bool close = ImGui.IsKeyPressed(ImGuiKey.Escape);

        if (ImGui.BeginChild("results"))
        {
            for (int i = 0; i < matches.Count; i++)
            {
                var c = matches[i];
                bool selected = i == _paletteSelection;
                if (!c.Enabled) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                var check = c.IsChecked != null ? (c.Checked ? "[x] " : "[ ] ") : "";
                if (ImGui.Selectable($"{c.Category}: {check}{c.Title}##{c.Id}", selected)) run = c;
                if (!c.Enabled) ImGui.PopStyleColor();
                if (selected && (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.IsKeyPressed(ImGuiKey.UpArrow))) ImGui.SetScrollHereY();

                var keys = ShortcutText(c);
                if (keys.Length > 0)
                {
                    var s = ImGui.GetStyle();
                    ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.CalcTextSize(keys).X - s.WindowPadding.X - s.ScrollbarSize - 4);
                    ImGui.TextDisabled(keys);
                }
            }
            if (matches.Count == 0) ImGui.TextDisabled("No commands match.");
        }
        ImGui.EndChild();

        if (_paletteFrames > 2 && !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) close = true;
        ImGui.End();

        if (run != null)
        {
            Ui.ShowCommandPalette = false;
            if (run.Enabled) run.Execute();
        }
        else if (close)
        {
            Ui.ShowCommandPalette = false;
        }
    }

    // ---------------------------------------------------------------- new document

    private int _newWidth = 128, _newHeight = 128;

    private void DrawNewDialog()
    {
        if (Ui.ShowNewDialog)
        {
            Ui.ShowNewDialog = false;
            ImGui.OpenPopup("New Animation");
        }
        bool open = true;
        if (!ImGui.BeginPopupModal("New Animation", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.Text("Canvas size in pixels");
        foreach (var s in new[] { 32, 64, 128, 256, 512 })
        {
            if (ImGui.Button($"{s}")) _newWidth = _newHeight = s;
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.SetNextItemWidth(160 * Ui.UiScale);
        ImGui.InputInt("Width", ref _newWidth);
        ImGui.SetNextItemWidth(160 * Ui.UiScale);
        ImGui.InputInt("Height", ref _newHeight);
        _newWidth = Math.Clamp(_newWidth, 1, 4096);
        _newHeight = Math.Clamp(_newHeight, 1, 4096);
        ImGui.Spacing();
        ImGui.TextDisabled("The current animation is discarded (saving comes in a later milestone).");
        if (ImGui.Button("Create") || ImGui.IsKeyPressed(ImGuiKey.Enter))
        {
            Ed.NewDocument(_newWidth, _newHeight);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- save workspace

    private void DrawSaveWorkspaceDialog()
    {
        if (Ui.ShowSaveWorkspace)
        {
            Ui.ShowSaveWorkspace = false;
            _newWorkspaceName = "";
            ImGui.OpenPopup("Save Workspace");
        }
        bool open = true;
        if (!ImGui.BeginPopupModal("Save Workspace", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.Text("Save the current panel arrangement as:");
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(260 * Ui.UiScale);
        bool enter = ImGui.InputTextWithHint("##name", "Name", ref _newWorkspaceName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        if ((ImGui.Button("Save") || enter) && _newWorkspaceName.Trim().Length > 0)
        {
            SaveCurrentWorkspace(_newWorkspaceName.Trim());
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
