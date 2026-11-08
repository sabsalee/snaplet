using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;
using Capture.Windows.Editor;

namespace Capture.Windows.UI;

/// <summary>Bringing pictures in: images dropped on the editor, framed window captures, WebP/HEIC files and phone photo orientation.</summary>
internal static class ImportSmokeTest
{
    // 8 × 4 pictures, red on the left half and blue on the right, made for this test.
    private const string Webp="UklGRh4AAABXRUJQVlA4TBIAAAAvB8AAAA8Q87//8x8O+hDR/wA=";
    private const string Heic="AAAAJGZ0eXBoZWljAAAAAG1pZjFNaVBybWlhZk1pSEJoZWljAAABhm1ldGEAAAAAAAAAIWhkbHIAAAAAAAAAAHBpY3QAAAAAAAAAAAAAAAAAAAAAJGRpbmYAAAAcZHJlZgAAAAAAAAABAAAADHVybCAAAAABAAAADnBpdG0AAAAAAAEAAAAjaWluZgAAAAAAAQAAABVpbmZlAgAAAAABAABodmMxAAAAAOZpcHJwAAAAxWlwY28AAAATY29scm5jbHgAAgACAAaAAAAADGNsbGkAywBAAAAAFGlzcGUAAAAAAAAACAAAAAQAAAAJaXJvdAAAAAAQcGl4aQAAAAADCAgIAAAAcWh2Y0MBA3AAAACwAAAAAAAe8AD8/fj4AAALA6AAAQAXQAEMAf//A3AAAAMAsAAAAwAAAwAecCShAAEAI0IBAQNwAAADALAAAAMAAAMAHqAUIEHAmw/iHuRZVNwICBgCogABAAlEAcBhcshAUyQAAAAZaXBtYQAAAAAAAAABAAEGgQIDBYaEAAAAHmlsb2MAAAAARAAAAQABAAAAAQAAAboAAABqAAAAAW1kYXQAAAAAAAAAegAAAGYoAa+jzIAqkueQGODKCYP4BnFLG5ikqVCCc21zofih/dl+YYlWngNFnThlEGecgtJ71JK2VnXzdm2R76rQ17+f077EQF//XbnX/oqmC8bZGs6hCD7r4Mzr4thH+bPH/JOs70MGsjA=";

    internal static void Run(string directory)
    {
        var checks=new List<string>();void Check(bool value,string label){if(!value)throw new InvalidOperationException("Import check failed: "+label);checks.Add(label);}
        var white=Solid(400,300,Colors.White);var stamp=Solid(40,20,Colors.Red);
        var document=new EditableDocument(400,300);
        var editor=new EditorWindow(white,document);
        try {
            editor.Show();editor.UpdateLayout();
            editor.DropImage(stamp,new PointD(395,150));
            var edge=document.Annotations[^1];
            Check(edge.Kind==AnnotationKind.Image && edge.End.X==400 && edge.Start.Y==140,"an image dropped at the edge stays on the canvas and lines up with its middle");
            editor.DropImage(stamp,new PointD(202,40));
            var centre=document.Annotations[^1];
            Check(centre.Start.X==180,"an image dropped near the centre lines up with it");
            editor.DropImage(stamp,new PointD(60,100));
            Check(document.Annotations[^1].Start.X==40 && document.Annotations[^1].Start.Y==90,"an image dropped away from any edge lands where it was dropped");
            document.Undo();Check(document.Annotations.Count==2,"a dropped image can be undone");
        } finally {editor.CloseForExit();}

        // A framed window keeps its frame as a backdrop the editor can change.
        var bare=Solid(120,80,Colors.SteelBlue);var frame=App.WindowBackdrop(WindowCaptureStyle.Backdrop);
        var framedDocument=EditableDocument.Restore(120,80,new(0,0,120,80),new(0,0,120,80),[],1,frame,1);
        Check(Bytes(BackdropRenderer.Export(bare,framedDocument)).AsSpan().SequenceEqual(Bytes(BackdropRenderer.Compose(bare,frame))),"a framed window reopened with its frame as a backdrop exports the same picture");
        framedDocument.SetBackdrop(framedDocument.Backdrop with {Enabled=false});
        Check(BackdropRenderer.Export(bare,framedDocument).PixelWidth==120,"turning the backdrop off leaves the bare window");

        var folder=Path.Combine(Path.GetTempPath(),"snaplet-import-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try {
            Check(ImageActions.IsImage(@"C:\a\B.WEBP") && ImageActions.IsImage("photo.heic") && ImageActions.IsImage("photo.HEIF") && !ImageActions.IsImage("notes.txt"),"WebP and HEIC count as images");
            foreach(var (name,data) in new[]{("sample.webp",Webp),("sample.heic",Heic)}) {
                var path=Path.Combine(folder,name);File.WriteAllBytes(path,Convert.FromBase64String(data));
                try {
                    var image=ImageActions.Load(path);
                    var left=Pixel(image,1,image.PixelHeight/2);var right=Pixel(image,image.PixelWidth*7/8,image.PixelHeight/2);
                    Check(left.R>left.B+80 && right.B>right.R+80,name+" opens with its colours");
                } catch(NotSupportedException error) {
                    // Windows only decodes these with a Store extension; without it the user is told which one.
                    Check(error.Message.Contains("Microsoft Store",StringComparison.Ordinal) && error.Message.Contains("확장",StringComparison.Ordinal),name+" without a codec names the extension to install");
                }
            }
            var broken=Path.Combine(folder,"broken.webp");File.WriteAllBytes(broken,[1,2,3,4]);
            bool told=false;try{ImageActions.Load(broken);}catch(NotSupportedException error){told=error.Message.Contains("WebP",StringComparison.Ordinal);}catch(FileFormatException){told=true;}
            Check(told,"a broken WebP file reports an error instead of crashing");
        } finally {try{Directory.Delete(folder,true);}catch(IOException){}}

        // A phone photo stores its rotation separately; every EXIF orientation is applied.
        var marked=Solid(30,10,Colors.White,(0,0,Colors.Black));
        foreach(var (orientation,width,x,y) in new[]{(1,30,0,0),(2,30,29,0),(3,30,29,9),(4,30,0,9),(5,10,0,0),(6,10,9,0),(7,10,9,29),(8,10,0,29)}) {
            var upright=ImageActions.Upright(marked,orientation);
            Check(upright.PixelWidth==width && Pixel(upright,x,y).R==0,$"EXIF orientation {orientation} puts the photo upright");
        }
        File.WriteAllText(Path.Combine(directory,"import-smoke.json"),System.Text.Json.JsonSerializer.Serialize(checks));
    }

    private static BitmapSource Solid(int width,int height,Color color,(int X,int Y,Color Color)? dot=null)
    {
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen()){dc.DrawRectangle(new SolidColorBrush(color),null,new Rect(0,0,width,height));if(dot is {} d)dc.DrawRectangle(new SolidColorBrush(d.Color),null,new Rect(d.X,d.Y,1,1));}
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
    }
    private static byte[] Bytes(BitmapSource image)
    {
        var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);var bytes=new byte[image.PixelWidth*image.PixelHeight*4];converted.CopyPixels(bytes,image.PixelWidth*4,0);return bytes;
    }
    private static (byte R,byte G,byte B) Pixel(BitmapSource image,int x,int y)
    {
        var bytes=new byte[4];new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0).CopyPixels(new Int32Rect(x,y,1,1),bytes,4,0);return (bytes[2],bytes[1],bytes[0]);
    }
}
