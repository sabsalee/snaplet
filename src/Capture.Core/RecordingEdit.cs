using System.Text;

namespace Capture.Core;

/// <summary>The kept part of a recording.</summary>
public readonly record struct TrimRange(TimeSpan Start, TimeSpan End)
{
    public static readonly TimeSpan Minimum = TimeSpan.FromMilliseconds(200);
    public TimeSpan Length => End - Start;

    /// <summary>Keeps the range inside the video and at least <see cref="Minimum"/> long, moving the handle that was not dragged as little as possible.</summary>
    public static TrimRange Clamp(TimeSpan start, TimeSpan end, TimeSpan duration, bool movedStart)
    {
        if (duration <= Minimum) return new(TimeSpan.Zero, duration);
        start = Max(TimeSpan.Zero, Min(start, duration)); end = Max(TimeSpan.Zero, Min(end, duration));
        if (end - start >= Minimum) return new(start, end);
        return movedStart ? new(Max(TimeSpan.Zero, end - Minimum), Max(end, Minimum)) : new(Min(start, duration - Minimum), Min(duration, start + Minimum));
    }

    public bool IsWhole(TimeSpan duration) => Start <= TimeSpan.FromMilliseconds(1) && End >= duration - TimeSpan.FromMilliseconds(1);
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>Frame times and size for turning a short recording into a GIF.</summary>
public static class GifPlan
{
    public const int MaxFrames = 600;

    /// <summary>Times of the frames to take, <paramref name="fps"/> per second, capped at <see cref="MaxFrames"/> by lowering the rate.</summary>
    public static IReadOnlyList<TimeSpan> Frames(TrimRange range, int fps, out int delayCentiseconds)
    {
        if (fps is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(fps));
        double seconds = Math.Max(range.Length.TotalSeconds, 0.01);
        double rate = Math.Min(fps, MaxFrames / seconds);
        // GIF delays are whole hundredths of a second; the frame spacing follows the rounded delay so playback speed is true.
        delayCentiseconds = Math.Max(2, (int)Math.Round(100 / rate));
        var step = TimeSpan.FromMilliseconds(delayCentiseconds * 10);
        var frames = new List<TimeSpan>();
        for (var t = range.Start; t < range.End && frames.Count < MaxFrames; t += step) frames.Add(t);
        return frames;
    }

    /// <summary>Output size with the long side at most <paramref name="maxLongSide"/>, never enlarged.</summary>
    public static (int Width, int Height) Size(int width, int height, int maxLongSide)
    {
        double scale = Math.Min(1, (double)maxLongSide / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}

/// <summary>
/// Sets frame delays and endless looping in a GIF. Some encoders write neither; this walks the blocks and
/// inserts a graphic control extension before each image that lacks one and a NETSCAPE2.0 loop block after the header.
/// </summary>
public static class GifTiming
{
    public static byte[] Apply(byte[] gif, int delayCentiseconds)
    {
        if (gif.Length < 13 || Encoding.ASCII.GetString(gif, 0, 3) != "GIF") throw new InvalidDataException("GIF 파일이 아닙니다.");
        var output = new List<byte>(gif.Length + 1024);
        output.AddRange("GIF89a"u8.ToArray());
        int position = 6;
        byte flags = gif[10];
        int header = 7 + ((flags & 0x80) != 0 ? 3 * (1 << ((flags & 7) + 1)) : 0);
        output.AddRange(gif.AsSpan(position, header).ToArray()); position += header;
        output.AddRange(new byte[] { 0x21, 0xFF, 11 }); output.AddRange("NETSCAPE2.0"u8.ToArray()); output.AddRange(new byte[] { 3, 1, 0, 0, 0 });
        bool controlled = false;
        while (position < gif.Length)
        {
            byte block = gif[position];
            if (block == 0x3B) { output.Add(0x3B); break; }
            if (block == 0x21)
            {
                byte label = gif[position + 1];
                int end = SkipSubBlocks(gif, position + 2);
                if (label == 0xFF && end - position > 13 && Encoding.ASCII.GetString(gif, position + 3, 8) == "NETSCAPE") { position = end; continue; }
                if (label == 0xF9)
                {
                    // Keep the encoder's transparency and disposal, replace the delay.
                    var control = gif.AsSpan(position, end - position).ToArray();
                    control[4] = (byte)delayCentiseconds; control[5] = (byte)(delayCentiseconds >> 8);
                    output.AddRange(control); controlled = true; position = end; continue;
                }
                output.AddRange(gif.AsSpan(position, end - position).ToArray()); position = end; continue;
            }
            if (block == 0x2C)
            {
                if (!controlled) output.AddRange(new byte[] { 0x21, 0xF9, 4, 0, (byte)delayCentiseconds, (byte)(delayCentiseconds >> 8), 0, 0 });
                controlled = false;
                byte local = gif[position + 9];
                int start = position;
                position += 10 + ((local & 0x80) != 0 ? 3 * (1 << ((local & 7) + 1)) : 0) + 1;
                position = SkipSubBlocks(gif, position);
                output.AddRange(gif.AsSpan(start, position - start).ToArray());
                continue;
            }
            throw new InvalidDataException("GIF 블록이 손상되었습니다.");
        }
        if (output[^1] != 0x3B) output.Add(0x3B);
        return output.ToArray();
    }

    /// <summary>Counts images, reads the first delay and whether it loops.</summary>
    public static (int Frames, int DelayCentiseconds, bool Loops) Describe(byte[] gif)
    {
        int position = 6; byte flags = gif[10];
        position += 7 + ((flags & 0x80) != 0 ? 3 * (1 << ((flags & 7) + 1)) : 0);
        int frames = 0, delay = 0; bool loops = false;
        while (position < gif.Length && gif[position] != 0x3B)
        {
            if (gif[position] == 0x21)
            {
                byte label = gif[position + 1];
                if (label == 0xFF && Encoding.ASCII.GetString(gif, position + 3, 8) == "NETSCAPE") loops = true;
                if (label == 0xF9 && delay == 0) delay = gif[position + 4] | gif[position + 5] << 8;
                position = SkipSubBlocks(gif, position + 2);
            }
            else if (gif[position] == 0x2C)
            {
                frames++; byte local = gif[position + 9];
                position = SkipSubBlocks(gif, position + 10 + ((local & 0x80) != 0 ? 3 * (1 << ((local & 7) + 1)) : 0) + 1);
            }
            else throw new InvalidDataException("GIF 블록이 손상되었습니다.");
        }
        return (frames, delay, loops);
    }

    private static int SkipSubBlocks(byte[] gif, int position)
    {
        while (position < gif.Length)
        {
            int size = gif[position];
            position += 1 + size;
            if (size == 0) return position;
        }
        throw new InvalidDataException("GIF가 중간에 끝났습니다.");
    }
}
