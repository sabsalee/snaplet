using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Windows.UI;

namespace Capture.Windows;

internal sealed class HomeWindow : Window
{
    private readonly Action<string> command;
    private readonly StackPanel recent=new();
    private readonly Dictionary<string,Border> keycaps=[];
    private readonly Action<BitmapSource> edit;
    internal HomeWindow(AppSettings settings,Action<string> capture,Action open,Action clipboard,Action demo,Action preferences,Action<BitmapSource> editImage,TextBlock status)
    {
        command=capture;edit=editImage;Title="Snaplet";Width=820;Height=650;MinWidth=660;MinHeight=520;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var root=new DockPanel{Background=Ui.Page};Content=root;
        var footer=new DockPanel{Margin=new Thickness(28,12,28,18)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var version=Ui.Label("v"+typeof(HomeWindow).Assembly.GetName().Version?.ToString(3),11,Ui.Muted);DockPanel.SetDock(version,Dock.Right);footer.Children.Add(version);
        footer.Children.Add(Ui.Label("로컬 캡처 · 닫아도 트레이에서 실행됩니다",11,Ui.Muted));
        var content=new StackPanel{Margin=new Thickness(28,24,28,0)};root.Children.Add(new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        var brand=new DockPanel{Margin=new Thickness(0,0,0,26)};
        var settingsButton=Ui.IconButton("settings","설정 열기",preferences);DockPanel.SetDock(settingsButton,Dock.Right);brand.Children.Add(settingsButton);
        var logo=new Border{Background=Ui.Brush("AccentFillBrush"),CornerRadius=new CornerRadius(8),Width=34,Height=34,Child=Ui.Icon("region",21),Margin=new Thickness(0,0,11,0)};DockPanel.SetDock(logo,Dock.Left);brand.Children.Add(logo);
        brand.Children.Add(new TextBlock{Text="Snaplet",FontSize=20,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});content.Children.Add(brand);
        content.Children.Add(Ui.Label("캡처하고, 바로 전달하세요.",28,weight:FontWeights.SemiBold));
        content.Children.Add(new TextBlock{Text="단축키로 화면을 담고, 작은 미리보기에서 편집·복사·저장까지.",Foreground=Ui.Muted,Margin=new Thickness(0,9,0,22),TextWrapping=TextWrapping.Wrap});
        var columns=new Grid();columns.ColumnDefinitions.Add(new(){Width=new GridLength(1.4,GridUnitType.Star)});columns.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});content.Children.Add(columns);
        var capturePanel=new StackPanel{Margin=new Thickness(0,0,16,0)};columns.Children.Add(capturePanel);
        capturePanel.Children.Add(ActionCard("영역 캡처","필요한 부분만 드래그하세요","region","region",true));
        capturePanel.Children.Add(ActionCard("활성 창","현재 사용 중인 창을 담습니다","window","window"));
        capturePanel.Children.Add(ActionCard("현재 화면","마우스가 있는 모니터 전체","display","display"));
        var other=new StackPanel();Grid.SetColumn(other,1);columns.Children.Add(other);
        other.Children.Add(Ui.Label("이미지로 시작",11,Ui.Muted,FontWeights.SemiBold));
        void Link(string text,string glyph,Action action){var b=Ui.Button(text,action,glyph,"QuietButton");b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Margin=new Thickness(0,6,0,0);other.Children.Add(b);}
        Link("이미지 열기","folder",open);Link("클립보드에서 가져오기","clipboard",clipboard);Link("편집 도구 체험","spark",demo);
        var extra=new WrapPanel{Margin=new Thickness(0,14,0,0)};other.Children.Add(extra);
        var timer=Ui.Button("3초 타이머",()=>command("delayed"),"clock","QuietButton");timer.Margin=new Thickness(0,0,6,0);extra.Children.Add(timer);
        extra.Children.Add(Ui.Button("긴 화면",()=>command("scroll"),"scroll","QuietButton"));
        var recentHeader=new DockPanel{Margin=new Thickness(0,23,0,10)};content.Children.Add(recentHeader);
        var last=Ui.Button("최근 편집 열기",()=>command("last"),style:"QuietButton");last.FontSize=11;last.Padding=new Thickness(8,2,8,2);last.MinHeight=24;DockPanel.SetDock(last,Dock.Right);recentHeader.Children.Add(last);recentHeader.Children.Add(Ui.Label("최근 캡처",13,weight:FontWeights.SemiBold));
        content.Children.Add(recent);status.Foreground=Ui.Muted;status.FontSize=11;status.Margin=new Thickness(0,12,0,0);content.Children.Add(status);
        UpdateSettings(settings);UpdateRecent([]);
    }
    private Button ActionCard(string title,string detail,string glyph,string key,bool primary=false)
    {
        var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new GridLength(36)});row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var icon=Ui.Icon(glyph,23);icon.HorizontalAlignment=HorizontalAlignment.Left;icon.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(icon);
        var text=new StackPanel{Margin=new Thickness(3,0,8,0)};Grid.SetColumn(text,1);row.Children.Add(text);text.Children.Add(Ui.Label(title,14,weight:FontWeights.SemiBold));text.Children.Add(new TextBlock{Text=detail,FontSize=11,Foreground=primary?Ui.Text:Ui.Muted,Margin=new Thickness(0,4,0,0)});
        var keycap=Ui.Keycap("");keycaps[key]=keycap;Grid.SetColumn(keycap,2);row.Children.Add(keycap);
        var b=Ui.Button(title,()=>command(key),style:primary?"PrimaryButton":"BaseButton");b.Content=row;b.HorizontalContentAlignment=HorizontalAlignment.Stretch;b.Margin=new Thickness(0,0,10,9);b.Padding=new Thickness(14);return b;
    }
    internal void UpdateSettings(AppSettings settings)
    {
        foreach(var pair in keycaps)if(pair.Value.Child is TextBlock text)text.Text=settings.Hotkeys.FirstOrDefault(k=>k.Command==pair.Key)?.ToString()??"미지정";
    }
    internal void UpdateRecent(IReadOnlyList<BitmapSource> images)
    {
        recent.Children.Clear();
        if(images.Count==0){var empty=Ui.Label("첫 캡처를 시작해 보세요. 최근 이미지는 앱이 실행되는 동안 여기에 남습니다.",12,Ui.Muted);empty.Margin=new Thickness(0,1,0,1);recent.Children.Add(Ui.CardOf(empty,14));return;}
        var grid=new UniformGrid{Columns=4};recent.Children.Add(grid);
        foreach(var image in images.Take(4)) {
            var bitmap=image;var box=new StackPanel();box.Children.Add(new Image{Source=bitmap,Height=54,Stretch=Stretch.Uniform});box.Children.Add(new TextBlock{Text=$"{bitmap.PixelWidth} × {bitmap.PixelHeight}",FontSize=10,Foreground=Ui.Muted,TextAlignment=TextAlignment.Center,Margin=new Thickness(0,5,0,0)});
            var b=Ui.Button("캡처 편집",()=>edit(bitmap));b.Content=box;b.Margin=new Thickness(0,0,8,0);b.Padding=new Thickness(8);grid.Children.Add(b);
        }
    }
}
