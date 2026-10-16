using Capture.Core;

internal static class AdvancedModelChecks
{
    internal static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); }
            catch (ArgumentException) { rejected = true; }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, message);
        }

        byte[] pixels = [10, 20, 30, 255, 40, 50, 60, 128];
        var asset = new RasterImage(2, 1, pixels);
        pixels[0] = 250;
        check(asset.CopyPixels()[0] == 10, "Raster asset must own caller-provided BGRA pixels.");
        var exported = asset.CopyPixels(); exported[4] = 240;
        check(asset.CopyPixels()[4] == 40 && asset.CopyPixels()[7] == 128,
            "Returned raster bytes must be independent and preserve alpha.");
        Reject(() => new RasterImage(2, 1, new byte[4]), "Raster rejects incomplete BGRA data.");
        Reject(() => new RasterImage(0, 1, []), "Raster rejects empty dimensions.");
        Reject(() => new RasterImage(int.MaxValue, int.MaxValue, []), "Raster dimension multiplication cannot overflow past allocation limits.");
        Reject(() => new RasterImage(8001, 8000, []), "Raster rejects assets above 64 million pixels.");

        var image = new Annotation(Guid.NewGuid(), AnnotationKind.Image, new(10, 20), new(70, 50),
            new(255, 255, 255), Image: asset, Style: new(Opacity: .5));
        var lens = image with { Id = Guid.NewGuid(), Kind = AnnotationKind.Magnifier,
            Image = null, SampleRegion = new(2, 4, 12, 8), Style = new(Magnification: 3) };
        var shiftedLens = lens.Move(-15, 30);
        check(shiftedLens.Start == new PointD(-5, 50) && shiftedLens.End == new PointD(55, 80),
            "Moving a magnifier translates its destination geometry.");
        check(shiftedLens.SampleRegion == lens.SampleRegion,
            "Moving a magnifier leaves its sampled source region fixed.");
        var duplicate = shiftedLens with { Id = Guid.NewGuid() };
        check(duplicate.SampleRegion == lens.SampleRegion && duplicate.Id != lens.Id,
            "Duplicated magnifiers can independently move while retaining the intended source region.");
        var shiftedImage = image.Move(-25, 7);
        check(ReferenceEquals(shiftedImage.Image, asset), "Moving an image shares its immutable raster asset.");

        var invalidStyleDoc=new EditableDocument(100,80);
        Reject(()=>invalidStyleDoc.Add(image with {Style=new(CornerRadius:double.NaN)}),"Corner radius rejects non-finite values.");
        Reject(()=>invalidStyleDoc.Add(image with {Style=new(CornerRadius:49)}),"Corner radius enforces supported bounds.");
        Reject(()=>invalidStyleDoc.Add(image with {Style=new(TailWidth:0)}),"Tail width rejects zero width.");
        Reject(()=>invalidStyleDoc.Add(image with {Style=new(TailWidth:double.PositiveInfinity)}),"Tail width rejects non-finite values.");
        var doc = new EditableDocument(100, 80);
        doc.Add(image);
        doc.Replace(shiftedImage);
        doc.Undo();
        check(doc.Annotations.Single().Start == image.Start && ReferenceEquals(doc.Annotations.Single().Image, asset),
            "Undo restores image placement without copying or replacing its asset.");
        doc.Redo();
        check(doc.Annotations.Single().Start == shiftedImage.Start && ReferenceEquals(doc.Annotations.Single().Image, asset),
            "Redo retains the same immutable image asset.");
        doc.Add(image with { Id = Guid.NewGuid() });
        check(doc.Annotations.All(a => ReferenceEquals(a.Image, asset)), "Duplicate image objects safely share one asset.");
        doc.Delete(image.Id); doc.Undo();
        check(doc.Annotations.Count == 2 && doc.Annotations.All(a => ReferenceEquals(a.Image, asset)),
            "Undo image deletion restores object and shared raster ownership.");

        var expanded = new EditableDocument(100, 80);
        var initial = expanded.Canvas;
        expanded.BeginEdit();
        expanded.Add(image with { Start = new(-20, -10), End = new(130, 100) });
        expanded.ExpandToInclude(new(-20, -10, 150, 110));
        expanded.EndEdit();
        var extent = new RectD(-20, -10, 150, 110);
        check(expanded.Canvas == extent && expanded.Crop == extent,
            "Adding an outside image expands both canvas and export crop into negative coordinates.");
        check(expanded.Width == 100 && expanded.Height == 80, "Canvas expansion does not change original source dimensions.");
        expanded.Undo();
        check(expanded.Annotations.Count == 0 && expanded.Canvas == initial && expanded.Crop == initial && !expanded.CanUndo,
            "One undo reverses the complete image-add and canvas-expand gesture.");
        expanded.Redo();
        check(expanded.Annotations.Count == 1 && expanded.Canvas == extent && expanded.Crop == extent,
            "Redo restores the expanded document and image together.");
        expanded.CropTo(new(-100, -100, 90, 100));
        check(expanded.Crop == new RectD(-20, -10, 10, 10), "Crop clips to the expanded canvas, including its negative origin.");
        expanded.ResetCrop();
        check(expanded.Crop == expanded.Canvas, "Reset crop restores all expanded image content.");
        expanded.CropTo(new(10, 10, 20, 20));
        expanded.ExpandToInclude(new(50, 40, 10, 10));
        check(expanded.Canvas == extent && expanded.Crop == new RectD(10, 10, 50, 40),
            "Adding within the canvas can reopen a prior crop without changing canvas dimensions.");
        expanded.Undo();
        check(expanded.Crop == new RectD(10, 10, 20, 20), "Undo restores crop before reopening an existing canvas area.");
        expanded.BeginEdit(); expanded.ExpandToInclude(new(-30, -20, 10, 10)); expanded.CancelEdit();
        check(expanded.Canvas == extent && expanded.Crop == new RectD(10, 10, 20, 20),
            "Cancelling an expansion gesture restores both canvas and crop.");

        foreach (var bounds in new[] { new RectD(double.NaN, 0, 10, 10), new RectD(0, 0, double.PositiveInfinity, 10),
            new RectD(0, 0, 0, 20), new RectD(-double.MaxValue, 0, double.MaxValue, 20),
            new RectD(-32768, 0, 1, 1), new RectD(0, 0, 8001, 8000) })
        {
            var beforeCanvas = expanded.Canvas; var beforeCrop = expanded.Crop;
            var beforeUndo = expanded.CanUndo; var beforeRedo = expanded.CanRedo; int events = 0;
            EventHandler changed = (_, _) => events++; expanded.Changed += changed;
            Reject(() => expanded.ExpandToInclude(bounds), "Invalid or excessive expanded bounds must be rejected.");
            expanded.Changed -= changed;
            check(expanded.Canvas == beforeCanvas && expanded.Crop == beforeCrop && events == 0 &&
                expanded.CanUndo == beforeUndo && expanded.CanRedo == beforeRedo,
                "Rejected expansion preserves crop, canvas, history branches and notifications.");
        }

        var validation = new EditableDocument(100, 80);
        var invalid = new[] {
            image with { Image = null },
            lens with { SampleRegion = new(double.NaN, 0, 10, 10) },
            lens with { SampleRegion = new(0, 0, -1, 10) },
            lens with { SampleRegion = new(0, 0, 10, double.PositiveInfinity) },
            lens with { Style = new(Magnification: .99) },
            lens with { Style = new(Magnification: 8.01) },
            lens with { Style = new(Magnification: double.NaN) },
            lens with { Style = new(Dim: -.01) },
            lens with { Style = new(Dim: 1.01) },
            lens with { Style = new(Dim: double.PositiveInfinity) },
            lens with { Style = new(HighlightShape: (MarkerShape)999) },
            lens with { Style = new(MarkerCap: (MarkerCap)(-1)) }
        };
        int invalidEvents = 0; validation.Changed += (_, _) => invalidEvents++;
        foreach (var annotation in invalid)
        {
            Reject(() => validation.Add(annotation), "Invalid advanced annotation field must be rejected.");
            check(validation.Annotations.Count == 0 && !validation.CanUndo && !validation.CanRedo && invalidEvents == 0,
                "Rejected advanced annotation must not alter the document or history.");
        }
        validation.Add(lens with { Style = new(Magnification: 1, Dim: 0, HighlightShape: MarkerShape.Freehand, MarkerCap: MarkerCap.Sketch) });
        validation.Add(lens with { Id = Guid.NewGuid(), Style = new(Magnification: 8, Dim: 1, HighlightShape: MarkerShape.Rectangle, MarkerCap: MarkerCap.Round) });
        check(validation.Annotations.Count == 2, "Inclusive magnifier and dim limits plus defined marker options are accepted.");
        validation.Replace(validation.Annotations[0] with { SampleRegion = new(-5, -5, 20, 20) });
        check(validation.Annotations[0].SampleRegion == new RectD(-5, -5, 20, 20),
            "Valid off-edge source regions remain representable for renderer clipping.");
    }
}
