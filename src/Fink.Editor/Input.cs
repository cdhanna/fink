using ImGuiNET;

namespace Fink.Editor;

[Flags]
public enum Mods
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>
/// Where a binding applies. Scoped bindings win over Global ones while their scope is active
/// (the mouse is over the canvas, or the timeline is hovered/focused).
/// </summary>
public enum Scope
{
    Global,
    Canvas,
    Timeline,
}

/// <summary>A key (or mouse button) plus modifiers, e.g. Ctrl+Shift+Z, or Alt on its own.</summary>
public readonly record struct KeyChord(ImGuiKey Key, Mods Mods)
{
    /// <summary>
    /// Right-hand modifier keys become left-hand ones, and a modifier key pressed on its own does
    /// not count as its own modifier (pressing Alt gives the chord "Alt", not "Alt+Alt").
    /// </summary>
    public static KeyChord Normalize(ImGuiKey key, Mods mods)
    {
        key = key switch
        {
            ImGuiKey.RightCtrl => ImGuiKey.LeftCtrl,
            ImGuiKey.RightShift => ImGuiKey.LeftShift,
            ImGuiKey.RightAlt => ImGuiKey.LeftAlt,
            _ => key,
        };
        mods &= ~ModOf(key);
        return new KeyChord(key, mods);
    }

    public static Mods ModOf(ImGuiKey key) => key switch
    {
        ImGuiKey.LeftCtrl or ImGuiKey.RightCtrl => Mods.Ctrl,
        ImGuiKey.LeftShift or ImGuiKey.RightShift => Mods.Shift,
        ImGuiKey.LeftAlt or ImGuiKey.RightAlt => Mods.Alt,
        _ => Mods.None,
    };

    public bool IsModifierOnly => ModOf(Key) != Mods.None;

    /// <summary>For people: "Ctrl+Shift+Z", "Alt", "Mouse 4", ",".</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Mods.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
        if (Mods.HasFlag(Mods.Shift)) parts.Add("Shift");
        if (Mods.HasFlag(Mods.Alt)) parts.Add("Alt");
        parts.Add(KeyNames.Display(Key));
        return string.Join("+", parts);
    }

    /// <summary>For files: "Ctrl+Shift+Z", "Alt", "MouseX1", "Comma". Always round-trips.</summary>
    public string Serialize()
    {
        var parts = new List<string>(4);
        if (Mods.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
        if (Mods.HasFlag(Mods.Shift)) parts.Add("Shift");
        if (Mods.HasFlag(Mods.Alt)) parts.Add("Alt");
        parts.Add(KeyNames.Serialize(Key));
        return string.Join("+", parts);
    }

    /// <summary>Accepts either form, case-insensitive. "Alt" alone is the Alt key.</summary>
    public static KeyChord Parse(string text)
    {
        if (!TryParse(text, out var chord)) throw new FormatException($"Not a key chord: \"{text}\"");
        return chord;
    }

    public static bool TryParse(string text, out KeyChord chord)
    {
        chord = default;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var mods = Mods.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Mods.Ctrl; break;
                case "shift": mods |= Mods.Shift; break;
                case "alt": mods |= Mods.Alt; break;
                default: return false;
            }
        }
        if (!KeyNames.TryParse(parts[^1], out var key)) return false;
        chord = Normalize(key, mods);
        return true;
    }
}

/// <summary>A chord in a scope: one entry in a command's list of bindings.</summary>
public readonly record struct Binding(KeyChord Chord, Scope Scope = Scope.Global)
{
    public override string ToString() => Scope == Scope.Global ? Chord.ToString() : $"{Chord} ({Scope})";

    public string Serialize() => Scope == Scope.Global ? Chord.Serialize() : $"{Chord.Serialize()}@{Scope}";

    public static Binding Parse(string text)
    {
        var at = text.LastIndexOf('@');
        if (at < 0) return new Binding(KeyChord.Parse(text));
        return new Binding(KeyChord.Parse(text[..at]), Enum.Parse<Scope>(text[(at + 1)..], ignoreCase: true));
    }
}

