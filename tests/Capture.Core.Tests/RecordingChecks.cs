using System.Buffers.Binary;
using System.Text;
using Capture.Core;

internal static class RecordingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

        var session = new RecordingSession();
        check(!session.Pause(S(1)) && !session.Stop(S(1)), "Idle cannot pause or stop.");
        check(session.BeginCountdown() && session.State == RecordingState.Countdown, "Countdown before recording.");
        check(session.MediaTime(S(3)) == TimeSpan.Zero, "Countdown is not part of the video.");
        check(session.Start(S(3)) && session.MediaTime(S(5)) == S(2), "Media time starts at the first recorded instant.");
        check(session.Pause(S(5)) && session.MediaTime(S(9)) == S(2), "Paused media time is frozen.");
        check(!session.Pause(S(9)), "Pausing twice is rejected.");
        check(session.Resume(S(9)) && session.MediaTime(S(10)) == S(3), "Paused interval is removed from the video.");
        check(session.Pause(S(11)) && session.Resume(S(12)) && session.Pauses == 2, "Pauses are counted.");
        check(session.Stop(S(14)) && session.MediaTime(S(99)) == S(6), "Stopped duration is fixed.");
        check(!session.Resume(S(15)) && !session.Start(S(15)), "Stopped session cannot continue.");
        check(session.Restart() && session.State == RecordingState.Idle && session.Pauses == 0, "Restart returns to idle.");
        check(session.Start(S(20)) && session.Stop(S(21)) && session.MediaTime(S(30)) == S(1), "Restarted take has its own time.");
        var paused = new RecordingSession(); paused.Start(S(0)); paused.Pause(S(4));
        check(paused.Stop(S(8)) && paused.MediaTime(S(8)) == S(4), "Stopping while paused keeps time up to the pause.");
        var backwards = new RecordingSession(); backwards.Start(S(5));
        check(!backwards.Pause(S(4)) && backwards.MediaTime(S(4)) == TimeSpan.Zero, "Clock going backwards never gives negative time.");

        check(RecordingOutput.Size(1921, 1081) == (1920, 1080), "Odd sides round down to even.");
        check(RecordingOutput.Size(3840, 2160, 1920) == (1920, 1080), "Long side is capped.");
        check(RecordingOutput.Size(1000, 3001, 1920) == (638, 1920), "Portrait cap keeps aspect and even sides.");
        check(RecordingOutput.Size(800, 600, 1920) == (800, 600), "Never scales up.");
        check(RecordingOutput.Size(3, 3) == (2, 2), "Smallest even size.");
        bool tiny = false; try { RecordingOutput.Size(1, 50); } catch (ArgumentOutOfRangeException) { tiny = true; }
        check(tiny, "One-pixel recording rejected.");
        check(RecordingOutput.Slot(S(1), 30) == 30 && RecordingOutput.Slot(S(1.0333), 30) == 30 && RecordingOutput.Slot(S(1.034), 30) == 31, "Frame slots follow media time.");
        check(RecordingOutput.SlotTime(31, 30) == TimeSpan.FromTicks(10_333_334), "Slot time rounds up to ticks.");
        check(Enumerable.Range(0, 3600).All(i => RecordingOutput.Slot(RecordingOutput.SlotTime(i, 60), 60) == i && RecordingOutput.Slot(RecordingOutput.SlotTime(i, 30), 30) == i), "Slot time maps back to its slot.");

        var events = new List<InputEvent> { new(TimeSpan.FromMilliseconds(16), InputEventKind.Move, -40, 1200), new(TimeSpan.FromMilliseconds(500), InputEventKind.Down, 10, 20, 1), new(TimeSpan.FromMilliseconds(620), InputEventKind.Up, 10, 20, 1), new(TimeSpan.FromMilliseconds(900), InputEventKind.Key, 0, 0, 0, "Ctrl+Shift+S") };
        var writer = new StringWriter(); InputTrack.Write(writer, events);
        var read = InputTrack.Read(new StringReader(writer.ToString()));
        check(read.SequenceEqual(events), "Input track round trip.");
        bool rejected = false; try { InputTrack.Write(new StringWriter(), [new(TimeSpan.Zero, InputEventKind.Key, 0, 0, 0, "a,b")]); } catch (ArgumentException) { rejected = true; }
        check(rejected, "Separator in key name rejected.");
        rejected = false; try { InputTrack.Read(new StringReader("other\n")); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "Unknown input track header rejected.");
        check(InputTrack.IsShortcut("S", true, false, false) && InputTrack.IsShortcut("F5", false, false, false) && InputTrack.IsShortcut("Esc", false, false, false), "Shortcuts and named keys are shown.");
        check(!InputTrack.IsShortcut("a", false, false, false) && !InputTrack.IsShortcut("F", false, false, false) && !InputTrack.IsShortcut("Fx", false, false, false), "Plain typing is never kept by default.");

        var mp4 = Movie();
        var info = Mp4Reader.Read(new MemoryStream(mp4));
        check(info is not null && info.Duration == S(12.5), "MP4 movie duration.");
        check(info!.Tracks.Count == 2 && info.Tracks[0] == new Mp4Track("vide", 1920, 1080, S(12.5)) && info.Tracks[1].Handler == "soun" && info.Tracks[1].Duration == S(12.48), "MP4 tracks.");
        check(Mp4Reader.Read(new MemoryStream(Box("ftyp", Encoding.ASCII.GetBytes("isom")).Concat(Box("mdat", new byte[64])).ToArray())) is null, "Missing index means an interrupted file.");
        check(Mp4Reader.Read(new MemoryStream(mp4[..^10])) is null, "Truncated index is not trusted.");
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray(); var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length); Encoding.ASCII.GetBytes(type).CopyTo(box, 4); body.CopyTo(box, 8); return box;
    }
    private static byte[] U32(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }
    private static byte[] Header(uint scale, uint units) => [.. new byte[12], .. U32(scale), .. U32(units), .. new byte[8]];
    private static byte[] Track(string handler, int width, int height, uint scale, uint units)
    {
        var tkhd = new byte[84]; BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(76), width << 16); BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(80), height << 16);
        return Box("trak", Box("tkhd", tkhd), Box("mdia", Box("mdhd", Header(scale, units)), Box("hdlr", [.. new byte[8], .. Encoding.ASCII.GetBytes(handler), .. new byte[12]])));
    }
    private static byte[] Movie() => [.. Box("ftyp", Encoding.ASCII.GetBytes("isom")), .. Box("mdat", new byte[32]),
        .. Box("moov", Box("mvhd", Header(1000, 12500)), Track("vide", 1920, 1080, 30000, 375000), Track("soun", 0, 0, 48000, 599040))];
}
