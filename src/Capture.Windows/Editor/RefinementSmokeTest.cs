using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>Real WPF layout and production gesture paths; never injects physical input or captures the desktop.</summary>
internal static class RefinementSmokeTest
{
    internal static void Run(string directory,BitmapSource source,Action<bool,string> check)
    {
        var document=new EditableDocument(source.PixelWidth,source.PixelHeight);
        var window=new EditorWindow(source,document);
        try {
            window.Show();window.UpdateLayout();
            var surface=Descendants(window).OfType<EditorSurface>().Single();
            var scroll=Descendants(window).OfType<ScrollViewer>().Single(v=>v.Content is Grid);
            var anchor=new Point(scroll.ActualWidth*.57,scroll.ActualHeight*.52);
            Point ImageAtAnchor(){var p=scroll.TranslatePoint(anchor,surface);return new(p.X/surface.Zoom,p.Y/surface.Zoom);}
            window.ZoomAt(2,anchor);var original=ImageAtAnchor();
            window.ZoomAt(3,anchor);var zoomed=ImageAtAnchor();
            check((zoomed-original).Length<1,"zoom keeps the same image point under an off-center viewport anchor");
            window.ZoomAt(2,anchor);
            check((ImageAtAnchor()-original).Length<1,"zooming back restores the anchored image position without drift");
            window.ZoomAt(1,new(scroll.ActualWidth/2,scroll.ActualHeight/2));
            check(surface.Zoom==1,"actual image scale remains available after anchored zoom");

            var red=new ColorRgba(235,65,78);
            var rectangle=new Annotation(Guid.NewGuid(),AnnotationKind.Rectangle,new(80,90),new(180,150),red);
            document.Add(rectangle);surface.Selected=rectangle.Id;
            window.NudgeKeyDown(Key.Right,false);window.NudgeKeyDown(Key.Right,false);window.NudgeKeyDown(Key.Down,true);
            window.NudgeKeyUp(Key.Right);
            check(document.IsEditing,"multi-key nudge stays grouped until the last held direction is released");
            window.NudgeKeyUp(Key.Down);
            check(!document.IsEditing && document.Annotations.Single().Start==new PointD(82,100),"repeated and shifted arrow keys move by exact source pixels");
            document.Undo();check(document.Annotations.Single()==rectangle,"one undo restores an entire held-key movement");
            document.Redo();check(document.Annotations.Single().Start==new PointD(82,100),"redo restores the grouped movement");
            surface.Selected=rectangle.Id;window.NudgeKeyDown(Key.Left,false);window.NudgeKeyUp(Key.Left);
            document.Undo();check(document.Annotations.Single().Start==new PointD(82,100),"a separate arrow-key press receives a separate undo entry");

            surface.Selected=rectangle.Id;surface.Tool=null;surface.SetZoom(2);
            surface.BeginPointer(new(120,100));
            check(surface.PointerPosition(new(-80,100)).X==-40,"captured object drag can continue beyond the left canvas edge at non-unit zoom");
            surface.UpdatePointer(surface.PointerPosition(new(-80,100)));surface.EndPointer(surface.PointerPosition(new(-100,100)));
            check(document.Annotations.Single().Start.X<0,"object movement beyond the image edge does not stick to the border");
            document.Undo();check(document.Annotations.Single().Start==new PointD(82,100),"off-canvas drag has one-step undo");
            surface.Tool=AnnotationKind.Rectangle;surface.Selected=null;surface.BeginPointer(new(30,30));
            check(surface.PointerPosition(new(-80,-80))==new PointD(0,0),"new drawing remains clipped to image bounds while existing-object editing can leave them");
            surface.CancelGesture();

            window.ZoomAt(3,new(scroll.ActualWidth/2,scroll.ActualHeight/2));
            surface.Tool=null;surface.Selected=rectangle.Id;surface.BeginPointer(new(120,100));
            var beforePan=scroll.HorizontalOffset;
            window.PanDragAt(new(scroll.ActualWidth-2,scroll.ActualHeight/2));
            check(scroll.HorizontalOffset>beforePan,"dragging against a scrollable viewport edge advances the view");
            surface.CancelGesture();
            window.ZoomAt(1,new(scroll.ActualWidth/2,scroll.ActualHeight/2));

            surface.Tool=AnnotationKind.Text;var callout=surface.NewTextAt(new(220,90));
            var content="긴 말풍선 편집 · Windows 11\n한글과 English를 함께 쓰고 자동 줄바꿈을 확인합니다. "+string.Concat(Enumerable.Repeat("수정해도 폭과 말꼬리를 유지합니다. ",4));
            window.BeginTextEdit(callout,true);window.ActiveTextEditor!.Text=content;window.UpdateLayout();
            var typingWidth=(window.ActiveTextEditor.Width-4)/surface.Zoom;
            var typingHeight=(window.ActiveTextEditor.Height-4)/surface.Zoom;
            check(surface.TextPreview is Annotation typing && typing.Text==content && !ImageRenderer.TextBounds(typing).Contains(ImageRenderer.P(ImageRenderer.TailTip(typing))),"typing keeps a live callout background and positions the new default tail outside the growing body");
            check(window.ActiveTextEditor.Background==Brushes.Transparent,"inline text remains on its actual callout or source image instead of a replacement background");
            check(typingHeight>80 && typingWidth==200,"inline callout grows vertically while keeping a predictable wrapping width");
            check(window.ActiveTextEditor.ExtentHeight<=window.ActiveTextEditor.ViewportHeight+2 && window.ActiveTextEditor.VerticalOffset==0,
                "actual WPF multiline editor fits the text without silently scrolling or clipping its first lines");
            check(!Descendants(window).OfType<Slider>().Any(c=>c.IsVisible),"inline text editing hides unrelated property controls until commit");
            Snapshot(window,Path.Combine(directory,"editor-text-editing.png"));
            check(window.FinishTextEdit(true),"multiline Korean and English callout commits successfully");
            check(Descendants(window).OfType<Slider>().Any(c=>c.IsVisible),"committing inline text restores its editable property panel");
            var text=document.Annotations.Single(a=>a.Kind==AnnotationKind.Text);
            var body=ImageRenderer.TextBounds(text);
            check(!body.Contains(ImageRenderer.P(ImageRenderer.TailTip(text))) && surface.TextPreview is null,"committed multiline callout retains a visible default tail and clears the transient typing layer");
            check(Math.Abs(body.Width-typingWidth)<.01 && Math.Abs(body.Height-typingHeight)<.01,"committed callout preserves the wrapping width and height shown during typing");
            document.Undo();check(document.Annotations.All(a=>a.Kind!=AnnotationKind.Text),"typing and committing new multiline text is a single undo operation");
            document.Redo();text=document.Annotations.Single(a=>a.Kind==AnnotationKind.Text);
            var custom=text with {End=new(text.Start.X+125,text.Start.Y+50),Tail=new(520,60)};
            surface.CommitText(custom,false);window.BeginTextEdit(custom,false);window.ActiveTextEditor!.Text="말꼬리 보존\n변경한 글자와 좁은 폭에서의 줄바꿈";window.UpdateLayout();
            var editedHeight=(window.ActiveTextEditor.Height-4)/surface.Zoom;
            check(window.ActiveTextEditor!.ExtentHeight<=window.ActiveTextEditor.ViewportHeight+2 && window.ActiveTextEditor.VerticalOffset==0,
                "actual WPF text remains fully visible when re-editing a narrow callout");
            check(window.FinishTextEdit(true),"existing resized callout accepts multiline re-editing");
            var edited=document.Annotations.Single(a=>a.Kind==AnnotationKind.Text);
            check(edited.Start==custom.Start && edited.End.X==custom.End.X && edited.Tail==custom.Tail,"re-editing keeps a customized callout width, position and tail target");
            check(Math.Abs(ImageRenderer.TextBounds(edited).Height-editedHeight)<.01,"existing callout preview and committed text height agree");
            document.Undo();check(document.Annotations.Single(a=>a.Kind==AnnotationKind.Text)==custom,"one undo restores text before re-editing without losing its custom geometry");
            window.BeginTextEdit(custom,false);window.ActiveTextEditor!.Text="취소할 내용";window.FinishTextEdit(false);
            check(document.Annotations.Single(a=>a.Kind==AnnotationKind.Text)==custom,"cancelling inline text preserves its prior content and geometry");

            var bottom=surface.NewTextAt(new(20,document.Crop.Bottom-20));
            window.BeginTextEdit(bottom,true);window.ActiveTextEditor!.Text=string.Join("\n",Enumerable.Repeat("캔버스 자동 확장",12));
            var oldCrop=document.Crop;window.FinishTextEdit(true);
            check(document.Crop.Bottom>oldCrop.Bottom,"long callout near the bottom expands the canvas instead of clipping its text");
            document.Undo();check(document.Crop==oldCrop,"undo long text also restores the previous canvas bounds");
            Snapshot(window,Path.Combine(directory,"editor-refined-text.png"));
            var invalid=surface.NewTextAt(new(50,50)) with {Start=new(32760,50),End=new(32960,90)};
            window.BeginTextEdit(invalid,true);window.ActiveTextEditor!.Text="한도 초과 시 입력 보존";
            check(!window.FinishTextEdit(true) && window.ActiveTextEditor?.Text=="한도 초과 시 입력 보존","rejected canvas expansion preserves inline text for correction instead of losing it");
            check(document.Crop==oldCrop && !document.Annotations.Any(a=>a.Id==invalid.Id),"rejected text commit rolls back canvas and annotation together");
            window.FinishTextEdit(false);
            var composed=surface.NewTextAt(new(30,30));window.BeginTextEdit(composed,true);var input=window.ActiveTextEditor!;input.Text="조합 중인 글자";
            input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,new TextComposition(InputManager.Current,input,"ㄱ")) {RoutedEvent=TextCompositionManager.PreviewTextInputStartEvent});
            check(!window.FinishTextEdit(true) && ReferenceEquals(window.ActiveTextEditor,input),"active text composition defers committing and preserves the editor");
            input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,new TextComposition(InputManager.Current,input,"가")) {RoutedEvent=TextCompositionManager.PreviewTextInputEvent});
            check(window.FinishTextEdit(true),"completed text composition permits committing the inline editor");
        } finally {window.CloseForExit();}
        CheckEffects(directory,check);
    }

    private static void CheckEffects(string directory,Action<bool,string> check)
    {
        const int width=900,height=600;
        var bytes=new byte[width*height*4];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++) {
            int i=(y*width+x)*4;var v=(byte)((x/3+y/3)%2==0?0:255);
            bytes[i]=bytes[i+1]=bytes[i+2]=v;bytes[i+3]=255;
        }
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,bytes,width*4);source.Freeze();
        var timings=new List<object>();
        foreach(var kind in new[]{AnnotationKind.Pixelate,AnnotationKind.Blur,AnnotationKind.Erase}) {
            var effect=new Annotation(Guid.NewGuid(),kind,new(80,80),new(820,520),new(240,65,70),6);
            var document=new EditableDocument(width,height);document.Add(effect);
            var exactBefore=Pixels(ImageRenderer.Render(source,document));
            var watch=System.Diagnostics.Stopwatch.StartNew();
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen())ImageRenderer.Paint(dc,source,document.Annotations,new(0,0,width,height),effect.Id);
            var preview=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);preview.Render(visual);var pixels=Pixels(preview);
            watch.Stop();timings.Add(new {effect=kind.ToString(),previewMilliseconds=watch.Elapsed.TotalMilliseconds,sourcePixels=width*height,previewPixelBudget=160000});
            var center=(300*width+450)*4;
            check(pixels[center] is >10 and <245 && pixels[center]==pixels[center+1] && pixels[center+1]==pixels[center+2],kind+" live preview shows the processed image rather than a blue placeholder");
            check(pixels[0]==bytes[0] && pixels[3]==255,kind+" live preview leaves pixels outside the effect untouched");
            check(Pixels(ImageRenderer.Render(source,document)).SequenceEqual(exactBefore),kind+" low-resolution live preview never contaminates full-resolution export caches");
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(preview));using var file=File.Create(Path.Combine(directory,$"editor-live-{kind.ToString().ToLowerInvariant()}.png"));png.Save(file);
        }
        File.WriteAllText(Path.Combine(directory,"editor-effect-timings.json"),System.Text.Json.JsonSerializer.Serialize(timings));
    }
    private static byte[] Pixels(BitmapSource image){var data=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(data,image.PixelWidth*4,0);return data;}
    private static void Snapshot(Window window,string path) {
        window.UpdateLayout();var root=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent) {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var descendant in Descendants(child))yield return descendant;}
    }
}
