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

    /// <summary>
    /// Drawing while recording: the bar turns drawing on, tools add marks, undo removes one, the layer passes clicks
    /// through when drawing is off, and the marks are on the captured screen while the bar is not.
    /// </summary>
    internal static async Task<string> RunSketchAsync(string folder)
    {
        if (!GraphicsCaptureSession.IsSupported()) return "SKIP: Windows Graphics Capture가 없어 그리기 검사를 건너뜁니다.";
        var output = Path.Combine(folder, "recordings-sketch");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        var window = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Width = 480, Height = 320, Left = 60, Top = 60, Background = new WpfBrush(WpfColor.FromRgb(246, 246, 248)) };
        window.Show();
        try
        {
            var display = Native.Displays()[0];
            var target = RecordingTarget.ForArea(new PixelRect(display.Bounds.Left + 60, display.Bounds.Top + 60, 480, 320)) ?? throw new InvalidOperationException("영역 대상을 만들지 못했습니다.");
            var desktop = new PixelRect(display.Bounds.Left + target.Region.X, display.Bounds.Top + target.Region.Y, target.Region.Width, target.Region.Height);
            var controller = new RecordingController(target, desktop, display, new(output, () => "sketch", 30, false, 0));
            var run = controller.RunAsync();
            for (int i = 0; i < 300 && controller.Elapsed <= TimeSpan.Zero; i++) await Task.Delay(50);
            var sketch = controller.Sketch ?? throw new InvalidOperationException("영역 녹화에 그리기 층이 없습니다.");
            var handle = new WindowInteropHelper(sketch).Handle;
            if ((GetWindowLongPtr(handle, -20) & 0x20) == 0) throw new InvalidOperationException("그리기를 켜기 전에는 클릭이 아래 앱으로 지나가야 합니다.");
            controller.Bar.Click("화면에 그리기");
            if (!sketch.Drawing || (GetWindowLongPtr(handle, -20) & 0x20) != 0) throw new InvalidOperationException("그리기 버튼이 그리기 층을 켜지 않았습니다.");
            var canvas = sketch.Canvas;
            // A drag on empty screen must reach the canvas, not fall through to the window behind it.
            foreach (var probe in new[] { new System.Windows.Point(20, 20), new System.Windows.Point(sketch.ActualWidth / 2, sketch.ActualHeight / 2), new System.Windows.Point(sketch.ActualWidth - 20, sketch.ActualHeight - 20) })
                if (sketch.InputHitTest(probe) != canvas) throw new InvalidOperationException($"그리기 층의 빈 곳({probe.X:0},{probe.Y:0})이 마우스를 받지 않습니다.");
            var frameLine = CheckFrame(controller, desktop, folder);
            void Drag(string tool, double x1, double y1, double x2, double y2)
            {
                controller.Bar.Click(tool);
                canvas.Begin(new(x1, y1)); canvas.Extend(new((x1 + x2) / 2, (y1 + y2) / 2)); canvas.Extend(new(x2, y2)); canvas.End();
            }
            Drag("사각형", 40, 40, 200, 140);
            Drag("화살표", 300, 260, 220, 160);
            Drag("펜", 260, 60, 420, 100);
            Drag("형광펜", 60, 220, 220, 230);
            Drag("스포트라이트", 30, 30, 210, 150);
            controller.Bar.Click("번호"); canvas.Begin(new(400, 200)); canvas.Begin(new(440, 250));
            if (canvas.Count != 7) throw new InvalidOperationException($"그린 개수가 {canvas.Count}개입니다(기대 7).");
            controller.Bar.Click("마지막 그림 지우기 (Ctrl+Z)");
            if (canvas.Count != 6) throw new InvalidOperationException("되돌리기가 마지막 그림을 지우지 않았습니다.");
            if (canvas.Marks[^1].Kind != AnnotationKind.Step || canvas.Marks[^1].Text != "1") throw new InvalidOperationException("되돌리기가 마지막 번호가 아닌 그림을 지웠습니다.");
            controller.Bar.Click("색 바꾸기");
            if (sketch.Color == new ColorRgba(255, 59, 48)) throw new InvalidOperationException("색 바꾸기가 색을 바꾸지 않았습니다.");
            await Task.Delay(400);
            var shot = await new CaptureEngine().CaptureMonitorAsync(display.Handle);
            var crop = new System.Windows.Media.Imaging.CroppedBitmap(shot.Image, new Int32Rect(desktop.X - shot.X, desktop.Y - shot.Y, desktop.Width, desktop.Height));
            var pixels = new byte[desktop.Width * desktop.Height * 4]; crop.CopyPixels(pixels, desktop.Width * 4, 0);
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(sketch);
            (byte B, byte G, byte R) At(double x, double y) { int px = (int)(x * dpi.DpiScaleX), py = (int)(y * dpi.DpiScaleY), i = (py * desktop.Width + px) * 4; return (pixels[i], pixels[i + 1], pixels[i + 2]); }
            var edge = At(120, 40); var outside = At(320, 40);
            if (!(edge.R > 180 && edge.G < 110 && edge.B < 110)) throw new InvalidOperationException($"사각형 테두리가 캡처 화면에 없습니다: {edge}");
            if (outside.R > 200 && outside.G > 200) throw new InvalidOperationException($"스포트라이트 바깥이 어둡지 않습니다: {outside}");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
            using (var file = File.Create(Path.Combine(folder, "ui-recording-sketch.png"))) encoder.Save(file);
            Save(controller.Bar, Path.Combine(folder, "ui-recording-bar-drawing.png"));
            sketch.Drawing = false;
            if ((GetWindowLongPtr(handle, -20) & 0x20) == 0) throw new InvalidOperationException("그리기를 끄면 클릭이 다시 아래 앱으로 지나가야 합니다.");
            controller.Stop();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(30)) ?? throw new InvalidOperationException("그리기 녹화가 저장되지 않았습니다.");
            return $"PASS: 7 marks drawn with 6 tools, undo, colour change; empty canvas takes the mouse; marks on the captured screen, spotlight dims outside; click-through toggles; {frameLine}; saved {Path.GetFileName(result.Path)}";
        }
        finally { window.Close(); }
    }

    /// <summary>Editing a saved recording: trim with sound, mute and shrink, a looping GIF, one frame, and the edit window's own save.</summary>
    internal static async Task<string> RunEditAsync(string folder)
    {
        var source = Path.Combine(folder, "recording-synthetic.mp4");
        if (!File.Exists(source)) return "SKIP: 합성 녹화가 없어(인코더 없음) 편집 검사를 건너뜁니다.";
        var output = Path.Combine(folder, "recordings-edit"); if (Directory.Exists(output)) Directory.Delete(output, true); Directory.CreateDirectory(output);
        var probe = await RecordingExport.ProbeAsync(source);
        if (probe.Width != 640 || probe.Height != 360 || Math.Abs(probe.Duration.TotalSeconds - 2.4) > .15) throw new InvalidOperationException($"합성 녹화 정보가 다릅니다: {probe}");
        Mp4Info Read(string path) { using var file = File.OpenRead(path); return Mp4Reader.Read(file) ?? throw new InvalidOperationException(path + "에 MP4 색인이 없습니다."); }
        TimeSpan S(double s) => TimeSpan.FromSeconds(s);

        var trimmed = Path.Combine(output, "trimmed.mp4");
        await RecordingExport.SaveVideoAsync(source, trimmed, new(S(.5), S(1.7)), false, 0, null, CancellationToken.None);
        var t = Read(trimmed); var tv = t.Tracks.First(x => x.Handler == "vide"); var ts = t.Tracks.FirstOrDefault(x => x.Handler == "soun");
        if (Math.Abs(tv.Duration.TotalSeconds - 1.2) > .15 || ts is null || Math.Abs(ts.Duration.TotalSeconds - 1.2) > .15 || tv.Width != 640) throw new InvalidOperationException($"잘라낸 영상이 기대와 다릅니다: {Describe(t)}");

        var small = Path.Combine(output, "small-muted.mp4");
        await RecordingExport.SaveVideoAsync(source, small, new(TimeSpan.Zero, probe.Duration), true, 320, null, CancellationToken.None);
        var m = Read(small); var mv = m.Tracks.First(x => x.Handler == "vide");
        if (m.Tracks.Any(x => x.Handler == "soun") || mv.Width != 320 || mv.Height != 180) throw new InvalidOperationException($"소리 빼고 줄인 영상이 기대와 다릅니다: {Describe(m)}");

        var gifPath = Path.Combine(output, "clip.gif");
        await RecordingExport.SaveGifAsync(source, gifPath, new(TimeSpan.Zero, S(1)), 10, 320, null, CancellationToken.None);
        var gif = GifTiming.Describe(await File.ReadAllBytesAsync(gifPath));
        if (gif != (10, 10, true)) throw new InvalidOperationException($"GIF 정보가 다릅니다: {gif}");

        var still = await RecordingExport.FrameAsync(source, S(1));
        if (still.PixelWidth != 640 || still.PixelHeight != 360) throw new InvalidOperationException("장면 이미지 크기가 다릅니다.");

        BitmapSourceOpened = null;
        var window = new RecordingEditWindow(source, true, image => BitmapSourceOpened = image);
        window.Show();
        try
        {
            for (int i = 0; i < 100 && window.Duration <= TimeSpan.Zero; i++) await Task.Delay(50);
            window.Trim.Range = new(S(.4), S(1.4));
            await Task.Delay(300);
            Save(window, Path.Combine(folder, "ui-recording-edit.png"));
            var saved = await window.SaveAsync(false) ?? throw new InvalidOperationException("편집 창의 MP4 저장이 실패했습니다: " + window.Status);
            if (Path.GetFileName(saved) != "recording-synthetic-편집.mp4" || Math.Abs(Read(saved).Duration.TotalSeconds - 1.0) > .15) throw new InvalidOperationException("편집 창이 고른 구간대로 저장하지 않았습니다: " + Describe(Read(saved)));
            File.Delete(saved);
        }
        finally { window.Close(); }
        return FormattableString.Invariant($"PASS: trim 0.5–1.7s → {tv.Duration.TotalSeconds:0.00}s with sound; muted 320x180 without sound; GIF {gif.Frames} frames at {gif.DelayCentiseconds}/100s looping ({new FileInfo(gifPath).Length / 1024} KB); frame 640x360; edit window saved its range");
    }

    /// <summary>Start-up recovery: a playable leftover moves to the save folder, an unplayable one is removed, nothing else is touched.</summary>
    internal static string RunRecovery(string folder)
    {
        var work = Path.Combine(folder, "recovery-work"); var save = Path.Combine(folder, "recovery-save");
        foreach (var d in new[] { work, save }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }
        var synthetic = Path.Combine(folder, "recording-synthetic.mp4");
        if (!File.Exists(synthetic)) return "SKIP: 합성 녹화가 없어 복구 검사를 건너뜁니다.";
        File.Copy(synthetic, Path.Combine(work, "playable.mp4"));
        var cut = File.ReadAllBytes(synthetic); File.WriteAllBytes(Path.Combine(work, "cut.mp4"), cut[..(cut.Length / 2)]);
        File.WriteAllText(Path.Combine(work, "cut.input"), InputTrack.Header + "\n");
        File.WriteAllText(Path.Combine(work, "notes.txt"), "keep");
        var (recovered, lost) = RecordingRecovery.Run(save, work);
        var left = Directory.GetFiles(work).Select(Path.GetFileName).Order().ToArray();
        // The plain MP4 here keeps its index at the end, so its first half cannot be played.
        if (recovered.Count != 1 || lost != 1 || !left.SequenceEqual(new[] { "notes.txt" }) || !Path.GetFileName(recovered[0]).StartsWith("복구된 녹화", StringComparison.Ordinal))
            throw new InvalidOperationException($"복구 결과가 기대와 다릅니다: 복구 {recovered.Count}, 삭제 {lost}, 남음 {string.Join(",", left)}");
        Directory.Delete(work, true); Directory.Delete(save, true);
        return "PASS: playable leftover recovered to the save folder, cut leftover and its input track removed, other files untouched";
    }

    private static System.Windows.Media.Imaging.BitmapSource? BitmapSourceOpened;
    private static string Describe(Mp4Info info) => string.Join(", ", info.Tracks.Select(t => FormattableString.Invariant($"{t.Handler} {t.Width}x{t.Height} {t.Duration.TotalSeconds:0.00}s")));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);

    /// <summary>The range window covers the region's screen, dims around the region and shows all four corners.</summary>
    private static string CheckFrame(RecordingController controller, PixelRect region, string folder)
    {
        var frame = controller.Frame ?? throw new InvalidOperationException("영역 녹화에 범위 표시가 없습니다.");
        var handle = new WindowInteropHelper(frame).Handle;
        if (!Native.GetWindowRect(handle, out var r) || r.Left != frame.Bounds.X || r.Top != frame.Bounds.Y || r.Width != frame.Bounds.Width || r.Height != frame.Bounds.Height)
            throw new InvalidOperationException($"범위 표시 창의 위치가 기대와 다릅니다: {r.Left},{r.Top} {r.Width}×{r.Height}");
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(frame.Bounds.Width, frame.Bounds.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.PushTransform(new System.Windows.Media.ScaleTransform(frame.Bounds.Width / frame.ActualWidth, frame.Bounds.Height / frame.ActualHeight)); dc.DrawRectangle(new System.Windows.Media.VisualBrush((System.Windows.Media.Visual)frame.Content), null, new Rect(0, 0, frame.ActualWidth, frame.ActualHeight)); }
        bitmap.Render(visual);
        var pixels = new byte[frame.Bounds.Width * frame.Bounds.Height * 4]; bitmap.CopyPixels(pixels, frame.Bounds.Width * 4, 0);
        (byte B, byte G, byte R, byte A) At(int x, int y) { int i = ((y - frame.Bounds.Y) * frame.Bounds.Width + (x - frame.Bounds.X)) * 4; return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]); }
        int reach = FrameLayer.Gap + FrameLayer.Stroke / 2;
        foreach (var (x, y, name) in new[] { (region.X - reach, region.Y - reach, "왼쪽 위"), (region.Right + reach - 1, region.Y - reach, "오른쪽 위"), (region.X - reach, region.Bottom + reach - 1, "왼쪽 아래"), (region.Right + reach - 1, region.Bottom + reach - 1, "오른쪽 아래") })
        {
            var mark = At(x, y);
            if (!(mark.A > 200 && mark.R > 180 && mark.G < 110)) throw new InvalidOperationException($"범위 표시의 {name} 모서리가 보이지 않습니다: {mark}");
        }
        var inside = At(region.X + region.Width / 2, region.Y + region.Height / 2);
        if (inside.A != 0) throw new InvalidOperationException($"녹화 범위 안이 어두워졌습니다: {inside}");
        var outside = new[] { At(region.X + region.Width / 2, region.Bottom + 30), At(region.Right + 30, region.Y + region.Height / 2) };
        if (outside.Any(p => p.A < 40)) throw new InvalidOperationException("녹화 범위 바깥이 어두워지지 않았습니다.");
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(folder, "ui-recording-frame.png"))) encoder.Save(file);
        return "range window covers the screen, four corners shown, outside dimmed, inside clear";
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
