using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tesseract;
using WpfRect = System.Windows.Rect;

namespace Capture.Windows.Platform;

/// <summary>Offline Korean+English OCR for unpackaged Windows x64. Native work never runs on the UI thread.</summary>
public static class TesseractOcr
{
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private const int TileDimension = 2560, Overlap = 160;
    private const long MaximumPixels = 100_000_000;

    public static Task<string> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("포터블 OCR은 Windows x64에서 지원됩니다.");
        if ((long)image.PixelWidth * image.PixelHeight > MaximumPixels)
            throw new InvalidOperationException("OCR 이미지는 최대 1억 픽셀까지 지원합니다. 필요한 부분을 잘라서 다시 시도해 주세요.");
        var owned = image;
        if (!owned.IsFrozen) { owned = image.CloneCurrentValue(); owned.Freeze(); }
        return Task.Run(async () =>
        {
            await NativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return Recognize(owned, cancellationToken); }
            finally { NativeGate.Release(); }
        }, cancellationToken);
    }

    public static Task<IReadOnlyList<WpfRect>> FindTextRegionsAsync(BitmapSource image,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(image);cancellationToken.ThrowIfCancellationRequested();
        if(!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture!=Architecture.X64)throw new PlatformNotSupportedException("텍스트 영역 인식은 Windows x64에서 지원됩니다.");
        if((long)image.PixelWidth*image.PixelHeight>MaximumPixels)throw new InvalidOperationException("텍스트 영역이 너무 큽니다.");
        var owned=image.IsFrozen?image:image.CloneCurrentValue();if(!owned.IsFrozen)owned.Freeze();
        return Task.Run<IReadOnlyList<WpfRect>>(async()=>{
            await NativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try {return RecognizeEntries(owned,cancellationToken,true).Select(line=>line.Bounds).ToArray();}
            finally {NativeGate.Release();}
        },cancellationToken);
    }
    private static string Recognize(BitmapSource image,CancellationToken token)=>string.Join(Environment.NewLine,RecognizeEntries(image,token,false).OrderBy(l=>l.Bounds.Top).ThenBy(l=>l.Bounds.Left).Select(l=>l.Text));
    private static List<Line> RecognizeEntries(BitmapSource image, CancellationToken token,bool words)
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        foreach (var language in new[] { "kor", "eng" })
            if (!File.Exists(Path.Combine(dataPath, language + ".traineddata")))
                throw new FileNotFoundException($"OCR 언어 모델({language})이 없습니다. 압축 파일의 tessdata 폴더까지 모두 풀어 주세요.", Path.Combine(dataPath, language + ".traineddata"));
        try
        {
            token.ThrowIfCancellationRequested();
            // Every request owns its engine. Serialization prevents multiple native models consuming memory at once.
            using var engine = new TesseractEngine(dataPath, "kor+eng", EngineMode.LstmOnly);
            var lines = new List<Line>();
            var level=words?PageIteratorLevel.Word:PageIteratorLevel.TextLine;
            foreach (int y in Starts(image.PixelHeight))
            foreach (int x in Starts(image.PixelWidth))
            {
                token.ThrowIfCancellationRequested();
                int width = Math.Min(TileDimension, image.PixelWidth - x), height = Math.Min(TileDimension, image.PixelHeight - y);
                var bytes = EncodeTile(image, new Int32Rect(x, y, width, height), token);
                using var pix = Pix.LoadFromMemory(bytes);
                // Auto excludes OSD: no extra osd language model is required.
                using var page = engine.Process(pix, PageSegMode.Auto);
                using var iterator = page.GetIterator();
                iterator.Begin();
                do
                {
                    token.ThrowIfCancellationRequested();
                    var text = iterator.GetText(level)?.Trim();
                    if (string.IsNullOrWhiteSpace(text) || !iterator.TryGetBoundingBox(level, out var bounds)) continue;
                    Merge(lines, new(text, new WpfRect(x + bounds.X1, y + bounds.Y1, bounds.Width, bounds.Height)));
                } while (iterator.Next(level));
                // Native recognition cannot be safely abandoned/disposed mid-call. Cancellation is checked after native work returns.
                token.ThrowIfCancellationRequested();
            }
            return lines;
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            throw new InvalidOperationException("OCR 네이티브 엔진을 불러오지 못했습니다. x64 배포 파일을 모두 풀고 Microsoft Visual C++ 2015–2022 Redistributable(x64)을 설치해 주세요.", error);
        }
        catch (TesseractException error)
        {
            throw new InvalidOperationException("OCR 엔진이 이미지를 처리하지 못했습니다. tessdata 언어 파일과 Visual C++ x64 런타임 설치를 확인해 주세요.", error);
        }
    }

    private static byte[] EncodeTile(BitmapSource image, Int32Rect region, CancellationToken token)
    {
        var crop = new CroppedBitmap(image, region);
        var converted = new FormatConvertedBitmap(crop, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(region.Width * region.Height * 4)];
        converted.CopyPixels(pixels, checked(region.Width * 4), 0);
        var rgb = new byte[checked(region.Width * region.Height * 3)];
        for (int y = 0; y < region.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < region.Width; x++)
            {
                int i = (y * region.Width + x) * 4, o = (y * region.Width + x) * 3, alpha = pixels[i + 3];
                // Straight BGRA is composited on white; invisible black RGB must not become OCR text.
                for (int c = 0; c < 3; c++) rgb[o + c] = (byte)((pixels[i + c] * alpha + 255 * (255 - alpha) + 127) / 255);
            }
        }
        var bitmap = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgr24, null, rgb, checked(region.Width * 3));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream(); encoder.Save(memory); return memory.ToArray();
    }
    private static IEnumerable<int> Starts(int length)
    {
        for (int start = 0; ; start += TileDimension - Overlap)
        {
            yield return start;
            if (length - start <= TileDimension) yield break;
        }
    }
    private sealed record Line(string Text, WpfRect Bounds);
    private static void Merge(List<Line> lines, Line candidate)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var old = lines[i]; var overlap = WpfRect.Intersect(old.Bounds, candidate.Bounds);
            double smaller = Math.Min(old.Bounds.Width * old.Bounds.Height, candidate.Bounds.Width * candidate.Bounds.Height);
            if (overlap.IsEmpty || smaller <= 0 || overlap.Width * overlap.Height < smaller * .5) continue;
            if (old.Text == candidate.Text || candidate.Text.Length >= 4 && old.Text.Contains(candidate.Text, StringComparison.Ordinal)) return;
            if (old.Text.Length >= 4 && candidate.Text.Contains(old.Text, StringComparison.Ordinal)) lines.RemoveAt(i);
        }
        lines.Add(candidate);
    }
}
