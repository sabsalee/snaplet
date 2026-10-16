using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.Editor;

internal static class ShapeCalloutSmokeTest
{
    internal static void Run(string directory,Action<bool,string> check)
    {
        const int width=1000,height=600;
        var pixels=Enumerable.Repeat((byte)255,width*height*4).ToArray();
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);source.Freeze();
        var red=new ColorRgba(238,65,77);var blue=new ColorRgba(44,111,224);
        var doc=new EditableDocument(width,height);
        var rectangle=new Annotation(Guid.NewGuid(),AnnotationKind.Rectangle,new(60,60),new(220,140),blue,4,Tail:new(120,230),Style:new(Fill:ShapeFill.Solid,ShapeTail:true));
        doc.Add(rectangle);
        byte[] Render(EditableDocument document) {
            var bitmap=ImageRenderer.Render(source,document);var data=new byte[width*height*4];bitmap.CopyPixels(data,width*4,0);return data;
        }
        var solid=Render(doc);
        check(solid[(200*width+125)*4]>200 && solid[(200*width+125)*4+2]<60,"filled rectangle callout paints its tail outside the body");
        doc.Replace(rectangle with {Style=new(ShapeTail:true)});
        var outlined=Render(doc);
        check(outlined[(140*width+134)*4+1]==255,"outline callout has no internal stroke across the tail base");
        var surface=new EditorSurface(source,doc);
        check(!surface.Hit(doc.Annotations[0],new(140,100)),"empty outline callout center remains transparent to selection");
        doc.Replace(rectangle with {Style=new(Fill:ShapeFill.Solid,ShapeTail:false)});
        var hidden=Render(doc);
        check(hidden[(200*width+125)*4+2]==255,"shape tail can be hidden without deleting its stored tip");
        doc.Undo();check(AnnotationGeometry.HasTail(doc.Annotations[0]) && doc.Annotations[0].Tail==rectangle.Tail,"undo restores tail option and original target together");
        var defaultShape=rectangle with {Tail=null,Style=new()};
        check(!AnnotationGeometry.HasTail(defaultShape),"ordinary shapes start without a tail");
        var edge=surface.WithReachableShapeTail(rectangle with {Start=new(800,540),End=new(980,595),Tail=null});
        check(edge.Tail is PointD t && doc.Crop.Contains(t) && t.Y<edge.Start.Y,"enabling a tail near bottom edge places its handle above the shape");
        foreach(var kind in new[]{AnnotationKind.Rectangle,AnnotationKind.Ellipse}) {
            var tiny=rectangle with {Kind=kind,Start=new(20,20),End=new(22,22),Tail=new(24,30)};
            check(!ImageRenderer.ShapeGeometry(tiny).Bounds.IsEmpty,kind+" tiny callout path does not fail corner clamping");
        }
        doc.Replace(rectangle with {Style=new(Fill:ShapeFill.Hatched,ShapeTail:true)});
        var hatched=Render(doc);int colored=0;
        for(int y=145;y<225;y++)for(int x=110;x<150;x++)if(hatched[(y*width+x)*4+2]<200)colored++;
        check(colored>10,"diagonal hatch extends into the shape tail");
        var shapes=new List<Annotation>();
        for(int row=0;row<2;row++)for(int col=0;col<4;col++) {
            var x=55+col*245;var y=80+row*270;
            var style=new AnnotationStyle(Fill:col==0?ShapeFill.Outline:col==1?ShapeFill.Solid:col==2?ShapeFill.Hatched:ShapeFill.Translucent,Sketch:col==3,ShapeTail:true);
            var tail=col switch {0=>new PointD(x+45,y+165),1=>new PointD(x+175,y-35),2=>new PointD(x+195,y+150),_=>new PointD(x-28,y+110)};
            var a=new Annotation(Guid.NewGuid(),row==0?AnnotationKind.Rectangle:AnnotationKind.Ellipse,new(x,y),new(x+165,y+95),row==0?blue:red,4,Tail:tail,Style:style);
            shapes.Add(a);
            check(ImageRenderer.ShapeGeometry(a).Bounds.Contains(ImageRenderer.P(tail)) || ImageRenderer.ShapeGeometry(a).Bounds.IntersectsWith(new Rect(tail.X-1,tail.Y-1,2,2)),"callout path reaches the editable tail target "+row+":"+col);
        }
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen()) {
            ImageRenderer.Paint(dc,source,shapes);
            dc.DrawText(new FormattedText("Shape callouts: outline / solid / hatched / organic translucent",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),18,Brushes.DimGray,1),new(35,20));
        }
        var gallery=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);gallery.Render(visual);gallery.Freeze();
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(gallery));using var file=File.Create(Path.Combine(directory,"editor-shape-callouts.png"));png.Save(file);
    }
}
