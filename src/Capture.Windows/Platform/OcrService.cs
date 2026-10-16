using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Capture.Windows.Platform;

/// <summary>
/// Local Windows OCR. Requires package identity and an installed OCR language.
/// Large images are tiled at original resolution, with overlap deduplication.
/// </summary>
public sealed class OcrService
{
    public Task<string> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        // Clone while still on the owner's thread, then hand immutable pixels to the worker.
        BitmapSource owned = image;
        if (!owned.IsFrozen)
        {
            owned = owned.CloneCurrentValue();
            owned.Freeze();
        }
        return Task.Run(() => RecognizeCoreAsync(owned, cancellationToken), cancellationToken);
    }

    private static async Task<string> RecognizeCoreAsync(BitmapSource image, CancellationToken cancellationToken)
    {
        RequirePackageIdentity();
        OcrEngine engine;
        try
        {
            var languages = OcrEngine.AvailableRecognizerLanguages;
            var korean = languages.FirstOrDefault(language => language.LanguageTag.Equals("ko-KR", StringComparison.OrdinalIgnoreCase));
            engine = (korean is null ? null : OcrEngine.TryCreateFromLanguage(korean))
                ?? OcrEngine.TryCreateFromUserProfileLanguages()
                ?? (languages.Count > 0 ? OcrEngine.TryCreateFromLanguage(languages[0]) : null)
                ?? throw new InvalidOperationException("설치된 OCR 언어가 없습니다. Windows 설정 → 시간 및 언어 → 언어 및 지역에서 한국어 또는 사용할 언어의 OCR 기능을 설치해 주세요.");
        }
        catch (COMException error)
        {
            throw new InvalidOperationException("Windows OCR을 초기화하지 못했습니다. MSIX 패키지로 설치했는지와 OCR 언어 기능이 설치되어 있는지 확인해 주세요.", error);
        }
        int maximum = checked((int)OcrEngine.MaxImageDimension);
        if (maximum <= 0) throw new InvalidOperationException("Windows OCR의 최대 이미지 크기를 확인하지 못했습니다.");
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0) return string.Empty;
        var lines = new List<RecognizedLine>();
        foreach (int y in TileStarts(image.PixelHeight, maximum))
        foreach (int x in TileStarts(image.PixelWidth, maximum))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int width = Math.Min(maximum, image.PixelWidth - x);
            int height = Math.Min(maximum, image.PixelHeight - y);
            var cropped = new CroppedBitmap(image, new Int32Rect(x, y, width, height));
            var converted = new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[checked(width * height * 4)];
            converted.CopyPixels(pixels, checked(width * 4), 0);
            using var writer = new DataWriter();
            writer.WriteBytes(pixels);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8,
                width, height, BitmapAlphaMode.Ignore);
            // Keep the bitmap alive until the native operation completes even if cancelled.
            var result = await engine.RecognizeAsync(bitmap);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var line in result.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Text) || line.Words.Count == 0) continue;
                double left = line.Words.Min(word => word.BoundingRect.Left) + x;
                double top = line.Words.Min(word => word.BoundingRect.Top) + y;
                double right = line.Words.Max(word => word.BoundingRect.Right) + x;
                double bottom = line.Words.Max(word => word.BoundingRect.Bottom) + y;
                var candidate = new RecognizedLine(line.Text.Trim(), new Rect(left, top, right - left, bottom - top));
                MergeLine(lines, candidate);
            }
        }
        // A screenshot's reading order is top-to-bottom, then left-to-right.
        // Overlapping tiles preserve small text without scaling the full screenshot down.
        return string.Join(Environment.NewLine, lines.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).Select(line => line.Text));
    }

    private static IEnumerable<int> TileStarts(int length, int maximum)
    {
        int overlap = Math.Min(128, maximum / 8);
        int step = Math.Max(1, maximum - overlap);
        for (int start = 0; ; start += step)
        {
            yield return start;
            if (length - start <= maximum) yield break;
        }
    }

    private static void MergeLine(List<RecognizedLine> lines, RecognizedLine candidate)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var existing = lines[i];
            var overlap = Rect.Intersect(existing.Bounds, candidate.Bounds);
            if (overlap.IsEmpty) continue;
            double smallerArea = Math.Min(existing.Bounds.Width * existing.Bounds.Height, candidate.Bounds.Width * candidate.Bounds.Height);
            if (smallerArea <= 0 || overlap.Width * overlap.Height < smallerArea * 0.5) continue;
            // Repeated words at separate positions are retained. Drop only geometrically
            // overlapping duplicates or cropped fragments found at tile boundaries.
            if (existing.Text == candidate.Text || (candidate.Text.Length >= 4 && existing.Text.Contains(candidate.Text, StringComparison.Ordinal))) return;
            if (existing.Text.Length >= 4 && candidate.Text.Contains(existing.Text, StringComparison.Ordinal)) lines.RemoveAt(i);
        }
        lines.Add(candidate);
    }

    private static void RequirePackageIdentity()
    {
        uint length = 0;
        int result = GetCurrentPackageFullName(ref length, 0);
        if (result == 15700) // APPMODEL_ERROR_NO_PACKAGE
            throw new InvalidOperationException("Windows 기본 OCR은 패키지 ID가 있는 앱에서 지원됩니다. 현재 실행 파일은 포터블 모드이므로 OCR을 사용할 수 없습니다. MSIX 설치 버전으로 실행해 주세요.");
        if (result != 0 && result != 122) // ERROR_INSUFFICIENT_BUFFER is expected for a packaged process.
            throw new Win32Exception(result, "앱의 패키지 ID를 확인하지 못했습니다.");
    }

    private sealed record RecognizedLine(string Text, Rect Bounds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);
}
