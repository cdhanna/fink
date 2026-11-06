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
    /// <summary>Pinned frames show in this tint.</summary>
    public Rgba PinTint { get; set; } = new(70, 140, 255);
    /// <summary>Show pinned frames' drawings.</summary>
    public bool ShowPins { get; set; } = true;
    public const int MaxCount = 8;
}

public enum OnionKind { Before, After, Pinned }

/// <summary>One ghost: the drawing a layer shows on <see cref="Frame"/>, tinted, at an opacity.</summary>
public readonly record struct OnionGhost(int Frame, OnionKind Kind, float Opacity, Rgba Tint);

/// <summary>
/// Which ghosts a layer gets on a frame. One place for it, so the canvas and the timeline (which
/// marks them) always agree.
/// </summary>
public static class Onion
{
    public static int BeforeFor(Layer layer, OnionSettings o) => Math.Clamp(layer.OnionBefore ?? o.Before, 0, OnionSettings.MaxCount);
    public static int AfterFor(Layer layer, OnionSettings o) => Math.Clamp(layer.OnionAfter ?? o.After, 0, OnionSettings.MaxCount);

    /// <summary>Farthest first, so nearer ghosts are drawn on top. Pinned frames first of all.</summary>
    public static List<OnionGhost> Plan(Document doc, Layer layer, int frameIndex, OnionSettings? o)
    {
        var ghosts = new List<OnionGhost>();
        if (o is not { Enabled: true } || layer.OnionOff || layer.Role == LayerRole.Reference) return ghosts;
        var here = doc.DrawingAt(layer, doc.Frames[frameIndex]);
        // by drawing, not by frame: on twos the previous frame is usually the same drawing
        for (int k = BeforeFor(layer, o); k >= 1; k--)
            if (doc.NeighborDrawingFrame(layer, frameIndex, -1, k) is { } at)
                ghosts.Add(new OnionGhost(at, OnionKind.Before, o.Opacity / (1 << (k - 1)), o.BeforeTint));
        for (int k = AfterFor(layer, o); k >= 1; k--)
            if (doc.NeighborDrawingFrame(layer, frameIndex, +1, k) is { } at)
                ghosts.Add(new OnionGhost(at, OnionKind.After, o.Opacity / (1 << (k - 1)), o.AfterTint));
        if (!o.ShowPins) return ghosts;
        // pins go under the rest; one already ghosted, or showing the same drawing as here, adds nothing
        var shown = ghosts.Select(g => doc.DrawingAt(layer, doc.Frames[g.Frame])).ToHashSet();
        var pins = new List<OnionGhost>();
        foreach (var pin in doc.OnionPins)
        {
            int i = doc.Frames.IndexOf(pin);
            if (i < 0 || i == frameIndex || doc.DrawingAt(layer, pin) is not { } d || d == here || !shown.Add(d)) continue;
            pins.Add(new OnionGhost(i, OnionKind.Pinned, o.Opacity, o.PinTint));
        }
        pins.AddRange(ghosts);
        return pins;
    }
}

/// <summary>Flattens a frame (plus onion skins) to packed RGBA for display.</summary>
public static class Compositor
{
    /// <param name="overlays">Per-layer document-sized pixels drawn just above that layer (a floating selection).</param>
    public static void Render(Document doc, int frameIndex, uint[] dest, OnionSettings? onion, bool checker = true,
        IReadOnlyDictionary<Layer, byte[]>? overlays = null, IReadOnlyList<Rgba>? colors = null,
        uint checkerLight = 0xFFF2F2F2u, uint checkerDark = 0xFFCCCCCCu, bool viewOpacity = false)
    {
        colors ??= doc.Palette.Entries;
        int w = doc.Width, h = doc.Height;
        if (dest.Length < w * h) throw new ArgumentException("dest too small");

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool dark = (((x >> 3) + (y >> 3)) & 1) == 1;
            dest[y * w + x] = !checker ? 0u : dark ? checkerDark : checkerLight;
        }

        var frame = doc.Frames[frameIndex];
        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible) continue;
            // each layer's onion skin sits just under that layer, so an opaque background or
            // reference layer further down can't hide it
            // the layer's working opacity: the canvas only (viewOpacity), never exports or thumbnails
            float op = viewOpacity ? Math.Clamp(layer.ViewOpacity, 0f, 1f) : 1f;
            if (op <= 0) continue;
            foreach (var g in Onion.Plan(doc, layer, frameIndex, onion))
                if (doc.TryGetCel(layer, doc.Frames[g.Frame], out var ghost))
                    Tint(ghost.Pixels, dest, g.Tint, g.Opacity * op);
            if (doc.TryGetCel(layer, frame, out var cel)) Draw(colors, cel.Pixels, dest, op);
            if (overlays != null && overlays.TryGetValue(layer, out var over)) Draw(colors, over, dest, op);
        }
    }

    private static void Draw(IReadOnlyList<Rgba> colors, byte[] px, uint[] dest, float opacity = 1f)
    {
        for (int i = 0; i < px.Length; i++)
        {
            if (px[i] == Palette.Transparent) continue;
            var c = px[i] < colors.Count ? colors[px[i]] : new Rgba(255, 0, 255);
            dest[i] = c.A == 255 && opacity >= 1f ? c.Packed : Blend(dest[i], c, c.A / 255f * opacity);
        }
    }

    private static void Tint(byte[] px, uint[] dest, Rgba tint, float opacity)
    {
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
