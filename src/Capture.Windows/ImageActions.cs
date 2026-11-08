using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Capture.Core;

namespace Capture.Windows;
internal enum ImageTurn {Left,Right,FlipHorizontal,FlipVertical}
internal static class ImageActions
{
    internal static BitmapSource Turn(BitmapSource image,ImageTurn turn)
    {
        Transform transform=turn switch {ImageTurn.Left=>new RotateTransform(270),ImageTurn.Right=>new RotateTransform(90),ImageTurn.FlipHorizontal=>new ScaleTransform(-1,1),_=>new ScaleTransform(1,-1)};
        var result=new TransformedBitmap(image,transform);result.Freeze();return result;
    }
    private static long clipboardRequest;
    public static long ReserveClipboard()=>Interlocked.Increment(ref clipboardRequest);
    public static async Task<bool> CopyAsync(BitmapSource image,Func<bool>? stillCurrent=null,long? reservedRequest=null) {
        var request=reservedRequest??ReserveClipboard();
        if(request!=Interlocked.Read(ref clipboardRequest) || stillCurrent?.Invoke()==false)return false;
        var bytes=await Task.Run(()=>{using var encoded=new MemoryStream();var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));png.Save(encoded);return encoded.ToArray();});
        using var stream=new MemoryStream(bytes);
        var data=new DataObject();data.SetImage(image);data.SetData("PNG",stream);
        for(var attempt=0;;attempt++) {
            if(request!=Interlocked.Read(ref clipboardRequest) || stillCurrent?.Invoke()==false)return false;
            try {Clipboard.SetDataObject(data,true);return true;}
            catch(ExternalException) when(attempt<4) {await Task.Delay(50*(attempt+1));}
        }
    }
    /// <summary>Current file name template; the app sets it from settings.</summary>
    public static string NameTemplate {get;set;}=FileNameTemplate.Default;
    public static string DefaultName()=>FileNameTemplate.Render(NameTemplate,DateTime.Now,Guid.NewGuid().ToString("N")[..4])+".png";
    /// <summary>Where an image lands on a page: centered, shrunk to fit, never enlarged. Sizes in device-independent units.</summary>
    internal static Rect PrintBounds(double width,double height,double pageWidth,double pageHeight) {
        var scale=Math.Min(1,Math.Min(pageWidth/Math.Max(1,width),pageHeight/Math.Max(1,height)));
        return new((pageWidth-width*scale)/2,(pageHeight-height*scale)/2,width*scale,height*scale);
    }
    public static void Print(BitmapSource image,Window owner) {
        var dialog=new System.Windows.Controls.PrintDialog();
        if(dialog.ShowDialog()!=true)return;
        var width=image.PixelWidth*96/Math.Max(1,image.DpiX);var height=image.PixelHeight*96/Math.Max(1,image.DpiY);
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())dc.DrawImage(image,PrintBounds(width,height,dialog.PrintableAreaWidth,dialog.PrintableAreaHeight));
        dialog.PrintVisual(visual,"Snaplet");
    }
    public static string? SaveDialog(BitmapSource image,Window? owner=null) {
        var dialog=new SaveFileDialog { FileName=DefaultName(),Filter="PNG 이미지|*.png|JPEG 이미지|*.jpg",DefaultExt=".png",AddExtension=true };
        if((owner is null?dialog.ShowDialog():dialog.ShowDialog(owner))!=true)return null;
        Save(image,dialog.FileName);return dialog.FileName;
    }
    public static void Save(BitmapSource image,string path) {
        BitmapEncoder encoder;
        if(Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg") {
            // JPEG has no alpha: explicitly composite onto white.
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen()) {dc.DrawRectangle(Brushes.White,null,new Rect(0,0,image.PixelWidth,image.PixelHeight));dc.DrawImage(image,new Rect(0,0,image.PixelWidth,image.PixelHeight));}
            var bitmap=new RenderTargetBitmap(image.PixelWidth,image.PixelHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();image=bitmap;
            encoder=new JpegBitmapEncoder { QualityLevel=94 };
        } else encoder=new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {using(var file=File.Create(temp))encoder.Save(file);File.Move(temp,path,true);}
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
    /// <summary>Image files Snaplet opens. WebP and HEIC are decoded by the codecs Windows provides as Store extensions.</summary>
    internal static readonly string[] ImageExtensions=[".png",".jpg",".jpeg",".bmp",".tif",".tiff",".gif",".webp",".heic",".heif"];
    internal static readonly string ImagePattern=string.Join(";",ImageExtensions.Select(e=>"*"+e));
    internal static bool IsImage(string path)=>ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static BitmapSource Load(string path) {
        using var stream=File.OpenRead(path);
        BitmapFrame frame;
        try {frame=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];}
        catch(Exception error) when(error is NotSupportedException or FileFormatException or COMException && MissingCodec(path) is string message) {throw new NotSupportedException(message,error);}
        BitmapSource result=Upright(frame,Orientation(frame));result.Freeze();return result;
    }
    /// <summary>The Store extension to install when Windows cannot decode a format it only supports through one.</summary>
    internal static string? MissingCodec(string path)=>Path.GetExtension(path).ToLowerInvariant() switch {
        ".webp"=>"WebP 이미지를 열 수 없습니다. Microsoft Store에서 'WebP 이미지 확장'을 설치한 뒤 다시 열어 주세요.",
        ".heic" or ".heif"=>"HEIC 이미지를 열 수 없습니다. Microsoft Store에서 'HEIF 이미지 확장'과 'HEVC 비디오 확장'을 설치한 뒤 다시 열어 주세요.",
        _=>null};
    /// <summary>The EXIF orientation (1–8) a camera stored instead of rotating the pixels; 1 when there is none.</summary>
    private static int Orientation(BitmapFrame frame)
    {
        try {
            if(frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("System.Photo.Orientation") && metadata.GetQuery("System.Photo.Orientation") is ushort value && value is >= 1 and <= 8)return value;
        } catch(Exception error) when(error is NotSupportedException or InvalidOperationException or ArgumentException or COMException) {}
        return 1;
    }
    /// <summary>Applies an EXIF orientation, so a phone photo opens the way it was taken.</summary>
    internal static BitmapSource Upright(BitmapSource image,int orientation)=>orientation switch {
        2=>Turn(image,ImageTurn.FlipHorizontal),
        3=>Turn(Turn(image,ImageTurn.Right),ImageTurn.Right),
        4=>Turn(image,ImageTurn.FlipVertical),
        5=>Turn(Turn(image,ImageTurn.Right),ImageTurn.FlipHorizontal),
        6=>Turn(image,ImageTurn.Right),
        7=>Turn(Turn(image,ImageTurn.Left),ImageTurn.FlipHorizontal),
        8=>Turn(image,ImageTurn.Left),
        _=>image};
    public static BitmapSource Demo() {
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen()) {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22,29,44)),null,new Rect(0,0,1200,760));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(35,47,67)),null,new Rect(65,65,1070,630),24,24);
            void Text(string text,double x,double y,double size,Brush color)=>dc.DrawText(new FormattedText(text,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,color,1),new Point(x,y));
            Text("Make your point.",115,120,64,Brushes.White);Text("빠르게 캡처하고, 선명하게 전달하세요.",120,220,30,Brushes.LightSteelBlue);
            for(int i=0;i<3;i++) {dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb((byte)(50+i*12),(byte)(95+i*25),(byte)(160+i*22))),null,new Rect(120+i*320,340,290,240),16,16);Text(new[]{"01  Capture","02  Annotate","03  Share"}[i],145+i*320,410,28,Brushes.White);}
            Text("Test image · 1200 × 760 px",120,630,20,Brushes.LightSteelBlue);
        }
        var bitmap=new RenderTargetBitmap(1200,760,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
    }
}
