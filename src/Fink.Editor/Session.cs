using System.Text.Json;

namespace Fink.Editor;

/// <summary>Where Fink keeps its settings. Automation runs point this at a scratch folder.</summary>
public sealed class SettingsPaths
{
    public string Root { get; }

    public SettingsPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fink");
    }

    public string Settings => Path.Combine(Root, "settings.json");
    public string Keymap => Path.Combine(Root, "keymap.json");
    /// <summary>ImGui's own layout file: where every panel is docked.</summary>
    public string LayoutIni => Path.Combine(Root, "layout.ini");
    public string WorkspacesDir => Path.Combine(Root, "workspaces");
    public string ShaderPreview => Path.Combine(Root, "shader-preview.json");
    /// <summary>Your own themes: one JSON file each.</summary>
    public string ThemesDir => Path.Combine(Root, "themes");
    /// <summary>Autosaved unsaved work, for crash recovery.</summary>
    public string RecoveryDir => Path.Combine(Root, "recovery");
}

/// <summary>The persisted preferences (not the keymap, which has its own file).</summary>
public sealed class FinkSettings
{
    public float UiScale { get; set; } = 1f;
    public bool AutoSelectLayer { get; set; }
    public bool ShowGrid { get; set; } = true;
    public string Workspace { get; set; } = Workspaces.Draw;
    public Dictionary<string, bool> Panels { get; set; } = new();
    public int OnionBefore { get; set; } = 1;
    public int OnionAfter { get; set; } = 1;
    public float OnionOpacity { get; set; } = 0.35f;
    public bool PlayTag { get; set; } = true;
    public string ExportFolder { get; set; } = "";
    public string ExportName { get; set; } = "sprite";
    public int ExportColumns { get; set; }
    public int ExportPadding { get; set; }
    public List<string> RecentFiles { get; set; } = new();
    /// <summary>Flipping the pen to its eraser end switches to the Eraser while it's flipped.</summary>
    public bool PenEraser { get; set; } = true;
    public bool ShowTransport { get; set; } = true;
    public string Theme { get; set; } = Themes.Default;
    public int BrushSize { get; set; } = 1;
    public bool BrushSquare { get; set; }
    public bool BrushPixelPerfect { get; set; } = true;
    public int EraserSize { get; set; } = 3;
    public bool EraserSquare { get; set; }
    public bool EraserPixelPerfect { get; set; }
}

/// <summary>A saved workspace: ImGui's layout plus which panels are open.</summary>
public sealed class SavedWorkspace
{
    public string Name { get; set; } = "";
    public string Ini { get; set; } = "";
    public Dictionary<string, bool> Panels { get; set; } = new();
}

