namespace Fink.Editor;

/// <summary>
/// UI requests and toggles that commands set and the window acts on: which panels are open,
/// dialogs, workspace switches, fullscreen. Plain data, so commands stay testable without a window.
/// </summary>
public sealed class UiState
{
    public const string Canvas = "Canvas";
    public static readonly string[] PanelNames = { "Tools", "Palette", "Layers", "Timeline", "History", "Shader", "Atlas", "Assets" };

    public Dictionary<string, bool> Panels { get; } = PanelNames.ToDictionary(p => p, _ => true);

    public bool ShowOptions { get; set; }
    public bool ShowCommandPalette { get; set; }
    public bool ShowNewDialog { get; set; }
    public bool ShowExport { get; set; }
    public bool ShowSaveWorkspace { get; set; }
    public bool ShowNewAsset { get; set; }
    public bool ShowAssetPicker { get; set; }
    public bool ShowRenameAsset { get; set; }
    /// <summary>Asks the layer lists to start renaming the current layer in place.</summary>
    public bool RenameLayer { get; set; }
    public bool ShowDeleteAsset { get; set; }
    public bool ShowRecovery { get; set; }

    /// <summary>The window shows a file dialog for these, then opens or saves.</summary>
    public bool RequestOpen { get; set; }
    public bool RequestSaveAs { get; set; }
    /// <summary>Runs after a Save As succeeds (e.g. the action an unsaved-changes prompt was guarding).</summary>
    public Action? AfterSaveAs { get; set; }

    /// <summary>
    /// "Save changes?" before something that would lose them (new, open, quit). The window
    /// asks; Save or Don't Save then runs this, Cancel drops it.
    /// </summary>
    public Action? PendingUnsaved { get; set; }

    /// <summary>Runs <paramref name="action"/> now, or after asking about unsaved changes.</summary>
    public void Guard(EditorState ed, Action action)
    {
        if (ed.IsDirty) PendingUnsaved = action;
        else action();
    }

    /// <summary>Flipping the pen to its eraser end erases (Windows Ink pen messages).</summary>
    public bool PenEraser { get; set; } = true;
    /// <summary>The play / step / scrub bar under the canvas.</summary>
    public bool ShowTransport { get; set; } = true;

    /// <summary>Most recent first.</summary>
    public List<string> RecentFiles { get; set; } = new();

    public void AddRecent(string path)
    {
        var full = Path.GetFullPath(path);
        RecentFiles.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, full);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
    }

    /// <summary>Tab: every panel hidden, only the canvas. The previous set comes back on exit.</summary>
    public bool FocusMode { get; private set; }
    private Dictionary<string, bool>? _beforeFocus;

    public bool Fullscreen { get; set; }
    public bool QuitRequested { get; set; }

    /// <summary>Set by commands; the window applies it between frames and clears it.</summary>
    public string? PendingWorkspace { get; set; }
    public string CurrentWorkspace { get; set; } = Workspaces.Draw;

    public float UiScale { get; set; } = 1f;
    /// <summary>The theme in use, by name (built in, or a file in the themes folder).</summary>
    public string ThemeName { get; set; } = Themes.Default;
    /// <summary>Set when <see cref="UiScale"/> changed and fonts must be rebuilt.</summary>
    public bool UiScaleDirty { get; set; }

    public bool IsOpen(string panel) => Panels.TryGetValue(panel, out var open) && open;

    public void TogglePanel(string panel)
    {
        if (FocusMode) ExitFocus();
        Panels[panel] = !IsOpen(panel);
    }

    public void ToggleFocus()
    {
        if (FocusMode) { ExitFocus(); return; }
        _beforeFocus = new Dictionary<string, bool>(Panels);
        foreach (var p in PanelNames) Panels[p] = false;
        FocusMode = true;
    }

    private void ExitFocus()
    {
        if (_beforeFocus != null) foreach (var (k, v) in _beforeFocus) Panels[k] = v;
        _beforeFocus = null;
        FocusMode = false;
    }
}

public static class Workspaces
{
    public const string Draw = "Draw";
    public const string Animate = "Animate";
    public const string Minimal = "Minimal";

    /// <summary>The layouts defined in code; user-saved ones live in the settings folder.</summary>
    public static readonly string[] BuiltIn = { Draw, Animate, Minimal };
}
