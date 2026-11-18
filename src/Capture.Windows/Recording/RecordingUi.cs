using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Capture.Core;
using Capture.Windows.Platform;
using Capture.Windows.UI;

namespace Capture.Windows.Recording;

/// <summary>Shared look of the recording windows: always dark, like the selection overlay, so it reads on any content.</summary>
internal static class RecordingLook
{
    public static readonly SolidColorBrush Surface = Frozen(Color.FromArgb(242, 18, 22, 30)), Edge = Frozen(Color.FromArgb(90, 255, 255, 255)),
        Hover = Frozen(Color.FromArgb(38, 255, 255, 255)), Text = Frozen(Colors.White), Muted = Frozen(Color.FromArgb(170, 255, 255, 255)),
        Live = Frozen(Color.FromRgb(255, 69, 58)), Paused = Frozen(Color.FromRgb(255, 179, 64));
    private static SolidColorBrush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    public static Button Button(string glyph, string label, Action action, Brush? foreground = null)
    {
        var face = new FrameworkElementFactory(typeof(Border), "face");
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        face.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = face };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Border.BackgroundProperty, Hover, "face")); template.Triggers.Add(hover);
        var button = new Button { Template = template, Width = 30, Height = 30, Focusable = false, Cursor = Cursors.Hand, Foreground = foreground ?? Text, ToolTip = label, Content = Ui.Icon(glyph, 16) };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    public static Border Divider() => new() { Width = 1, Height = 18, Margin = new Thickness(5, 0, 5, 0), Background = Edge };

    /// <summary>Keeps a window out of every screen capture, including the recording itself.</summary>
    public static void ExcludeFromCapture(Window window) => window.SourceInitialized += (_, _) => Native.SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, 0x11);

    /// <summary>Moves a window to physical desktop pixels once it knows its own size.</summary>
    public static void PlacePhysical(Window window, Func<int, int, (int X, int Y)> place)
    {
        var handle = new WindowInteropHelper(window).Handle; if (handle == 0) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        var (x, y) = place(width, height);
        Native.SetWindowPos(handle, -1, x, y, 0, 0, 0x0001 | 0x0010);
    }
}

