using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Capture.Core;
using Capture.Windows.UI;

namespace Capture.Windows.Recording;

/// <summary>
/// Trims a recording, drops its sound or makes it smaller, and saves the result as a new MP4 or a looping GIF
/// beside the original. A frame can be opened in the image editor. The original is never overwritten.
/// </summary>
internal sealed class RecordingEditWindow : Window
{
    private readonly string source;
    private readonly Action<BitmapSource> editImage;
    private readonly MediaElement video = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
    private readonly TrimBar trim = new();
    private readonly TextBlock time = Ui.Label("0:00 / 0:00", 12, Ui.Brush("MutedBrush"));
    private readonly TextBlock status = Ui.Label("", 12, Ui.Brush("MutedBrush"));
    private readonly ProgressBar progress = new() { Height = 4, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox mute = new() { Content = "소리 빼기", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    private readonly ComboBox size = new() { MinWidth = 130, VerticalAlignment = VerticalAlignment.Center, ItemsSource = new[] { "원본 크기", "긴 변 1920", "긴 변 1280", "긴 변 720" }, SelectedIndex = 0 };
    private readonly Button play, saveVideo, saveGif, frame;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private CancellationTokenSource? work;
    private TimeSpan duration;

    public RecordingEditWindow(string source, bool hasSound, Action<BitmapSource> editImage)
    {
        this.source = source; this.editImage = editImage;
        Editor.EditorTheme.FollowSystem(this);
        Title = $"{System.IO.Path.GetFileName(source)} — Snaplet"; Width = 760; Height = 600; MinWidth = 560; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; FontFamily = Ui.Font;
        mute.IsEnabled = hasSound; mute.ToolTip = hasSound ? "저장할 때 소리를 뺍니다." : "이 녹화에는 소리가 없습니다.";
        AutomationProperties.SetName(size, "저장 크기"); AutomationProperties.SetName(mute, "소리 빼기");
        play = Ui.IconButton("play", "재생 (Space)", TogglePlay, 34);
        saveVideo = Ui.Button("MP4로 저장", () => _ = SaveAsync(false), style: "PrimaryButton");
        saveGif = Ui.Button("GIF로 저장", () => _ = SaveAsync(true));
        frame = Ui.Button("이 장면을 이미지로", () => _ = OpenFrameAsync());
        foreach (var b in new[] { saveGif, frame }) b.Margin = new Thickness(0, 0, 8, 0);

        var root = new DockPanel { Background = Ui.Page, Margin = new Thickness(0) };
        var footer = new StackPanel { Margin = new Thickness(20, 12, 20, 18) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var transport = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(play, Dock.Left); transport.Children.Add(play);
        time.VerticalAlignment = VerticalAlignment.Center; time.Margin = new Thickness(10, 0, 12, 0); DockPanel.SetDock(time, Dock.Right); transport.Children.Add(time);
        trim.Margin = new Thickness(10, 0, 0, 0); transport.Children.Add(trim);
        footer.Children.Add(transport);
        var options = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right);
        actions.Children.Add(frame); actions.Children.Add(saveGif); actions.Children.Add(saveVideo);
        options.Children.Add(actions);
        var left = new StackPanel { Orientation = Orientation.Horizontal }; left.Children.Add(mute); left.Children.Add(size); options.Children.Add(left);
        footer.Children.Add(options);
        footer.Children.Add(progress);
        status.Margin = new Thickness(0, 8, 0, 0); status.TextWrapping = TextWrapping.Wrap; footer.Children.Add(status);
        var stage = new Border { Background = Brushes.Black, Margin = new Thickness(20, 20, 20, 8), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = video };
        root.Children.Add(stage);
        Content = root; WindowFrame.UnifyWithStrip(this);

        trim.Changed += () => { Seek(trim.Dragging == TrimHandle.End ? trim.Range.End : trim.Range.Start); UpdateTime(); };
        trim.Scrubbed += position => Seek(position);
        video.MediaOpened += (_, _) =>
        {
            duration = video.NaturalDuration.HasTimeSpan ? video.NaturalDuration.TimeSpan : TimeSpan.Zero;
            trim.Duration = duration; video.Pause(); video.Position = TimeSpan.Zero; UpdateTime();
        };
        video.MediaFailed += (_, e) => status.Text = "미리보기를 열지 못했습니다. 저장은 계속 할 수 있습니다: " + e.ErrorException?.Message;
        clock.Tick += (_, _) =>
        {
            // Playback stays inside the kept range and loops there, so what is heard is what will be saved.
            if (playing && video.Position >= trim.Range.End) video.Position = trim.Range.Start;
            trim.Playhead = video.Position; UpdateTime();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Space) { TogglePlay(); e.Handled = true; } else if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) =>
        {
            video.Source = new Uri(source); video.Play(); video.Pause();
            // The media element may not report a length on machines without its codecs; the composition always can.
            try { if (duration == TimeSpan.Zero) { var probe = await RecordingExport.ProbeAsync(source); duration = probe.Duration; trim.Duration = duration; UpdateTime(); } } catch (Exception error) { status.Text = error.Message; }
            clock.Start();
        };
        Closed += (_, _) => { clock.Stop(); work?.Cancel(); video.Close(); };
    }

    internal TrimBar Trim => trim;
    internal string Status => status.Text;
    internal TimeSpan Duration => duration;

    private bool playing;
    private void TogglePlay()
    {
        playing = !playing;
        if (playing) { if (video.Position < trim.Range.Start || video.Position >= trim.Range.End) video.Position = trim.Range.Start; video.Play(); }
        else video.Pause();
        play.Content = Ui.Icon(playing ? "pause" : "play"); Ui.BindIcon((FrameworkElement)play.Content, play);
    }

    private void Seek(TimeSpan position) { if (playing) TogglePlay(); video.Position = position; trim.Playhead = position; }

    private void UpdateTime() => time.Text = $"{RecordingControls.Clock(trim.Range.Length)} 남김 · {RecordingControls.Clock(trim.Playhead)} / {RecordingControls.Clock(duration)}";

    internal async Task<string?> SaveAsync(bool gif)
    {
        if (work is not null) return null;
        if (duration <= TimeSpan.Zero) { status.Text = "영상 길이를 아직 읽지 못했습니다."; return null; }
        var folder = System.IO.Path.GetDirectoryName(source)!; var stem = System.IO.Path.GetFileNameWithoutExtension(source) + (gif ? "" : "-편집");
        var target = RecordingFiles.UniquePath(folder, stem, gif ? ".gif" : ".mp4", File.Exists);
        int maxSide = size.SelectedIndex switch { 1 => 1920, 2 => 1280, 3 => 720, _ => 0 };
        if (gif && trim.Range.Length > TimeSpan.FromSeconds(30)) { status.Text = "GIF는 30초까지 만들 수 있습니다. 구간을 줄이거나 MP4로 저장하세요."; return null; }
        work = new CancellationTokenSource();
        SetBusy(true, gif ? "GIF를 만드는 중…" : "MP4를 저장하는 중…");
        try
        {
            var report = new Progress<double>(value => progress.Value = value);
            if (gif) await RecordingExport.SaveGifAsync(source, target, trim.Range, 15, maxSide == 0 ? 800 : Math.Min(maxSide, 800), report, work.Token);
            else await RecordingExport.SaveVideoAsync(source, target, trim.Range, mute.IsChecked == true || !mute.IsEnabled, maxSide, report, work.Token);
            status.Text = $"저장했습니다: {System.IO.Path.GetFileName(target)} ({Size(target)})";
            return target;
        }
        catch (OperationCanceledException) { status.Text = "저장을 취소했습니다."; return null; }
        catch (Exception error) { status.Text = error.Message; return null; }
        finally { SetBusy(false, null); work.Dispose(); work = null; }
    }

    private async Task OpenFrameAsync()
    {
        try { editImage(await RecordingExport.FrameAsync(source, trim.Playhead)); }
        catch (Exception error) { status.Text = "이 장면을 가져오지 못했습니다: " + error.Message; }
    }

    private void SetBusy(bool busy, string? message)
    {
        foreach (var control in new Control[] { saveVideo, saveGif, frame, mute, size }) control.IsEnabled = !busy && (control != mute || mute.ToolTip as string != "이 녹화에는 소리가 없습니다.");
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; progress.Value = 0;
        if (message is not null) status.Text = message;
    }

    private static string Size(string path)
    {
        long bytes = new FileInfo(path).Length;
        return bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    }
}

internal enum TrimHandle { None, Start, End }

/// <summary>A timeline with two handles that mark the kept part and a playhead; clicking the track moves the playhead.</summary>
internal sealed class TrimBar : FrameworkElement
{
    private const double HandleWidth = 10, TrackHeight = 34;
    private TimeSpan duration;
    private TrimRange range;
    private TimeSpan playhead;

