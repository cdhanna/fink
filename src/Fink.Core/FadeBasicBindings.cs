using System.Text;

namespace Fink.Core;

/// <summary>
/// Generates what a fade.mono game needs to use an atlas without naming anything by string:
/// <list type="bullet">
/// <item><c>fink_&lt;atlas&gt;.fbasic</c>: <c>#constant</c>s for every clip (TAG_ASSET_TAG, TAG_ASSET for the
/// whole timeline) and palette row (PAL_BASE, PAL_RED...), and a function that loads the atlas.
/// List it FIRST in the game's FadeSource items: constants are text replacement, so they must
/// come before they're used.</item>
/// <item><c>&lt;atlas&gt;_palette.fx</c>: the shader that turns the atlas's palette indices into colors.
/// It goes next to the textures so the game's content build compiles it for its backend.</item>
/// </list>
/// </summary>
public static class FadeBasicBindings
{
    public static string FileName(AtlasConfig config) => $"fink_{config.Name}.fbasic";
    public static string ShaderFileName(AtlasConfig config) => $"{config.Name}_palette.fx";
    public static string LoadFunction(AtlasConfig config) => $"fink_load_{config.Name.ToLowerInvariant().Replace('-', '_')}";

    /// <summary>NAME_LIKE_THIS from any name: letters and digits, everything else an underscore.</summary>
    public static string Identifier(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var id = sb.ToString().Trim('_');
        if (id.Length == 0) id = "X";
        if (char.IsDigit(id[0])) id = "N" + id;
        return id;
    }

    /// <summary>The constants, in file order. Throws when two different things get the same name.</summary>
    public static List<(string name, int value, string what)> Constants(AtlasBuild build, Palette palette)
    {
        var list = new List<(string name, int value, string what)>();
        var taken = new Dictionary<string, string>();
        void Add(string name, int value, string what)
        {
            if (taken.TryGetValue(name, out var other))
                throw new InvalidOperationException($"{what} and {other} both become {name} in FadeBasic. Rename one of them.");
            taken[name] = what;
            list.Add((name, value, what));
        }
        foreach (var asset in build.Assets)
        {
            var a = Identifier(asset.Name);
            foreach (var clip in build.Clips.Where(c => c.Asset == asset))
            {
                if (clip.Name == "") Add($"TAG_{a}", clip.Id, $"asset \"{asset.Name}\" (whole timeline)");
                else Add($"TAG_{a}_{Identifier(clip.Name)}", clip.Id, $"tag \"{clip.Name}\" of \"{asset.Name}\"");
            }
        }
        foreach (var asset in build.Assets)
            foreach (var p in asset.Points.Where(p => !p.IsPivot))
                Add($"POINT_{Identifier(asset.Name)}_{Identifier(p.Name)}", p.Id, $"point \"{p.Name}\" of \"{asset.Name}\"");
        for (int i = 0; i < build.Config.Maps.Count; i++)
            Add($"MAP_{Identifier(build.Config.Maps[i].Name)}", i, $"map \"{build.Config.Maps[i].Name}\"");
        Add("PAL_BASE", 0, "the base palette");
        for (int i = 0; i < palette.Variants.Count; i++)
            Add($"PAL_{Identifier(palette.Variants[i].Name)}", i + 1, $"palette variant \"{palette.Variants[i].Name}\"");
        return list;
    }

