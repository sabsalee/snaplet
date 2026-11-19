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
    private readonly TextBlock message=new(){FontSize=11};
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
        Editor.EditorTheme.FollowSystem(this);
        Title="Snaplet · 미리보기";WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        thumbnail=new Image{Source=image,Stretch=Stretch.Uniform,Cursor=Cursors.Hand,ToolTip="클릭하여 편집 · 드래그하여 PNG 파일 전달"};
        ToolTipService.SetInitialShowDelay(thumbnail,1500);
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
        MouseWheel+=(_,e)=>{if(!Pinned)return;if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)){SetPinOpacity(PinOpacity+(e.Delta>0?.1:-.1));e.Handled=true;return;}ZoomPin(e.Delta>0?1.1:1/1.1);e.Handled=true;};
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
    private Button FileHandle()
    {
        var icon=new StackPanel {Orientation=Orientation.Horizontal};icon.Children.Add(Ui.Grip());icon.Children.Add(Ui.Icon("file",16));icon.Children.Add(Ui.Grip());
        var handle=new Button {Content=icon,ToolTip="끌어서 파일로 놓기",Height=30,Padding=new Thickness(2),Cursor=Cursors.SizeAll,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Style=(Style)Application.Current.FindResource("EditorIconButton")};handle.SetResourceReference(ForegroundProperty,"TextBrush");
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
    /// <summary>Zooms a pinned image by a factor of its current size, from a fifth of the original up to four times it.</summary>
    internal void ZoomPin(double factor)=>FitPin(pinImageWidth*factor);
    /// <summary>Shows a pinned image at its original size again.</summary>
    internal void ActualSizePin()=>FitPin(NaturalWidth);
    /// <summary>The image's size at 100%, in device-independent units on this monitor.</summary>
    private double NaturalWidth{get{var scale=VisualTreeHelper.GetDpi(this).DpiScaleX;return image.PixelWidth/(scale<=0?1:scale);}}
    internal double PinZoom=>pinImageWidth/NaturalWidth;
    /// <summary>The window follows the image plus room for its shadow; the floating bar sits inside the image's top-left corner.</summary>
    private void FitPin(double? imageWidth=null)
    {
        var natural=NaturalWidth;
        // Actual size at first, but a large capture starts smaller so it does not cover the screen. It can then grow past 100%.
        var width=Math.Clamp(imageWidth ?? Math.Min(natural,PinStartMaxWidth),Math.Min(natural,48),Math.Max(natural*PinMaxZoom,48));
        var height=width*image.PixelHeight/image.PixelWidth;
        pinImageWidth=width;
        // A very short image keeps its bar above it rather than covering all of it.
        pinBarAbove=height<PinBarHeight+16;
        if(imageContainer!=null){imageContainer.Width=width;imageContainer.Height=height;imageContainer.Margin=new Thickness(PinShadow,PinShadow+(pinBarAbove?PinBarHeight+6:0),PinShadow,PinShadow);}
        if(pinBar!=null){pinBar.Margin=pinBarAbove?new Thickness(PinShadow,PinShadow,0,0):new Thickness(PinShadow+8,PinShadow+8,0,0);}
        if(zoomLabel!=null)zoomLabel.Text=$"{PinZoom:P0}";
        Width=width+2*PinShadow;Height=height+2*PinShadow+(pinBarAbove?PinBarHeight+6:0);
    }
    private const double PinStartMaxWidth=640,PinMaxZoom=4,PinShadow=14,PinBarHeight=32;
    private double pinImageWidth;
    private bool pinBarAbove;
    private Border? pinBar;
    private TextBlock? zoomLabel;
    private Button IconTool(string glyph,string label,Action action,double size=30)
    {
        var b=Ui.IconButton(glyph,label,action,size);b.Padding=new Thickness(6);b.SetResourceReference(ForegroundProperty,"TextBrush");return b;
    }
    private void Copy()=>_=RunAsync(async()=>message.Text=await ImageActions.CopyAsync(image)?"클립보드에 복사했습니다.":"새 복사 요청이 있어 취소했습니다.");
    private void SaveAs()=>Run(()=>{if(ImageActions.SaveDialog(image,this)!=null)message.Text="파일로 저장했습니다.";});
    private void TogglePin(){Pinned=!Pinned;if(!Pinned){ClickThrough=false;PinOpacity=1;}ApplyLayering();Build();if(interactive && !Pinned)Position();}
    private void Build()
    {
        // The image may sit inside the pin's clipping border or the popup's frame; free it from whichever holds it.
        if(thumbnail.Parent is System.Windows.Controls.Decorator holder)holder.Child=null;
        if(imageContainer!=null)imageContainer.Child=null;
        if(message.Parent is Panel old)old.Children.Remove(message);
        Content=null;thumbnail.Height=thumbnail.Width=double.NaN;thumbnail.Effect=null;pinBar=null;zoomLabel=null;
        thumbnail.ContextMenu=Pinned?null:PopupMenu();thumbnail.Stretch=Pinned?Stretch.Fill:Stretch.Uniform;
        thumbnail.ToolTip=Pinned?"드래그로 이동 · 휠로 확대·축소 · Shift+휠로 불투명도 · 클릭하여 편집":"클릭하여 편집 · 드래그하여 PNG 파일 전달";
        if(Pinned)BuildPin();else BuildPopup();
    }
    /// <summary>
    /// After a capture: a small window with its tools on top and the image below, like an editor at a glance.
    /// The tools are always visible; the image opens the editor on click and hands out a PNG on drag.
    /// </summary>
    private void BuildPopup()
    {
        SizeToContent=SizeToContent.WidthAndHeight;Width=Height=double.NaN;
        var tools=new DockPanel{LastChildFill=false,Margin=new Thickness(6,5,6,3)};
        var left=new StackPanel{Orientation=Orientation.Horizontal};
        left.Children.Add(IconTool("close","미리보기 닫기",Close));
        left.Children.Add(Divider());
        left.Children.Add(IconTool("copy","이미지 복사",Copy));
        left.Children.Add(IconTool("save","이미지 저장",SaveAs));
        left.Children.Add(IconTool("pin","이미지 고정",TogglePin));
        left.Children.Add(Divider());
        left.Children.Add(FileHandle());
        var right=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(right,Dock.Right);
        right.Children.Add(IconTool("ocr","텍스트 추출",()=>OcrRequested?.Invoke(image)));
        right.Children.Add(IconTool("edit","편집",RequestEdit));
        tools.Children.Add(right);tools.Children.Add(left);
        // Fit inside the card without enlarging; the toolbar sets the card's minimum width.
        var fit=Math.Min(1,Math.Min(PopupImageWidth/image.PixelWidth,PopupImageHeight/image.PixelHeight));
        thumbnail.Width=Math.Max(1,image.PixelWidth*fit);thumbnail.Height=Math.Max(1,image.PixelHeight*fit);
        imageContainer=new Border{Child=thumbnail,CornerRadius=new CornerRadius(6),Margin=new Thickness(8,0,8,0),HorizontalAlignment=HorizontalAlignment.Center,ClipToBounds=true};
        imageContainer.Clip=new RectangleGeometry(new Rect(0,0,thumbnail.Width,thumbnail.Height),6,6);
        var stage=new Border{Child=imageContainer,Margin=new Thickness(8,0,8,0),CornerRadius=new CornerRadius(6),MinHeight=60,Padding=new Thickness(0)};
        stage.SetResourceReference(Border.BackgroundProperty,"WorkspaceBrush");
        message.Text=$"{image.PixelWidth:N0} × {image.PixelHeight:N0}"+(settings.CopyAfterCapture?" · 복사됨":"");
        message.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");message.FontSize=11;message.TextAlignment=TextAlignment.Left;message.Margin=new Thickness(12,6,12,9);
        var body=new StackPanel();body.Children.Add(tools);body.Children.Add(stage);body.Children.Add(message);
        popupCard=new Border{Child=body,CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1),Margin=new Thickness(PinShadow),MinWidth=PopupMinWidth,
            Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=22,ShadowDepth=4,Direction=270,Opacity=.28}};
        popupCard.SetResourceReference(Border.BackgroundProperty,"CardBrush");popupCard.SetResourceReference(Border.BorderBrushProperty,"DividerBrush");
        Content=popupCard;
    }
    private static Border Divider(){var d=new Border{Width=1,Height=18,Margin=new Thickness(4,0,4,0),VerticalAlignment=VerticalAlignment.Center};d.SetResourceReference(Border.BackgroundProperty,"DividerBrush");return d;}
    /// <summary>
    /// A pinned image is just the image, rounded and lifted by a shadow. Hovering shows a small dark bar inside its top-left corner.
    /// </summary>
    private void BuildPin()
    {
        SizeToContent=SizeToContent.Manual;
        var clip=new Border{Child=thumbnail,CornerRadius=new CornerRadius(8),ClipToBounds=true};
        thumbnail.Stretch=Stretch.Fill;
        imageContainer=new Border{Child=clip,CornerRadius=new CornerRadius(8),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,
            Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=18,ShadowDepth=3,Direction=270,Opacity=.35}};
        imageContainer.SizeChanged+=(_,e)=>clip.Clip=new RectangleGeometry(new Rect(e.NewSize),8,8);
        var bar=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        Button Light(string glyph,string label,Action action){var b=Ui.IconButton(glyph,label,action,26);b.Padding=new Thickness(5);b.Foreground=Brushes.White;b.Style=PinButtonStyle();return b;}
        // The close control is a red dot, as on a window's title bar.
        var close=new Button{Width=22,Height=26,Style=PinButtonStyle(),ToolTip="고정 이미지 닫기",Content=new System.Windows.Shapes.Ellipse{Width=12,Height=12,Fill=new SolidColorBrush(Color.FromRgb(255,95,87))}};
        System.Windows.Automation.AutomationProperties.SetName(close,"고정 이미지 닫기");close.Click+=(_,_)=>Close();
        bar.Children.Add(close);
        var handle=FileHandle();handle.Height=26;handle.Foreground=Brushes.White;handle.Style=PinButtonStyle();bar.Children.Add(handle);
        bar.Children.Add(Light("edit","편집",RequestEdit));
        bar.Children.Add(Light("copy","이미지 복사",Copy));
        bar.Children.Add(Light("pin","고정 해제",TogglePin));
        var more=Light("more","더 보기 · 저장·텍스트 추출·불투명도·클릭 통과",()=>{});more.Click+=(_,_)=>{var menu=PinMenu();menu.PlacementTarget=more;menu.IsOpen=true;};bar.Children.Add(more);
        zoomLabel=new TextBlock{Foreground=Brushes.White,FontSize=12,FontFamily=Ui.Font,VerticalAlignment=VerticalAlignment.Center};
        var zoom=new Button{Content=zoomLabel,Height=26,Padding=new Thickness(6,0,7,0),Style=PinButtonStyle(),ToolTip="원래 크기로 · 휠로 확대·축소"};
        System.Windows.Automation.AutomationProperties.SetName(zoom,"원래 크기로");zoom.Click+=(_,_)=>ActualSizePin();bar.Children.Add(zoom);
        pinBar=new Border{Child=bar,Background=new SolidColorBrush(Color.FromArgb(190,38,38,40)),CornerRadius=new CornerRadius(8),Padding=new Thickness(4,1,4,1),
            HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,BorderBrush=new SolidColorBrush(Color.FromArgb(40,255,255,255)),BorderThickness=new Thickness(1)};
        var root=new Grid{Background=Brushes.Transparent};root.Children.Add(imageContainer);root.Children.Add(pinBar);
        Content=root;FitPin();SetPinChrome(IsMouseOver);
        // Either mouse button moves a pinned image; the file handle in its bar hands out a file.
        thumbnail.MouseRightButtonDown-=MovePinned;thumbnail.MouseRightButtonDown+=MovePinned;
    }
    private static Style? pinButtonStyle;
    /// <summary>A flat button for the dark pin bar: a light wash on hover, no theme colours.</summary>
    private static Style PinButtonStyle()
    {
        if(pinButtonStyle!=null)return pinButtonStyle;
        var face=new FrameworkElementFactory(typeof(Border),"face");face.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));face.SetValue(Border.BackgroundProperty,Brushes.Transparent);
        face.SetBinding(Border.PaddingProperty,new System.Windows.Data.Binding("Padding"){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
        var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);face.AppendChild(content);
        var template=new ControlTemplate(typeof(Button)){VisualTree=face};
        var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,new SolidColorBrush(Color.FromArgb(46,255,255,255)),"face"));template.Triggers.Add(hover);
        pinButtonStyle=new Style(typeof(Button));pinButtonStyle.Setters.Add(new Setter(Control.TemplateProperty,template));pinButtonStyle.Setters.Add(new Setter(UIElement.FocusableProperty,false));pinButtonStyle.Setters.Add(new Setter(FrameworkElement.CursorProperty,Cursors.Hand));
        return pinButtonStyle;
    }
    private ContextMenu PinMenu()
    {
        var menu=Editor.EditorTheme.Themed(new ContextMenu(),Editor.EditorTheme.SystemUsesDark());
        void Add(ItemsControl parent,string text,Action action,bool check=false){var item=new MenuItem{Header=text,IsChecked=check};item.Click+=(_,_)=>action();parent.Items.Add(item);}
        Add(menu,"저장…",SaveAs);Add(menu,"텍스트 추출",()=>OcrRequested?.Invoke(image));
        menu.Items.Add(new Separator());
        var opacity=new MenuItem{Header="불투명도"};foreach(var value in new[]{1,.8,.6,.4}){var v=value;Add(opacity,$"{v:P0}",()=>SetPinOpacity(v),Math.Abs(PinOpacity-v)<.01);}menu.Items.Add(opacity);
        Add(menu,"클릭 통과 · 트레이 메뉴에서 해제",()=>SetClickThrough(true));
        Add(menu,"원래 크기로",ActualSizePin);
        return menu;
    }
    /// <summary>The whole pin (not just its image) becomes see-through. This window is created transparent, the only state in which WPF honours Window.Opacity.</summary>
    internal void SetPinOpacity(double value)
    {
        PinOpacity=Pinned?Math.Clamp(Math.Round(value,2),.2,1):1;ApplyLayering();
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
    private const double PopupImageWidth=300,PopupImageHeight=200,PopupMinWidth=280;
    private Border? popupCard;
    private void SetPinChrome(bool show){
        if(pinBar==null)return;pinBar.Opacity=show?1:0;pinBar.IsHitTestVisible=show;
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
