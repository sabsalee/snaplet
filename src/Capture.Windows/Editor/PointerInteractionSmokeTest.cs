using System.Windows.Input;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>Runs the same image-coordinate gesture path used by WPF mouse handlers.</summary>
internal static class PointerInteractionSmokeTest
{
    internal static void Run(BitmapSource source,Action<bool,string> check)
    {
        var red=new ColorRgba(240,65,70);
        EditorSurface Surface(out EditableDocument document) {document=new(640,420);return new(source,document) {SnapToObjects=false};}
        var pen=Surface(out var penDoc);pen.Tool=AnnotationKind.Pen;
        pen.BeginPointer(new(40,40));pen.EndPointer(new(40,40));
        check(penDoc.Annotations.Single().Points is {Count:1},"pen click commits a visible single-point stroke");
        check(pen.Hit(penDoc.Annotations.Single(),new(40,40)),"single-point pen can be reselected");
        penDoc.Undo();check(penDoc.Annotations.Count==0,"pen click is one undo operation");
        penDoc.Redo();check(penDoc.Annotations.Count==1,"redo restores single-point stroke");

        var line=Surface(out var lineDoc);line.Tool=AnnotationKind.Line;
        line.BeginPointer(new(20,20));line.EndPointer(new(200,80));
        check(lineDoc.Annotations.Single().End==new PointD(200,80),"fast drag commits release position even without intermediate move events");
        line.Tool=null;var id=lineDoc.Annotations.Single().Id;line.Selected=id;
        line.BeginPointer(new(100,47));line.EndPointer(new(101,47));
        check(lineDoc.Annotations.Single().Start==new PointD(20,20),"one pixel click jitter does not move an annotation");
        lineDoc.Undo();check(lineDoc.Annotations.Count==0,"click jitter does not add an undo entry");

        var rectangle=Surface(out var rectangleDoc);rectangle.Tool=AnnotationKind.Rectangle;
        rectangle.BeginPointer(new(20,20),ModifierKeys.Shift);rectangle.EndPointer(new(100,20),ModifierKeys.Shift);
        check(rectangleDoc.Annotations.Single().Bounds==new RectD(20,20,80,80),"horizontal Shift drag creates a square rather than a zero-height shape");
        var rectangleId=rectangleDoc.Annotations.Single().Id;rectangle.Selected=rectangleId;
        rectangle.BeginPointer(new(200,200));rectangle.EndPointer(new(200,200));
        check(rectangleDoc.Annotations.Count==1 && rectangle.Selected==rectangleId,"empty shape click creates no invisible object and preserves selection");
        rectangle.BeginPointer(new(200,200));rectangle.UpdatePointer(new(300,280));
        var restoredSelectionNotified=false;rectangle.SelectionChanged+=()=>restoredSelectionNotified=true;rectangle.CancelGesture();
        check(restoredSelectionNotified && rectangle.Selected==rectangleId && rectangleDoc.Annotations.Count==1,"cancel drawing restores prior selection and creates no annotation");

        var small=Surface(out var smallDoc);
        var box=new Annotation(Guid.NewGuid(),AnnotationKind.Rectangle,new(100,100),new(112,112),red);
        smallDoc.Add(box);small.Selected=box.Id;
        small.BeginPointer(new(112,112));small.EndPointer(new(140,150));
        check(smallDoc.Annotations.Single().Start==box.Start && smallDoc.Annotations.Single().End==new PointD(140,150),"overlapping small-shape handles choose the nearest corner");
        smallDoc.Undo();check(smallDoc.Annotations.Single()==box,"corner resize has one-step undo");

        var curve=Surface(out var curveDoc);
        var arrow=new Annotation(Guid.NewGuid(),AnnotationKind.Arrow,new(40,100),new(240,100),red,Control:new(140,200));
        curveDoc.Add(arrow);curve.Selected=arrow.Id;
        curve.BeginPointer(arrow.End);curve.EndPointer(new(300,140));
        var resized=curveDoc.Annotations.Single();
        check(resized.Control==new PointD(170,220),"curved arrow endpoint keeps its bow relative to the new chord");
        check(resized.Start==arrow.Start,"endpoint edit leaves opposite arrow endpoint fixed");
        curveDoc.Undo();check(curveDoc.Annotations.Single()==arrow,"curved endpoint edit undoes as one operation");
        curve.BeginPointer(new(140,150));curve.EndPointer(new(140,180));
        check(AnnotationGeometry.CurvePoint(curveDoc.Annotations.Single(),.5)==new PointD(140,180),"middle arrow handle bends through the pointer");

        var image=Surface(out var imageDoc);
        var asset=new RasterImage(2,1,new byte[]{0,0,0,255,0,0,0,255});
        var picture=new Annotation(Guid.NewGuid(),AnnotationKind.Image,new(100,100),new(300,200),red,Image:asset);
        imageDoc.Add(picture);image.Selected=picture.Id;
        image.BeginPointer(new(300,200));image.EndPointer(new(200,200));
        check(imageDoc.Annotations.Single().Bounds==new RectD(100,100,100,50),"image corner can shrink with one-axis movement and preserves aspect ratio");
        imageDoc.Undo();image.Selected=picture.Id;
        image.BeginPointer(new(300,200),ModifierKeys.Shift);image.EndPointer(new(200,200),ModifierKeys.Shift);
        check(imageDoc.Annotations.Single().Bounds==new RectD(100,100,100,100),"Shift image corner permits free aspect ratio");

        var tail=Surface(out var tailDoc);
        var callout=box with {End=new(200,160),Style=new(ShapeTail:true),Tail=new(150,220)};
        tailDoc.Add(callout);tail.Selected=callout.Id;
        tail.BeginPointer(new(150,220));tail.EndPointer(new(250,100));
        check(tailDoc.Annotations.Single().Tail==new PointD(250,100),"shape tail handle can move to another side independently");
        check(tailDoc.Annotations.Single().Bounds==callout.Bounds,"tail edit leaves the shape body fixed");
        tailDoc.Undo();check(tailDoc.Annotations.Single()==callout,"tail adjustment has a single undo step");
    }
}
