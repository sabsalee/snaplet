using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Capture.Core;

namespace Capture.Windows.Editor;

internal sealed class EditorSurface : FrameworkElement
{
    private readonly BitmapSource source;
    internal readonly EditableDocument Document;
    internal AnnotationKind? Tool;
    internal bool CropTool;
    /// <summary>Eyedropper mode: clicks report image points instead of drawing or selecting.</summary>
    internal bool PickColorTool;
    internal event Action<PointD,ModifierKeys>? ColorPickRequested;
    internal bool SnapToObjects=true;
    private double? guideX,guideY;
    private Guid? hovered;
    private int? hoveredControl;
    internal string PointerHint { get; private set; }="";
    internal event Action<string>? PointerHintChanged;
    private readonly DispatcherTimer digitCommit=new() {Interval=TimeSpan.FromMilliseconds(800)};
    internal ColorRgba Ink = new(240,65,70,255);
    internal double Thickness = 4;
    internal AnnotationStyle InkStyle = new();
    internal double Zoom = 1;
    internal Guid? Selected;
    internal RectD? PendingCrop;
    /// <summary>A pending range box exists that the pointer can move and resize: the crop tool, or a drag on empty canvas in select mode.</summary>
    internal bool RegionActive=>PendingCrop is not null && (CropTool || Tool is null && !PickColorTool);
    private bool IsRegionGesture=>gesture is Gesture.CropNew or Gesture.CropMove or Gesture.CropResize;
    internal event Action? SelectionChanged;
    internal event Action<string>? OperationFailed;
    internal event Action<Annotation,bool>? TextEditRequested;
    private enum Gesture { None, Create, Move, Resize, Bend, Tail, Corner, CropNew, CropMove, CropResize, SampleMove, SampleResize }
    private Gesture gesture;
    private PointD start,current;
    private Annotation? original,draft;
    private List<PointD>? points;
    private int handle;
    private Vector grabOffset;
    private ModifierKeys pointerModifiers;
    private Vector cornerDirection;
    private double cornerRadiusStart;
    private Guid? selectionBeforeGesture;
    private RectD cropBefore;
    private string digits="";
    private DateTime digitTime;
    private Guid? digitObject;
    private Guid? editingTextId;
    internal Annotation? TextPreview { get; private set; }
    internal void SetTextPreview(Annotation preview){TextPreview=preview;InvalidateVisual();}
    internal void SetTextEditing(Guid? id){editingTextId=id;TextPreview=null;InvalidateVisual();}
    internal bool IsDragging=>gesture!=Gesture.None;
    internal Annotation? Selection => Document.Annotations.FirstOrDefault(a=>a.Id==Selected);
    internal EditorSurface(BitmapSource image,EditableDocument document)
    {
        source=image;Document=document;Focusable=true;Cursor=Cursors.Cross;
        digitCommit.Tick+=(_,_)=>FinishNumberEdit();
        LostKeyboardFocus+=(_,_)=>FinishNumberEdit();
        Document.Changed+=(_,_)=>{if(Selected is Guid id && !Document.Annotations.Any(a=>a.Id==id))Selected=null;UpdateSize();InvalidateVisual();};
        UpdateSize();
    }
    internal void SetZoom(double zoom) {Zoom=Math.Clamp(zoom,.05,8);UpdateSize();InvalidateVisual();}
    private void UpdateSize() {Width=Document.Crop.Width*Zoom;Height=Document.Crop.Height*Zoom;}
    private PointD ImagePoint(MouseEventArgs e)=>PointerPosition(e.GetPosition(this));
    internal PointD PointerPosition(Point position)
    {
        var x=position.X/Zoom+Document.Crop.X;var y=position.Y/Zoom+Document.Crop.Y;
        var clipped=gesture is Gesture.None or Gesture.Create or Gesture.CropNew or Gesture.CropMove or Gesture.CropResize
            || gesture==Gesture.Resize && original?.Kind is AnnotationKind.Pixelate or AnnotationKind.Blur or AnnotationKind.Erase;
        return clipped?new(Math.Clamp(x,Document.Crop.X,Document.Crop.Right),Math.Clamp(y,Document.Crop.Y,Document.Crop.Bottom))
            :new(Math.Clamp(x,-32768,32768),Math.Clamp(y,-32768,32768));
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushClip(new RectangleGeometry(new Rect(0,0,Width,Height)));
        dc.PushTransform(new ScaleTransform(Zoom,Zoom));dc.PushTransform(new TranslateTransform(-Document.Crop.X,-Document.Crop.Y));
        var preview=Document.Annotations.Where(a=>a.Id!=editingTextId).Select(a=>a.Id==draft?.Id?PreviewAnnotation(draft):a).ToList();
        if(draft!=null && !preview.Any(a=>a.Id==draft.Id))preview.Add(PreviewAnnotation(draft));
        var effectDraft=draft?.Kind is AnnotationKind.Pixelate or AnnotationKind.Blur or AnnotationKind.Erase;
        ImageRenderer.Paint(dc,source,preview,ImageRenderer.RectOf(Document.Crop),effectDraft?draft!.Id:null);
        if(TextPreview is Annotation typing && typing.Appearance.Bubble) {
            var body=ImageRenderer.TextBounds(typing);
            ImageRenderer.PaintAnnotation(dc,source,typing with {Text="",End=new(body.Right,body.Bottom)});
        }
        if(PendingCrop is RectD crop) {
            var outer=new RectangleGeometry(ImageRenderer.RectOf(Document.Crop));var inner=new RectangleGeometry(ImageRenderer.RectOf(crop));
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(110,0,0,0)),null,new CombinedGeometry(GeometryCombineMode.Exclude,outer,inner));
            dc.DrawRectangle(null,new Pen(Brushes.White,1/Zoom){DashStyle=DashStyles.Dash},ImageRenderer.RectOf(crop));
            foreach(var h in BoxHandles(ImageRenderer.RectOf(crop)))DrawHandle(dc,h.Point);
            DrawRegionSize(dc,crop);
        } else if(!CropTool && (draft ?? Selection) is Annotation a && a.Id!=editingTextId) {
            var shown=PreviewAnnotation(a);
            DrawSelection(dc,shown,false);
            foreach(var h in Handles(shown))DrawControl(dc,h);
        }
        if(gesture==Gesture.None && hovered is Guid hover && hover!=Selected && Document.Annotations.FirstOrDefault(x=>x.Id==hover) is Annotation over) {
            DrawSelection(dc,over,true);
        }
        var guidePen=new Pen(new SolidColorBrush(Color.FromArgb(210,50,140,255)),1/Zoom) {DashStyle=DashStyles.Dash};
        if(guideX is double gx)dc.DrawLine(guidePen,new(gx,Document.Crop.Y),new(gx,Document.Crop.Bottom));
        if(guideY is double gy)dc.DrawLine(guidePen,new(Document.Crop.X,gy),new(Document.Crop.Right,gy));
        dc.Pop();dc.Pop();dc.Pop();
    }
    private static readonly Brush RegionLabelFill=CreateFrozen(new SolidColorBrush(Color.FromArgb(200,20,22,26)));
    private static Brush CreateFrozen(Brush brush){brush.Freeze();return brush;}
    /// <summary>Whole-pixel size of the range box in a small dark tag below it, or above when it would leave the canvas.</summary>
    private void DrawRegionSize(DrawingContext dc,RectD region)
    {
        var size=ToPixels(region);
        var text=new FormattedText($"{size.Width} × {size.Height}",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),11/Zoom,Brushes.White,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double padX=6/Zoom,padY=2/Zoom,gap=6/Zoom;
        var w=text.Width+2*padX;var h=text.Height+2*padY;
        var x=Math.Clamp(region.X,Document.Crop.X,Math.Max(Document.Crop.X,Document.Crop.Right-w));
        var y=region.Bottom+gap+h<=Document.Crop.Bottom?region.Bottom+gap:Math.Max(Document.Crop.Y,region.Y-gap-h);
        dc.DrawRoundedRectangle(RegionLabelFill,null,new Rect(x,y,w,h),4/Zoom,4/Zoom);
        dc.DrawText(text,new Point(x+padX,y+padY));
    }
    private static readonly Brush SelectionInk=new SolidColorBrush(Color.FromRgb(104,165,255));
    private void DrawSelection(DrawingContext dc,Annotation a,bool hover)
    {
        var pen=new Pen(SelectionInk,(hover?1.5:1)/Zoom);
        if(a.Kind is AnnotationKind.Arrow or AnnotationKind.Line) {
            dc.DrawGeometry(null,pen,ImageRenderer.ArrowStrokeGeometry(a));return;
        }
        if(a.Kind==AnnotationKind.Pen) {dc.DrawGeometry(null,pen,ImageRenderer.PenGeometry(a));return;}
        var b=ImageRenderer.BoundsOf(a);var outset=hover?2/Zoom:HandleOutset/Zoom;b.Inflate(outset,outset);
        dc.DrawRectangle(null,pen,b);
    }
    private void DrawControl(DrawingContext dc,Handle h)
    {
        var p=ImageRenderer.P(h.Point);var pen=new Pen(SelectionInk,1.25/Zoom);
        if(hoveredControl==h.Index && gesture==Gesture.None)dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(65,104,165,255)),null,p,9/Zoom,9/Zoom);
        if(h.Index==-6 && (draft ?? Selection) is Annotation selected) {
            var b=ImageRenderer.BoundsOf(selected);var anchor=new Point(Math.Clamp(p.X,b.Left,b.Right),Math.Clamp(p.Y,b.Top,b.Bottom));
            dc.DrawLine(new Pen(SelectionInk,1/Zoom){DashStyle=DashStyles.Dot},anchor,p);
        }
        if(h.Index==-6) {
            var radius=5/Zoom;var diamond=new StreamGeometry();
            using(var g=diamond.Open()) {g.BeginFigure(new(p.X,p.Y-radius),true,true);g.LineTo(new(p.X+radius,p.Y),true,false);g.LineTo(new(p.X,p.Y+radius),true,false);g.LineTo(new(p.X-radius,p.Y),true,false);}
            dc.DrawGeometry(Brushes.MediumAquamarine,pen,diamond);
        } else if(h.Index==-4)dc.DrawEllipse(SelectionInk,new Pen(Brushes.White,1.5/Zoom),p,5/Zoom,5/Zoom);
        else DrawHandle(dc,h.Point);
    }
    // Reference handles: white rounded 8 px squares with a gray border; shape handles sit 6 px outside the bounds.
    private const double HandleOutset=6;
    private static readonly Pen HandleBorder=CreateHandleBorder();
    private static Pen CreateHandleBorder(){var pen=new Pen(new SolidColorBrush(Color.FromRgb(142,142,147)),1);pen.Freeze();return pen;}
    private void DrawHandle(DrawingContext dc,PointD p)
    {
        var border=new Pen(HandleBorder.Brush,1/Zoom);
        dc.DrawRoundedRectangle(Brushes.White,border,new(p.X-4/Zoom,p.Y-4/Zoom,8/Zoom,8/Zoom),2/Zoom,2/Zoom);
    }
    /// <summary>Anchor is the point on the object the handle edits; it differs from Point for handles drawn outside the bounds.</summary>
    private readonly record struct Handle(int Index,PointD Point,PointD Anchor)
    {
        public Handle(int index,PointD point):this(index,point,point){}
    }
    private IEnumerable<Handle> OutsetHandles(Rect r)
    {
        var outer=r;outer.Inflate(HandleOutset/Zoom,HandleOutset/Zoom);
        return BoxHandles(outer).Zip(BoxHandles(r),(drawn,edge)=>new Handle(drawn.Index,drawn.Point,edge.Point));
    }
    private static IEnumerable<Handle> BoxHandles(Rect r)
    {
        yield return new(0,new(r.Left,r.Top));yield return new(1,new(r.X+r.Width/2,r.Top));yield return new(2,new(r.Right,r.Top));
        yield return new(3,new(r.Right,r.Y+r.Height/2));yield return new(4,new(r.Right,r.Bottom));yield return new(5,new(r.X+r.Width/2,r.Bottom));
        yield return new(6,new(r.Left,r.Bottom));yield return new(7,new(r.Left,r.Y+r.Height/2));
    }
    private IEnumerable<Handle> Handles(Annotation a)
    {
        if(a.Kind is AnnotationKind.Arrow or AnnotationKind.Line || a.Kind==AnnotationKind.Highlight && a.Appearance.HighlightShape==MarkerShape.Line) {
            yield return new(-1,a.Start);yield return new(-2,a.End);
            if(a.Kind==AnnotationKind.Arrow)yield return new(-3,AnnotationGeometry.CurvePoint(a,.5));
        } else if(a.Kind!=AnnotationKind.Pen && a.Kind!=AnnotationKind.Step && !(a.Kind==AnnotationKind.Highlight && a.Appearance.HighlightShape==MarkerShape.Freehand)) {
            foreach(var h in OutsetHandles(ImageRenderer.BoundsOf(a)))yield return h;
        }
        if(a.Kind==AnnotationKind.Rectangle && !a.Appearance.Sketch || a.Kind==AnnotationKind.Text && a.Appearance.Bubble) {
            var box=ImageRenderer.BoundsOf(a);var inset=Math.Min(Math.Max(12/Zoom,a.Appearance.CornerRadius),Math.Min(box.Width,box.Height)/2);
            var pad=14/Zoom;var safe=ImageRenderer.RectOf(Document.Crop);safe.Inflate(-6/Zoom,-6/Zoom);
            if(Math.Min(box.Width,box.Height)*Zoom>=24 && !safe.IsEmpty) {
                var candidates=new[]{new PointD(box.Right-inset,box.Top-pad),new PointD(box.Right+pad,box.Top+inset),new PointD(box.Left+inset,box.Bottom+pad),new PointD(box.Left-pad,box.Top+inset)};
                var point=candidates.FirstOrDefault(p=>safe.Contains(ImageRenderer.P(p)),new(double.NaN,double.NaN));
                if(point.IsFinite)yield return new(-6,point);
            }
        }
        if(a.Kind==AnnotationKind.Magnifier) {
            var sample=AdvancedDrawing.SampleBounds(a);
            foreach(var h in BoxHandles(ImageRenderer.RectOf(sample)))yield return new(100+h.Index,h.Point);
            yield return new(-5,new(sample.X+sample.Width/2,sample.Y+sample.Height/2));
        }
        if(AnnotationGeometry.HasTail(a)) {
            var b=ImageRenderer.BoundsOf(a);
            yield return new(-4,ImageRenderer.TailTip(a));
        }
    }
    // Ties between outset handles of small objects go to the handle whose edited point is nearest.
    private Handle? NearHandle(IEnumerable<Handle> handles,PointD p)=>handles.Where(h=>AnnotationGeometry.Distance(h.Point,p)<=10/Zoom)
        .OrderBy(h=>Math.Round(AnnotationGeometry.Distance(h.Point,p),3)).ThenBy(h=>AnnotationGeometry.Distance(h.Anchor,p)).Cast<Handle?>().FirstOrDefault();
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);Focus();
        if(PickColorTool){ColorPickRequested?.Invoke(ImagePoint(e),Keyboard.Modifiers);e.Handled=true;return;}
        BeginPointer(ImagePoint(e),Keyboard.Modifiers,e.ClickCount);
    }
    internal void BeginPointer(PointD position,ModifierKeys modifiers=ModifierKeys.None,int clickCount=1)
    {
        FinishNumberEdit();pointerModifiers=modifiers;selectionBeforeGesture=Selected;start=current=position;
        if(CropTool) {
            cropBefore=PendingCrop ?? default;var near=PendingCrop is RectD c?NearHandle(BoxHandles(ImageRenderer.RectOf(c)),start):null;
            if(near is Handle h){handle=h.Index;gesture=Gesture.CropResize;}
            else if(PendingCrop is RectD p && p.Contains(start))gesture=Gesture.CropMove;
            else {gesture=Gesture.CropNew;PendingCrop=new(start.X,start.Y,1,1);}
            Selected=null;CaptureMouse();InvalidateVisual();return;
        }
        if(Tool is null && PendingCrop is RectD region) {
            // The select-mode range box keeps its handles and body until a click lands outside it.
            cropBefore=region;
            if(NearHandle(BoxHandles(ImageRenderer.RectOf(region)),start) is Handle rh){handle=rh.Index;gesture=Gesture.CropResize;CaptureMouse();InvalidateVisual();return;}
            if(region.Contains(start)){gesture=Gesture.CropMove;CaptureMouse();InvalidateVisual();return;}
            PendingCrop=null;InvalidateVisual();
        }
        var hit=ImageRenderer.LayerOrder(Document.Annotations).Reverse().FirstOrDefault(a=>Hit(a,start));
        if(hit?.Kind==AnnotationKind.Text && (Tool==AnnotationKind.Text || clickCount==2) && ImageRenderer.BoundsOf(hit).Contains(ImageRenderer.P(start))) {
            Selected=hit.Id;SelectionChanged?.Invoke();TextEditRequested?.Invoke(hit,false);return;
        }
        var selected=Selection;var selectedHandle=selected is null?null:NearHandle(Handles(selected),start);
        if(selectedHandle is Handle sh) {
            original=selected;handle=sh.Index;grabOffset=new(sh.Anchor.X-start.X,sh.Anchor.Y-start.Y);
            if(handle==-6) {var b=ImageRenderer.BoundsOf(selected!);cornerRadiusStart=Math.Min(selected!.Appearance.CornerRadius,Math.Min(b.Width,b.Height)/2);cornerDirection=new(b.X+b.Width/2-start.X,b.Y+b.Height/2-start.Y);if(cornerDirection.LengthSquared>0)cornerDirection.Normalize();}
            gesture=handle>=100?Gesture.SampleResize:handle==-5?Gesture.SampleMove:handle==-3?Gesture.Bend:handle==-4?Gesture.Tail:handle==-6?Gesture.Corner:Gesture.Resize;CaptureMouse();return;
        }
        if(hit!=null && (Tool is null || hit.Id==Selected || clickCount==2 || Tool==AnnotationKind.Text && hit.Kind==AnnotationKind.Text)) {
            Selected=hit.Id;SelectionChanged?.Invoke();
            if(hit.Kind==AnnotationKind.Text && (clickCount==2 || Tool==AnnotationKind.Text)){TextEditRequested?.Invoke(hit,false);return;}
            original=hit;
            if(pointerModifiers.HasFlag(ModifierKeys.Alt))original=hit with {Id=Guid.NewGuid()};
            gesture=hit.Kind==AnnotationKind.Magnifier && AdvancedDrawing.SampleBounds(hit).Contains(start) && !hit.Bounds.Contains(start)?Gesture.SampleMove:Gesture.Move;CaptureMouse();InvalidateVisual();return;
        }
        if(Tool is not AnnotationKind kind) {
            // Dragging on empty canvas in select mode draws a range box: Enter crops, Delete erases.
            Selected=null;SelectionChanged?.Invoke();gesture=Gesture.CropNew;CaptureMouse();InvalidateVisual();return;
        }
        var a=new Annotation(Guid.NewGuid(),kind,start,start,Ink,Thickness,Style:InkStyle);
        if(kind==AnnotationKind.Text) {
            a=NewTextAt(start);
            TextEditRequested?.Invoke(a,true);return;
        }
        if(kind==AnnotationKind.Step) {
            // With a pointer the click marks the pointer tip and the bubble sits up and to the right, like the reference app.
            var reach=AnnotationGeometry.CounterRadius(a)*1.45/Math.Sqrt(2);
            a=a.Appearance.ShowTail ? a with {Text=Document.NextCounter.ToString(),Start=new(start.X+reach,start.Y-reach),End=new(start.X+reach,start.Y-reach),Tail=start}
                : a with {Text=Document.NextCounter.ToString(),Tail=new(start.X-reach,start.Y+reach)};
            Document.Add(a);Selected=a.Id;SelectionChanged?.Invoke();InvalidateVisual();return;
        }
        Selected=a.Id;points=kind==AnnotationKind.Pen || kind==AnnotationKind.Highlight && InkStyle.HighlightShape==MarkerShape.Freehand?[start]:null;draft=a with {Points=points?.ToArray()};gesture=Gesture.Create;CaptureMouse();InvalidateVisual();
    }
    internal Annotation NewTextAt(PointD p)
    {
        var crop=Document.Crop;var width=Math.Min(200,crop.Width);var height=Math.Min(40,crop.Height);
        var x=Math.Clamp(p.X,crop.X,Math.Max(crop.X,crop.Right-width));
        var y=Math.Clamp(p.Y,crop.Y,Math.Max(crop.Y,crop.Bottom-height));
        var tailY=y+height+28<=crop.Bottom?y+height+28:Math.Max(crop.Y,y-28);
        return new(Guid.NewGuid(),AnnotationKind.Text,new(x,y),new(x+width,y+height),Ink,Thickness,"",
            Tail:new(Math.Min(crop.Right,x+width*.35),tailY),Style:InkStyle);
    }
    internal bool Hit(Annotation a,PointD p)
    {
        var margin=Math.Max(7/Zoom,a.Thickness/2+3/Zoom);
        if(a.Kind is not (AnnotationKind.Rectangle or AnnotationKind.Ellipse) && ImageRenderer.TailGeometry(a).FillContains(ImageRenderer.P(p)))return true;
        if(a.Kind==AnnotationKind.Arrow && a.Appearance.ArrowHead is ArrowHead.Filled or ArrowHead.Double)
            return ImageRenderer.ArrowGeometry(a).FillContains(ImageRenderer.P(p)) || ImageRenderer.ArrowGeometry(a).StrokeContains(new Pen(Brushes.Black,margin*2),ImageRenderer.P(p));
        if(a.Kind==AnnotationKind.Arrow)return ImageRenderer.ArrowStrokeGeometry(a).StrokeContains(new Pen(Brushes.Black,margin*2) {StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round},ImageRenderer.P(p));
        if(a.Kind==AnnotationKind.Highlight)return AdvancedDrawing.MarkerGeometry(a).FillContains(ImageRenderer.P(p)) || AdvancedDrawing.MarkerGeometry(a).StrokeContains(new Pen(Brushes.Black,6/Zoom),ImageRenderer.P(p));
        if(a.Kind==AnnotationKind.Magnifier) {
            var sample=AdvancedDrawing.SampleBounds(a);var rect=ImageRenderer.RectOf(sample);rect.Inflate(margin,margin);
            if(rect.Contains(ImageRenderer.P(p)))return true;
        }
        if(a.Kind==AnnotationKind.Spotlight) {
            var rect=ImageRenderer.RectOf(a.Bounds);Geometry hole=a.Appearance.Elliptical?new EllipseGeometry(rect):new RectangleGeometry(rect,6,6);
            return hole.StrokeContains(new Pen(Brushes.Black,margin*2),ImageRenderer.P(p));
        }
        if(a.Kind==AnnotationKind.Line)return AnnotationGeometry.CurveDistance(a,p)<=margin;
        if(a.Kind==AnnotationKind.Pen && a.Points is {Count:1} dot)return AnnotationGeometry.Distance(dot[0],p)<=margin;
        if(a.Kind==AnnotationKind.Pen)return ImageRenderer.PenGeometry(a).StrokeContains(new Pen(Brushes.Black,margin*2) {StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round},ImageRenderer.P(p));
        if(a.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse) {
            var path=ImageRenderer.ShapeGeometry(a);
            return path.StrokeContains(new Pen(Brushes.Black,margin*2),ImageRenderer.P(p)) || a.Appearance.Fill!=ShapeFill.Outline && path.FillContains(ImageRenderer.P(p));
        }
        var b=ImageRenderer.BoundsOf(a);b.Inflate(margin,margin);
        if(!b.Contains(ImageRenderer.P(p)))return false;
        if(a.Kind==AnnotationKind.Step)return AnnotationGeometry.Distance(a.Start,p)<=AnnotationGeometry.CounterRadius(a)+margin;
        return true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);var position=ImagePoint(e);
        if(gesture==Gesture.None) {UpdateHover(position);return;}
        if(e.LeftButton!=MouseButtonState.Pressed)return;
        UpdatePointer(position,Keyboard.Modifiers);
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);hovered=null;hoveredControl=null;SetPointerHint("");if(!IsDragging)InvalidateVisual();
    }
    internal void UpdateHover(PointD position)
    {
        current=position;var previousHover=hovered;var previousControl=hoveredControl;
        var region=RegionActive?PendingCrop:null;
        var near=region is RectD crop?NearHandle(BoxHandles(ImageRenderer.RectOf(crop)),position):Selection is Annotation selected?NearHandle(Handles(selected),position):null;
        var insideRegion=region is RectD box && box.Contains(position);
        var hit=CropTool || insideRegion?null:ImageRenderer.LayerOrder(Document.Annotations).Reverse().FirstOrDefault(a=>Hit(a,position));
        hovered=Tool is null?hit?.Id:null;hoveredControl=near?.Index;
        var textInsertion=Tool==AnnotationKind.Text && hit?.Kind==AnnotationKind.Text;
        var editable=hit!=null && (Tool is null || hit.Id==Selected || textInsertion);
        Cursor=PickColorTool?Cursors.Cross:near is Handle h?HandleCursor(h.Index):insideRegion?Cursors.SizeAll:textInsertion?Cursors.IBeam:editable?Cursors.SizeAll:Tool==AnnotationKind.Text?Cursors.IBeam:Tool is null && !CropTool?Cursors.Arrow:Cursors.Cross;
        SetPointerHint(near is Handle control?control.Index switch {
            -3=>"곡률 · 드래그해 화살표를 휘세요",-4=>"말꼬리 · 파란 원을 드래그해 방향과 길이를 바꾸세요",
            -6=>"모서리 · 민트색 마름모를 드래그해 둥글기를 바꾸세요",-5=>"원본 영역 이동",_=>"크기 조절 · Shift 비율 유지"
        }:textInsertion?"클릭 글자 편집 · V 선택·이동":editable?hit!.Kind==AnnotationKind.Text?"드래그 이동 · 더블클릭 글자 편집":"드래그 이동 · Alt+드래그 복제 · Delete 삭제":Tool==AnnotationKind.Text?"클릭해 글자 입력 · 더블클릭으로 기존 글자 편집":Tool!=null?"드래그해 그리기 · Esc 선택 모드":insideRegion?"범위 이동 · Enter 자르기 · Delete 지우기 · Esc 취소":"객체 선택 · 빈 곳을 드래그해 범위 지정");
        if(previousHover!=hovered || previousControl!=hoveredControl)InvalidateVisual();
    }
    private void SetPointerHint(string value) {if(PointerHint==value)return;PointerHint=value;PointerHintChanged?.Invoke(value);}
    private static Cursor HandleCursor(int index)=>index switch {
        -6=>Cursors.Hand,0 or 4 or 100 or 104=>Cursors.SizeNWSE,2 or 6 or 102 or 106=>Cursors.SizeNESW,
        1 or 5 or 101 or 105=>Cursors.SizeNS,3 or 7 or 103 or 107=>Cursors.SizeWE,_=>Cursors.SizeAll};
    internal void UpdatePointer(PointD position,ModifierKeys modifiers=ModifierKeys.None)
    {
        if(gesture==Gesture.None)return;
        current=position;pointerModifiers=modifiers;
        if(gesture is Gesture.Move or Gesture.SampleMove or Gesture.Corner && AnnotationGeometry.Distance(start,current)<3/Zoom && draft is null)return;
        if(gesture==Gesture.CropNew)PendingCrop=RectD.FromPoints(start,Constrained(start,current));
        else if(gesture==Gesture.CropMove) {
            var x=Math.Clamp(cropBefore.X+current.X-start.X,Document.Crop.X,Document.Crop.Right-cropBefore.Width);
            var y=Math.Clamp(cropBefore.Y+current.Y-start.Y,Document.Crop.Y,Document.Crop.Bottom-cropBefore.Height);
            PendingCrop=cropBefore with {X=x,Y=y};
        } else if(gesture==Gesture.CropResize)PendingCrop=Resize(cropBefore,current,handle);
        else if(original is Annotation a) {
            draft=gesture switch {
                Gesture.Move=>SnapMove(a.Move(current.X-start.X,current.Y-start.Y)),
                Gesture.Bend=>AnnotationGeometry.BendThrough(a,current),
                Gesture.Tail=>a with {Tail=current},
                Gesture.Corner=>a with {Style=a.Appearance with {CornerRadius=Math.Clamp(cornerRadiusStart+(current.X-start.X)*cornerDirection.X+(current.Y-start.Y)*cornerDirection.Y,0,Math.Min(48,Math.Min(ImageRenderer.BoundsOf(a).Width,ImageRenderer.BoundsOf(a).Height)/2))}},
                Gesture.SampleMove=>a with {SampleRegion=AdvancedDrawing.SampleBounds(a) with {X=AdvancedDrawing.SampleBounds(a).X+current.X-start.X,Y=AdvancedDrawing.SampleBounds(a).Y+current.Y-start.Y}},
                Gesture.SampleResize=>ResizeSample(a,current),
                Gesture.Resize when handle==-1=>ResizeEndpoint(a,Constrained(a.End,current,a.Kind),true),
                Gesture.Resize when handle==-2=>ResizeEndpoint(a,Constrained(a.Start,current,a.Kind),false),
                // Grabbing an outset handle must not jump the edge to the pointer.
                Gesture.Resize=>ResizeAnnotation(a,new(current.X+grabOffset.X,current.Y+grabOffset.Y)),
                _=>a
            };
        } else if(draft is Annotation created) {
            if(points!=null && AnnotationGeometry.Distance(points[^1],current)>=.5/Zoom)points.Add(current);draft=created with {End=Constrained(start,current,created.Kind),Points=points?.ToArray()};
        }
        InvalidateVisual();
    }
    private Annotation SnapMove(Annotation moving)
    {
        guideX=guideY=null;
        if(!SnapToObjects || pointerModifiers.HasFlag(ModifierKeys.Control))return moving;
        var others=Document.Annotations.Where(a=>a.Id!=original?.Id).Select(ImageRenderer.BoundsOf).ToArray();
        var b=ImageRenderer.BoundsOf(moving);
        var xs=others.SelectMany(r=>new[]{r.Left,r.Left+r.Width/2,r.Right});
        var ys=others.SelectMany(r=>new[]{r.Top,r.Top+r.Height/2,r.Bottom});
        var x=AnnotationGeometry.SnapAxis(b.Left,b.Right,xs,5/Zoom);
        var y=AnnotationGeometry.SnapAxis(b.Top,b.Bottom,ys,5/Zoom);
        guideX=x.Guide;guideY=y.Guide;return moving.Move(x.Offset,y.Offset);
    }
    private PointD Constrained(PointD anchor,PointD p,AnnotationKind? kind=null)
    {
        if(!pointerModifiers.HasFlag(ModifierKeys.Shift))return p;
        if(kind is AnnotationKind.Arrow or AnnotationKind.Line or AnnotationKind.Highlight)return AnnotationGeometry.SnapAngle(anchor,p);
        var dx=p.X-anchor.X;var dy=p.Y-anchor.Y;var length=Math.Max(Math.Abs(dx),Math.Abs(dy));
        return new(anchor.X+(dx<0?-1:1)*length,anchor.Y+(dy<0?-1:1)*length);
    }
    private static Annotation ResizeEndpoint(Annotation a,PointD position,bool startPoint)
    {
        var previous=startPoint?a.Start:a.End;
        // Keep the quadratic bow relative to the chord while moving either endpoint.
        var control=a.Control is PointD c?new PointD(c.X+(position.X-previous.X)/2,c.Y+(position.Y-previous.Y)/2):(PointD?)null;
        return startPoint?a with {Start=position,Control=control}:a with {End=position,Control=control};
    }
    private static RectD Resize(RectD b,PointD p,int h)
    {
        double l=b.X,t=b.Y,r=b.Right,d=b.Bottom;
        if(h is 0 or 6 or 7)l=Math.Min(p.X,r-2);if(h is 2 or 3 or 4)r=Math.Max(p.X,l+2);
        if(h is 0 or 1 or 2)t=Math.Min(p.Y,d-2);if(h is 4 or 5 or 6)d=Math.Max(p.Y,t+2);
        return new(l,t,r-l,d-t);
    }
    private Annotation ResizeAnnotation(Annotation a,PointD p)
    {
        var b=ImageRenderer.BoundsOf(a);var next=Resize(new(b.X,b.Y,b.Width,b.Height),p,handle);
        if(a.Kind==AnnotationKind.Text) {
            var minimum=a.Appearance.Bubble?40:24;
            if(next.Width<minimum)next=next with {X=handle is 0 or 6 or 7?next.Right-minimum:next.X,Width=minimum};
        }
        if(a.Kind==AnnotationKind.Image && handle is 0 or 2 or 4 or 6 && !pointerModifiers.HasFlag(ModifierKeys.Shift)) {
            var ws=next.Width/Math.Max(1,b.Width);var hs=next.Height/Math.Max(1,b.Height);
            var scale=Math.Abs(ws-1)>=Math.Abs(hs-1)?ws:hs;
            var w=b.Width*scale;var h=b.Height*scale;
            var x=handle is 0 or 6?b.Right-w:b.Left;var y=handle is 0 or 2?b.Bottom-h:b.Top;
            return a with {Start=new(x,y),End=new(x+w,y+h)};
        }
        if(a.Kind==AnnotationKind.Magnifier) {
            var sample=AdvancedDrawing.SampleBounds(a);
            var ws=next.Width/sample.Width;var hs=next.Height/sample.Height;
            var desired=handle is 1 or 5?hs:handle is 3 or 7?ws:Math.Abs(ws-a.Appearance.Magnification)>=Math.Abs(hs-a.Appearance.Magnification)?ws:hs;
            var scale=Math.Clamp(desired,1,8);
            var width=sample.Width*scale;var height=sample.Height*scale;
            var x=handle is 0 or 6 or 7?b.Right-width:b.X;var y=handle is 0 or 1 or 2?b.Bottom-height:b.Y;
            return a with {Start=new(x,y),End=new(x+width,y+height),Style=a.Appearance with {Magnification=scale}};
        }
        return a with {Start=new(next.X,next.Y),End=new(next.Right,next.Bottom)};
    }
    private Annotation ResizeSample(Annotation a,PointD p)
    {
        var sample=Resize(AdvancedDrawing.SampleBounds(a),p,handle-100);
        return a with {SampleRegion=sample,End=new(a.Start.X+sample.Width*a.Appearance.Magnification,a.Start.Y+sample.Height*a.Appearance.Magnification)};
    }
    private Annotation PreviewAnnotation(Annotation a)=>gesture==Gesture.Create && a.Kind==AnnotationKind.Magnifier && !a.Bounds.IsEmpty
        ? CreateMagnifier(a.Bounds) with {Id=a.Id} : WithReachableShapeTail(a);
    internal Annotation CreateMagnifier(RectD sample)
    {
        var ratio=InkStyle.Magnification;var x=sample.Right+24;var y=sample.Bottom+24;
        return new(Guid.NewGuid(),AnnotationKind.Magnifier,new(x,y),new(x+sample.Width*ratio,y+sample.Height*ratio),Ink,Thickness,Style:InkStyle,SampleRegion:sample);
    }
    internal void InsertImage(BitmapSource image)
    {
        if((long)image.PixelWidth*image.PixelHeight>64_000_000)throw new InvalidOperationException("추가 이미지가 64MP 한도를 초과합니다.");
        var bytes=Document.Annotations.Where(a=>a.Image!=null).Select(a=>a.Image!).Distinct().Sum(a=>(long)a.Width*a.Height*4);
        if(bytes+(long)image.PixelWidth*image.PixelHeight*4>256_000_000)throw new InvalidOperationException("문서 이미지 한도 256MB를 초과합니다.");
        var asset=ImageRenderer.RasterOf(image);var crop=Document.Crop;
        var x=crop.X+(crop.Width-image.PixelWidth)/2;var y=crop.Y+(crop.Height-image.PixelHeight)/2;
        var a=new Annotation(Guid.NewGuid(),AnnotationKind.Image,new(x,y),new(x+image.PixelWidth,y+image.PixelHeight),new(255,255,255),Image:asset);
        CommitAnnotation(a,true);Tool=null;Selected=a.Id;SelectionChanged?.Invoke();InvalidateVisual();
    }
    internal void CommitText(Annotation a,bool add)=>CommitAnnotation(a,add);
    private void CommitAnnotation(Annotation a,bool add)
    {
        var owns=!Document.IsEditing;if(owns)Document.BeginEdit();
        try {
            if(a.Kind is AnnotationKind.Image or AnnotationKind.Magnifier or AnnotationKind.Text) {
                var b=ImageRenderer.BoundsOf(a);
                if(a.Kind==AnnotationKind.Text && !ImageRenderer.TailGeometry(a).Bounds.IsEmpty)b.Union(ImageRenderer.TailGeometry(a).Bounds);
                Document.ExpandToInclude(new(b.X,b.Y,b.Width,b.Height));
            }
            if(add)Document.Add(a);else Document.Replace(a);
            if(owns)Document.EndEdit();
        } catch {if(owns)Document.CancelEdit();throw;}
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);EndPointer(ImagePoint(e),Keyboard.Modifiers);
    }
    internal void EndPointer(PointD position,ModifierKeys modifiers=ModifierKeys.None)
    {
        if(gesture==Gesture.None)return;
        UpdatePointer(position,modifiers);
        if(draft is Annotation a) {
            try {
                a=WithReachableShapeTail(a);
                if(gesture==Gesture.Create && a.Kind==AnnotationKind.Magnifier) {
                    var sample=a.Bounds;
                    if(sample.Width<2 || sample.Height<2)sample=new RectD(start.X-20,start.Y-20,40,40).Intersect(Document.Crop);
                    a=CreateMagnifier(sample);
                }
                var add=gesture==Gesture.Create || !Document.Annotations.Any(x=>x.Id==a.Id);
                var freehand=a.Kind==AnnotationKind.Pen || a.Kind==AnnotationKind.Highlight && a.Appearance.HighlightShape==MarkerShape.Freehand;
                var linear=a.Kind is AnnotationKind.Arrow or AnnotationKind.Line || a.Kind==AnnotationKind.Highlight && a.Appearance.HighlightShape==MarkerShape.Line;
                var valid=freehand || (linear?AnnotationGeometry.Distance(a.Start,a.End)>=1:a.Bounds.Width>=1 && a.Bounds.Height>=1);
                if(!add || valid) {
                    CommitAnnotation(a,add);Selected=a.Id;
                }
            } catch(Exception error) when(error is InvalidOperationException or ArgumentException or OverflowException) {OperationFailed?.Invoke(error.Message);}
        }
        if(gesture==Gesture.CropNew && PendingCrop is RectD drawn && Math.Min(drawn.Width,drawn.Height)*Zoom<3)PendingCrop=null;
        if(!CropTool && !IsRegionGesture && Selection is null)Selected=selectionBeforeGesture;
        gesture=Gesture.None;guideX=guideY=null;draft=original=null;points=null;ReleaseMouseCapture();SelectionChanged?.Invoke();UpdateHover(position);InvalidateVisual();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) {base.OnLostMouseCapture(e);if(gesture!=Gesture.None)CancelGesture();}
    internal void CancelGesture()
    {
        FinishNumberEdit();var active=gesture!=Gesture.None;
        if(active)Selected=selectionBeforeGesture;
        gesture=Gesture.None;guideX=guideY=null;draft=original=null;points=null;ReleaseMouseCapture();
        if(active)SelectionChanged?.Invoke();InvalidateVisual();
    }
    internal bool ApplyCrop()
    {
        if(PendingCrop is not RectD c || c.Width<1 || c.Height<1)return false;
        Document.CropTo(new(Math.Floor(c.X),Math.Floor(c.Y),Math.Ceiling(c.Right)-Math.Floor(c.X),Math.Ceiling(c.Bottom)-Math.Floor(c.Y)));
        PendingCrop=null;CropTool=false;InvalidateVisual();return true;
    }
    internal void ClearCrop() {PendingCrop=null;CropTool=false;InvalidateVisual();}
    /// <summary>Turns the range box into an effect object (Delete → Erase) and selects it so its options appear.</summary>
    internal bool ApplyRegionEffect(AnnotationKind kind,ColorRgba color,double thickness,AnnotationStyle style)
    {
        if(PendingCrop is not RectD r || r.Width<1 || r.Height<1)return false;
        var a=new Annotation(Guid.NewGuid(),kind,new(r.X,r.Y),new(r.Right,r.Bottom),color,thickness,Style:style);
        PendingCrop=null;CropTool=false;CommitAnnotation(a,true);Selected=a.Id;SelectionChanged?.Invoke();InvalidateVisual();return true;
    }
    private static PixelRect ToPixels(RectD r)=>new((int)Math.Floor(r.X),(int)Math.Floor(r.Y),(int)Math.Ceiling(r.Right)-(int)Math.Floor(r.X),(int)Math.Ceiling(r.Bottom)-(int)Math.Floor(r.Y));
    private static RectD FromPixels(PixelRect r)=>new(r.X,r.Y,r.Width,r.Height);
    /// <summary>Keyboard precision for the pending crop: move, or resize the right/bottom edges, by whole image pixels.</summary>
    internal bool NudgeCrop(int dx,int dy,bool resize)
    {
        if(PendingCrop is not RectD c)return false;
        var bounds=ToPixels(Document.Crop);var r=ToPixels(c);
        PendingCrop=FromPixels(resize?SelectionMath.ResizeBy(r,dx,dy,bounds):SelectionMath.Move(r,dx,dy,bounds));InvalidateVisual();return true;
    }
    /// <summary>[ and ] grow or shrink every side of the pending crop.</summary>
    internal bool GrowCrop(int amount)
    {
        if(PendingCrop is not RectD c)return false;
        PendingCrop=FromPixels(SelectionMath.Grow(ToPixels(c),amount,ToPixels(Document.Crop)));InvalidateVisual();return true;
    }
    /// <summary>Fits the pending crop to content by trimming uniform margins of the original screenshot. Returns false when nothing changed.</summary>
    internal bool FitCropToContent()
    {
        if(PendingCrop is not RectD c)return false;
        var area=ToPixels(c).Intersect(new PixelRect(0,0,source.PixelWidth,source.PixelHeight));
        if(area.IsEmpty || (long)area.Width*area.Height>40_000_000)return false;
        BitmapSource pixels=source.Format==PixelFormats.Bgra32 || source.Format==PixelFormats.Pbgra32?source:new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        var buffer=new byte[area.Width*area.Height*4];pixels.CopyPixels(new Int32Rect(area.X,area.Y,area.Width,area.Height),buffer,area.Width*4,0);
        var local=PixelProbe.TrimUniformBorder(buffer,area.Width,area.Height,new PixelRect(0,0,area.Width,area.Height),10);
        var fitted=new PixelRect(area.X+local.X,area.Y+local.Y,local.Width,local.Height);
        if(fitted==ToPixels(c))return false;
        PendingCrop=FromPixels(fitted);InvalidateVisual();return true;
    }
    internal void DeleteSelected() {CancelGesture();if(Selected is Guid id)Document.Delete(id);Selected=null;SelectionChanged?.Invoke();InvalidateVisual();}
    internal void ChangeSelected(Func<Annotation,Annotation> update) {FinishNumberEdit();if(Selection is Annotation a){var changed=update(a);if(changed.Kind is AnnotationKind.Image or AnnotationKind.Magnifier or AnnotationKind.Text)CommitAnnotation(changed,false);else Document.Replace(changed);SelectionChanged?.Invoke();InvalidateVisual();}}
    internal void ApplyStyle()=>ChangeSelected(a=>WithReachableShapeTail(a with {Color=Ink,Thickness=Thickness,Style=InkStyle}));
    internal Annotation WithReachableShapeTail(Annotation a)
    {
        if(a.Kind is not (AnnotationKind.Rectangle or AnnotationKind.Ellipse) || !a.Appearance.ShapeTail || a.Tail!=null)return a;
        var b=a.Bounds;var crop=Document.Crop;
        var x=Math.Clamp(b.X+b.Width*.35,crop.X,crop.Right);
        var y=b.Bottom+28<=crop.Bottom?b.Bottom+28:b.Y-28>=crop.Y?b.Y-28:double.NaN;
        if(double.IsFinite(y))return a with {Tail=new(x,y)};
        var tailX=b.Right+28<=crop.Right?b.Right+28:Math.Max(crop.X,b.X-28);
        return a with {Tail=new(tailX,Math.Clamp(b.Y+b.Height/2,crop.Y,crop.Bottom))};
    }
    internal void DuplicateSelected() {
        if(Selection is not Annotation a)return;var copy=a.Move(16,16) with {Id=Guid.NewGuid()};CommitAnnotation(copy,true);Selected=copy.Id;SelectionChanged?.Invoke();InvalidateVisual();
    }
    internal void Nudge(double dx,double dy) {ChangeSelected(a=>a.Move(dx,dy));}
    internal void SetSpotlightDim(double value)
    {
        InkStyle=InkStyle with {Dim=value};
        if(Selection?.Kind!=AnnotationKind.Spotlight)return;
        var owns=!Document.IsEditing;if(owns)Document.BeginEdit();
        try {
            foreach(var a in Document.Annotations.Where(a=>a.Kind==AnnotationKind.Spotlight).ToArray())Document.Replace(a with {Style=a.Appearance with {Dim=value}});
            if(owns)Document.EndEdit();InkStyle=InkStyle with {Dim=value};SelectionChanged?.Invoke();
        } catch {if(owns)Document.CancelEdit();throw;}
    }
    private void FinishNumberEdit() {if(digitObject is null)return;digitCommit.Stop();Document.EndEdit();digits="";digitObject=null;}
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if(Selection is {Kind:AnnotationKind.Spotlight} spotlight && int.TryParse(e.Text,out var level) && level is >=1 and <=9) {
            SetSpotlightDim(level/10d);e.Handled=true;return;
        }
        if(Selection is not {Kind:AnnotationKind.Step} a || string.IsNullOrEmpty(e.Text) || e.Text.Any(c=>c<'0'||c>'9'))return;
        if(digitObject!=a.Id || (DateTime.UtcNow-digitTime).TotalMilliseconds>800 || digits.Length>=2) {FinishNumberEdit();digits="";}
        Document.BeginEdit();digitCommit.Stop();digitCommit.Start();
        digits=new string((digits+e.Text).TakeLast(2).ToArray());digitObject=a.Id;digitTime=DateTime.UtcNow;
        var value=Math.Clamp(int.Parse(digits),0,99);Document.Replace(a with {Text=value.ToString()});SelectionChanged?.Invoke();e.Handled=true;
    }
}
