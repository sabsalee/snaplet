using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Automation;
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
        surface.Selected=bubble.Id;surface.UpdateHover(ImageRenderer.TailTip(bubble));
        check(surface.PointerHint.Contains("말꼬리"),"tail handle explains its role independently of resize handles");
        surface.UpdateHover(new(284,86));check(surface.PointerHint.Contains("모서리"),"corner radius handle advertises direct rounding adjustment");
        surface.BeginPointer(new(284,86));surface.EndPointer(new(284,86));doc.Undo();
        check(doc.Annotations.Count==0,"clicking a radius control without dragging does not create a spurious undo entry");
        doc.Redo();surface.Selected=bubble.Id;
        surface.BeginPointer(new(284,86));surface.EndPointer(new(275,95));
        check(doc.Annotations.Single().Appearance.CornerRadius>16 && doc.Annotations.Single().Appearance.CornerRadius<=48,"dragging the exterior radius diamond inward increases rounding without covering text");
        doc.Undo();check(doc.Annotations.Single()==bubble,"corner rounding is one undoable action preserving text and tail");
        surface.BeginPointer(ImageRenderer.TailTip(bubble));surface.EndPointer(new(355,130));
        var moved=doc.Annotations.Single();check(moved.Tail==new PointD(355,130) && surface.Hit(moved,new(325,130)) && ImageRenderer.TailTip(moved).X<=336,"tail can switch sides while its visible body and handle share the bounded reach");
        doc.Undo();surface.Tool=AnnotationKind.Rectangle;bool edited=false;surface.TextEditRequested+=(_,_)=>edited=true;
        surface.BeginPointer(new(180,125),clickCount:2);check(edited && doc.Annotations.Count==1,"double clicking text enters editing even while another drawing tool is active");
        surface.Tool=AnnotationKind.Text;surface.Selected=null;surface.UpdateHover(new(700,700));check(surface.Cursor==Cursors.IBeam,"text tool uses an insertion cursor on empty canvas");
        edited=false;surface.UpdateHover(new(180,125));surface.BeginPointer(new(180,125));
        check(edited && surface.Cursor==Cursors.IBeam && doc.Annotations.Count==1,"text tool clicks edit existing text instead of creating an overlapping object");
        var narrow=bubble with {End=new(124,125),Text="",Style=bubble.Appearance with {CornerRadius=48}};
        foreach(var target in new[]{new PointD(90,120),new PointD(150,110),new PointD(112,80),new PointD(112,190)}) {
            var path=ImageRenderer.TailGeometry(narrow with {Tail=target});
            check(!path.Bounds.IsEmpty && double.IsFinite(path.Bounds.Width),"maximal rounding on a small callout keeps every tail direction finite");
        }
        var corners=new DrawingVisual();using(var dc=corners.RenderOpen()) {
            dc.DrawRectangle(Brushes.White,null,new Rect(0,0,880,320));
            for(int row=0;row<2;row++)for(int column=0;column<4;column++) {
                double x=column*220+50,y=row*160+55;
                var targets=new[]{new PointD(x-400,y-400),new PointD(x+520,y-400),new PointD(x+520,y+450),new PointD(x-400,y+450)};
                var shape=new Annotation(Guid.NewGuid(),AnnotationKind.Rectangle,new(x,y),new(x+120,y+50),new(239,68,83),2,Tail:targets[column],Style:new(Fill:ShapeFill.Solid,ShapeTail:true,CornerRadius:row==0?12:25,TailWidth:32));
                var tail=ImageRenderer.TailGeometry(shape);var body=AnnotationPaths.Shape(shape);
                check(new CombinedGeometry(GeometryCombineMode.Intersect,body,tail).GetArea()>0,"corner callout tail overlaps its rounded body without a detached seam");
                check(tail.Bounds.Width<220 && tail.Bounds.Height<150,"distant corner targets cannot stretch callout silhouettes into long spikes");
                ImageRenderer.PaintAnnotation(dc,source,shape);
            }
        }
        var cornerImage=new RenderTargetBitmap(880,320,96,96,PixelFormats.Pbgra32);cornerImage.Render(corners);
        var cornerPng=new PngBitmapEncoder();cornerPng.Frames.Add(BitmapFrame.Create(cornerImage));using(var file=File.Create(Path.Combine(directory,"editor-callout-corners.png")))cornerPng.Save(file);
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
            check(Capture.Windows.UI.WindowAppearance.GetDarkChrome(window)==window.UsesDarkTheme,"editor title bar matches the editor theme that follows the Windows app setting");
            window.InspectorExpanded=true;window.UpdateLayout();
            var root=(FrameworkElement)window.Content;
            var number=Descendants(root).OfType<TextBox>().Single(c=>AutomationProperties.GetName(c)=="말꼬리 너비 숫자 입력");
            check(number.Parent is StackPanel row && row.Children.OfType<Slider>().Count()==1 && row.Children.OfType<TextBlock>().Count()==1,"label, slider and number form one wrapping unit without orphaned values");
            number.Focus();number.Text="48";
            number.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(number),0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent});
            check(doc.Annotations.Single().Appearance.TailWidth==48,"numeric property input applies precise values through the production control path");
            doc.Undo();check(doc.Annotations.Single().Appearance.TailWidth==32,"numeric property input is one undoable style change");
            window.UpdateLayout();
            var liveSurface=Descendants(root).OfType<EditorSurface>().Single();
            var beforeEdit=liveSurface.TranslatePoint(new Point(100,100),root);var beforeZoom=liveSurface.Zoom;
            window.BeginTextEdit(doc.Annotations.Single(),false);window.UpdateLayout();
            check((liveSurface.TranslatePoint(new Point(100,100),root)-beforeEdit).Length<.1 && Math.Abs(liveSurface.Zoom-beforeZoom)<.0001,"entering text editing keeps the canvas and insertion point stationary");
            window.FinishTextEdit(false);window.UpdateLayout();
            check((liveSurface.TranslatePoint(new Point(100,100),root)-beforeEdit).Length<.1 && Math.Abs(liveSurface.Zoom-beforeZoom)<.0001,"leaving text editing preserves the same viewport");
            var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,"editor-experience.png"));encoder.Save(file);
        } finally {window.CloseForExit();}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
}
