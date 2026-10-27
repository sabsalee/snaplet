namespace Capture.Core;

/// <summary>Integer rectangle in source-image pixels. Selection and measurement never use fractional pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    public static PixelRect FromCorners(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    public PixelRect Intersect(PixelRect other)
    {
        int x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
        return right <= x || bottom <= y ? default : new(x, y, right - x, bottom - y);
    }
}

/// <summary>Eight resize handles in clockwise order from the top-left corner.</summary>
public enum SelectionHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>Pure selection arithmetic shared by the capture overlay and editor crop tools.</summary>
public static class SelectionMath
{
    /// <summary>Rectangle from a drag anchor to the pointer. With <paramref name="square"/> the shorter side follows the longer one, clipped to bounds.</summary>
    public static PixelRect FromDrag(int anchorX, int anchorY, int x, int y, PixelRect bounds, bool square = false)
    {
        x = Math.Clamp(x, bounds.X, bounds.Right);
        y = Math.Clamp(y, bounds.Y, bounds.Bottom);
        if (square)
        {
            int dx = x - anchorX, dy = y - anchorY;
            int side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            int maxX = dx >= 0 ? bounds.Right - anchorX : anchorX - bounds.X;
            int maxY = dy >= 0 ? bounds.Bottom - anchorY : anchorY - bounds.Y;
            side = Math.Min(side, Math.Min(maxX, maxY));
            x = anchorX + (dx >= 0 ? side : -side);
            y = anchorY + (dy >= 0 ? side : -side);
        }
        return PixelRect.FromCorners(anchorX, anchorY, x, y);
    }

    /// <summary>Moves without changing size; the rectangle stays fully inside bounds.</summary>
    public static PixelRect Move(PixelRect rect, int dx, int dy, PixelRect bounds)
    {
        int width = Math.Min(rect.Width, bounds.Width), height = Math.Min(rect.Height, bounds.Height);
        int x = Math.Clamp(rect.X + dx, bounds.X, bounds.Right - width);
        int y = Math.Clamp(rect.Y + dy, bounds.Y, bounds.Bottom - height);
        return new(x, y, width, height);
    }

    /// <summary>Grows (or shrinks with a negative amount) every side, keeping at least one pixel.</summary>
    public static PixelRect Grow(PixelRect rect, int amount, PixelRect bounds)
    {
        int x = rect.X - amount, y = rect.Y - amount, right = rect.Right + amount, bottom = rect.Bottom + amount;
        if (right - x < 1) { x = rect.X + rect.Width / 2; right = x + 1; }
        if (bottom - y < 1) { y = rect.Y + rect.Height / 2; bottom = y + 1; }
        var grown = new PixelRect(x, y, right - x, bottom - y).Intersect(bounds);
        return grown.IsEmpty ? rect : grown;
    }

    /// <summary>Changes the right/bottom edges by keyboard steps, keeping at least one pixel.</summary>
    public static PixelRect ResizeBy(PixelRect rect, int dw, int dh, PixelRect bounds)
    {
        int width = Math.Clamp(rect.Width + dw, 1, bounds.Right - rect.X);
        int height = Math.Clamp(rect.Height + dh, 1, bounds.Bottom - rect.Y);
        return rect with { Width = width, Height = height };
    }

    /// <summary>Drags one handle to a pointer position. Crossing the opposite edge flips instead of producing negative sizes.</summary>
    public static PixelRect Resize(PixelRect rect, SelectionHandle handle, int x, int y, PixelRect bounds)
    {
        x = Math.Clamp(x, bounds.X, bounds.Right);
        y = Math.Clamp(y, bounds.Y, bounds.Bottom);
        int left = rect.X, top = rect.Y, right = rect.Right, bottom = rect.Bottom;
        switch (handle)
        {
            case SelectionHandle.TopLeft: left = x; top = y; break;
            case SelectionHandle.Top: top = y; break;
            case SelectionHandle.TopRight: right = x; top = y; break;
            case SelectionHandle.Right: right = x; break;
            case SelectionHandle.BottomRight: right = x; bottom = y; break;
            case SelectionHandle.Bottom: bottom = y; break;
            case SelectionHandle.BottomLeft: left = x; bottom = y; break;
            case SelectionHandle.Left: left = x; break;
        }
        var result = PixelRect.FromCorners(left, top, right, bottom);
        return result with { Width = Math.Max(1, result.Width), Height = Math.Max(1, result.Height) };
    }
}

