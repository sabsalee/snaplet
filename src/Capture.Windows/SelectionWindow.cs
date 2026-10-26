using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Capture.Core;
using Capture.Windows.Platform;

namespace Capture.Windows;

internal sealed record SelectionResult(Int32Rect? Region,nint Window);

/// <summary>Per-capture options for the selection overlay.</summary>
internal sealed record SelectionOptions(bool ShowLoupe=true,bool AdjustBeforeCapture=false,string? Purpose=null);

/// <summary>
/// One physical monitor per overlay avoids one DPI assumption for the virtual desktop.
/// Selection works in the monitor image's physical pixels; WPF DIPs are used only for drawing.
/// Precision aids: pixel loupe with coordinates and colour, Tab/Shift+Tab/C colour copy, R ruler,
/// Shift square, Space move while dragging, adjust handles, arrow/Alt+arrow/[ ] keys and A smart fit.
/// </summary>
internal sealed class SelectionWindow : Window
{
    private const int LoupeSamples=15, LoupeCell=9, HandleSize=9, RulerTolerance=12, TrimTolerance=10;
    private enum Mode { Idle, Drawing, Adjusting, Moving, Resizing }

    private readonly Canvas canvas=new() {IsHitTestVisible=false};
    private readonly Path dim=new() {Fill=new SolidColorBrush(Color.FromArgb(96,0,0,0)),IsHitTestVisible=false};
    private readonly Rectangle border=new() {Stroke=new SolidColorBrush(Color.FromRgb(56,160,255)),StrokeThickness=1,Visibility=Visibility.Collapsed,SnapsToDevicePixels=true};
    private readonly Rectangle[] handles=new Rectangle[8];
    private readonly Border sizeTag=Chip();
    private readonly Border hint=Chip();
    private readonly Border toast=Chip();
    private readonly Line crossX=Cross(),crossY=Cross();
    private readonly Line rulerX=Ruler(),rulerY=Ruler();
    private readonly Border rulerTag=Chip();
    private readonly Border loupe=new() {Width=LoupeSamples*LoupeCell+2,BorderBrush=Brushes.White,BorderThickness=new Thickness(1),Background=Brushes.Black,Visibility=Visibility.Collapsed,SnapsToDevicePixels=true};
    private readonly Canvas loupeCanvas=new() {Width=LoupeSamples*LoupeCell,Height=LoupeSamples*LoupeCell,ClipToBounds=true};
    private readonly Image loupeImage=new() {Stretch=Stretch.Fill};
    private readonly TextBlock loupeText=new() {Foreground=Brushes.White,FontSize=11,FontFamily=new FontFamily("Consolas, Segoe UI"),Margin=new Thickness(6,4,6,5)};
    private readonly System.Windows.Threading.DispatcherTimer toastTimer=new() {Interval=TimeSpan.FromMilliseconds(1400)};

    private readonly DisplayInfo display;
    private readonly BitmapSource image;
    private readonly BitmapSource pixels;
    private readonly IReadOnlyList<PickableWindow>? windows;
    private readonly SelectionOptions options;
    private readonly PixelRect bounds;
    private Mode mode;
    private PixelRect selection;
    private PixelRect gestureStart;
    private (int X,int Y) anchor,pointer,gesturePointer;
    private SelectionHandle activeHandle;
    private bool spaceHeld,ruler,pointerInside;
    public event Action<SelectionResult?>? Completed;
    /// <summary>Raised when this monitor starts a new selection so the other overlays can clear theirs.</summary>
    public event Action? SelectionStarted;

