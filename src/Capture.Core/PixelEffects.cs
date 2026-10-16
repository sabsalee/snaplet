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
    public static void Pixelate(Span<byte> bgra, int width, int height, RectD region, int blockSize = 12)
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
            Solid(bgra, width, height, new(bx, by, right - bx, bottom - by), color);
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
    /// <summary>
    /// Removes a rectangular object by estimating a smooth background from the immediately
    /// surrounding pixels. Fits an alpha-aware color plane with bounded, outlier-resistant
    /// sampling. Intended for screenshot text/UI on uniform or gradient backgrounds, not
    /// photographic texture reconstruction. If no outside pixels exist, leaves the image alone.
    /// Work is O(region area + perimeter), with at most 4096 background samples.
    /// </summary>
    public static void Erase(Span<byte> bgra, int width, int height, RectD region)
    {
        var r = Validate(bgra.Length, width, height, region);
        if (r.W == 0 || r.H == 0) return;
        long perimeter = (r.Y > 0 ? r.W : 0L) + (r.Y + r.H < height ? r.W : 0L)
            + (r.X > 0 ? r.H : 0L) + (r.X + r.W < width ? r.H : 0L);
        if (perimeter == 0) return; // There is no evidence for a replacement background.
        int count = (int)Math.Min(4096, perimeter);
        var samples = new double[count, 6]; // normalized x/y, premultiplied B/G/R, alpha
        double scale = Math.Max(r.W, r.H), centerX = r.X + (r.W - 1) / 2.0, centerY = r.Y + (r.H - 1) / 2.0;
        for (int sample = 0; sample < count; sample++)
        {
            long offset = sample * perimeter / count;
            int x, y;
            if (r.Y > 0 && offset < r.W) { x = r.X + (int)offset; y = r.Y - 1; }
            else
            {
                if (r.Y > 0) offset -= r.W;
                if (r.Y + r.H < height && offset < r.W) { x = r.X + (int)offset; y = r.Y + r.H; }
                else
                {
                    if (r.Y + r.H < height) offset -= r.W;
                    if (r.X > 0 && offset < r.H) { x = r.X - 1; y = r.Y + (int)offset; }
                    else { if (r.X > 0) offset -= r.H; x = r.X + r.W; y = r.Y + (int)offset; }
                }
            }
            int i = (y * width + x) * 4;
            double alpha = bgra[i + 3];
            samples[sample, 0] = (x - centerX) / scale; samples[sample, 1] = (y - centerY) / scale;
            for (int channel = 0; channel < 3; channel++) samples[sample, channel + 2] = bgra[i + channel] * alpha / 255;
            samples[sample, 5] = alpha;
        }
        var accepted = Enumerable.Repeat(true, count).ToArray();
        var plane = new double[4, 3];
        for (int pass = 0; pass < 3; pass++)
        {
            FitBackgroundPlane(samples, accepted, plane);
            if (pass == 2) break;
            var errors = new double[count];
            for (int i = 0; i < count; i++)
                for (int channel = 0; channel < 4; channel++)
                    errors[i] = Math.Max(errors[i], Math.Abs(samples[i, channel + 2] - (plane[channel, 0]
                        + plane[channel, 1] * samples[i, 0] + plane[channel, 2] * samples[i, 1])));
            var ordered = errors.ToArray(); Array.Sort(ordered);
            double threshold = Math.Max(3, ordered[count / 2] * 2.5);
            for (int i = 0; i < count; i++) accepted[i] = errors[i] <= threshold;
        }
        for (int y = r.Y; y < r.Y + r.H; y++) for (int x = r.X; x < r.X + r.W; x++)
        {
            double nx = (x - centerX) / scale, ny = (y - centerY) / scale;
            double alpha = Math.Clamp(plane[3, 0] + plane[3, 1] * nx + plane[3, 2] * ny, 0, 255);
            int i = (y * width + x) * 4;
            for (int channel = 0; channel < 3; channel++)
            {
                double premultiplied = Math.Clamp(plane[channel, 0] + plane[channel, 1] * nx + plane[channel, 2] * ny, 0, alpha);
                bgra[i + channel] = alpha < .5 ? (byte)0 : (byte)Math.Clamp(Math.Round(premultiplied * 255 / alpha), 0, 255);
            }
            bgra[i + 3] = (byte)Math.Round(alpha);
        }
    }
    private static void FitBackgroundPlane(double[,] samples, bool[] accepted, double[,] plane)
    {
        // A tiny ridge on the two slopes supplies the constant-background fallback when
        // only a single row/column surrounds an edge selection. The intercept is unpenalized.
        var matrix = new double[3, 7];
        matrix[1, 1] = matrix[2, 2] = 1e-8;
        for (int i = 0; i < accepted.Length; i++) if (accepted[i])
        {
            double x = samples[i, 0], y = samples[i, 1];
            double[] basis = [1, x, y];
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++) matrix[row, col] += basis[row] * basis[col];
                for (int channel = 0; channel < 4; channel++) matrix[row, channel + 3] += basis[row] * samples[i, channel + 2];
            }
        }
        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 3; row++) if (Math.Abs(matrix[row, col]) > Math.Abs(matrix[pivot, col])) pivot = row;
            if (Math.Abs(matrix[pivot, col]) < 1e-12) return;
            for (int k = col; k < 7; k++) (matrix[col, k], matrix[pivot, k]) = (matrix[pivot, k], matrix[col, k]);
            double divisor = matrix[col, col];
            for (int k = col; k < 7; k++) matrix[col, k] /= divisor;
            for (int row = 0; row < 3; row++) if (row != col)
            {
                double factor = matrix[row, col];
                for (int k = col; k < 7; k++) matrix[row, k] -= factor * matrix[col, k];
            }
        }
        for (int channel = 0; channel < 4; channel++) for (int term = 0; term < 3; term++) plane[channel, term] = matrix[term, channel + 3];
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
