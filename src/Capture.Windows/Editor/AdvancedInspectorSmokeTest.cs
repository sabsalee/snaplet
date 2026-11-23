using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>Synthetic editor-only property-panel snapshots; no desktop capture, clipboard or native input.</summary>
internal static class AdvancedInspectorSmokeTest
{
    internal static void Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var source = Sheet(1000, 600);
        var document = new EditableDocument(1000, 600);
        var red = new ColorRgba(231, 68, 78);
        var image = new Annotation(Guid.NewGuid(), AnnotationKind.Image, new(80, 100), new(300, 220),
            new(255, 255, 255), Image: ImageRenderer.RasterOf(Sheet(220, 120)), Style: new(Opacity: .8));
        var magnifier = new Annotation(Guid.NewGuid(), AnnotationKind.Magnifier, new(400, 250), new(700, 460),
            red, 3, SampleRegion: new(100, 100, 100, 70), Style: new(Magnification: 3, Elliptical: true));
        var spotlight = new Annotation(Guid.NewGuid(), AnnotationKind.Spotlight, new(65, 80), new(320, 235),
            red, Style: new(Dim: .35));
        var highlighter = new Annotation(Guid.NewGuid(), AnnotationKind.Highlight, new(80, 330), new(310, 350),
            new(255, 227, 63), 7, Style: new(HighlightShape: MarkerShape.Line, MarkerCap: MarkerCap.Round));
        var blur = new Annotation(Guid.NewGuid(), AnnotationKind.Blur, new(750, 100), new(900, 160), red);
        foreach (var annotation in new[] { image, magnifier, spotlight, highlighter, blur }) document.Add(annotation);
        var window = new EditorWindow(source, document);
        try
        {
            window.Show(); window.InspectorExpanded = true; window.UpdateLayout();
            var surface = Descendants(window).OfType<EditorSurface>().Single();
            foreach (var annotation in new[] { magnifier, spotlight, highlighter, blur, image })
            {
                surface.Selected = annotation.Id;
                surface.ChangeSelected(a => a);
                window.UpdateLayout();
                var descendants = Descendants(window).ToArray();
                var sliders = descendants.OfType<Slider>().Where(c => c.IsVisible).ToArray();
                // Modes are segmented buttons named after each choice, so every option is one click away.
                var choices = descendants.OfType<Button>().Where(c => c.IsVisible).Select(AutomationProperties.GetName).ToHashSet();
                bool HasChoice(string text) => choices.Contains(text);
                Slider? SliderNamed(string text) => sliders.FirstOrDefault(c => c.ToolTip is string tip && tip.Contains(text, StringComparison.Ordinal));
                switch (annotation.Kind)
                {
                    case AnnotationKind.Magnifier:
                        check(HasChoice("사각 렌즈") && HasChoice("곡선 렌즈"), "magnifier inspector exposes rectangular and curved lens choices");
                        check(SliderNamed("확대 배율") is { ActualWidth: > 0 } zoom && zoom.Value == 3 && zoom.Maximum >= 8,
                            "magnifier inspector exposes its current zoom separately from border thickness");
                        break;
                    case AnnotationKind.Spotlight:
                        check(HasChoice("사각 Spotlight") && HasChoice("타원 Spotlight"), "spotlight inspector exposes both hole shapes");
                        check(SliderNamed("주변 어둡기") is { ActualWidth: > 0 } dim && Math.Abs(dim.Value - .35) < .001,
                            "spotlight inspector exposes current darkness and the digit shortcut");
                        break;
                    case AnnotationKind.Highlight:
                        check(HasChoice("마커 선") && HasChoice("자유 그리기") && HasChoice("사각 영역"),
                            "highlighter inspector makes all three drawing modes discoverable");
                        check(HasChoice("평평한 마감") && HasChoice("둥근 마감") && HasChoice("손그림 마감"),
                            "highlighter inspector makes marker finishing styles discoverable");
                        break;
                    case AnnotationKind.Blur:
                        check(descendants.OfType<Button>().Any(c => c.IsVisible && c.IsEnabled && Equals(c.Content, "텍스트만 처리")),
                            "blur inspector offers the OCR text-only operation without starting OCR");
                        break;
                    case AnnotationKind.Image:
                        var keyboardSlider=SliderNamed("이미지 불투명도")!;
                        keyboardSlider.Focus();
                        keyboardSlider.Value=.65;
                        window.UpdateLayout();
                        check(ReferenceEquals(keyboardSlider,Descendants(window).OfType<Slider>().FirstOrDefault(c=>c.IsKeyboardFocusWithin)),
                            "keyboard slider retains focus while applying an image style");
                        check(Math.Abs(surface.Selection!.Appearance.Opacity-.65)<.001,"focused inspector slider applies its value to the selection");
                        keyboardSlider.Value=.8;
                        surface.Focus();
                        check(SliderNamed("이미지 불투명도") is { ActualWidth: > 0 } opacity && Math.Abs(opacity.Value - .8) < .001,
                            "inserted image inspector exposes its current opacity separately from other sliders");
                        break;
                }
                var root = (FrameworkElement)window.Content;
                check(sliders.Length>0 && sliders.All(c => c.ActualWidth > 0 && c.ActualHeight > 0 &&
                    IsInside(root, c)), annotation.Kind + " inspector controls are laid out inside the visible editor content");
                int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
                check(width > 600 && height > 350, annotation.Kind + " inspector snapshot has usable editor dimensions");
                var screenshot = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                screenshot.Render(root);
                var pixels = new byte[checked(width * height * 4)]; screenshot.CopyPixels(pixels, width * 4, 0);
                check(pixels.Where((_, i) => i % 4 == 3).Count(alpha => alpha > 0) > width * height / 2,
                    annotation.Kind + " inspector snapshot contains painted editor content");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot));
                using var file = File.Create(Path.Combine(directory, $"advanced-inspector-{annotation.Kind.ToString().ToLowerInvariant()}.png"));
                encoder.Save(file);
            }
            // Wheel over a property: each notch is one step, and the whole turn undoes at once.
            surface.Selected = spotlight.Id; surface.ChangeSelected(a => a); window.UpdateLayout();
            var dimSlider = Descendants(window).OfType<Slider>().First(c => c.IsVisible && c.ToolTip is string tip && tip.Contains("주변 어둡기", StringComparison.Ordinal));
            for (int i = 0; i < 2; i++) dimSlider.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            window.FinishWheelEdit(); window.UpdateLayout();
            check(Math.Abs(document.Annotations.Single(x => x.Id == spotlight.Id).Appearance.Dim - .45) < .001, "mouse wheel over a property slider steps its value");
            check(Descendants(window).OfType<TextBox>().Any(c => c.IsVisible && c.Text == "45%"), "fractional properties read as percentages");
            document.Undo();
            check(Math.Abs(document.Annotations.Single(x => x.Id == spotlight.Id).Appearance.Dim - .35) < .001, "a run of wheel steps is one undoable edit");
        }
        finally { window.CloseForExit(); }
    }

    private static bool IsInside(FrameworkElement root, FrameworkElement control)
    {
        var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }

    private static BitmapSource Sheet(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int row = 0; row < 4; row++) for (int col = 0; col < 8; col++)
            {
                var color = Color.FromRgb((byte)(50 + col * 20), (byte)(100 + row * 30), (byte)(220 - col * 16));
                dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(20 + col * 22, 20 + row * 16, 20, 14));
            }
            dc.DrawText(new FormattedText("SAMPLE 123 · Windows editor", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 21, Brushes.Black, 1), new Point(24, Math.Min(height - 30, 260)));
            if (width > 500)
            {
                dc.DrawText(new FormattedText("Magnifier · Spotlight · Marker", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 25, Brushes.DimGray, 1), new Point(60, 500));
                dc.DrawText(new FormattedText("PRIVATE 456", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 20, Brushes.Black, 1), new Point(750, 118));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
