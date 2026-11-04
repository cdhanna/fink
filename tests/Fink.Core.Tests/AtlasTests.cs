using System.Text.Json;
using Fink.Core;

namespace Fink.Core.Tests;

public class AtlasTests
{
    /// <summary>hero (16x16): drawings 1 1 2 1, tagged walk 2-4; coin (8x8) one drawing.</summary>
    private static Project Sample()
    {
        var p = Project.CreateDefault(16, 16);
        var hero = p.Assets[0];
        hero.Name = "hero";
        var h = new History();
        var line = hero.Layers[1];
        var cel = hero.GetCel(line, hero.Frames[0]);
        for (int x = 4; x < 10; x++) cel.Pixels[5 * 16 + x] = 1;     // a 6x1 bar at (4,5)
        DocumentOps.InsertFrame(hero, h, 1, NewFrameKind.Hold, line);
        DocumentOps.InsertFrame(hero, h, 2, NewFrameKind.Blank, line);
        var c2 = hero.GetCel(line, hero.Frames[2]);
        c2.Pixels[2 * 16 + 3] = 7;
        c2.Pixels[4 * 16 + 5] = 8;                                   // 3x3 box from (3,2)
        DocumentOps.InsertFrame(hero, h, 3, NewFrameKind.Blank, line);
        DocumentOps.SetCell(hero, h, line, 3, 1);
        DocumentOps.AddTag(hero, h, "walk", 1, 3);

        var coin = p.AddAsset("coin", 8, 8);
        coin.GetCel(coin.Layers[1], coin.Frames[0]).Pixels[0] = 5;
        return p;
    }

    [Fact]
    public void Pictures_are_trimmed_shared_and_packed()
    {
        var p = Sample();
        var b = AtlasBuilder.Build(p, p.Atlases[0]);
        Assert.Null(b.Error);
        Assert.Equal(3, b.Cells.Count); // bar, box, coin dot: drawing 1 reused is one picture
        Assert.Contains(b.Cells, c => (c.W, c.H) == (6, 1));
        Assert.Contains(b.Cells, c => (c.W, c.H) == (3, 3));
        Assert.Contains(b.Cells, c => (c.W, c.H) == (1, 1));
        // power of two, everything inside, nothing overlapping (with padding)
        Assert.True((b.Width & (b.Width - 1)) == 0 && (b.Height & (b.Height - 1)) == 0);
        foreach (var c in b.Cells)
        {
            Assert.True(c.X >= 1 && c.Y >= 1 && c.X + c.W <= b.Width - 1 && c.Y + c.H <= b.Height - 1);
            foreach (var d in b.Cells.Where(d => d != c))
                Assert.True(c.X + c.W + 1 <= d.X || d.X + d.W + 1 <= c.X || c.Y + c.H + 1 <= d.Y || d.Y + d.H + 1 <= c.Y);
        }
        // the atlas holds the indices
        var box = b.Cells.Single(c => c.W == 3);
        Assert.Equal(7, b.Indices[box.Y * b.Width + box.X]);
        Assert.Equal(8, b.Indices[(box.Y + 2) * b.Width + box.X + 2]);
    }

    [Fact]
    public void Clips_have_offsets_times_and_stable_ids()
    {
        var p = Sample();
        var hero = p.Assets[0];
        var b = AtlasBuilder.Build(p, p.Atlases[0]);
        var whole = b.Clips.Single(c => c.Asset == hero && c.Name == "");
        Assert.Equal(hero.TimelineTagId, whole.Id);
        // runs: [0] (tag starts at 1, so the 1 1 hold splits), [1], [2], [3]
        Assert.Equal(new[] { (4, 5), (4, 5), (3, 2), (4, 5) }, whole.Frames.Select(f => (f.OffsetX, f.OffsetY)));
        var walk = b.Clips.Single(c => c.Name == "walk");
        Assert.Equal(hero.Tags[0].Id, walk.Id);
        Assert.Equal(3, walk.Frames.Count);
        Assert.Equal(250, walk.Frames.Sum(f => f.Ms)); // 3 frames at 12 fps
        // ids are unique and survive save/load
        var ids = b.Clips.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(0, ids);
        using var ms = new MemoryStream();
        ProjectFile.Write(p, ms);
        ms.Position = 0;
        var back = ProjectFile.Read(ms);
        Assert.Equal(ids, AtlasBuilder.Build(back, back.Atlases[0]).Clips.Select(c => c.Id));
        // a new tag never reuses an id
        DocumentOps.RemoveTag(hero, new History(), hero.Tags[0]);
        var again = DocumentOps.AddTag(hero, new History(), "run", 0, 1);
        Assert.DoesNotContain(again.Id, ids);
    }

