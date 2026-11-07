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
    private BitmapSource image;
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
    private FrameworkElement? popupChrome;
    internal BitmapSource Image=>image;
    /// <summary>The capture moved on to an editor, so closing this popup is not worth offering to undo.</summary>
    internal bool HandedOff {get;private set;}
    internal Func<IReadOnlyList<(int Left,int Top,int Right,int Bottom)>>? Occupied {get;set;}
    internal event Action? CloseAllRequested;
    internal (int Left,int Top,int Right,int Bottom) ScreenBounds=>Native.GetWindowRect(new WindowInteropHelper(this).Handle,out var r)?(r.Left,r.Top,r.Right,r.Bottom):(0,0,0,0);
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
        Loaded+=(_,_)=>{ApplyLayering();if(Pinned)FitPin();if(interactive){Position();outside.Start();}};
        outside.Tick+=(_,_)=>{
            bool pressed=Native.GetAsyncKeyState(1)<0;
            if(IsVisible && settings.ClosePreviewOutside && !Pinned && !busy && DateTime.UtcNow-shown>TimeSpan.FromMilliseconds(500) && pressed && !previousMouse && !IsMouseOver)Close();
            previousMouse=pressed;
        };
        MouseWheel+=(_,e)=>{if(!Pinned)return;if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)){SetPinOpacity(PinOpacity+(e.Delta>0?.1:-.1));e.Handled=true;return;}ResizePin(e.Delta>0?32:-32);e.Handled=true;};
        PreviewMouseDown+=(_,_)=>{if(Pinned)Activate();};
        PreviewKeyDown+=(_,e)=>{
            if(!Pinned)return;var step=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?10:1;
            var (dx,dy)=e.Key switch {Key.Left=>(-step,0),Key.Right=>(step,0),Key.Up=>(0,-step),Key.Down=>(0,step),_=>(0,0)};
            if(dx==0 && dy==0)return;NudgePin(dx,dy);e.Handled=true;
        };
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
    internal void ResizePin(double change)=>FitPin(pinImageWidth+change);
    /// <summary>The window follows the image's shape. An image too short to sit under the tools gets clear bands above and below, which show the tools only on hover.</summary>
    private void FitPin(double? imageWidth=null)
    {
        var scale=VisualTreeHelper.GetDpi(this).DpiScaleX;if(scale<=0)scale=1;
        // Actual size at first, but a large capture starts smaller so it does not cover the screen and can still grow.
        var width=Math.Clamp(imageWidth ?? Math.Min(image.PixelWidth/scale,PinStartMaxWidth),180,900);var height=width*image.PixelHeight/image.PixelWidth;
        if(height>900){height=900;width=Math.Max(180,height*image.PixelWidth/image.PixelHeight);}
        pinImageWidth=width;
        var bands=height<PinToolsHeight;
        if(imageContainer!=null){imageContainer.Margin=bands?new Thickness(0,34,0,84):new Thickness(0);}
        Width=width+2;Height=height+2+(bands?118:0);
    }
    private const double PinToolsHeight=120,PinStartMaxWidth=640;
    private double pinImageWidth;
    private void Build()
    {
        if(imageContainer!=null)imageContainer.Child=null;
        if(message.Parent is Panel old)old.Children.Remove(message);
        Content=null;thumbnail.Height=thumbnail.Width=double.NaN;thumbnail.Effect=null;popupChrome=null;popupCard=null;popupLetterboxed=false;message.ClearValue(TextBlock.ForegroundProperty);message.Foreground=Ui.Muted;thumbnail.ContextMenu=Pinned?null:PopupMenu();thumbnail.ToolTip=Pinned?"드래그로 이동 · 휠로 크기 조절 · Shift+휠로 불투명도 · 클릭하여 편집":"클릭하여 편집 · 드래그하여 PNG 파일 전달";
        var header=new DockPanel{LastChildFill=true};
        var close=Ui.IconButton("close","미리보기 닫기",Close,26);DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
        var title=Ui.Label(Pinned?"고정 이미지":"캡처",12,Ui.Muted,FontWeights.SemiBold);title.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(title);
        var tools=new DockPanel{LastChildFill=false};
        var pin=Ui.IconButton("pin",Pinned?"고정 해제":"이미지 고정",()=>{Pinned=!Pinned;if(!Pinned){ClickThrough=false;PinOpacity=1;}ApplyLayering();Build();},32);pin.Foreground=Pinned?Ui.Accent:Ui.Text;DockPanel.SetDock(pin,Dock.Right);tools.Children.Add(pin);
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
            SizeToContent=SizeToContent.Manual;imageContainer.Background=Brushes.Transparent;imageContainer.CornerRadius=new CornerRadius(0);imageContainer.BorderBrush=Ui.Line;imageContainer.BorderThickness=new Thickness(1);FitPin();
            var root=new Grid{Background=Brushes.Transparent};root.Children.Add(imageContainer);
            var top=new Border{Background=new SolidColorBrush(Color.FromArgb(235,25,28,33)),Child=header,Padding=new Thickness(8,4,6,4),VerticalAlignment=VerticalAlignment.Top};root.Children.Add(top);
            var bottom=new Border{Background=new SolidColorBrush(Color.FromArgb(235,25,28,33)),Child=tools,Padding=new Thickness(8,4,8,4),VerticalAlignment=VerticalAlignment.Bottom};root.Children.Add(bottom);
            pinHeader=top;pinTools=bottom;Content=root;SetPinChrome(IsMouseOver);
            header.MouseLeftButtonDown+=(_,e)=>{if(e.OriginalSource is TextBlock)DragMove();};
            // Either mouse button moves a pinned image; the file handle in its tools hands out a file.
            thumbnail.MouseRightButtonDown-=MovePinned;thumbnail.MouseRightButtonDown+=MovePinned;
        } else if(nearPointer) {
            // Only the image at rest; hovering dims it and shows copy and save in the middle, with the other actions in the corners.
            // It never takes focus, so Ctrl+V into another app keeps working.
            // The window keeps room for the hover controls; an image that does not fill it stays clear around it until hover.
            var fit=Math.Min(1,Math.Min(PopupImageWidth/image.PixelWidth,PopupImageHeight/image.PixelHeight));
            var shownWidth=image.PixelWidth*fit;var shownHeight=image.PixelHeight*fit;
            var boxWidth=Math.Max(shownWidth,PopupMinWidth);var boxHeight=Math.Max(shownHeight,PopupMinHeight);
            popupLetterboxed=boxWidth>shownWidth+.5 || boxHeight>shownHeight+.5;
            SizeToContent=SizeToContent.Height;Width=boxWidth+8;Height=double.NaN;pinHeader=pinTools=null;
            thumbnail.Width=shownWidth;thumbnail.Height=shownHeight;imageContainer.Background=Brushes.Transparent;imageContainer.CornerRadius=new CornerRadius(6);
            var frame=new Grid{Width=boxWidth,Height=boxHeight};frame.Children.Add(imageContainer);
            var dim=new Border{Background=new SolidColorBrush(Color.FromArgb(120,10,12,16)),CornerRadius=new CornerRadius(6),IsHitTestVisible=false};
            var chrome=new Grid();chrome.Children.Add(dim);
            Button Corner(Button b,HorizontalAlignment x,VerticalAlignment y) {
                ((Panel)b.Parent).Children.Remove(b);b.Width=b.Height=26;b.Padding=new Thickness(5);b.Margin=new Thickness(4);b.Foreground=Brushes.White;b.Background=new SolidColorBrush(Color.FromArgb(170,20,22,26));b.HorizontalAlignment=x;b.VerticalAlignment=y;chrome.Children.Add(b);return b;
            }
            Corner(close,HorizontalAlignment.Left,VerticalAlignment.Top);
            Corner(pin,HorizontalAlignment.Right,VerticalAlignment.Top);
            Corner(FindTool(tools,"편집"),HorizontalAlignment.Left,VerticalAlignment.Bottom);
            Corner(FindTool(tools,"텍스트 추출"),HorizontalAlignment.Right,VerticalAlignment.Bottom);
            var middle=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};chrome.Children.Add(middle);
            void Pill(Button b,string text) {
                ((Panel)b.Parent).Children.Remove(b);b.Width=double.NaN;b.MinWidth=62;b.Height=26;b.Padding=new Thickness(10,2,10,2);b.Margin=new Thickness(0,2,0,2);b.ToolTip=null;b.Content=new TextBlock{Text=text,FontSize=12,FontWeight=FontWeights.SemiBold,Foreground=Brushes.Black,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
                // Its own template: the shared icon button turns dark on hover, which hides this black label.
                var pillFace=new FrameworkElementFactory(typeof(Border));pillFace.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));pillFace.SetBinding(Border.BackgroundProperty,new System.Windows.Data.Binding("Background"){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
                var pillText=new FrameworkElementFactory(typeof(ContentPresenter));pillText.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);pillText.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);pillFace.AppendChild(pillText);
                b.Template=new ControlTemplate(typeof(Button)){VisualTree=pillFace};
                b.Background=new SolidColorBrush(Color.FromArgb(235,255,255,255));b.MouseEnter+=(_,_)=>b.Background=Brushes.White;b.MouseLeave+=(_,_)=>b.Background=new SolidColorBrush(Color.FromArgb(235,255,255,255));middle.Children.Add(b);
            }
            Pill(FindTool(tools,"이미지 복사"),"복사");Pill(FindTool(tools,"이미지 저장"),"저장");
            message.Text=$"{image.PixelWidth:N0} × {image.PixelHeight:N0}"+(settings.CopyAfterCapture?" · 복사됨":"");message.Foreground=Brushes.White;message.TextAlignment=TextAlignment.Center;message.FontSize=10;message.Margin=new Thickness(0,2,0,0);middle.Children.Add(message);
            var handle=FileHandle();handle.HorizontalAlignment=HorizontalAlignment.Center;handle.VerticalAlignment=VerticalAlignment.Bottom;handle.Margin=new Thickness(0,0,0,3);handle.SetValue(Button.ForegroundProperty,Brushes.White);chrome.Children.Add(handle);
            popupChrome=chrome;frame.Children.Add(chrome);
            popupCard=Ui.CardOf(frame,3);Content=popupCard;SetPinChrome(IsMouseOver);
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
        // A pinned image takes focus when clicked so arrow keys can move it; a popup never does.
        ex=Pinned?ex&~0x08000000L:ex|0x08000000L;
        Native.SetWindowLongPtr(hwnd,-20,(nint)ex);
    }
    private void MovePinned(object sender,MouseButtonEventArgs e)
    {
        if(!Pinned)return;BeginPinMove();e.Handled=true;
    }
    private void BeginPinMove(){Native.GetCursorPos(out var p);pinMoveStart=p;Native.GetWindowRect(new WindowInteropHelper(this).Handle,out pinMoveBounds);thumbnail.CaptureMouse();}
    private const double PopupImageWidth=204,PopupImageHeight=150,PopupMinWidth=164,PopupMinHeight=124;
    private bool popupLetterboxed;
    private Border? popupCard;
    private void SetPinChrome(bool show){
        if(popupCard!=null && popupChrome!=null && popupLetterboxed){popupCard.Background=show?Ui.Card:Brushes.Transparent;popupCard.BorderBrush=show?Ui.Line:Brushes.Transparent;}
        if(popupChrome!=null){popupChrome.Opacity=show?1:0;popupChrome.IsHitTestVisible=show;thumbnail.Effect=show?new System.Windows.Media.Effects.BlurEffect{Radius=4}:null;}
        if(pinHeader==null || pinTools==null)return;pinHeader.Opacity=pinTools.Opacity=show?1:0;pinHeader.IsHitTestVisible=pinTools.IsHitTestVisible=show;
    }
    private void NudgePin(int dx,int dy)
    {
        var hwnd=new WindowInteropHelper(this).Handle;
        if(Native.GetWindowRect(hwnd,out var r))Native.SetWindowPos(hwnd,0,r.Left+dx,r.Top+dy,0,0,0x1|0x4|0x10);
    }
    private ContextMenu PopupMenu()
    {
        var menu=new ContextMenu();
        void Add(string text,Action action){var item=new MenuItem{Header=text};item.Click+=(_,_)=>action();menu.Items.Add(item);}
        Add("편집",RequestEdit);
        Add("복사",()=>_=RunAsync(async()=>message.Text=await ImageActions.CopyAsync(image)?"복사됨":"복사 취소됨"));
        Add("저장",()=>Run(()=>{if(ImageActions.SaveDialog(image,this)!=null)message.Text="저장됨";}));
        Add("고정",()=>{Pinned=true;Build();ApplyLayering();});
        Add("텍스트 추출",()=>OcrRequested?.Invoke(image));
        menu.Items.Add(new Separator());
        void Turn(ImageTurn turn){image=ImageActions.Turn(image,turn);thumbnail.Source=image;Build();}
        Add("왼쪽으로 회전",()=>Turn(ImageTurn.Left));Add("오른쪽으로 회전",()=>Turn(ImageTurn.Right));
        Add("기본 앱으로 열기",()=>Run(()=>{
            var path=Path.Combine(SettingsStore.DirectoryPath,"Drag",Guid.NewGuid()+".png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);ImageActions.Save(image,path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=true})?.Dispose();
        }));
        menu.Items.Add(new Separator());
        Add("닫기",Close);Add("팝업 모두 닫기",()=>CloseAllRequested?.Invoke());
        return menu;
    }
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
            if(Occupied?.Invoke() is {Count:>0} taken)p=AvoidOverlap(p.X,p.Y,r.Width,r.Height,taken,monitor.Work.Left,monitor.Work.Right,monitor.Work.Bottom);
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
    /// <summary>Moves a new popup clear of those already open: below them, else beside them, else where it was.</summary>
    internal static (int X,int Y) AvoidOverlap(int x,int y,int width,int height,IReadOnlyList<(int Left,int Top,int Right,int Bottom)> taken,int left,int right,int bottom)
    {
        const int gap=8;int startX=x,startY=y;
        for(int i=0;i<=taken.Count*2;i++) {
            var hit=taken.FirstOrDefault(t=>x<t.Right && x+width>t.Left && y<t.Bottom && y+height>t.Top);
            if(hit==default)return (x,y);
            if(hit.Bottom+gap+height<=bottom)y=hit.Bottom+gap;
            else if(hit.Left-gap-width>=left){x=hit.Left-gap-width;y=startY;}
            else if(hit.Right+gap+width<=right){x=hit.Right+gap;y=startY;}
            else return (startX,startY);
        }
        return (startX,startY);
    }
    private void RequestEdit(){if(busy)return;bool opened=false;Run(()=>{EditRequested?.Invoke(image);opened=true;});if(opened && !Pinned){HandedOff=true;Close();}}
    private void Run(Action action){if(busy)return;busy=true;try{action();}catch(Exception e){message.Text=e.Message;}finally{busy=false;}}
    private async Task RunAsync(Func<Task> action){if(busy)return;busy=true;try{await action();}catch(Exception e){message.Text=e.Message;}finally{busy=false;}}
}
