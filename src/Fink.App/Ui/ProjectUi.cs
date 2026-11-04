using Fink.Core;
using Fink.Editor;
using ImGuiNET;
using Num = System.Numerics;

namespace Fink.App;

/// <summary>Projects and assets: open/save, unsaved-changes prompts, recovery, the asset tabs.</summary>
public sealed partial class FinkGame
{
    private bool _exitConfirmed;
    private string _assetName = "";
    private int _assetW = 64, _assetH = 64;
    private string _lastTitle = "";
    private string? _fileError;

    /// <summary>Every frame, outside ImGui: file dialogs, autosave, the window title.</summary>
    private void UpdateProject()
    {
        if (Ui.RequestOpen)
        {
            Ui.RequestOpen = false;
            var path = FileDialogs.Open(InitialDir());
            if (path != null) OpenFile(path);
        }
        if (Ui.RequestSaveAs)
        {
            Ui.RequestSaveAs = false;
            var after = Ui.AfterSaveAs;
            Ui.AfterSaveAs = null;
            var path = FileDialogs.Save(InitialDir(), Ed.DisplayName + ProjectFile.Extension);
            if (path != null && SaveFile(path)) after?.Invoke();
        }
        if (Ui.QuitRequested)
        {
            Ui.QuitRequested = false;
            _exitConfirmed = true;
            Exit();
        }

        if (_script == null) _session.Recovery.Tick(Ed, Ed.Time);

        var title = $"Fink - {Ed.DisplayName}{(Ed.IsDirty ? " *" : "")}  [{Ed.Document.Name}]";
        if (title != _lastTitle) Window.Title = _lastTitle = title;
    }

    private string? InitialDir() =>
        Ed.FilePath != null ? Path.GetDirectoryName(Ed.FilePath)
        : Ui.RecentFiles.Count > 0 ? Path.GetDirectoryName(Ui.RecentFiles[0])
        : null;

    private void OpenFile(string path)
    {
        try
        {
            Ed.Open(path);
            Ui.AddRecent(path);
            _session.Recovery.Clear();
            _fileError = null;
        }
        catch (Exception e)
        {
            _fileError = $"Couldn't open {Path.GetFileName(path)}: {e.Message}";
        }
    }

    private bool SaveFile(string path)
    {
        try
        {
            Ed.Save(path);
            Ui.AddRecent(Ed.FilePath!);
            _fileError = null;
            return true;
        }
        catch (Exception e)
        {
            _fileError = $"Couldn't save {Path.GetFileName(path)}: {e.Message}";
            return false;
        }
    }

    /// <summary>Closing the window with unsaved work asks first.</summary>
    private bool InterceptExit()
    {
        // scripted runs (finished ones too: --keep-open) never ask
        if (_exitConfirmed || _options.ScriptPath != null || !Ed.IsDirty) return false;
        Ui.PendingUnsaved = () => { _exitConfirmed = true; Exit(); };
        return true;
    }

    // ---------------------------------------------------------------- dialogs

