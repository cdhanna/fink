using Fink.Core;
using Fink.Editor;

namespace Fink.Editor.Tests;

public class ThemeTests
{
    [Fact]
    public void Built_in_themes_have_valid_colors()
    {
        Assert.Contains(Themes.BuiltIn, t => t.Name == Themes.Default);
        foreach (var t in Themes.BuiltIn)
        {
            Assert.Contains(t.Base, new[] { "dark", "light", "classic" });
            foreach (var hex in t.Ui.Values) Rgba.FromHex(hex);
            var c = t.Canvas;
            foreach (var hex in new[] { c.Background, c.CheckerLight, c.CheckerDark, c.OnionBefore, c.OnionAfter, c.Line, c.Fill, c.Reference, c.Playhead, c.Accent })
                Rgba.FromHex(hex);
        }
    }

    [Fact]
    public void A_copy_is_saved_found_and_read_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fink-tests", Guid.NewGuid().ToString("N"), "themes");
        var path = Themes.SaveCopy(Themes.BuiltIn.Single(t => t.Name == "Neutral Gray"), dir);
        Assert.EndsWith("Neutral Gray (mine).json", path);

        // edit it the way a person would: change a color, keep the rest
        var text = File.ReadAllText(path).Replace("#808080", "#336699");
        File.WriteAllText(path, text);
        var all = Themes.All(dir);
        var mine = all.Single(t => t.Name == "Neutral Gray (mine)");
        Assert.False(mine.BuiltIn);
        Assert.Equal(path, mine.File);
        Assert.Equal("#336699", mine.Canvas.Background);
        Assert.Equal("#2b2b2b", mine.Ui["WindowBg"]);
        Assert.Equal(Themes.BuiltIn.Length + 1, all.Count);
    }

    [Fact]
    public void The_theme_choice_is_remembered()
    {
        var root = Path.Combine(Path.GetTempPath(), "fink-tests", Guid.NewGuid().ToString("N"));
        var s = new Session(new SettingsPaths(root));
        s.Ui.ThemeName = "Light";
        s.Save();
        var again = new Session(new SettingsPaths(root));
        again.Load();
        Assert.Equal("Light", again.Ui.ThemeName);
    }
}
