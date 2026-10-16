using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Path=System.Windows.Shapes.Path;

namespace Capture.Windows.UI;

public static class WindowAppearance
{
    public static readonly DependencyProperty DarkChromeProperty=DependencyProperty.RegisterAttached("DarkChrome",typeof(bool),typeof(WindowAppearance),new PropertyMetadata(false,Changed));
    public static void SetDarkChrome(DependencyObject target,bool value)=>target.SetValue(DarkChromeProperty,value);
    public static bool GetDarkChrome(DependencyObject target)=>(bool)target.GetValue(DarkChromeProperty);
    private static void Changed(DependencyObject target,DependencyPropertyChangedEventArgs e)
    {
        if(target is not Window w || e.NewValue is not true)return;
        void Apply(){var hwnd=new WindowInteropHelper(w).Handle;if(hwnd==0)return;int enabled=1;DwmSetWindowAttribute(hwnd,20,ref enabled,4);int caption=0x00211C19;DwmSetWindowAttribute(hwnd,35,ref caption,4);int text=0x00F6F2F0;DwmSetWindowAttribute(hwnd,36,ref text,4);}
        if(new WindowInteropHelper(w).Handle!=0)Apply();else w.SourceInitialized+=(_,_)=>Apply();
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd,int attribute,ref int value,int size);
}

