namespace Capture.Core;

/// <summary>In-place effects for tightly packed, straight-alpha BGRA32 source pixels. Regions are clipped; outside pixels never change.</summary>
public static class PixelEffects
{
    public static void Solid(Span<byte> bgra, int width, int height, RectD region, ColorRgba color)
    {
        var r = Validate(bgra.Length, width, height, region);
        for (int y = r.Y; y < r.Y + r.H; y++) for (int x = r.X; x < r.X + r.W; x++)
        {
            int i = (y * width + x) * 4; bgra[i] = color.B; bgra[i + 1] = color.G; bgra[i + 2] = color.R; bgra[i + 3] = color.A;
        }
    }
    /// <param name="seed">Nonzero adds a small per-block brightness offset derived from it, so identical text no longer produces identical blocks that can be matched against guesses.</param>
    public static void Pixelate(Span<byte> bgra, int width, int height, RectD region, int blockSize = 12, int seed = 0)
    {
        if (blockSize < 1 || blockSize > 4096) throw new ArgumentOutOfRangeException(nameof(blockSize));
        var r = Validate(bgra.Length, width, height, region);
        for (int by = r.Y; by < r.Y + r.H; by += blockSize) for (int bx = r.X; bx < r.X + r.W; bx += blockSize)
        {
            int right = Math.Min(r.X + r.W, bx + blockSize), bottom = Math.Min(r.Y + r.H, by + blockSize);
            long b = 0, g = 0, red = 0, a = 0; int count = 0;
            for (int y = by; y < bottom; y++) for (int x = bx; x < right; x++)
            {
                int i = (y * width + x) * 4, alpha = bgra[i + 3];
                b += bgra[i] * alpha; g += bgra[i + 1] * alpha; red += bgra[i + 2] * alpha; a += alpha; count++;
            }
            var color = new ColorRgba((byte)(a == 0 ? 0 : red / a), (byte)(a == 0 ? 0 : g / a), (byte)(a == 0 ? 0 : b / a), (byte)(a / count));
            if (seed != 0 && a != 0) color = Jitter(color, seed, bx, by);
            Solid(bgra, width, height, new(bx, by, right - bx, bottom - by), color);
        }
    }
    private static ColorRgba Jitter(ColorRgba color, int seed, int x, int y)
    {
        unchecked
        {
            uint h = (uint)seed ^ (uint)x * 73856093u ^ (uint)y * 19349663u;
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15; h *= 0x2545f491u; h ^= h >> 13;
            // The same offset on every channel changes brightness only, so gray text stays gray.
            int offset = (int)(h & 31) - 15;
            byte Shift(byte value) => (byte)Math.Clamp(value + offset, 0, 255);
            return new(Shift(color.R), Shift(color.G), Shift(color.B), color.A);
        }
    }
    public static void Blur(Span<byte> bgra, int width, int height, RectD region, int radius = 10)
    {
        if (radius < 1 || radius > 256) throw new ArgumentOutOfRangeException(nameof(radius));
        var r = Validate(bgra.Length, width, height, region);
        if (r.W == 0 || r.H == 0) return;
        var local = new byte[checked(r.W * r.H * 4)];
        for (int y = 0; y < r.H; y++) bgra.Slice(((r.Y + y) * width + r.X) * 4, r.W * 4).CopyTo(local.AsSpan(y * r.W * 4));
        var temp = new byte[local.Length];
        BoxPass(local, temp, r.W, r.H, radius, true);
        BoxPass(temp, local, r.W, r.H, radius, false);
        for (int y = 0; y < r.H; y++) local.AsSpan(y * r.W * 4, r.W * 4).CopyTo(bgra.Slice(((r.Y + y) * width + r.X) * 4, r.W * 4));
    }
    private static void BoxPass(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width, length = horizontal ? width : height;
        for (int line = 0; line < lines; line++)
        {
            long b = 0, g = 0, red = 0, a = 0;
            int Index(int position) => (horizontal ? line * width + position : position * width + line) * 4;
            void Accumulate(int position, int sign)
            {
                int i = Index(position), alpha = source[i + 3];
                b += sign * source[i] * alpha; g += sign * source[i + 1] * alpha; red += sign * source[i + 2] * alpha; a += sign * alpha;
            }
            for (int p = 0; p <= Math.Min(radius, length - 1); p++) Accumulate(p, 1);
            for (int p = 0; p < length; p++)
            {
                int i = Index(p), count = Math.Min(length - 1, p + radius) - Math.Max(0, p - radius) + 1;
                target[i] = (byte)(a == 0 ? 0 : b / a); target[i + 1] = (byte)(a == 0 ? 0 : g / a); target[i + 2] = (byte)(a == 0 ? 0 : red / a); target[i + 3] = (byte)(a / count);
                if (p - radius >= 0) Accumulate(p - radius, -1);
                if (p + radius + 1 < length) Accumulate(p + radius + 1, 1);
            }
        }
    }
    private static (int X, int Y, int W, int H) Validate(int length, int width, int height, RectD region)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 4 > int.MaxValue || length != (long)width * height * 4) throw new ArgumentException("Expected tightly packed BGRA32 image.");
        if (!region.IsFinite) throw new ArgumentException("Region must be finite.", nameof(region));
        var r = region.Intersect(new(0, 0, width, height));
        if (r.IsEmpty) return (0, 0, 0, 0);
        int x = (int)Math.Floor(r.X), y = (int)Math.Floor(r.Y);
        return (x, y, (int)Math.Ceiling(r.Right) - x, (int)Math.Ceiling(r.Bottom) - y);
    }
}
