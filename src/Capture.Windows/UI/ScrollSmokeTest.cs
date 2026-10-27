using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capture.Windows.UI;

/// <summary>
/// Automatic scroll capture against a simulated scrolling page: the wheel callback moves a viewport over a tall synthetic image.
/// Exercises the real auto loop (adaptive step, end detection, finish); it does not inject input into another app.
/// </summary>
internal static class ScrollSmokeTest
{
    internal static async Task RunAsync(string directory)
    {
        const int width=240,viewport=300,total=1500,sticky=24;
        var random=new Random(77);var page=new byte[width*total*4];random.NextBytes(page);for(int i=3;i<page.Length;i+=4)page[i]=255;
        var header=new byte[width*sticky*4];random.NextBytes(header);for(int i=3;i<header.Length;i+=4)header[i]=255;
        int position=0,wheelCalls=0;
        BitmapSource Frame()
        {
            var px=new byte[width*viewport*4];
            header.CopyTo(px,0);
            page.AsSpan((position+sticky)*width*4,(viewport-sticky)*width*4).CopyTo(px.AsSpan(sticky*width*4));
            var b=BitmapSource.Create(width,viewport,96,96,PixelFormats.Bgra32,null,px,width*4);b.Freeze();return b;
        }
        var window=new ManualScrollWindow(Frame(),_=>Task.FromResult(Frame()),notches=>{wheelCalls++;position=Math.Min(position+notches*40,total-viewport);return true;});
        BitmapSource? result=null;window.Finished+=image=>result=image;
        try {
            window.Show();
            var run=window.AutoAsync();
            var timeout=Task.Delay(TimeSpan.FromSeconds(60));
            if(await Task.WhenAny(run,timeout)==timeout)throw new InvalidOperationException("Auto scroll did not finish within 60 s.");
            await run;
        } finally {if(window.IsVisible)window.Close();}
        if(result is null)throw new InvalidOperationException("Auto scroll finished without an image.");
        if(result.PixelHeight!=total)throw new InvalidOperationException($"Auto scroll height {result.PixelHeight} != {total}.");
        var got=new byte[width*total*4];new FormatConvertedBitmap(result,PixelFormats.Bgra32,null,0).CopyPixels(got,width*4,0);
        if(!got.AsSpan(0,sticky*width*4).SequenceEqual(header))throw new InvalidOperationException("Sticky header missing at the top.");
        if(!got.AsSpan(sticky*width*4).SequenceEqual(page.AsSpan(sticky*width*4)))throw new InvalidOperationException("Auto scroll content is not continuous.");
        File.WriteAllText(Path.Combine(directory,"scroll-smoke.json"),System.Text.Json.JsonSerializer.Serialize(new{component="automatic scroll capture (simulated page)",passed=3,wheelCalls,checks=new[]{"auto loop reaches the end and finishes","sticky header appears once","content is continuous without duplicates"}}));
    }
}
