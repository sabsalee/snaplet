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
        static bool Throws(Action action){try{action();return false;}catch(ArgumentException){return true;}}
        foreach(var name in AppIcons.Paths.Keys) {
            var icon=Ui.Icon(name);Check(icon is Viewbox {Child:Canvas {Width:AppIcons.Grid,Height:AppIcons.Grid}},"icon keeps the Phosphor design grid: "+name);
        }
        Check(Throws(()=>Ui.Icon("■")),"an unknown icon name is an error, never drawn as text");
        foreach(var weight in new[]{400,500,600}) {
            var face=new Typeface(Ui.Font,FontStyles.Normal,FontWeight.FromOpenTypeWeight(weight),FontStretches.Normal);
            Check(face.TryGetGlyphTypeface(out var glyphs) && glyphs.FamilyNames.Values.Contains("Pretendard") && glyphs.Weight.ToOpenTypeWeight()==weight && glyphs.StyleSimulations==StyleSimulations.None
                && glyphs.CharacterToGlyphMap.ContainsKey('가') && glyphs.CharacterToGlyphMap.ContainsKey('A'),"the bundled Pretendard face is used for weight "+weight);
        }
        Check(Application.Current.TryFindResource("AppFont") is FontFamily appFont && appFont.Source.Contains("Pretendard"),"windows, tooltips and menus use the bundled interface font");
        {
            var plainOcr=new OcrWindow("가나다");
            try {Check(plainOcr.FontFamily.Source.Contains("Pretendard") && TextOptions.GetTextFormattingMode(plainOcr)==TextFormattingMode.Ideal,"window subclasses without their own font setting still use the interface font");}
            finally {plainOcr.Close();}
        }
        var supported=WindowFrame.Override;
        try {
            WindowFrame.Override=false;
            var plain=new Window();var plainBand=new DockPanel();plain.Content=plainBand;
            Check(!WindowFrame.Unify(plain,plainBand,52) && System.Windows.Shell.WindowChrome.GetWindowChrome(plain) is null,"older Windows keeps the normal title bar");
            WindowFrame.Override=true;
            var unified=new Window {Width=600,Height=300};var band=new DockPanel {Height=52};var action=new Button();band.Children.Add(action);var body=new DockPanel();DockPanel.SetDock(band,Dock.Top);body.Children.Add(band);unified.Content=body;
            Check(WindowFrame.Unify(unified,band,52) && System.Windows.Shell.WindowChrome.GetWindowChrome(unified) is {CaptionHeight:52,UseAeroCaptionButtons:true},"the toolbar becomes the title bar and Windows keeps its caption buttons");
            Check(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(action) && WindowFrame.IsTitleBand(band) && band.Children.OfType<FrameworkElement>().Any(e=>e.Width==WindowFrame.CaptionButtons),"toolbar buttons stay clickable and the caption buttons keep their room");
            var strip=new Window {Title="스트립 제목",Content=new Grid()};
            Check(WindowFrame.UnifyWithStrip(strip) && strip.Content is DockPanel {Children.Count:2} root && root.Children[0] is DockPanel first && WindowFrame.IsTitleBand(first) && Find<TextBlock>(first).Any(t=>t.Text=="스트립 제목"),"windows without a toolbar get a slim strip with their title");
        } finally {WindowFrame.Override=supported;}
        var image=ImageActions.Demo();var settings=new AppSettings();var commands=new List<string>();
        var home=new HomeWindow(settings,commands.Add,()=>commands.Add("open"),()=>commands.Add("paste"),()=>{},()=>{},_=>{},_=>{},new TextBlock());
        try {
            Shot(home,"home-empty");
            Check(Find<TextBlock>(home).Where(t=>t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).All(t=>t.Foreground is SolidColorBrush c && c.Color!=Colors.Black),"home text uses explicit readable theme foregrounds");
            Click(home,"영역 캡처");Check(commands.SequenceEqual(new[]{"region"}),"home region action invokes the expected command without taking a screenshot");
            Click(home,"클립보드에서 가져오기");Click(home,"이미지 열기");Check(commands.SequenceEqual(new[]{"region","paste","open"}),"the empty main window pastes and opens images from its toolbar");
            Check(home.AllowDrop && Find<TextBlock>(home).Any(t=>t.IsVisible && t.Text=="캡처하거나 이미지를 끌어다 놓으세요"),"the empty main window accepts dropped images and says so");
            Check(Find<TextBlock>(home).Any(t=>t.IsVisible && t.Text==settings.Hotkeys.First(k=>k.Command=="region").ToString()),"the empty main window shows the region capture shortcut");
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
        // Secondary windows follow the Windows app theme like the editor; the capture popup stays dark over images.
        try {
            foreach(var dark in new[]{false,true}) {
                Editor.EditorTheme.Override=dark;
                var expected=((System.Windows.Media.SolidColorBrush)(dark?Application.Current.FindResource("PageBrush"):Editor.EditorTheme.Light["PageBrush"])).Color;
                var themed=new SettingsWindow(settings,_=>{});
                try {
                    Shot(themed,dark?"settings-dark":"settings-light");
                    Check(((System.Windows.Media.SolidColorBrush)themed.FindResource("PageBrush")).Color==expected && ((System.Windows.Media.SolidColorBrush)Ui.Page).Color==expected,(dark?"dark":"light")+" Windows theme reaches the settings window and its built-in colours");
                }finally{themed.Close();}
            }
            Editor.EditorTheme.Override=false;
            Check(((System.Windows.Media.SolidColorBrush)Ui.Dark.Card).Color==((System.Windows.Media.SolidColorBrush)Application.Current.FindResource("CardBrush")).Color,"the capture popup keeps its dark palette in the light theme");
        }finally{Editor.EditorTheme.Override=null;}
        var preview=new PreviewWindow(image,settings,enableDesktopIntegration:false);
        try {
            Shot(preview,"preview");Click(preview,"이미지 고정");Check(preview.Pinned,"preview pin command switches to pinned presentation");
            Shot(preview,"pinned");preview.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=UIElement.MouseEnterEvent});Shot(preview,"pinned-tools");
            preview.SetPinOpacity(.5);Check(preview.Opacity==.5,"pin opacity reaches the window");preview.SetPinOpacity(1);
            preview.ActualSizePin();preview.ZoomPin(1.5);preview.ZoomPin(1.5);preview.UpdateLayout();Check(preview.PinZoom>2.2,"a pinned image zooms past its original size");Shot(preview,"pinned-zoomed");
            preview.ZoomPin(100);Check(Math.Abs(preview.PinZoom-4)<.01,"pin zoom stops at 400%");preview.ActualSizePin();Check(Math.Abs(preview.PinZoom-1)<.01,"the zoom label returns the pin to 100%");
            preview.ZoomPin(2);preview.UpdateLayout();Click(preview,"원래 크기로");Check(Math.Abs(preview.PinZoom-1)<.01,"clicking the zoom level in the bar returns to 100%");
            Click(preview,"고정 해제");preview.UpdateLayout();Check(!preview.Pinned,"unpin rebuilds compact preview without reparenting errors");
            bool edited=false;preview.EditRequested+=_=>edited=true;Click(preview,"편집");Check(edited && !preview.IsVisible,"preview edit dispatches image and dismisses unpinned window");
        }finally{if(preview.IsVisible)preview.Close();}
        var popup=new PreviewWindow(image,settings,enableDesktopIntegration:false,nearPointer:true);
        try {
            Shot(popup,"popup");
            popup.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=UIElement.MouseEnterEvent});Shot(popup,"popup-hover");
            Check(((FrameworkElement)popup.Content).ActualWidth<=380,"the after-capture popup stays small");
            Check(new[]{"편집","이미지 복사","이미지 저장","텍스트 추출","이미지 고정","미리보기 닫기"}.All(n=>Find<Button>(popup).Any(b=>b.IsVisible && AutomationProperties.GetName(b)==n)),"the popup offers edit, copy, save, text, pin and close");
            Click(popup,"이미지 고정");Check(popup.Pinned,"pinning from the popup switches to a pinned image");
            popup.UpdateLayout();Click(popup,"고정 해제");popup.UpdateLayout();Check(!popup.Pinned && ((FrameworkElement)popup.Content).ActualWidth<=380,"unpinning returns to the small popup");
        }finally{if(popup.IsVisible)popup.Close();}
        Check(PreviewWindow.PopupPlacement(100,100,200,150,0,0,1920,1040)==(118,118),"the popup opens below and right of the pointer");
        Check(PreviewWindow.AvoidOverlap(100,100,200,150,[(100,100,300,250)],0,1920,1040)==(100,258),"a new popup moves below one already open");
        Check(PreviewWindow.AvoidOverlap(500,900,200,150,[(500,900,700,1040)],0,1920,1040)==(292,900),"at the bottom edge it moves beside the open popup");
        Check(PreviewWindow.AvoidOverlap(100,100,200,150,[(600,600,800,750)],0,1920,1040)==(100,100),"a clear spot stays where it is");
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
        var scaled=new HomeWindow(settings,_=>{},()=>{},()=>{},()=>{},()=>{},_=>{},_=>{},new TextBlock());
        try {scaled.Width=1120;scaled.Height=900;((FrameworkElement)scaled.Content).LayoutTransform=new ScaleTransform(1.5,1.5);Shot(scaled,"home-layout-150");Check(((FrameworkElement)scaled.Content).ActualWidth>=660,"150 percent layout scale retains supported content width");}finally{scaled.Close();}
        File.WriteAllText(Path.Combine(directory,"ui-smoke.json"),JsonSerializer.Serialize(new{component="application design and routed actions",passed=checks.Count,checks,limitations=new[]{"Routed WPF command checks are not physical mouse/keyboard or IME testing","Layout scaling is not an OS DPI transition test","Does not read the desktop or measure capture latency"}},new JsonSerializerOptions{WriteIndented=true}));
        void Shot(Window window,string name)
        {
            if(!window.IsVisible)window.Show();window.UpdateLayout();
            var content=(FrameworkElement)window.Content;
            Check(content.ActualWidth>100 && content.ActualHeight>60,name+" has nonempty arranged content");
            // A transparent window draws its content inside a margin for the shadow, so the whole window is captured.
            Visual target=window.AllowsTransparency?window:content;
            double w=window.AllowsTransparency?window.ActualWidth:content.ActualWidth,h=window.AllowsTransparency?window.ActualHeight:content.ActualHeight;
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(w),(int)Math.Ceiling(h),96,96,PixelFormats.Pbgra32);bitmap.Render(target);
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
