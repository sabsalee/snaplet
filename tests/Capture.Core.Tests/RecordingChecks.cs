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

internal static class RecordingEngineChecks
{
    internal static void Run(Action<bool, string> check)
    {
        PixelRect[] monitors = [new(0, 0, 1920, 1080), new(1920, -200, 1280, 1024)];
        check(RecordingArea.OnMonitor(new(100, 100, 400, 300), monitors) == (0, new PixelRect(100, 100, 400, 300)), "Selection inside the first monitor.");
        check(RecordingArea.OnMonitor(new(1800, 0, 600, 300), monitors) == (1, new PixelRect(0, 200, 480, 300)), "Larger share wins and is clipped, relative to that monitor.");
        check(RecordingArea.OnMonitor(new(-500, 0, 400, 300), monitors) is null, "Selection outside every monitor.");
        check(RecordingArea.OnMonitor(new(1919, 0, 1, 50), monitors) is null, "Sliver is rejected.");

        var sampler = new InputSampler(); var t = TimeSpan.FromMilliseconds(10);
        sampler.Sample(t, 5, 5, 0); sampler.Sample(t * 2, 5, 5, 0); sampler.Sample(t * 3, 6, 5, 1); sampler.Sample(t * 4, 6, 5, 1 | 2); sampler.Sample(t * 5, 6, 5, 0);
        var kinds = string.Join(",", sampler.Events.Select(e => $"{e.Kind}{e.Button}"));
        check(kinds == "Move0,Move0,Down1,Down2,Up1,Up2", "Sampler writes moves and button edges only: " + kinds);
        check(sampler.Events[2].X == 6 && sampler.Events[2].Time == t * 3, "Button edge carries position and time.");

        var taken = new HashSet<string> { Path.Combine("v", "a.mp4"), Path.Combine("v", "a-2.mp4") };
        check(RecordingFiles.UniquePath("v", "a", ".mp4", taken.Contains) == Path.Combine("v", "a-3.mp4"), "Unique recording name.");
        check(RecordingFiles.UniquePath("v", "b", ".mp4", taken.Contains) == Path.Combine("v", "b.mp4"), "Free name kept.");
        var work = new PixelRect(0, 0, 1920, 1040);
        check(RecordingControls.Place(new(400, 200, 800, 500), 300, 44, work) == (650, 710), "Toolbar centred under the region.");
        check(RecordingControls.Place(new(400, 600, 800, 420), 300, 44, work) == (650, 546), "Toolbar above when no room below.");
        check(RecordingControls.Place(new(0, 0, 1920, 1040), 300, 44, work) == (810, 986), "Full-screen region keeps the toolbar inside the bottom edge.");
        check(RecordingControls.Place(new(1800, 200, 100, 100), 300, 44, work).X == 1620, "Toolbar clamped to the work area.");
        check(RecordingControls.Clock(TimeSpan.FromSeconds(65.9)) == "1:05" && RecordingControls.Clock(TimeSpan.FromSeconds(3725)) == "1:02:05" && RecordingControls.Clock(TimeSpan.FromSeconds(-3)) == "0:00", "Elapsed clock text.");
    }
}

internal static class RecordingAudioChecks
{
    internal static void Run(Action<bool, string> check)
    {
        byte[] Float(int frames, int channels, Func<int, int, float> value)
        {
            var data = new byte[frames * channels * 4];
            for (int f = 0; f < frames; f++) for (int c = 0; c < channels; c++) BitConverter.TryWriteBytes(data.AsSpan((f * channels + c) * 4), value(f, c));
            return data;
        }
        var output = new List<float>();
        new AudioConverter(new(48000, 2, 32, true)).Convert(Float(480, 2, (f, c) => c == 0 ? .5f : -.25f), output);
        check(output.Count == 960 && output[0] == .5f && output[1] == -.25f, "48 kHz stereo float passes through unchanged.");

        output.Clear();
        var converter = new AudioConverter(new(44100, 2, 32, true));
        // Two buffers of 441 frames = 20 ms must give 960 frames at 48 kHz, give or take one at the edge.
        converter.Convert(Float(441, 2, (f, c) => f / 882f), output); converter.Convert(Float(441, 2, (f, c) => (f + 441) / 882f), output);
        check(Math.Abs(output.Count / 2 - 960) <= 1, "44.1 kHz resampled to 48 kHz keeps duration: " + output.Count / 2);
        bool rising = true; for (int i = 2; i < output.Count; i += 2) rising &= output[i] >= output[i - 2] - 1e-6f;
        check(rising, "A rising ramp stays monotonic across buffer edges (no clicks).");

        output.Clear();
        var mono16 = new byte[4]; BitConverter.TryWriteBytes(mono16.AsSpan(0), (short)16384); BitConverter.TryWriteBytes(mono16.AsSpan(2), (short)-32768);
        new AudioConverter(new(48000, 1, 16, false)).Convert(mono16, output);
        check(output.SequenceEqual(new[] { .5f, .5f, -1f, -1f }), "16-bit mono is copied to both sides.");
        output.Clear();
        new AudioConverter(new(48000, 6, 24, false)).Convert(new byte[] { 0, 0, 0x40, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, output);
        check(output.SequenceEqual(new[] { .5f, -.5f }), "24-bit 5.1 keeps front left and right.");
        bool rejected = false; try { _ = new AudioConverter(new(48000, 2, 8, false)); } catch (ArgumentException) { rejected = true; }
        check(rejected, "8-bit audio rejected.");

        var timeline = new AudioTimeline();
        timeline.Add(Enumerable.Repeat(1f, 480 * 2).ToList());
        var first = timeline.Take(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200));
        check(first.Length == 960 * 4 && BitConverter.ToInt16(first, 0) == 32767 && BitConverter.ToInt16(first, 480 * 4) == 0, "Missing sound is filled with silence up to the video time.");
        check(timeline.Take(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200)).Length == 0 && timeline.Written == 960, "Nothing is repeated for the same time.");
        timeline.Add(Enumerable.Repeat(.5f, 48000 * 2).ToList());
        var lagged = timeline.Take(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(100));
        check(lagged.Length == 960 * 4, "Only the time asked for is written.");
        var after = timeline.Take(TimeSpan.FromMilliseconds(140), TimeSpan.FromMilliseconds(100));
        check(after.Length == 4800 * 4 && timeline.Written == 6720, "A backlog beyond the allowed lag is trimmed to stay in sync.");

        var mixer = new AudioMixer(); var mixed = new List<float>();
        mixer.AddSystem([.6f, .6f, .6f, .6f]); mixer.AddMicrophone([.6f, -.2f]);
        mixer.Drain(mixed, true, true);
        check(mixed.Count == 2 && mixed[0] == 1f && Math.Abs(mixed[1] - .4f) < 1e-6f, "Both sources mix where both have frames, clamped to full scale.");
        mixer.Drain(mixed, true, true, flush: true);
        check(mixed.Count == 4 && Math.Abs(mixed[2] - .6f) < 1e-6f, "Flush releases the longer source.");
        mixed.Clear(); mixer.AddMicrophone([.3f, .3f]); mixer.Drain(mixed, false, true);
        check(mixed.SequenceEqual(new[] { .3f, .3f }), "Microphone alone passes through.");
        check(Math.Abs(AudioMixer.Peak([.1f, -.8f, .3f]) - .8f) < 1e-6f, "Peak level for the indicator.");
    }
}
