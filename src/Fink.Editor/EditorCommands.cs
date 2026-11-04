using System.Numerics;
using Fink.Core;
using ImGuiNET;

namespace Fink.Editor;

/// <summary>Registers every editor command with its default bindings.</summary>
public static class EditorCommands
{
    public static void RegisterAll(CommandRegistry r, EditorState ed, UiState ui)
    {
        Binding B(string chord, Scope scope = Scope.Global) => new(KeyChord.Parse(chord), scope);

        void Add(string id, string title, string category, Action run, string description = "",
            Binding[]? keys = null, Func<bool>? enabled = null, Func<bool>? isChecked = null,
            bool repeat = false, Action? release = null)
        {
            r.Register(new Command
            {
                Id = id, Title = title, Category = category, Description = description, Execute = run,
                DefaultBindings = keys ?? Array.Empty<Binding>(), IsEnabled = enabled, IsChecked = isChecked,
                Repeat = repeat, OnRelease = release,
            });
        }

        // ---------------------------------------------------------------- app
        const string App = "App";
        Add("app.commandPalette", "Command Palette...", App, () => ui.ShowCommandPalette = true,
            "Search and run any command.", new[] { B("Ctrl+Shift+P"), B("F1") });
        Add("app.options", "Options...", App, () => ui.ShowOptions = !ui.ShowOptions,
            "Hotkeys and interface settings.", new[] { B("Ctrl+Comma") });
        Add("app.quit", "Quit", App, () => ui.Guard(ed, () => ui.QuitRequested = true), keys: new[] { B("Ctrl+Q") });

        // ---------------------------------------------------------------- assets
        const string Assets = "Assets";
        Add("asset.new", "New Asset...", Assets, () => ui.ShowNewAsset = true,
            "Add another animation to the project. It shares the palette and the atlas.", new[] { B("Ctrl+Shift+N") });
        Add("asset.goto", "Go to Asset...", Assets, () => ui.ShowAssetPicker = true,
            "Type part of a name to find an asset.", new[] { B("Ctrl+P") });
        Add("asset.duplicate", "Duplicate Asset", Assets, () => ed.DuplicateAsset(ed.Document));
        Add("asset.rename", "Rename Asset...", Assets, () => ui.ShowRenameAsset = true);
        Add("asset.delete", "Delete Asset...", Assets, () => ui.ShowDeleteAsset = true, enabled: () => ed.Project.Assets.Count > 1);
        Add("asset.next", "Next Asset", Assets, () => ed.StepAsset(+1), keys: new[] { B("Ctrl+PageDown") }, enabled: () => ed.Project.Assets.Count > 1);
        Add("asset.previous", "Previous Asset", Assets, () => ed.StepAsset(-1), keys: new[] { B("Ctrl+PageUp") }, enabled: () => ed.Project.Assets.Count > 1);

        // ---------------------------------------------------------------- file
        const string File = "File";
        Add("file.new", "New Project...", File, () => ui.Guard(ed, () => ui.ShowNewDialog = true), "Start a new project with one asset.", new[] { B("Ctrl+N") });
        Add("file.open", "Open...", File, () => ui.Guard(ed, () => ui.RequestOpen = true), "Open a .fink project.", new[] { B("Ctrl+O") });
        Add("file.save", "Save", File, () => { if (ed.FilePath == null) ui.RequestSaveAs = true; else { ed.Save(ed.FilePath); ui.AddRecent(ed.FilePath); } },
            "Save the project (every asset and the palette).", new[] { B("Ctrl+S") });
        Add("file.saveAs", "Save As...", File, () => ui.RequestSaveAs = true, keys: new[] { B("Ctrl+Shift+S") });
        Add("file.export", "Export Atlases", File, () =>
            {
                if (ed.Project.Atlases.Any(a => string.IsNullOrWhiteSpace(a.OutputFolder)))
                {
                    ui.Panels["Atlas"] = true;
                    ed.SetStatus("Set the atlas's output folder in the Atlas panel first.", 4);
                    return;
                }
                ed.ExportAtlases();
            },
            "Write every atlas: <atlas>.png (palette indices), <atlas>.palette.png, <atlas>.atlas.json and <atlas>_atlas.fbasic.",
            new[] { B("Ctrl+E") });
        Add("atlas.repack", "Repack Atlas Tightly", File, () => ed.BuildAtlas(ed.CurrentAtlas, tight: true),
            "Pack the current atlas from scratch into the smallest size. Normally pictures keep their places as you work.");

        // ---------------------------------------------------------------- edit
        const string Edit = "Edit";
        Add("edit.undo", "Undo", Edit, ed.Undo, "Undo the last change. While a selection floats, puts it back instead.",
            new[] { B("Ctrl+Z") }, enabled: () => (ed.History.CanUndo || ed.Selection.Floating != null) && !ed.Stroking, repeat: true);
        Add("edit.redo", "Redo", Edit, ed.Redo, keys: new[] { B("Ctrl+Y"), B("Ctrl+Shift+Z") },
            enabled: () => ed.History.CanRedo && !ed.Stroking, repeat: true);

        // ---------------------------------------------------------------- tools
        const string Tools = "Tools";
        foreach (var (name, key, desc) in new[]
                 {
                     ("Pencil", "B", "Freehand hard lines on Line layers."),
                     ("Eraser", "E", "Erase to transparent on Line or Fill layers."),
                     ("Line", "L", "Straight lines on Line layers. Shift snaps the angle."),
                     ("Fill", "G", "Bucket fill on Fill layers, bounded by the visible line art."),
                     ("Eyedropper", "I", "Pick a palette color from the canvas."),
                 })
        {
            var tool = ed.FindTool(name);
            Add($"tool.{name.ToLowerInvariant()}", name, Tools, () => ed.SelectTool(tool), desc,
                new[] { B(key) }, isChecked: () => ed.Tool == tool);
        }
        var pointTool = ed.FindTool("Point");
        Add("tool.point", "Point", Tools, () => ed.SelectTool(pointTool),
            "Place the pivot or a named point on the current drawing. Right-click clears it there.", new[] { B("P") },
            isChecked: () => ed.Tool == pointTool);
        Add("channel.cycle", "Paint Colors / Data", Tools, ed.CycleChannel,
            "Switch between painting colors and each data channel (normal, emissive...).", new[] { B("C") });
        Add("tool.eraser.hold", "Eraser (hold)", Tools, () => ed.BeginMomentary(Momentary.Eraser),
            "Erase while held; the previous tool returns on release. The pen's eraser end does this by itself; " +
            "bind a key here if your tablet driver sends a key for the eraser instead.",
            release: () => ed.EndMomentary(Momentary.Eraser));
        Add("tool.eyedropper.hold", "Eyedropper (hold)", Tools, () => ed.BeginMomentary(Momentary.Eyedropper),
            "Eyedropper while held; the previous tool returns on release. Off for selection tools, where Alt subtracts.",
            new[] { B("Alt", Scope.Canvas) }, enabled: () => ed.Tool is not ISelectionTool,
            release: () => ed.EndMomentary(Momentary.Eyedropper));
        Add("brush.size.decrease", "Brush Size Down", Tools, () => ed.ToolSettings.Size = Math.Max(1, ed.ToolSettings.Size - 1),
            keys: new[] { B("LeftBracket") }, repeat: true);
        Add("brush.size.increase", "Brush Size Up", Tools, () => ed.ToolSettings.Size = Math.Min(64, ed.ToolSettings.Size + 1),
            keys: new[] { B("RightBracket") }, repeat: true);
        Add("brush.pixelPerfect", "Pixel Perfect", Tools, () => ed.ToolSettings.PixelPerfect = !ed.ToolSettings.PixelPerfect,
            "Remove L-shaped corner pixels from 1px strokes.", isChecked: () => ed.ToolSettings.PixelPerfect);
        Add("brush.squareTip", "Square Tip", Tools, () => ed.ToolSettings.SquareTip = !ed.ToolSettings.SquareTip,
            isChecked: () => ed.ToolSettings.SquareTip);
        Add("fill.contiguous", "Contiguous Fill", Tools, () => ed.ToolSettings.FillContiguous = !ed.ToolSettings.FillContiguous,
            "Off: fill every matching pixel on the layer.", isChecked: () => ed.ToolSettings.FillContiguous);
        Add("layer.autoSelect", "Auto-select Layer", Tools, () => ed.AutoSelectLayer = !ed.AutoSelectLayer,
            "When a tool can't edit the current layer, switch to the nearest one it can instead of refusing.",
            isChecked: () => ed.AutoSelectLayer);

        // ---------------------------------------------------------------- selection
        const string Select = "Select";
        var sel = ed.Selection;
        bool Floating() => sel.Floating != null;
        foreach (var (name, key, desc) in new[]
                 {
                     ("Select", "M", "Rectangle selection. Shift adds, Alt subtracts, both intersect. Drag inside a selection to move it."),
                     ("Lasso", "Q", "Freehand selection. Shift adds, Alt subtracts, both intersect."),
                     ("Wand", "W", "Select pixels of the same color on the current layer."),
                     ("Transform", "T", "Move, scale and rotate the selection (or the whole layer) with handles."),
                 })
        {
            var tool = ed.FindTool(name);
            Add($"tool.{name.ToLowerInvariant()}", name, Select, () => ed.SelectTool(tool), desc,
                new[] { B(key) }, isChecked: () => ed.Tool == tool);
        }
        Add("selection.all", "Select All", Select, () => { sel.Commit(); sel.Selection.SelectAll(); }, keys: new[] { B("Ctrl+A") });
        Add("selection.deselect", "Deselect", Select, () => { sel.Commit(); sel.Selection.Clear(); }, keys: new[] { B("Ctrl+D") },
            enabled: () => sel.HasSelection);
        Add("selection.invert", "Invert Selection", Select, () => { sel.Commit(); sel.Selection.Invert(); }, keys: new[] { B("Ctrl+Shift+I") });
        Add("selection.layerContent", "Select Layer Content", Select, () => { sel.Commit(); sel.SelectLayerContent(); },
            "Select every drawn pixel on the current layer (all layers when All Layers is on).", new[] { B("Ctrl+Shift+A") });
        Add("selection.commit", "Apply Transform", Select, sel.Commit, "Stamp the floating pixels down.",
            new[] { B("Enter", Scope.Canvas), B("KeypadEnter", Scope.Canvas) }, enabled: Floating);
        Add("selection.cancel", "Cancel Transform", Select, sel.Cancel, "Put the floating pixels back where they were.",
            new[] { B("Escape") }, enabled: Floating);
        Add("selection.flipH", "Flip Horizontal", Select, () => sel.Flip(true), "Flips the selection, or the whole layer.", new[] { B("Shift+H") });
        Add("selection.flipV", "Flip Vertical", Select, () => sel.Flip(false), "Flips the selection, or the whole layer.", new[] { B("Shift+V") });
        Add("selection.rotateCW", "Rotate 90 Clockwise", Select, () => sel.Rotate(90), "Turns the selection, or the whole layer.", new[] { B("R") });
        Add("selection.rotateCCW", "Rotate 90 Counter-clockwise", Select, () => sel.Rotate(-90), "Turns the selection, or the whole layer.", new[] { B("Shift+R") });
        foreach (var (dir, key, dx, dy) in new[] { ("Left", "LeftArrow", -1, 0), ("Right", "RightArrow", 1, 0), ("Up", "UpArrow", 0, -1), ("Down", "DownArrow", 0, 1) })
        {
            int ddx = dx, ddy = dy;
            Add($"selection.nudge{dir}", $"Nudge {dir}", Select, () => sel.Nudge(ddx, ddy), "Move the selection 1 pixel.",
                new[] { B(key, Scope.Canvas) }, enabled: () => sel.HasSelection, repeat: true);
            Add($"selection.nudge{dir}8", $"Nudge {dir} 8px", Select, () => sel.Nudge(ddx * 8, ddy * 8), "Move the selection 8 pixels.",
                new[] { B($"Shift+{key}", Scope.Canvas) }, enabled: () => sel.HasSelection, repeat: true);
        }
        Add("selection.allLayers", "Selection Affects All Layers", Select, () => sel.AllLayers = !sel.AllLayers,
            "Move, transform, copy and clear every visible, unlocked line and fill layer together (e.g. line art with its fill).",
            isChecked: () => sel.AllLayers);
        Add("selection.withLinked", "Selection Includes Linked Fill", Select, () => sel.WithLinked = !sel.WithLinked,
            "Selecting on a line layer takes the fill layer that follows it along (and the other way round), so they move together.",
            isChecked: () => sel.WithLinked);
        Add("selection.wandContiguous", "Wand: Contiguous", Select, () => sel.WandContiguous = !sel.WandContiguous,
            "Off: the wand selects that color everywhere on the layer.", isChecked: () => sel.WandContiguous);

        Add("edit.copy", "Copy", Edit, sel.Copy, "Copy the selection (or the whole layer).", new[] { B("Ctrl+C") });
        Add("edit.cut", "Cut", Edit, sel.Cut, keys: new[] { B("Ctrl+X") }, enabled: () => sel.HasSelection);
        Add("edit.paste", "Paste", Edit, () => { sel.Paste(); if (ed.Tool is not ISelectionTool) ed.SelectTool(ed.FindTool("Select")); },
            "Paste in place, floating, so it lines up with where it was copied from (good for the next frame).",
            new[] { B("Ctrl+V") }, enabled: () => sel.Clipboard != null);
        Add("edit.clear", "Clear Selection Pixels", Edit, () => sel.Clear(), "Erase the selected pixels.",
            new[] { B("Delete"), B("Backspace") }, enabled: () => sel.HasSelection);

        // ---------------------------------------------------------------- color
        const string Color = "Color";
        Add("color.swap", "Swap Primary/Secondary", Color, () => (ed.Primary, ed.Secondary) = (ed.Secondary, ed.Primary),
            keys: new[] { B("X") });
        Add("color.next", "Next Palette Color", Color,
            () => ed.Primary = (byte)Math.Min(ed.Document.Palette.Count - 1, ed.Primary + 1), repeat: true);
        Add("color.previous", "Previous Palette Color", Color,
            () => ed.Primary = (byte)Math.Max(1, ed.Primary - 1), repeat: true);

        // ---------------------------------------------------------------- timeline
        const string Frames = "Timeline";
        Add("frame.next", "Next Frame", Frames, () => ed.StepFrame(+1),
            keys: new[] { B("Period"), B("RightArrow", Scope.Timeline) }, repeat: true);
        Add("frame.previous", "Previous Frame", Frames, () => ed.StepFrame(-1),
            keys: new[] { B("Comma"), B("LeftArrow", Scope.Timeline) }, repeat: true);
        Add("frame.first", "First Frame", Frames, () => { ed.StepFrame(0); ed.FrameIndex = 0; }, keys: new[] { B("Home") });
        Add("frame.last", "Last Frame", Frames, () => { ed.StepFrame(0); ed.FrameIndex = ed.Document.Frames.Count - 1; }, keys: new[] { B("End") });
        Add("frame.new", "New Frame", Frames, () => ed.AddFrame(NewFrameKind.Blank),
            "Insert a frame after this one with a new blank drawing on the current layer. Other layers hold.", new[] { B("Alt+N") });
        Add("frame.duplicate", "Duplicate Frame", Frames, () => ed.AddFrame(NewFrameKind.Duplicate),
            "Insert a frame after this one with a copy of the current layer's drawing (and its fill). Other layers hold.", new[] { B("Alt+D") });
        Add("frame.hold", "Insert Held Frame", Frames, () => ed.AddFrame(NewFrameKind.Hold),
            "Insert a frame after this one where every layer holds what it shows now.", new[] { B("Alt+H") });
        Add("drawing.new", "New Drawing", Frames, () => ed.NewDrawing(false),
            "Replace the current layer's drawing with a new blank one, from this frame to the end of its hold.", new[] { B("N") });
        Add("drawing.duplicate", "Duplicate Drawing", Frames, () => ed.NewDrawing(true),
            "Like New Drawing, but starting from a copy of the current one.", new[] { B("D") });
        Add("hold.longer", "Hold Longer", Frames, () => ed.Retime(+1),
            "Show the current drawing one frame longer; the rest of this layer slides later.", new[] { B("Shift+Period") }, repeat: true);
        Add("hold.shorter", "Hold Shorter", Frames, () => ed.Retime(-1),
            "Show the current drawing one frame shorter; the rest of this layer slides earlier.", new[] { B("Shift+Comma") }, repeat: true);
        Add("cell.clear", "Clear Cell", Frames, () => ed.SetCell(ed.FrameIndex, ed.CurrentLayer, null),
            "Show nothing on the current layer on this frame. The drawing itself is kept.");
        Add("frame.delete", "Delete Frame", Frames, () => { ed.Settle(); DocumentOps.DeleteFrame(ed.Document, ed.History, ed.FrameIndex); ed.Clamp(); },
            keys: new[] { B("Alt+Delete"), B("Delete", Scope.Timeline) }, enabled: () => ed.Document.Frames.Count > 1);
        Add("frame.moveLeft", "Move Frame Left", Frames, () => ed.MoveFrame(-1), keys: new[] { B("Alt+LeftArrow") }, enabled: () => ed.FrameIndex > 0);
        Add("frame.moveRight", "Move Frame Right", Frames, () => ed.MoveFrame(+1), keys: new[] { B("Alt+RightArrow") },
            enabled: () => ed.FrameIndex < ed.Document.Frames.Count - 1);
        Add("playback.toggle", "Play / Stop", Frames, ed.TogglePlay, keys: new[] { B("Enter") }, isChecked: () => ed.Playing);
        Add("playback.tag", "Play Tag Only", Frames, () => ed.PlayTag = !ed.PlayTag,
            "When the playhead is inside a tag, Play loops just that tag, in its loop mode.", isChecked: () => ed.PlayTag);
        Add("onion.toggle", "Onion Skin", Frames, () => ed.Onion.Enabled = !ed.Onion.Enabled,
            keys: new[] { B("O") }, isChecked: () => ed.Onion.Enabled);

        // ---------------------------------------------------------------- layers
        const string Layers = "Layers";
        Add("layer.addLine", "New Line Layer", Layers, () => ed.AddLayer(LayerRole.Line), "Above the current layer.", new[] { B("Ctrl+Shift+L") });
        Add("layer.addFill", "New Fill Layer", Layers, () => ed.AddLayer(LayerRole.Fill), "Below the current layer.", new[] { B("Ctrl+Shift+F") });
        Add("layer.addReference", "New Reference Layer", Layers, () => ed.AddLayer(LayerRole.Reference), "At the bottom; never exported.");
        Add("layer.delete", "Delete Layer", Layers, () => { ed.Settle(); DocumentOps.DeleteLayer(ed.Document, ed.History, ed.LayerIndex); ed.Clamp(); },
            enabled: () => ed.Document.Layers.Count > 1);
        Add("layer.selectAbove", "Select Layer Above", Layers, () => ed.LayerIndex++, keys: new[] { B("Ctrl+UpArrow") }, repeat: true);
        Add("layer.selectBelow", "Select Layer Below", Layers, () => ed.LayerIndex--, keys: new[] { B("Ctrl+DownArrow") }, repeat: true);
        Add("layer.moveUp", "Move Layer Up", Layers, () => ed.MoveLayer(+1), keys: new[] { B("Ctrl+Shift+UpArrow") });
        Add("layer.moveDown", "Move Layer Down", Layers, () => ed.MoveLayer(-1), keys: new[] { B("Ctrl+Shift+DownArrow") });
        Add("layer.toggleVisible", "Toggle Layer Visibility", Layers, () => ed.CurrentLayer.Visible = !ed.CurrentLayer.Visible,
            keys: new[] { B("V") });
        Add("layer.toggleLock", "Toggle Layer Lock", Layers, () => ed.CurrentLayer.Locked = !ed.CurrentLayer.Locked);

        // ---------------------------------------------------------------- view
        const string View = "View";
        Add("view.fit", "Fit Canvas", View, () => ed.View.FitRequested = true, keys: new[] { B("F") });
        Add("view.zoomIn", "Zoom In", View, () => ed.View.Step(+1, Vector2.Zero), keys: new[] { B("Equal"), B("KeypadAdd") }, repeat: true);
        Add("view.zoomOut", "Zoom Out", View, () => ed.View.Step(-1, Vector2.Zero), keys: new[] { B("Minus"), B("KeypadSubtract") }, repeat: true);
        Add("view.pan.hold", "Pan (hold)", View, () => ed.BeginMomentary(Momentary.Pan),
            "Drag to pan while held.", new[] { B("Space", Scope.Canvas), B("MouseMiddle", Scope.Canvas) },
            release: () => ed.EndMomentary(Momentary.Pan));
        Add("view.grid", "Pixel Grid", View, () => ed.View.ShowGrid = !ed.View.ShowGrid,
            "Shown at 800% zoom and above.", isChecked: () => ed.View.ShowGrid);
        Add("view.focus", "Focus Canvas", View, ui.ToggleFocus, "Hide every panel; press again to bring them back.",
            new[] { B("Tab") }, isChecked: () => ui.FocusMode);
        Add("view.fullscreen", "Fullscreen", View, () => ui.Fullscreen = !ui.Fullscreen, keys: new[] { B("F11") },
            isChecked: () => ui.Fullscreen);
        foreach (var panel in UiState.PanelNames)
        {
            var p = panel;
            Add($"panel.{p.ToLowerInvariant()}", $"{p} Panel", View, () => ui.TogglePanel(p), isChecked: () => ui.IsOpen(p));
        }

        // ---------------------------------------------------------------- workspaces
        const string Workspace = "Workspace";
        for (int i = 0; i < Workspaces.BuiltIn.Length; i++)
        {
            var name = Workspaces.BuiltIn[i];
            Add($"workspace.{name.ToLowerInvariant()}", $"{name} Workspace", Workspace, () => ui.PendingWorkspace = name,
                keys: new[] { B($"Ctrl+{i + 1}") }, isChecked: () => ui.CurrentWorkspace == name);
        }
        Add("workspace.reset", "Reset Workspace", Workspace, () => ui.PendingWorkspace = ui.CurrentWorkspace,
            "Put the current workspace's panels back where they started.");
        Add("workspace.save", "Save Workspace As...", Workspace, () => ui.ShowSaveWorkspace = true,
            "Save the current panel arrangement under a name.");
    }
}
