using System.IO;
using System.Runtime.InteropServices;
using Capture.Core;
using Capture.Windows.Platform;
using Windows.Graphics.Capture;

namespace Capture.Windows.Recording;

internal enum RecordingKind { Area, Window, Screen }

/// <summary>What to record. <see cref="Region"/> is relative to the capture item (monitor or window).</summary>
internal sealed record RecordingTarget(RecordingKind Kind, nint Handle, PixelRect Region)
{
    /// <summary>A selection in desktop pixels, recorded inside the monitor that holds most of it.</summary>
    public static RecordingTarget? ForArea(PixelRect selection)
    {
        var displays = Native.Displays();
        var placed = RecordingArea.OnMonitor(selection, displays.Select(d => new PixelRect(d.Bounds.Left, d.Bounds.Top, d.Bounds.Width, d.Bounds.Height)).ToList());
        return placed is null ? null : new(RecordingKind.Area, displays[placed.Value.Monitor].Handle, placed.Value.Region);
    }

    public static RecordingTarget ForScreen(nint monitor)
    {
        var bounds = Native.Displays().First(d => d.Handle == monitor).Bounds;
        return new(RecordingKind.Screen, monitor, new(0, 0, bounds.Width, bounds.Height));
    }

    /// <summary>A window's visible bounds in desktop pixels, without the invisible resize border.</summary>
    public static PixelRect WindowBounds(nint window)
    {
        if (DwmGetWindowAttribute(window, 9, out Native.RECT bounds, Marshal.SizeOf<Native.RECT>()) < 0 && !Native.GetWindowRect(window, out bounds)) return default;
        return new(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Native.RECT value, int size);

    /// <summary>The whole window; its size is read from the capture item when recording starts.</summary>
    public static RecordingTarget ForWindow(nint window) => new(RecordingKind.Window, window, default);
}

internal sealed record RecordingResult(string Path, TimeSpan Duration, int Width, int Height, string InputTrack, bool TargetClosed, bool HasSound = false, IReadOnlyList<string>? SoundProblems = null)
{
    /// <summary>Why the recording ended on its own, in words for the user; null when the user stopped it.</summary>
    public string? EndedBecause { get; init; }
}

/// <summary>
/// One recording from start to saved file: capture and encoding into a temporary file, cursor and click
/// sampling, keeping the display awake, and moving the finished video into the save folder.
/// </summary>
internal sealed class RecordingJob
{
    private readonly RecordingTarget target;
    private readonly string folder, stem;
    private readonly int fps;
    private readonly bool cursor, systemSound, microphoneSound;
    private AudioRecorder? audio;
    private volatile string? endedBecause;
    /// <summary>Free space below which a recording stops and saves rather than fail mid-write.</summary>
    internal static long MinimumFreeBytes { get; set; } = 300L << 20;
    private ScreenRecorder? recorder;
    private (int X, int Y)? monitorOrigin;
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RecordingJob(RecordingTarget target, string folder, string stem, int fps = 30, bool cursor = true, bool systemSound = false, bool microphoneSound = false)
    {
        this.target = target; this.folder = folder; this.stem = stem; this.fps = fps; this.cursor = cursor; this.systemSound = systemSound; this.microphoneSound = microphoneSound;
    }

    /// <summary>Completes when frames are being captured, or faults when recording could not start.</summary>
    public Task Started => started.Task;
    public RecordingState State => recorder?.State ?? RecordingState.Idle;
    public TimeSpan Duration => recorder?.Duration ?? TimeSpan.Zero;
    public void Pause() => recorder?.Pause();
    public void Resume() => recorder?.Resume();
    public void Stop() => recorder?.Stop();
    /// <summary>Latest sound level, 0..1, and whether the microphone has been silent for a while.</summary>
    public float SoundLevel => audio?.Level ?? 0;
    public bool HasSound => audio?.HasSound == true;
    public bool MicrophoneQuiet => audio?.MicrophoneQuiet == true;

    internal static string WorkFolder => Path.Combine(SettingsStore.DirectoryPath, "Recordings");

