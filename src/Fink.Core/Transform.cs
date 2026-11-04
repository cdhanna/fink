using System.Numerics;

namespace Fink.Core;

/// <summary>A 2D affine map: p' = (A·x + B·y + Tx, C·x + D·y + Ty).</summary>
public readonly record struct Affine(float A, float B, float C, float D, float Tx, float Ty)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public Vector2 Apply(Vector2 p) => new(A * p.X + B * p.Y + Tx, C * p.X + D * p.Y + Ty);

    public float Determinant => A * D - B * C;

    public Affine Inverse()
    {
        float det = Determinant;
        if (MathF.Abs(det) < 1e-8f) return Identity;
        float ia = D / det, ib = -B / det, ic = -C / det, id = A / det;
        return new Affine(ia, ib, ic, id, -(ia * Tx + ib * Ty), -(ic * Tx + id * Ty));
    }

    /// <summary>this ∘ other: apply <paramref name="other"/> first.</summary>
    public Affine Then(Affine other) => other.Compose(this);

    private Affine Compose(Affine first) => new(
        A * first.A + B * first.C, A * first.B + B * first.D,
        C * first.A + D * first.C, C * first.B + D * first.D,
        A * first.Tx + B * first.Ty + Tx, C * first.Tx + D * first.Ty + Ty);

    public static Affine Translate(float x, float y) => new(1, 0, 0, 1, x, y);
    public static Affine Scale(float sx, float sy) => new(sx, 0, 0, sy, 0, 0);
    public static Affine Rotate(float degrees)
    {
        float r = degrees * MathF.PI / 180f, c = MathF.Cos(r), s = MathF.Sin(r);
        // snap the exact quarter turns so 90° rotations stay pixel-exact
        if (MathF.Abs(c) < 1e-6f) c = 0;
        if (MathF.Abs(s) < 1e-6f) s = 0;
        return new Affine(c, -s, s, c, 0, 0);
    }
    public static Affine SkewX(float degrees) => new(1, MathF.Tan(degrees * MathF.PI / 180f), 0, 1, 0, 0);
}

/// <summary>
/// A transform described the way the UI edits it: where the center goes, scale (negative flips),
/// rotation and horizontal skew. Maps source-local pixels (0..w, 0..h) to document pixels.
/// </summary>
public record struct TransformParams(Vector2 Center, float ScaleX = 1, float ScaleY = 1, float Angle = 0, float Skew = 0)
{
    public Affine ToAffine(int srcW, int srcH) =>
        Affine.Translate(-srcW / 2f, -srcH / 2f)
            .Then(Affine.Scale(ScaleX, ScaleY))
            .Then(Affine.SkewX(Skew))
            .Then(Affine.Rotate(Angle))
            .Then(Affine.Translate(Center.X, Center.Y));

    /// <summary>The linear part without scale (rotation · skew), for handle math.</summary>
    public Affine Frame() => Affine.SkewX(Skew).Then(Affine.Rotate(Angle));

    public bool IsAxisAligned =>
        MathF.Abs(Skew) < 1e-4f && MathF.Abs(MathF.IEEERemainder(Angle, 90)) < 1e-4f
        && MathF.Abs(ScaleX - MathF.Round(ScaleX)) < 1e-4f && MathF.Abs(ScaleY - MathF.Round(ScaleY)) < 1e-4f;
}
