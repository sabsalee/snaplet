using System.IO.Compression;
using System.Text;
using Capture.Core;

internal static class ProjectFileChecks
{
    internal static void Run(Action<bool, string> check)
    {
        byte[] source = [1, 2, 3, 4, 5];
        var asset = new RasterImage(2, 1, [10, 20, 30, 255, 40, 50, 60, 128]);
        var style = new AnnotationStyle(ArrowHead: ArrowHead.Open, Fill: ShapeFill.Hatched, RibbonStyle: RibbonStyle.Chevron, FontFamily: "맑은 고딕", Align: TextAlign.Right, Opacity: .5);
        var document = new EditableDocument(100, 80);
        var rich = new Annotation(Guid.NewGuid(), AnnotationKind.Arrow, new(1.5, 2), new(30, 40), new(9, 8, 7, 6), 5, "글자", null, new(3, 4), new(5, 6), style);
        var pen = new Annotation(Guid.NewGuid(), AnnotationKind.Pen, new(0, 0), new(9, 9), new(1, 1, 1), 2, "", [new(0, 0), new(4, 5), new(9, 9)]);
        var first = new Annotation(Guid.NewGuid(), AnnotationKind.Image, new(0, 0), new(2, 1), new(0, 0, 0), Image: asset);
        var second = new Annotation(Guid.NewGuid(), AnnotationKind.Image, new(5, 5), new(7, 6), new(0, 0, 0), Image: asset);
        var magnifier = new Annotation(Guid.NewGuid(), AnnotationKind.Magnifier, new(10, 10), new(20, 20), new(0, 0, 0), SampleRegion: new(1, 2, 3, 4));
        foreach (var a in new[] { rich, pen, first, second, magnifier }) document.Add(a);
        document.Add(new Annotation(Guid.NewGuid(), AnnotationKind.Step, new(40, 40), new(50, 50), new(0, 0, 0), Text: "7"));
        document.ExpandToInclude(new(-10, -5, 150, 100));
        document.CropTo(new(2, 3, 50, 40));
        document.SetBackdrop(new(true, BackdropFill.Solid, 3, new(1, 2, 3, 4), 40, 10, .25));
        document.SetExportScale(.5);

        var read = RoundTrip(source, document);
        var restored = read.Document;
        check(read.SourceImage.SequenceEqual(source), "A project keeps the original image bytes.");
        check(restored.Width == 100 && restored.Height == 80 && restored.Canvas == document.Canvas && restored.Crop == document.Crop, "A project keeps the canvas and crop.");
        check(restored.Backdrop == document.Backdrop && restored.ExportScale == .5 && restored.NextCounter == 8, "A project keeps the backdrop, export size and next step number.");
        check(restored.Annotations.Count == 6 && !restored.CanUndo && !restored.CanRedo, "A project restores its objects with an empty undo history.");
        var arrow = restored.Annotations[0];
        check(arrow.Id == rich.Id && arrow.Kind == rich.Kind && arrow.Start == rich.Start && arrow.End == rich.End && arrow.Color == rich.Color && arrow.Thickness == 5 && arrow.Text == "글자" && arrow.Control == rich.Control && arrow.Tail == rich.Tail, "An object keeps its geometry, colour and text.");
        check(arrow.Appearance == style, "An object keeps its whole style.");
        check(restored.Annotations[1].Points!.SequenceEqual(pen.Points!), "A pen stroke keeps its points.");
        check(restored.Annotations[2].Image!.CopyPixels().SequenceEqual(asset.CopyPixels()), "An inserted image keeps its pixels.");
        check(ReferenceEquals(restored.Annotations[2].Image, restored.Annotations[3].Image), "An image used twice is stored once.");
        check(restored.Annotations[4].SampleRegion == magnifier.SampleRegion, "A magnifier keeps its sample region.");
        restored.Undo(); check(restored.Annotations.Count == 6, "Undo cannot step before the opened state.");
        restored.Delete(rich.Id); check(restored.CanUndo && restored.Annotations.Count == 5, "A restored project stays editable."); restored.Undo();
        check(restored.Annotations.Count == 6, "Edits after opening can be undone.");
        check(RoundTrip([9], new EditableDocument(3, 4)).Document.Annotations.Count == 0, "An untouched document is a valid project.");

        void Rejects(byte[] file, string message)
        {
            bool rejected = false;
            try { ProjectFile.Read(new MemoryStream(file)); }
            catch (InvalidDataException error) { rejected = !string.IsNullOrWhiteSpace(error.Message); }
            check(rejected, message);
        }
        const string valid = """{"Version":1,"Width":10,"Height":10,"Canvas":[0,0,10,10],"Crop":[0,0,10,10],"NextCounter":1,"Backdrop":{"Enabled":false,"Fill":"Gradient","Preset":0,"Color":[0,0,0,0],"Padding":56,"CornerRadius":12,"Shadow":0.5},"ExportScale":1,"Images":[],"Annotations":[]}""";
        check(ProjectFile.Read(new MemoryStream(Zip(valid))).Document.Width == 10, "A hand-built valid project opens, so the rejections below test what they claim.");
        Rejects([1, 2, 3], "A file that is not a ZIP is rejected.");
        Rejects(Zip(null), "A ZIP without project data is rejected.");
        Rejects(Zip("not json"), "Malformed project data is rejected.");
        Rejects(Zip("null"), "Empty project data is rejected.");
        Rejects(Zip(valid, source: false), "A project without its original image is rejected.");
        Rejects(Zip(valid.Replace("\"Width\":10", "\"Width\":-1")), "A negative size is rejected.");
        Rejects(Zip(valid.Replace("\"Crop\":[0,0,10,10]", "\"Crop\":[500,500,10,10]")), "A crop outside the canvas is rejected.");
        Rejects(Zip(valid.Replace("\"Canvas\":[0,0,10,10]", "\"Canvas\":[0,0,5,5]")), "A canvas that cuts off the image is rejected.");
        Rejects(Zip(valid.Replace("\"Canvas\":[0,0,10,10]", "\"Canvas\":[0,0,10]")), "A malformed rectangle is rejected.");
        Rejects(Zip(valid.Replace("\"Padding\":56", "\"Padding\":99999")), "An invalid backdrop is rejected.");
        Rejects(Zip(valid.Replace("\"ExportScale\":1", "\"ExportScale\":50")), "An invalid export size is rejected.");
        Rejects(Zip(valid.Replace("\"Color\":[0,0,0,0]", "\"Color\":[0,0,0,999]")), "An out-of-range colour is rejected.");
        Rejects(Zip(valid.Replace("\"Images\":[]", "\"Images\":[{\"Width\":2,\"Height\":1}]")), "A declared image without pixels is rejected.");
        Rejects(Zip(valid.Replace("\"Images\":[]", "\"Images\":[{\"Width\":60000,\"Height\":60000}]")), "A huge declared image is rejected before allocating it.");
        Rejects(Zip(valid.Replace("\"Images\":[]", "\"Images\":[{\"Width\":2,\"Height\":1}]"), image: new byte[7]), "Image pixels of the wrong length are rejected.");
        const string item = """{"Id":"11111111-1111-1111-1111-111111111111","Kind":"Line","Start":[0,0],"End":[1,1],"Color":[0,0,0,255],"Thickness":2,"Text":""}""";
        check(ProjectFile.Read(new MemoryStream(Zip(valid.Replace("\"Annotations\":[]", $"\"Annotations\":[{item}]")))).Document.Annotations[0].Appearance == new AnnotationStyle(), "Style fields missing from a file take their defaults.");
        Rejects(Zip(valid.Replace("\"Annotations\":[]", $"\"Annotations\":[{item},{item}]")), "Duplicate object IDs are rejected.");
        Rejects(Zip(valid.Replace("\"Annotations\":[]", $"\"Annotations\":[{item.Replace("\"Thickness\":2", "\"Thickness\":-2")}]")), "An invalid object is rejected.");
        Rejects(Zip(valid.Replace("\"Annotations\":[]", $"\"Annotations\":[{item.Replace("\"Line\"", "\"Hologram\"")}]")), "An unknown object kind is rejected.");
        Rejects(Zip(valid.Replace("\"Annotations\":[]", $"\"Annotations\":[{item.Replace("\"Thickness\":2", "\"Image\":3,\"Thickness\":2")}]")), "An object pointing at a missing image is rejected.");
        bool newer = false;
        try { ProjectFile.Read(new MemoryStream(Zip(valid.Replace("\"Version\":1", "\"Version\":2")))); }
        catch (InvalidDataException error) { newer = error.Message.Contains("업데이트", StringComparison.Ordinal); }
        check(newer, "A project from a newer version asks for an update.");
    }

    private static ProjectContents RoundTrip(byte[] source, EditableDocument document)
    {
        var stream = new MemoryStream();
        ProjectFile.Write(stream, source, document);
        stream.Position = 0;
        return ProjectFile.Read(stream);
    }
    private static byte[] Zip(string? project, bool source = true, byte[]? image = null)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] bytes) { using var entry = zip.CreateEntry(name).Open(); entry.Write(bytes); }
            if (project is not null) Add("project.json", Encoding.UTF8.GetBytes(project));
            if (source) Add("source.png", [1, 2, 3]);
            if (image is not null) Add("images/0.bgra", image);
        }
        return stream.ToArray();
    }
}
