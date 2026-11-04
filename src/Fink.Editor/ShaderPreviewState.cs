using System.Text.Json;

namespace Fink.Editor;

/// <summary>Where a shader parameter's value comes from.</summary>
public enum ParamSource
{
    /// <summary>The numbers typed in.</summary>
    Value,
    /// <summary>Seconds since the preview started (x of a vector).</summary>
    Time,
    /// <summary>1 / sprite size: the size of one texel in UVs.</summary>
    TexelSize,
    /// <summary>The sprite's size in pixels.</summary>
    TextureSize,
}

/// <summary>The preview's setting for one shader parameter.</summary>
public sealed class ShaderParamState
{
    public float[] Values { get; set; } = new float[4];
    public ParamSource Source { get; set; }
    /// <summary>Show a float3/float4 as a color picker.</summary>
    public bool AsColor { get; set; }
    /// <summary>For texture parameters: "sprite", "" (none), or a PNG path.</summary>
    public string Texture { get; set; } = "";
}

/// <summary>
/// What the shader preview shows and with which inputs. Plain data, so it saves with the other
/// settings and scripts can set it; the window does the compiling and drawing.
/// </summary>
public sealed class ShaderPreviewState
{
    /// <summary>Folder searched for .fx files, usually a game's Assets folder.</summary>
    public string Folder { get; set; } = "";
    /// <summary>The shader being previewed; empty draws with the engine's plain sprite shader.</summary>
    public string File { get; set; } = "";
    public int Zoom { get; set; } = 4;
    /// <summary>Transparent pixels around the sprite, for outlines and glows.</summary>
    public int Margin { get; set; } = 4;
    public float[] Background { get; set; } = { 0.16f, 0.15f, 0.2f };
    public bool LinearSampling { get; set; }
    /// <summary>The sprite's vertex color (FadeSpriteBatch's color tint).</summary>
    public float[] VertexColor { get; set; } = { 1, 1, 1, 1 };
    /// <summary>Per-sprite values the engine passes in texcoord1 and texcoord2.</summary>
    public float[] TexCoord1 { get; set; } = new float[4];
    public float[] TexCoord2 { get; set; } = new float[4];
    /// <summary>Parameter settings, per shader file, per parameter name.</summary>
    public Dictionary<string, Dictionary<string, ShaderParamState>> Params { get; set; } = new();

    /// <summary>Set by the window: "", "compiling", "ok" or the compile error.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Status { get; set; } = "";

    public Dictionary<string, ShaderParamState> ParamsFor(string file)
    {
        var key = string.IsNullOrEmpty(file) ? "" : Path.GetFullPath(file).ToLowerInvariant();
        if (!Params.TryGetValue(key, out var d)) Params[key] = d = new Dictionary<string, ShaderParamState>();
        return d;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static ShaderPreviewState Load(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                return JsonSerializer.Deserialize<ShaderPreviewState>(System.IO.File.ReadAllText(path)) ?? new();
        }
        catch { /* a bad file just starts fresh */ }
        return new();
    }
}
