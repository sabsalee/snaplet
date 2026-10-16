using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>Synthetic WPF checks: no desktop capture, clipboard access or external files.</summary>
internal static class AdvancedToolsSmokeTest
{
    internal static void Run(string outputDir, Action<bool, string> check)
    {
        var red = new ColorRgba(235, 65, 78);
        var blue = new ColorRgba(38, 105, 222);
        var white = Sheet(120, 100);
        var gridBytes = Enumerable.Repeat((byte)255, 120 * 100 * 4).ToArray();
        for (int y = 20; y < 40; y++) for (int x = 20; x < 40; x++)
            Set(gridBytes, 120, x, y, x < 30 ? new(240, 30, 40) : new(25, 80, 230));
        var grid = Bitmap(gridBytes, 120, 100);
        var lens = new Annotation(Guid.NewGuid(), AnnotationKind.Magnifier, new(60, 40), new(100, 80), blue, 2,
            SampleRegion: new(20, 20, 20, 20), Style: new(Magnification: 2));
        var lensDoc = new EditableDocument(120, 100); lensDoc.Add(lens);
        var magnified = ImageRenderer.Render(grid, lensDoc);
        check(Pixel(magnified, 70, 60).R > 225 && Pixel(magnified, 90, 60).B > 215, "magnifier samples its separate source rectangle into the lens");
        check(Pixel(magnified, 70, 60) == Pixel(grid, 25, 30) && Pixel(magnified, 90, 60) == Pixel(grid, 35, 30), "magnifier enlargement preserves interior source colors");
        check(Pixel(magnified, 110, 90) == Pixel(white, 110, 90), "magnifier does not paint outside its lens and source decoration");
        lensDoc.Replace(lens with { Style = lens.Appearance with { Elliptical = true } });
        var roundLens = ImageRenderer.Render(grid, lensDoc);
        check(Pixel(roundLens, 62, 42).R == 255 && Pixel(roundLens, 70, 60).R > 225, "elliptical magnifier clips image corners while preserving its interior");
        var fallback = AdvancedDrawing.SampleBounds(lens with { SampleRegion = null });
        check(fallback.Width == 20 && fallback.Height == 20 && fallback.X == 70 && fallback.Y == 50, "magnifier fallback derives centered sampling bounds from its zoom factor");

        var insertPixels = Enumerable.Repeat((byte)255, 20 * 20 * 4).ToArray();
        for (int y = 0; y < 20; y++) for (int x = 0; x < 20; x++) Set(insertPixels, 20, x, y, new(38, 190, 115));
        var inserted = new Annotation(Guid.NewGuid(), AnnotationKind.Image, new(20, 20), new(40, 40), blue, Image: new RasterImage(20, 20, insertPixels));
        var insertedDoc = new EditableDocument(120, 100); insertedDoc.Add(inserted); insertedDoc.Add(lens);
        var insertedLens = ImageRenderer.Render(white, insertedDoc);
        check(Pixel(insertedLens, 80, 60).G == 190 && Pixel(insertedLens, 80, 60).R == 38, "magnifier samples pasted image layers rather than only the original capture");
        var imageCopy = insertPixels.ToArray(); var asset = new RasterImage(20, 20, imageCopy); imageCopy[0] = 0;
        check(asset.CopyPixels().SequenceEqual(insertPixels), "pasted image raster owns its immutable source pixels");
        var surface = new EditorSurface(white, insertedDoc);
        check(surface.Hit(lens, new(80, 60)) && !surface.Hit(lens, new(110, 60)), "magnifier selection follows its lens bounds");

        var spotlightDoc = new EditableDocument(120, 100);
        spotlightDoc.Add(new(Guid.NewGuid(), AnnotationKind.Spotlight, new(20, 20), new(60, 60), blue, Style: new(Dim: .6)));
        spotlightDoc.Add(new(Guid.NewGuid(), AnnotationKind.Spotlight, new(45, 30), new(85, 70), blue, Style: new(Dim: .6)));
        var lit = ImageRenderer.Render(white, spotlightDoc);
        check(Pixel(lit, 25, 25).R == 255 && Pixel(lit, 80, 65).R == 255, "multiple spotlight regions each reveal the original image");
        check(Pixel(lit, 50, 40).R == 255, "overlapping spotlight holes remain fully revealed");
        check(Pixel(lit, 5, 5).R is >= 100 and <= 104 && Pixel(lit, 5, 5).A == 255, "spotlight dims the outside exactly once with the requested opacity");
        spotlightDoc.Replace(spotlightDoc.Annotations[0] with { Style = new(Dim: .6, Elliptical: true) });
        check(Pixel(ImageRenderer.Render(white, spotlightDoc), 21, 21).R < 120, "elliptical spotlight leaves its rectangle corners dimmed");
        spotlightDoc.Delete(spotlightDoc.Annotations[1].Id); spotlightDoc.Undo();
        check(Pixel(ImageRenderer.Render(white, spotlightDoc), 80, 65).R == 255, "undo restores a removed spotlight hole in the grouped mask");

        var spotlightSurface=new EditorSurface(white,spotlightDoc) {Selected=spotlightDoc.Annotations[0].Id};
        spotlightSurface.SetSpotlightDim(.3);
        check(spotlightDoc.Annotations.All(a=>a.Appearance.Dim==.3) && Pixel(ImageRenderer.Render(white,spotlightDoc),5,5).R>170,"spotlight strength changes the shared outside mask consistently");
        spotlightDoc.Undo();check(spotlightDoc.Annotations.All(a=>a.Appearance.Dim==.6),"one undo restores the previous shared spotlight strength");
        var marker = new Annotation(Guid.NewGuid(), AnnotationKind.Highlight, new(25, 50), new(85, 50), new(255, 180, 0), 4);
        var markerDoc = new EditableDocument(120, 100); markerDoc.Add(marker);
        var flat = ImageRenderer.Render(white, markerDoc);
        check(Pixel(flat, 50, 50).B is > 150 and < 200 && Pixel(flat, 50, 50).R == 255, "marker remains translucent over the screenshot");
        check(Pixel(flat, 20, 50).B == 255 && Pixel(flat, 30, 50).B < 200, "flat marker cap ends at its drawing start");
        markerDoc.Replace(marker with { Style = new(MarkerCap: MarkerCap.Round) });
        check(Pixel(ImageRenderer.Render(white, markerDoc), 20, 50).B < 200, "round marker cap extends smoothly beyond its endpoint");
        var sketchMarker = marker with { Style = new(MarkerCap: MarkerCap.Sketch) };
        markerDoc.Replace(sketchMarker);
        check(Pixel(ImageRenderer.Render(white, markerDoc), 24, 50).B < 200, "sketch marker adds a visible irregular nib beyond its endpoint");
        check(AdvancedDrawing.MarkerGeometry(sketchMarker).ToString() == AdvancedDrawing.MarkerGeometry(sketchMarker.Move(0, 0)).ToString(), "sketch marker nib is deterministic across redraw and export");
        var freehand = marker with { Points = new[] { new PointD(20, 70), new PointD(50, 25), new PointD(90, 70) }, Style = new(HighlightShape: MarkerShape.Freehand, MarkerCap: MarkerCap.Round) };
        markerDoc.Replace(freehand);
        var freehandImage = ImageRenderer.Render(white, markerDoc);
        check(Pixel(freehandImage, 50, 25).B < 200 && Pixel(freehandImage, 50, 65).B == 255, "freehand marker follows its curved samples instead of filling their bounding rectangle");
        var rectangular = marker with { Start = new(20, 20), End = new(80, 60), Style = new(HighlightShape: MarkerShape.Rectangle) };
        markerDoc.Replace(rectangular);
        check(Pixel(ImageRenderer.Render(white, markerDoc), 50, 40).B < 200, "rectangle marker keeps area highlighting available as a separate mode");

        var textPixels = Enumerable.Repeat((byte)255, 120 * 100 * 4).ToArray();
        for (int y = 35; y < 60; y++) for (int x = 35; x < 70; x++)
            if ((x / 4) % 2 == 0 || y > 55) Set(textPixels, 120, x, y, new(15, 20, 25));
        var textSheet = Bitmap(textPixels, 120, 100); var eraseDoc = new EditableDocument(120, 100);
        eraseDoc.Add(new(Guid.NewGuid(), AnnotationKind.Erase, new(30, 30), new(75, 65), red));
        var erased = ImageRenderer.Render(textSheet, eraseDoc);
        check(Pixel(erased, 40, 40) == (255, 255, 255, 255) && Pixel(erased, 50, 58) == (255, 255, 255, 255), "erase renderer reconstructs a white background over screenshot text");
        check(Pixel(erased, 20, 20) == Pixel(textSheet, 20, 20), "erase renderer preserves pixels outside its selection");
        eraseDoc.Undo(); check(Pixel(ImageRenderer.Render(textSheet, eraseDoc), 40, 40).R == 15, "undo erase restores original text pixels");
        eraseDoc.Redo(); check(Pixel(ImageRenderer.Render(textSheet, eraseDoc), 40, 40).R == 255, "redo reapplies erase without mutating the original source");
        check(Pixel(textSheet, 40, 40).R == 15, "erase never changes the captured bitmap asset");
        var fullErase = new EditableDocument(120, 100); fullErase.Add(new(Guid.NewGuid(), AnnotationKind.Erase, new(0, 0), new(120, 100), red));
        check(Bytes(ImageRenderer.Render(textSheet, fullErase)).SequenceEqual(Bytes(textSheet)), "entire-image erase retains content when no surrounding background exists");
        var compositedErase = new EditableDocument(120, 100);
        compositedErase.Add(new(Guid.NewGuid(), AnnotationKind.Image, new(0, 0), new(120, 100), red, Image: ImageRenderer.RasterOf(textSheet)));
        compositedErase.Add(eraseDoc.Annotations[0]);
        check(Pixel(ImageRenderer.Render(white, compositedErase), 40, 40).R == 255, "erase samples the composited pasted image background");
        var compositedBlur = new EditableDocument(120, 100); compositedBlur.Add(compositedErase.Annotations[0]);
        compositedBlur.Add(new(Guid.NewGuid(), AnnotationKind.Blur, new(30, 30), new(75, 65), red, 5));
        var blurredPixel = Pixel(ImageRenderer.Render(white, compositedBlur), 40, 40);
        check(blurredPixel.R > 20 && blurredPixel.R < 245, "blur processes pasted screenshot pixels instead of the white base capture");
        compositedBlur.Replace(compositedBlur.Annotations[1] with { Kind = AnnotationKind.Pixelate });
        var pixelated = Pixel(ImageRenderer.Render(white, compositedBlur), 40, 40);
        check(pixelated.R > 20 && pixelated.R < 245, "pixelation processes pasted screenshot pixels instead of the base capture");

        var insertionDoc = new EditableDocument(120, 100); var window = new EditorWindow(white, insertionDoc);
        try
        {
            var larger = Sheet(160, 120, new(38, 190, 115));
            window.AddCapturedImage(larger);
            check(insertionDoc.Annotations.Count == 1 && insertionDoc.Annotations[0].Kind == AnnotationKind.Image, "capture insertion creates an editable image object in the same document");
            check(insertionDoc.Canvas == new RectD(-20, -10, 160, 120) && insertionDoc.Crop == insertionDoc.Canvas, "larger inserted capture expands the canvas into negative source coordinates");
            var expanded = ImageRenderer.Render(white, insertionDoc);
            check(expanded.PixelWidth == 160 && expanded.PixelHeight == 120 && Pixel(expanded, 5, 5).G == 190, "expanded image export preserves native inserted pixel dimensions and origin");
            insertionDoc.Undo();
            check(insertionDoc.Annotations.Count == 0 && insertionDoc.Canvas == new RectD(0, 0, 120, 100), "one undo removes both the inserted capture and its canvas expansion");
            insertionDoc.Redo();
            check(Bytes(ImageRenderer.Render(white, insertionDoc)).SequenceEqual(Bytes(expanded)), "redo restores inserted pixels and expanded canvas exactly");
            var duplicateSurface=new EditorSurface(white,insertionDoc) {Selected=insertionDoc.Annotations[0].Id};
            duplicateSurface.DuplicateSelected();
            check(insertionDoc.Canvas.Right==156 && insertionDoc.Canvas.Bottom==126,"duplicating an edge image expands the canvas for its offset");
            insertionDoc.Undo();check(insertionDoc.Annotations.Count==1 && insertionDoc.Canvas==new RectD(-20,-10,160,120),"duplicate canvas expansion undoes together with the image object");
            insertionDoc.Add(new(Guid.NewGuid(), AnnotationKind.Solid, new(10, 10), new(30, 30), red));
            window.AddCapturedImage(Sheet(160, 120, new(25, 80, 230)));
            check(Pixel(ImageRenderer.Render(white, insertionDoc), 40, 30).R == red.R, "new inserted captures remain below existing vector annotations");
        }
        finally { window.CloseForExit(); }
        var textDoc=new EditableDocument(120,100);var area=new Annotation(Guid.NewGuid(),AnnotationKind.Erase,new(25,25),new(90,75),red);textDoc.Add(area);
        var textWindow=new EditorWindow(textSheet,textDoc);
        try {
            var selected=textDoc.Annotations[0];textWindow.ApplyTextRegions(selected,selected.Bounds,new[]{new Rect(8,8,12,26),new Rect(28,8,16,26)});
            check(textDoc.Annotations.Count==2 && textDoc.Annotations.All(a=>a.Kind==AnnotationKind.Erase && a.Bounds.Width<area.Bounds.Width),"OCR text-only conversion keeps effects within detected word rectangles");
            textDoc.Undo();check(textDoc.Annotations.Count==1 && textDoc.Annotations[0].Id==area.Id,"one undo restores the original area effect after text-only conversion");
            textWindow.ApplyTextRegions(textDoc.Annotations[0],area.Bounds,Array.Empty<Rect>());
            check(textDoc.Annotations.Count==1 && textDoc.Annotations[0].Id==area.Id,"empty text detection leaves the existing effect unchanged");
            textDoc.Delete(area.Id);textWindow.ApplyTextRegions(selected,area.Bounds,new[]{new Rect(8,8,12,26)});
            check(textDoc.Annotations.Count==0,"late OCR results cannot recreate a deleted effect");
        } finally {textWindow.CloseForExit();}
        SaveGallery(outputDir, white, grid, textSheet, red, blue);
    }

