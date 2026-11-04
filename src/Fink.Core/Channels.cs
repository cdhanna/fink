namespace Fink.Core;

public enum ChannelKind
{
    /// <summary>One value per pixel, 0..255 (emissive, a mask, anything labeled).</summary>
    Value,
    /// <summary>A surface direction per pixel, painted from <see cref="Normals"/>; exports x, y, z.</summary>
    Normal,
}

/// <summary>
/// A kind of data painted on drawings alongside their colors. Project-wide: every asset can
/// paint every channel. Where a drawing has color, its channel values say something about that
/// pixel (how much it glows, which way it faces).
/// </summary>
public sealed record DataChannel(int Id, string Name, ChannelKind Kind);

/// <summary>
/// The directions a Normal channel is painted with. Index 0 is "nothing painted" (exported as
/// facing the viewer); 1 faces the viewer; then eight directions tilted 45° and eight tilted 75°.
/// Indices, like palette colors, so painting and filling stay exact.
/// </summary>
public static class Normals
{
    public static readonly (float x, float y, float z)[] Directions = Build();

    private static (float, float, float)[] Build()
    {
        var list = new List<(float, float, float)> { (0, 0, 1), (0, 0, 1) };
        foreach (var tilt in new[] { 45.0, 75.0 })
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4; // 0 = right, then clockwise on screen (y down)
                double s = Math.Sin(tilt * Math.PI / 180), c = Math.Cos(tilt * Math.PI / 180);
                list.Add(((float)(Math.Cos(a) * s), (float)(Math.Sin(a) * s), (float)c));
            }
        return list.ToArray();
    }

    public static string NameOf(int index) => index switch
    {
        0 => "none",
        1 => "facing you",
        _ => $"{new[] { "right", "down-right", "down", "down-left", "left", "up-left", "up", "up-right" }[(index - 2) % 8]}" +
             (index >= 10 ? ", steep" : ""),
    };

    /// <summary>The usual normal-map encoding: each component from -1..1 to 0..255.</summary>
    public static byte Encode(float v) => (byte)Math.Clamp((int)Math.Round((v + 1) * 127.5), 0, 255);

    /// <summary>A color to show a direction with (the same encoding as the exported map).</summary>
    public static Rgba Color(int index)
    {
        var (x, y, z) = Directions[Math.Clamp(index, 0, Directions.Length - 1)];
        return new Rgba(Encode(x), Encode(-y), Encode(z)); // y up in the map, as most lighting expects
    }
}

/// <summary>One exported texture beside an atlas: which channel goes into each of R, G, B, A.</summary>
public sealed class MapConfig
{
    public string Name { get; set; } = "map";
    /// <summary>
    /// Each slot is "" (0), a number 0..255 (constant), a Value channel's name, or a Normal
    /// channel's name plus .x / .y / .z.
    /// </summary>
    public string R { get; set; } = "";
    public string G { get; set; } = "";
    public string B { get; set; } = "";
    public string A { get; set; } = "255";

    public string[] Slots => new[] { R, G, B, A };
}
