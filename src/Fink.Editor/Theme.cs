using System.Text.Json;
using System.Text.Json.Serialization;
using Fink.Core;

namespace Fink.Editor;

/// <summary>
/// How Fink looks: an ImGui base style, any of its colors replaced, corner rounding, and the
/// colors Fink draws itself (canvas, checkerboard, onion skin, timeline, highlights).
/// Built-in themes are in <see cref="Themes"/>; your own are JSON files in the settings
/// folder's themes/ directory, in the same shape, and reload as you save them.
/// </summary>
public sealed class Theme
{
    public string Name { get; set; } = "Theme";
    /// <summary>"dark", "light" or "classic": ImGui's own style to start from.</summary>
    public string Base { get; set; } = "dark";
    /// <summary>Corner rounding, in pixels at 100% UI scale.</summary>
    public float Rounding { get; set; } = 4;
    /// <summary>ImGui colors to replace, by ImGuiCol name ("WindowBg", "Button"...), as #rrggbb or #rrggbbaa.</summary>
    public Dictionary<string, string> Ui { get; set; } = new();
    public ThemeCanvas Canvas { get; set; } = new();

    [JsonIgnore] public bool BuiltIn { get; set; }
    /// <summary>Where a custom theme was read from.</summary>
    [JsonIgnore] public string? File { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public Theme Copy(string name)
    {
        var t = JsonSerializer.Deserialize<Theme>(JsonSerializer.Serialize(this, Json), Json)!;
        t.Name = name;
        return t;
    }
}

/// <summary>The colors Fink draws itself, as #rrggbb or #rrggbbaa.</summary>
public sealed class ThemeCanvas
{
    /// <summary>Behind the canvas (and the atlas view).</summary>
    public string Background { get; set; } = "#1e1a1c";
    /// <summary>The two squares of the checkerboard behind transparent pixels.</summary>
    public string CheckerLight { get; set; } = "#f2f2f2";
    public string CheckerDark { get; set; } = "#cccccc";
    /// <summary>Onion skin: drawings before and after.</summary>
    public string OnionBefore { get; set; } = "#e64646";
    public string OnionAfter { get; set; } = "#3caa5a";
    /// <summary>Pinned frames' ghosts.</summary>
    public string OnionPinned { get; set; } = "#468cff";
    /// <summary>Layer roles, in the timeline and layer list.</summary>
    public string Line { get; set; } = "#8cbfff";
    public string Fill { get; set; } = "#ffbf73";
    public string Reference { get; set; } = "#999999";
    /// <summary>The current frame's column in the timeline.</summary>
    public string Playhead { get; set; } = "#ffd08028";
    /// <summary>Highlights: what you're dragging, keyed points, history names, your asset in the atlas.</summary>
    public string Accent { get; set; } = "#ffd040";
}

/// <summary>The themes that come with Fink.</summary>
public static class Themes
{
    public const string Default = "Fink Purple";

