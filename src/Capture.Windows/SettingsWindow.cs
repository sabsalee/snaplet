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
    private readonly CheckBox copy,save,outside,autoUpdate,adjust,modeBar,liveSelection,loupe,joinLines,cursor,keepHistory,deepLinks,launchAtLogin;
    private readonly ComboBox escape,windowStyle,afterCapture;
    private readonly UpdateService updates;
    private readonly Action? restartUpdate;
    private readonly TextBox folder,fileName;
    private readonly ComboBox textFont;
    private readonly ContentControl page=new();
    private readonly TextBlock error=Ui.Label("",11,Ui.Brush("WarningBrush"));
    private readonly Dictionary<string,Button> navigation=[];
    private readonly Dictionary<string,UIElement> pages=[];
    internal SettingsWindow(AppSettings settings,Action<AppSettings> applySettings,UpdateService? updateService=null,Action? restartUpdate=null)
    {
        Editor.EditorTheme.FollowSystem(this);
        updates=updateService ?? new UpdateService(new UninstalledBackend());this.restartUpdate=restartUpdate;
        original=settings;apply=applySettings;keys=settings.Hotkeys.ToList();
        Title="Snaplet 설정";Width=800;Height=660;MinWidth=720;MinHeight=540;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        afterCapture=new ComboBox{MinWidth=150,VerticalAlignment=VerticalAlignment.Center,ItemsSource=new[]{"커서 옆 작은 팝업","바로 편집창 열기","아무것도 안 함"},SelectedIndex=(int)settings.AfterCapture};AutomationProperties.SetName(afterCapture,"캡처 후 표시");
        copy=Toggle("자동 복사",settings.CopyAfterCapture);save=Toggle("자동 저장",settings.SaveAfterCapture);outside=Toggle("바깥 클릭으로 미리보기 닫기",settings.ClosePreviewOutside);
        launchAtLogin=Toggle("로그인 시 자동 실행",settings.LaunchAtLogin);
        autoUpdate=Toggle("업데이트 자동 확인 및 다운로드",settings.AutoUpdate);
        adjust=Toggle("영역을 놓은 뒤 조정",settings.AdjustSelectionBeforeCapture);modeBar=Toggle("올인원 캡처 막대",settings.ShowCaptureModeBar);liveSelection=Toggle("실시간 선택",settings.LiveSelection);loupe=Toggle("픽셀 확대경 표시",settings.ShowLoupe);joinLines=Toggle("인식한 줄바꿈 합치기",settings.OcrJoinLines);cursor=Toggle("마우스 포인터 포함",settings.IncludeCursor);keepHistory=Toggle("최근 캡처 디스크 보관",settings.KeepHistoryOnDisk);deepLinks=Toggle("snaplet:// 링크 허용",settings.EnableDeepLinks);
        windowStyle=new ComboBox{MinWidth=150,VerticalAlignment=VerticalAlignment.Center,ItemsSource=new[]{"그대로","투명 여백 + 그림자","그라데이션 배경"},SelectedIndex=(int)settings.WindowStyle};AutomationProperties.SetName(windowStyle,"창 캡처 모양");
        escape=new ComboBox{MinWidth=150,VerticalAlignment=VerticalAlignment.Center,ItemsSource=new[]{"아무것도 안 함","이미지 복사","이미지 저장"},SelectedIndex=(int)settings.EscapeAction};AutomationProperties.SetName(escape,"Esc로 편집창을 닫을 때");
        folder=new TextBox{Text=settings.SaveFolder,MinWidth=100,TextWrapping=TextWrapping.Wrap};AutomationProperties.SetName(folder,"저장 폴더 경로");
        textFont=new ComboBox{MinWidth=190,VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(textFont,"기본 글꼴");
        textFont.Items.Add(Capture.Windows.Editor.TextFonts.Preferred);foreach(var name in Capture.Windows.Editor.TextFonts.Installed.Where(n=>!n.Equals(Capture.Windows.Editor.TextFonts.Preferred,StringComparison.OrdinalIgnoreCase)))textFont.Items.Add(name);
        textFont.SelectedItem=string.IsNullOrWhiteSpace(settings.DefaultTextFont)?Capture.Windows.Editor.TextFonts.Preferred:settings.DefaultTextFont;if(textFont.SelectedIndex<0)textFont.SelectedIndex=0;
        fileName=new TextBox{Text=settings.FileNameTemplate,MinWidth=230,VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(fileName,"저장 파일 이름 규칙");
        var root=new DockPanel{Background=Ui.Page};Content=root;
        var footer=new DockPanel{Margin=new Thickness(22,14,22,18)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(buttons,Dock.Right);footer.Children.Add(buttons);
        var cancel=Ui.Button("취소",Close,style:"QuietButton");cancel.Margin=new Thickness(0,0,8,0);buttons.Children.Add(cancel);
        var submit=Ui.Button("변경 사항 저장",Save,style:"PrimaryButton");AutomationProperties.SetAutomationId(submit,"settings-save");buttons.Children.Add(submit);footer.Children.Add(error);
        var columns=new Grid();columns.ColumnDefinitions.Add(new(){Width=new GridLength(166)});columns.ColumnDefinitions.Add(new());root.Children.Add(columns);
        var sidebar=new StackPanel{Margin=new Thickness(16,24,12,0)};columns.Children.Add(sidebar);
        var brand=Ui.Label("설정",20,weight:FontWeights.SemiBold);brand.Margin=new Thickness(12,0,0,18);sidebar.Children.Add(brand);
        void Nav(string id,string name,string glyph){var b=Ui.Button(name,()=>SelectPage(id),glyph,"QuietButton");b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Margin=new Thickness(0,0,0,5);AutomationProperties.SetAutomationId(b,"settings-"+id);sidebar.Children.Add(b);navigation[id]=b;}
        Nav("capture","캡처","region");Nav("shortcuts","단축키","keyboard");Nav("storage","저장","folder");Nav("updates","업데이트","sync");Nav("about","정보","info");
        var scroll=new ScrollViewer{Content=page,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Padding=new Thickness(22,24,24,8)};Grid.SetColumn(scroll,1);columns.Children.Add(scroll);
        pages["capture"]=CapturePage();pages["shortcuts"]=ShortcutsPage();pages["storage"]=StoragePage();pages["updates"]=UpdatesPage();pages["about"]=AboutPage();
        SelectPage("capture");
    }
    private static CheckBox Toggle(string name,bool value){var c=new CheckBox{IsChecked=value,Style=(Style)Application.Current.FindResource("Switch"),VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(c,name);c.ToolTip=name;return c;}
    private static StackPanel Section(string title,string subtitle=""){var p=new StackPanel{Margin=new Thickness(0,0,0,12)};p.Children.Add(Ui.Header(title,subtitle));return p;}
    /// <summary>One rounded list per group with hairlines between rows, instead of a card around every setting.</summary>
    private static Border Group(StackPanel page,string? label,params FrameworkElement[] rows)
    {
        if(label!=null){var caption=Ui.Label(label,12,Ui.Muted,FontWeights.SemiBold);caption.Margin=new Thickness(4,20,0,8);page.Children.Add(caption);}
        var list=new StackPanel();
        for(int i=0;i<rows.Length;i++) {
            if(i>0)list.Children.Add(new Border{Height=1,Background=Ui.Brush("DividerBrush"),Margin=new Thickness(16,0,0,0)});
            list.Children.Add(rows[i]);
        }
        var card=new Border{Child=list,Background=Ui.Card,BorderBrush=Ui.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8)};
        if(label==null)card.Margin=new Thickness(0,page.Children.Count>1?12:0,0,0);
        page.Children.Add(card);return card;
    }
    private static FrameworkElement Row(string title,string? detail,UIElement control)
    {
        var row=new DockPanel{Margin=new Thickness(16,10,14,10),MinHeight=30};DockPanel.SetDock(control,Dock.Right);row.Children.Add(control);
        var text=new StackPanel{Margin=new Thickness(0,0,20,0),VerticalAlignment=VerticalAlignment.Center};text.Children.Add(Ui.Label(title,14));
        if(!string.IsNullOrEmpty(detail))text.Children.Add(new TextBlock{Text=detail,Foreground=Ui.Muted,FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,0)});
        row.Children.Add(text);return row;
    }
    private static TextBlock Note(string text){var t=Ui.Label(text,12,Ui.Muted);t.Margin=new Thickness(4,8,0,0);return t;}
    private UIElement CapturePage()
    {
        var p=Section("캡처");
        var fontNote=Capture.Windows.Editor.TextFonts.IsInstalled(Capture.Windows.Editor.TextFonts.Preferred)?null:"Noto Sans KR이 없으면 맑은 고딕으로 표시합니다.";
        var outsideRow=Row("바깥을 클릭하면 팝업 닫기","고정한 이미지는 남습니다.",outside);
        Group(p,"캡처한 뒤",Row("캡처 후 표시",null,afterCapture),Row("클립보드에 복사",null,copy),Row("파일로 저장","저장 탭에서 정한 폴더에 원본을 남깁니다.",save),outsideRow);
        void Update()=>outside.IsEnabled=afterCapture.SelectedIndex==(int)AfterCaptureAction.Popup;afterCapture.SelectionChanged+=(_,_)=>Update();Update();
        Group(p,"캡처 내용",Row("마우스 포인터 포함",null,cursor),Row("창 캡처 모양","편집창에서 열면 바꾸거나 끌 수 있습니다.",windowStyle));
        Group(p,"영역 선택",Row("픽셀 확대경","Tab으로 색상 코드를 복사합니다.",loupe),Row("올인원 캡처 막대","선택 화면 위에서 캡처 모드와 비율을 고릅니다.",modeBar),
            Row("실시간 선택","화면을 멈추지 않고 고릅니다. 선택 화면에서 F로 전환합니다.",liveSelection),Row("놓은 뒤 조정","조절점과 방향키로 다듬고 Enter로 캡처합니다. 꺼져 있어도 Ctrl을 누른 채 놓으면 조정합니다.",adjust));
        Group(p,"편집과 텍스트",Row("기본 글꼴",fontNote,textFont),Row("Esc로 편집창을 닫을 때",null,escape),Row("인식한 텍스트의 줄바꿈 합치기",null,joinLines));
        return p;
    }
    private UIElement ShortcutsPage()
    {
        var p=Section("단축키","조합을 클릭하고 새 키를 누르세요. Esc는 취소, Backspace는 해제입니다.");
        var names=AppSettings.Commands.ToDictionary(c=>c.Command,c=>c.Name);
        var rows=new List<FrameworkElement>();
        for(int index=0;index<keys.Count;index++) {
            var i=index;var b=Ui.Button(keys[i].ToString(),()=>{});b.MinWidth=150;b.FontSize=12;b.MinHeight=28;b.Padding=new Thickness(10,4,10,4);AutomationProperties.SetName(b,names[keys[i].Command]+" 단축키");
            rows.Add(Row(names[keys[i].Command],null,b));bool recording=false;
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
            void Reset(){recording=false;b.Content=keys[i].ToString();b.ClearValue(BorderBrushProperty);}
        }
        Group(p,null,rows.ToArray());
        p.Children.Add(Note("다른 앱과 겹치는 조합은 저장할 때 알려 드립니다."));
        Group(p,"자동화",Row("로그인 시 자동 실행","창 없이 트레이에서 시작해 단축키를 바로 받습니다.",launchAtLogin),
            Row("snaplet:// 링크 허용","snaplet://region 같은 링크로 캡처를 시작합니다. 명령줄 Snaplet.exe --command region은 항상 쓸 수 있습니다.",deepLinks));
        p.Children.Add(Note("명령: "+string.Join(", ",AppSettings.Commands.Select(c=>c.Command))));
        return p;
    }
    private UIElement StoragePage()
    {
        var p=Section("저장");
        folder.TextWrapping=TextWrapping.NoWrap;folder.Width=250;folder.MinWidth=0;folder.VerticalAlignment=VerticalAlignment.Center;
        var browse=Ui.Button("선택…",()=>{var d=new OpenFolderDialog();if(d.ShowDialog(this)==true)folder.Text=d.FolderName;});browse.Margin=new Thickness(8,0,0,0);
        var picker=new StackPanel{Orientation=Orientation.Horizontal};picker.Children.Add(folder);picker.Children.Add(browse);
        fileName.Width=250;fileName.MinWidth=0;
        Group(p,null,Row("저장 폴더","자동 저장한 PNG가 여기에 남습니다.",picker),Row("파일 이름 규칙","%Y %m %d 날짜, %H %M %S 시각, %f 밀리초, %r 임의 문자 4개",fileName));
        Group(p,"캡처 기록",Row("디스크에 보관",$"최근 {HistoryStore.DiskLimit}개 원본을 이 PC에 남겨 다시 시작해도 열 수 있습니다. 끄면 보관한 파일을 지웁니다.",keepHistory));
        p.Children.Add(Note("편집창의 다른 이름으로 저장에서는 위치와 형식을 따로 고를 수 있습니다."));return p;
    }
    private UIElement UpdatesPage()
    {
        var p=Section("업데이트");
        Group(p,null,Row("자동 확인·다운로드","앱 시작 후와 6시간마다 확인합니다. 앱을 마음대로 종료하지 않습니다.",autoUpdate));
        var card=new StackPanel{Margin=new Thickness(16,14,16,14)};var version=Ui.Label("",14,weight:FontWeights.SemiBold);
        var message=Ui.Label("",12,Ui.Muted);message.Margin=new Thickness(0,4,0,12);card.Children.Add(version);card.Children.Add(message);
        var progress=new ProgressBar {Minimum=0,Maximum=100,Height=5,Margin=new Thickness(0,0,0,16)};card.Children.Add(progress);
        var actions=new WrapPanel();var check=Ui.Button("업데이트 확인",async()=>await updates.CheckAsync());
        var install=Ui.Button("",async()=>{if(updates.Snapshot.Stage==UpdateStage.Ready)restartUpdate?.Invoke();else await updates.DownloadAsync();},style:"PrimaryButton");
        AutomationProperties.SetAutomationId(check,"updates-check");AutomationProperties.SetAutomationId(install,"updates-install");
        check.Margin=new Thickness(0,0,8,0);actions.Children.Add(check);actions.Children.Add(install);card.Children.Add(actions);Group(p,null,card);
        var download=Ui.Button("다운로드 및 변경 사항",()=>{try{Process.Start(new ProcessStartInfo(UpdateDistribution.Releases){UseShellExecute=true});}catch{error.Text="브라우저를 열지 못했습니다.";}},style:"QuietButton");
        download.HorizontalAlignment=HorizontalAlignment.Left;download.Margin=new Thickness(0,10,0,0);p.Children.Add(download);
        p.Children.Add(Note("개발 버전 채널입니다. 업데이트하면 저장하지 않은 편집은 사라지므로 먼저 저장해 주세요."));
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
        var p=Section("정보");
        var head=new StackPanel{Margin=new Thickness(16,14,16,14)};head.Children.Add(Ui.Label("Snaplet",14,weight:FontWeights.SemiBold));
        var version=Ui.Label($"버전 {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)} · Windows 11 x64",12,Ui.Muted);version.Margin=new Thickness(0,2,0,0);head.Children.Add(version);
        Group(p,null,head);
        Group(p,null,Row("캡처와 텍스트 인식","모두 이 PC 안에서 처리합니다. 한국어와 영어를 읽습니다.",new FrameworkElement()),Row("화면 색","SDR로 캡처합니다. HDR 색은 아직 보존하지 않습니다.",new FrameworkElement()));
        return p;
    }
    internal void SelectPage(string id)
    {
        page.Content=pages[id];foreach(var entry in navigation)entry.Value.Background=entry.Key==id?Ui.Brush("SelectedToolBrush"):System.Windows.Media.Brushes.Transparent;
    }
    private void Save()
    {
        try {
            if(!Path.IsPathFullyQualified(folder.Text))throw new InvalidOperationException("저장 폴더를 전체 경로로 입력해 주세요.");
            if(keys.Where(k=>k.Assigned).GroupBy(k=>(k.Key,k.Modifiers)).Any(g=>g.Count()>1))throw new InvalidOperationException("같은 단축키를 두 작업에 지정할 수 없습니다.");
            apply(original with{AutoUpdate=autoUpdate.IsChecked==true,AfterCapture=(AfterCaptureAction)Math.Max(0,afterCapture.SelectedIndex),CopyAfterCapture=copy.IsChecked==true,SaveAfterCapture=save.IsChecked==true,ClosePreviewOutside=outside.IsChecked==true,SaveFolder=folder.Text,FileNameTemplate=fileName.Text,DefaultTextFont=(string?)textFont.SelectedItem??"",LaunchAtLogin=launchAtLogin.IsChecked==true,Hotkeys=keys.ToList(),AdjustSelectionBeforeCapture=adjust.IsChecked==true,ShowCaptureModeBar=modeBar.IsChecked==true,LiveSelection=liveSelection.IsChecked==true,ShowLoupe=loupe.IsChecked==true,OcrJoinLines=joinLines.IsChecked==true,EscapeAction=(EscapeAction)Math.Max(0,escape.SelectedIndex),IncludeCursor=cursor.IsChecked==true,KeepHistoryOnDisk=keepHistory.IsChecked==true,EnableDeepLinks=deepLinks.IsChecked==true,WindowStyle=(WindowCaptureStyle)Math.Max(0,windowStyle.SelectedIndex)});Close();
        }catch(Exception ex){error.Text=ex.Message;}
    }
}