/// <summary>Colour sampling, run-length measurement and uniform-border trimming over top-down BGRA buffers.</summary>
public static class PixelProbe
{
    public static ColorRgba At(ReadOnlySpan<byte> bgra, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        if (x < 0 || y < 0 || x >= width || i < 0 || i + 3 >= bgra.Length) throw new ArgumentOutOfRangeException(nameof(x));
        return new(bgra[i + 2], bgra[i + 1], bgra[i], bgra[i + 3]);
    }

    public static ColorRgba Average(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length < 4 || bgra.Length % 4 != 0) throw new ArgumentException("BGRA buffer required.", nameof(bgra));
        long r = 0, g = 0, b = 0, a = 0; int count = bgra.Length / 4;
        for (int i = 0; i < bgra.Length; i += 4) { b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; a += bgra[i + 3]; }
        return new((byte)((r + count / 2) / count), (byte)((g + count / 2) / count), (byte)((b + count / 2) / count), (byte)((a + count / 2) / count));
    }

    public static string Hex(ColorRgba c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    public static string Rgb(ColorRgba c) => $"rgb({c.R}, {c.G}, {c.B})";

    /// <summary>Largest per-channel difference. Alpha is ignored because captured desktops are opaque.</summary>
    public static int Distance(ColorRgba a, ColorRgba b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>
    /// Counts neighbouring pixels on a one-pixel line whose colour stays within tolerance of the pixel at <paramref name="index"/>.
    /// Returns how many such pixels lie before and after it; the measured span is Before + 1 + After.
    /// </summary>
    public static (int Before, int After) Run(ReadOnlySpan<byte> lineBgra, int index, int tolerance)
    {
        int length = lineBgra.Length / 4;
        if (index < 0 || index >= length) throw new ArgumentOutOfRangeException(nameof(index));
        var origin = At(lineBgra, length, index, 0);
        int before = 0, after = 0;
        while (index - before - 1 >= 0 && Distance(At(lineBgra, length, index - before - 1, 0), origin) <= tolerance) before++;
        while (index + after + 1 < length && Distance(At(lineBgra, length, index + after + 1, 0), origin) <= tolerance) after++;
        return (before, after);
    }

    /// <summary>
    /// Shrinks <paramref name="rect"/> past outer rows/columns that share the margin colour, like an automatic "fit to content".
    /// Each side takes its outermost line's first pixel as the background, so differing margins trim independently.
    /// Stacked bands of different colours (a title bar above a page) trim one band per call; calling again continues.
    /// Returns the original rectangle when it is entirely background.
    /// </summary>
    public static PixelRect TrimUniformBorder(ReadOnlySpan<byte> bgra, int width, int height, PixelRect rect, int tolerance)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 4 > bgra.Length) throw new ArgumentException("BGRA buffer is smaller than its dimensions.", nameof(bgra));
        rect = rect.Intersect(new(0, 0, width, height));
        if (rect.IsEmpty) return rect;
        int left = rect.X, top = rect.Y, right = rect.Right - 1, bottom = rect.Bottom - 1;
        bool Row(ReadOnlySpan<byte> px, int y, int x0, int x1, ColorRgba background)
        {
            for (int x = x0; x <= x1; x++) if (Distance(At(px, width, x, y), background) > tolerance) return false;
            return true;
        }
        bool Column(ReadOnlySpan<byte> px, int x, int y0, int y1, ColorRgba background)
        {
            for (int y = y0; y <= y1; y++) if (Distance(At(px, width, x, y), background) > tolerance) return false;
            return true;
        }
        var topBackground = At(bgra, width, left, top);
        while (top <= bottom && Row(bgra, top, left, right, topBackground)) top++;
        if (top > bottom) return rect;
        var bottomBackground = At(bgra, width, left, bottom);
        while (bottom > top && Row(bgra, bottom, left, right, bottomBackground)) bottom--;
        var leftBackground = At(bgra, width, left, top);
        while (left < right && Column(bgra, left, top, bottom, leftBackground)) left++;
        var rightBackground = At(bgra, width, right, top);
        while (right > left && Column(bgra, right, top, bottom, rightBackground)) right--;
        return new(left, top, right - left + 1, bottom - top + 1);
    }
}