/// <summary>
/// The small bar shown while recording: state light and elapsed time, pause, stop and save, start over, and discard.
/// It is excluded from the video, can be dragged anywhere, and never takes focus from the app being recorded.
/// </summary>
internal sealed class RecordingBar : Window
{
    private readonly TextBlock time = new() { Text = "0:00", Foreground = RecordingLook.Text, FontSize = 13, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), MinWidth = 40, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Left };
    private readonly Ellipse light = new() { Width = 9, Height = 9, Fill = RecordingLook.Live, Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button pause, stop;
    private readonly ContentControl sound = new() { Content = Ui.Icon("mic", 14), Foreground = RecordingLook.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0), Visibility = Visibility.Collapsed, ToolTip = "소리 녹음 중" };
    private readonly TextBlock warning = new() { Text = "마이크 소리 없음", Foreground = RecordingLook.Paused, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0), Visibility = Visibility.Collapsed };
    private bool paused;

    public event Action? PauseToggled, StopRequested, RestartRequested, DiscardRequested;

    public RecordingBar()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; SizeToContent = SizeToContent.WidthAndHeight; FontFamily = Ui.Font;
        Title = "Snaplet 녹화";
        pause = RecordingLook.Button("pause", "일시정지", () => PauseToggled?.Invoke());
        stop = RecordingLook.Button("stop", "정지하고 저장", () => StopRequested?.Invoke(), RecordingLook.Live);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var grip = new ContentControl { Content = Ui.Grip(), Foreground = RecordingLook.Muted, Cursor = Cursors.SizeAll, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        row.Children.Add(grip); row.Children.Add(light); row.Children.Add(time); row.Children.Add(sound); row.Children.Add(warning);
        row.Children.Add(RecordingLook.Divider());
        row.Children.Add(pause); row.Children.Add(stop);
        row.Children.Add(RecordingLook.Divider());
        row.Children.Add(RecordingLook.Button("sync", "처음부터 다시", () => RestartRequested?.Invoke()));
        row.Children.Add(RecordingLook.Button("close", "취소하고 버리기", () => DiscardRequested?.Invoke()));
        Content = new Border { Child = row, Background = RecordingLook.Surface, BorderBrush = RecordingLook.Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(5, 4, 5, 4), Margin = new Thickness(8), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = .35 } };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not DependencyObject source || FindButton(source) is null) DragMove(); };
        RecordingLook.ExcludeFromCapture(this);
    }

    public bool Paused => paused;

    public void Update(TimeSpan elapsed, bool isPaused, bool blink, float? soundLevel = null, bool microphoneQuiet = false)
    {
        // The microphone glyph brightens with the sound level, so a silent recording is noticed early.
        sound.Visibility = soundLevel is null ? Visibility.Collapsed : Visibility.Visible;
        sound.Opacity = .35 + .65 * Math.Clamp(soundLevel ?? 0, 0, 1);
        warning.Visibility = microphoneQuiet && !isPaused ? Visibility.Visible : Visibility.Collapsed;
        time.Text = RecordingControls.Clock(elapsed);
        if (isPaused != paused)
        {
            paused = isPaused;
            pause.Content = Ui.Icon(paused ? "play" : "pause", 16);
            pause.ToolTip = paused ? "이어서 녹화" : "일시정지";
            System.Windows.Automation.AutomationProperties.SetName(pause, (string)pause.ToolTip);
        }
        light.Fill = paused ? RecordingLook.Paused : RecordingLook.Live;
        light.Opacity = paused || !blink ? 1 : .35;
    }

    /// <summary>Shows a countdown number in place of the time before recording starts.</summary>
    public void Countdown(int seconds) { time.Text = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture); light.Fill = RecordingLook.Muted; }

    internal void Click(string label)
    {
        foreach (var button in Buttons(Content as DependencyObject))
            if (System.Windows.Automation.AutomationProperties.GetName(button) == label) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); return; }
        throw new InvalidOperationException("녹화 막대에 버튼이 없습니다: " + label);
    }

    private static IEnumerable<Button> Buttons(DependencyObject? root)
    {
        if (root is null) yield break;
        if (root is Button button) yield return button;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var found in Buttons(child)) yield return found;
    }

    private static Button? FindButton(DependencyObject source)
    {
        for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is Button button) return button;
        return null;
    }
}

/// <summary>Red corner marks just outside the recorded region. Click-through and excluded from the video.</summary>
internal sealed class RecordingFrame : Window
{
    private const int Gap = 4, ArmLength = 18, Stroke = 3;

    public RecordingFrame(PixelRect region)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; IsHitTestVisible = false; Title = "Snaplet 녹화 범위";
        var canvas = new Canvas();
        Content = canvas;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Transparent to the mouse and never activated, so the recorded app keeps every click.
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x20 | 0x80 | 0x08000000);
            Native.SetWindowDisplayAffinity(handle, 0x11);
            int x = region.X - Gap - Stroke, y = region.Y - Gap - Stroke, w = region.Width + 2 * (Gap + Stroke), h = region.Height + 2 * (Gap + Stroke);
            Native.SetWindowPos(handle, -1, x, y, w, h, 0x0010);
            var dpi = VisualTreeHelper.GetDpi(this);
            double width = w / dpi.DpiScaleX, height = h / dpi.DpiScaleY, t = Stroke / dpi.DpiScaleX, arm = ArmLength / dpi.DpiScaleX;
            void Corner(double cx, double cy, double dx, double dy)
            {
                canvas.Children.Add(new Rectangle { Width = arm, Height = t, Fill = RecordingLook.Live, RadiusX = t / 2, RadiusY = t / 2, Margin = new Thickness(dx < 0 ? cx - arm : cx, dy < 0 ? cy - t : cy, 0, 0) });
                canvas.Children.Add(new Rectangle { Width = t, Height = arm, Fill = RecordingLook.Live, RadiusX = t / 2, RadiusY = t / 2, Margin = new Thickness(dx < 0 ? cx - t : cx, dy < 0 ? cy - arm : cy, 0, 0) });
            }
            Corner(0, 0, 1, 1); Corner(width, 0, -1, 1); Corner(0, height, 1, -1); Corner(width, height, -1, -1);
        };
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}

