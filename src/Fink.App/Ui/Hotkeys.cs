using Fink.Editor;
using ImGuiNET;

namespace Fink.App;

public sealed partial class FinkGame
{
    // which scopes were active last frame (set while drawing the canvas and timeline)
    private bool _canvasHovered, _canvasActive, _timelineActive;
    private Mods _prevMods;
    private readonly List<Scope> _scopes = new();

    private static readonly (ImGuiMouseButton button, ImGuiKey key)[] ExtraMouseButtons =
    {
        (ImGuiMouseButton.Middle, ImGuiKey.MouseMiddle),
        ((ImGuiMouseButton)3, ImGuiKey.MouseX1),
        ((ImGuiMouseButton)4, ImGuiKey.MouseX2),
    };

    private static Mods CurrentMods()
    {
        var io = ImGui.GetIO();
        var m = Mods.None;
        if (io.KeyCtrl) m |= Mods.Ctrl;
        if (io.KeyShift) m |= Mods.Shift;
        if (io.KeyAlt) m |= Mods.Alt;
        return m;
    }

    /// <summary>
    /// Reads this frame's key and extra-mouse-button transitions from ImGui and feeds them to the
    /// dispatcher. Modifiers come from io.KeyCtrl/KeyShift/KeyAlt: the engine's renderer reports the
    /// left modifier keys as ImGui's Mod flags, not as LeftCtrl/LeftAlt key presses.
    /// </summary>
    private void PollHotkeys()
    {
        var io = ImGui.GetIO();
        var mods = CurrentMods();
        var hotkeys = _session.Hotkeys;
        hotkeys.Enabled = !io.WantTextInput && _capture == null;

        _scopes.Clear();
        if (_canvasHovered || _canvasActive) _scopes.Add(Scope.Canvas);
        if (_timelineActive) _scopes.Add(Scope.Timeline);
        _scopes.Add(Scope.Global);

        // modifiers on their own (hold Alt for the eyedropper)
        foreach (var (flag, key) in new[] { (Mods.Ctrl, ImGuiKey.LeftCtrl), (Mods.Shift, ImGuiKey.LeftShift), (Mods.Alt, ImGuiKey.LeftAlt) })
        {
            bool now = mods.HasFlag(flag), before = _prevMods.HasFlag(flag);
            if (now && !before) Dispatch(new KeyEvent(key, true), mods);
            if (!now && before) Dispatch(new KeyEvent(key, false), mods);
        }
        _prevMods = mods;

        foreach (var key in KeyNames.BindableKeys)
        {
            if (KeyChord.ModOf(key) != Mods.None || key is ImGuiKey.MouseMiddle or ImGuiKey.MouseX1 or ImGuiKey.MouseX2) continue;
            if (ImGui.IsKeyPressed(key, false)) Dispatch(new KeyEvent(key, true), mods);
            else if (ImGui.IsKeyPressed(key, true)) Dispatch(new KeyEvent(key, true, Repeat: true), mods);
            if (ImGui.IsKeyReleased(key)) Dispatch(new KeyEvent(key, false), mods);
        }

        foreach (var (button, key) in ExtraMouseButtons)
        {
            // presses only count over the canvas or timeline; a middle click on a panel is the panel's
            if (ImGui.IsMouseClicked(button) && (_canvasHovered || _timelineActive || _capture != null)) Dispatch(new KeyEvent(key, true), mods);
            if (ImGui.IsMouseReleased(button)) Dispatch(new KeyEvent(key, false), mods);
        }

        void Dispatch(KeyEvent e, Mods m)
        {
            if (_capture != null) { CaptureKey(e, m); return; }
            hotkeys.Handle(e, m, _scopes);
        }
    }

    /// <summary>The text shown for a command's shortcut: its first binding.</summary>
    private string ShortcutText(Command c)
    {
        var bindings = _session.Keymap.BindingsOf(c.Id);
        return bindings.Count == 0 ? "" : bindings[0].ToString();
    }

    private string TooltipFor(Command c)
    {
        var keys = string.Join(", ", _session.Keymap.BindingsOf(c.Id).Select(b => b.ToString()));
        var text = keys.Length > 0 ? $"{c.Title}  ({keys})" : c.Title;
        return c.Description.Length > 0 ? $"{text}\n{c.Description}" : text;
    }

    /// <summary>A button that runs a command, with its shortcut in the tooltip.</summary>
    private bool CommandButton(string id, string? label = null, System.Numerics.Vector2 size = default, bool small = false)
    {
        var c = _session.Commands.Get(id);
        bool disabled = !c.Enabled;
        if (disabled) ImGui.BeginDisabled();
        bool highlighted = c.IsChecked != null && c.Checked;
        if (highlighted) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
        bool clicked = small ? ImGui.SmallButton(label ?? c.Title) : ImGui.Button(label ?? c.Title, size);
        if (highlighted) ImGui.PopStyleColor();
        if (disabled) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(TooltipFor(c));
        if (clicked) c.Execute();
        return clicked;
    }

    /// <summary>A menu item that runs a command, showing its shortcut and check state.</summary>
    private void MenuCommand(string id, string? label = null)
    {
        var c = _session.Commands.Get(id);
        if (ImGui.MenuItem(label ?? c.Title, ShortcutText(c), c.IsChecked != null && c.Checked, c.Enabled)) c.Execute();
        if (c.Description.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal)) ImGui.SetTooltip(c.Description);
    }
}
