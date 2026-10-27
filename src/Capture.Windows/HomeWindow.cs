using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Windows.Editor;
using Capture.Windows.UI;

namespace Capture.Windows;

/// <summary>
/// The main window is an editor with nothing in it: the same toolbar band and workspace, a few hints in the middle,
/// and images arrive by capture, drop, paste or open. The app itself lives in the tray; this window only opens on request.
/// </summary>
internal sealed class HomeWindow : Window
{
    private readonly Action<string> command;
    private readonly Action<BitmapSource> edit;
    private readonly Action clipboard;
    private readonly Action open;
    private readonly Border regionKeycap;
    private readonly WrapPanel recent=new() {HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,26,0,0)};
    private readonly Button recentButton;
    private IReadOnlyList<BitmapSource> recentImages=[];
    internal HomeWindow(AppSettings settings,Action<string> capture,Action open,Action clipboard,Action demo,Action preferences,Action<BitmapSource> editImage,TextBlock status)
    {
        command=capture;edit=editImage;this.clipboard=clipboard;this.open=open;
        Title="Snaplet";Width=1180;Height=800;MinWidth=660;MinHeight=450;WindowStartupLocation=WindowStartupLocation.CenterScreen;AllowDrop=true;
        var dark=EditorTheme.SystemUsesDark();EditorTheme.Themed(this,dark);WindowAppearance.SetDarkChrome(this,dark);WindowAppearance.Apply(this,dark);
        SetResourceReference(BackgroundProperty,"PageBrush");SetResourceReference(ForegroundProperty,"TextBrush");FontFamily=new FontFamily("Segoe UI");
        var root=new DockPanel();Content=root;

        // Same toolbar band as the editor; capture and image sources sit where the editor keeps its tools.
        var toolbar=new DockPanel {LastChildFill=false,Height=52};toolbar.SetResourceReference(Panel.BackgroundProperty,"ToolbarBrush");
        var toolbarFrame=new Border {Child=toolbar,BorderThickness=new Thickness(0,0,0,1)};toolbarFrame.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");
        DockPanel.SetDock(toolbarFrame,Dock.Top);root.Children.Add(toolbarFrame);
        var left=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(left,Dock.Left);toolbar.Children.Add(left);
        void Tool(Panel panel,string glyph,string label,Action action){var b=Ui.IconButton(glyph,label,action,36);b.Margin=new Thickness(2);panel.Children.Add(b);}
        Tool(left,"region","영역 캡처",()=>command("region"));Tool(left,"window","활성 창",()=>command("window"));Tool(left,"display","현재 화면",()=>command("display"));
        Tool(left,"clock","3초 타이머",()=>command("delayed"));Tool(left,"scroll","긴 화면",()=>command("scroll"));Tool(left,"ocr","텍스트·QR",()=>command("ocr"));
        left.Children.Add(Divider());
        Tool(left,"folder","이미지 열기",open);Tool(left,"clipboard","클립보드에서 가져오기",clipboard);
        var right=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};DockPanel.SetDock(right,Dock.Right);toolbar.Children.Add(right);
        recentButton=Ui.IconButton("clock","최근 캡처",()=>{},36);recentButton.Margin=new Thickness(2);recentButton.Click+=(_,_)=>ShowRecentMenu(demo);right.Children.Add(recentButton);
        Tool(right,"settings","설정 열기",preferences);

        var footer=new DockPanel {Margin=new Thickness(14,6,14,7)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var version=Muted("v"+typeof(HomeWindow).Assembly.GetName().Version?.ToString(3),11);DockPanel.SetDock(version,Dock.Right);footer.Children.Add(version);
        status.FontSize=11;status.Margin=new Thickness(0);status.TextTrimming=TextTrimming.CharacterEllipsis;status.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
        if(status.Parent is Panel previous)previous.Children.Remove(status);footer.Children.Add(status);

        var workspace=new Grid();workspace.SetResourceReference(Panel.BackgroundProperty,"WorkspaceBrush");root.Children.Add(workspace);
        var hints=new StackPanel {HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(24)};workspace.Children.Add(hints);
        var mark=Ui.Icon("region",44);mark.Opacity=.35;mark.HorizontalAlignment=HorizontalAlignment.Center;mark.Margin=new Thickness(0,0,0,16);hints.Children.Add(mark);
        if(mark is Viewbox {Child:Canvas canvas})foreach(var path in canvas.Children.OfType<System.Windows.Shapes.Path>())path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,"MutedBrush");
        var title=new TextBlock {Text="캡처하거나 이미지를 끌어다 놓으세요",FontSize=16,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center};hints.Children.Add(title);
        var keys=new Grid {HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,16,0,0)};keys.ColumnDefinitions.Add(new());keys.ColumnDefinitions.Add(new(){Width=GridLength.Auto});hints.Children.Add(keys);
        regionKeycap=Hint(keys,"영역 캡처","");Hint(keys,"클립보드 이미지 붙여넣기","Ctrl+V");Hint(keys,"이미지 파일 열기","Ctrl+O");
        hints.Children.Add(recent);

        Drop+=(_,e)=>{
            if(e.Data.GetData(DataFormats.FileDrop) is not string[] files)return;
            foreach(var path in files.Where(File.Exists)) {
                try{edit(ImageActions.Load(path));}catch(Exception error) when(error is IOException or NotSupportedException or ArgumentException or InvalidOperationException or UnauthorizedAccessException){status.Text=error.Message;}
            }
        };
        PreviewKeyDown+=(_,e)=>{
            if(!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))return;
            if(e.Key==Key.V){this.clipboard();e.Handled=true;}else if(e.Key==Key.O){this.open();e.Handled=true;}
        };
        UpdateSettings(settings);UpdateRecent([]);
    }
    private static Border Divider(){var d=new Border {Width=1,Height=24,Margin=new Thickness(6,0,6,0)};d.SetResourceReference(Border.BackgroundProperty,"BorderBrush");return d;}
    private static TextBlock Muted(string text,double size){var t=new TextBlock {Text=text,FontSize=size,VerticalAlignment=VerticalAlignment.Center};t.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");return t;}
    private static Border Hint(Grid grid,string label,string keys)
    {
        var row=grid.RowDefinitions.Count;grid.RowDefinitions.Add(new());
        var name=Muted(label,12);name.Margin=new Thickness(0,4,14,4);Grid.SetRow(name,row);grid.Children.Add(name);
        var cap=new Border {CornerRadius=new CornerRadius(4),BorderThickness=new Thickness(1),Padding=new Thickness(6,1,6,2),Margin=new Thickness(0,3,0,3),HorizontalAlignment=HorizontalAlignment.Left,Child=Muted(keys,11)};
        cap.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");cap.SetResourceReference(Border.BackgroundProperty,"CardBrush");
        Grid.SetRow(cap,row);Grid.SetColumn(cap,1);grid.Children.Add(cap);return cap;
    }
    private void ShowRecentMenu(Action demo)
    {
        var menu=EditorTheme.Themed(new ContextMenu {PlacementTarget=recentButton,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom},!IsLightTheme);
        foreach(var image in recentImages) {
            var bitmap=image;var item=new MenuItem {Header=$"{bitmap.PixelWidth} × {bitmap.PixelHeight} px",Icon=new Image {Source=bitmap,Width=40,Height=26,Stretch=Stretch.Uniform}};
            item.Click+=(_,_)=>edit(bitmap);menu.Items.Add(item);
        }
        if(recentImages.Count==0)menu.Items.Add(new MenuItem {Header="아직 캡처가 없습니다",IsEnabled=false});
        menu.Items.Add(new Separator());
        var sample=new MenuItem {Header="편집 도구 체험"};sample.Click+=(_,_)=>demo();menu.Items.Add(sample);
        menu.IsOpen=true;
    }
    private bool IsLightTheme=>Resources.MergedDictionaries.Contains(EditorTheme.Light);
    internal void UpdateSettings(AppSettings settings)
    {
        if(regionKeycap.Child is TextBlock text)text.Text=settings.Hotkeys.FirstOrDefault(k=>k.Command=="region")?.ToString() ?? "단축키 없음";
    }
    /// <summary>Up to four recent captures sit quietly under the hints; the toolbar menu lists all of them.</summary>
    internal void UpdateRecent(IReadOnlyList<BitmapSource> images)
    {
        recentImages=images;recent.Children.Clear();
        foreach(var image in images.Take(4)) {
            var bitmap=image;
            var b=new Button {Content=new Image {Source=bitmap,Height=56,MaxWidth=96,Stretch=Stretch.Uniform},ToolTip=$"{bitmap.PixelWidth} × {bitmap.PixelHeight} px · 클릭하여 편집",Margin=new Thickness(4),Padding=new Thickness(4),Style=(Style)Application.Current.FindResource("QuietButton")};
            System.Windows.Automation.AutomationProperties.SetName(b,"캡처 편집");b.Click+=(_,_)=>edit(bitmap);recent.Children.Add(b);
        }
    }
}
