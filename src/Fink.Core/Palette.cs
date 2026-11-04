namespace Fink.Core;

/// <summary>An RGBA color, packed the way MonoGame's <c>Color</c> is (R in the low byte).</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public uint Packed => (uint)(R | (G << 8) | (B << 16) | (A << 24));

    public static Rgba FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        var v = Convert.ToUInt32(hex, 16);
        return hex.Length == 8
            ? new Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

/// <summary>A named run of palette entries that belong together (a skin ramp, a cloak ramp).</summary>
public sealed class PaletteRamp
{
    public string Name { get; set; }
    /// <summary>First entry index (1 or more: 0 is transparent).</summary>
    public int Start { get; set; }
    public int Count { get; set; }

    public PaletteRamp(string name, int start, int count)
    {
        Name = name;
        Start = start;
        Count = count;
    }

    public bool Contains(int index) => index >= Start && index < Start + Count;
}

/// <summary>
/// Another set of colors for the same entries (red team, night). Exported as one row of the
/// palette image; a game swaps rows in its shader.
/// </summary>
public sealed class PaletteVariant
{
    public string Name { get; set; }
    public List<Rgba> Entries { get; }

    public PaletteVariant(string name, IEnumerable<Rgba> entries)
    {
        Name = name;
        Entries = entries.ToList();
    }
}

/// <summary>
/// Up to 256 colors, shared by every asset in a project. Pixels store an index into this list.
/// Index 0 is always transparent and is what the eraser writes. Entries are grouped into named
/// ramps; variants recolor the same entries.
/// </summary>
public sealed class Palette
{
    public const byte Transparent = 0;
    public const int MaxEntries = 256;

    public List<Rgba> Entries { get; } = new() { new Rgba(0, 0, 0, 0) };
    public List<PaletteRamp> Ramps { get; } = new();
    public List<PaletteVariant> Variants { get; } = new();

    /// <summary>Bumps on every change, for saving and redrawing.</summary>
    public int Version { get; private set; }

    public int Count => Entries.Count;

    public Rgba this[byte index] => index < Entries.Count ? Entries[index] : new Rgba(255, 0, 255);

    public void Set(int index, Rgba color)
    {
        if (index <= 0 || index >= Entries.Count) return;
        Entries[index] = color;
        Version++;
    }

    public byte Add(Rgba color)
    {
        if (Entries.Count >= MaxEntries) throw new InvalidOperationException("Palette is full (256 entries).");
        Entries.Add(color);
        foreach (var v in Variants) v.Entries.Add(color);
        Version++;
        return (byte)(Entries.Count - 1);
    }

    /// <summary>The colors of variant <paramref name="variant"/> (0 is the base palette, 1 the first variant).</summary>
    public IReadOnlyList<Rgba> ColorsFor(int variant) =>
        variant <= 0 || variant > Variants.Count ? Entries : Variants[variant - 1].Entries;

    /// <summary>Sets an entry in the base palette (variant 0) or a variant.</summary>
    public void Set(int variant, int index, Rgba color)
    {
        if (variant <= 0 || variant > Variants.Count) { Set(index, color); return; }
        var list = Variants[variant - 1].Entries;
        if (index <= 0 || index >= list.Count) return;
        list[index] = color;
        Version++;
    }

    /// <summary>A new variant, starting as a copy of the base colors.</summary>
    public PaletteVariant AddVariant(string name)
    {
        var v = new PaletteVariant(name, Entries);
        Variants.Add(v);
        Version++;
        return v;
    }

    /// <summary>The ramp an entry belongs to, if any.</summary>
    public PaletteRamp? RampOf(int index) => Ramps.FirstOrDefault(r => r.Contains(index));

    public void Touch() => Version++;

    public static Palette CreateDefault()
    {
        var p = new Palette();
        void Ramp(string name, params string[] hexes)
        {
            int start = p.Entries.Count;
            foreach (var hex in hexes) p.Entries.Add(Rgba.FromHex(hex));
            p.Ramps.Add(new PaletteRamp(name, start, hexes.Length));
        }
        Ramp("Ink", "1a1423", "3b3346", "6b6578", "a29ea8", "e4e1e6", "ffffff");
        Ramp("Warm", "5a1f2b", "a33a3a", "e0603a", "f2a65a", "fbe3a1");
        Ramp("Cool", "1d2b4f", "2f5f8a", "4aa3c8", "9adbe8");
        Ramp("Green", "1f4030", "3d7a3f", "7fb84e", "c8e07a");
        Ramp("Skin", "7a4a3a", "c48a69", "f0c8a8");
        Ramp("Accent", "8a4fa8", "e88ad6");
        return p;
    }
}
