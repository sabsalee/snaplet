using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Capture.Core;
using Capture.Windows.Platform;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;

namespace Capture.Windows.Recording;

/// <summary>
/// Recording phase 0 checks. <c>--record-probe</c> records the real screen and reports latency, frame pacing,
/// pause continuity and capture exclusion. <see cref="SyntheticAsync"/> encodes generated frames and sound
/// with the same encoder settings so CI can check the MP4 without a desktop.
/// </summary>
internal static class RecordingProbe
{
    /// <summary>Encodes 3 s of generated video and sound with a 0.6 s pause and checks the MP4 index. Returns a SKIP line when the encoder is unavailable.</summary>
    internal static async Task<string> SyntheticAsync(string folder)
    {
        const int width = 640, height = 360, fps = 30, sampleRate = 48000, chunk = 960;
        TimeSpan pauseStart = TimeSpan.FromSeconds(1), pauseEnd = TimeSpan.FromSeconds(1.6), end = TimeSpan.FromSeconds(3);
        var expected = end - (pauseEnd - pauseStart);
        // Wall-clock schedule through the real session rules: nothing is produced while paused.
        var session = new RecordingSession(); session.Start(TimeSpan.Zero);
        var video = new List<TimeSpan>(); var audio = new List<TimeSpan>();
        for (var wall = TimeSpan.Zero; wall < end; wall += TimeSpan.FromMilliseconds(1))
        {
            if (wall >= pauseStart && wall < pauseEnd) { session.Pause(pauseStart); continue; }
            if (wall == pauseEnd) session.Resume(pauseEnd);
            var media = session.MediaTime(wall);
            if (RecordingOutput.Slot(media, fps) == video.Count) video.Add(RecordingOutput.SlotTime(video.Count, fps));
            if (media >= TimeSpan.FromTicks(audio.Count * chunk * TimeSpan.TicksPerSecond / sampleRate)) audio.Add(TimeSpan.FromTicks(audio.Count * chunk * TimeSpan.TicksPerSecond / sampleRate));
        }
        var videoStream = new VideoStreamDescriptor(VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, width, height));
        var audioStream = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(sampleRate, 2, 16));
        var source = new MediaStreamSource(videoStream, audioStream) { BufferTime = TimeSpan.Zero, CanSeek = false };
        int videoIndex = 0, audioIndex = 0;
        source.SampleRequested += (_, args) =>
        {
            var request = args.Request;
            if (request.StreamDescriptor is VideoStreamDescriptor)
            {
                if (videoIndex >= video.Count) { request.Sample = null; return; }
                var pixels = new byte[width * height * 4];
                int bar = videoIndex * 7 % width;
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4; bool lit = Math.Abs(x - bar) < 24;
                    pixels[i] = (byte)(lit ? 255 : 40); pixels[i + 1] = (byte)(lit ? 200 : y * 255 / height); pixels[i + 2] = (byte)(lit ? 60 : 90); pixels[i + 3] = 255;
                }
                var sample = MediaStreamSample.CreateFromBuffer(pixels.AsBuffer(), video[videoIndex++]);
                sample.Duration = RecordingOutput.SlotTime(1, fps);
                request.Sample = sample;
            }
            else
            {
                if (audioIndex >= audio.Count) { request.Sample = null; return; }
                var pcm = new byte[chunk * 4];
                for (int n = 0; n < chunk; n++)
                {
                    short value = (short)(Math.Sin(2 * Math.PI * 440 * (audioIndex * chunk + n) / sampleRate) * 8000);
                    BitConverter.TryWriteBytes(pcm.AsSpan(n * 4), value); BitConverter.TryWriteBytes(pcm.AsSpan(n * 4 + 2), value);
                }
                var sample = MediaStreamSample.CreateFromBuffer(pcm.AsBuffer(), audio[audioIndex++]);
                sample.Duration = TimeSpan.FromTicks(chunk * TimeSpan.TicksPerSecond / sampleRate);
                request.Sample = sample;
            }
        };
        var path = Path.Combine(folder, "recording-synthetic.mp4");
        PrepareTranscodeResult prepared;
        var clock = Stopwatch.StartNew();
        await using (var file = File.Create(path))
        {
            var stream = file.AsRandomAccessStream();
            try { prepared = await new MediaTranscoder { HardwareAccelerationEnabled = true }.PrepareMediaStreamSourceTranscodeAsync(source, stream, ScreenRecorder.Profile(width, height, fps, true)); }
            catch (COMException error) { return $"SKIP: 이 환경에는 내장 인코더가 없습니다 (0x{error.HResult:X8}). MP4 검증을 건너뜁니다."; }
            if (!prepared.CanTranscode) return $"SKIP: 인코더를 준비하지 못했습니다 ({prepared.FailureReason}). MP4 검증을 건너뜁니다.";
            var prepareMs = clock.Elapsed.TotalMilliseconds;
            await prepared.TranscodeAsync();
            clock.Stop();
            await file.FlushAsync();
            file.Position = 0;
            var info = Mp4Reader.Read(file) ?? throw new InvalidOperationException("합성 녹화 MP4에 색인(moov)이 없습니다.");
            var videoTrack = info.Tracks.FirstOrDefault(t => t.Handler == "vide") ?? throw new InvalidOperationException("합성 녹화에 영상 트랙이 없습니다.");
            var soundTrack = info.Tracks.FirstOrDefault(t => t.Handler == "soun") ?? throw new InvalidOperationException("합성 녹화에 소리 트랙이 없습니다.");
            var tolerance = TimeSpan.FromMilliseconds(150);
            if (videoTrack.Width != width || videoTrack.Height != height) throw new InvalidOperationException($"합성 녹화 크기 {videoTrack.Width}×{videoTrack.Height}, 기대 {width}×{height}");
            if ((videoTrack.Duration - expected).Duration() > tolerance) throw new InvalidOperationException($"영상 길이 {videoTrack.Duration.TotalSeconds:0.000}s, 기대 {expected.TotalSeconds:0.000}s (일시정지 0.6s 제외)");
            if ((soundTrack.Duration - expected).Duration() > tolerance) throw new InvalidOperationException($"소리 길이 {soundTrack.Duration.TotalSeconds:0.000}s, 기대 {expected.TotalSeconds:0.000}s");
            return string.Create(CultureInfo.InvariantCulture, $"PASS: synthetic {width}x{height}@{fps} H.264 + AAC, video {videoTrack.Duration.TotalSeconds:0.000}s, sound {soundTrack.Duration.TotalSeconds:0.000}s, expected {expected.TotalSeconds:0.000}s after a 0.6s pause; prepare {prepareMs:0} ms, total {clock.Elapsed.TotalMilliseconds:0} ms, {new FileInfo(path).Length / 1024} KB.");
        }
    }

    /// <summary>
    /// <c>--record-probe &lt;folder&gt; [--seconds 10] [--fps 30] [--pause-at 3] [--pause-for 2] [--region x,y,w,h] [--cursor] [--crash-at 5]</c>
    /// and <c>--record-inspect &lt;file.mp4&gt;</c>. Writes probe.mp4 and probe-report.txt; nothing leaves the PC.
    /// </summary>
    internal static async Task RunAsync(string[] args, Action<int> shutdown)
    {
        string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        double Number(string name, double fallback) => double.Parse(Option(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        var report = new StringBuilder();
        string reportPath = "";
        int code = 1;
        try
        {
            if (args[0] == "--record-inspect")
            {
                var target = args[1]; reportPath = target + ".inspect.txt";
                await using var file = File.OpenRead(target);
                var info = Mp4Reader.Read(file);
                report.AppendLine(info is null ? $"NO INDEX: {target} 에 moov가 없어 일반 재생기로 열 수 없습니다 ({file.Length / 1024} KB 기록됨)." : Describe(info));
                code = 0;
                return;
            }
            var folder = Path.GetFullPath(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "record-probe");
            Directory.CreateDirectory(folder); reportPath = Path.Combine(folder, "probe-report.txt");
            int fps = (int)Number("--fps", 30); var seconds = TimeSpan.FromSeconds(Number("--seconds", 10));
            var pauseAt = TimeSpan.FromSeconds(Number("--pause-at", 3)); var pauseFor = TimeSpan.FromSeconds(Number("--pause-for", 2));
            double crashAt = Number("--crash-at", -1);
            bool cursor = args.Contains("--cursor");
            var monitor = Native.MonitorFromPoint(new Native.POINT(), 1);
            var bounds = Native.Displays().First(d => d.Handle == monitor).Bounds;
            var parts = Option("--region", $"0,0,{bounds.Width},{bounds.Height}").Split(',').Select(int.Parse).ToArray();
            var region = new RecordingRegion(parts[0], parts[1], parts[2], parts[3]);
            report.AppendLine($"Snaplet recording probe {typeof(RecordingProbe).Assembly.GetName().Version} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"OS {Environment.OSVersion.Version}, {Environment.ProcessorCount} logical CPUs, primary monitor {bounds.Width}x{bounds.Height}");
            report.AppendLine($"WGC supported: {GraphicsCaptureSession.IsSupported()}; border can be removed (IsBorderRequired present): {ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired")}");
            report.AppendLine($"Focus session API present (do-not-disturb candidate): {ApiInformation.IsTypePresent("Windows.UI.Shell.FocusSessionManager")}");
            report.AppendLine($"Region {region.X},{region.Y} {region.Width}x{region.Height}, {fps} fps, cursor {(cursor ? "on" : "off")}, {seconds.TotalSeconds:0.#} s, pause at {pauseAt.TotalSeconds:0.#} s for {pauseFor.TotalSeconds:0.#} s");

            // Two markers inside the region: one excluded from capture like the recording toolbar, one included like a live annotation.
            var excluded = Marker(region.X + 20, region.Y + 20, WpfColor.FromRgb(255, 0, 255), true);
            var included = Marker(region.X + 120, region.Y + 20, WpfColor.FromRgb(0, 255, 0), false);
            var recorder = new ScreenRecorder(CaptureEngine.CreateItem(monitor, false), region, fps, cursor);
            var path = Path.Combine(folder, "probe.mp4");
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            string exclusion = "not checked";
            await using (var file = File.Create(path))
            {
                var recording = recorder.RunAsync(file.AsRandomAccessStream(), CancellationToken.None);
                var control = Task.Run(async () =>
                {
                    while (recorder.Elapsed < TimeSpan.FromSeconds(1.5)) await Task.Delay(50);
                    exclusion = await CheckMarkers(recorder);
                    if (crashAt >= 0) { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, crashAt - 1.5))); Environment.FailFast("Snaplet recording probe: intentional crash to test recovery."); }
                    while (recorder.Elapsed < pauseAt) await Task.Delay(20);
                    if (pauseFor > TimeSpan.Zero) { recorder.Pause(); await Task.Delay(pauseFor); recorder.Resume(); }
                    while (recorder.Elapsed < seconds) await Task.Delay(20);
                    recorder.Stop();
                });
                await Task.WhenAll(recording, control);
                await file.FlushAsync();
            }
            wall.Stop();
            excluded.Close(); included.Close();
            var used = Process.GetCurrentProcess().TotalProcessorTime - cpu;
            var m = recorder.Metrics; var expected = recorder.Duration;
            report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Encoder prepare {m.PrepareMs:0} ms (done before capture; a pre-warmed encoder hides this)"));
            report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Capture start → first frame {m.FirstFrameMs:0} ms, → first encoded sample {m.FirstSampleMs:0} ms"));
            report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Frames arrived {m.FramesArrived}, copied {m.FramesCopied}, replaced before encoding {m.FramesSuperseded}; samples new {m.SamplesNew}, repeated (static screen) {m.SamplesRepeated}, slots missed (encoder behind) {m.SlotsMissed}"));
            report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"GPU copy avg {(m.FramesCopied == 0 ? 0 : m.CopyMs / m.FramesCopied):0.00} ms, max {m.MaxCopyMs:0.00} ms; process CPU {used.TotalMilliseconds / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:0.0}% of all cores"));
            report.AppendLine($"Capture exclusion: {exclusion}");
            await using (var file = File.OpenRead(path))
            {
                var info = Mp4Reader.Read(file);
                report.AppendLine(info is null ? "MP4: no index (moov) — file is not playable." : Describe(info));
                var video = info?.Tracks.FirstOrDefault(t => t.Handler == "vide");
                bool continuous = video is not null && (video.Duration - expected).Duration() <= TimeSpan.FromMilliseconds(200);
                report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Pause continuity: expected {expected.TotalSeconds:0.000} s after {recorder.Pauses} pause(s), video {(video?.Duration.TotalSeconds ?? 0):0.000} s → {(continuous ? "PASS" : "CHECK")}"));
                code = continuous ? 0 : 1;
            }
            report.AppendLine($"Saved {path}. Open it in the Media Player and a browser to confirm playback.");
        }
        catch (Exception error) { report.AppendLine("FAIL: " + error); }
        finally
        {
            if (reportPath.Length > 0) File.WriteAllText(reportPath, report.ToString());
            shutdown(code);
        }
    }

    private static string Describe(Mp4Info info) => FormattableString.Invariant($"MP4: {info.Duration.TotalSeconds:0.000} s; {string.Join("; ", info.Tracks.Select(t => t.Handler == "vide"
        ? FormattableString.Invariant($"video {t.Width}x{t.Height} {t.Duration.TotalSeconds:0.000} s") : FormattableString.Invariant($"{t.Handler} {t.Duration.TotalSeconds:0.000} s")))}");

    private static Window Marker(int x, int y, WpfColor color, bool excludeFromCapture)
    {
        var window = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Background = new WpfBrush(color), Width = 80, Height = 80 };
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (excludeFromCapture) Native.SetWindowDisplayAffinity(handle, 0x11);
            // Physical pixels, independent of the monitor's scale. SWP_NOSIZE is not used so the size is physical too.
            Native.SetWindowPos(handle, -1, x, y, 80, 80, 0x0010);
        };
        window.Show();
        return window;
    }

    private static async Task<string> CheckMarkers(ScreenRecorder recorder)
    {
        using var bitmap = await recorder.SnapshotAsync();
        if (bitmap is null) return "no frame yet";
        using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyToBuffer(pixels.AsBuffer());
        (byte B, byte G, byte R) At(int x, int y) { int i = (y * converted.PixelWidth + x) * 4; return (pixels[i], pixels[i + 1], pixels[i + 2]); }
        var toolbar = At(60, 60); var annotation = At(160, 60);
        bool toolbarVisible = toolbar.R > 230 && toolbar.G < 30 && toolbar.B > 230, annotationVisible = annotation.G > 230 && annotation.R < 30 && annotation.B < 30;
        return $"excluded marker {(toolbarVisible ? "VISIBLE (bad)" : "hidden (good)")}, included marker {(annotationVisible ? "visible (good)" : "MISSING (bad)")}";
    }
}
