using System.Text.Json;
using Fink.Core;

namespace Fink.Editor;

/// <summary>What a recovery file says about the work in it.</summary>
public sealed class RecoveryInfo
{
    /// <summary>Where the project was saved, if it ever was.</summary>
    public string? OriginalPath { get; set; }
    public string ProjectName { get; set; } = "untitled";
    public DateTime SavedAtUtc { get; set; }
    public int Assets { get; set; }

    /// <summary>The .fink holding the work (not serialized; found next to the info file).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string File { get; set; } = "";
}

/// <summary>
/// Autosave for crash recovery. While there are unsaved changes, the whole project is written
/// every <see cref="IntervalSeconds"/> to its own file in the settings folder's recovery
/// directory, never over the real file. Saving or quitting normally deletes it, so any recovery
/// file found at startup is work from a run that didn't end cleanly.
/// </summary>
public sealed class Recovery
{
    public string Directory { get; }
    /// <summary>This run's recovery file.</summary>
    public string File { get; }
    private string InfoFile => Path.ChangeExtension(File, ".json");

    public double IntervalSeconds { get; set; } = 20;
    public DateTime? LastWriteUtc { get; private set; }

    private long _writtenStamp = long.MinValue;
    private double _lastWrite = double.NegativeInfinity;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Recovery(string directory)
    {
        Directory = directory;
        File = Path.Combine(directory, $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.fink");
    }

    /// <summary>Call every frame. Writes when due; deletes the file once nothing is unsaved.</summary>
    public void Tick(EditorState ed, double now)
    {
        if (!ed.IsDirty)
        {
            if (LastWriteUtc != null) Clear();
            return;
        }
        if (ed.Stroking || ed.Selection.Floating != null) return; // mid-gesture: wait
        if (ed.ChangeStamp == _writtenStamp || now - _lastWrite < IntervalSeconds) return;
        Write(ed, now);
    }

    /// <summary>Writes the project now (scripts and tests use this).</summary>
    public void Write(EditorState ed, double now = 0)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = File + ".writing";
        using (var f = System.IO.File.Create(temp)) ProjectFile.Write(ed.Project, f);
        System.IO.File.Move(temp, File, overwrite: true);
        var info = new RecoveryInfo
        {
            OriginalPath = ed.FilePath,
            ProjectName = ed.DisplayName,
            SavedAtUtc = DateTime.UtcNow,
            Assets = ed.Project.Assets.Count,
        };
        System.IO.File.WriteAllText(InfoFile, JsonSerializer.Serialize(info, Json));
        _writtenStamp = ed.ChangeStamp;
        _lastWrite = now;
        LastWriteUtc = info.SavedAtUtc;
    }

    /// <summary>Removes this run's recovery file (after a save, or on a clean exit).</summary>
    public void Clear()
    {
        TryDelete(File);
        TryDelete(InfoFile);
        LastWriteUtc = null;
        _writtenStamp = long.MinValue;
    }

    /// <summary>Recovery files left by other runs: work that was never saved before Fink stopped.</summary>
    public List<RecoveryInfo> FindLeftovers()
    {
        var result = new List<RecoveryInfo>();
        if (!System.IO.Directory.Exists(Directory)) return result;
        foreach (var fink in System.IO.Directory.GetFiles(Directory, "*.fink"))
        {
            if (string.Equals(Path.GetFullPath(fink), Path.GetFullPath(File), StringComparison.OrdinalIgnoreCase)) continue;
            RecoveryInfo info;
            try
            {
                var infoPath = Path.ChangeExtension(fink, ".json");
                info = System.IO.File.Exists(infoPath)
                    ? JsonSerializer.Deserialize<RecoveryInfo>(System.IO.File.ReadAllText(infoPath)) ?? new()
                    : new RecoveryInfo { SavedAtUtc = System.IO.File.GetLastWriteTimeUtc(fink) };
            }
            catch { info = new RecoveryInfo { SavedAtUtc = System.IO.File.GetLastWriteTimeUtc(fink) }; }
            info.File = fink;
            result.Add(info);
        }
        return result.OrderByDescending(r => r.SavedAtUtc).ToList();
    }

    /// <summary>
    /// Opens leftover work as the current project. It takes over this run's recovery file, so it
    /// stays protected until saved, and counts as unsaved.
    /// </summary>
    public void Recover(EditorState ed, RecoveryInfo info)
    {
        var project = ProjectFile.Load(info.File);
        ed.SetProject(project, info.OriginalPath);
        ed.MarkDirty();
        Discard(info);
        Write(ed);
        ed.SetStatus($"Recovered \"{info.ProjectName}\" from {info.SavedAtUtc.ToLocalTime():g}. Save it to keep it.", 6);
    }

    public void Discard(RecoveryInfo info)
    {
        TryDelete(info.File);
        TryDelete(Path.ChangeExtension(info.File, ".json"));
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch { /* in use or gone: leave it */ }
    }
}
