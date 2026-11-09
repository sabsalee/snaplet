using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Capture.Core;

public enum RecordingState { Idle, Countdown, Recording, Paused, Stopped }

/// <summary>
/// Recording state and media time. Wall-clock instants are passed in so the rules can be tested without a timer.
/// Media time excludes countdown and paused intervals, so the encoded video has no gaps.
/// </summary>
public sealed class RecordingSession
{
    private TimeSpan started, pausedAt, pausedTotal, stoppedMedia;
    public RecordingState State { get; private set; } = RecordingState.Idle;
    public int Pauses { get; private set; }

    public bool BeginCountdown() => Move(RecordingState.Idle, RecordingState.Countdown);
    public bool Start(TimeSpan now)
    {
        if (State is not (RecordingState.Idle or RecordingState.Countdown)) return false;
        started = now; pausedTotal = TimeSpan.Zero; State = RecordingState.Recording; return true;
    }
    public bool Pause(TimeSpan now)
    {
        if (State != RecordingState.Recording || now < started) return false;
        pausedAt = now; Pauses++; State = RecordingState.Paused; return true;
    }
    public bool Resume(TimeSpan now)
    {
        if (State != RecordingState.Paused || now < pausedAt) return false;
        pausedTotal += now - pausedAt; State = RecordingState.Recording; return true;
    }
    public bool Stop(TimeSpan now)
    {
        if (State is not (RecordingState.Recording or RecordingState.Paused)) return false;
        stoppedMedia = MediaTime(now); State = RecordingState.Stopped; return true;
    }
    /// <summary>Restarting discards the take and returns to idle with the same target.</summary>
    public bool Restart()
    {
        if (State is RecordingState.Idle) return false;
        State = RecordingState.Idle; Pauses = 0; pausedTotal = stoppedMedia = TimeSpan.Zero; return true;
    }

    /// <summary>Position in the encoded video for a wall-clock instant. Frozen while paused.</summary>
    public TimeSpan MediaTime(TimeSpan now) => State switch
    {
        RecordingState.Recording => Max(now - started - pausedTotal),
        RecordingState.Paused => Max(pausedAt - started - pausedTotal),
        RecordingState.Stopped => stoppedMedia,
        _ => TimeSpan.Zero,
    };

    private bool Move(RecordingState from, RecordingState to) { if (State != from) return false; State = to; return true; }
    private static TimeSpan Max(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}

public static class RecordingOutput
{
    /// <summary>H.264 needs even sides. Scales down so the long side fits <paramref name="maxLongSide"/>, never up.</summary>
    public static (int Width, int Height) Size(int width, int height, int maxLongSide = 0)
    {
        if (width < 2 || height < 2) throw new ArgumentOutOfRangeException(nameof(width), "녹화 범위가 너무 작습니다.");
        double scale = maxLongSide > 0 && Math.Max(width, height) > maxLongSide ? (double)maxLongSide / Math.Max(width, height) : 1;
        int Even(double value) => Math.Max(2, (int)Math.Floor(value) & ~1);
        return (Even(width * scale), Even(height * scale));
    }

    /// <summary>
    /// Frame slot of a media time. A capture source only delivers frames when the screen changes,
    /// so the writer repeats the last frame for empty slots and skips frames that land in a slot already written.
    /// </summary>
    public static long Slot(TimeSpan media, int fps) =>
        fps is < 1 or > 240 ? throw new ArgumentOutOfRangeException(nameof(fps)) : media.Ticks * fps / TimeSpan.TicksPerSecond;
    /// <summary>Start of a slot, rounded up so it always maps back to the same slot.</summary>
    public static TimeSpan SlotTime(long slot, int fps) => TimeSpan.FromTicks((slot * TimeSpan.TicksPerSecond + fps - 1) / fps);
}

public enum InputEventKind { Move, Down, Up, Key }

/// <summary>Cursor, click and (opt-in) shortcut events in media time, kept beside a recording for later effects.</summary>
public readonly record struct InputEvent(TimeSpan Time, InputEventKind Kind, int X, int Y, int Button = 0, string? Key = null);

public static class InputTrack
{
    public const string Header = "snaplet-input 1";

