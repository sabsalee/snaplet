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
        var window=new ManualScrollWindow(Frame(),_=>Task.FromResult(Frame()),(notches,_)=>{wheelCalls++;position=Math.Min(position+notches*40,total-viewport);return true;});
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
        await SidewaysAsync(directory);
    }
    /// <summary>The same loop on a page that scrolls right under a fixed sidebar.</summary>
    private static async Task SidewaysAsync(string directory)
    {
        const int height=240,viewport=300,total=1500,sidebar=24;
        var random=new Random(78);var page=new byte[total*height*4];random.NextBytes(page);for(int i=3;i<page.Length;i+=4)page[i]=255;
        int position=0;bool onlySideways=true;
        BitmapSource Frame()
        {
            var px=new byte[viewport*height*4];
            for(int y=0;y<height;y++) {
                page.AsSpan(y*total*4,sidebar*4).CopyTo(px.AsSpan(y*viewport*4));
                page.AsSpan((y*total+position+sidebar)*4,(viewport-sidebar)*4).CopyTo(px.AsSpan((y*viewport+sidebar)*4));
            }
            var b=BitmapSource.Create(viewport,height,96,96,PixelFormats.Bgra32,null,px,viewport*4);b.Freeze();return b;
        }
        var window=new ManualScrollWindow(Frame(),_=>Task.FromResult(Frame()),(notches,sideways)=>{onlySideways&=sideways;position=Math.Min(position+notches*40,total-viewport);return true;});
        BitmapSource? result=null;window.Finished+=image=>result=image;
        try {
            window.Show();
            if(window.IsHorizontal)throw new InvalidOperationException("Scroll capture should start downward.");
            window.SetHorizontal(true);if(!window.IsHorizontal)throw new InvalidOperationException("The direction did not change.");
            var run=window.AutoAsync();
            if(await Task.WhenAny(run,Task.Delay(TimeSpan.FromSeconds(60)))!=run)throw new InvalidOperationException("Sideways auto scroll did not finish within 60 s.");
            await run;
        } finally {if(window.IsVisible)window.Close();}
        if(!onlySideways)throw new InvalidOperationException("Sideways capture sent a vertical wheel.");
        if(result is null || result.PixelWidth!=total || result.PixelHeight!=height)throw new InvalidOperationException($"Sideways size {result?.PixelWidth} × {result?.PixelHeight} != {total} × {height}.");
        var got=new byte[total*height*4];new FormatConvertedBitmap(result,PixelFormats.Bgra32,null,0).CopyPixels(got,total*4,0);
        if(!got.AsSpan().SequenceEqual(page))throw new InvalidOperationException("Sideways content is not continuous.");

        var tiny=BitmapSource.Create(100,40,96,96,PixelFormats.Bgra32,null,new byte[100*40*4],400);tiny.Freeze();
        var wide=new ManualScrollWindow(tiny,_=>Task.FromResult<BitmapSource>(tiny));
        try {
            wide.SetHorizontal(false);
            if(!wide.IsHorizontal)throw new InvalidOperationException("A flat area can only scroll sideways.");
        } finally {wide.Close();}
        var small=BitmapSource.Create(40,40,96,96,PixelFormats.Bgra32,null,new byte[40*40*4],160);small.Freeze();
        bool refused=false;try{new ManualScrollWindow(small,_=>Task.FromResult<BitmapSource>(small)).Close();}catch(InvalidOperationException){refused=true;}
        if(!refused)throw new InvalidOperationException("An area too small in both directions should be refused.");
        File.WriteAllText(Path.Combine(directory,"scroll-sideways-smoke.json"),"PASS: right-scrolling page with a fixed sidebar stitched pixel for pixel; direction limits by area size.");
    }
}
