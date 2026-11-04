using Fink.Editor;
using ImGuiNET;

namespace Fink.Editor.Tests;

public class EditorTests
{
    private static Session NewSession() =>
        new(new SettingsPaths(Path.Combine(Path.GetTempPath(), "fink-tests", Guid.NewGuid().ToString("N"))));

    public static IEnumerable<object[]> Scripts() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "scripts"), "*.fink-test")
            .Select(p => new object[] { Path.GetFileName(p) });

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Script_passes_headless(string file)
    {
        var runner = new ScriptRunner(NewSession(), File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", file)), file);
        runner.RunHeadless();
        Assert.True(runner.Passed, runner.Report());
        Assert.True(runner.AssertCount > 0);
    }

    [Theory]
    [InlineData("Ctrl+Shift+Z")]
    [InlineData("Alt")]
    [InlineData("Comma")]
    [InlineData("1")]
    [InlineData("MouseX1")]
    [InlineData("Alt+Delete")]
    public void Chords_round_trip(string text)
    {
        var chord = KeyChord.Parse(text);
        Assert.Equal(chord, KeyChord.Parse(chord.Serialize()));
        Assert.Equal(chord, KeyChord.Parse(chord.ToString()));
    }

    [Fact]
    public void Modifier_alone_is_its_own_chord()
    {
        var c = KeyChord.Normalize(ImGuiKey.RightAlt, Mods.Alt);
        Assert.Equal(new KeyChord(ImGuiKey.LeftAlt, Mods.None), c);
        Assert.Equal("Alt", c.ToString());
    }

    [Fact]
    public void Default_bindings_have_no_conflicts()
    {
        var s = NewSession();
        var problems = new List<string>();
        foreach (var c in s.Commands.All)
        foreach (var b in s.Keymap.BindingsOf(c.Id))
        foreach (var other in s.Keymap.Conflicts(c.Id, b))
            problems.Add($"{b}: {c.Id} and {other.Id}");
        Assert.Empty(problems);
    }

    [Fact]
    public void Keymap_saves_only_changes_and_loads_them_back()
    {
        var s = NewSession();
        s.Keymap.SetBindings("edit.undo", new[] { Binding.Parse("Ctrl+U"), Binding.Parse("MouseX1@Canvas") });
        var json = s.Keymap.ToJson();
        Assert.Contains("edit.undo", json);
        Assert.DoesNotContain("edit.redo", json);

        var s2 = NewSession();
        Assert.Empty(s2.Keymap.LoadJson(json));
        Assert.Equal(new[] { "Ctrl+U", "Mouse 4 (Canvas)" }, s2.Keymap.BindingsOf("edit.undo").Select(b => b.ToString()));
    }

    [Fact]
    public void Setting_bindings_back_to_defaults_clears_the_override()
    {
        var s = NewSession();
        var defaults = s.Commands.Get("edit.undo").DefaultBindings;
        s.Keymap.SetBindings("edit.undo", new[] { Binding.Parse("U") });
        Assert.True(s.Keymap.IsCustomized("edit.undo"));
        s.Keymap.SetBindings("edit.undo", defaults);
        Assert.False(s.Keymap.IsCustomized("edit.undo"));
    }

    [Fact]
    public void Settings_round_trip_through_disk()
    {
        var paths = new SettingsPaths(Path.Combine(Path.GetTempPath(), "fink-tests", Guid.NewGuid().ToString("N")));
        var s = new Session(paths);
        s.Ui.UiScale = 1.5f;
        s.Ui.Panels["History"] = false;
        s.Editor.AutoSelectLayer = true;
        s.Keymap.AddBinding("tool.fill", Binding.Parse("K"));
        s.Save();

        var s2 = new Session(paths);
        Assert.Empty(s2.Load());
        Assert.Equal(1.5f, s2.Ui.UiScale);
        Assert.False(s2.Ui.IsOpen("History"));
        Assert.True(s2.Editor.AutoSelectLayer);
        Assert.Contains(Binding.Parse("K"), s2.Keymap.BindingsOf("tool.fill"));
    }

    [Fact]
    public void Text_input_disables_presses_but_not_releases()
    {
        var s = NewSession();
        var scopes = new[] { Scope.Canvas, Scope.Global };
        s.Hotkeys.Handle(new KeyEvent(ImGuiKey.Space, true), Mods.None, scopes);
        Assert.Equal(Momentary.Pan, s.Editor.Momentary);
        s.Hotkeys.Enabled = false;
        s.Hotkeys.Handle(new KeyEvent(ImGuiKey.G, true), Mods.None, scopes);
        Assert.Equal("Pencil", s.Editor.Tool.Name);
        s.Hotkeys.Handle(new KeyEvent(ImGuiKey.Space, false), Mods.None, scopes);
        Assert.Equal(Momentary.None, s.Editor.Momentary);
    }

    [Fact]
    public void Every_command_has_a_title_category_and_unique_id()
    {
        var s = NewSession();
        Assert.All(s.Commands.All, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title));
            Assert.False(string.IsNullOrWhiteSpace(c.Category));
        });
        Assert.Equal(s.Commands.All.Count, s.Commands.All.Select(c => c.Id).Distinct().Count());
    }
}
