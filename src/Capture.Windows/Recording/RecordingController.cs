using System.IO;
using System.Windows;
using System.Windows.Threading;
using Capture.Core;
using Capture.Windows.Platform;

namespace Capture.Windows.Recording;

internal sealed record RecordingOptions(string Folder, Func<string> Stem, int Fps = 30, bool Cursor = true, int CountdownSeconds = 0, bool SystemSound = false, bool Microphone = false);

/// <summary>
/// One recording as the user sees it: corner marks and the bar appear, an optional countdown runs, then
/// recording starts. Start over throws the take away and records again with the same target; discard
/// throws it away and ends. Only one recording runs at a time.
/// </summary>
internal sealed class RecordingController
{
    private readonly RecordingTarget target;
    private readonly PixelRect desktop;
    private readonly DisplayInfo display;
    private readonly RecordingOptions options;
    private readonly RecordingBar bar = new();
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private RecordingFrame? frame;
    private RecordingJob? job;
    private bool restart, discard, stopBeforeStart;
    private int ticks;

    public static RecordingController? Active { get; private set; }

    /// <summary>Elapsed time while recording, for the tray tooltip; null when it ends.</summary>
    public event Action<TimeSpan?>? Progress;

    public RecordingController(RecordingTarget target, PixelRect desktop, DisplayInfo display, RecordingOptions options)
    {
        this.target = target; this.desktop = desktop; this.display = display; this.options = options;
        bar.PauseToggled += TogglePause;
        bar.StopRequested += Stop;
        bar.RestartRequested += () => { restart = true; job?.Stop(); };
        bar.DiscardRequested += () => { discard = true; if (job is null) stopBeforeStart = true; job?.Stop(); };
        tick.Tick += (_, _) => Refresh();
    }

    internal RecordingBar Bar => bar;
    public TimeSpan Elapsed => job?.Duration ?? TimeSpan.Zero;
    public bool Paused => job?.State == RecordingState.Paused;

    public void TogglePause() { if (job is null) return; if (job.State == RecordingState.Paused) job.Resume(); else job.Pause(); Refresh(); }
    public void Stop() { if (job is null) stopBeforeStart = true; job?.Stop(); }

    /// <summary>Records until stopped. Returns the saved video, or null when discarded or stopped during the countdown.</summary>
    public async Task<RecordingResult?> RunAsync()
    {
        if (Active is not null) throw new InvalidOperationException("이미 녹화 중입니다.");
        Active = this;
        try
        {
            // The full screen needs no marks: the bar and the tray already say it is recording.
            if (target.Kind != RecordingKind.Screen) { frame = new RecordingFrame(desktop); frame.Show(); }
            bar.Show();
            RecordingLook.PlacePhysical(bar, (w, h) => RecordingControls.Place(desktop, w, h, new(display.Work.Left, display.Work.Top, display.Work.Width, display.Work.Height)));
            for (int left = options.CountdownSeconds; left > 0 && !stopBeforeStart; left--) { bar.Countdown(left); await Task.Delay(1000); }
            if (stopBeforeStart) return null;
            while (true)
            {
                restart = false;
                job = new RecordingJob(target, options.Folder, options.Stem(), options.Fps, options.Cursor, options.SystemSound, options.Microphone);
                var run = job.RunAsync();
                await job.Started;
                if (stopBeforeStart) job.Stop();
                tick.Start(); Refresh();
                var result = await run;
                tick.Stop();
                if (restart && !discard) { Delete(result); continue; }
                if (discard) { Delete(result); return null; }
                return result;
            }
        }
        finally
        {
            tick.Stop();
            Active = null;
            bar.Close(); frame?.Close();
            Progress?.Invoke(null);
        }
    }

    private void Refresh()
    {
        if (job is null) return;
        bool paused = job.State == RecordingState.Paused;
        bar.Update(job.Duration, paused, ++ticks % 4 >= 2, job.HasSound ? job.SoundLevel : null, job.MicrophoneQuiet);
        Progress?.Invoke(job.Duration);
    }

    private static void Delete(RecordingResult result)
    {
        foreach (var path in new[] { result.Path, result.InputTrack }) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
