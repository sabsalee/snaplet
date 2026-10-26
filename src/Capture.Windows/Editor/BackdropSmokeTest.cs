using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>Backdrop export pixels: size, fill, rounded clip, shadow and transparency. Renders with WPF; no desktop capture.</summary>
internal static class BackdropSmokeTest
{
    internal static void Run(string directory)
    {
        var checks=new List<string>();void Check(bool value,string label){if(!value)throw new InvalidOperationException("Backdrop check failed: "+label);checks.Add(label);}
        var content=Solid(200,100,Color.FromRgb(20,120,220));
        var doc=new EditableDocument(200,100);
        Check(ReferenceEquals(BackdropRenderer.Export(content,doc),content) || BackdropRenderer.Export(content,doc).PixelWidth==200,"disabled backdrop exports the plain image");
        doc.SetBackdrop(new Backdrop(Enabled:true,Preset:0,Padding:40,CornerRadius:16,Shadow:.8));
        var framed=BackdropRenderer.Export(content,doc);
        Check(framed.PixelWidth==280 && framed.PixelHeight==180,"padding adds whole pixels on every side");
        var corner=Pixel(framed,2,2);var preset=BackdropRenderer.Presets[0].From;
        Check(Math.Abs(corner.R-preset.R)<16 && Math.Abs(corner.G-preset.G)<16 && corner.A==255,"outer corner shows the gradient start colour");
        var center=Pixel(framed,140,90);Check(center.R==20 && center.G==120 && center.B==220,"content pixels are unchanged at the centre");
        var roundedCorner=Pixel(framed,41,41);Check(!(roundedCorner.B==220 && roundedCorner.R==20),"rounded clip removes the content's square corner");
        var below=Pixel(framed,140,40+100+8);var beside=Pixel(framed,140,4);
        Check(below.R+below.G+below.B<beside.R+beside.G+beside.B-20,"shadow darkens the backdrop under the image");
        doc.SetBackdrop(doc.Backdrop with {Fill=BackdropFill.Transparent,Shadow=0});
        var clear=BackdropRenderer.Export(content,doc);Check(Pixel(clear,3,3).A==0 && Pixel(clear,140,90).A==255,"transparent backdrop keeps an alpha margin");
        doc.SetBackdrop(doc.Backdrop with {Fill=BackdropFill.Solid,Color=new ColorRgba(255,255,255),CornerRadius=0});
        var square=BackdropRenderer.Export(content,doc);Check(Pixel(square,40,40).B==220,"zero radius keeps square corners");
        Check(Pixel(square,10,10) is {R:255,G:255,B:255},"solid backdrop uses its colour");
        doc.Undo();doc.Undo();doc.Undo();Check(!doc.Backdrop.Enabled,"backdrop changes undo back to the plain image");
        // Eyedropper samples the composited image and computes contrast between the two picks.
        var split=new DrawingVisual();using(var dc=split.RenderOpen()){dc.DrawRectangle(Brushes.Black,null,new Rect(0,0,100,80));dc.DrawRectangle(Brushes.White,null,new Rect(100,0,100,80));}
        var splitImage=new RenderTargetBitmap(200,80,96,96,PixelFormats.Pbgra32);splitImage.Render(split);splitImage.Freeze();
        var editor=new EditorWindow(splitImage,closeOnDismiss:true);
        try {
            editor.Show();editor.UpdateLayout();
            editor.SimulatePick(new PointD(20,20),false);editor.SimulatePick(new PointD(150,20),true);
            var state=editor.PickState;
            Check(state.Active && state.Foreground==new ColorRgba(0,0,0) && state.Background==new ColorRgba(255,255,255),"eyedropper picks foreground and Shift background from the image");
            Check(Math.Abs(ColorMath.WcagRatio(state.Foreground,state.Background)-21)<1e-9,"picked colours give the 21:1 WCAG maximum");
            Check(editor.SampleColor(new PointD(500,500)) is null,"sampling outside the image is ignored");
        } finally {editor.CloseForExit();}
        var gallery=new EditableDocument(200,100);gallery.SetBackdrop(new Backdrop(Enabled:true,Preset:2,Padding:48,CornerRadius:14,Shadow:.6));
        Save(BackdropRenderer.Export(content,gallery),Path.Combine(directory,"backdrop-preview.png"));
        File.WriteAllText(Path.Combine(directory,"backdrop-smoke.json"),System.Text.Json.JsonSerializer.Serialize(new{component="backdrop export",passed=checks.Count,checks}));
    }
    private static BitmapSource Solid(int w,int h,Color c)
    {
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())dc.DrawRectangle(new SolidColorBrush(c),null,new Rect(0,0,w,h));
        var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
    }
    private static (byte R,byte G,byte B,byte A) Pixel(BitmapSource image,int x,int y)
    {
        var px=new byte[4];image.CopyPixels(new Int32Rect(x,y,1,1),px,4,0);return (px[2],px[1],px[0],px[3]);
    }
    private static void Save(BitmapSource image,string path){var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(image));using var f=File.Create(path);e.Save(f);}
}