    public async Task<RecordingResult> RunAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(WorkFolder);
        var id = Guid.NewGuid().ToString("N");
        string temp = Path.Combine(WorkFolder, id + ".mp4"), inputPath = Path.Combine(WorkFolder, id + ".input");
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("이 PC에서는 화면 녹화를 사용할 수 없습니다(Windows Graphics Capture 없음).");
            var item = CaptureEngine.CreateItem(target.Handle, target.Kind == RecordingKind.Window);
            var region = target.Kind == RecordingKind.Window ? new PixelRect(0, 0, item.Size.Width, item.Size.Height) : target.Region;
            ScreenRecorder? current = null;
            if (systemSound || microphoneSound) audio = new AudioRecorder(systemSound, microphoneSound, () => current?.State == RecordingState.Recording);
            current = new ScreenRecorder(item, region, fps, cursor, audio);
            recorder = current;
            var sampler = new InputSampler();
            var sampling = new CancellationTokenSource();
            var samplingThread = new Thread(() => Sample(current, region, sampler, sampling.Token)) { IsBackground = true, Name = "Snaplet recording input" };
            // Locking the screen or sleeping freezes capture; the take so far is saved instead.
            void OnSession(object? _, Microsoft.Win32.SessionSwitchEventArgs e) { if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect) End("화면이 잠겨"); }
            void OnPower(object? _, Microsoft.Win32.PowerModeChangedEventArgs e) { if (e.Mode == Microsoft.Win32.PowerModes.Suspend) End("PC가 절전 모드로 들어가"); }
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSession; Microsoft.Win32.SystemEvents.PowerModeChanged += OnPower;
            try {
            await using (var file = File.Create(temp))
            {
                var recording = current.RunAsync(file.AsRandomAccessStream(), cancellationToken);
                samplingThread.Start();
                _ = Task.Run(async () =>
                {
                    while (current.Metrics.FirstSampleMs < 0 && !recording.IsCompleted) await Task.Delay(10);
                    if (recording.IsFaulted) started.TrySetException(recording.Exception!.InnerExceptions); else started.TrySetResult();
                });
                try { await recording; }
                finally { sampling.Cancel(); samplingThread.Join(); }
                await file.FlushAsync(cancellationToken);
            }
            } finally { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSession; Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPower; }
            await using (var writer = File.CreateText(inputPath)) InputTrack.Write(writer, sampler.Events);
            Directory.CreateDirectory(folder);
            var final = RecordingFiles.UniquePath(folder, stem, ".mp4", File.Exists);
            File.Move(temp, final);
            return new(final, current.Duration, current.Output.Width, current.Output.Height, inputPath, current.TargetClosed, audio?.HasSound == true, audio?.Problems ?? []) { EndedBecause = current.TargetClosed ? "창이 닫혀" : endedBecause };
        }
        catch (Exception error)
        {
            started.TrySetException(error);
            throw;
        }
        finally
        {
            audio?.Dispose();
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Polls the cursor and mouse buttons 60 times a second. No hook is installed, so nothing typed is seen.
    /// Also keeps the display and system awake for the length of the recording.
    /// </summary>
    private void Sample(ScreenRecorder recorder, PixelRect region, InputSampler sampler, CancellationToken token)
    {
        SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
        var drive = new DriveInfo(Path.GetPathRoot(WorkFolder)!);
        int tick = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Every two seconds: stop and keep what was recorded before the disk fills up.
                if (++tick % 120 == 0) try { if (drive.AvailableFreeSpace < MinimumFreeBytes) End("저장 공간이 부족해"); } catch (IOException) { }
                if (recorder.State == RecordingState.Recording && Native.GetCursorPos(out var point) && Origin(region) is { } origin)
                {
                    int pressed = (Down(0x01) ? 1 : 0) | (Down(0x02) ? 2 : 0) | (Down(0x04) ? 4 : 0);
                    sampler.Sample(recorder.Duration, point.X - origin.X, point.Y - origin.Y, pressed);
                }
                token.WaitHandle.WaitOne(16);
            }
        }
        finally { SetThreadExecutionState(EsContinuous); }
    }

    private void End(string reason) { endedBecause ??= reason; recorder?.Stop(); }

    private static bool Down(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    /// <summary>Desktop position of the recorded region's top-left corner. A window is followed as it moves.</summary>
    private (int X, int Y)? Origin(PixelRect region)
    {
        if (target.Kind == RecordingKind.Window)
            return DwmGetWindowAttribute(target.Handle, 9, out Native.RECT bounds, Marshal.SizeOf<Native.RECT>()) >= 0 ? (bounds.Left, bounds.Top) : null;
        if (monitorOrigin is null && Native.Displays().FirstOrDefault(d => d.Handle == target.Handle) is { } monitor) monitorOrigin = (monitor.Bounds.Left + region.X, monitor.Bounds.Top + region.Y);
        return monitorOrigin;
    }

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x1, EsDisplayRequired = 0x2;
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Native.RECT value, int size);
}
