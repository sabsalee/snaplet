using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.Editor;

internal static class ExperienceSmokeTest
{
    internal static void Run(string directory,Action<bool,string> check)
    {
        const int width=1000,height=760;
        var bytes=Enumerable.Repeat((byte)255,width*height*4).ToArray();
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,bytes,width*4);source.Freeze();
        var doc=new EditableDocument(width,height);var surface=new EditorSurface(source,doc);surface.Tool=null;
        var bubble=new Annotation(Guid.NewGuid(),AnnotationKind.Text,new(100,100),new(300,160),new(239,68,83),3,"Editable callout",Tail:new(160,215),Style:new(CornerRadius:16,TailWidth:32));
        doc.Add(bubble);surface.UpdateHover(new(180,125));
        check(surface.Cursor==Cursors.SizeAll && surface.PointerHint.Contains("더블클릭"),"hovering a text body advertises dragging and text editing before selection");
        surface.Selected=bubble.Id;surface.UpdateHover(new(160,215));
        check(surface.PointerHint.Contains("말꼬리"),"tail handle explains its role independently of resize handles");
        surface.UpdateHover(new(116,116));check(surface.PointerHint.Contains("모서리"),"corner radius handle advertises direct rounding adjustment");
        surface.BeginPointer(new(116,116));surface.EndPointer(new(125,125));
        check(doc.Annotations.Single().Appearance.CornerRadius==25,"dragging the radius diamond changes radius by pointer displacement");
        doc.Undo();check(doc.Annotations.Single()==bubble,"corner rounding is one undoable action preserving text and tail");
        surface.BeginPointer(bubble.Tail!.Value);surface.EndPointer(new(355,130));
        var moved=doc.Annotations.Single();check(moved.Tail==new PointD(355,130) && surface.Hit(moved,new(342,130)),"tail can switch sides and remains selectable on its visible tapered body");
        doc.Undo();surface.Tool=AnnotationKind.Rectangle;bool edited=false;surface.TextEditRequested+=(_,_)=>edited=true;
        surface.BeginPointer(new(180,125),clickCount:2);check(edited && doc.Annotations.Count==1,"double clicking text enters editing even while another drawing tool is active");
        surface.Tool=AnnotationKind.Text;surface.Selected=null;surface.UpdateHover(new(700,700));check(surface.Cursor==Cursors.IBeam,"text tool uses an insertion cursor on empty canvas");
        var narrow=bubble with {End=new(124,125),Text="",Style=bubble.Appearance with {CornerRadius=48}};
        foreach(var target in new[]{new PointD(90,120),new PointD(150,110),new PointD(112,80),new PointD(112,190)}) {
            var path=ImageRenderer.TailGeometry(narrow with {Tail=target});
            check(!path.Bounds.IsEmpty && double.IsFinite(path.Bounds.Width),"maximal rounding on a small callout keeps every tail direction finite");
        }
        // Export an actual renderer gallery: the preset previews and final export share these paths.
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen()) {
            dc.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));
            var kinds=new[]{AnnotationKind.Arrow,AnnotationKind.Text,AnnotationKind.Step,AnnotationKind.Rectangle,AnnotationKind.Ellipse,AnnotationKind.Highlight,AnnotationKind.Pen,AnnotationKind.Line};
            for(int row=0;row<kinds.Length;row++) {
                dc.DrawText(new FormattedText(kinds[row].ToString(),System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),13,Brushes.SlateGray,1),new(18,35+row*90));
                var presets=EditorPresets.For(kinds[row]);
                for(int col=0;col<presets.Count;col++) {
                    dc.PushTransform(new TranslateTransform(150+col*210,12+row*90));
                    ImageRenderer.PaintAnnotation(dc,source,presets[col].Sample(kinds[row]));dc.Pop();
                }
            }
        }
        var output=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);output.Render(visual);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(output));using(var file=File.Create(Path.Combine(directory,"editor-style-presets.png")))png.Save(file);
        var window=new EditorWindow(source,doc);try {
            window.Show();window.UpdateLayout();
            var root=(FrameworkElement)window.Content;
            var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,"editor-experience.png"));encoder.Save(file);
        } finally {window.CloseForExit();}
    }
}
