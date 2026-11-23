using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>The layer panel lists objects front to back and changes the drawing order as undoable steps.</summary>
internal static class LayerPanelSmokeTest
{
    internal static void Run(string directory,Action<bool,string> check)
    {
        const int width=900,height=520;
        var pixels=Enumerable.Repeat((byte)235,width*height*4).ToArray();
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);source.Freeze();
        var red=new ColorRgba(238,65,77);var blue=new ColorRgba(44,111,224);
        var doc=new EditableDocument(width,height);
        var box=new Annotation(Guid.NewGuid(),AnnotationKind.Rectangle,new(60,60),new(300,200),red,4);
        var spot=new Annotation(Guid.NewGuid(),AnnotationKind.Spotlight,new(380,80),new(700,300),blue,3,Style:new(Dim:.5));
        var note=new Annotation(Guid.NewGuid(),AnnotationKind.Text,new(90,340),new(330,390),red,4,"스포트라이트 위 설명");
        doc.Add(box);doc.Add(spot);doc.Add(note);
        var window=new EditorWindow(source,doc);
        try {
            window.Show();
            check(!window.LayersShown,"the layer panel starts closed");
            window.ShowLayers(true);window.UpdateLayout();
            var panel=window.Layers;
            check(panel.IsVisible && panel.RowNames.SequenceEqual(new[]{"글자 · 스포트라이트 위 설명","Spotlight","사각형"}),"layer panel lists objects front to back");
            var content=(FrameworkElement)window.Content;
            var at=panel.TransformToAncestor((Visual)content).Transform(new Point(0,0));
            check(at.X>content.ActualWidth/2 && Math.Abs(at.X+panel.ActualWidth-content.ActualWidth)<2,"layer panel is docked at the right edge");
            check(panel.SelectRow(box.Id) && panel.Restack(LayerMove.Front) && doc.Annotations[^1].Id==box.Id,"bring to front moves the selected object to the top of the drawing order");
            window.UpdateLayout();
            check(panel.RowNames[0]=="사각형","the list follows the new drawing order");
            doc.Undo();
            check(doc.Annotations[0].Id==box.Id && doc.Annotations[^1].Id==note.Id,"one undo restores the previous order");
            check(!panel.Restack(LayerMove.Back),"sending the bottom object back does nothing");
            check(panel.Restack(LayerMove.Forward) && doc.Annotations[1].Id==box.Id,"bring forward moves the object one step up");
            doc.Undo();window.UpdateLayout();
            var shot=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);shot.Render(content);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(shot));
            using(var file=File.Create(Path.Combine(directory,"editor-layers.png")))png.Save(file);
            window.ShowLayers(false);window.UpdateLayout();
            check(!panel.IsVisible,"the layer panel closes again");
        } finally {window.ShowLayers(false);window.CloseForExit();}
    }
}
