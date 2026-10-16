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
        LastMatch = ScrollOverlapMatcher.Match(previous, frame, Width, FrameHeight, minimumOverlap);
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
        output.CopyTo(next, 0);
        frame.Slice((FrameHeight - LastMatch.Offset) * Width * 4).CopyTo(next.AsSpan(output.Length));
        var nextPrevious = frame.ToArray();
        output = next; previous = nextPrevious; OutputHeight = (int)nextHeight;
        LastRejection = null;
        return true;
    }

    /// <summary>Returns an owned snapshot; callers cannot mutate the active session.</summary>
    public byte[] ToArray() => output.ToArray();
}