    private void DrawProjectDialogs()
    {
        DrawUnsavedPrompt();
        DrawNewAssetDialog();
        DrawRenameAssetDialog();
        DrawDeleteAssetDialog();
        DrawRecoveryDialog();
        if (_fileError != null)
        {
            ImGui.OpenPopup("File error");
            if (ImGui.BeginPopupModal("File error", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextWrapped(_fileError);
                if (ImGui.Button("OK") || ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    _fileError = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }
    }

    private void DrawUnsavedPrompt()
    {
        if (Ui.PendingUnsaved != null && !ImGui.IsPopupOpen("Unsaved changes")) ImGui.OpenPopup("Unsaved changes");
        if (!ImGui.BeginPopupModal("Unsaved changes", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.Text($"Save changes to \"{Ed.DisplayName}\" first?");
        ImGui.TextDisabled(Ed.FilePath ?? "It hasn't been saved yet.");
        ImGui.Spacing();
        var action = Ui.PendingUnsaved;
        if (ImGui.Button("Save") || ImGui.IsKeyPressed(ImGuiKey.Enter))
        {
            Ui.PendingUnsaved = null;
            ImGui.CloseCurrentPopup();
            if (Ed.FilePath == null) { Ui.AfterSaveAs = action; Ui.RequestSaveAs = true; }
            else if (SaveFile(Ed.FilePath)) action?.Invoke();
        }
        ImGui.SameLine();
        if (ImGui.Button("Don't Save"))
        {
            Ui.PendingUnsaved = null;
            ImGui.CloseCurrentPopup();
            _session.Recovery.Clear();
            Ed.MarkSaved(); // discarded on purpose
            action?.Invoke();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            Ui.PendingUnsaved = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawNewAssetDialog()
    {
        if (Ui.ShowNewAsset)
        {
            Ui.ShowNewAsset = false;
            _assetName = Ed.Project.UniqueName("sprite");
            _assetW = Ed.Document.Width;
            _assetH = Ed.Document.Height;
            ImGui.OpenPopup("New Asset");
        }
        bool open = true;
        if (!ImGui.BeginPopupModal("New Asset", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextDisabled("Another animation in this project: same palette, same atlas.");
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(220 * Ui.UiScale);
        ImGui.InputText("Name", ref _assetName, 64);
        foreach (var s in new[] { 16, 32, 48, 64, 96, 128 })
        {
            if (ImGui.Button($"{s}##sz")) _assetW = _assetH = s;
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.SetNextItemWidth(140 * Ui.UiScale);
        ImGui.InputInt("Width", ref _assetW);
        ImGui.SetNextItemWidth(140 * Ui.UiScale);
        ImGui.InputInt("Height", ref _assetH);
        _assetW = Math.Clamp(_assetW, 1, 4096);
        _assetH = Math.Clamp(_assetH, 1, 4096);
        if ((ImGui.Button("Create") || ImGui.IsKeyPressed(ImGuiKey.Enter)) && _assetName.Trim().Length > 0)
        {
            Ed.AddAsset(_assetName.Trim(), _assetW, _assetH);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawRenameAssetDialog()
    {
        if (Ui.ShowRenameAsset)
        {
            Ui.ShowRenameAsset = false;
            _assetName = Ed.Document.Name;
            ImGui.OpenPopup("Rename Asset");
        }
        bool open = true;
        if (!ImGui.BeginPopupModal("Rename Asset", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(220 * Ui.UiScale);
        ImGui.InputText("Name", ref _assetName, 64);
        ImGui.TextDisabled("It's also the asset's name in the exported atlas.");
        if (ImGui.Button("Rename") || ImGui.IsKeyPressed(ImGuiKey.Enter))
        {
            Ed.RenameAsset(Ed.Document, _assetName);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawDeleteAssetDialog()
    {
        if (Ui.ShowDeleteAsset)
        {
            Ui.ShowDeleteAsset = false;
            if (Ed.Project.Assets.Count > 1) ImGui.OpenPopup("Delete Asset");
        }
        bool open = true;
        if (!ImGui.BeginPopupModal("Delete Asset", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.Text($"Delete \"{Ed.Document.Name}\" from the project?");
        ImGui.TextDisabled("This can't be undone (but nothing is lost until you save).");
        if (ImGui.Button("Delete"))
        {
            Ed.RemoveAsset(Ed.Document);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawRecoveryDialog()
    {
        if (Ui.ShowRecovery)
        {
            Ui.ShowRecovery = false;
            if (_session.Leftovers.Count > 0) ImGui.OpenPopup("Recover unsaved work");
        }
        if (!ImGui.BeginPopupModal("Recover unsaved work", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.Text("Fink didn't close normally last time. This unsaved work was autosaved:");
        ImGui.Spacing();
        foreach (var info in _session.Leftovers.ToList())
        {
            ImGui.PushID(info.File);
            ImGui.Text($"{info.ProjectName}  ({info.Assets} asset{(info.Assets == 1 ? "" : "s")}, {info.SavedAtUtc.ToLocalTime():g})");
            if (info.OriginalPath != null) ImGui.TextDisabled($"    last saved as {info.OriginalPath}");
            if (ImGui.Button("Recover"))
            {
                try { _session.Recovery.Recover(Ed, info); }
                catch (Exception e) { _fileError = $"Couldn't recover it: {e.Message}"; }
                _session.Leftovers.Remove(info);
                _session.Leftovers.Clear(); // only one can be open; the rest stay on disk for next time
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                _session.Recovery.Discard(info);
                _session.Leftovers.Remove(info);
            }
            ImGui.PopID();
            ImGui.Separator();
        }
        if (ImGui.Button("Later") || _session.Leftovers.Count == 0) ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        ImGui.TextDisabled("Later keeps them; you'll be asked again next time.");
        ImGui.EndPopup();
    }
}
