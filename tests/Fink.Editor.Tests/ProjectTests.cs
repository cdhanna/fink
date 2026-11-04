using System.Numerics;
using Fink.Core;
using Fink.Editor;

namespace Fink.Editor.Tests;

public class ProjectTests
{
    private static string NewDir() => Path.Combine(Path.GetTempPath(), "fink-tests", Guid.NewGuid().ToString("N"));

    private static void Dot(EditorState ed, int x, int y)
    {
        ed.SelectTool(ed.FindTool("Pencil"));
        ed.SelectLayer("Line");
        ed.PointerDown(new Vector2(x + 0.5f, y + 0.5f), PointerButton.Primary, Mods.None);
        ed.PointerUp();
    }

    [Fact]
    public void Unsaved_work_is_recovered_by_the_next_run()
    {
        var dir = NewDir();
        // run 1: edits, autosaves, then "crashes" (never clears its recovery file)
        var crashed = new Session(new SettingsPaths(dir));
        crashed.Load();
        crashed.Editor.AddAsset("slime", 16, 16);
        Dot(crashed.Editor, 3, 4);
        Assert.True(crashed.Editor.IsDirty);
        crashed.Recovery.IntervalSeconds = 0;
        crashed.Recovery.Tick(crashed.Editor, now: 100);
        Assert.True(File.Exists(crashed.Recovery.File));

        // run 2 finds it and gets the work back, as unsaved
        var next = new Session(new SettingsPaths(dir));
        next.Load();
        var leftover = Assert.Single(next.Leftovers);
        Assert.Equal(2, leftover.Assets);
        next.Recovery.Recover(next.Editor, leftover);
        var ed = next.Editor;
        Assert.Equal(new[] { "sprite", "slime" }, ed.Project.Assets.Select(a => a.Name));
        var slime = ed.FindAsset("slime");
        Assert.Equal(1, slime.DrawingAt(slime.Layers[1], slime.Frames[0])!.Cel.Get(3, 4));
        Assert.True(ed.IsDirty);
        // the old file was taken over: only this run's recovery file is left
        Assert.False(File.Exists(leftover.File));
        Assert.True(File.Exists(next.Recovery.File));

        // saving makes it safe, so the recovery file goes away
        var path = Path.Combine(dir, "saved.fink");
        ed.Save(path);
        Assert.False(ed.IsDirty);
        next.Recovery.Tick(ed, now: 200);
        Assert.False(File.Exists(next.Recovery.File));
        Assert.Empty(new Session(new SettingsPaths(dir)).Recovery.FindLeftovers());
    }

    [Fact]
    public void Autosave_waits_for_the_interval_and_for_changes()
    {
        var s = new Session(new SettingsPaths(NewDir()));
        s.Recovery.IntervalSeconds = 20;
        s.Recovery.Tick(s.Editor, now: 0);
        Assert.False(File.Exists(s.Recovery.File)); // nothing unsaved

        Dot(s.Editor, 1, 1);
        s.Recovery.Tick(s.Editor, now: 1);
        Assert.True(File.Exists(s.Recovery.File)); // first change: written at once
        var first = File.GetLastWriteTimeUtc(s.Recovery.File);

        Dot(s.Editor, 2, 2);
        s.Recovery.Tick(s.Editor, now: 5);
        Assert.Equal(first, File.GetLastWriteTimeUtc(s.Recovery.File)); // too soon
        Thread.Sleep(20);
        s.Recovery.Tick(s.Editor, now: 30);
        Assert.NotEqual(first, File.GetLastWriteTimeUtc(s.Recovery.File));
    }

    [Fact]
    public void Undo_covers_the_whole_project_and_shows_what_it_changed()
    {
        var ed = new Session(new SettingsPaths(NewDir())).Editor;
        Dot(ed, 1, 1);
        ed.AddFrame(NewFrameKind.Blank);
        var first = ed.Document;

        ed.AddAsset("coin", 8, 8);
        Assert.Equal(0, ed.FrameIndex);          // each asset keeps its place
        Dot(ed, 2, 2);
        ed.SwitchAsset(first);
        Assert.Equal(1, ed.FrameIndex);

        // undo takes back the coin's stroke, and shows the coin
        ed.Undo();
        Assert.Same(ed.FindAsset("coin"), ed.Document);
        Assert.Equal(0, ed.Document.DrawingAt(ed.Document.Layers[1], ed.Document.Frames[0])?.Cel.Get(2, 2) ?? 0);
        // then the coin itself
        ed.Undo();
        Assert.Single(ed.Project.Assets);
        Assert.Same(first, ed.Document);
        ed.Redo();
        ed.Redo();
        Assert.Equal(1, ed.FindAsset("coin").DrawingAt(ed.FindAsset("coin").Layers[1], ed.FindAsset("coin").Frames[0])!.Cel.Get(2, 2));
    }

    [Fact]
    public void Changing_an_old_state_branches_and_the_old_way_stays()
    {
        var ed = new Session(new SettingsPaths(NewDir())).Editor;
        Dot(ed, 1, 1);
        Dot(ed, 2, 2);
        var tip = ed.History.Current;
        ed.Undo();
        Dot(ed, 5, 5);                           // a new branch
        var cel = ed.Document.GetCel(ed.Document.Layers[1], ed.Document.Frames[0]);
        Assert.Equal(0, cel.Get(2, 2));
        Assert.Equal(1, cel.Get(5, 5));
        Assert.Equal(2, ed.History.Nodes.Count(n => n.Children.Count == 0));

        ed.JumpTo(tip);                          // back to the old way
        Assert.Equal(1, cel.Get(2, 2));
        Assert.Equal(0, cel.Get(5, 5));
        ed.Undo();
        ed.Redo();                               // redo follows the branch you came from
        Assert.Same(tip, ed.History.Current);
        Assert.True(ed.IsDirty);
    }

    [Fact]
    public void Project_changes_undo_too()
    {
        var ed = new Session(new SettingsPaths(NewDir())).Editor;
        var pal = ed.Project.Palette;
        var before = pal.Entries[3];

        ed.BeginProjectChange();                 // a color dragged in the picker: one step
        pal.Set(3, new Rgba(1, 2, 3));
        pal.Set(3, new Rgba(4, 5, 6));
        ed.EndProjectChange("Edit color");
        ed.ProjectChange("Add variant", () => pal.AddVariant("night"));
        var glow = ed.AddChannel("glow", ChannelKind.Value);
        ed.RenameAsset(ed.Document, "hero");
        Assert.Equal(4, ed.History.Position);

        ed.Undo();
        Assert.Equal("sprite", ed.Document.Name);
        ed.Undo();
        Assert.Null(ed.Project.FindChannel("glow"));
        Assert.Equal(0, ed.ActiveChannelId == glow.Id ? 1 : 0);
        ed.Undo();
        Assert.Empty(pal.Variants);
        ed.Undo();
        Assert.Equal(before, pal.Entries[3]);
        Assert.False(ed.IsDirty);                // back where it started
        ed.History.JumpTo(4);
        Assert.Equal(new Rgba(4, 5, 6), pal.Entries[3]);
        Assert.Equal("hero", ed.Document.Name);

        // nothing changed: no step
        ed.ProjectChange("Nothing", () => { });
        Assert.Equal(4, ed.History.Position);
    }
}
