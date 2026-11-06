using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Capture.Windows.Platform;
using Capture.Windows.UI;

namespace Capture.Windows;
internal sealed class PreviewWindow : Window
{
    private readonly BitmapSource image;
    private readonly AppSettings settings;
    private readonly Image thumbnail;
    private readonly TextBlock message=Ui.Label("",11,Ui.Muted);
    private readonly DispatcherTimer outside=new(){Interval=TimeSpan.FromMilliseconds(100)};
    private readonly DateTime shown=DateTime.UtcNow;
    private readonly bool interactive;
    /// <summary>The after-capture popup: a small card beside the pointer instead of the corner preview.</summary>
    private readonly bool nearPointer;
    private bool busy,previousMouse;
    private Point? down;
    private Native.POINT? pinMoveStart;
    private Native.RECT pinMoveBounds;
    private Border? imageContainer;
    private FrameworkElement? pinHeader,pinTools;
    private Slider? pinOpacity;
    public bool Pinned{get;private set;}
    /// <summary>Pinned images can be made translucent (Shift+wheel or the opacity button) and click-through for overlay comparison.</summary>
    public double PinOpacity{get;private set;}=1;
    public bool ClickThrough{get;private set;}
    public event Action<BitmapSource>? EditRequested;
    public event Action<BitmapSource>? OcrRequested;
    internal PreviewWindow(BitmapSource bitmap,AppSettings preferences,bool pinned=false,bool enableDesktopIntegration=true,bool nearPointer=false)
    {
        image=bitmap;settings=preferences;Pinned=pinned;interactive=enableDesktopIntegration;this.nearPointer=nearPointer;
        Title="Snaplet · 미리보기";WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        thumbnail=new Image{Source=image,Stretch=Stretch.Uniform,Cursor=Cursors.Hand,ToolTip="클릭하여 편집 · 드래그하여 PNG 파일 전달"};
        thumbnail.MouseLeftButtonDown+=(_,e)=>down=e.GetPosition(thumbnail);
        thumbnail.MouseMove+=(_,e)=>{
            if(down is not Point start || busy || e.LeftButton!=MouseButtonState.Pressed || (e.GetPosition(thumbnail)-start).Length<8)return;
            down=null;
            // A pinned image moves when dragged; only the unpinned preview hands out a file.
            if(Pinned){BeginPinMove();return;}
            DragFile(thumbnail);
        };
        thumbnail.MouseMove+=(_,e)=>{if(pinMoveStart is not Native.POINT p || e.RightButton!=MouseButtonState.Pressed && e.LeftButton!=MouseButtonState.Pressed)return;Native.GetCursorPos(out var at);Native.SetWindowPos(new WindowInteropHelper(this).Handle,0,pinMoveBounds.Left+at.X-p.X,pinMoveBounds.Top+at.Y-p.Y,0,0,0x1|0x4|0x10);};
        thumbnail.MouseRightButtonUp+=(_,e)=>{if(pinMoveStart==null)return;pinMoveStart=null;thumbnail.ReleaseMouseCapture();e.Handled=true;};
        thumbnail.LostMouseCapture+=(_,_)=>pinMoveStart=null;
        thumbnail.MouseLeftButtonUp+=(_,e)=>{if(pinMoveStart!=null){pinMoveStart=null;thumbnail.ReleaseMouseCapture();e.Handled=true;return;}var start=down;down=null;if(start is Point p && (e.GetPosition(thumbnail)-p).Length<8)RequestEdit();};
        Build();
        SourceInitialized+=(_,_)=>{
            var hwnd=new WindowInteropHelper(this).Handle;Native.SetWindowLongPtr(hwnd,-20,(nint)(Native.GetWindowLongPtr(hwnd,-20).ToInt64()|0x08000000L|0x80L));Native.SetWindowDisplayAffinity(hwnd,0x11);
        };
        Loaded+=(_,_)=>{if(interactive){Position();outside.Start();}};
        outside.Tick+=(_,_)=>{
            bool pressed=Native.GetAsyncKeyState(1)<0;
            if(settings.ClosePreviewOutside && !Pinned && !busy && DateTime.UtcNow-shown>TimeSpan.FromMilliseconds(500) && pressed && !previousMouse && !IsMouseOver)Close();
            previousMouse=pressed;
        };
        MouseWheel+=(_,e)=>{if(!Pinned)return;if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)){SetPinOpacity(PinOpacity+(e.Delta>0?.1:-.1));e.Handled=true;return;}ResizePin(e.Delta>0?32:-32);e.Handled=true;};
        MouseEnter+=(_,_)=>SetPinChrome(true);MouseLeave+=(_,_)=>SetPinChrome(false);
        Closed+=(_,_)=>outside.Stop();
    }
    private void DragFile(DependencyObject from)=>Run(()=>{
        var path=Path.Combine(SettingsStore.DirectoryPath,"Drag",Guid.NewGuid()+".png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);ImageActions.Save(image,path);
        DragDrop.DoDragDrop(from,new DataObject(DataFormats.FileDrop,new[]{path}),DragDropEffects.Copy);message.Text="파일을 전달했습니다.";
    });
    /// <summary>A visible handle for dragging the capture out as a PNG file.</summary>
    private FrameworkElement FileHandle()
    {
        var icon=new StackPanel {Orientation=Orientation.Horizontal};icon.Children.Add(Ui.Grip());icon.Children.Add(Ui.Icon("file",16));icon.Children.Add(Ui.Grip());
        var handle=new Button {Content=icon,ToolTip="끌어서 파일로 놓기",Height=30,Padding=new Thickness(2),Cursor=Cursors.SizeAll,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Foreground=Ui.Text,Style=(Style)Application.Current.FindResource("EditorIconButton")};
        System.Windows.Automation.AutomationProperties.SetName(handle,"끌어서 파일로 놓기");
        Point? start=null;
        handle.PreviewMouseLeftButtonDown+=(_,e)=>start=e.GetPosition(handle);
        handle.PreviewMouseLeftButtonUp+=(_,_)=>start=null;
        handle.PreviewMouseMove+=(_,e)=>{
            if(start is not Point s || e.LeftButton!=MouseButtonState.Pressed || (e.GetPosition(handle)-s).Length<4)return;
            start=null;e.Handled=true;DragFile(handle);
        };
        return handle;
    }
    internal void ResizePin(double change){Width=Math.Clamp(Width+change,180,900);Height=PinHeight(Width);}
    private double PinHeight(double width)=>Math.Clamp(width*image.PixelHeight/image.PixelWidth,120,900);
    private void Build()
    {
        if(imageContainer!=null)imageContainer.Child=null;
        if(message.Parent is Panel old)old.Children.Remove(message);
        Content=null;thumbnail.Height=double.NaN;thumbnail.ToolTip=Pinned?"드래그로 이동 · 휠로 크기 조절 · Shift+휠로 불투명도 · 클릭하여 편집":"클릭하여 편집 · 드래그하여 PNG 파일 전달";
        var header=new DockPanel{LastChildFill=true};
        var close=Ui.IconButton("close","미리보기 닫기",Close,26);DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
        var title=Ui.Label(Pinned?"고정 이미지":"캡처",12,Ui.Muted,FontWeights.SemiBold);title.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(title);
        var tools=new DockPanel{LastChildFill=false};
        var pin=Ui.IconButton("pin",Pinned?"고정 해제":"이미지 고정",()=>{Pinned=!Pinned;if(!Pinned){ClickThrough=false;PinOpacity=1;ApplyLayering();}Build();},32);pin.Foreground=Pinned?Ui.Accent:Ui.Text;DockPanel.SetDock(pin,Dock.Right);tools.Children.Add(pin);
        Panel left=Pinned?new WrapPanel():new StackPanel{Orientation=Orientation.Horizontal};tools.Children.Add(left);
        left.Children.Add(Ui.IconButton("edit","편집",RequestEdit,34));
        var copy=Ui.IconButton("copy","이미지 복사",()=>{},34);copy.Click+=async (_,_)=>await RunAsync(async()=>message.Text=await ImageActions.CopyAsync(image)?"클립보드에 복사했습니다.":"새 복사 요청이 있어 취소했습니다.");left.Children.Add(copy);
        left.Children.Add(Ui.IconButton("save","이미지 저장",()=>Run(()=>{if(ImageActions.SaveDialog(image,this)!=null)message.Text="파일로 저장했습니다.";}),34));
        left.Children.Add(Ui.IconButton("ocr","텍스트 추출",()=>OcrRequested?.Invoke(image),34));
        if(Pinned) {
            left.Children.Add(Ui.IconButton("−","축소 · 휠",()=>ResizePin(-64),34));
            left.Children.Add(Ui.IconButton("+","확대 · 휠",()=>ResizePin(64),34));
            pinOpacity=new Slider{Minimum=.2,Maximum=1,Value=PinOpacity,Width=72,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,4,0),ToolTip="불투명도 · Shift+휠"};
            System.Windows.Automation.AutomationProperties.SetName(pinOpacity,"불투명도");pinOpacity.ValueChanged+=(_,e)=>SetPinOpacity(e.NewValue);left.Children.Add(pinOpacity);
            left.Children.Add(Ui.IconButton("↖","클릭 통과 · 트레이 메뉴에서 해제",()=>SetClickThrough(true),34));
            left.Children.Add(FileHandle());
        }
        imageContainer=new Border{Background=new SolidColorBrush(Color.FromRgb(17,19,23)),CornerRadius=new CornerRadius(6),Child=thumbnail};
        message.Text=Pinned?"":"클릭하여 편집 · 드래그하여 전달";message.TextTrimming=TextTrimming.CharacterEllipsis;message.TextWrapping=TextWrapping.NoWrap;
        if(Pinned) {
            SizeToContent=SizeToContent.Manual;Width=360;Height=PinHeight(Width);
            var root=new Grid{Background=Ui.Page};root.Children.Add(imageContainer);
            var top=new Border{Background=new SolidColorBrush(Color.FromArgb(235,25,28,33)),Child=header,Padding=new Thickness(8,4,6,4),VerticalAlignment=VerticalAlignment.Top};root.Children.Add(top);
            var bottom=new Border{Background=new SolidColorBrush(Color.FromArgb(235,25,28,33)),Child=tools,Padding=new Thickness(8,4,8,4),VerticalAlignment=VerticalAlignment.Bottom};root.Children.Add(bottom);
            pinHeader=top;pinTools=bottom;Content=new Border{BorderBrush=Ui.Line,BorderThickness=new Thickness(1),Child=root};SetPinChrome(IsMouseOver);
            header.MouseLeftButtonDown+=(_,e)=>{if(e.OriginalSource is TextBlock)DragMove();};
            // Either mouse button moves a pinned image; the file handle in its tools hands out a file.
            thumbnail.MouseRightButtonDown-=MovePinned;thumbnail.MouseRightButtonDown+=MovePinned;
        } else if(nearPointer) {
            // Image with a close corner, one row of actions and a drag handle, and a line that names the hovered action.
            // It never takes focus, so Ctrl+V into another app keeps working.
            SizeToContent=SizeToContent.Height;Width=212;Height=double.NaN;pinHeader=pinTools=null;
            var root=new StackPanel();
            thumbnail.Height=Math.Clamp(196d*image.PixelHeight/image.PixelWidth,48,120);imageContainer.CornerRadius=new CornerRadius(5);
            var frame=new Grid();frame.Children.Add(imageContainer);root.Children.Add(frame);
            ((Panel)close.Parent).Children.Remove(close);close.Width=close.Height=22;close.Padding=new Thickness(4);close.Margin=new Thickness(3);close.Foreground=Brushes.White;close.Background=new SolidColorBrush(Color.FromArgb(150,20,22,26));close.HorizontalAlignment=HorizontalAlignment.Right;close.VerticalAlignment=VerticalAlignment.Top;frame.Children.Add(close);
            var row=new DockPanel{LastChildFill=false,Margin=new Thickness(0,6,0,0)};root.Children.Add(row);
            var handle=FileHandle();DockPanel.SetDock(handle,Dock.Right);row.Children.Add(handle);
            var idle=$"{image.PixelWidth:N0} × {image.PixelHeight:N0}"+(settings.CopyAfterCapture?" · 복사됨 ✓":"");
            foreach(Button b in new[]{(Button)FindTool(tools,"이미지 복사"),FindTool(tools,"이미지 저장"),FindTool(tools,"편집"),FindTool(tools,"텍스트 추출"),pin}) {
                ((Panel)b.Parent).Children.Remove(b);b.Width=b.Height=28;b.Padding=new Thickness(5);b.Margin=new Thickness(0,0,1,0);DockPanel.SetDock(b,Dock.Left);row.Children.Add(b);
                var name=System.Windows.Automation.AutomationProperties.GetName(b);
                b.MouseEnter+=(_,_)=>message.Text=name;b.MouseLeave+=(_,_)=>message.Text=idle;
            }
            message.Text=idle;message.Margin=new Thickness(1,4,0,0);root.Children.Add(message);
            message.MouseLeftButtonDown+=(_,_)=>DragMove();
            Content=Ui.CardOf(root,7);
        } else {
            SizeToContent=SizeToContent.Height;Width=334;Height=double.NaN;pinHeader=pinTools=null;
            var root=new StackPanel();header.Margin=new Thickness(0,0,0,8);root.Children.Add(header);
            thumbnail.Height=Math.Clamp(310d*image.PixelHeight/image.PixelWidth,92,210);root.Children.Add(imageContainer);
            var dimensions=Ui.Label($"{image.PixelWidth:N0} × {image.PixelHeight:N0} px",10,Ui.Muted);dimensions.Margin=new Thickness(1,8,0,3);root.Children.Add(dimensions);root.Children.Add(tools);
            message.Margin=new Thickness(1,5,0,0);root.Children.Add(message);Content=Ui.CardOf(root,11);
            header.MouseLeftButtonDown+=(_,e)=>{if(e.OriginalSource is TextBlock)DragMove();};
        }
    }
    /// <summary>The whole pin (not just its image) becomes see-through. This window is created transparent, the only state in which WPF honours Window.Opacity.</summary>
    internal void SetPinOpacity(double value)
    {
        PinOpacity=Pinned?Math.Clamp(Math.Round(value,2),.2,1):1;ApplyLayering();
        if(pinOpacity!=null && Math.Abs(pinOpacity.Value-PinOpacity)>.001)pinOpacity.Value=PinOpacity;
        if(Pinned)message.Text=$"불투명도 {PinOpacity:P0}";
    }
    /// <summary>Click-through pins ignore the mouse entirely; turn it off from the tray menu.</summary>
    internal void SetClickThrough(bool value){ClickThrough=Pinned && value;if(ClickThrough && PinOpacity>.8)PinOpacity=.6;ApplyLayering();}
    private void ApplyLayering()
    {
        Opacity=PinOpacity;
        var hwnd=new WindowInteropHelper(this).Handle;if(hwnd==0)return;
        long ex=Native.GetWindowLongPtr(hwnd,-20).ToInt64();
        ex=ClickThrough?ex|0x20L:ex&~0x20L;
        Native.SetWindowLongPtr(hwnd,-20,(nint)ex);
    }
    private void MovePinned(object sender,MouseButtonEventArgs e)
    {
        if(!Pinned)return;BeginPinMove();e.Handled=true;
    }
    private void BeginPinMove(){Native.GetCursorPos(out var p);pinMoveStart=p;Native.GetWindowRect(new WindowInteropHelper(this).Handle,out pinMoveBounds);thumbnail.CaptureMouse();}
    private void SetPinChrome(bool show){if(pinHeader==null || pinTools==null)return;pinHeader.Opacity=pinTools.Opacity=show?1:0;pinHeader.IsHitTestVisible=pinTools.IsHitTestVisible=show;}
    private static Button FindTool(Panel tools,string label)=>tools.Children.OfType<Button>().Concat(tools.Children.OfType<Panel>().SelectMany(p=>p.Children.OfType<Button>()))
        .Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)==label);
    private void Position()
    {
        var monitor=Native.PointerDisplay();var hwnd=new WindowInteropHelper(this).Handle;
        if(nearPointer && !Pinned){PlaceNearPointer(hwnd,monitor);return;}
        Native.SetWindowPos(hwnd,(nint)(-1),monitor.Work.Left+16,monitor.Work.Top+16,0,0,0x1|0x10);
        Dispatcher.BeginInvoke(()=>{
            if(!IsVisible || !Native.GetWindowRect(hwnd,out var r))return;
            Native.SetWindowPos(hwnd,(nint)(-1),Math.Max(monitor.Work.Left,monitor.Work.Right-r.Width-18),Math.Max(monitor.Work.Top,monitor.Work.Bottom-r.Height-18),0,0,0x1|0x10);
        },DispatcherPriority.ContextIdle);
    }
    /// <summary>Below and right of the pointer, flipped to the other side wherever that would leave the monitor's work area.</summary>
    private void PlaceNearPointer(nint hwnd,DisplayInfo monitor)
    {
        Native.GetCursorPos(out var at);
        // Show off-screen first so the measured window size is in this monitor's physical pixels.
        Native.SetWindowPos(hwnd,(nint)(-1),at.X+18,at.Y+18,0,0,0x1|0x10);
        Dispatcher.BeginInvoke(()=>{
            if(!IsVisible || !Native.GetWindowRect(hwnd,out var r))return;
            var p=PopupPlacement(at.X,at.Y,r.Width,r.Height,monitor.Work.Left,monitor.Work.Top,monitor.Work.Right,monitor.Work.Bottom);
            Native.SetWindowPos(hwnd,(nint)(-1),p.X,p.Y,0,0,0x1|0x10);
        },DispatcherPriority.ContextIdle);
    }
    internal static (int X,int Y) PopupPlacement(int pointerX,int pointerY,int width,int height,int left,int top,int right,int bottom)
    {
        const int gap=18;
        int x=pointerX+gap+width<=right?pointerX+gap:pointerX-gap-width;
        int y=pointerY+gap+height<=bottom?pointerY+gap:pointerY-gap-height;
        return (Math.Clamp(x,left,Math.Max(left,right-width)),Math.Clamp(y,top,Math.Max(top,bottom-height)));
    }
    private void RequestEdit(){if(busy)return;bool opened=false;Run(()=>{EditRequested?.Invoke(image);opened=true;});if(opened && !Pinned)Close();}
    private void Run(Action action){if(busy)return;busy=true;try{action();}catch(Exception e){message.Text=e.Message;}finally{busy=false;}}
    private async Task RunAsync(Func<Task> action){if(busy)return;busy=true;try{await action();}catch(Exception e){message.Text=e.Message;}finally{busy=false;}}
}
