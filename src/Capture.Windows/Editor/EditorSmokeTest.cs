using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>Opt-in Windows renderer verification; never captures the screen or touches the clipboard.</summary>
public static class EditorSmokeTest
{
    public static string Run(string outputDirectory)
    {
        if (!OperatingSystem.IsWindows() || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Editor smoke tests require the Windows WPF STA dispatcher.");
        if (Application.Current is { } app && !app.Dispatcher.CheckAccess())
            throw new InvalidOperationException("Run the editor smoke tests on the application's WPF dispatcher.");
        Directory.CreateDirectory(outputDirectory);
        var checks = new List<string>();
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("Editor smoke test failed: " + label); checks.Add(label); }
        const int width = 32, height = 24;
        var raw = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            byte v = (byte)(((x + y) % 2 == 0) ? 255 : 0);
            raw[i] = v; raw[i + 1] = (byte)(x * 7); raw[i + 2] = (byte)(y * 9); raw[i + 3] = 255;
        }
        // Unusual input DPI must not change source pixel dimensions or coordinates.
        var source = BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgra32, null, raw, width * 4); source.Freeze();
        var doc = new EditableDocument(width, height);
        var baseline = ImageRenderer.Render(source, doc);
        Check(baseline.PixelWidth == width && baseline.PixelHeight == height, "export uses source pixel dimensions regardless of source DPI");
        Check(Bytes(baseline).SequenceEqual(raw), "unmodified image preserves opaque source pixels");
        var red = new ColorRgba(255, 0, 0, 255);
        var line = new Annotation(Guid.NewGuid(), AnnotationKind.Line, new(2, 18), new(28, 18), red, 4);
        doc.Add(line);
        Check(Pixel(ImageRenderer.Render(source, doc), 12, 18) == (0, 0, 255, 255), "vector line reaches expected source pixel");
        doc.Add(new Annotation(Guid.NewGuid(), AnnotationKind.Solid, new(8, 4), new(20, 20), new(0, 0, 0, 255)));
        var redacted = ImageRenderer.Render(source, doc);
        Check(Pixel(redacted, 12, 18) == (0, 0, 0, 255), "opaque redaction covers both source pixels and earlier vector annotations");
        Check(Pixel(redacted, 2, 2) == Pixel(baseline, 2, 2), "redaction leaves pixels outside its rectangle unchanged");
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) ImageRenderer.Paint(dc, source, doc.Annotations);
        var preview = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); preview.Render(visual);
        Check(Bytes(preview).SequenceEqual(Bytes(redacted)), "preview painter and export produce identical pixels without selection handles");
        doc.CropTo(new(8, 4, 12, 16));
        var cropped = ImageRenderer.Render(source, doc);
        Check(cropped.PixelWidth == 12 && cropped.PixelHeight == 16 && Pixel(cropped, 4, 14) == (0, 0, 0, 255), "crop translates annotations and preserves exact output dimensions");
        doc.Undo(); Check(Bytes(ImageRenderer.Render(source, doc)).SequenceEqual(Bytes(redacted)), "undo crop restores original edited bounds and pixels");
        doc.Redo(); doc.ResetCrop();
        Check(doc.Crop == new RectD(0, 0, width, height), "reset crop restores full source extent");
        Check(Bytes(source).SequenceEqual(raw), "crop, redaction and undo never mutate source bitmap");
        foreach (var kind in new[] { AnnotationKind.Pixelate, AnnotationKind.Blur })
        {
            var effectDoc = new EditableDocument(width, height);
            effectDoc.Add(new Annotation(Guid.NewGuid(), kind, new(4, 4), new(16, 16), red, 2));
            var effect = ImageRenderer.Render(source, effectDoc);
            var local = new byte[12 * 12 * 4];
            for (int y = 0; y < 12; y++) Array.Copy(raw, ((y + 4) * width + 4) * 4, local, y * 12 * 4, 12 * 4);
            if (kind == AnnotationKind.Blur) PixelEffects.Blur(local, 12, 12, new(0, 0, 12, 12), 4);
            else PixelEffects.Pixelate(local, 12, 12, new(0, 0, 12, 12), 6);
            var rendered = Bytes(effect);
            bool exact = true;
            for (int y = 0; y < 12; y++) for (int x = 0; x < 12; x++) for (int c = 0; c < 4; c++)
                exact &= rendered[((y + 4) * width + x + 4) * 4 + c] == local[(y * 12 + x) * 4 + c];
            Check(exact, kind + " export matches independently computed core effect pixels including edges");
            Check(Pixel(effect, 3, 3) == Pixel(baseline, 3, 3), kind + " preserves outside pixels");
        }
        var transparent = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16);
        var white = ImageRenderer.FlattenWhite(transparent);
        Check(Pixel(white, 1, 1) == (255, 255, 255, 255), "JPEG preparation flattens transparent pixels onto opaque white");
        var jpeg = new JpegBitmapEncoder { QualityLevel = 95 }; jpeg.Frames.Add(BitmapFrame.Create(white));
        using (var memory = new MemoryStream())
        {
            jpeg.Save(memory); memory.Position = 0;
            var decoded = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var p = Pixel(decoded, 1, 1);
            Check(p.B >= 250 && p.G >= 250 && p.R >= 250, "JPEG encoder output retains white transparency background");
        }
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(redacted));
        using (var file = File.Create(Path.Combine(outputDirectory, "editor-redacted.png"))) png.Save(file);
        // Larger synthetic document verifies geometry beyond the original tiny raster fixture.
        var whitePixels=Enumerable.Repeat((byte)255,640*420*4).ToArray();
        var sheet=BitmapSource.Create(640,420,96,96,PixelFormats.Bgra32,null,whitePixels,640*4);sheet.Freeze();
        var rich=new EditableDocument(640,420);
        var callout=new Annotation(Guid.NewGuid(),AnnotationKind.Text,new(40,30),new(240,90),red,4,"Callout",Tail:new(350,200));
        rich.Add(callout);
        var arrow=AnnotationGeometry.BendThrough(new(Guid.NewGuid(),AnnotationKind.Arrow,new(40,300),new(240,300),red,4),new(140,360));rich.Add(arrow);
        var richImage=ImageRenderer.Render(sheet,rich);
        Check(Pixel(richImage,45,70).R==255 && Pixel(richImage,45,70).G==0,"callout has opaque colored body");
        var tailPixels=0;var targetPixels=0;PointD? paintedTail=null;var calloutTip=ImageRenderer.TailTip(callout);
        for(int y=90;y<202;y++)for(int x=240;x<352;x++) {
            var pixel=Pixel(richImage,x,y);if(pixel.R>240 && pixel.G<30){tailPixels++;paintedTail=new(x+.5,y+.5);if(AnnotationGeometry.Distance(new(x+.5,y+.5),calloutTip)<6)targetPixels++;}
        }
        Check(tailPixels>25 && targetPixels>0,"callout tail paints outside its body and reaches its bounded handle");
        Check(Pixel(richImage,140,360).G<30 && Pixel(richImage,140,300).G==255,"curved arrow uses its control point rather than the straight chord");
        var pointer=new Annotation(Guid.NewGuid(),AnnotationKind.Step,new(450,260),new(450,260),red,4,"7",Tail:new(550,260));rich.Add(pointer);
        Check(Pixel(ImageRenderer.Render(sheet,rich),472,260).G<30,"counter pointer renders outside the numbered circle");
        rich.Replace(pointer with {Style=new(ShowTail:false)});
        Check(Pixel(ImageRenderer.Render(sheet,rich),472,260).G==255,"counter pointer can be hidden independently");
        var shape=new Annotation(Guid.NewGuid(),AnnotationKind.Rectangle,new(380,30),new(550,100),red,4,Style:new(Fill:ShapeFill.Solid));rich.Add(shape);
        Check(Pixel(ImageRenderer.Render(sheet,rich),460,65).G==0,"solid shape fill is included in export");
        rich.Replace(shape with {Style=new(Fill:ShapeFill.Outline)});
        Check(Pixel(ImageRenderer.Render(sheet,rich),460,65).G==255,"outline shape preserves interior source pixels");
        var surface=new EditorSurface(sheet,rich);
        Check(surface.Hit(arrow,new(140,360)) && !surface.Hit(arrow,new(140,300)),"editor curve hit test follows rendered curve");
        Check(!surface.Hit(shape with {Style=new(Fill:ShapeFill.Outline)},new(460,65)),"outline hit test does not capture empty center");
        Check(surface.Hit(callout,new(50,50)),"callout body uses shared render and hit bounds");
        Check(paintedTail is PointD witness && surface.Hit(callout,witness),"callout can be selected by its painted tail");
        Check(surface.Hit(pointer,new(472,260)),"counter can be selected by its painted pointer");
        var openArrow=new Annotation(Guid.NewGuid(),AnnotationKind.Arrow,new(20,200),new(300,200),red,10,Style:new(ArrowHead:ArrowHead.Open));
        Check(surface.Hit(openArrow,new(250,226)),"open arrow can be selected by its curved head wing away from its shaft");
        var dotAnnotation=new Annotation(Guid.NewGuid(),AnnotationKind.Pen,new(30,30),new(30,30),red,4,Points:new[]{new PointD(30,30)});
        Check(surface.Hit(dotAnnotation,new(30,30)) && !surface.Hit(dotAnnotation,new(100,100)),"single-point pen mark remains selectable");
        var edgeText=surface.NewTextAt(new(630,410));
        Check(edgeText.End.X<=640 && edgeText.End.Y<=420 && edgeText.Tail!.Value.Y<=420,"new text and initial tail remain reachable near screenshot edge");
        surface.Selected=pointer.Id;surface.ChangeSelected(a=>a with {Text="7"});surface.DuplicateSelected();
        Check(rich.Annotations.Count(a=>a.Kind==AnnotationKind.Step)==2 && rich.Annotations.Last().Id!=pointer.Id,"duplicate creates an independent editable counter");
        // The reviewed screenshot uses the light theme the reference app shows by default; the dark variant is rendered below.
        EditorTheme.Override=false;
        var window=new EditorWindow(sheet,rich);
        try {
            // A real synthetic WPF window exercises toolbar/inspector/layout construction.
            // RenderTargetBitmap only draws this window's content, never reads the desktop.
            window.Show();window.UpdateLayout();
            var editingSurface=Descendants(window).OfType<EditorSurface>().Single();
            var buttons=Descendants(window).OfType<Button>().ToArray();
            void ClickTool(string tooltip)=>buttons.Single(b=>Equals(b.ToolTip,tooltip)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ClickTool("화살표 · A");editingSurface.Ink=new(35,90,210);editingSurface.Thickness=7;editingSurface.InkStyle=new(ArrowHead:ArrowHead.Open);
            window.SetTool(AnnotationKind.Step);editingSurface.Thickness=9;
            ClickTool("화살표 · A");
            Check(editingSurface.Thickness==7 && editingSurface.Ink==new ColorRgba(35,90,210) && editingSurface.InkStyle.ArrowHead==ArrowHead.Open,"switching tools restores that tool's color size and style");
            editingSurface.Selected=pointer.Id;
            var beforeNumber=rich.Annotations.Single(a=>a.Id==pointer.Id).Text;
            foreach(var digit in new[]{"4","2"}) {
                var composition=new TextComposition(InputManager.Current,editingSurface,digit);
                editingSurface.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice,composition) {RoutedEvent=TextCompositionManager.TextInputEvent});
            }
            Check(rich.Annotations.Single(a=>a.Id==pointer.Id).Text=="42" && rich.NextCounter==43,"consecutive digits edit the selected counter and next number");
            editingSurface.CancelGesture();rich.Undo();
            Check(rich.Annotations.Single(a=>a.Id==pointer.Id).Text==beforeNumber,"two-digit counter typing undoes as one gesture");
            int ShownTools()=>buttons.Count(b=>b.IsVisible);
            bool Shown(string tip)=>buttons.Any(b=>b.IsVisible && Equals(b.ToolTip,tip));
            var editorWidth=window.Width;
            window.Width=1500;window.UpdateLayout();window.UpdateLayout();
            Check(Shown("흐림 · B") && Shown("돋보기 · G") && Shown("불투명 가림"),"a wide editor shows every tool in the toolbar");
            var wideCount=ShownTools();
            window.Width=760;window.UpdateLayout();window.UpdateLayout();
            Check(ShownTools()<wideCount,"a narrow editor folds tools behind ···");
            window.SetTool(AnnotationKind.Magnifier);window.UpdateLayout();
            Check(buttons.Count(b=>b.IsVisible && Equals(b.ToolTip,"돋보기 · G"))==1,"a folded tool picked from ··· takes the slot before ··· like the reference toolbar");
            window.SetTool(null);window.Width=editorWidth;window.UpdateLayout();window.UpdateLayout();
            ClickTool("사각형 · R");editingSurface.Selected=shape.Id;editingSurface.ChangeSelected(a=>a);
            window.InspectorExpanded=true;
            var tailOption=Descendants(window).OfType<CheckBox>().Single(c=>Equals(c.Content,"말꼬리"));
            tailOption.IsChecked=true;tailOption.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(rich.Annotations.Single(a=>a.Id==shape.Id) is {Tail:not null,Appearance.ShapeTail:true},"rectangle inspector enables a tail with a reachable drag target");
            rich.Undo();Check(!rich.Annotations.Single(a=>a.Id==shape.Id).Appearance.ShapeTail,"shape tail checkbox is one undoable edit");
            rich.Redo();window.InspectorExpanded=false;window.UpdateLayout();
            Check(!window.UsesDarkTheme && Capture.Windows.UI.WindowAppearance.GetDarkChrome(window)==false,"light editor theme requests a light title bar");
            var panel=Descendants(window).OfType<Border>().Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="속성 패널");
            var panelAt=panel.TransformToAncestor((Visual)window.Content).Transform(new Point(0,0));
            Check(panel.IsVisible && panelAt.X>((FrameworkElement)window.Content).ActualWidth/2 && panelAt.Y<130,"property panel floats at the top right below the toolbar");
            Check(Descendants(panel).OfType<Slider>().Count(s=>s.IsVisible)==1 && !Descendants(panel).OfType<ComboBox>().Any(c=>c.IsVisible),"compact property panel shows one size slider and keeps secondary controls behind ···");
            var content=(FrameworkElement)window.Content;
            var ui=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);ui.Render(content);
            var uiPng=new PngBitmapEncoder();uiPng.Frames.Add(BitmapFrame.Create(ui));
            using var file=File.Create(Path.Combine(outputDirectory,"editor-ui.png"));uiPng.Save(file);
            Check(content.ActualWidth>600 && content.ActualHeight>300,"compact editor toolbar and workspace construct on Windows");
        } finally {window.CloseForExit();}
        EditorTheme.Override=true;
        var darkWindow=new EditorWindow(sheet,rich);
        try {
            darkWindow.Show();darkWindow.UpdateLayout();
            Check(darkWindow.UsesDarkTheme && Capture.Windows.UI.WindowAppearance.GetDarkChrome(darkWindow),"dark Windows app mode keeps the dark editor theme and title bar");
            var content=(FrameworkElement)darkWindow.Content;
            var ui=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);ui.Render(content);
            var uiPng=new PngBitmapEncoder();uiPng.Frames.Add(BitmapFrame.Create(ui));
            using var file=File.Create(Path.Combine(outputDirectory,"editor-ui-dark.png"));uiPng.Save(file);
        } finally {darkWindow.CloseForExit();EditorTheme.Override=null;}
        var richPng=new PngBitmapEncoder();richPng.Frames.Add(BitmapFrame.Create(ImageRenderer.Render(sheet,rich)));
        using(var file=File.Create(Path.Combine(outputDirectory,"editor-callouts.png")))richPng.Save(file);
        CheckPolishedTools(outputDirectory, Check);
        ShapeCalloutSmokeTest.Run(outputDirectory,Check);
        PointerInteractionSmokeTest.Run(sheet,Check);
        RefinementSmokeTest.Run(outputDirectory,sheet,Check);
        ExperienceSmokeTest.Run(outputDirectory,Check);
        AdvancedToolsSmokeTest.Run(outputDirectory,Check);
        AdvancedInspectorSmokeTest.Run(outputDirectory,Check);
        var report = JsonSerializer.Serialize(new { component = "WPF editor renderer", passed = checks.Count, checks, limitations = new[] { "Uses routed WPF commands/text events, not physical mouse or keyboard input", "Does not verify live screen capture, clipboard interoperability, monitor DPI transitions or visual latency" } }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDirectory, "editor-smoke.json"), report);
        return report;
    }
    private static void CheckPolishedTools(string directory, Action<bool, string> check)
    {
        var red = new ColorRgba(238, 65, 77, 255);
        var blue = new ColorRgba(44, 111, 224, 255);
        const int width = 1200, height = 1040;
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        var annotations = new List<Annotation>();
        var captions = new List<(string Text, Point Position)>();
        var styles = new[] { ArrowHead.Filled, ArrowHead.Open, ArrowHead.Simple, ArrowHead.Double };
        for (int i = 0; i < styles.Length; i++)
        {
            double x = 35 + i * 295;
            captions.Add((styles[i].ToString(), new(x, 22)));
            annotations.Add(new(Guid.NewGuid(), AnnotationKind.Arrow, new(x + 15, 100), new(x + 235, 100), red, 5, Style: new(ArrowHead: styles[i])));
            var bent = AnnotationGeometry.BendThrough(new(Guid.NewGuid(), AnnotationKind.Arrow, new(x + 15, 180), new(x + 235, 180), blue, 5, Style: new(ArrowHead: styles[i])), new(x + 125, 240));
            annotations.Add(bent);
            if (styles[i] is ArrowHead.Filled or ArrowHead.Double)
            {
                var outline = ImageRenderer.ArrowGeometry(bent);
                check(outline.FillContains(new Point(x + 125, 240)), styles[i] + " curved silhouette contains its bend handle");
                check(!outline.FillContains(new Point(x + 125, 180)), styles[i] + " curved silhouette excludes the empty chord");
            }
        }
        var wide = annotations[0];
        check(ImageRenderer.ArrowGeometry(wide).FillContains(new Point(120, 106)), "filled arrow has a broad continuous shaft rather than a thin line with a triangle");
        foreach (var endpoint in new[] { new PointD(30, 30), new PointD(30.01, 30.01), new PointD(31, 31) })
        {
            var tiny = wide with { Start = new(30, 30), End = endpoint };
            check(!ImageRenderer.ArrowGeometry(tiny).Bounds.IsEmpty, "zero or very short filled arrow remains a valid visible path");
        }
        captions.Add(("Counter pointers / rotate the tail handle", new(35, 300)));
        for (int i = 0; i < 4; i++)
        {
            double x = 100 + i * 155;
            var direction = i switch { 0 => new PointD(0, -100), 1 => new PointD(100, 0), 2 => new PointD(0, 100), _ => new PointD(-100, 0) };
            var counter = new Annotation(Guid.NewGuid(), AnnotationKind.Step, new(x, 400), new(x, 400), red, 9, (i + 1).ToString(), Tail: new(x + direction.X, 400 + direction.Y));
            annotations.Add(counter);
            var tip = AnnotationGeometry.CounterTip(counter);
            var inside = new Point((counter.Start.X + tip.X) / 2, (counter.Start.Y + tip.Y) / 2);
            check(ImageRenderer.TailGeometry(counter).FillContains(inside), "counter tail remains selectable when rotated " + i);
        }
        captions.Add(("Rounded callouts / tails on every side", new(35, 480)));
        for (int i = 0; i < 4; i++)
        {
            double x = 60 + i * 290;
            var target = i switch { 0 => new PointD(x + 95, 520), 1 => new PointD(x + 255, 590), 2 => new PointD(x + 95, 690), _ => new PointD(x - 30, 590) };
            var bubble = new Annotation(Guid.NewGuid(), AnnotationKind.Text, new(x, 565), new(x + 190, 625), blue, 4, "Rounded callout", Tail: target);
            annotations.Add(bubble);
            check(!ImageRenderer.TailGeometry(bubble).Bounds.IsEmpty, "callout supports a rounded tail on side " + i);
        }
        captions.Add(("Regular / organic / diagonal hatch / solid", new(35, 715)));
        for (int i = 0; i < 4; i++)
        {
            double x = 45 + i * 295;
            var style = new AnnotationStyle(Fill: i == 2 ? ShapeFill.Hatched : i == 3 ? ShapeFill.Solid : ShapeFill.Outline, Sketch: i == 1);
            var shape = new Annotation(Guid.Parse("1f84d3ec-11f0-4000-9999-23c9650e5501"), AnnotationKind.Rectangle, new(x, 765), new(x + 220, 830), red, 4, Style: style);
            annotations.Add(shape);
            annotations.Add(shape with { Id = Guid.Parse("3aaf139d-458a-4113-a855-9998f823fa65"), Kind = AnnotationKind.Ellipse, Start = new(x + 25, 860), End = new(x + 195, 925) });
            if (i == 1)
                check(ImageRenderer.ShapeGeometry(shape).ToString() == ImageRenderer.ShapeGeometry(shape).ToString(), "organic shape contours are deterministic between preview and export");
        }
        captions.Add(("Smooth freehand strokes / single-point dot", new(35, 960)));
        var ink = new Annotation(Guid.NewGuid(), AnnotationKind.Pen, new(450, 985), new(850, 985), blue, 5,
            Points: new[] { new PointD(450, 985), new PointD(510, 970), new PointD(560, 1000), new PointD(620, 975), new PointD(690, 1005), new PointD(760, 975), new PointD(850, 985) });
        annotations.Add(ink);
        var dot = ink with { Points = new[] { new PointD(920, 985) } };
        annotations.Add(dot);
        var surface = new EditorSurface(source, new EditableDocument(width, height));
        check(surface.Hit(dot, new(920, 985)) && !surface.Hit(dot, new(940, 985)), "single-point pen dot can be selected without selecting empty surrounding space");
        var openHead = annotations[2];
        var headWing = new PointD(openHead.End.X - 25, openHead.End.Y + 13);
        check(surface.Hit(openHead, headWing), "open arrow can be selected by a curved head wing away from its shaft");
        check(ImageRenderer.PenGeometry(ink).StrokeContains(new Pen(Brushes.Black, 2), new Point(560, 1000)), "smoothed freehand path still passes through the captured samples");
        // A semitransparent circle and its overlapping tail must be composited once.
        var opacityCounter = new Annotation(Guid.NewGuid(), AnnotationKind.Step, new(1000, 400), new(1000, 400), red, 9, "8", Tail: new(1100, 400), Style: new(Opacity: .5));
        annotations.Add(opacityCounter);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            ImageRenderer.Paint(dc, source, annotations);
            foreach (var caption in captions)
                dc.DrawText(new FormattedText(caption.Text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 17, Brushes.DimGray, 1), caption.Position);
        }
        var gallery = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        gallery.Render(visual); gallery.Freeze();
        var body = Pixel(gallery, 974, 400); var join = Pixel(gallery, 1026, 400);
        check(Math.Abs(body.G - join.G) <= 1 && body.G > 140 && body.G < 180, "semitransparent counter body and tail share one fill without a dark overlap seam");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(gallery));
        using var file = File.Create(Path.Combine(directory, "editor-tools.png")); encoder.Save(file);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {
            var child=VisualTreeHelper.GetChild(root,i);yield return child;
            foreach(var nested in Descendants(child))yield return nested;
        }
    }
    private static byte[] Bytes(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)]; converted.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
    }
    private static (byte B, byte G, byte R, byte A) Pixel(BitmapSource bitmap, int x, int y)
    {
        var bytes = Bytes(bitmap); int i = (y * bitmap.PixelWidth + x) * 4; return (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
    }
}