    public static void Write(TextWriter writer, IEnumerable<InputEvent> events)
    {
        writer.WriteLine(Header);
        foreach (var e in events)
        {
            if (e.Key is not null && (e.Kind != InputEventKind.Key || e.Key.Any(c => c is ',' or '\n' or '\r')))
                throw new ArgumentException("키 이름에 구분 문자를 쓸 수 없습니다.", nameof(events));
            writer.WriteLine(string.Join(',', ((long)e.Time.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                e.Kind switch { InputEventKind.Move => "m", InputEventKind.Down => "d", InputEventKind.Up => "u", _ => "k" },
                e.X.ToString(CultureInfo.InvariantCulture), e.Y.ToString(CultureInfo.InvariantCulture), e.Button.ToString(CultureInfo.InvariantCulture), e.Key ?? ""));
        }
    }

    public static List<InputEvent> Read(TextReader reader)
    {
        if (reader.ReadLine() != Header) throw new InvalidDataException("입력 기록 형식이 아닙니다.");
        var events = new List<InputEvent>();
        for (string? line; (line = reader.ReadLine()) is not null;)
        {
            if (line.Length == 0) continue;
            var parts = line.Split(',');
            if (parts.Length != 6) throw new InvalidDataException("입력 기록 줄이 손상되었습니다.");
            var kind = parts[1] switch { "m" => InputEventKind.Move, "d" => InputEventKind.Down, "u" => InputEventKind.Up, "k" => InputEventKind.Key, _ => throw new InvalidDataException("알 수 없는 입력 종류입니다.") };
            int Number(string text) => int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            events.Add(new(TimeSpan.FromMilliseconds(long.Parse(parts[0], CultureInfo.InvariantCulture)), kind, Number(parts[2]), Number(parts[3]), Number(parts[4]), parts[5].Length == 0 ? null : parts[5]));
        }
        return events;
    }

    /// <summary>Default keystroke filter: only combinations with Ctrl, Alt or Win, and named function keys. Plain typing is never kept.</summary>
    public static bool IsShortcut(string key, bool ctrl, bool alt, bool win) =>
        ctrl || alt || win || key is "Esc" or "Enter" or "Tab" or "Backspace" or "Delete" or "Space" || key.Length > 1 && key[0] == 'F' && key[1..].All(char.IsAsciiDigit);
}

public sealed record Mp4Track(string Handler, int Width, int Height, TimeSpan Duration);
public sealed record Mp4Info(TimeSpan Duration, IReadOnlyList<Mp4Track> Tracks);

/// <summary>Reads duration and tracks from an MP4 header. Returns null when the index (moov) is missing, as in an interrupted recording.</summary>
public static class Mp4Reader
{
    public static Mp4Info? Read(Stream stream)
    {
        foreach (var (type, start, length) in Boxes(stream, 0, stream.Length))
            if (type == "moov") return Movie(stream, start, length);
        return null;
    }

    private static Mp4Info Movie(Stream stream, long start, long length)
    {
        TimeSpan duration = TimeSpan.Zero; var tracks = new List<Mp4Track>();
        foreach (var (type, s, l) in Boxes(stream, start, length))
        {
            if (type == "mvhd") duration = HeaderDuration(Bytes(stream, s, l));
            if (type != "trak") continue;
            string handler = ""; int width = 0, height = 0; TimeSpan trackDuration = TimeSpan.Zero;
            foreach (var (t, ts, tl) in Boxes(stream, s, l))
            {
                if (t == "tkhd")
                {
                    var data = Bytes(stream, ts, tl); int offset = data[0] == 1 ? 88 : 76;
                    if (data.Length >= offset + 8) { width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset)) >> 16; height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 4)) >> 16; }
                }
                if (t != "mdia") continue;
                foreach (var (m, ms, ml) in Boxes(stream, ts, tl))
                {
                    if (m == "mdhd") trackDuration = HeaderDuration(Bytes(stream, ms, ml));
                    if (m == "hdlr") { var data = Bytes(stream, ms, ml); if (data.Length >= 12) handler = Encoding.ASCII.GetString(data, 8, 4); }
                }
            }
            tracks.Add(new(handler, width, height, trackDuration));
        }
        return new(duration, tracks);
    }

    /// <summary>mvhd and mdhd share a layout: version, flags, times, timescale, duration.</summary>
    private static TimeSpan HeaderDuration(byte[] data)
    {
        if (data.Length < 20) return TimeSpan.Zero;
        bool wide = data[0] == 1;
        uint scale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(wide ? 20 : 12));
        ulong units = wide ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(24)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
        return scale == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)(units * (decimal)TimeSpan.TicksPerSecond / scale));
    }

    private static IEnumerable<(string Type, long Start, long Length)> Boxes(Stream stream, long start, long length)
    {
        long position = start, end = start + length; var header = new byte[16];
        while (position + 8 <= end)
        {
            stream.Position = position; stream.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header); var type = Encoding.ASCII.GetString(header, 4, 4); int headerSize = 8;
            if (size == 1) { stream.ReadExactly(header, 8, 8); size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)); headerSize = 16; }
            else if (size == 0) size = end - position;
            if (size < headerSize || position + size > end) yield break;
            yield return (type, position + headerSize, size - headerSize);
            position += size;
        }
    }

    private static byte[] Bytes(Stream stream, long start, long length)
    {
        var data = new byte[Math.Min(length, 4096)]; stream.Position = start; stream.ReadExactly(data); return data;
    }
}
