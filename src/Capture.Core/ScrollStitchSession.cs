namespace Capture.Core;

/// <summary>
/// Accumulates equally sized downward-scrolled BGRA32 tiles. Only confident overlaps are appended;
/// rejected frames leave both the prior accepted tile and the assembled image intact.
/// </summary>
public sealed class ScrollStitchSession
{
    private byte[] output;
    private byte[] previous;
    private readonly int maxOutputBytes;
    private readonly int maxHeight;
    private readonly int minimumOverlap;
    public int Width { get; }
    public int FrameHeight { get; }
    public int OutputHeight { get; private set; }
    public ScrollMatch LastMatch { get; private set; }
    public string? LastRejection { get; private set; }
    /// <summary>True when the last frame was identical to the previous one: the page did not move (usually its end).</summary>
    public bool ReachedEnd { get; private set; }
    /// <summary>Rows at the top/bottom that stayed fixed in the last accepted pair (sticky header/footer).</summary>
    public (int Top, int Bottom) LastStaticBands { get; private set; }

    public ScrollStitchSession(int width, int height, ReadOnlySpan<byte> initialFrame,
        int maxOutputBytes = 256 * 1024 * 1024, int maxHeight = 32768, int minimumOverlap = 32)
    {
        long size = (long)width * height * 4;
        if (width <= 0 || height <= 0 || size > int.MaxValue || initialFrame.Length != size)
            throw new ArgumentException("Expected a tightly packed BGRA32 initial frame.", nameof(initialFrame));
        if (maxOutputBytes < size) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes), "Initial image exceeds the output byte limit.");
        if (maxHeight < height) throw new ArgumentOutOfRangeException(nameof(maxHeight), "Initial image exceeds the output height limit.");
        if (minimumOverlap < 4 || minimumOverlap >= height) throw new ArgumentOutOfRangeException(nameof(minimumOverlap));
        Width = width; FrameHeight = height; OutputHeight = height;
        this.maxOutputBytes = maxOutputBytes; this.maxHeight = maxHeight; this.minimumOverlap = minimumOverlap;
        output = initialFrame.ToArray(); previous = initialFrame.ToArray();
    }

    public bool Append(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != previous.Length) throw new ArgumentException("Scroll frames must keep their original dimensions.", nameof(frame));
        var (top, bottom) = StaticBands(previous, frame, Width, FrameHeight);
        if (top == FrameHeight)
        {
            ReachedEnd = true; LastMatch = new(false, 0, 0, 0);
            LastRejection = "The view did not move; the end of the content may have been reached.";
            return false;
        }
        ReachedEnd = false;
        // Sticky headers/footers stay put while content scrolls: match only the moving middle and keep the footer once, at the end.
        if (FrameHeight - top - bottom < minimumOverlap * 2) { top = 0; bottom = 0; }
        int middle = FrameHeight - top - bottom, rowBytes = Width * 4;
        LastMatch = ScrollOverlapMatcher.Match(previous.AsSpan(top * rowBytes, middle * rowBytes), frame.Slice(top * rowBytes, middle * rowBytes), Width, middle, minimumOverlap);
        if (!LastMatch.Accepted)
        {
            LastRejection = "No confident downward overlap; scroll less or choose an area without fixed headers or animation.";
            return false;
        }
        long nextHeight = (long)OutputHeight + LastMatch.Offset;
        long nextBytes = nextHeight * Width * 4;
        if (nextHeight > maxHeight || nextBytes > maxOutputBytes || nextBytes > int.MaxValue)
        {
            LastRejection = "The stitched image has reached its size limit.";
            return false;
        }

        // Allocate and fill both buffers before committing any image state.
        var next = new byte[(int)nextBytes];
        int keep = (OutputHeight - bottom) * rowBytes, added = LastMatch.Offset * rowBytes;
        output.AsSpan(0, keep).CopyTo(next);
        frame.Slice((FrameHeight - bottom - LastMatch.Offset) * rowBytes, added).CopyTo(next.AsSpan(keep));
        output.AsSpan(keep).CopyTo(next.AsSpan(keep + added));
        var nextPrevious = frame.ToArray();
        output = next; previous = nextPrevious; OutputHeight = (int)nextHeight; LastStaticBands = (top, bottom);
        LastRejection = null;
        return true;
    }

    /// <summary>
    /// Counts rows identical (within a small mean difference) at the same position in both frames, from the top and from the bottom.
    /// Returns (height, 0) when the frames are identical.
    /// </summary>
    public static (int Top, int Bottom) StaticBands(ReadOnlySpan<byte> previousBgra, ReadOnlySpan<byte> currentBgra, int width, int height, double tolerance = 2.0)
    {
        if (width <= 0 || height <= 0 || previousBgra.Length != (long)width * height * 4 || currentBgra.Length != previousBgra.Length) throw new ArgumentException("Expected equally sized BGRA32 frames.");
        int step = Math.Max(1, width / 256);
        double RowDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int y)
        {
            long sum = 0; int count = 0;
            for (int x = 0; x < width; x += step)
            {
                int i = (y * width + x) * 4;
                sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]); count += 3;
            }
            return (double)sum / count;
        }
        int top = 0;
        while (top < height && RowDifference(previousBgra, currentBgra, top) <= tolerance) top++;
        if (top == height) return (height, 0);
        int bottom = 0;
        while (bottom < height - top && RowDifference(previousBgra, currentBgra, height - 1 - bottom) <= tolerance) bottom++;
        return (top, bottom);
    }

    /// <summary>Returns an owned snapshot; callers cannot mutate the active session.</summary>
    public byte[] ToArray() => output.ToArray();
}