    private static void SaveGallery(string outputDir, BitmapSource white, BitmapSource grid, BitmapSource textSheet, ColorRgba red, ColorRgba blue)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(241, 244, 249)), null, new Rect(0, 0, 1200, 920));
            Label(dc, "SNAPLET / EXTENDED EDITING TOOLS", new(30, 22), 25, Brushes.Black);
            Label(dc, "Synthetic source images · shared preview and export renderer", new(30, 60), 14, Brushes.DimGray);
            void Card(int col, int row, string title, string detail, Action<DrawingContext> draw)
            {
                double x = 28 + col * 585, y = 100 + row * 267;
                dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(222, 228, 237)), 1), new Rect(x, y, 560, 246), 14, 14);
                Label(dc, title, new(x + 20, y + 15), 18, Brushes.Black);
                Label(dc, detail, new(x + 20, y + 43), 12, Brushes.DimGray);
                dc.PushClip(new RectangleGeometry(new Rect(x + 15, y + 69, 530, 165), 8, 8));
                dc.PushTransform(new TranslateTransform(x + 15, y + 69)); draw(dc); dc.Pop(); dc.Pop();
            }
            Card(0, 0, "Magnifier", "Separate source frame · 3× lens · ellipse or rectangle", p =>
            {
                var sheet = Sheet(530, 165); var doc = new EditableDocument(530, 165);
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Image, new(20, 25), new(260, 155), red, Image: ImageRenderer.RasterOf(grid)));
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Magnifier, new(330, 14), new(470, 154), blue, 2, SampleRegion: new(60, 53, 47, 47), Style: new(Magnification: 3, Elliptical: true)));
                p.DrawImage(ImageRenderer.Render(sheet, doc), new Rect(0, 0, 530, 165));
            });
            Card(1, 0, "Spotlight", "Multiple regions share one dimming mask", p =>
            {
                var sheet = Sheet(530, 165); var doc = new EditableDocument(530, 165);
                for (int i = 0; i < 5; i++) doc.Add(new(Guid.NewGuid(), AnnotationKind.Rectangle, new(20 + i * 102, 30), new(104 + i * 102, 135), i % 2 == 0 ? blue : red, Style: new(Fill: ShapeFill.Solid)));
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Spotlight, new(26, 20), new(125, 145), blue, Style: new(Dim: .7, Elliptical: true)));
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Spotlight, new(220, 20), new(325, 145), blue, Style: new(Dim: .7)));
                p.DrawImage(ImageRenderer.Render(sheet, doc), new Rect(0, 0, 530, 165));
            });
            Card(0, 1, "Highlighter", "Flat · round · sketch nibs · curved freehand", p =>
            {
                var sheet = Sheet(530, 165); var doc = new EditableDocument(530, 165);
                for (int i = 0; i < 3; i++) doc.Add(new(Guid.NewGuid(), AnnotationKind.Highlight, new(30, 30 + i * 45), new(230, 30 + i * 45), new(255, 184, 32), 5, Style: new(MarkerCap: (MarkerCap)i)));
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Highlight, new(290, 120), new(490, 120), new(58, 189, 153), 6, Points: new[] { new PointD(290, 120), new PointD(340, 40), new PointD(395, 105), new PointD(445, 35), new PointD(490, 120) }, Style: new(HighlightShape: MarkerShape.Freehand, MarkerCap: MarkerCap.Round)));
                p.DrawImage(ImageRenderer.Render(sheet, doc), new Rect(0, 0, 530, 165));
            });
            Card(1, 1, "Erase", "Local background reconstruction · undo keeps the source", p =>
            {
                p.DrawImage(textSheet, new Rect(20, 10, 190, 150));
                var doc = new EditableDocument(120, 100); doc.Add(new(Guid.NewGuid(), AnnotationKind.Erase, new(30, 30), new(75, 65), red));
                p.DrawImage(ImageRenderer.Render(textSheet, doc), new Rect(315, 10, 190, 150));
                p.DrawLine(new Pen(ImageRenderer.BrushOf(blue), 3), new(236, 80), new(280, 80));
                p.DrawLine(new Pen(ImageRenderer.BrushOf(blue), 3), new(270, 70), new(280, 80));
                p.DrawLine(new Pen(ImageRenderer.BrushOf(blue), 3), new(270, 90), new(280, 80));
            });
            Card(0, 2, "Paste Image / Add Capture", "Movable image objects · native pixels · growing canvas", p =>
            {
                var sheet = Sheet(530, 165); var doc = new EditableDocument(530, 165);
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Image, new(18, 15), new(250, 150), blue, Image: ImageRenderer.RasterOf(grid)));
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Image, new(190, 28), new(490, 150), red, Image: ImageRenderer.RasterOf(textSheet)));
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Arrow, new(65, 125), new(345, 80), red, 3, Control: new(180, 20)));
                p.DrawImage(ImageRenderer.Render(sheet, doc), new Rect(0, 0, 530, 165));
            });
            Card(1, 2, "Effects on inserted captures", "Blur and pixelation work on the composed document", p =>
            {
                var sheet = Sheet(530, 165); var doc = new EditableDocument(530, 165);
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Image, new(15, 8), new(245, 158), blue, Image: ImageRenderer.RasterOf(textSheet)));
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Image, new(280, 8), new(510, 158), red, Image: ImageRenderer.RasterOf(textSheet)));
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Blur, new(66, 50), new(168, 112), blue, 5));
                doc.Add(new(Guid.NewGuid(), AnnotationKind.Pixelate, new(332, 50), new(433, 112), red, 3));
                p.DrawImage(ImageRenderer.Render(sheet, doc), new Rect(0, 0, 530, 165));
            });
        }
        var image = new RenderTargetBitmap(1200, 920, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(outputDir, "advanced-tools.png")); png.Save(file);
    }

    private static void Label(DrawingContext dc, string text, Point position, double size, Brush ink) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, ink, 1), position);
    private static BitmapSource Sheet(int width, int height, ColorRgba? color = null)
    {
        var data = new byte[width * height * 4]; var fill = color ?? new ColorRgba(255, 255, 255);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) Set(data, width, x, y, fill);
        return Bitmap(data, width, height);
    }
    private static void Set(byte[] data, int width, int x, int y, ColorRgba c)
    {
        int i = (y * width + x) * 4; data[i] = c.B; data[i + 1] = c.G; data[i + 2] = c.R; data[i + 3] = c.A;
    }
    private static BitmapSource Bitmap(byte[] bytes, int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4); image.Freeze(); return image;
    }
    private static byte[] Bytes(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; converted.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
    }
    private static (byte B, byte G, byte R, byte A) Pixel(BitmapSource bitmap, int x, int y)
    {
        var bytes = Bytes(bitmap); int i = (y * bitmap.PixelWidth + x) * 4; return (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
    }
}
