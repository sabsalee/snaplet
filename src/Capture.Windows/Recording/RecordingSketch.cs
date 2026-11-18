using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Capture.Core;
using Capture.Windows.Editor;
using Capture.Windows.Platform;

namespace Capture.Windows.Recording;

internal enum SketchTool { Arrow, Rectangle, Pen, Highlight, Step, Spotlight }

/// <summary>
/// Drawing on the screen while recording. A transparent window over the recorded region is captured with the
/// video, so marks appear in it; the recording bar stays excluded. While drawing, the window takes the mouse;
/// otherwise every click passes through to the app below. Marks use the editor's own renderer.
/// </summary>
internal sealed class RecordingSketch : Window
{
    private readonly SketchCanvas canvas;
    private nint handle;
    private bool drawing;

    public RecordingSketch(PixelRect region)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; Title = "Snaplet 녹화 중 그리기";
        // Almost clear: fully transparent pixels would not receive the mouse while drawing.
        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
        canvas = new SketchCanvas();
        Content = canvas;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(handle, -1, region.X, region.Y, region.Width, region.Height, 0x0010);
            SetPassThrough(true);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Drawing = false;
            else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) canvas.Undo();
            else if (e.Key == Key.Delete) canvas.Clear();
        };
    }

    public event Action<bool>? DrawingChanged;
    internal SketchCanvas Canvas => canvas;
    public SketchTool Tool { get => canvas.Tool; set => canvas.Tool = value; }
    public ColorRgba Color { get => canvas.Color; set => canvas.Color = value; }
    /// <summary>Marks fade out this long after they are drawn; zero keeps them.</summary>
    public TimeSpan FadeAfter { get => canvas.FadeAfter; set => canvas.FadeAfter = value; }

    public bool Drawing
    {
        get => drawing;
        set
        {
            if (drawing == value) return;
            drawing = value;
            SetPassThrough(!value);
            if (value) { Activate(); Focus(); }
            Cursor = value ? Cursors.Cross : null;
            DrawingChanged?.Invoke(value);
        }
    }

    public void Undo() => canvas.Undo();
    public void Clear() => canvas.Clear();

    private void SetPassThrough(bool through)
    {
        if (handle == 0) return;
        // WS_EX_TRANSPARENT lets clicks fall to the app below; WS_EX_NOACTIVATE keeps focus there.
        var style = GetWindowLongPtr(handle, -20) | 0x80;
        style = through ? style | 0x20 | 0x08000000 : style & ~(nint)0x20 & ~(nint)0x08000000;
        SetWindowLongPtr(handle, -20, style);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}

/// <summary>Collects marks from mouse drags and paints them with <see cref="ImageRenderer"/>.</summary>
internal sealed class SketchCanvas : FrameworkElement
{
    private readonly List<(Annotation Mark, DateTime Drawn)> marks = new();
    private readonly DispatcherTimer fade = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Annotation? current;
    private int steps;

    public SketchCanvas()
    {
        fade.Tick += (_, _) => Fade();
        Focusable = true;
    }

    public SketchTool Tool { get; set; } = SketchTool.Arrow;
    public ColorRgba Color { get; set; } = new(255, 59, 48);
    public TimeSpan FadeAfter { get; set; }
    public int Count => marks.Count;
    internal IReadOnlyList<Annotation> Marks => marks.Select(m => m.Mark).ToList();

    public void Undo() { if (marks.Count == 0) return; if (marks[^1].Mark.Kind == AnnotationKind.Step) steps--; marks.RemoveAt(marks.Count - 1); InvalidateVisual(); }
    public void Clear() { marks.Clear(); steps = 0; InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var at = e.GetPosition(this); var p = new PointD(at.X, at.Y);
        Begin(p);
        CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (current is null || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(this); Extend(new PointD(at.X, at.Y));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); End(); }

    /// <summary>Starts a mark at <paramref name="p"/>; a number is placed immediately.</summary>
    internal void Begin(PointD p)
    {
        var style = new AnnotationStyle(HighlightShape: MarkerShape.Freehand, MarkerCap: MarkerCap.Round, Dim: .45);
        current = Tool switch
        {
            SketchTool.Arrow => new(Guid.NewGuid(), AnnotationKind.Arrow, p, p, Color, 6, Style: style),
            SketchTool.Rectangle => new(Guid.NewGuid(), AnnotationKind.Rectangle, p, p, Color, 5, Style: style with { CornerRadius = 6 }),
            SketchTool.Pen => new(Guid.NewGuid(), AnnotationKind.Pen, p, p, Color, 5, Points: [p]),
            SketchTool.Highlight => new(Guid.NewGuid(), AnnotationKind.Highlight, p, p, new(255, 214, 10), 5, Points: [p], Style: style),
            SketchTool.Step => new(Guid.NewGuid(), AnnotationKind.Step, p, p, Color, 4, Text: (++steps).ToString(System.Globalization.CultureInfo.InvariantCulture), Style: style with { ShowTail = false }),
            _ => new(Guid.NewGuid(), AnnotationKind.Spotlight, p, p, new(0, 0, 0), 2, Style: style),
        };
        if (Tool == SketchTool.Step) End(); else InvalidateVisual();
    }

    internal void Extend(PointD p)
    {
        if (current is null) return;
        current = current.Kind is AnnotationKind.Pen or AnnotationKind.Highlight
            ? current with { End = p, Points = [.. current.Points ?? [], p] }
            : current with { End = p };
        InvalidateVisual();
    }

    internal void End()
    {
        if (current is null) return;
        bool tiny = current.Kind is not (AnnotationKind.Step or AnnotationKind.Pen or AnnotationKind.Highlight) && Math.Abs(current.End.X - current.Start.X) < 3 && Math.Abs(current.End.Y - current.Start.Y) < 3;
        if (!tiny) marks.Add((current, DateTime.UtcNow));
        current = null;
        if (FadeAfter > TimeSpan.Zero) fade.Start();
        InvalidateVisual();
    }

    private void Fade()
    {
        var now = DateTime.UtcNow;
        int removed = marks.RemoveAll(m => now - m.Drawn > FadeAfter + TimeSpan.FromMilliseconds(400));
        bool fading = marks.Any(m => now - m.Drawn > FadeAfter);
        if (removed > 0 || fading) InvalidateVisual();
        if (marks.Count == 0) fade.Stop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var now = DateTime.UtcNow;
        var shown = marks.Select(m => (m.Mark, Opacity: FadeAfter > TimeSpan.Zero ? Math.Clamp(1 - (now - m.Drawn - FadeAfter).TotalMilliseconds / 400, 0, 1) : 1)).ToList();
        if (current is not null) shown.Add((current, 1));
        // Spotlight dims everything outside its holes, so it is painted once for all spotlights, under the other marks.
        var spotlights = shown.Where(m => m.Mark.Kind == AnnotationKind.Spotlight).ToList();
        if (spotlights.Count > 0) { dc.PushOpacity(spotlights.Max(s => s.Opacity)); AdvancedDrawing.DrawSpotlights(dc, bounds, spotlights.Select(s => s.Mark)); dc.Pop(); }
        foreach (var (mark, opacity) in shown.Where(m => m.Mark.Kind != AnnotationKind.Spotlight))
        {
            dc.PushOpacity(opacity);
            ImageRenderer.PaintAnnotation(dc, Empty, mark);
            dc.Pop();
        }
    }

    /// <summary>The renderer's source image is only read by effects that sketching does not offer.</summary>
    private static readonly System.Windows.Media.Imaging.BitmapSource Empty = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
}
