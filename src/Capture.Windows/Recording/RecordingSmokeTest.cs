using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Capture.Core;
using Capture.Windows.Platform;
using Windows.Graphics.Capture;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfColor = System.Windows.Media.Color;

namespace Capture.Windows.Recording;

/// <summary>
/// Records a real, animated window and an area over it through <see cref="RecordingJob"/>, then checks the
/// saved MP4 size and length, the input track and the clean-up of temporary files. Skips only when the
/// machine has no screen capture or no encoder.
/// </summary>
internal static class RecordingSmokeTest
{
    internal static async Task<string> RunAsync(string folder)
    {
        if (!GraphicsCaptureSession.IsSupported()) return "SKIP: Windows Graphics Capture가 없어 실제 녹화 검사를 건너뜁니다.";
        var output = Path.Combine(folder, "recordings");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        var window = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Width = 420, Height = 300, Left = 80, Top = 80, Background = new WpfBrush(WpfColor.FromRgb(30, 90, 200)) };
        int tick = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal, (_, _) => window.Background = new WpfBrush(WpfColor.FromRgb((byte)(tick++ * 9 % 255), 90, 200)), window.Dispatcher);
        window.SourceInitialized += (_, _) => Native.SetWindowPos(new WindowInteropHelper(window).Handle, -1, 80, 80, 420, 300, 0x0010);
        window.Show();
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            await Task.Delay(300);
            var lines = new List<string>();

            var windowJob = new RecordingJob(RecordingTarget.ForWindow(handle), output, "window", 30, cursor: true);
            var windowRun = windowJob.RunAsync();
            try { await windowJob.Started.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("인코더", StringComparison.Ordinal)) { return "SKIP: " + error.Message; }
            catch (COMException error) { return $"SKIP: 녹화 인코더를 준비하지 못했습니다 (0x{error.HResult:X8})."; }
            // Cursor moves inside the window, then a pause that must not appear in the video.
            SetCursorPos(80 + 100, 80 + 100); await Task.Delay(500);
            SetCursorPos(80 + 200, 80 + 150); await Task.Delay(500);
            windowJob.Pause(); await Task.Delay(700); windowJob.Resume();
            await Task.Delay(800);
            windowJob.Stop();
            var windowResult = await windowRun.WaitAsync(TimeSpan.FromSeconds(30));
            lines.Add(Check(windowResult, 420, 300, TimeSpan.FromSeconds(1.8), "window"));
            var events = InputTrack.Read(new StringReader(File.ReadAllText(windowResult.InputTrack)));
            if (!events.Any(e => e.Kind == InputEventKind.Move && e.X == 100 && e.Y == 100) || !events.Any(e => e.Kind == InputEventKind.Move && e.X == 200 && e.Y == 150))
                throw new InvalidOperationException("창 녹화의 커서 기록이 창 기준 좌표와 맞지 않습니다: " + string.Join(" ", events.Take(8).Select(e => $"{e.Kind}({e.X},{e.Y})")));
            lines.Add($"window input track: {events.Count} events, window-relative positions match");

            var area = RecordingTarget.ForArea(new PixelRect(100, 100, 301, 201)) ?? throw new InvalidOperationException("영역 대상을 만들지 못했습니다.");
            var areaJob = new RecordingJob(area, output, "window", 30, cursor: false);
            var areaRun = areaJob.RunAsync();
            await areaJob.Started.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(1200);
            areaJob.Stop();
            var areaResult = await areaRun.WaitAsync(TimeSpan.FromSeconds(30));
            if (Path.GetFileName(areaResult.Path) != "window-2.mp4") throw new InvalidOperationException("같은 이름의 녹화가 덮어쓰였습니다: " + areaResult.Path);
            lines.Add(Check(areaResult, 300, 200, TimeSpan.FromSeconds(1.2), "area (odd 301x201 rounded to even)"));

            var leftovers = Directory.GetFiles(RecordingJob.WorkFolder, "*.mp4");
            if (leftovers.Length > 0) throw new InvalidOperationException("임시 녹화 파일이 남았습니다: " + string.Join(", ", leftovers));
            foreach (var track in new[] { windowResult.InputTrack, areaResult.InputTrack }) File.Delete(track);
            return "PASS: " + string.Join("; ", lines);
        }
        finally
        {
            timer.Stop();
            window.Close();
        }
    }

    /// <summary>The user-facing flow: bar buttons pause, resume, stop, start over and discard; the bar and popup are saved as review images.</summary>
    internal static async Task<string> RunFlowAsync(string folder)
    {
        if (!GraphicsCaptureSession.IsSupported()) return "SKIP: Windows Graphics Capture가 없어 녹화 흐름 검사를 건너뜁니다.";
        var output = Path.Combine(folder, "recordings-flow");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        var window = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Width = 360, Height = 240, Left = 120, Top = 120, Background = new WpfBrush(WpfColor.FromRgb(240, 240, 244)) };
        window.Show();
        int count = 0;
        try
        {
            var display = Native.Displays()[0];
            var target = RecordingTarget.ForArea(new PixelRect(display.Bounds.Left + 120, display.Bounds.Top + 120, 360, 240)) ?? throw new InvalidOperationException("영역 대상을 만들지 못했습니다.");
            var desktop = new PixelRect(display.Bounds.Left + target.Region.X, display.Bounds.Top + target.Region.Y, target.Region.Width, target.Region.Height);
            RecordingController Start() => new(target, desktop, display, new(output, () => $"flow-{++count}", 30, true, 0));
            async Task<RecordingController> Running(RecordingController controller)
            {
                for (int i = 0; i < 300 && controller.Elapsed <= TimeSpan.Zero; i++) await Task.Delay(50);
                if (controller.Elapsed <= TimeSpan.Zero) throw new InvalidOperationException("녹화가 시작되지 않았습니다.");
                return controller;
            }

            var first = Start(); var firstRun = first.RunAsync();
            await Running(first);
            if (RecordingController.Active != first || !first.Bar.IsVisible) throw new InvalidOperationException("녹화 중 막대가 보이지 않습니다.");
            await Task.Delay(400);
            first.Bar.Click("일시정지"); await Task.Delay(600);
            if (!first.Paused) throw new InvalidOperationException("일시정지 버튼이 녹화를 멈추지 않았습니다.");
            Save(first.Bar, Path.Combine(folder, "ui-recording-bar-paused.png"));
            first.Bar.Click("이어서 녹화"); await Task.Delay(700);
            Save(first.Bar, Path.Combine(folder, "ui-recording-bar.png"));
            first.Bar.Click("정지하고 저장");
            var saved = await firstRun.WaitAsync(TimeSpan.FromSeconds(30)) ?? throw new InvalidOperationException("정지했는데 저장된 녹화가 없습니다.");
            if (RecordingController.Active is not null || first.Bar.IsVisible) throw new InvalidOperationException("녹화가 끝난 뒤 막대가 남았습니다.");
            var line = Check(saved, 360, 240, TimeSpan.FromSeconds(1.1), "bar stop (0.6s pause removed)");

            var second = Start(); var secondRun = second.RunAsync();
            await Running(second); await Task.Delay(700);
            second.Bar.Click("처음부터 다시");
            for (int i = 0; i < 300 && second.Elapsed > TimeSpan.FromMilliseconds(500); i++) await Task.Delay(20);
            await Running(second); await Task.Delay(800);
            second.Bar.Click("정지하고 저장");
            var again = await secondRun.WaitAsync(TimeSpan.FromSeconds(30)) ?? throw new InvalidOperationException("처음부터 다시 녹화한 뒤 저장되지 않았습니다.");
            line += "; " + Check(again, 360, 240, TimeSpan.FromSeconds(0.8), "start over keeps only the new take");

            var third = Start(); var thirdRun = third.RunAsync();
            await Running(third); await Task.Delay(400);
            third.Bar.Click("취소하고 버리기");
            if (await thirdRun.WaitAsync(TimeSpan.FromSeconds(30)) is not null) throw new InvalidOperationException("버린 녹화가 저장되었습니다.");
            var files = Directory.GetFiles(output, "*.mp4").Select(Path.GetFileName).Order().ToArray();
            if (!files.SequenceEqual(new[] { "flow-1.mp4", "flow-3.mp4" })) throw new InvalidOperationException("남은 녹화 파일이 기대와 다릅니다: " + string.Join(", ", files));

            var popup = new RecordingPopup(saved); popup.Show(); await Task.Delay(1200);
            Save(popup, Path.Combine(folder, "ui-recording-popup.png")); popup.Close();
            return "PASS: " + line + "; discard leaves no file; bar and popup images saved";
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Sound requested: with an audio device the MP4 must carry a sound track as long as the picture, pause included;
    /// without one the recording must still be saved and say why the sound is missing.
    /// </summary>
    internal static async Task<string> RunSoundAsync(string folder)
    {
        if (!GraphicsCaptureSession.IsSupported()) return "SKIP: Windows Graphics Capture가 없어 소리 녹화 검사를 건너뜁니다.";
        var output = Path.Combine(folder, "recordings-sound");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        var display = Native.Displays()[0];
        var target = RecordingTarget.ForArea(new PixelRect(display.Bounds.Left, display.Bounds.Top, 320, 240)) ?? throw new InvalidOperationException("영역 대상을 만들지 못했습니다.");
        var job = new RecordingJob(target, output, "sound", 30, false, systemSound: true, microphoneSound: true);
        var run = job.RunAsync();
        await job.Started.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(700); job.Pause(); await Task.Delay(500); job.Resume(); await Task.Delay(800);
        job.Stop();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        using var file = File.OpenRead(result.Path);
        var info = Mp4Reader.Read(file) ?? throw new InvalidOperationException("소리를 요청한 녹화에 MP4 색인이 없습니다.");
        var video = info.Tracks.First(t => t.Handler == "vide"); var sound = info.Tracks.FirstOrDefault(t => t.Handler == "soun");
        if (!result.HasSound)
        {
            if (sound is not null) throw new InvalidOperationException("소리 장치가 없다고 했는데 소리 트랙이 있습니다.");
            if (result.SoundProblems is not { Count: > 0 }) throw new InvalidOperationException("소리 없이 녹화한 이유가 없습니다.");
            return FormattableString.Invariant($"PASS (no audio device): video {video.Duration.TotalSeconds:0.00}s saved without sound; reason shown: {string.Join(" / ", result.SoundProblems)}");
        }
        if (sound is null) throw new InvalidOperationException("소리를 녹음했다고 했는데 소리 트랙이 없습니다.");
        if ((sound.Duration - video.Duration).Duration() > TimeSpan.FromMilliseconds(250)) throw new InvalidOperationException($"소리 {sound.Duration.TotalSeconds:0.000}s와 영상 {video.Duration.TotalSeconds:0.000}s 길이가 어긋납니다.");
        return FormattableString.Invariant($"PASS: video {video.Duration.TotalSeconds:0.00}s, sound {sound.Duration.TotalSeconds:0.00}s after a 0.5s pause; problems: {(result.SoundProblems is { Count: > 0 } p ? string.Join(" / ", p) : "none")}");
    }

    private static void Save(Window window, string path)
    {
        // The whole window, so the content's margin and shadow are included rather than cropped.
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    private static string Check(RecordingResult result, int width, int height, TimeSpan expected, string label)
    {
        using var file = File.OpenRead(result.Path);
        var info = Mp4Reader.Read(file) ?? throw new InvalidOperationException($"{label} 녹화에 MP4 색인이 없습니다.");
        var video = info.Tracks.FirstOrDefault(t => t.Handler == "vide") ?? throw new InvalidOperationException($"{label} 녹화에 영상 트랙이 없습니다.");
        if (video.Width != width || video.Height != height) throw new InvalidOperationException($"{label} 녹화 크기 {video.Width}x{video.Height}, 기대 {width}x{height}");
        // Timer-driven waits on a shared runner drift; the pause must still be gone, so the upper bound is tight.
        if (video.Duration < expected - TimeSpan.FromMilliseconds(300) || video.Duration > expected + TimeSpan.FromMilliseconds(500))
            throw new InvalidOperationException($"{label} 녹화 길이 {video.Duration.TotalSeconds:0.000}s, 기대 약 {expected.TotalSeconds:0.0}s");
        return FormattableString.Invariant($"{label} {video.Width}x{video.Height} {video.Duration.TotalSeconds:0.00}s ({new FileInfo(result.Path).Length / 1024} KB)");
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
