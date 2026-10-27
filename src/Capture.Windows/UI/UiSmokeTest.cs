using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capture.Windows.UI;

/// <summary>Rendered synthetic windows and routed commands; no physical input, desktop capture or clipboard writes.</summary>
internal static class UiSmokeTest
{
    internal static void Run(string directory)
    {
        var checks=new List<string>();void Check(bool value,string label){if(!value)throw new InvalidOperationException("UI check failed: "+label);checks.Add(label);}
        foreach(var name in FluentIcons.Paths.Keys) {
            var icon=Ui.Icon(name);Check(icon is Viewbox {Child:Canvas {Width:24,Height:24}},"Fluent icon retains consistent design grid: "+name);
        }
        var image=ImageActions.Demo();var settings=new AppSettings();var commands=new List<string>();
        var home=new HomeWindow(settings,commands.Add,()=>commands.Add("open"),()=>{},()=>{},()=>{},_=>{},new TextBlock());
        try {
            Shot(home,"home-empty");
            Check(Find<TextBlock>(home).Where(t=>t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).All(t=>t.Foreground is SolidColorBrush c && c.Color!=Colors.Black),"home text uses explicit readable theme foregrounds");
            Click(home,"영역 캡처");Check(commands.SequenceEqual(new[]{"region"}),"home region action invokes the expected command without taking a screenshot");
            home.UpdateRecent(new[]{image,image});Shot(home,"home-recent");
            home.Width=660;home.Height=540;Shot(home,"home-compact");
            Check(Find<Button>(home).Any(b=>b.IsVisible && AutomationProperties.GetName(b)=="설정 열기"),"compact home retains visible settings action");
        }finally{home.Close();}
        AppSettings? saved=null;
        var preferences=new SettingsWindow(settings,value=>saved=value);
        try {
            Shot(preferences,"settings-capture");
            foreach(var id in new[]{"shortcuts","storage","updates","about"}) {
                Find<Button>(preferences).Single(b=>AutomationProperties.GetAutomationId(b)=="settings-"+id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Shot(preferences,"settings-"+id);
            }
            preferences.SelectPage("storage");preferences.UpdateLayout();
            var folder=Find<TextBox>(preferences).Single(b=>AutomationProperties.GetName(b)=="저장 폴더 경로");folder.Text="relative-folder";
            Find<Button>(preferences).Single(b=>AutomationProperties.GetAutomationId(b)=="settings-save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(saved==null && preferences.IsVisible,"invalid folder leaves settings open and never calls persistence callback");
            Shot(preferences,"settings-validation");folder.Text=settings.SaveFolder;
            preferences.SelectPage("capture");preferences.UpdateLayout();
            var after=Find<ComboBox>(preferences).Single(c=>AutomationProperties.GetName(c)=="캡처 후 표시");after.SelectedIndex=(int)AfterCaptureAction.None;
            Check(!Find<CheckBox>(preferences).Single(c=>AutomationProperties.GetName(c)=="바깥 클릭으로 미리보기 닫기").IsEnabled,"the outside-click option is disabled unless the popup is chosen");
            Find<Button>(preferences).Single(b=>AutomationProperties.GetAutomationId(b)=="settings-save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(saved is {AfterCapture:AfterCaptureAction.None} && saved.Hotkeys.SequenceEqual(settings.Hotkeys),"settings applies changed capture preference and preserves key bindings");
        }finally{if(preferences.IsVisible)preferences.Close();}
        var preview=new PreviewWindow(image,settings,enableDesktopIntegration:false);
        try {
            Shot(preview,"preview");Click(preview,"이미지 고정");Check(preview.Pinned,"preview pin command switches to pinned presentation");
            Shot(preview,"pinned");preview.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=UIElement.MouseEnterEvent});Shot(preview,"pinned-tools");
            Click(preview,"고정 해제");preview.UpdateLayout();Check(!preview.Pinned,"unpin rebuilds compact preview without reparenting errors");
            bool edited=false;preview.EditRequested+=_=>edited=true;Click(preview,"편집");Check(edited && !preview.IsVisible,"preview edit dispatches image and dismisses unpinned window");
        }finally{if(preview.IsVisible)preview.Close();}
        var popup=new PreviewWindow(image,settings,enableDesktopIntegration:false,nearPointer:true);
        try {
            Shot(popup,"popup");
            Check(((FrameworkElement)popup.Content).ActualWidth<=220,"the after-capture popup stays small");
            Check(new[]{"편집","이미지 복사","이미지 저장","텍스트 추출","이미지 고정","미리보기 닫기"}.All(n=>Find<Button>(popup).Any(b=>b.IsVisible && AutomationProperties.GetName(b)==n)),"the popup offers edit, copy, save, text, pin and close");
            Click(popup,"이미지 고정");Check(popup.Pinned,"pinning from the popup switches to a pinned image");
            Click(popup,"고정 해제");popup.UpdateLayout();Check(!popup.Pinned && ((FrameworkElement)popup.Content).ActualWidth<=220,"unpinning returns to the small popup");
        }finally{if(popup.IsVisible)popup.Close();}
        Check(PreviewWindow.PopupPlacement(100,100,200,150,0,0,1920,1040)==(118,118),"the popup opens below and right of the pointer");
        Check(PreviewWindow.PopupPlacement(1900,1000,200,150,0,0,1920,1040)==(1682,832),"the popup flips left and up at the bottom-right corner");
        Check(PreviewWindow.PopupPlacement(-1500,500,200,150,-1920,0,0,1040)==(-1482,518),"placement works on a monitor left of the primary one");
        var ocr=new OcrWindow("화면에서 읽은 텍스트입니다.\n필요한 부분만 수정하고 복사하세요.\n\nSnaplet — local OCR");
        try {
            Shot(ocr,"ocr");Click(ocr,"줄바꿈 합치기");Check(!Find<TextBox>(ocr).Single().Text.Contains('\n'),"OCR line-break action edits synthetic text without touching clipboard");
            Find<TextBox>(ocr).Single().Clear();Check(!Find<Button>(ocr).Single(b=>AutomationProperties.GetName(b)=="텍스트 복사").IsEnabled,"empty OCR result cannot invoke clipboard copy");
        }finally{ocr.Close();}
        var scroll=new ManualScrollWindow(image,_=>Task.FromResult(image));
        try{Shot(scroll,"scroll");Check(Find<Button>(scroll).Count(b=>b.IsEnabled)>=3,"manual scrolling keeps add, finish and cancel actions available");}finally{scroll.Close();}
        var notice=new NoticeWindow("이미지를 저장하지 못했습니다","저장 폴더에 접근할 수 없습니다. 다른 폴더를 선택한 뒤 다시 저장해 주세요.");
        try{Shot(notice,"notice");}finally{notice.Close();}
        // Layout magnification is useful QA, but does not claim OS per-monitor DPI transition coverage.
        var scaled=new HomeWindow(settings,_=>{},()=>{},()=>{},()=>{},()=>{},_=>{},new TextBlock());
        try {scaled.Width=1120;scaled.Height=900;((FrameworkElement)scaled.Content).LayoutTransform=new ScaleTransform(1.5,1.5);Shot(scaled,"home-layout-150");Check(((FrameworkElement)scaled.Content).ActualWidth>=660,"150 percent layout scale retains supported content width");}finally{scaled.Close();}
        File.WriteAllText(Path.Combine(directory,"ui-smoke.json"),JsonSerializer.Serialize(new{component="application design and routed actions",passed=checks.Count,checks,limitations=new[]{"Routed WPF command checks are not physical mouse/keyboard or IME testing","Layout scaling is not an OS DPI transition test","Does not read the desktop or measure capture latency"}},new JsonSerializerOptions{WriteIndented=true}));
        void Shot(Window window,string name)
        {
            if(!window.IsVisible)window.Show();window.UpdateLayout();
            var content=(FrameworkElement)window.Content;
            Check(content.ActualWidth>100 && content.ActualHeight>60,name+" has nonempty arranged content");
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(content);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,"ui-"+name+".png"));encoder.Save(file);
        }
    }
    private static void Click(Window w,string label)=>Find<Button>(w).Single(b=>AutomationProperties.GetName(b)==label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
    {
        if(parent is T value)yield return value;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)foreach(var child in Find<T>(VisualTreeHelper.GetChild(parent,i)))yield return child;
    }
}
