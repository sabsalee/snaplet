using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Capture.Windows.UI;

namespace Capture.Windows;
internal sealed class OcrWindow : Window
{
    private readonly TextBox box;
    private readonly TextBlock feedback=Ui.Label("",11,Ui.Muted);
    internal OcrWindow(string text)
    {
        Title="Snaplet · 텍스트 추출";Width=660;Height=510;MinWidth=480;MinHeight=350;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var root=new DockPanel();Content=new Border{Background=Ui.Page,Padding=new Thickness(24),Child=root};
        var header=Ui.Header("텍스트 추출","이미지에서 읽은 내용을 확인하고 필요한 부분을 수정하세요.");DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var footer=new DockPanel{Margin=new Thickness(0,14,0,0)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var copy=Ui.Button("텍스트 복사",Copy,"copy","PrimaryButton");DockPanel.SetDock(copy,Dock.Right);footer.Children.Add(copy);feedback.VerticalAlignment=VerticalAlignment.Center;footer.Children.Add(feedback);
        var tools=new DockPanel{Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(tools,Dock.Top);root.Children.Add(tools);
        var lines=Ui.Button("줄바꿈 합치기",()=>box!.Text=System.Text.RegularExpressions.Regex.Replace(box.Text,@"\s*\r?\n\s*"," "),style:"QuietButton");lines.FontSize=11;lines.MinHeight=26;lines.Padding=new Thickness(8,4,8,4);DockPanel.SetDock(lines,Dock.Right);tools.Children.Add(lines);
        tools.Children.Add(new TextBlock{Text="한국어·영어 · PC 안에서 처리",FontSize=11,Foreground=Ui.Muted,VerticalAlignment=VerticalAlignment.Center});
        box=new TextBox{Text=text,AcceptsReturn=true,AcceptsTab=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(16),FontSize=14};root.Children.Add(box);
        box.TextChanged+=(_,_)=>Update();Update();
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape)Close();};
        void Update(){copy.IsEnabled=box.Text.Length>0;feedback.Text=box.Text.Length==0?"인식된 텍스트가 없습니다.":$"{box.Text.Length:N0}자 · 복사할 내용을 수정할 수 있습니다.";}
    }
    private void Copy(){if(string.IsNullOrEmpty(box.Text)){feedback.Text="복사할 텍스트가 없습니다.";return;}try{Clipboard.SetText(box.Text);feedback.Text="클립보드에 복사했습니다.";}catch(ExternalException){feedback.Text="다른 앱이 클립보드를 사용 중입니다. 다시 시도해 주세요.";}}
}
