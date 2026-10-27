using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace Capture.Windows.Platform;

/// <summary>Local QR / 2D code decoding. Runs entirely on this PC; decoded text is never sent anywhere.</summary>
internal static class QrReader
{
    private static readonly IList<BarcodeFormat> Formats=[BarcodeFormat.QR_CODE,BarcodeFormat.DATA_MATRIX,BarcodeFormat.AZTEC,BarcodeFormat.PDF_417];

    /// <summary>Returns every distinct code text found, in reading order of discovery. Empty when nothing decodes.</summary>
    public static IReadOnlyList<string> Decode(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        BitmapSource source=image.Format==PixelFormats.Bgra32 || image.Format==PixelFormats.Pbgra32?image:new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
        int width=source.PixelWidth,height=source.PixelHeight;
        if(width<21 || height<21)return [];
        var pixels=new byte[checked(width*height*4)];
        source.CopyPixels(pixels,width*4,0);
        var reader=new BarcodeReaderGeneric{AutoRotate=true,Options=new DecodingOptions{TryHarder=true,PossibleFormats=Formats}};
        var luminance=new RGBLuminanceSource(pixels,width,height,RGBLuminanceSource.BitmapFormat.BGRA32);
        var results=reader.DecodeMultiple(luminance);
        if(results is null || results.Length==0) {
            // Dark-mode screenshots often show light-on-dark codes.
            reader.Options.TryInverted=true;
            results=reader.DecodeMultiple(luminance);
        }
        return results?.Select(r=>r.Text).Where(t=>!string.IsNullOrWhiteSpace(t)).Distinct().ToList() ?? [];
    }

    /// <summary>Self-test: encodes a known QR with the same library, renders it via WPF and decodes it back.</summary>
    internal static void RunSelfTest()
    {
        var writer=new BarcodeWriterPixelData{Format=BarcodeFormat.QR_CODE,Options=new EncodingOptions{Width=240,Height=240,Margin=2}};
        var data=writer.Write("https://example.com/snaplet?q=1");
        var bitmap=BitmapSource.Create(data.Width,data.Height,96,96,PixelFormats.Bgra32,null,data.Pixels,data.Width*4);bitmap.Freeze();
        var decoded=Decode(bitmap);
        if(decoded.Count!=1 || decoded[0]!="https://example.com/snaplet?q=1")throw new InvalidOperationException("QR self-test decode mismatch: "+string.Join(" | ",decoded));
        var blank=BitmapSource.Create(64,64,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)255,64*64*4).ToArray(),64*4);
        if(Decode(blank).Count!=0)throw new InvalidOperationException("QR self-test found a code in a blank image.");
    }
}
