using System.Diagnostics;
using Fink.Editor;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

public sealed partial class FinkGame
{
    /// <summary>Waiting for the user to press a chord: to rebind a command, or to search by key.</summary>
    private sealed class CaptureState
    {
        public string? CommandId;       // null: "find by key" in the search box
        public Binding? Replacing;      // null: adding a new binding
        public Scope Scope;
        public ImGuiKey? ModifierAlone; // a modifier went down; if it comes up alone, that's the chord
    }

    private CaptureState? _capture;
    private string _hotkeySearch = "";
    private float _scaleEdit = -1;
    private string _newWorkspaceName = "";

    /// <summary>Gets every key event while capturing (the dispatcher is off).</summary>
    private void CaptureKey(KeyEvent e, Mods mods)
    {
        var cap = _capture!;
        if (e.Down)
        {
            if (e.Repeat) return;
            if (e.Key == ImGuiKey.Escape) { _capture = null; return; }
            if (KeyChord.ModOf(e.Key) != Mods.None) { cap.ModifierAlone = e.Key; return; }
            Finish(KeyChord.Normalize(e.Key, mods));
        }
        else if (cap.ModifierAlone == e.Key)
        {
            Finish(KeyChord.Normalize(e.Key, mods));
        }

        void Finish(KeyChord chord)
        {
            _capture = null;
            if (cap.CommandId == null) { _hotkeySearch = chord.ToString(); return; }
            var binding = new Binding(chord, cap.Replacing?.Scope ?? cap.Scope);
            if (cap.Replacing is { } old) _session.Keymap.ReplaceBinding(cap.CommandId, old, binding);
            else _session.Keymap.AddBinding(cap.CommandId, binding);
            SaveKeymapQuietly();
        }
    }

    private void SaveKeymapQuietly()
    {
        if (_script != null) return; // automation never writes settings
        try { _session.Keymap.Save(_session.Paths.Keymap); }
        catch (Exception ex) { Ed.SetStatus($"Couldn't save keymap: {ex.Message}"); }
    }

