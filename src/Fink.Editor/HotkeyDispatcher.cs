using ImGuiNET;

namespace Fink.Editor;

/// <summary>
/// Turns key events into commands. A chord is looked up in the active scopes, most specific
/// first, then Global. Hold commands get their release when the same key comes back up, wherever
/// the mouse is by then.
/// </summary>
public sealed class HotkeyDispatcher
{
    private readonly CommandRegistry _commands;
    private readonly Keymap _keymap;
    private readonly Dictionary<ImGuiKey, Command> _held = new();

    /// <summary>Set to false while something else owns the keyboard (text fields, rebinding).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The last command run by a key, for the status line and tests.</summary>
    public string? LastCommandId { get; private set; }

    public HotkeyDispatcher(CommandRegistry commands, Keymap keymap)
    {
        _commands = commands;
        _keymap = keymap;
    }

    /// <summary>
    /// Feeds one key event. <paramref name="activeScopes"/> lists the scopes that apply right now,
    /// most specific first; Global is always tried last. Returns whether a command ran.
    /// </summary>
    public bool Handle(KeyEvent e, Mods mods, IReadOnlyList<Scope> activeScopes)
    {
        var chord = KeyChord.Normalize(e.Key, mods);

        if (!e.Down)
        {
            if (_held.Remove(chord.Key, out var held)) { held.OnRelease!(); return true; }
            return false;
        }

        if (!Enabled) return false;
        if (_held.ContainsKey(chord.Key)) return false; // key repeat of a held command

        var command = Resolve(chord, activeScopes);
        if (command == null || !command.Enabled) return false;
        if (e.Repeat && !command.Repeat) return false;

        command.Execute();
        LastCommandId = command.Id;
        if (command.IsHold) _held[chord.Key] = command;
        return true;
    }

    public Command? Resolve(KeyChord chord, IReadOnlyList<Scope> activeScopes)
    {
        // A disabled command lets the key fall through to the next scope: Enter over the canvas
        // applies a transform while one is floating, and plays the animation otherwise.
        foreach (var scope in activeScopes)
        {
            if (scope == Scope.Global) continue;
            var c = _keymap.CommandsFor(new Binding(chord, scope)).FirstOrDefault(c => c.Enabled);
            if (c != null) return c;
        }
        return _keymap.CommandsFor(new Binding(chord, Scope.Global)).FirstOrDefault(c => c.Enabled);
    }

    /// <summary>Releases every held command (focus lost, script reset).</summary>
    public void ReleaseAll()
    {
        foreach (var c in _held.Values) c.OnRelease!();
        _held.Clear();
    }

    public bool IsHeld(string commandId) => _held.Values.Any(c => c.Id == commandId);
}
