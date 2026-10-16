using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Windows.Platform;

namespace Capture.Windows;

internal sealed record SelectionResult(Int32Rect? Region,nint Window);

/// <summary>One physical monitor per overlay avoids one DPI assumption for the virtual desktop.</summary>
internal sealed class SelectionWindow : Window
{
    private readonly Canvas canvas=new();
    private readonly System.Windows.Shapes.Rectangle rectangle=new() { Stroke=Brushes.DeepSkyBlue,StrokeThickness=2,Fill=new SolidColorBrush(Color.FromArgb(35,60,180,255)),Visibility=Visibility.Collapsed,IsHitTestVisible=false };
    private readonly TextBlock label=new() { Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(220,15,22,34)),Padding=new Thickness(14),IsHitTestVisible=false };
    private Point? anchor;
    private Rect selection;
    private readonly DisplayInfo display;
    private readonly BitmapSource image;
    private readonly IReadOnlyList<PickableWindow>? windows;
    public event Action<SelectionResult?>? Completed;

    public SelectionWindow(DisplayInfo monitor,BitmapSource background,IReadOnlyList<PickableWindow>? pickWindows=null) {
        display=monitor;image=background;windows=pickWindows;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=true;
        Background=Brushes.Black;Cursor=Cursors.Cross;Title="Snaplet · 영역 선택";
        var root=new Grid();
        root.Children.Add(new Image { Source=image,Stretch=Stretch.Fill,IsHitTestVisible=false });
        root.Children.Add(new Border { Background=new SolidColorBrush(Color.FromArgb(50,0,0,0)),IsHitTestVisible=false });
        root.Children.Add(canvas);canvas.Children.Add(rectangle);canvas.Children.Add(label);
        label.Text=windows is null?"드래그하여 영역 선택 · Esc 취소":"캡처할 창을 클릭하세요 · Esc 취소";
        Canvas.SetLeft(label,24);Canvas.SetTop(label,24);Content=root;
        SourceInitialized+=(_,_)=>{
            var hwnd=new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(hwnd,(nint)(-1),display.Bounds.Left,display.Bounds.Top,display.Bounds.Width,display.Bounds.Height,0x40);
            Native.SetWindowDisplayAffinity(hwnd,0x11);
        };
        MouseLeftButtonDown+=Down;MouseMove+=Move;MouseLeftButtonUp+=Up;
        MouseRightButtonDown+=(_,_)=>Completed?.Invoke(null);
        KeyDown+=(_,e)=>{ if(e.Key==Key.Escape){ReleaseMouseCapture();Completed?.Invoke(null);e.Handled=true;} };
        Loaded+=(_,_)=>Focus();
    }
    private void Down(object sender,MouseButtonEventArgs e) {
        if(windows is not null) {
            Native.GetCursorPos(out var p);
            var target=windows.FirstOrDefault(w=>p.X>=w.Bounds.Left && p.X<w.Bounds.Right && p.Y>=w.Bounds.Top && p.Y<w.Bounds.Bottom);
            if(target is not null)Completed?.Invoke(new(null,target.Handle));
            return;
        }
        anchor=e.GetPosition(this);CaptureMouse();selection=Rect.Empty;rectangle.Visibility=Visibility.Visible;
    }
    private void Move(object sender,MouseEventArgs e) {
        if(windows is not null) {
            Native.GetCursorPos(out var p);
            var target=windows.FirstOrDefault(w=>p.X>=w.Bounds.Left && p.X<w.Bounds.Right && p.Y>=w.Bounds.Top && p.Y<w.Bounds.Bottom);
            if(target is null) {rectangle.Visibility=Visibility.Collapsed;return;}
            var b=target.Bounds;
            ShowRect(new Rect((b.Left-display.Bounds.Left)*ActualWidth/image.PixelWidth,(b.Top-display.Bounds.Top)*ActualHeight/image.PixelHeight,b.Width*ActualWidth/image.PixelWidth,b.Height*ActualHeight/image.PixelHeight));
            label.Text=target.Title+" · 클릭하여 캡처 · Esc 취소";
        } else if(anchor is Point start) {
            var end=e.GetPosition(this);
            end.X=Math.Clamp(end.X,0,ActualWidth);end.Y=Math.Clamp(end.Y,0,ActualHeight);
            selection=new Rect(start,end);ShowRect(selection);
            var r=PixelRect(selection);
            label.Text=$"{r.Width} × {r.Height} px · Esc 취소";
        }
    }
    private void ShowRect(Rect rect) { rectangle.Visibility=Visibility.Visible;Canvas.SetLeft(rectangle,rect.Left);Canvas.SetTop(rectangle,rect.Top);rectangle.Width=rect.Width;rectangle.Height=rect.Height; }
    private Int32Rect PixelRect(Rect r) {
        if(r.IsEmpty || ActualWidth<=0 || ActualHeight<=0)return Int32Rect.Empty;
        var x=Math.Clamp((int)Math.Floor(r.Left*image.PixelWidth/ActualWidth),0,image.PixelWidth);
        var y=Math.Clamp((int)Math.Floor(r.Top*image.PixelHeight/ActualHeight),0,image.PixelHeight);
        var right=Math.Clamp((int)Math.Ceiling(r.Right*image.PixelWidth/ActualWidth),x,image.PixelWidth);
        var bottom=Math.Clamp((int)Math.Ceiling(r.Bottom*image.PixelHeight/ActualHeight),y,image.PixelHeight);
        return new(x,y,right-x,bottom-y);
    }
    private void Up(object sender,MouseButtonEventArgs e) {
        if(windows is not null || anchor is null)return;
        ReleaseMouseCapture();anchor=null;
        var rect=PixelRect(selection);
        if(rect.Width<2 || rect.Height<2) {Completed?.Invoke(null);return;}
        Completed?.Invoke(new(new Int32Rect(display.Bounds.Left+rect.X,display.Bounds.Top+rect.Y,rect.Width,rect.Height),0));
    }
}
