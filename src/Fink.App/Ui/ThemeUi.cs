using Fink.Core;
using Fink.Editor;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

/// <summary>Theme colors as packed values, ready for ImGui's draw lists and the canvas.</summary>
public sealed record ThemeColors(uint Background, uint CheckerLight, uint CheckerDark, uint Line, uint Fill, uint Reference,
    uint Playhead, uint Accent);

/// <summary>Applying themes: ImGui's style, Fink's own colors, and live reload of theme files.</summary>
public sealed partial class FinkGame
{
    private Theme _theme = Themes.BuiltIn[0];
    private ThemeColors Colors { get; set; } = Resolve(Themes.BuiltIn[0]);
    private string _appliedTheme = "";
    private DateTime _themeStamp;
    private double _nextThemePoll;
    private string? _themeError;

    private static ThemeColors Resolve(Theme t) => new(
        Themes.Packed(t.Canvas.Background, 0xFF1C1A1Eu),
        Themes.Packed(t.Canvas.CheckerLight, 0xFFF2F2F2u),
        Themes.Packed(t.Canvas.CheckerDark, 0xFFCCCCCCu),
        Themes.Packed(t.Canvas.Line, 0xFFFFBF8Cu),
        Themes.Packed(t.Canvas.Fill, 0xFF73BFFFu),
        Themes.Packed(t.Canvas.Reference, 0xFF999999u),
        Themes.Packed(t.Canvas.Playhead, 0x2880D0FFu),
        Themes.Packed(t.Canvas.Accent, 0xFF40D0FFu));

    private static Num.Vector4 Vec(uint packed) => new((packed & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f,
        ((packed >> 16) & 0xFF) / 255f, (packed >> 24) / 255f);

    private static uint WithAlpha(uint packed, uint alpha) => (packed & 0x00FFFFFFu) | (alpha << 24);

    /// <summary>The text color, faded: lines and shading that show on dark and light themes alike.</summary>
    private static uint Faint(uint alpha) => WithAlpha(ImGui.GetColorU32(ImGuiCol.Text), alpha);

    /// <summary>The theme to use: the one named in settings, else the default.</summary>
    private Theme FindTheme(string name) =>
        Themes.All(_session.Paths.ThemesDir).FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? Themes.BuiltIn[0];

    /// <summary>Every frame: switch themes when asked, and reload a theme file when it's saved.</summary>
    private void UpdateTheme()
    {
        if (_appliedTheme != Ui.ThemeName)
        {
            _theme = FindTheme(Ui.ThemeName);
            _themeStamp = _theme.File != null ? File.GetLastWriteTimeUtc(_theme.File) : default;
            _appliedTheme = Ui.ThemeName;
            Ui.UiScaleDirty = true;
            _appliedScale = -1; // forces ApplyUiScale to restyle
            return;
        }
        if (_theme.File == null || Ed.Time < _nextThemePoll) return;
        _nextThemePoll = Ed.Time + 0.5;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(_theme.File);
            if (stamp == _themeStamp) return;
            _themeStamp = stamp;
            var t = Themes.Load(_theme.File);
            t.Name = _theme.Name; // keep it selected even if the name inside changed
            _theme = t;
            _themeError = null;
            Ui.UiScaleDirty = true;
            _appliedScale = -1;
        }
        catch (Exception e)
        {
            _themeError = $"{Path.GetFileName(_theme.File)}: {e.Message}";
        }
    }

    /// <summary>The style: ImGui's base, then the theme's colors and rounding. Sizes come from the UI scale.</summary>
    private void ApplyThemeStyle(float scale)
    {
        switch (_theme.Base.ToLowerInvariant())
        {
            case "light": ImGui.StyleColorsLight(); break;
            case "classic": ImGui.StyleColorsClassic(); break;
            default: ImGui.StyleColorsDark(); break;
        }
        var s = ImGui.GetStyle();
        float r = Math.Max(0, _theme.Rounding) * scale;
        s.WindowRounding = r;
        s.ChildRounding = r;
        s.FrameRounding = r * 0.75f;
        s.PopupRounding = r;
        s.ScrollbarRounding = r * 1.5f;
        s.GrabRounding = r * 0.75f;
        s.TabRounding = r;
        var c = s.Colors;
        foreach (var (name, hex) in _theme.Ui)
            if (Enum.TryParse<ImGuiCol>(name, true, out var col) && (int)col < c.Count)
                c[(int)col] = Vec(Themes.Packed(hex, ImGui.GetColorU32(col)));

        Colors = Resolve(_theme);
        Ed.Onion.BeforeTint = Rgba.FromHex(_theme.Canvas.OnionBefore is { Length: > 0 } b ? b : "#e64646");
        Ed.Onion.AfterTint = Rgba.FromHex(_theme.Canvas.OnionAfter is { Length: > 0 } a ? a : "#3caa5a");
        Ed.Onion.PinTint = Rgba.FromHex(_theme.Canvas.OnionPinned is { Length: > 0 } p ? p : "#468cff");
    }

    /// <summary>Options > Interface: the theme picker.</summary>
    private void DrawThemeOptions()
    {
        ImGui.SeparatorText("Theme");
        var all = Themes.All(_session.Paths.ThemesDir);
        ImGui.SetNextItemWidth(220 * Ui.UiScale);
        if (ImGui.BeginCombo("##theme", _theme.Name))
        {
            foreach (var t in all)
            {
                if (ImGui.Selectable($"{t.Name}{(t.BuiltIn ? "" : "   (yours)")}##{t.Name}", t.Name == _theme.Name))
                    Ui.ThemeName = t.Name;
                if (ImGui.IsItemHovered() && t.File != null) ImGui.SetTooltip(t.File);
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Make a copy to edit"))
        {
            var path = Themes.SaveCopy(_theme, _session.Paths.ThemesDir);
            Ui.ThemeName = Path.GetFileNameWithoutExtension(path);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { /* no editor for .json: the file is still there */ }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Writes this theme to a JSON file in your settings folder's themes/ and opens it.\nSave it and Fink restyles at once.");
        ImGui.SameLine();
        if (ImGui.Button("Themes folder"))
        {
            Directory.CreateDirectory(_session.Paths.ThemesDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_session.Paths.ThemesDir) { UseShellExecute = true });
        }
        ImGui.TextDisabled(_theme.BuiltIn
            ? "Built in. Make a copy to change its colors: the UI's (by ImGui color name) and Fink's own (canvas, onion skin, timeline)."
            : $"Yours: {_theme.File}. Changes show when you save the file.");
        if (_themeError != null) ImGui.TextColored(new Num.Vector4(1, 0.45f, 0.45f, 1), _themeError);
    }
}