    public static string Generate(Project project, AtlasBuild build, FinkAtlasManifest manifest)
    {
        var config = build.Config;
        var atlas = Identifier(config.Name);
        var constants = Constants(build, project.Palette);
        var sb = new StringBuilder();
        sb.AppendLine($"` FINK ATLAS \"{config.Name}\", made by Fink from {project.Name}.fink. Do not change this file: change the art");
        sb.AppendLine("` in Fink and export again (Ctrl+E). List this file FIRST in the game's FadeSource items: the constants");
        sb.AppendLine("` are text replacement, so they have to come before anything that uses them.");
        sb.AppendLine("`");
        sb.AppendLine($"`   atlas = {LoadFunction(config)}()     loads it, once, at the start");
        sb.AppendLine("`   play fink sprite spr, TAG_HERO_WALK     plays a clip on a sprite");
        sb.AppendLine("`   set fink sprite palette spr, PAL_RED    swaps its colors");
        sb.AppendLine();
        sb.AppendLine($"#constant FINK_{atlas}_STAMP {manifest.Stamp}");
        sb.AppendLine();
        sb.AppendLine("` clips: TAG_<ASSET> is an asset's whole timeline, TAG_<ASSET>_<TAG> one of its tags");
        foreach (var (name, value, _) in constants.Where(c => c.name.StartsWith("TAG_")))
            sb.AppendLine($"#constant {name} {value}");
        sb.AppendLine();
        if (constants.Any(c => c.name.StartsWith("POINT_")))
        {
            sb.AppendLine("` points: fink sprite point x(spr, POINT_HERO_HAND) / y() say where one is on screen");
            foreach (var (name, value, _) in constants.Where(c => c.name.StartsWith("POINT_")))
                sb.AppendLine($"#constant {name} {value}");
            sb.AppendLine();
        }
        if (constants.Any(c => c.name.StartsWith("MAP_")))
        {
            sb.AppendLine("` data maps: fink atlas map(atlas, MAP_NORMAL) is the map's texture id, for set effect param texture");
            foreach (var (name, value, _) in constants.Where(c => c.name.StartsWith("MAP_")))
                sb.AppendLine($"#constant {name} {value}");
            sb.AppendLine();
        }
        sb.AppendLine("` palette rows: which colors a sprite is drawn with");
        foreach (var (name, value, _) in constants.Where(c => c.name.StartsWith("PAL_")))
            sb.AppendLine($"#constant {name} {value}");
        sb.AppendLine();
        sb.AppendLine($"FUNCTION {LoadFunction(config)}()");
        sb.AppendLine($"    atlasId = fink atlas load(\"{config.EffectiveContentPath}\", FINK_{atlas}_STAMP)");
        sb.AppendLine("ENDFUNCTION atlasId");
        return sb.ToString();
    }

    /// <summary>
    /// The palette shader: the sprite's texture holds palette indices (red channel), the Palette
    /// texture holds a row of colors per variant, and texcoord1.x picks the row. Pixel shader
    /// only, so it keeps the engine's sprite vertex shader. Outputs straight alpha, which is what
    /// fade.mono's default NonPremultiplied blend expects.
    /// </summary>
    public static string PaletteShader(AtlasConfig config) => $$"""
        // {{config.Name}}_palette.fx, made by Fink. Do not change this file; Fink writes it on every export.
        //
        // Draws a Fink atlas: the sprite's texture holds palette indices (red channel, alpha 0 or 1),
        // the Palette texture holds 256 colors per row, one row per palette variant, and texcoord1.x
        // picks the row (Fade.MonoGame.Fink sets it: set fink sprite palette). The sprite's color
        // still tints it. Fade.MonoGame.Fink sets Palette and PaletteRows every frame.
        #if defined(SM6) || defined(VULKAN)
            #define PS_SHADERMODEL ps_6_0
            #define PS_TARGET SV_Target0
            Texture2D<float4> SpriteTexture : register(t0);
            sampler SpriteSampler : register(s0);
            Texture2D<float4> Palette : register(t1);
            sampler PaletteSampler : register(s1) = sampler_state
            {
                MinFilter = Point; MagFilter = Point; MipFilter = Point; AddressU = Clamp; AddressV = Clamp;
            };
            #define SAMPLE(t, s, uv) t.Sample(s, uv)
        #else
            #define PS_SHADERMODEL ps_3_0
            #define PS_TARGET COLOR0
            Texture2D SpriteTexture;
            sampler2D SpriteSampler : register(s0) = sampler_state { Texture = <SpriteTexture>; };
            Texture2D Palette;
            sampler2D PaletteSampler : register(s1) = sampler_state
            {
                Texture = <Palette>;
                MinFilter = Point; MagFilter = Point; MipFilter = Point; AddressU = Clamp; AddressV = Clamp;
            };
            #define SAMPLE(t, s, uv) tex2D(s, uv)
        #endif

        float PaletteRows;

        float4 MainPS(float4 color : COLOR0, float2 uv : TEXCOORD0, float4 custom : TEXCOORD1) : PS_TARGET
        {
            float4 indexed = SAMPLE(SpriteTexture, SpriteSampler, uv);
            if (indexed.a < 0.5) return float4(0, 0, 0, 0);
            float index = floor(indexed.r * 255.0 + 0.5);
            float rows = max(PaletteRows, 1.0);
            float row = clamp(floor(custom.x + 0.5), 0.0, rows - 1.0);
            float4 c = SAMPLE(Palette, PaletteSampler, float2((index + 0.5) / 256.0, (row + 0.5) / rows));
            // the content build premultiplies textures; the sprite batch blends straight alpha
            c.rgb /= max(c.a, 0.0001);
            return c * color;
        }

        technique SpriteDrawing
        {
            pass P0
            {
                PixelShader = compile PS_SHADERMODEL MainPS();
            }
        };
        """;
}
