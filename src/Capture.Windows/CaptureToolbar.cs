using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Capture.Windows.UI;

namespace Capture.Windows;

/// <summary>
/// One small bar with every way to start a capture or a recording, opened from the tray menu or its own shortcut.
/// It sits at the top of the screen under the pointer, closes on Esc or when another window takes focus,
/// and steps aside before the chosen command runs so it never ends up in the shot.
/// </summary>
internal sealed class CaptureToolbar : Window
{
    /// <summary>Capture modes, in the order the bar shows them: label, icon, command.</summary>
    internal static readonly IReadOnlyList<(string Label,string Glyph,string Command)> Captures=[
        ("영역","region","region"),("창","window","window"),("현재 화면","display","display"),("모든 화면","backdrop","desktop"),
        ("3초 후","timer","delayed"),("스크롤","scroll","scroll"),("텍스트·QR","ocr","ocr"),("직전 영역","sync","repeat")];
    internal static readonly IReadOnlyList<(string Label,string Glyph,string Command)> Recordings=[
        ("영역 녹화","record","record"),("창 녹화","window","record-window"),("화면 녹화","display","record-screen")];
    private static readonly SolidColorBrush Live=Frozen(Color.FromRgb(255,69,58));
    private readonly Action<string> run;
    private bool activated,closing;

    internal CaptureToolbar(Action<string> command,bool recording)
    {
        run=command;
        Editor.EditorTheme.FollowSystem(this);
        Title="Snaplet 캡처 도구";WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ResizeMode=ResizeMode.NoResize;
        ShowInTaskbar=false;Topmost=true;SizeToContent=SizeToContent.WidthAndHeight;
        // Off screen until it knows its size and moves under the pointer's screen, so it never flashes elsewhere.
        WindowStartupLocation=WindowStartupLocation.Manual;Left=-32000;Top=-32000;

        var row=new StackPanel {Orientation=Orientation.Horizontal};
        var grip=new ContentControl {Content=Ui.Grip(),Cursor=Cursors.SizeAll,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,4,0),ToolTip="끌어서 옮기기"};
        grip.SetResourceReference(ForegroundProperty,"MutedBrush");row.Children.Add(grip);
        foreach(var (label,glyph,name) in Captures)row.Children.Add(Tool(label,glyph,name,label is "영역" or "창"?label+" 캡처":label));
        row.Children.Add(Divider());
        foreach(var (label,glyph,name) in Recordings) {
            var button=Tool(label,glyph,name,label);button.Foreground=Live;
            // While a recording runs these would stop it instead; the tray menu has the recording controls.
            if(recording){button.IsEnabled=false;button.ToolTip="녹화 중에는 새 녹화를 시작할 수 없습니다";ToolTipService.SetShowOnDisabled(button,true);}
            row.Children.Add(button);
        }
        row.Children.Add(Divider());
        var close=Ui.IconButton("close","닫기 (Esc)",Close,32);close.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(close);

        var card=new Border {Child=row,CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1),Padding=new Thickness(6,5,6,5),Margin=new Thickness(14),
            Effect=new System.Windows.Media.Effects.DropShadowEffect {BlurRadius=22,ShadowDepth=4,Direction=270,Opacity=.28}};
        card.SetResourceReference(Border.BackgroundProperty,"CardBrush");card.SetResourceReference(Border.BorderBrushProperty,"DividerBrush");
        Content=card;

        MouseLeftButtonDown+=(_,e)=>{if(e.OriginalSource is DependencyObject source && FindButton(source) is null)DragMove();};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
        Activated+=(_,_)=>activated=true;
        // Like a flyout: clicking anywhere else puts it away. Only after it has had focus, so opening from the tray menu does not close it at once.
        Deactivated+=(_,_)=>{if(activated && !closing)Close();};
        Closing+=(_,_)=>closing=true;
        Loaded+=(_,_)=>Recording.RecordingLook.PlacePhysical(this,(width,height)=>{
            var work=Platform.Native.PointerDisplay().Work;
            return (work.Left+(work.Width-width)/2,work.Top);
        });
        Ui.Exclude(this);
    }

    private Button Tool(string label,string glyph,string command,string name)
    {
        var button=Ui.Button(name,()=>Choose(command),style:"EditorIconButton");
        var icon=Ui.Icon(glyph,20);icon.HorizontalAlignment=HorizontalAlignment.Center;Ui.BindIcon(icon,button);
        var text=new TextBlock {Text=label,FontSize=11,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,4,0,0)};
        text.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");
        var stack=new StackPanel();stack.Children.Add(icon);stack.Children.Add(text);
        button.Content=stack;button.MinWidth=58;button.Height=54;button.MinHeight=0;button.Padding=new Thickness(6,6,6,5);button.Margin=new Thickness(1,0,1,0);
        AutomationProperties.SetName(button,name);
        return button;
    }

    /// <summary>Closes first, so the bar is gone before the capture hides the app's windows and takes its shot.</summary>
    private void Choose(string command){Close();run(command);}

    private static Border Divider(){var d=new Border {Width=1,Height=34,Margin=new Thickness(5,0,5,0),VerticalAlignment=VerticalAlignment.Center};d.SetResourceReference(Border.BackgroundProperty,"DividerBrush");return d;}
    private static SolidColorBrush Frozen(Color color){var brush=new SolidColorBrush(color);brush.Freeze();return brush;}
    private static Button? FindButton(DependencyObject source)
    {
        for(var node=source;node is not null;node=node is Visual or System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node))
            if(node is Button button)return button;
        return null;
    }
}
