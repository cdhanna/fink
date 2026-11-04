namespace Fink.Core;

/// <summary>Shows a data channel on the canvas: normals as their map colors, values as gray.</summary>
public static class ChannelView
{
    /// <summary>
    /// Replaces each drawn pixel of <paramref name="dest"/> (already showing the frame) with its
    /// channel value, taken from the layer whose color is on top there. Unpainted pixels are dimmed.
    /// </summary>
    public static void Overlay(Document doc, int frameIndex, DataChannel channel, uint[] dest)
    {
        var layers = doc.Layers.Where(l => l.Visible && l.Role != LayerRole.Reference).ToList();
        var flat = new byte[doc.Width * doc.Height];
        var from = new Layer?[flat.Length];
        AtlasBuilder.FlattenIndices(doc, layers, frameIndex, flat, from);
        var frame = doc.Frames[frameIndex];
        var cache = new Dictionary<Layer, Cel?>();
        for (int i = 0; i < flat.Length; i++)
        {
            if (from[i] is not { } layer) continue;
            if (!cache.TryGetValue(layer, out var cel))
                cache[layer] = cel = doc.DrawingAt(layer, frame) is { } d && d.Channels.TryGetValue(channel.Id, out var c) ? c : null;
            byte v = cel?.Pixels[i] ?? 0;
            if (v == 0)
            {
                // not painted: the color, dark
                uint p = dest[i];
                uint r = (p & 0xFF) / 4, g = ((p >> 8) & 0xFF) / 4, b = ((p >> 16) & 0xFF) / 4;
                dest[i] = r | (g << 8) | (b << 16) | 0xFF000000u;
                continue;
            }
            dest[i] = channel.Kind == ChannelKind.Normal
                ? Normals.Color(v).Packed
                : (uint)(v | (v << 8) | (v << 16)) | 0xFF000000u;
        }
    }
}