    private void DrawOptionsWindow()
    {
        if (!Ui.ShowOptions) { if (_capture != null) _capture = null; return; }

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowSize(new Num.Vector2(900, 680) * Ui.UiScale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize / 2, ImGuiCond.FirstUseEver, new Num.Vector2(0.5f, 0.5f));
        bool open = true;
        if (ImGui.Begin("Options", ref open, ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse))
        {
            if (ImGui.BeginTabBar("optionsTabs"))
            {
                if (ImGui.BeginTabItem("Hotkeys")) { DrawHotkeysTab(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Interface")) { DrawInterfaceTab(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Workspaces")) { DrawWorkspacesTab(); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
        if (!open) { Ui.ShowOptions = false; _capture = null; }
    }

    // ---------------------------------------------------------------- hotkeys

    private void DrawHotkeysTab()
    {
        var keymap = _session.Keymap;
        bool findingKey = _capture is { CommandId: null };

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.45f);
        ImGui.InputTextWithHint("##search", "Search commands, categories or keys...", ref _hotkeySearch, 100);
        ImGui.SameLine();
        if (findingKey) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
        if (ImGui.Button(findingKey ? "Press a key... (Esc)" : "Find by key"))
            _capture = findingKey ? null : new CaptureState();
        if (findingKey) ImGui.PopStyleColor();
        ImGui.SameLine();
        if (_hotkeySearch.Length > 0 && ImGui.Button("Clear")) _hotkeySearch = "";
        ImGui.SameLine();
        if (ImGui.Button("Reset all...")) ImGui.OpenPopup("Reset all hotkeys?");
        ImGui.SameLine();
        if (ImGui.Button("Open settings folder")) OpenSettingsFolder();

        if (ImGui.BeginPopupModal("Reset all hotkeys?", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Every command goes back to its default keys.");
            if (ImGui.Button("Reset all")) { keymap.ResetAll(); SaveKeymapQuietly(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.TextDisabled("Click a shortcut to change it, right-click for its scope or to remove it, + to add another.");
        ImGui.TextDisabled("Scopes: Global works anywhere; Canvas only over the canvas; Timeline over the Timeline panel. Scoped keys win over Global ones.");

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("hotkeys", 3, flags, new Num.Vector2(0, -1))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthStretch, 0.42f);
        ImGui.TableSetupColumn("Shortcuts", ImGuiTableColumnFlags.WidthStretch, 0.58f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60 * Ui.UiScale);
        ImGui.TableHeadersRow();

        var words = _hotkeySearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        KeyChord.TryParse(_hotkeySearch, out var searchChord);
        bool searchIsChord = _hotkeySearch.Length > 0 && KeyChord.TryParse(_hotkeySearch, out _);

        foreach (var category in _session.Commands.Categories)
        {
            var commands = _session.Commands.All.Where(c => c.Category == category && Matches(c)).ToList();
            if (commands.Count == 0) continue;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Spacing();
            ImGui.TextColored(new Num.Vector4(0.7f, 0.6f, 1f, 1f), category.ToUpperInvariant());

            foreach (var c in commands) DrawHotkeyRow(c);
        }
        ImGui.EndTable();

        bool Matches(Command c)
        {
            if (words.Length == 0) return true;
            var bindings = keymap.BindingsOf(c.Id);
            if (searchIsChord && bindings.Any(b => b.Chord == searchChord)) return true;
            var hay = $"{c.Title} {c.Category} {c.Id} {c.Description} {string.Join(" ", bindings.Select(b => b.ToString()))}";
            return words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void DrawHotkeyRow(Command c)
    {
        var keymap = _session.Keymap;
        ImGui.PushID(c.Id);
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.Text(c.Title);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(c.Description.Length > 0 ? $"{c.Description}\n\n{c.Id}" : c.Id);
        if (c.IsHold) { ImGui.SameLine(); ImGui.TextDisabled("hold"); }

        ImGui.TableSetColumnIndex(1);
        var bindings = keymap.BindingsOf(c.Id);
        var conflicts = new List<(Binding binding, Command other)>();
        for (int i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];
            ImGui.PushID(i);
            var others = keymap.Conflicts(c.Id, b).ToList();
            foreach (var o in others) conflicts.Add((b, o));

            bool capturingThis = _capture is { } cap && cap.CommandId == c.Id && cap.Replacing == b;
            var label = capturingThis ? "Press keys... (Esc)" : b.ToString();
            if (others.Count > 0) ImGui.PushStyleColor(ImGuiCol.Button, new Num.Vector4(0.55f, 0.18f, 0.20f, 1f));
            else if (capturingThis) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            else ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]);
            if (ImGui.Button(label)) _capture = new CaptureState { CommandId = c.Id, Replacing = b, Scope = b.Scope };
            ImGui.PopStyleColor();
            if (others.Count > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip("Also bound to: " + string.Join(", ", others.Select(o => o.Title)));

            if (ImGui.BeginPopupContextItem("bindingMenu"))
            {
                ImGui.TextDisabled(b.Chord.ToString());
                ImGui.Separator();
                foreach (var scope in Enum.GetValues<Scope>())
                    if (ImGui.MenuItem($"Scope: {scope}", "", b.Scope == scope))
                    {
                        keymap.ReplaceBinding(c.Id, b, b with { Scope = scope });
                        SaveKeymapQuietly();
                    }
                ImGui.Separator();
                if (ImGui.MenuItem("Remove")) { keymap.RemoveBinding(c.Id, b); SaveKeymapQuietly(); }
                ImGui.EndPopup();
            }
            ImGui.SameLine();
            ImGui.PopID();
        }

        bool addingHere = _capture is { } cap2 && cap2.CommandId == c.Id && cap2.Replacing == null;
        if (addingHere)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            if (ImGui.Button("Press keys... (Esc)")) _capture = null;
            ImGui.PopStyleColor();
        }
        else if (ImGui.SmallButton("+"))
        {
            var scope = c.DefaultBindings.Count > 0 ? c.DefaultBindings[0].Scope : Scope.Global;
            _capture = new CaptureState { CommandId = c.Id, Scope = scope };
        }

        foreach (var (b, other) in conflicts)
        {
            ImGui.TextColored(new Num.Vector4(1f, 0.5f, 0.45f, 1f), $"{b} is also {other.Title}.");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove it there##{other.Id}{b}"))
            {
                keymap.RemoveBinding(other.Id, b);
                SaveKeymapQuietly();
            }
        }

        ImGui.TableSetColumnIndex(2);
        if (keymap.IsCustomized(c.Id) && ImGui.SmallButton("Reset"))
        {
            keymap.Reset(c.Id);
            SaveKeymapQuietly();
        }
        ImGui.PopID();
    }

    // ---------------------------------------------------------------- interface

    private void DrawInterfaceTab()
    {
        ImGui.SeparatorText("Display");
        if (_scaleEdit < 0) _scaleEdit = Ui.UiScale;
        ImGui.SetNextItemWidth(260 * Ui.UiScale);
        ImGui.SliderFloat("UI scale", ref _scaleEdit, 0.75f, 2.5f, "%.2fx");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Ui.UiScale = MathF.Round(_scaleEdit * 20) / 20;
            Ui.UiScaleDirty = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(applies when you let go)");
        Toggle("view.grid");
        Toggle("view.fullscreen");

        ImGui.SeparatorText("Drawing");
        Toggle("layer.autoSelect");
        Toggle("brush.pixelPerfect");

        ImGui.SeparatorText("Pen");
        bool penEraser = Ui.PenEraser;
        if (ImGui.Checkbox("Pen eraser end erases", ref penEraser)) Ui.PenEraser = penEraser;
        ImGui.TextDisabled(!PenInput.Hooked ? "  Not listening for pen input (Windows only)."
            : PenInput.PenSeen ? $"  Pen seen{(PenInput.Eraser ? ": eraser end now" : "")}."
            : "  No pen seen yet. Touch the screen area with the pen; if it never shows up, turn on Windows Ink in the XP-Pen driver.");
        ImGui.TextDisabled("  Or have the driver send a key for the eraser end and bind it to Eraser (hold) in Hotkeys.");

        ImGui.SeparatorText("Files");
        ImGui.TextDisabled(_session.Paths.Root);
        if (ImGui.Button("Open settings folder")) OpenSettingsFolder();
    }

    // ---------------------------------------------------------------- workspaces

    private void DrawWorkspacesTab()
    {
        ImGui.TextWrapped("A workspace is an arrangement of panels. The built-in ones can always be reset; " +
                          "save your own arrangement under a name to switch back to it later.");
        ImGui.SeparatorText("Built in");
        foreach (var name in Workspaces.BuiltIn)
        {
            var id = $"workspace.{name.ToLowerInvariant()}";
            CommandButton(id, $"{name}  ({ShortcutText(_session.Commands.Get(id))})");
            ImGui.SameLine();
        }
        ImGui.NewLine();

        ImGui.SeparatorText("Saved");
        var saved = _session.SavedWorkspaceNames().ToList();
        if (saved.Count == 0) ImGui.TextDisabled("None yet.");
        foreach (var name in saved)
        {
            ImGui.PushID(name);
            if (ImGui.Button("Use")) Ui.PendingWorkspace = name;
            ImGui.SameLine();
            if (ImGui.Button("Delete")) _session.DeleteWorkspace(name);
            ImGui.SameLine();
            ImGui.Text(name);
            ImGui.PopID();
        }

        ImGui.SeparatorText("Save current");
        ImGui.SetNextItemWidth(240 * Ui.UiScale);
        ImGui.InputTextWithHint("##wsname", "Name", ref _newWorkspaceName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Save") && _newWorkspaceName.Trim().Length > 0) SaveCurrentWorkspace(_newWorkspaceName.Trim());
    }

    private void SaveCurrentWorkspace(string name)
    {
        if (Workspaces.BuiltIn.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Ed.SetStatus($"\"{name}\" is a built-in workspace; pick another name.");
            return;
        }
        _session.SaveWorkspace(name, ImGui.SaveIniSettingsToMemory());
        Ui.CurrentWorkspace = name;
        Ed.SetStatus($"Saved workspace \"{name}\"");
        _newWorkspaceName = "";
    }

    private void OpenSettingsFolder()
    {
        Directory.CreateDirectory(_session.Paths.Root);
        try { Process.Start(new ProcessStartInfo("explorer.exe", _session.Paths.Root) { UseShellExecute = true }); }
        catch (Exception e) { Ed.SetStatus(e.Message); }
    }
}
