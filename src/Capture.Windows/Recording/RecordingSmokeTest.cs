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
