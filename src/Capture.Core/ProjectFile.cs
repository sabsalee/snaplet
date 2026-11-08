using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capture.Core;

/// <summary>What a project file holds: the encoded original image and its editable document.</summary>
public sealed record ProjectContents(byte[] SourceImage, EditableDocument Document);

/// <summary>
/// A ".snaplet" project is a ZIP of the original image, the objects as JSON, and the pixels of inserted images.
/// Undo history is not kept. Files may come from anywhere, so reading validates everything and bounds every allocation.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".snaplet";
    public const int CurrentVersion = 1;
    private const string ProjectEntry = "project.json", SourceEntry = "source.png";
    private const long MaxProjectBytes = 64L * 1024 * 1024, MaxSourceBytes = 512L * 1024 * 1024, MaxImageBytes = 256_000_000;
    private const int MaxAnnotations = 100_000;
    private const string Unreadable = "Snaplet 프로젝트 파일이 아니거나 손상되었습니다.";
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record BackdropData(bool Enabled, BackdropFill Fill, int Preset, int[] Color, double Padding, double CornerRadius, double Shadow);
    private sealed record ImageData(int Width, int Height);
    private sealed record AnnotationData(Guid Id, AnnotationKind Kind, double[] Start, double[] End, int[] Color, double Thickness, string Text,
        double[][]? Points, double[]? Control, double[]? Tail, AnnotationStyle? Style, int? Image, double[]? SampleRegion);
    private sealed record ProjectData(int Version, int Width, int Height, double[] Canvas, double[] Crop, int NextCounter, BackdropData Backdrop, double ExportScale, ImageData[] Images, AnnotationData[] Annotations);

    public static void Write(Stream destination, ReadOnlySpan<byte> sourceImage, EditableDocument document)
    {
        ArgumentNullException.ThrowIfNull(destination); ArgumentNullException.ThrowIfNull(document);
        var images = new List<RasterImage>();
        int ImageIndex(RasterImage image) { int found = images.IndexOf(image); if (found >= 0) return found; images.Add(image); return images.Count - 1; }
        var annotations = document.Annotations.Select(a => new AnnotationData(a.Id, a.Kind, Pair(a.Start), Pair(a.End), Rgba(a.Color), a.Thickness, a.Text,
            a.Points?.Select(Pair).ToArray(), a.Control is PointD c ? Pair(c) : null, a.Tail is PointD t ? Pair(t) : null, a.Style,
            a.Image is RasterImage i ? ImageIndex(i) : null, a.SampleRegion is RectD s ? Rect(s) : null)).ToArray();
        var b = document.Backdrop;
        var data = new ProjectData(CurrentVersion, document.Width, document.Height, Rect(document.Canvas), Rect(document.Crop), document.NextCounter,
            new(b.Enabled, b.Fill, b.Preset, Rgba(b.Color), b.Padding, b.CornerRadius, b.Shadow), document.ExportScale,
            images.Select(i => new ImageData(i.Width, i.Height)).ToArray(), annotations);

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        using (var entry = zip.CreateEntry(ProjectEntry, CompressionLevel.Optimal).Open()) JsonSerializer.Serialize(entry, data, Json);
        // The PNG is compressed already.
        using (var entry = zip.CreateEntry(SourceEntry, CompressionLevel.NoCompression).Open()) entry.Write(sourceImage);
        for (int i = 0; i < images.Count; i++)
        {
            using var entry = zip.CreateEntry(ImageName(i), CompressionLevel.Fastest).Open();
            entry.Write(images[i].CopyPixels());
        }
    }

    /// <exception cref="InvalidDataException">The file is damaged, not a project, or from a newer version. The message can be shown to the user.</exception>
    public static ProjectContents Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            var data = JsonSerializer.Deserialize<ProjectData>(Entry(zip, ProjectEntry, MaxProjectBytes), Json) ?? throw new InvalidDataException(Unreadable);
            if (data.Version > CurrentVersion) throw new InvalidDataException("더 새로운 버전의 Snaplet에서 만든 프로젝트입니다. 앱을 업데이트해 주세요.");
            if (data.Version < 1 || data.Images is null || data.Annotations is null || data.Backdrop is null || data.Annotations.Length > MaxAnnotations) throw new InvalidDataException(Unreadable);

            // Check every declared size before allocating any of it.
            long total = 0;
            foreach (var image in data.Images)
            {
                if (image is null || image.Width <= 0 || image.Height <= 0) throw new InvalidDataException(Unreadable);
                total += (long)image.Width * image.Height * 4;
                if (total > MaxImageBytes) throw new InvalidDataException(Unreadable);
            }
            var images = data.Images.Select((image, index) => new RasterImage(image.Width, image.Height, Entry(zip, ImageName(index), (long)image.Width * image.Height * 4, exact: true))).ToArray();
            var annotations = data.Annotations.Select(a => new Annotation(a.Id, a.Kind, ToPoint(a.Start), ToPoint(a.End), ToColor(a.Color), a.Thickness, a.Text ?? "",
                a.Points?.Select(ToPoint).ToArray(), a.Control is null ? null : ToPoint(a.Control), a.Tail is null ? null : ToPoint(a.Tail), a.Style,
                a.Image is int index ? images[index] : null, a.SampleRegion is null ? null : ToRect(a.SampleRegion))).ToArray();
            var b = data.Backdrop;
            var document = EditableDocument.Restore(data.Width, data.Height, ToRect(data.Canvas), ToRect(data.Crop), annotations, data.NextCounter,
                new(b.Enabled, b.Fill, b.Preset, ToColor(b.Color), b.Padding, b.CornerRadius, b.Shadow), data.ExportScale);
            return new(Entry(zip, SourceEntry, MaxSourceBytes), document);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException or NotSupportedException or NullReferenceException or EndOfStreamException)
        { throw new InvalidDataException(Unreadable, error); }
        catch (InvalidDataException error) when (error.Message != Unreadable && !error.Message.Contains("Snaplet", StringComparison.Ordinal))
        { throw new InvalidDataException(Unreadable, error); }
    }

    private static string ImageName(int index) => $"images/{index}.bgra";
    /// <summary>Reads an entry without trusting its declared size: the stream is cut off at the limit.</summary>
    private static byte[] Entry(ZipArchive zip, string name, long limit, bool exact = false)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException(Unreadable);
        if (entry.Length > limit || (exact && entry.Length != limit)) throw new InvalidDataException(Unreadable);
        using var stream = entry.Open();
        var bytes = new byte[entry.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException(Unreadable);
        return bytes;
    }
    private static double[] Pair(PointD p) => [p.X, p.Y];
    private static double[] Rect(RectD r) => [r.X, r.Y, r.Width, r.Height];
    private static int[] Rgba(ColorRgba c) => [c.R, c.G, c.B, c.A];
    private static PointD ToPoint(double[] v) => v is { Length: 2 } ? new(v[0], v[1]) : throw new InvalidDataException(Unreadable);
    private static RectD ToRect(double[] v) => v is { Length: 4 } ? new(v[0], v[1], v[2], v[3]) : throw new InvalidDataException(Unreadable);
    private static ColorRgba ToColor(int[] v) => v is { Length: 4 } ? new(checked((byte)v[0]), checked((byte)v[1]), checked((byte)v[2]), checked((byte)v[3])) : throw new InvalidDataException(Unreadable);
}
