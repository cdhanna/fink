using Fink.Core;

namespace Fink.Core.Tests;

public class ProjectFileTests
{
    private static string Track(Document doc, Layer layer) =>
        string.Join(' ', doc.Frames.Select(f => doc.DrawingAt(layer, f)?.Number.ToString() ?? "-"));

    private static Project Sample()
    {
        var project = Project.CreateDefault(8, 6);
        project.Name = "game";
        var pal = project.Palette;
        pal.Set(3, new Rgba(10, 20, 30, 128));
        pal.Variants.Add(new PaletteVariant("night", pal.Entries.Select(c => c with { B = 200 })));
        pal.Ramps[0].Name = "Outline";

        var hero = project.Assets[0];
        hero.Name = "hero";
        hero.Fps = 24;
        var history = new History();
        var fill = hero.Layers[0];
        var line = hero.Layers[1];
        var bg = DocumentOps.AddLayer(hero, history, 0, "BG", LayerRole.Reference);
        bg.Locked = true;
        hero.GetCel(bg, hero.Frames[0]).Pixels[5] = 4;
        hero.GetCel(line, hero.Frames[0]).Pixels[0] = 1;
        hero.GetCel(fill, hero.Frames[0]).Pixels[9] = 9;
        DocumentOps.InsertFrame(hero, history, 1, NewFrameKind.Hold, line);
        DocumentOps.InsertFrame(hero, history, 2, NewFrameKind.Duplicate, line);
        hero.GetCel(line, hero.Frames[2]).Pixels[1] = 2;
        DocumentOps.InsertFrame(hero, history, 3, NewFrameKind.Blank, line);
        DocumentOps.SetCell(hero, history, line, 3, 1); // reuse drawing 1: 1 1 2 1
        DocumentOps.AddTag(hero, history, "walk", 1, 3, LoopMode.PingPong);

        var coin = project.AddAsset("coin", 4, 4);
        coin.GetCel(coin.Layers[1], coin.Frames[0]).Pixels[3] = 5;
        return project;
    }

    private static Project RoundTrip(Project p)
    {
        using var ms = new MemoryStream();
        ProjectFile.Write(p, ms);
        ms.Position = 0;
        return ProjectFile.Read(ms);
    }

    [Fact]
    public void Everything_survives_a_round_trip()
    {
        var back = RoundTrip(Sample());
        Assert.Equal("game", back.Name);
        Assert.Equal(new[] { "hero", "coin" }, back.Assets.Select(a => a.Name));

        // palette, ramps, variants, shared by every asset
        Assert.Equal(new Rgba(10, 20, 30, 128), back.Palette.Entries[3]);
        Assert.Equal("Outline", back.Palette.Ramps[0].Name);
        Assert.Equal(Palette.CreateDefault().Ramps.Count, back.Palette.Ramps.Count);
        Assert.Equal("night", back.Palette.Variants.Single().Name);
        Assert.Equal(200, back.Palette.Variants[0].Entries[5].B);
        Assert.All(back.Assets, a => Assert.Same(back.Palette, a.Palette));

        var hero = back.Assets[0];
        Assert.Equal((8, 6, 24), (hero.Width, hero.Height, hero.Fps));
        Assert.Equal(new[] { "BG", "Fill", "Line" }, hero.Layers.Select(l => l.Name));
        var bg = hero.Layers[0];
        var fill = hero.Layers[1];
        var line = hero.Layers[2];
        Assert.Equal(LayerRole.Reference, bg.Role);
        Assert.True(bg.Locked);
        Assert.Same(line, hero.TimingLayer(fill));
        Assert.Equal("1 1 2 1", Track(hero, line));
        Assert.Equal("1 1 2 1", Track(hero, fill));
        Assert.Equal("1 1 1 1", Track(hero, bg));
        // reuse is the same drawing, not a copy
        Assert.Same(hero.DrawingAt(line, hero.Frames[0]), hero.DrawingAt(line, hero.Frames[3]));
        Assert.Equal(2, hero.DrawingAt(line, hero.Frames[2])!.Cel.Pixels[1]);
        Assert.Equal(9, hero.DrawingAt(fill, hero.Frames[2])!.Cel.Pixels[9]); // duplicated fill
        Assert.Equal(4, hero.DrawingAt(bg, hero.Frames[0])!.Cel.Pixels[5]);

        var tag = hero.Tags.Single();
        Assert.Equal(("walk", (1, 3), LoopMode.PingPong), (tag.Name, hero.TagRange(tag), tag.Loop));

        var coin = back.Assets[1];
        Assert.Equal(5, coin.DrawingAt(coin.Layers[1], coin.Frames[0])!.Cel.Pixels[3]);
    }

    [Fact]
    public void Saving_replaces_the_file_in_one_step()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fink-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = Path.Combine(dir, "game.fink");
            ProjectFile.Save(Sample(), path);
            var p = ProjectFile.Load(path);
            p.Assets[0].Name = "renamed";
            ProjectFile.Save(p, path);
            Assert.Equal("renamed", ProjectFile.Load(path).Assets[0].Name);
            Assert.Single(Directory.GetFiles(dir)); // no temp file left behind
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_newer_format_is_refused_with_a_reason()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry("fink.json").Open());
            w.Write("{\"format\":\"fink\",\"version\":99,\"palette\":{},\"assets\":[]}");
        }
        ms.Position = 0;
        var e = Assert.Throws<InvalidDataException>(() => ProjectFile.Read(ms));
        Assert.Contains("newer", e.Message);
    }
}
