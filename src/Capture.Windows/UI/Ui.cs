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
        if(target is Window w)Apply(w,e.NewValue is true);
    }
    /// <summary>Title bar colours for the editor, which follows the Windows app theme instead of always being dark.</summary>
    public static void Apply(Window w,bool dark)
    {
        void Set(){var hwnd=new WindowInteropHelper(w).Handle;if(hwnd==0)return;int enabled=dark?1:0;DwmSetWindowAttribute(hwnd,20,ref enabled,4);int caption=dark?0x00211C19:0x00F7F6F6;DwmSetWindowAttribute(hwnd,35,ref caption,4);int text=dark?0x00F6F2F0:0x001F1D1D;DwmSetWindowAttribute(hwnd,36,ref text,4);}
        if(new WindowInteropHelper(w).Handle!=0)Set();else w.SourceInitialized+=(_,_)=>Set();
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd,int attribute,ref int value,int size);
}

public static class EditorChrome
{
    public static readonly DependencyProperty CornerRadiusProperty=DependencyProperty.RegisterAttached("CornerRadius",typeof(CornerRadius),typeof(EditorChrome),new PropertyMetadata(new CornerRadius(8)));
    public static void SetCornerRadius(DependencyObject target,CornerRadius value)=>target.SetValue(CornerRadiusProperty,value);
    public static CornerRadius GetCornerRadius(DependencyObject target)=>(CornerRadius)target.GetValue(CornerRadiusProperty);
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
        if(icon is Viewbox {Child:System.Windows.Controls.Canvas canvas}) {
            foreach(var path in canvas.Children.OfType<Path>())path.SetBinding(Path.FillProperty,new Binding("Foreground"){Source=control});
        } else icon.SetBinding(TextBlock.ForegroundProperty,new Binding("Foreground"){Source=control});
    }
    internal static FrameworkElement Icon(string name,double size=20)
    {
        name=name switch {"⧉"=>"copy","↧"=>"save","⌖"=>"pin","✎"=>"edit",_=>name};
        if(!FluentIcons.Paths.TryGetValue(name,out var data)) {
            var label=new TextBlock {Text=name,FontSize=size,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center,MinWidth=size};
            label.SetBinding(TextBlock.ForegroundProperty,new Binding("Foreground"){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Control),1),FallbackValue=Text});return label;
        }
        // Keep the original 24 px design grid; never normalize glyphs by their ink bounds.
        var path=new Path {Data=Geometry.Parse("F1 "+data)};
        path.SetBinding(Path.FillProperty,new Binding("Foreground"){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Control),1),FallbackValue=Text});
        var canvas=new System.Windows.Controls.Canvas {Width=24,Height=24};canvas.Children.Add(path);
        return new Viewbox {Width=size,Height=size,Child=canvas,Stretch=Stretch.Uniform};
    }
    internal static DockPanel Header(string title,string subtitle)
    {
        var row=new DockPanel{Margin=new Thickness(0,0,0,22)};var labels=new StackPanel();labels.Children.Add(Label(title,24,weight:FontWeights.SemiBold));labels.Children.Add(new TextBlock{Text=subtitle,Foreground=Muted,Margin=new Thickness(0,7,0,0),TextWrapping=TextWrapping.Wrap});row.Children.Add(labels);return row;
    }
    internal static void Exclude(Window w)=>w.SourceInitialized+=(_,_)=>Platform.Native.SetWindowDisplayAffinity(new WindowInteropHelper(w).Handle,0x11);
}