    [Fact]
    public void Packing_keeps_places_until_asked_to_repack()
    {
        var p = Sample();
        var config = p.Atlases[0];
        var first = AtlasBuilder.Build(p, config);
        var places = first.Cells.ToDictionary(c => c.Key, c => (c.X, c.Y));

        // a new picture arrives: the old ones stay put
        var coin = p.Assets[1];
        var cel = coin.GetCel(coin.Layers[1], coin.Frames[0]);
        Array.Fill(cel.Pixels, (byte)3, 8, 8); // a second row: the coin picture changes to 8x2
        var second = AtlasBuilder.Build(p, config);
        foreach (var c in second.Cells.Where(c => places.ContainsKey(c.Key)))
            Assert.Equal(places[c.Key], (c.X, c.Y));

        var tight = AtlasBuilder.Build(p, config, tight: true);
        Assert.True(tight.Width * tight.Height <= second.Width * second.Height);
    }

    [Fact]
    public void Too_big_says_why()
    {
        var p = Project.CreateDefault(64, 64);
        Array.Fill(p.Assets[0].GetCel(p.Assets[0].Layers[1], p.Assets[0].Frames[0]).Pixels, (byte)1);
        p.AssignIds();
        p.Atlases[0].MaxSize = 32;
        var b = AtlasBuilder.Build(p, p.Atlases[0]);
        Assert.Contains("doesn't fit", b.Error);
    }

    [Fact]
    public void Export_writes_index_palette_and_manifest()
    {
        var p = Sample();
        p.Palette.AddVariant("red").Entries[1] = new Rgba(255, 0, 0);
        var dir = Path.Combine(Path.GetTempPath(), "fink-atlas-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            p.Atlases[0].Name = "chars";
            p.Atlases[0].OutputFolder = dir;
            var b = AtlasBuilder.Build(p, p.Atlases[0]);
            var files = AtlasExport.Write(p, b).Select(Path.GetFileName).ToList();
            Assert.Contains("chars.png", files);
            Assert.Contains("chars.palette.png", files);
            Assert.Contains("chars.atlas.json", files);

            var (idx, w, h) = Png.Decode(File.ReadAllBytes(Path.Combine(dir, "chars.png")));
            Assert.Equal((b.Width, b.Height), (w, h));
            var box = b.Cells.Single(c => c.W == 3);
            Assert.Equal(0xFF000007u, idx[box.Y * w + box.X]); // R = index, A = 255
            Assert.Equal(0u, idx[0]);

            var (pal, pw, ph) = Png.Decode(File.ReadAllBytes(Path.Combine(dir, "chars.palette.png")));
            Assert.Equal((256, 2), (pw, ph));
            Assert.Equal(p.Palette.Entries[1].Packed, pal[1]);
            Assert.Equal(new Rgba(255, 0, 0).Packed, pal[256 + 1]);

            var m = JsonSerializer.Deserialize<FinkAtlasManifest>(File.ReadAllText(Path.Combine(dir, "chars.atlas.json")), AtlasExport.Json)!;
            Assert.Equal(new[] { "base", "red" }, m.PaletteRows);
            Assert.Equal(b.Cells.Count, m.Cells.Count);
            Assert.Contains(m.Clips, c => c.Name == "walk" && c.Frames.Count == 3);
            Assert.True(m.Stamp > 0);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("walk", "WALK")]
    [InlineData("Hero Walk!", "HERO_WALK")]
    [InlineData("  run--fast ", "RUN_FAST")]
    [InlineData("2nd", "N2ND")]
    [InlineData("!!!", "X")]
    public void Names_become_identifiers(string name, string expected) =>
        Assert.Equal(expected, FadeBasicBindings.Identifier(name));

    [Fact]
    public void Generated_code_has_every_clip_and_palette_row()
    {
        var p = Sample();
        p.Palette.AddVariant("red team");
        p.Atlases[0].Name = "chars";
        var b = AtlasBuilder.Build(p, p.Atlases[0]);
        var code = FadeBasicBindings.Generate(p, b, AtlasExport.Manifest(b, p.Palette));
        var hero = p.Assets[0];
        Assert.Contains($"#constant TAG_HERO {hero.TimelineTagId}", code);
        Assert.Contains($"#constant TAG_HERO_WALK {hero.Tags[0].Id}", code);
        Assert.Contains($"#constant TAG_COIN {p.Assets[1].TimelineTagId}", code);
        Assert.Contains("#constant PAL_BASE 0", code);
        Assert.Contains("#constant PAL_RED_TEAM 1", code);
        Assert.Contains("FUNCTION fink_load_chars()", code);
        Assert.Contains("fink atlas load(\"Textures/chars\", FINK_CHARS_STAMP)", code);
        Assert.Contains("ENDFUNCTION atlasId", code);
    }

    [Fact]
    public void Names_that_clash_stop_the_export()
    {
        var p = Sample();
        DocumentOps.AddTag(p.Assets[0], new History(), "Walk!", 0, 0); // WALK again
        var b = AtlasBuilder.Build(p, p.Atlases[0]);
        var e = Assert.Throws<InvalidOperationException>(() => FadeBasicBindings.Constants(b, p.Palette));
        Assert.Contains("TAG_HERO_WALK", e.Message);
    }
}
