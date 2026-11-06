namespace Capture.Core;

/// <summary>Keeps a crop rectangle at a fixed width-to-height ratio while it stays inside its bounds.</summary>
public static class AspectMath
{
    /// <summary>The rectangle from a fixed corner towards the pointer, grown to the ratio and shrunk to fit the bounds.</summary>
    public static RectD FromDrag(PointD anchor, PointD pointer, double ratio, RectD bounds)
    {
        if (!double.IsFinite(ratio) || ratio <= 0) throw new ArgumentOutOfRangeException(nameof(ratio));
        double dx = pointer.X - anchor.X, dy = pointer.Y - anchor.Y;
        double width = Math.Max(Math.Abs(dx), Math.Abs(dy) * ratio), height = width / ratio;
        double roomX = dx >= 0 ? bounds.Right - anchor.X : anchor.X - bounds.X;
        double roomY = dy >= 0 ? bounds.Bottom - anchor.Y : anchor.Y - bounds.Y;
        if (width > roomX) { width = Math.Max(0, roomX); height = width / ratio; }
        if (height > roomY) { height = Math.Max(0, roomY); width = height * ratio; }
        return new(dx >= 0 ? anchor.X : anchor.X - width, dy >= 0 ? anchor.Y : anchor.Y - height, width, height);
    }

    /// <summary>Applies the ratio to a resized rectangle, keeping fixed whichever edges the resize left in place.</summary>
    public static RectD Resize(RectD before, RectD resized, double ratio, RectD bounds)
    {
        bool movedLeft = resized.X != before.X, movedTop = resized.Y != before.Y;
        bool heightOnly = resized.Width == before.Width && resized.Height != before.Height;
        double width = heightOnly ? resized.Height * ratio : resized.Width;
        var anchor = new PointD(movedLeft ? resized.Right : resized.X, movedTop ? resized.Bottom : resized.Y);
        var pointer = new PointD(movedLeft ? anchor.X - width : anchor.X + width, movedTop ? anchor.Y - width / ratio : anchor.Y + width / ratio);
        return FromDrag(anchor, pointer, ratio, bounds);
    }
}