internal static class Ui
{
    internal static Brush Brush(string key)=>(Brush)Application.Current.FindResource(key);
    internal static Brush Page=>Brush("PageBrush");internal static Brush Card=>Brush("CardBrush");internal static Brush Raised=>Brush("RaisedBrush");
    internal static Brush Text=>Brush("TextBrush");internal static Brush Muted=>Brush("MutedBrush");internal static Brush Accent=>Brush("AccentBrush");internal static Brush Line=>Brush("BorderBrush");
    internal static TextBlock Label(string text,double size=13,Brush? ink=null,FontWeight? weight=null)=>new(){Text=text,FontSize=size,Foreground=ink??Text,FontWeight=weight??FontWeights.Normal,TextWrapping=TextWrapping.Wrap};
    internal static Border CardOf(UIElement child,double padding=18)=>new(){Child=child,Background=Card,BorderBrush=Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Padding=new Thickness(padding)};
    internal static Border Keycap(string value)=>new(){Child=new TextBlock{Text=value,FontSize=11,Foreground=Muted,VerticalAlignment=VerticalAlignment.Center},Background=Page,CornerRadius=new CornerRadius(4),BorderBrush=Line,BorderThickness=new Thickness(1),Padding=new Thickness(7,4,7,4),VerticalAlignment=VerticalAlignment.Center};
    internal static Button Button(string title,Action action,string? glyph=null,string style="BaseButton")
    {
        var b=new Button {Style=(Style)Application.Current.FindResource(style)};
        if(glyph==null)b.Content=title;
        else {var row=new StackPanel{Orientation=Orientation.Horizontal};var icon=Icon(glyph);icon.Margin=new Thickness(0,0,8,0);row.Children.Add(icon);row.Children.Add(new TextBlock{Text=title,VerticalAlignment=VerticalAlignment.Center});b.Content=row;}
        AutomationProperties.SetName(b,title);b.Click+=(_,_)=>action();return b;
    }
    internal static Button IconButton(string glyph,string label,Action action,double size=34)
    {
        var b=Button(label,action,style:"EditorIconButton");var icon=Icon(glyph);b.Content=icon;BindIcon(icon,b);b.ToolTip=label;b.Width=size;b.Height=size;b.MinHeight=0;b.Padding=new Thickness(7);return b;
    }
    internal static void BindIcon(FrameworkElement icon,Control control)
    {
        var property=icon is Path?Path.StrokeProperty:TextBlock.ForegroundProperty;
        icon.SetBinding(property,new Binding("Foreground"){Source=control});
    }
    internal static FrameworkElement Icon(string name,double size=20)
    {
        var data=name switch {
            "region"=>"M 3,8 V 3 H 8 M 16,3 H 21 V 8 M 21,16 V 21 H 16 M 8,21 H 3 V 16 M 8,8 H 16 V 16 H 8 Z",
            "▧"=>"M 3,3 H 21 V 21 H 3 Z M 9,3 V 21 M 15,3 V 21 M 3,9 H 21 M 3,15 H 21",
            "window"=>"M 3,4 H 21 V 20 H 3 Z M 3,9 H 21 M 6,6.5 H 7 M 10,6.5 H 11",
            "display"=>"M 2,4 H 22 V 17 H 2 Z M 12,17 V 21 M 7,21 H 17",
            "clock"=>"M 12,2 A 10,10 0 1 1 11.99,2 M 12,6 V 12 L 16,14",
            "copy" or "⧉"=>"M 8,8 H 21 V 21 H 8 Z M 16,8 V 3 H 3 V 16 H 8",
            "save" or "↧"=>"M 4,3 H 18 L 21,6 V 21 H 3 V 3 Z M 7,3 V 9 H 17 V 3 M 7,21 V 14 H 17 V 21",
            "pin" or "⌖"=>"M 9,3 H 17 L 16,9 L 20,13 L 13,14 L 8,21 M 9,3 L 8,9 L 4,11 L 13,14",
            "edit" or "✎"=>"M 4,16 L 15,5 L 19,9 L 8,20 L 3,21 Z M 15,5 L 17,3 L 21,7 L 19,9 M 4,16 L 8,20",
            "close"=>"",
            "folder"=>"M 2,6 H 9 L 11,8 H 22 L 19,20 H 2 Z M 2,6 V 3 H 9 L 12,6 H 20 V 8",
            "clipboard"=>"M 8,5 H 4 V 22 H 20 V 5 H 16 M 8,3 H 16 V 8 H 8 Z M 8,12 H 16 M 8,16 H 14",
            "settings"=>"M 9,2 H 15 L 16,6 L 20,7 L 22,12 L 19,15 L 19,19 L 14,22 L 11,19 L 7,19 L 3,15 L 5,11 L 3,7 L 7,4 Z M 16,12 A 4,4 0 1 1 8,12 A 4,4 0 1 1 16,12",
            "check"=>"M 4,12 L 9,17 L 20,6",
            "ocr"=>"M 3,8 V 3 H 8 M 16,3 H 21 V 8 M 21,16 V 21 H 16 M 8,21 H 3 V 16 M 8,8 H 16 M 12,8 V 17",
            "scroll"=>"M 4,5 H 8 M 4,10 H 8 M 4,15 H 8 M 4,20 H 8 M 15,3 V 21 M 11,7 L 15,3 L 19,7 M 11,17 L 15,21 L 19,17",
            "↶"=>"M 8,5 L 3,10 L 8,15 M 3,10 H 14 A 6,6 0 0 1 14,22",
            "↷"=>"M 16,5 L 21,10 L 16,15 M 21,10 H 10 A 6,6 0 0 0 10,22",
            "↖"=>"M 4,3 L 10,21 L 13,13 L 21,10 Z",
            "➜"=>"M 3,12 H 21 M 15,6 L 21,12 L 15,18",
            "▢"=>"M 3,4 H 21 V 20 H 3 Z",
            "○"=>"M 22,12 A 10,8 0 1 1 2,12 A 10,8 0 1 1 22,12",
            "···"=>"M 4,12 H 4.1 M 12,12 H 12.1 M 20,12 H 20.1",
            "⇄"=>"M 3,8 H 21 L 17,4 M 21,16 H 3 L 7,20",
            "spark"=>"M 12,2 L 15,9 L 22,12 L 15,15 L 12,22 L 9,15 L 2,12 L 9,9 Z",
            _=>""
        };
        if(data.Length==0) {
            var label=new TextBlock{Text=name=="close"?"×":name,FontFamily=new FontFamily("Segoe UI"),FontSize=size,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center,MinWidth=size};
            label.SetBinding(TextBlock.ForegroundProperty,new Binding("Foreground"){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Control),1),FallbackValue=Text});return label;
        }
        var path=new Path {Data=Geometry.Parse(data),StrokeThickness=1.7,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Stretch=Stretch.Uniform,Width=size,Height=size};
        path.SetBinding(Path.StrokeProperty,new Binding("Foreground"){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Control),1),FallbackValue=Text});
        return path;
    }
    internal static DockPanel Header(string title,string subtitle)
    {
        var row=new DockPanel{Margin=new Thickness(0,0,0,22)};var labels=new StackPanel();labels.Children.Add(Label(title,24,weight:FontWeights.SemiBold));labels.Children.Add(new TextBlock{Text=subtitle,Foreground=Muted,Margin=new Thickness(0,7,0,0),TextWrapping=TextWrapping.Wrap});row.Children.Add(labels);return row;
    }
    internal static void Exclude(Window w)=>w.SourceInitialized+=(_,_)=>Platform.Native.SetWindowDisplayAffinity(new WindowInteropHelper(w).Handle,0x11);
}
