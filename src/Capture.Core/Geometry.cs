namespace Capture.Core;

public readonly record struct PointD(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) && double.IsFinite(Right) && double.IsFinite(Bottom);
    public bool IsEmpty => !IsFinite || Width <= 0 || Height <= 0;
    public static RectD FromPoints(PointD a, PointD b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    public RectD Intersect(RectD other)
    {
        if (IsEmpty || other.IsEmpty) return default;
        double x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(Right, other.Right) - x), Math.Max(0, Math.Min(Bottom, other.Bottom) - y));
    }
    public bool Contains(PointD p) => !IsEmpty && p.IsFinite && p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
}

public readonly record struct ColorRgba(byte R, byte G, byte B, byte A = 255);

/// <summary>Maps source-image pixels to view DIPs; DPI conversion belongs at the OS boundary.</summary>
public readonly record struct ViewTransform(double Scale, PointD Offset)
{
    public PointD ToView(PointD source) { Validate(); return new(source.X * Scale + Offset.X, source.Y * Scale + Offset.Y); }
    public PointD ToSource(PointD view) { Validate(); return new((view.X - Offset.X) / Scale, (view.Y - Offset.Y) / Scale); }
    private void Validate() { if (!double.IsFinite(Scale) || Scale <= 0 || !Offset.IsFinite) throw new ArgumentOutOfRangeException(nameof(Scale)); }
}
