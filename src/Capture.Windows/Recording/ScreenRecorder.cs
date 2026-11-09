using System.Diagnostics;
using Capture.Core;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace Capture.Windows.Recording;

internal sealed record RecordingRegion(int X, int Y, int Width, int Height);

internal sealed class RecordingMetrics
{
    public double PrepareMs, FirstFrameMs = -1, FirstSampleMs = -1, CopyMs, MaxCopyMs;
    public int FramesArrived, FramesCopied, FramesSuperseded, SamplesNew, SamplesRepeated;
    public long SlotsMissed;
}

/// <summary>
/// Phase 0 recorder: Windows Graphics Capture frames are cropped on the GPU and pulled by the built-in
/// encoder through a MediaStreamSource. Timestamps come from <see cref="RecordingSession"/>, so paused
/// time is removed, and the last frame is repeated when the screen does not change.
/// </summary>
internal sealed class ScreenRecorder
{
    private readonly GraphicsCaptureItem item;
    private readonly RecordingRegion region;
    private readonly int fps;
    private readonly bool cursor;
    private readonly RecordingSession session = new();
    private readonly Stopwatch clock = new();
    private readonly object gate = new();
    private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDirect3DSurface? latest;
    private bool fresh, stopRequested;
    private long lastSlot = -1;
    private D3D11? d3d;

    public ScreenRecorder(GraphicsCaptureItem item, RecordingRegion region, int fps, bool cursor)
    {
        this.item = item; this.region = region; this.fps = fps; this.cursor = cursor;
        Output = RecordingOutput.Size(region.Width, region.Height);
    }

    public (int Width, int Height) Output { get; }
    public RecordingMetrics Metrics { get; } = new();
    public TimeSpan Elapsed => clock.Elapsed;
    public TimeSpan Duration { get { lock (gate) return session.MediaTime(clock.Elapsed); } }
    public int Pauses { get { lock (gate) return session.Pauses; } }

    public void Pause() { lock (gate) session.Pause(clock.Elapsed); }
    public void Resume() { lock (gate) session.Resume(clock.Elapsed); Wake(); }
    public void Stop() { lock (gate) stopRequested = true; Wake(); }

    public async Task RunAsync(IRandomAccessStream output, CancellationToken cancellationToken)
    {
        using var device = D3D11.Create();
        d3d = device;
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var capture = pool.CreateCaptureSession(item);
        capture.IsCursorCaptureEnabled = cursor;
        var source = new MediaStreamSource(new VideoStreamDescriptor(VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)Output.Width, (uint)Output.Height))) { BufferTime = TimeSpan.Zero, CanSeek = false };
        source.SampleRequested += OnSampleRequested;
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var preparing = Stopwatch.StartNew();
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output, Profile(Output.Width, Output.Height, fps, false));
        Metrics.PrepareMs = preparing.Elapsed.TotalMilliseconds;
        if (!prepared.CanTranscode) throw new InvalidOperationException("인코더를 준비하지 못했습니다: " + prepared.FailureReason);
        pool.FrameArrived += OnFrame;
        using var registration = cancellationToken.Register(Stop);
        try
        {
            lock (gate) { clock.Start(); session.Start(clock.Elapsed); }
            capture.StartCapture();
            await prepared.TranscodeAsync();
        }
        finally
        {
            pool.FrameArrived -= OnFrame;
            lock (gate) { latest?.Dispose(); latest = null; d3d = null; }
        }
    }

    /// <summary>A CPU copy of the newest frame, for checking what the video contains.</summary>
    public async Task<SoftwareBitmap?> SnapshotAsync()
    {
        IDirect3DSurface? copy;
        lock (gate) copy = latest is null || d3d is null ? null : d3d.CopyRegion(latest, 0, 0, Output.Width, Output.Height, Output.Width, Output.Height);
        if (copy is null) return null;
        using (copy) return await SoftwareBitmap.CreateCopyFromSurfaceAsync(copy);
    }

    internal static MediaEncodingProfile Profile(int width, int height, int fps, bool audio)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)width; profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)fps; profile.Video.FrameRate.Denominator = 1;
        // About 0.12 bits per pixel: 1080p30 ≈ 7.5 Mbps, enough for screen text.
        profile.Video.Bitrate = (uint)Math.Clamp(width * (long)height * fps * 12 / 100, 500_000, 40_000_000);
        profile.Audio = audio ? AudioEncodingProperties.CreateAac(48000, 2, 128000) : null;
        return profile;
    }

    private void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame is null) return;
        D3D11? device;
        lock (gate)
        {
            Metrics.FramesArrived++;
            if (Metrics.FirstFrameMs < 0) Metrics.FirstFrameMs = clock.Elapsed.TotalMilliseconds;
            if (session.State != RecordingState.Recording || stopRequested) return;
            device = d3d;
        }
        if (device is null) return;
        var copying = Stopwatch.StartNew();
        var copy = device.CopyRegion(frame.Surface, region.X, region.Y, Output.Width, Output.Height, frame.ContentSize.Width, frame.ContentSize.Height);
        var copyMs = copying.Elapsed.TotalMilliseconds;
        lock (gate)
        {
            Metrics.FramesCopied++; Metrics.CopyMs += copyMs; Metrics.MaxCopyMs = Math.Max(Metrics.MaxCopyMs, copyMs);
            if (fresh) Metrics.FramesSuperseded++;
            // Samples already handed to the encoder hold their own reference to the texture.
            latest?.Dispose();
            latest = copy; fresh = true;
        }
        Wake();
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        var deferral = request.GetDeferral();
        _ = SupplyAsync(request, deferral);
    }

    private async Task SupplyAsync(MediaStreamSourceSampleRequest request, MediaStreamSourceSampleRequestDeferral deferral)
    {
        try
        {
            while (true)
            {
                Task wake; TimeSpan delay = TimeSpan.FromMilliseconds(200);
                lock (gate)
                {
                    if (stopRequested)
                    {
                        session.Stop(clock.Elapsed);
                        request.Sample = null;
                        return;
                    }
                    if (session.State == RecordingState.Recording && latest is not null)
                    {
                        var media = session.MediaTime(clock.Elapsed);
                        long slot = RecordingOutput.Slot(media, fps);
                        if (slot > lastSlot)
                        {
                            if (lastSlot >= 0) Metrics.SlotsMissed += slot - lastSlot - 1;
                            if (fresh) Metrics.SamplesNew++; else Metrics.SamplesRepeated++;
                            if (Metrics.FirstSampleMs < 0) Metrics.FirstSampleMs = clock.Elapsed.TotalMilliseconds;
                            lastSlot = slot; fresh = false;
                            var sample = MediaStreamSample.CreateFromDirect3D11Surface(latest, RecordingOutput.SlotTime(slot, fps));
                            sample.Duration = RecordingOutput.SlotTime(1, fps);
                            request.Sample = sample;
                            return;
                        }
                        delay = RecordingOutput.SlotTime(lastSlot + 1, fps) - media;
                        if (delay < TimeSpan.FromMilliseconds(1)) delay = TimeSpan.FromMilliseconds(1);
                    }
                    wake = signal.Task;
                }
                await Task.WhenAny(wake, Task.Delay(delay)).ConfigureAwait(false);
            }
        }
        finally { deferral.Complete(); }
    }

    private void Wake()
    {
        TaskCompletionSource previous;
        lock (gate) { previous = signal; signal = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        previous.TrySetResult();
    }
}
