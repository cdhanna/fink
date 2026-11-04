using System.IO.Compression;

namespace Fink.Core;

/// <summary>A minimal PNG writer (8-bit RGBA, no filtering), so export needs no graphics device.</summary>
public static class Png
{
    /// <param name="rgba">Packed pixels: R in the low byte, A in the high byte.</param>
    public static byte[] Encode(uint[] rgba, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + width * 4];
            for (int y = 0; y < height; y++)
            {
                row[0] = 0; // filter: none
                for (int x = 0; x < width; x++)
                {
                    uint p = rgba[y * width + x];
                    int o = 1 + x * 4;
                    row[o] = (byte)p;
                    row[o + 1] = (byte)(p >> 8);
                    row[o + 2] = (byte)(p >> 16);
                    row[o + 3] = (byte)(p >> 24);
                }
                z.Write(row);
            }
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width);
        WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>Reads back a PNG written by <see cref="Encode"/> (for tests).</summary>
    public static (uint[] rgba, int width, int height) Decode(byte[] png)
    {
        int pos = 8, w = 0, h = 0;
        using var idat = new MemoryStream();
        while (pos < png.Length)
        {
            int len = (int)ReadBE(png, pos);
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IHDR") { w = (int)ReadBE(png, pos + 8); h = (int)ReadBE(png, pos + 12); }
            if (type == "IDAT") idat.Write(png, pos + 8, len);
            pos += 12 + len;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var data = new MemoryStream();
        z.CopyTo(data);
        var bytes = data.ToArray();
        var result = new uint[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = y * (1 + w * 4) + 1 + x * 4;
            result[y * w + x] = (uint)(bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16) | (bytes[o + 3] << 24));
        }
        return (result, w, h);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        WriteBE(head, 0, (uint)data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        uint crc = Crc(head.AsSpan(4, 4), 0xFFFFFFFFu);
        crc = Crc(data, crc) ^ 0xFFFFFFFFu;
        var tail = new byte[4];
        WriteBE(tail, 0, crc);
        s.Write(tail);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void WriteBE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    private static uint ReadBE(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
}
