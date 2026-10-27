using System.IO;
using System.Diagnostics;
using Capture.Windows.Updates;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Capture.Windows.Platform;
using Capture.Windows.UI;
using Microsoft.Win32;

namespace Capture.Windows;
internal sealed class SettingsWindow : Window
{
    private readonly List<HotkeyBinding> keys;
    private readonly AppSettings original;
    private readonly Action<AppSettings> apply;
    private readonly CheckBox preview,copy,save,outside,autoUpdate,adjust,loupe,joinLines,cursor,keepHistory,deepLinks,launchAtLogin;
    private readonly ComboBox escape,windowStyle;
    private readonly UpdateService updates;
    private readonly Action? restartUpdate;
    private readonly TextBox folder;
    private readonly ContentControl page=new();
    private readonly TextBlock error=Ui.Label("",11,Ui.Brush("WarningBrush"));
    private readonly Dictionary<string,Button> navigation=[];
    private readonly Dictionary<string,UIElement> pages=[];
    internal SettingsWindow(AppSettings settings,Action<AppSettings> applySettings,UpdateService? updateService=null,Action? restartUpdate=null)
    {
        updates=updateService ?? new UpdateService(new UninstalledBackend());this.restartUpdate=restartUpdate;
        original=settings;apply=applySettings;keys=settings.Hotkeys.ToList();
        Title="Snaplet 설정";Width=800;Height=660;MinWidth=720;MinHeight=540;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        preview=Toggle("캡처 미리보기",settings.ShowPreview);copy=Toggle("자동 복사",settings.CopyAfterCapture);save=Toggle("자동 저장",settings.SaveAfterCapture);outside=Toggle("바깥 클릭으로 미리보기 닫기",settings.ClosePreviewOutside);
        launchAtLogin=Toggle("로그인 시 자동 실행",settings.LaunchAtLogin);
        autoUpdate=Toggle("업데이트 자동 확인 및 다운로드",settings.AutoUpdate);
        adjust=Toggle("영역을 놓은 뒤 조정",settings.AdjustSelectionBeforeCapture);loupe=Toggle("픽셀 확대경 표시",settings.ShowLoupe);joinLines=Toggle("인식한 줄바꿈 합치기",settings.OcrJoinLines);cursor=Toggle("마우스 포인터 포함",settings.IncludeCursor);keepHistory=Toggle("최근 캡처 디스크 보관",settings.KeepHistoryOnDisk);deepLinks=Toggle("snaplet:// 링크 허용",settings.EnableDeepLinks);
        windowStyle=new ComboBox{MinWidth=150,VerticalAlignment=VerticalAlignment.Center,ItemsSource=new[]{"그대로","투명 여백 + 그림자","그라데이션 배경"},SelectedIndex=(int)settings.WindowStyle};AutomationProperties.SetName(windowStyle,"창 캡처 모양");
        escape=new ComboBox{MinWidth=150,VerticalAlignment=VerticalAlignment.Center,ItemsSource=new[]{"아무것도 안 함","이미지 복사","이미지 저장"},SelectedIndex=(int)settings.EscapeAction};AutomationProperties.SetName(escape,"Esc로 편집창을 닫을 때");
        folder=new TextBox{Text=settings.SaveFolder,MinWidth=100,TextWrapping=TextWrapping.Wrap};AutomationProperties.SetName(folder,"저장 폴더 경로");
        var root=new DockPanel{Background=Ui.Page};Content=root;
        var footer=new DockPanel{Margin=new Thickness(22,14,22,18)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(buttons,Dock.Right);footer.Children.Add(buttons);
        var cancel=Ui.Button("취소",Close,style:"QuietButton");cancel.Margin=new Thickness(0,0,8,0);buttons.Children.Add(cancel);
        var submit=Ui.Button("변경 사항 저장",Save,style:"PrimaryButton");AutomationProperties.SetAutomationId(submit,"settings-save");buttons.Children.Add(submit);footer.Children.Add(error);
        var columns=new Grid();columns.ColumnDefinitions.Add(new(){Width=new GridLength(166)});columns.ColumnDefinitions.Add(new());root.Children.Add(columns);
        var sidebar=new StackPanel{Margin=new Thickness(16,24,12,0)};columns.Children.Add(sidebar);
        var brand=Ui.Label("설정",21,weight:FontWeights.SemiBold);brand.Margin=new Thickness(10,0,0,24);sidebar.Children.Add(brand);
        void Nav(string id,string name,string glyph){var b=Ui.Button(name,()=>SelectPage(id),glyph,"QuietButton");b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Margin=new Thickness(0,0,0,5);AutomationProperties.SetAutomationId(b,"settings-"+id);sidebar.Children.Add(b);navigation[id]=b;}
        Nav("capture","캡처","region");Nav("shortcuts","단축키","clipboard");Nav("storage","저장","folder");Nav("updates","업데이트","spark");Nav("about","정보","spark");
        var scroll=new ScrollViewer{Content=page,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Padding=new Thickness(22,24,24,8)};Grid.SetColumn(scroll,1);columns.Children.Add(scroll);
        pages["capture"]=CapturePage();pages["shortcuts"]=ShortcutsPage();pages["storage"]=StoragePage();pages["updates"]=UpdatesPage();pages["about"]=AboutPage();
        SelectPage("capture");
    }
    private static CheckBox Toggle(string name,bool value){var c=new CheckBox{IsChecked=value,Style=(Style)Application.Current.FindResource("Switch"),VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(c,name);c.ToolTip=name;return c;}
    private static StackPanel Section(string title,string subtitle){var p=new StackPanel();p.Children.Add(Ui.Header(title,subtitle));return p;}
    private static Border Row(string title,string detail,UIElement control)
    {
        var grid=new DockPanel();DockPanel.SetDock(control,Dock.Right);grid.Children.Add(control);
        var text=new StackPanel{Margin=new Thickness(0,0,18,0)};text.Children.Add(Ui.Label(title,13,weight:FontWeights.SemiBold));text.Children.Add(new TextBlock{Text=detail,Foreground=Ui.Muted,FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,0)});grid.Children.Add(text);
        var card=Ui.CardOf(grid,16);card.Margin=new Thickness(0,0,0,9);return card;
    }
    private UIElement CapturePage()
    {
        var p=Section("캡처 후 동작","필요한 작업만 켜 두세요. 각각 독립적으로 동작합니다.");
        p.Children.Add(Row("작은 미리보기 표시","캡처 직후 편집·복사·저장 버튼을 보여 줍니다.",preview));
        p.Children.Add(Row("클립보드에 자동 복사","캡처가 끝나면 다른 앱에 바로 붙여넣을 수 있습니다.",copy));
        p.Children.Add(Row("파일로 자동 저장","캡처 원본을 저장 페이지에서 정한 폴더에 남깁니다.",save));
        p.Children.Add(Row("바깥 클릭으로 닫기","고정하지 않은 미리보기만 닫습니다.",outside));
        void Update()=>outside.IsEnabled=preview.IsChecked==true;preview.Click+=(_,_)=>Update();Update();
        p.Children.Add(Ui.Label("고정한 이미지는 새 캡처 후에도 화면에 남습니다.",11,Ui.Muted));
        p.Children.Add(Row("마우스 포인터 포함","캡처를 시작한 순간의 포인터를 이미지에 넣습니다. 숨겨진 포인터는 넣지 않습니다.",cursor));
        p.Children.Add(Row("창 캡처 모양","활성 창·창 선택 캡처에 Windows 11 스타일 그림자나 배경을 더합니다. 편집창의 배경 꾸미기로 이후에도 바꿀 수 있습니다.",windowStyle));
        var selection=Ui.Header("영역 선택","정밀하게 고를 때 쓰는 도구입니다.");selection.Margin=new Thickness(0,22,0,0);p.Children.Add(selection);
        p.Children.Add(Row("픽셀 확대경","선택 화면에서 포인터 주변 픽셀·좌표·색을 크게 보여 줍니다. Tab은 색상 코드를 복사합니다.",loupe));
        p.Children.Add(Row("놓은 뒤 조정하고 Enter로 캡처","끄면 마우스를 놓는 즉시 캡처합니다. 켜면 조절점·방향키·A(내용에 맞춤)로 다듬을 수 있습니다. 끈 상태에서도 Ctrl을 누른 채 놓으면 조정 단계로 들어갑니다.",adjust));
        var other=Ui.Header("편집과 텍스트","");other.Margin=new Thickness(0,22,0,0);p.Children.Add(other);
        p.Children.Add(Row("Esc로 편집창을 닫을 때","닫으면서 결과를 복사하거나 저장 폴더에 저장할 수 있습니다.",escape));
        p.Children.Add(Row("텍스트 인식 줄바꿈 합치기","바로 인식·텍스트 추출 결과를 한 줄로 이어 붙여 복사합니다.",joinLines));
        return p;
    }
    private UIElement ShortcutsPage()
    {
        var p=Section("단축키","조합을 클릭하고 새 키를 누르세요. Esc는 취소, Backspace는 해제입니다.");
        var names=AppSettings.Commands.ToDictionary(c=>c.Command,c=>c.Name);
        for(int index=0;index<keys.Count;index++) {
            var i=index;var b=Ui.Button(keys[i].ToString(),()=>{});b.MinWidth=160;b.FontSize=12;b.Padding=new Thickness(10,7,10,7);AutomationProperties.SetName(b,names[keys[i].Command]+" 단축키");
            var row=new DockPanel();DockPanel.SetDock(b,Dock.Right);row.Children.Add(b);row.Children.Add(new TextBlock{Text=names[keys[i].Command],VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0)});
            var card=Ui.CardOf(row,12);card.Margin=new Thickness(0,0,0,8);p.Children.Add(card);bool recording=false;
            b.Click+=(_,_)=>{recording=true;b.Content="새 조합을 누르세요";b.BorderBrush=Ui.Accent;b.Focus();};
            b.PreviewKeyDown+=(_,e)=>{
                if(!recording)return;e.Handled=true;var key=e.Key==Key.System?e.SystemKey:e.Key;
                if(key==Key.Escape){Reset();return;}
                if(key is Key.Back or Key.Delete && Keyboard.Modifiers==ModifierKeys.None){keys[i]=keys[i] with{Key=Key.None,Modifiers=ModifierKeys.None};Reset();return;}
                if(key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)return;
                if(Keyboard.Modifiers==ModifierKeys.None){b.Content="Ctrl / Alt / Shift와 함께";return;}
                keys[i]=keys[i] with{Key=key,Modifiers=Keyboard.Modifiers};Reset();
            };
            b.LostKeyboardFocus+=(_,_)=>Reset();
            void Reset(){recording=false;b.Content=keys[i].ToString();b.BorderBrush=Ui.Line;}
        }
        p.Children.Add(Ui.Label("다른 앱과 겹치는 조합은 저장할 때 알려 드립니다.",11,Ui.Muted));
        var automation=Ui.Header("자동화","런처·스크립트에서 같은 작업을 실행합니다.");automation.Margin=new Thickness(0,22,0,0);p.Children.Add(automation);
        p.Children.Add(Row("로그인 시 자동 실행","Windows에 로그인하면 Snaplet이 창 없이 트레이에서 시작해 단축키를 바로 받습니다.",launchAtLogin));
        p.Children.Add(Row("snaplet:// 링크 허용","켜면 snaplet://region 같은 링크로 캡처를 시작할 수 있습니다. 명령줄 Snaplet.exe --command region 은 항상 사용할 수 있습니다.",deepLinks));
        var list=Ui.Label("명령: "+string.Join(", ",AppSettings.Commands.Select(c=>c.Command)),11,Ui.Muted);list.Margin=new Thickness(0,4,0,0);p.Children.Add(list);
        return p;
    }
    private UIElement StoragePage()
    {
        var p=Section("저장","자동 저장 이미지의 위치를 정합니다.");
        var card=new StackPanel();card.Children.Add(Ui.Label("캡처 저장 폴더",13,weight:FontWeights.SemiBold));folder.Margin=new Thickness(0,12,0,10);card.Children.Add(folder);
        var browse=Ui.Button("폴더 선택",()=>{var d=new OpenFolderDialog();if(d.ShowDialog(this)==true)folder.Text=d.FolderName;},"folder");browse.HorizontalAlignment=HorizontalAlignment.Left;card.Children.Add(browse);p.Children.Add(Ui.CardOf(card));
        var history=Row("최근 캡처를 디스크에 보관","최근 12개 원본을 이 PC의 앱 데이터 폴더에 PNG로 남겨 다시 시작·업데이트 후에도 열 수 있습니다. 끄면 보관한 파일을 지웁니다. 편집 객체는 포함하지 않습니다.",keepHistory);history.Margin=new Thickness(0,16,0,0);p.Children.Add(history);
        var text=Ui.Label("자동 저장을 켠 경우 PNG 원본이 이 폴더에 저장됩니다. 편집창의 다른 이름으로 저장에서는 위치와 형식을 따로 고를 수 있습니다.",12,Ui.Muted);text.Margin=new Thickness(0,16,0,0);p.Children.Add(text);return p;
    }
    private UIElement UpdatesPage()
    {
        var p=Section("업데이트","새 버전은 미리 받아 두고, 준비됐을 때 다시 시작하세요.");
        p.Children.Add(Row("자동 확인·다운로드","앱 시작 후와 6시간마다 확인합니다. 자동으로 앱을 종료하지 않습니다.",autoUpdate));
        var card=new StackPanel();var version=Ui.Label("",13,weight:FontWeights.SemiBold);
        var message=Ui.Label("",12,Ui.Muted);message.Margin=new Thickness(0,12,0,12);card.Children.Add(version);card.Children.Add(message);
        var progress=new ProgressBar {Minimum=0,Maximum=100,Height=5,Margin=new Thickness(0,0,0,16)};card.Children.Add(progress);
        var actions=new WrapPanel();var check=Ui.Button("업데이트 확인",async()=>await updates.CheckAsync());
        var install=Ui.Button("",async()=>{if(updates.Snapshot.Stage==UpdateStage.Ready)restartUpdate?.Invoke();else await updates.DownloadAsync();},style:"PrimaryButton");
        AutomationProperties.SetAutomationId(check,"updates-check");AutomationProperties.SetAutomationId(install,"updates-install");
        check.Margin=new Thickness(0,0,8,8);install.Margin=new Thickness(0,0,0,8);actions.Children.Add(check);actions.Children.Add(install);card.Children.Add(actions);p.Children.Add(Ui.CardOf(card));
        var download=Ui.Button("다운로드 및 변경 사항",()=>{try{Process.Start(new ProcessStartInfo(UpdateDistribution.Releases){UseShellExecute=true});}catch{error.Text="브라우저를 열지 못했습니다.";}},style:"QuietButton");
        download.HorizontalAlignment=HorizontalAlignment.Left;download.Margin=new Thickness(0,12,0,10);p.Children.Add(download);
        p.Children.Add(Ui.Label("현재 개발 버전 채널을 사용합니다. 업데이트를 적용하면 최근 캡처와 저장하지 않은 편집은 사라지므로 먼저 저장해 주세요.",11,Ui.Muted));
        void Refresh()
        {
            if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(Refresh);return;}
            var state=updates.Snapshot;version.Text="현재 버전 "+state.CurrentVersion;
            message.Text=state.Stage switch {
                UpdateStage.NotInstalled=>"ZIP 실행 버전입니다. 자동 업데이트를 사용하려면 다운로드 페이지에서 Setup.exe를 한 번 설치하세요. 기존 설정은 그대로 사용합니다.",
                UpdateStage.Idle=>"업데이트를 확인할 수 있습니다.",UpdateStage.Checking=>"새 버전을 확인하고 있습니다…",
                UpdateStage.UpToDate=>"최신 버전입니다.",UpdateStage.Available=>$"새 버전 {state.TargetVersion}을 받을 수 있습니다.",
                UpdateStage.Downloading=>$"{state.TargetVersion} 다운로드 중 · {state.Progress}%",
                UpdateStage.Ready=>$"{state.TargetVersion} 업데이트가 준비됐습니다. 원하는 때 다시 시작하세요.",
                UpdateStage.Applying=>"업데이트를 적용하고 다시 시작합니다…",_=>state.Message};
            check.IsEnabled=state.Stage is UpdateStage.Idle or UpdateStage.UpToDate or UpdateStage.Available or UpdateStage.Failed;
            progress.Visibility=state.Stage==UpdateStage.Downloading?Visibility.Visible:Visibility.Collapsed;progress.Value=state.Progress;
            install.Visibility=state.Stage is UpdateStage.Available or UpdateStage.Ready?Visibility.Visible:Visibility.Collapsed;
            install.Content=state.Stage==UpdateStage.Ready?"업데이트하고 다시 시작":"업데이트 다운로드";
            install.IsEnabled=state.Stage!=UpdateStage.Ready || restartUpdate!=null;
        }
        updates.Changed+=Refresh;Closed+=(_,_)=>updates.Changed-=Refresh;Refresh();return p;
    }
    private static UIElement AboutPage()
    {
        var p=Section("Snaplet","단축키로 캡처하고, 필요한 내용만 빠르게 전달하세요.");
        var notes=new StackPanel();notes.Children.Add(Ui.Label("Windows 11 · x64",15,weight:FontWeights.SemiBold));
        foreach(var line in new[]{"버전 "+typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3),"캡처·한국어/영어 OCR은 PC 안에서 처리합니다.","최근 이미지는 메모리에 보관하며 앱 종료 시 사라집니다.","현재는 SDR 캡처를 지원합니다. HDR 색 보존·자동 스크롤은 아직 지원하지 않습니다."}) {var t=Ui.Label(line,12,Ui.Muted);t.Margin=new Thickness(0,12,0,0);notes.Children.Add(t);}
        p.Children.Add(Ui.CardOf(notes));return p;
    }
    internal void SelectPage(string id)
    {
        page.Content=pages[id];foreach(var entry in navigation)entry.Value.Background=entry.Key==id?Ui.Raised:System.Windows.Media.Brushes.Transparent;
    }
    private void Save()
    {
        try {
            if(!Path.IsPathFullyQualified(folder.Text))throw new InvalidOperationException("저장 폴더를 전체 경로로 입력해 주세요.");
            if(keys.Where(k=>k.Assigned).GroupBy(k=>(k.Key,k.Modifiers)).Any(g=>g.Count()>1))throw new InvalidOperationException("같은 단축키를 두 작업에 지정할 수 없습니다.");
            apply(original with{AutoUpdate=autoUpdate.IsChecked==true,ShowPreview=preview.IsChecked==true,CopyAfterCapture=copy.IsChecked==true,SaveAfterCapture=save.IsChecked==true,ClosePreviewOutside=outside.IsChecked==true,SaveFolder=folder.Text,LaunchAtLogin=launchAtLogin.IsChecked==true,Hotkeys=keys.ToList(),AdjustSelectionBeforeCapture=adjust.IsChecked==true,ShowLoupe=loupe.IsChecked==true,OcrJoinLines=joinLines.IsChecked==true,EscapeAction=(EscapeAction)Math.Max(0,escape.SelectedIndex),IncludeCursor=cursor.IsChecked==true,KeepHistoryOnDisk=keepHistory.IsChecked==true,EnableDeepLinks=deepLinks.IsChecked==true,WindowStyle=(WindowCaptureStyle)Math.Max(0,windowStyle.SelectedIndex)});Close();
        }catch(Exception ex){error.Text=ex.Message;}
    }
}