    public TrimBar() { Height = TrackHeight; Cursor = Cursors.Hand; Focusable = false; }

    public event Action? Changed;
    public event Action<TimeSpan>? Scrubbed;
    public TrimHandle Dragging { get; private set; }

    public TimeSpan Duration { get => duration; set { duration = value; range = new(TimeSpan.Zero, value); InvalidateVisual(); Changed?.Invoke(); } }
    public TrimRange Range { get => range; set { range = TrimRange.Clamp(value.Start, value.End, duration, false); InvalidateVisual(); Changed?.Invoke(); } }
    public TimeSpan Playhead { get => playhead; set { playhead = value; InvalidateVisual(); } }

    private double X(TimeSpan t) => duration <= TimeSpan.Zero ? HandleWidth : HandleWidth + (ActualWidth - 2 * HandleWidth) * t.Ticks / duration.Ticks;
    private TimeSpan T(double x) => duration <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks((long)(Math.Clamp((x - HandleWidth) / Math.Max(1, ActualWidth - 2 * HandleWidth), 0, 1) * duration.Ticks));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        double x = e.GetPosition(this).X;
        Dragging = Math.Abs(x - (X(range.Start) - HandleWidth / 2)) <= HandleWidth ? TrimHandle.Start : Math.Abs(x - (X(range.End) + HandleWidth / 2)) <= HandleWidth ? TrimHandle.End : TrimHandle.None;
        if (Dragging == TrimHandle.None) Scrubbed?.Invoke(T(x));
        CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var t = T(e.GetPosition(this).X);
        if (Dragging == TrimHandle.Start) { range = TrimRange.Clamp(t, range.End, duration, true); InvalidateVisual(); Changed?.Invoke(); }
        else if (Dragging == TrimHandle.End) { range = TrimRange.Clamp(range.Start, t, duration, false); InvalidateVisual(); Changed?.Invoke(); }
        else Scrubbed?.Invoke(t);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); Dragging = TrimHandle.None; }

    protected override void OnRender(DrawingContext dc)
    {
        var track = (Brush)FindResource("WorkspaceBrush"); var accent = (Brush)FindResource("AccentBrush");
        double top = 4, height = ActualHeight - 8, left = X(range.Start), right = X(range.End);
        dc.DrawRoundedRectangle(track, null, new Rect(HandleWidth, top, Math.Max(0, ActualWidth - 2 * HandleWidth), height), 4, 4);
        var kept = new SolidColorBrush(Color.FromArgb(70, 56, 110, 230)); kept.Freeze();
        dc.DrawRectangle(kept, new Pen(accent, 2), new Rect(left, top, Math.Max(0, right - left), height));
        dc.DrawRoundedRectangle(accent, null, new Rect(left - HandleWidth, top, HandleWidth, height), 3, 3);
        dc.DrawRoundedRectangle(accent, null, new Rect(right, top, HandleWidth, height), 3, 3);
        var grip = new Pen(Brushes.White, 1.5);
        foreach (var x in new[] { left - HandleWidth / 2, right + HandleWidth / 2 }) dc.DrawLine(grip, new Point(x, top + height / 2 - 6), new Point(x, top + height / 2 + 6));
        double p = X(playhead);
        dc.DrawLine(new Pen((Brush)FindResource("TextBrush"), 2), new Point(p, 0), new Point(p, ActualHeight));
    }
}