/// <summary>
/// After a recording: a muted preview that plays while the pointer is over it, the length and size, and
/// copy, show in folder and close. The video itself can be dragged into other apps.
/// </summary>
internal sealed class RecordingPopup : Window
{
    public RecordingPopup(RecordingResult result)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; SizeToContent = SizeToContent.WidthAndHeight; FontFamily = Ui.Font; Title = "Snaplet 녹화 완료";
        double previewWidth = 300, previewHeight = Math.Clamp(previewWidth * result.Height / Math.Max(1, result.Width), 90, 300);
        var placeholder = new Border { Background = new SolidColorBrush(Color.FromRgb(36, 40, 48)), Child = new ContentControl { Content = Ui.Icon("record", 28), Foreground = RecordingLook.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var video = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
        video.MediaOpened += (_, _) => { placeholder.Visibility = Visibility.Collapsed; video.Pause(); };
        video.MediaEnded += (_, _) => { video.Position = TimeSpan.Zero; video.Play(); };
        var stage = new Grid { Width = previewWidth, Height = previewHeight, ClipToBounds = true, Cursor = Cursors.Hand, ToolTip = "끌어서 다른 앱에 놓기" };
        stage.Children.Add(placeholder); stage.Children.Add(video);
        stage.MouseEnter += (_, _) => video.Play();
        stage.MouseLeave += (_, _) => { video.Pause(); video.Position = TimeSpan.Zero; };
        Point? pressed = null;
        stage.MouseLeftButtonDown += (_, e) => pressed = e.GetPosition(stage);
        stage.MouseMove += (_, e) =>
        {
            if (pressed is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(stage);
            if (Math.Abs(now.X - start.X) < 4 && Math.Abs(now.Y - start.Y) < 4) return;
            pressed = null;
            DragDrop.DoDragDrop(stage, new DataObject(DataFormats.FileDrop, new[] { result.Path }), DragDropEffects.Copy);
        };
        stage.MouseLeftButtonUp += (_, _) => pressed = null;
        var info = new TextBlock { Text = $"{RecordingControls.Clock(result.Duration)} · {result.Width} × {result.Height} · {Size(result.Path)}", Foreground = RecordingLook.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        var actions = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(RecordingLook.Button("copy", "파일 복사", () => CopyFile(result.Path)));
        buttons.Children.Add(RecordingLook.Button("folder", "폴더에서 보기", () => ShowInFolder(result.Path)));
        buttons.Children.Add(RecordingLook.Button("close", "닫기", Close));
        DockPanel.SetDock(buttons, Dock.Right); actions.Children.Add(buttons); actions.Children.Add(info);
        var body = new StackPanel(); body.Children.Add(new Border { Child = stage, CornerRadius = new CornerRadius(6), ClipToBounds = true }); body.Children.Add(actions);
        Content = new Border { Child = body, Background = RecordingLook.Surface, BorderBrush = RecordingLook.Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(6), Margin = new Thickness(10), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = .4 } };
        stage.Clip = new RectangleGeometry(new Rect(0, 0, previewWidth, previewHeight), 6, 6);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => { try { video.Source = new Uri(result.Path); video.Play(); } catch (Exception) { } };
        Closed += (_, _) => video.Close();
    }

    /// <summary>Puts the video file on the clipboard, the way Explorer copies a file.</summary>
    public static void CopyFile(string path)
    {
        var files = new StringCollection { path };
        for (int attempt = 0; ; attempt++)
        {
            try { Clipboard.SetFileDropList(files); return; }
            catch (ExternalException) when (attempt < 4) { Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private static void ShowInFolder(string path) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    private static string Size(string path)
    {
        long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        return bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    }
}