/// <summary>
/// One running editor: state, UI flags, commands, keymap and hotkeys, wired together. The window
/// owns one; tests and the headless script runner make their own.
/// </summary>
public sealed class Session
{
    public EditorState Editor { get; } = new();
    public UiState Ui { get; } = new();
    public CommandRegistry Commands { get; } = new();
    public Keymap Keymap { get; }
    public HotkeyDispatcher Hotkeys { get; }
    public SettingsPaths Paths { get; }
    /// <summary>The shader preview's file, inputs and options.</summary>
    public ShaderPreviewState Shader { get; private set; } = new();
    /// <summary>Autosave of unsaved work.</summary>
    public Recovery Recovery { get; }
    /// <summary>Unsaved work from runs that didn't end cleanly, found by <see cref="Load"/>.</summary>
    public List<RecoveryInfo> Leftovers { get; private set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Session(SettingsPaths? paths = null)
    {
        Paths = paths ?? new SettingsPaths();
        Recovery = new Recovery(Paths.RecoveryDir);
        EditorCommands.RegisterAll(Commands, Editor, Ui);
        Keymap = new Keymap(Commands);
        Hotkeys = new HotkeyDispatcher(Commands, Keymap);
    }

    /// <summary>Loads settings and keymap; returns any problems (unknown commands, bad chords).</summary>
    public List<string> Load()
    {
        var problems = new List<string>();
        try
        {
            if (File.Exists(Paths.Settings))
            {
                var s = JsonSerializer.Deserialize<FinkSettings>(File.ReadAllText(Paths.Settings)) ?? new();
                Ui.UiScale = Math.Clamp(s.UiScale, 0.5f, 3f);
                Editor.AutoSelectLayer = s.AutoSelectLayer;
                Editor.View.ShowGrid = s.ShowGrid;
                Ui.CurrentWorkspace = s.Workspace;
                foreach (var (k, v) in s.Panels) if (Ui.Panels.ContainsKey(k)) Ui.Panels[k] = v;
                Editor.Onion.Before = s.OnionBefore;
                Editor.Onion.After = s.OnionAfter;
                Editor.Onion.Opacity = s.OnionOpacity;
                Editor.PlayTag = s.PlayTag;
                Editor.ExportFolder = s.ExportFolder;
                Editor.ExportOptions.Name = s.ExportName;
                Editor.ExportOptions.Columns = s.ExportColumns;
                Editor.ExportOptions.Padding = s.ExportPadding;
                Ui.RecentFiles = s.RecentFiles.Where(File.Exists).ToList();
                Ui.PenEraser = s.PenEraser;
                Ui.ShowTransport = s.ShowTransport;
                Ui.ThemeName = s.Theme;
                Editor.ToolSettings.Size = Math.Clamp(s.BrushSize, 1, 64);
                Editor.ToolSettings.SquareTip = s.BrushSquare;
                Editor.ToolSettings.PixelPerfect = s.BrushPixelPerfect;
                Editor.EraserSettings.Size = Math.Clamp(s.EraserSize, 1, 64);
                Editor.EraserSettings.SquareTip = s.EraserSquare;
                Editor.EraserSettings.PixelPerfect = s.EraserPixelPerfect;
            }
        }
        catch (Exception e) { problems.Add($"settings.json: {e.Message}"); }

        Shader = ShaderPreviewState.Load(Paths.ShaderPreview);
        Leftovers = Recovery.FindLeftovers();
        try { problems.AddRange(Keymap.Load(Paths.Keymap).Select(p => $"keymap.json: {p}")); }
        catch (Exception e) { problems.Add($"keymap.json: {e.Message}"); }
        return problems;
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Root);
        var s = new FinkSettings
        {
            UiScale = Ui.UiScale,
            AutoSelectLayer = Editor.AutoSelectLayer,
            ShowGrid = Editor.View.ShowGrid,
            Workspace = Ui.CurrentWorkspace,
            Panels = new Dictionary<string, bool>(Ui.Panels),
            OnionBefore = Editor.Onion.Before,
            OnionAfter = Editor.Onion.After,
            OnionOpacity = Editor.Onion.Opacity,
            PlayTag = Editor.PlayTag,
            ExportFolder = Editor.ExportFolder,
            ExportName = Editor.ExportOptions.Name,
            ExportColumns = Editor.ExportOptions.Columns,
            ExportPadding = Editor.ExportOptions.Padding,
            RecentFiles = Ui.RecentFiles,
            PenEraser = Ui.PenEraser,
            ShowTransport = Ui.ShowTransport,
            Theme = Ui.ThemeName,
            BrushSize = Editor.ToolSettings.Size,
            BrushSquare = Editor.ToolSettings.SquareTip,
            BrushPixelPerfect = Editor.ToolSettings.PixelPerfect,
            EraserSize = Editor.EraserSettings.Size,
            EraserSquare = Editor.EraserSettings.SquareTip,
            EraserPixelPerfect = Editor.EraserSettings.PixelPerfect,
        };
        File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(s, JsonOptions));
        Keymap.Save(Paths.Keymap);
        Shader.Save(Paths.ShaderPreview);
    }

    // ------------------------------------------------------------------ user workspaces

    public IEnumerable<string> SavedWorkspaceNames() =>
        Directory.Exists(Paths.WorkspacesDir)
            ? Directory.GetFiles(Paths.WorkspacesDir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order()
            : Enumerable.Empty<string>();

    public void SaveWorkspace(string name, string ini)
    {
        Directory.CreateDirectory(Paths.WorkspacesDir);
        var w = new SavedWorkspace { Name = name, Ini = ini, Panels = new Dictionary<string, bool>(Ui.Panels) };
        File.WriteAllText(Path.Combine(Paths.WorkspacesDir, SafeName(name) + ".json"), JsonSerializer.Serialize(w, JsonOptions));
    }

    public SavedWorkspace? LoadWorkspace(string name)
    {
        var path = Path.Combine(Paths.WorkspacesDir, SafeName(name) + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SavedWorkspace>(File.ReadAllText(path)) : null;
    }

    public void DeleteWorkspace(string name)
    {
        var path = Path.Combine(Paths.WorkspacesDir, SafeName(name) + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}