    public static readonly Theme[] BuiltIn =
    {
        new()
        {
            Name = "Fink Purple", Base = "dark", Rounding = 4, BuiltIn = true,
            Ui = new()
            {
                ["WindowBg"] = "#211f26", ["ChildBg"] = "#1c1a21", ["PopupBg"] = "#26242efa",
                ["TitleBg"] = "#1a171f", ["TitleBgActive"] = "#292436", ["MenuBarBg"] = "#1a171f",
                ["Header"] = "#5445808c", ["HeaderHovered"] = "#66549ecc", ["HeaderActive"] = "#735eb3",
                ["Button"] = "#3d384d", ["ButtonHovered"] = "#5c4d85", ["ButtonActive"] = "#755eb8",
                ["FrameBg"] = "#302e3d", ["FrameBgHovered"] = "#403b52", ["FrameBgActive"] = "#4d4566",
                ["Tab"] = "#292433", ["TabHovered"] = "#66549e", ["TabSelected"] = "#4d4073",
                ["TabDimmed"] = "#211f29", ["TabDimmedSelected"] = "#38304d",
                ["SliderGrab"] = "#8c73d9", ["SliderGrabActive"] = "#a68cf2", ["CheckMark"] = "#b399ff",
                ["DockingPreview"] = "#8c73d999", ["Separator"] = "#474257",
            },
            Canvas = new() { Background = "#1e1a1c" },
        },
        new()
        {
            // no tint anywhere, so colors in the art are judged against neutral gray
            Name = "Neutral Gray", Base = "dark", Rounding = 3, BuiltIn = true,
            Ui = new()
            {
                ["WindowBg"] = "#2b2b2b", ["ChildBg"] = "#262626", ["PopupBg"] = "#2e2e2efa",
                ["TitleBg"] = "#222222", ["TitleBgActive"] = "#2f2f2f", ["MenuBarBg"] = "#222222",
                ["Header"] = "#5a5a5a80", ["HeaderHovered"] = "#6a6a6acc", ["HeaderActive"] = "#7a7a7a",
                ["Button"] = "#404040", ["ButtonHovered"] = "#555555", ["ButtonActive"] = "#6a6a6a",
                ["FrameBg"] = "#383838", ["FrameBgHovered"] = "#444444", ["FrameBgActive"] = "#505050",
                ["Tab"] = "#303030", ["TabHovered"] = "#5a5a5a", ["TabSelected"] = "#474747",
                ["TabDimmed"] = "#2a2a2a", ["TabDimmedSelected"] = "#3a3a3a",
                ["SliderGrab"] = "#9a9a9a", ["SliderGrabActive"] = "#bdbdbd", ["CheckMark"] = "#d8d8d8",
                ["DockingPreview"] = "#9a9a9a99", ["Separator"] = "#4a4a4a",
                ["TextSelectedBg"] = "#80808066", ["NavCursor"] = "#bdbdbd",
            },
            Canvas = new()
            {
                Background = "#808080", CheckerLight = "#9c9c9c", CheckerDark = "#8a8a8a",
                Line = "#a8c4e6", Fill = "#e6c8a0", Reference = "#9a9a9a", Playhead = "#ffffff20", Accent = "#e0e0e0",
            },
        },
        new()
        {
            Name = "Dark", Base = "dark", Rounding = 0, BuiltIn = true,
            Canvas = new() { Background = "#141414", Playhead = "#4296fa30", Accent = "#4296fa" },
        },
        new()
        {
            Name = "Light", Base = "light", Rounding = 3, BuiltIn = true,
            Ui = new() { ["WindowBg"] = "#f2f2f2", ["ChildBg"] = "#ebebeb", ["MenuBarBg"] = "#e0e0e0" },
            Canvas = new()
            {
                Background = "#d6d6d6", CheckerLight = "#ffffff", CheckerDark = "#e4e4e4",
                Line = "#2f6fc4", Fill = "#c47a1f", Reference = "#777777", Playhead = "#2f6fc425", Accent = "#d08a00",
            },
        },
        new()
        {
            Name = "Classic", Base = "classic", Rounding = 0, BuiltIn = true,
            Canvas = new() { Background = "#2a2a3a", Playhead = "#8080ff30", Accent = "#a0a0ff" },
        },
    };

    /// <summary>The built-in themes, then every theme in <paramref name="folder"/> (custom ones win on a name clash).</summary>
    public static List<Theme> All(string folder)
    {
        var list = BuiltIn.ToList();
        foreach (var t in LoadFolder(folder))
        {
            list.RemoveAll(b => b.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
            list.Add(t);
        }
        return list;
    }

    public static IEnumerable<Theme> LoadFolder(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => f))
        {
            Theme? t = null;
            try { t = Load(file); } catch { /* a broken file is skipped; Options says so */ }
            if (t != null) yield return t;
        }
    }

    public static Theme Load(string file)
    {
        var t = JsonSerializer.Deserialize<Theme>(File.ReadAllText(file), Theme.Json) ?? throw new InvalidDataException("empty theme");
        if (string.IsNullOrWhiteSpace(t.Name)) t.Name = Path.GetFileNameWithoutExtension(file);
        t.File = file;
        return t;
    }

    /// <summary>Writes a copy of a theme to edit, and returns its path.</summary>
    public static string SaveCopy(Theme theme, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = theme.BuiltIn ? $"{theme.Name} (mine)" : $"{theme.Name} copy";
        var path = Path.Combine(folder, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} {i}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(theme.Copy(Path.GetFileNameWithoutExtension(path)), Theme.Json));
        return path;
    }

    /// <summary>A theme color as packed RGBA (what both ImGui and the canvas take); fallback when it doesn't parse.</summary>
    public static uint Packed(string hex, uint fallback)
    {
        try { return Rgba.FromHex(hex).Packed; }
        catch { return fallback; }
    }
}