public static class KeyNames
{
    private static readonly Dictionary<ImGuiKey, string> DisplayNames = new()
    {
        [ImGuiKey.LeftCtrl] = "Ctrl", [ImGuiKey.LeftShift] = "Shift", [ImGuiKey.LeftAlt] = "Alt",
        [ImGuiKey.Comma] = ",", [ImGuiKey.Period] = ".", [ImGuiKey.Slash] = "/", [ImGuiKey.Backslash] = "\\",
        [ImGuiKey.Semicolon] = ";", [ImGuiKey.Apostrophe] = "'", [ImGuiKey.GraveAccent] = "`",
        [ImGuiKey.LeftBracket] = "[", [ImGuiKey.RightBracket] = "]", [ImGuiKey.Minus] = "-", [ImGuiKey.Equal] = "=",
        [ImGuiKey.UpArrow] = "Up", [ImGuiKey.DownArrow] = "Down", [ImGuiKey.LeftArrow] = "Left", [ImGuiKey.RightArrow] = "Right",
        [ImGuiKey.PageUp] = "PgUp", [ImGuiKey.PageDown] = "PgDn", [ImGuiKey.Escape] = "Esc", [ImGuiKey.Delete] = "Del",
        [ImGuiKey.KeypadAdd] = "Num Plus", [ImGuiKey.KeypadSubtract] = "Num Minus", [ImGuiKey.KeypadMultiply] = "Num *",
        [ImGuiKey.KeypadDivide] = "Num /", [ImGuiKey.KeypadEnter] = "Num Enter", [ImGuiKey.KeypadDecimal] = "Num .",
        [ImGuiKey.MouseMiddle] = "Middle Mouse", [ImGuiKey.MouseX1] = "Mouse 4", [ImGuiKey.MouseX2] = "Mouse 5",
    };

    private static Dictionary<string, ImGuiKey> BuildAliases()
    {
        var d = new Dictionary<string, ImGuiKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in BindableKeys) d[Serialize(key)] = key;
        foreach (var (key, name) in DisplayNames) d.TryAdd(name, key);
        d["Ctrl"] = ImGuiKey.LeftCtrl;
        d["Shift"] = ImGuiKey.LeftShift;
        d["Alt"] = ImGuiKey.LeftAlt;
        return d;
    }

    /// <summary>
    /// Every bindable key with its file name: keyboard keys and the extra mouse buttons (left and
    /// right always draw, so they are not bindable). Built from the enum's NAMES because several
    /// share a value with a marker (Tab == NamedKey_BEGIN), and Enum.GetName may pick the marker.
    /// </summary>
    private static readonly Dictionary<ImGuiKey, string> FileNames = BuildFileNames();

    private static Dictionary<ImGuiKey, string> BuildFileNames()
    {
        var d = new Dictionary<ImGuiKey, string>();
        foreach (var name in Enum.GetNames<ImGuiKey>())
        {
            var k = Enum.Parse<ImGuiKey>(name);
            bool extraMouse = k is ImGuiKey.MouseMiddle or ImGuiKey.MouseX1 or ImGuiKey.MouseX2;
            if (!extraMouse)
            {
                if ((int)k < (int)ImGuiKey.Tab) continue;
                if (name.StartsWith("Gamepad") || name.StartsWith("Mouse") || name.StartsWith("Reserved")
                    || name.StartsWith("Mod") || name.StartsWith("NamedKey") || name.Contains("COUNT")
                    || name is "RightCtrl" or "RightShift" or "RightAlt" or "Menu"
                    || name.Contains("Super") || name.StartsWith("App"))
                    continue;
            }
            d.TryAdd(k, name.StartsWith('_') ? name[1..] : name); // "_1" -> "1"
        }
        return d;
    }

    public static IReadOnlyList<ImGuiKey> BindableKeys { get; } = FileNames.Keys.ToArray();

    // after FileNames: static fields initialize in order
    private static readonly Dictionary<string, ImGuiKey> Aliases = BuildAliases();

    public static string Display(ImGuiKey key) =>
        DisplayNames.TryGetValue(key, out var n) ? n : Serialize(key);

    public static string Serialize(ImGuiKey key) =>
        FileNames.TryGetValue(key, out var n) ? n : ((int)key).ToString();

    public static bool TryParse(string text, out ImGuiKey key) => Aliases.TryGetValue(text.Trim(), out key);
}

/// <summary>One key event, as the dispatcher sees it.</summary>
public readonly record struct KeyEvent(ImGuiKey Key, bool Down, bool Repeat = false);
