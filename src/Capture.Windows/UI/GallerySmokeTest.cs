using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.UI;

/// <summary>History on a temporary folder and the gallery window over synthetic captures. It never touches the user's history.</summary>
internal static class GallerySmokeTest
{
    internal static async Task RunAsync(string directory)
    {
        var checks=new List<string>();void Check(bool value,string label){if(!value)throw new InvalidOperationException("Gallery check failed: "+label);checks.Add(label);}
        var folder=Path.Combine(Path.GetTempPath(),"snaplet-gallery-"+Guid.NewGuid().ToString("N"));
        try {
            var tiny=BitmapSource.Create(4,3,96,96,PixelFormats.Bgra32,null,new byte[4*3*4],16);tiny.Freeze();
            var first=HistoryStore.Save(tiny,folder);await Task.Delay(5);var second=HistoryStore.Save(tiny,folder);
            var entries=HistoryStore.Entries(folder);
            Check(entries.Count==2 && entries[0].Path==second && entries[1].Path==first,"entries list the newest capture first");
            Check(Math.Abs((entries[0].When.ToUniversalTime()-DateTime.UtcNow).TotalMinutes)<5,"entries read the capture time from the file name");
            File.WriteAllBytes(Path.Combine(folder,"not-a-capture.png"),[1,2,3]);
            Check(HistoryStore.Entries(folder).Count==2,"files Snaplet did not name are not listed");
            var broken=Path.Combine(folder,HistoryNames.Create(DateTime.UtcNow.AddHours(1),"broken"));File.WriteAllBytes(broken,[1,2,3]);
            Check(HistoryStore.Load(folder).Count==2,"an unreadable capture does not stop loading");File.Delete(broken);
            var thumbnail=HistoryStore.Thumbnail(first,432);Check(thumbnail is {Width:4,Height:3} && thumbnail.Image.IsFrozen,"a thumbnail reports the original size and can cross threads");
            HistoryStore.Delete(first);Check(HistoryStore.Entries(folder).Single().Path==second,"delete removes one capture");
            HistoryStore.Delete(first);Check(HistoryStore.Entries(folder).Count==1,"deleting a file that is already gone is harmless");
            for(int i=0;i<HistoryStore.Limit+2;i++)HistoryStore.Save(tiny,folder);
            Check(HistoryStore.Load(folder).Count==HistoryStore.Limit && HistoryStore.Entries(folder).Count==HistoryStore.Limit+3,"startup loads the recent few while the disk keeps more");
            Check(HistoryStore.DiskLimit>HistoryStore.Limit,"the disk keeps more captures than memory");
            HistoryStore.Clear(folder);Check(HistoryStore.Entries(folder).Count==0,"clearing leaves an empty history");

            var demo=ImageActions.Demo();var onDisk=HistoryStore.Save(demo,folder);var now=DateTime.Now;
            var items=new List<GalleryItem>{new(now,onDisk,null),new(now.AddMinutes(-3),null,demo),new(now.AddDays(-1),null,demo),new(now.AddDays(-40),null,demo)};
            BitmapSource? edited=null;GalleryItem? deleted=null;
            var window=new GalleryWindow(()=>items,image=>edited=image,item=>deleted=item,()=>{},()=>{},()=>false);
            try {
                window.Show();window.UpdateLayout();await window.ThumbnailsLoaded;window.UpdateLayout();
                Check(window.CardCount==4,"every capture gets a card");
                Check(window.Headings.Count>=3 && window.Headings[0]=="오늘","captures are grouped by day, newest first");
                Shot(window,directory,"gallery");
                items[0].Open();Check(items[0].Memory is null && items[0].Open().PixelWidth==demo.PixelWidth,"a capture on disk opens at full size");
                items.Clear();window.Reload();window.UpdateLayout();
                Check(window.CardCount==0 && window.Headings.Count==0,"an empty history shows its empty state");
                Shot(window,directory,"gallery-empty");
            } finally {window.Close();}
            Check(edited is null && deleted is null,"opening the gallery neither edits nor deletes anything");
        } finally {HistoryStore.Clear(folder);}
        File.WriteAllText(Path.Combine(directory,"gallery-smoke.json"),System.Text.Json.JsonSerializer.Serialize(checks));
    }
    private static void Shot(Window window,string directory,string name)
    {
        var content=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(content);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,"ui-"+name+".png"));encoder.Save(file);
    }
}
