namespace Fink.Core;

public sealed class OnionSettings
{
    public bool Enabled { get; set; } = true;
    public int Before { get; set; } = 1;
    public int After { get; set; } = 1;
    /// <summary>Opacity of the nearest onion frame; each further frame fades by half.</summary>
    public float Opacity { get; set; } = 0.35f;
    public Rgba BeforeTint { get; set; } = new(230, 70, 70);
    public Rgba AfterTint { get; set; } = new(60, 170, 90);
}

/// <summary>Flattens a frame (plus onion skins) to packed RGBA for display.</summary>
public static class Compositor
{
    /// <param name="overlays">Per-layer document-sized pixels drawn just above that layer (a floating selection).</param>
    public static void Render(Document doc, int frameIndex, uint[] dest, OnionSettings? onion, bool checker = true,
        IReadOnlyDictionary<Layer, byte[]>? overlays = null, IReadOnlyList<Rgba>? colors = null)
    {
        colors ??= doc.Palette.Entries;
        int w = doc.Width, h = doc.Height;
        if (dest.Length < w * h) throw new ArgumentException("dest too small");

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool dark = (((x >> 3) + (y >> 3)) & 1) == 1;
            dest[y * w + x] = !checker ? 0u : dark ? 0xFFCCCCCCu : 0xFFF2F2F2u;
        }

        var frame = doc.Frames[frameIndex];
        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible) continue;
            // each layer's onion skin sits just under that layer, so an opaque background or
            // reference layer further down can't hide it
            if (onion is { Enabled: true } && layer.Role != LayerRole.Reference)
            {
                // by drawing, not by frame: on twos the previous frame is usually the same
                // drawing. farthest first, so nearer drawings sit on top
                for (int k = onion.Before; k >= 1; k--)
                    DrawOnion(doc, layer, frameIndex, -1, k, dest, onion.BeforeTint, onion.Opacity / (1 << (k - 1)));
                for (int k = onion.After; k >= 1; k--)
                    DrawOnion(doc, layer, frameIndex, +1, k, dest, onion.AfterTint, onion.Opacity / (1 << (k - 1)));
            }
            if (doc.TryGetCel(layer, frame, out var cel)) Draw(colors, cel.Pixels, dest);
            if (overlays != null && overlays.TryGetValue(layer, out var over)) Draw(colors, over, dest);
        }
    }

    private static void Draw(IReadOnlyList<Rgba> colors, byte[] px, uint[] dest)
    {
        for (int i = 0; i < px.Length; i++)
        {
            if (px[i] == Palette.Transparent) continue;
            var c = px[i] < colors.Count ? colors[px[i]] : new Rgba(255, 0, 255);
            dest[i] = c.A == 255 ? c.Packed : Blend(dest[i], c, c.A / 255f);
        }
    }

    /// <summary>A layer's k-th different drawing before or after this frame, tinted.</summary>
    private static void DrawOnion(Document doc, Layer layer, int frameIndex, int direction, int k, uint[] dest, Rgba tint, float opacity)
    {
        if (doc.NeighborDrawingFrame(layer, frameIndex, direction, k) is not { } at) return;
        if (!doc.TryGetCel(layer, doc.Frames[at], out var cel)) return;
        var px = cel.Pixels;
        for (int i = 0; i < px.Length; i++)
            if (px[i] != Palette.Transparent) dest[i] = Blend(dest[i], tint, opacity);
    }

    private static uint Blend(uint dst, Rgba src, float a)
    {
        byte r = (byte)(dst & 0xFF), g = (byte)((dst >> 8) & 0xFF), b = (byte)((dst >> 16) & 0xFF);
        r = (byte)(r + (src.R - r) * a);
        g = (byte)(g + (src.G - g) * a);
        b = (byte)(b + (src.B - b) * a);
        return (uint)(r | (g << 8) | (b << 16) | (0xFF << 24));
    }
}