    public SelectionWindow(DisplayInfo monitor,BitmapSource background,IReadOnlyList<PickableWindow>? pickWindows=null,SelectionOptions? selectionOptions=null) {
        display=monitor;image=background;windows=pickWindows;options=selectionOptions??new();
        pixels=image.Format==PixelFormats.Bgra32 || image.Format==PixelFormats.Pbgra32?image:new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
        if(!pixels.IsFrozen && pixels.CanFreeze)pixels.Freeze();
        bounds=new PixelRect(0,0,image.PixelWidth,image.PixelHeight);
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=true;
        Background=Brushes.Black;Cursor=Cursors.Cross;Title="Snaplet · 영역 선택";
        KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.None);
        UseLayoutRounding=true;
        var root=new Grid();
        root.Children.Add(new Image {Source=image,Stretch=Stretch.Fill,IsHitTestVisible=false});
        root.Children.Add(dim);root.Children.Add(canvas);
        canvas.Children.Add(border);
        for(int i=0;i<handles.Length;i++) {handles[i]=new Rectangle {Width=HandleSize,Height=HandleSize,Fill=Brushes.White,Stroke=border.Stroke,StrokeThickness=1,Visibility=Visibility.Collapsed};canvas.Children.Add(handles[i]);}
        canvas.Children.Add(crossX);canvas.Children.Add(crossY);canvas.Children.Add(rulerX);canvas.Children.Add(rulerY);canvas.Children.Add(rulerTag);
        canvas.Children.Add(sizeTag);canvas.Children.Add(hint);canvas.Children.Add(toast);
        RenderOptions.SetBitmapScalingMode(loupeImage,BitmapScalingMode.NearestNeighbor);
        loupeCanvas.Children.Add(loupeImage);
        loupeCanvas.Children.Add(new Rectangle {Width=LoupeSamples*LoupeCell,Height=LoupeSamples*LoupeCell,Fill=GridBrush()});
        var center=new Rectangle {Width=LoupeCell+2,Height=LoupeCell+2,Stroke=Brushes.White,StrokeThickness=1.5};
        Canvas.SetLeft(center,LoupeSamples/2*LoupeCell-1);Canvas.SetTop(center,LoupeSamples/2*LoupeCell-1);loupeCanvas.Children.Add(center);
        var loupeStack=new StackPanel();loupeStack.Children.Add(loupeCanvas);loupeStack.Children.Add(new Border {Background=new SolidColorBrush(Color.FromArgb(235,18,22,30)),Child=loupeText});
        loupe.Child=loupeStack;canvas.Children.Add(loupe);
        Content=root;
        ((TextBlock)hint.Child).Text=windows is not null?"캡처할 창을 클릭하세요 · Esc 취소"
            :(options.Purpose is string purpose?purpose+" · ":"")+"드래그하여 선택 · Shift 정사각형 · Space 이동 · Tab 색 복사 · R 측정 · Esc 취소";
        Canvas.SetLeft(hint,24);Canvas.SetTop(hint,24);
        toast.Visibility=Visibility.Collapsed;sizeTag.Visibility=Visibility.Collapsed;rulerTag.Visibility=Visibility.Collapsed;
        toastTimer.Tick+=(_,_)=>{toastTimer.Stop();toast.Visibility=Visibility.Collapsed;};
        SourceInitialized+=(_,_)=>{
            var hwnd=new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(hwnd,(nint)(-1),display.Bounds.Left,display.Bounds.Top,display.Bounds.Width,display.Bounds.Height,0x40);
            Native.SetWindowDisplayAffinity(hwnd,0x11);
        };
        SizeChanged+=(_,_)=>Refresh();
        MouseLeftButtonDown+=Down;MouseMove+=Move;MouseLeftButtonUp+=Up;
        MouseEnter+=(_,_)=>{pointerInside=true;if(!IsActive)Activate();};
        MouseLeave+=(_,_)=>{pointerInside=false;RefreshPointer();};
        MouseRightButtonDown+=(_,_)=>Completed?.Invoke(null);
        PreviewKeyDown+=OnKey;PreviewKeyUp+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Space)spaceHeld=false;};
        Loaded+=(_,_)=>{Focus();Refresh();};
        Closed+=(_,_)=>toastTimer.Stop();
    }

    // ---- coordinate helpers -------------------------------------------------------------
    private double ScaleX=>ActualWidth>0?image.PixelWidth/ActualWidth:1;
    private double ScaleY=>ActualHeight>0?image.PixelHeight/ActualHeight:1;
    private (int X,int Y) ToPixel(Point p)=>(Math.Clamp((int)Math.Floor(p.X*ScaleX),0,image.PixelWidth-1),Math.Clamp((int)Math.Floor(p.Y*ScaleY),0,image.PixelHeight-1));
    /// <summary>Pointer as a selection edge (0..Width inclusive), so a drag to the far edge includes the last pixel.</summary>
    private (int X,int Y) ToEdge(Point p)=>(Math.Clamp((int)Math.Round(p.X*ScaleX),0,image.PixelWidth),Math.Clamp((int)Math.Round(p.Y*ScaleY),0,image.PixelHeight));
    private Rect ToView(PixelRect r)=>new(r.X/ScaleX,r.Y/ScaleY,r.Width/ScaleX,r.Height/ScaleY);
    private Point ToView(int x,int y)=>new(x/ScaleX,y/ScaleY);

    // ---- mouse ----------------------------------------------------------------------------
    private void Down(object sender,MouseButtonEventArgs e) {
        if(windows is not null) {
            if(WindowUnderCursor() is PickableWindow target)Completed?.Invoke(new(null,target.Handle));
            return;
        }
        var view=e.GetPosition(this);var edge=ToEdge(view);
        if(mode==Mode.Adjusting && !selection.IsEmpty) {
            if(e.ClickCount==2 && Contains(selection,view)){Finish();return;}
            if(HandleAt(view) is SelectionHandle h){mode=Mode.Resizing;activeHandle=h;}
            else if(Contains(selection,view)){mode=Mode.Moving;}
            else StartDrawing(edge);
            gestureStart=selection;gesturePointer=edge;CaptureMouse();Refresh();return;
        }
        StartDrawing(edge);CaptureMouse();
    }
    private void StartDrawing((int X,int Y) edge) {
        mode=Mode.Drawing;anchor=edge;gesturePointer=edge;selection=new PixelRect(edge.X,edge.Y,0,0);SelectionStarted?.Invoke();Refresh();
    }
    private void Move(object sender,MouseEventArgs e) {
        var view=e.GetPosition(this);pointer=ToPixel(view);
        if(windows is not null) {
            if(WindowUnderCursor() is PickableWindow target) {
                var b=target.Bounds;
                selection=new PixelRect(b.Left-display.Bounds.Left,b.Top-display.Bounds.Top,b.Width,b.Height).Intersect(bounds);
                ((TextBlock)hint.Child).Text=target.Title+" · 클릭하여 캡처 · Esc 취소";
            } else selection=default;
            Refresh();return;
        }
        var edge=ToEdge(view);
        switch(mode) {
            case Mode.Drawing:
                if(spaceHeld && !selection.IsEmpty) {
                    // Space while dragging moves the whole rectangle (anchor follows the pointer).
                    var moved=SelectionMath.Move(selection,edge.X-gesturePointer.X,edge.Y-gesturePointer.Y,bounds);
                    anchor=(anchor.X+moved.X-selection.X,anchor.Y+moved.Y-selection.Y);selection=moved;
                } else selection=SelectionMath.FromDrag(anchor.X,anchor.Y,edge.X,edge.Y,bounds,Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                gesturePointer=edge;break;
            case Mode.Moving:selection=SelectionMath.Move(gestureStart,edge.X-gesturePointer.X,edge.Y-gesturePointer.Y,bounds);break;
            case Mode.Resizing:selection=SelectionMath.Resize(gestureStart,activeHandle,edge.X,edge.Y,bounds);break;
            case Mode.Adjusting:Cursor=HandleAt(view) is SelectionHandle h?HandleCursor(h):Contains(selection,view)?Cursors.SizeAll:Cursors.Cross;break;
        }
        Refresh();
    }
    private void Up(object sender,MouseButtonEventArgs e) {
        if(windows is not null)return;
        ReleaseMouseCapture();
        if(mode==Mode.Drawing) {
            if(selection.Width<2 || selection.Height<2) {
                // A click without a drag keeps the overlay open (adjust mode) or cancels (immediate mode), like before.
                selection=default;
                if(options.AdjustBeforeCapture){mode=Mode.Idle;Refresh();}else Completed?.Invoke(null);
                return;
            }
            if(options.AdjustBeforeCapture || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){EnterAdjust();return;}
            Finish();return;
        }
        if(mode is Mode.Moving or Mode.Resizing){mode=Mode.Adjusting;Refresh();}
    }
    private void EnterAdjust() {
        mode=Mode.Adjusting;
        ((TextBlock)hint.Child).Text="Enter·더블클릭 캡처 · 방향키 이동 · Alt+방향키 크기 · [ ] 확장/축소 · A 내용에 맞춤 · Esc 취소";
        Refresh();
    }

    // ---- keyboard -------------------------------------------------------------------------
    private void OnKey(object sender,KeyEventArgs e) {
        var key=e.Key==System.Windows.Input.Key.System?e.SystemKey:e.Key;
        var shift=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);var alt=Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        int step=shift?10:1;
        bool hasSelection=!selection.IsEmpty && windows is null && mode is Mode.Adjusting or Mode.Drawing;
        switch(key) {
            case System.Windows.Input.Key.Escape:ReleaseMouseCapture();Completed?.Invoke(null);break;
            case System.Windows.Input.Key.Enter:
                if(windows is not null){if(WindowUnderCursor() is PickableWindow w)Completed?.Invoke(new(null,w.Handle));}
                else if(mode==Mode.Adjusting && !selection.IsEmpty)Finish();
                else if(mode==Mode.Idle){selection=bounds;Finish();}
                break;
            case System.Windows.Input.Key.Space:spaceHeld=true;break;
            case System.Windows.Input.Key.Tab:CopyColor(shift?PixelProbe.Rgb(PixelAt(pointer.X,pointer.Y)):PixelProbe.Hex(PixelAt(pointer.X,pointer.Y)));break;
            case System.Windows.Input.Key.C when !Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                CopyColor(PixelProbe.Hex(hasSelection?Average(selection):PixelAt(pointer.X,pointer.Y)),hasSelection?"선택 영역 평균색":null);break;
            case System.Windows.Input.Key.R:ruler=!ruler;ShowToast(ruler?"측정자 켜짐 · 포인터 아래 같은 색 범위를 잽니다":"측정자 꺼짐");RefreshPointer();break;
            case System.Windows.Input.Key.A when mode==Mode.Adjusting || (mode==Mode.Idle && !selection.IsEmpty):
                var fitted=Trim(selection);
                if(fitted==selection)ShowToast("가장자리에 단색 여백이 없습니다");else {selection=fitted;ShowToast($"내용에 맞춤 · {fitted.Width} × {fitted.Height}");}
                EnterAdjust();break;
            case System.Windows.Input.Key.OemOpenBrackets when mode==Mode.Adjusting:selection=SelectionMath.Grow(selection,-step,bounds);Refresh();break;
            case System.Windows.Input.Key.OemCloseBrackets when mode==Mode.Adjusting:selection=SelectionMath.Grow(selection,step,bounds);Refresh();break;
            case System.Windows.Input.Key.Left or System.Windows.Input.Key.Right or System.Windows.Input.Key.Up or System.Windows.Input.Key.Down:
                int dx=key==System.Windows.Input.Key.Left?-step:key==System.Windows.Input.Key.Right?step:0;
                int dy=key==System.Windows.Input.Key.Up?-step:key==System.Windows.Input.Key.Down?step:0;
                if(mode==Mode.Adjusting)selection=alt?SelectionMath.ResizeBy(selection,dx,dy,bounds):SelectionMath.Move(selection,dx,dy,bounds);
                else if(mode is Mode.Idle or Mode.Drawing)NudgePointer(dx,dy);
                Refresh();break;
            default:return;
        }
        e.Handled=true;
    }
    /// <summary>Arrow keys before/while drawing move the real pointer by physical pixels for an exact start or end point.</summary>
    private void NudgePointer(int dx,int dy) {
        if(!Native.GetCursorPos(out var p))return;
        int x=Math.Clamp(p.X+dx,display.Bounds.Left,display.Bounds.Right-1),y=Math.Clamp(p.Y+dy,display.Bounds.Top,display.Bounds.Bottom-1);
        Native.SetCursorPos(x,y);
    }

    // ---- pixels ---------------------------------------------------------------------------
    private ColorRgba PixelAt(int x,int y) {
        var px=new byte[4];
        pixels.CopyPixels(new Int32Rect(Math.Clamp(x,0,image.PixelWidth-1),Math.Clamp(y,0,image.PixelHeight-1),1,1),px,4,0);
        return PixelProbe.At(px,1,0,0);
    }
    private byte[] Region(PixelRect r) {var px=new byte[r.Width*r.Height*4];pixels.CopyPixels(new Int32Rect(r.X,r.Y,r.Width,r.Height),px,r.Width*4,0);return px;}
    private ColorRgba Average(PixelRect r) {
        // Sample at most ~250k pixels so a 4K average stays instant.
        var area=r.Intersect(bounds);if(area.IsEmpty)return PixelAt(pointer.X,pointer.Y);
        return PixelProbe.Average(Region(area.Width*area.Height>250_000?Centered(area,500):area));
    }
    private static PixelRect Centered(PixelRect r,int side)=>new(r.X+Math.Max(0,(r.Width-side)/2),r.Y+Math.Max(0,(r.Height-side)/2),Math.Min(side,r.Width),Math.Min(side,r.Height));
    private PixelRect Trim(PixelRect r) {
        var area=r.Intersect(bounds);if(area.IsEmpty || (long)area.Width*area.Height>40_000_000)return r;
        var local=PixelProbe.TrimUniformBorder(Region(area),area.Width,area.Height,new PixelRect(0,0,area.Width,area.Height),TrimTolerance);
        return new PixelRect(area.X+local.X,area.Y+local.Y,local.Width,local.Height);
    }
    private void CopyColor(string text,string? label=null) {
        try{Clipboard.SetText(text);ShowToast($"{label??"색상"} {text} 복사됨");}
        catch(ExternalException){ShowToast("다른 앱이 클립보드를 사용 중입니다");}
    }

    // ---- drawing --------------------------------------------------------------------------
    private void Refresh() {
        if(ActualWidth<=0)return;
        var full=new RectangleGeometry(new Rect(0,0,ActualWidth,ActualHeight));
        if(selection.IsEmpty) {dim.Data=full;border.Visibility=Visibility.Collapsed;sizeTag.Visibility=Visibility.Collapsed;}
        else {
            var view=ToView(selection);
            var group=new GeometryGroup {FillRule=FillRule.EvenOdd};group.Children.Add(full);group.Children.Add(new RectangleGeometry(view));dim.Data=group;
            border.Visibility=Visibility.Visible;Canvas.SetLeft(border,view.X-.5);Canvas.SetTop(border,view.Y-.5);border.Width=view.Width+1;border.Height=view.Height+1;
            ((TextBlock)sizeTag.Child).Text=windows is null?$"{selection.Width} × {selection.Height}":$"{selection.Width} × {selection.Height} · 창";
            sizeTag.Visibility=Visibility.Visible;sizeTag.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
            var tagY=view.Bottom+6+sizeTag.DesiredSize.Height<=ActualHeight?view.Bottom+6:Math.Max(0,view.Top-6-sizeTag.DesiredSize.Height);
            Canvas.SetLeft(sizeTag,Math.Clamp(view.X,0,Math.Max(0,ActualWidth-sizeTag.DesiredSize.Width)));Canvas.SetTop(sizeTag,tagY);
        }
        var showHandles=mode is Mode.Adjusting or Mode.Moving or Mode.Resizing && !selection.IsEmpty;
        var points=HandlePoints();
        for(int i=0;i<handles.Length;i++) {
            handles[i].Visibility=showHandles?Visibility.Visible:Visibility.Collapsed;
            if(showHandles){Canvas.SetLeft(handles[i],points[i].X-HandleSize/2.0);Canvas.SetTop(handles[i],points[i].Y-HandleSize/2.0);}
        }
        RefreshPointer();
    }
    private void RefreshPointer() {
        bool precise=windows is null && pointerInside && mode is Mode.Idle or Mode.Drawing;
        var at=ToView(pointer.X,pointer.Y);var cell=new Size(1/ScaleX,1/ScaleY);
        var cx=Math.Round(at.X+cell.Width/2)+.5;var cy=Math.Round(at.Y+cell.Height/2)+.5;
        crossX.Visibility=crossY.Visibility=precise && options.ShowLoupe?Visibility.Visible:Visibility.Collapsed;
        crossX.X1=0;crossX.X2=ActualWidth;crossX.Y1=crossX.Y2=cy;
        crossY.Y1=0;crossY.Y2=ActualHeight;crossY.X1=crossY.X2=cx;
        RefreshRuler(precise && ruler);
        if(!(pointerInside && options.ShowLoupe && windows is null && mode is not Mode.Moving)){loupe.Visibility=Visibility.Collapsed;return;}
        var half=LoupeSamples/2;
        var sample=new PixelRect(pointer.X-half,pointer.Y-half,LoupeSamples,LoupeSamples);
        var clipped=sample.Intersect(bounds);
        if(clipped.IsEmpty){loupe.Visibility=Visibility.Collapsed;return;}
        loupeImage.Source=new CroppedBitmap(image,new Int32Rect(clipped.X,clipped.Y,clipped.Width,clipped.Height));
        loupeImage.Width=clipped.Width*LoupeCell;loupeImage.Height=clipped.Height*LoupeCell;
        Canvas.SetLeft(loupeImage,(clipped.X-sample.X)*LoupeCell);Canvas.SetTop(loupeImage,(clipped.Y-sample.Y)*LoupeCell);
        var color=PixelAt(pointer.X,pointer.Y);
        loupeText.Text=$"X {pointer.X}  Y {pointer.Y}\n{PixelProbe.Hex(color)}  {color.R},{color.G},{color.B}";
        loupe.Visibility=Visibility.Visible;loupe.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        var size=loupe.DesiredSize;
        var x=at.X+22+size.Width<=ActualWidth?at.X+22:at.X-22-size.Width;
        var y=at.Y+22+size.Height<=ActualHeight?at.Y+22:at.Y-22-size.Height;
        Canvas.SetLeft(loupe,Math.Max(0,x));Canvas.SetTop(loupe,Math.Max(0,y));
    }
    /// <summary>Ruler: horizontal and vertical extent of the colour run under the pointer, e.g. a button's width.</summary>
    private void RefreshRuler(bool show) {
        rulerX.Visibility=rulerY.Visibility=rulerTag.Visibility=show?Visibility.Visible:Visibility.Collapsed;
        if(!show)return;
        var row=Region(new PixelRect(0,pointer.Y,image.PixelWidth,1));var column=Region(new PixelRect(pointer.X,0,1,image.PixelHeight));
        var (left,right)=PixelProbe.Run(row,pointer.X,RulerTolerance);var (up,down)=PixelProbe.Run(column,pointer.Y,RulerTolerance);
        var a=ToView(pointer.X-left,pointer.Y);var b=ToView(pointer.X+right+1,pointer.Y);
        var c=ToView(pointer.X,pointer.Y-up);var d=ToView(pointer.X,pointer.Y+down+1);
        var midY=a.Y+.5/ScaleY;var midX=c.X+.5/ScaleX;
        rulerX.X1=a.X;rulerX.X2=b.X;rulerX.Y1=rulerX.Y2=midY;
        rulerY.Y1=c.Y;rulerY.Y2=d.Y;rulerY.X1=rulerY.X2=midX;
        ((TextBlock)rulerTag.Child).Text=$"↔ {left+right+1}  ↕ {up+down+1}";
        rulerTag.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        Canvas.SetLeft(rulerTag,Math.Clamp(midX+10,0,Math.Max(0,ActualWidth-rulerTag.DesiredSize.Width)));
        Canvas.SetTop(rulerTag,Math.Clamp(midY-rulerTag.DesiredSize.Height-10,0,Math.Max(0,ActualHeight-rulerTag.DesiredSize.Height)));
    }
    private void ShowToast(string text) {
        ((TextBlock)toast.Child).Text=text;toast.Visibility=Visibility.Visible;
        toast.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        Canvas.SetLeft(toast,Math.Max(0,(ActualWidth-toast.DesiredSize.Width)/2));Canvas.SetTop(toast,24);
        toastTimer.Stop();toastTimer.Start();
    }

    // ---- handles and helpers -------------------------------------------------------------
    private Point[] HandlePoints() {
        var r=ToView(selection);
        return [new(r.Left,r.Top),new(r.Left+r.Width/2,r.Top),new(r.Right,r.Top),new(r.Right,r.Top+r.Height/2),
                new(r.Right,r.Bottom),new(r.Left+r.Width/2,r.Bottom),new(r.Left,r.Bottom),new(r.Left,r.Top+r.Height/2)];
    }
    private SelectionHandle? HandleAt(Point view) {
        if(selection.IsEmpty)return null;
        var points=HandlePoints();int best=-1;double bestDistance=HandleSize;
        for(int i=0;i<points.Length;i++){var d=(points[i]-view).Length;if(d<=bestDistance){best=i;bestDistance=d;}}
        if(best>=0)return (SelectionHandle)best;
        // Edges are grabbable along their whole length, not only at the midpoint handle.
        var r=ToView(selection);const double edge=4;
        bool inX=view.X>=r.Left-edge && view.X<=r.Right+edge, inY=view.Y>=r.Top-edge && view.Y<=r.Bottom+edge;
        if(inY && Math.Abs(view.X-r.Left)<=edge)return SelectionHandle.Left;
        if(inY && Math.Abs(view.X-r.Right)<=edge)return SelectionHandle.Right;
        if(inX && Math.Abs(view.Y-r.Top)<=edge)return SelectionHandle.Top;
        if(inX && Math.Abs(view.Y-r.Bottom)<=edge)return SelectionHandle.Bottom;
        return null;
    }
    private bool Contains(PixelRect r,Point view)=>ToView(r).Contains(view);
    private static Cursor HandleCursor(SelectionHandle h)=>h switch {
        SelectionHandle.TopLeft or SelectionHandle.BottomRight=>Cursors.SizeNWSE,
        SelectionHandle.TopRight or SelectionHandle.BottomLeft=>Cursors.SizeNESW,
        SelectionHandle.Top or SelectionHandle.Bottom=>Cursors.SizeNS,_=>Cursors.SizeWE};
    private PickableWindow? WindowUnderCursor() {
        Native.GetCursorPos(out var p);
        return windows?.FirstOrDefault(w=>p.X>=w.Bounds.Left && p.X<w.Bounds.Right && p.Y>=w.Bounds.Top && p.Y<w.Bounds.Bottom);
    }
    private void Finish() {
        if(selection.Width<1 || selection.Height<1){Completed?.Invoke(null);return;}
        Completed?.Invoke(new(new Int32Rect(display.Bounds.Left+selection.X,display.Bounds.Top+selection.Y,selection.Width,selection.Height),0));
    }
    /// <summary>Clears this monitor's selection when another monitor starts one.</summary>
    public void ClearSelection() {if(mode is Mode.Drawing or Mode.Moving or Mode.Resizing)return;selection=default;mode=Mode.Idle;Refresh();}
    /// <summary>Test hook: current selection in monitor pixels and whether handles are active.</summary>
    internal (PixelRect Selection,bool Adjusting) State=>(selection,mode==Mode.Adjusting);
    /// <summary>Test hook: drives the same code paths as mouse and keyboard input, in monitor pixels.</summary>
    internal void SimulatePointer(int x,int y,bool showRuler=false){pointer=(Math.Clamp(x,0,image.PixelWidth-1),Math.Clamp(y,0,image.PixelHeight-1));pointerInside=true;ruler=showRuler;RefreshPointer();}
    internal string LoupeText=>loupe.Visibility==Visibility.Visible?loupeText.Text:"";
    internal string RulerText=>rulerTag.Visibility==Visibility.Visible?((TextBlock)rulerTag.Child).Text:"";
    internal void SimulateSelect(PixelRect rect,bool adjust){selection=rect.Intersect(bounds);if(adjust)EnterAdjust();else Refresh();}
    internal void SimulateKey(System.Windows.Input.Key key)=>OnKey(this,new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(this)!,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});

    private static Border Chip()=>new() {Background=new SolidColorBrush(Color.FromArgb(225,15,22,34)),CornerRadius=new CornerRadius(5),Padding=new Thickness(9,5,9,6),IsHitTestVisible=false,
        Child=new TextBlock {Foreground=Brushes.White,FontSize=12}};
    private static Line Cross()=>new() {Stroke=new SolidColorBrush(Color.FromArgb(150,255,255,255)),StrokeThickness=1,IsHitTestVisible=false,SnapsToDevicePixels=true};
    private static Line Ruler()=>new() {Stroke=new SolidColorBrush(Color.FromRgb(255,72,120)),StrokeThickness=1.5,IsHitTestVisible=false};
    private static DrawingBrush GridBrush() {
        var pen=new Pen(new SolidColorBrush(Color.FromArgb(70,0,0,0)),.6);
        var drawing=new GeometryDrawing(Brushes.Transparent,pen,new RectangleGeometry(new Rect(0,0,LoupeCell,LoupeCell)));
        return new DrawingBrush(drawing) {TileMode=TileMode.Tile,Viewport=new Rect(0,0,LoupeCell,LoupeCell),ViewportUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,LoupeCell,LoupeCell),ViewboxUnits=BrushMappingMode.Absolute};
    }
}
