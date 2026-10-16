using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Capture.Windows;
internal static class ImageActions
{
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
    public static string DefaultName()=>"Snaplet-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")[..4]+".png";
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
    public static BitmapSource Load(string path) {
        using var stream=File.OpenRead(path);
        var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        BitmapSource result=decoder.Frames[0];result.Freeze();return result;
    }
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
