using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Capture.Windows.Editor;
using Capture.Windows.Platform;
using Capture.Windows.Updates;

namespace Capture.Windows;

public partial class App : Application
{
    private UpdateService? updates;
    private readonly DispatcherTimer updatePoll=new() {Interval=TimeSpan.FromHours(6)};
    private Mutex? singleton;
    private EventWaitHandle? wake;
    private RegisteredWaitHandle? wakeRegistration;
    private HotkeyService? hotkeys;
    private TrayIcon? tray;
    private AppSettings settings=new();
    private readonly CaptureEngine engine=new();
    private readonly List<PreviewWindow> previews=[];
    private readonly List<BitmapSource> history=[];
    private readonly Dictionary<BitmapSource,EditorWindow> editors=[];
    private HomeWindow? home;
    private SettingsWindow? preferences;
    private bool capturing;
    private int backgroundExports;
    private bool quitting;
    private long captureRevision;
    private ManualScrollWindow? scrollWindow;
    private nint lastExternalWindow;
    private Int32Rect? repeatRegion;
    private string? repeatMonitor;
    private Native.RECT? repeatMonitorBounds;
    private CancellationTokenSource? activeCapture;
    private readonly DispatcherTimer foregroundPoll=new() {Interval=TimeSpan.FromMilliseconds(250)};
    private readonly TextBlock status=new() {Foreground=Brushes.LightSteelBlue,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,16,0,0)};

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        // Rendering smoke tests run without global shortcuts, tray or screen capture permission.
        if(e.Args.FirstOrDefault() is "--update-integration" or "--verify-update"){_=UpdateIntegration.RunAsync(e.Args,Shutdown);return;}
        if(e.Args.Contains("--self-test")) {RunSelfTests(e.Args);return;}
        if(e.Args.Contains("--editor-demo")) {
            var editor=new EditorWindow(ImageActions.Demo(),closeOnDismiss:true);
            editor.Closed+=(_,_)=>Shutdown();editor.Show();return;
        }
        singleton=new Mutex(true,@"Local\Snaplet.Windows11.User",out var created);
        if(!created){try{using var handle=EventWaitHandle.OpenExisting(@"Local\Snaplet.Windows11.Show");handle.Set();}catch(WaitHandleCannotBeOpenedException){}Shutdown();return;}
        wake=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\Snaplet.Windows11.Show");
        wakeRegistration=ThreadPool.RegisterWaitForSingleObject(wake,(_,_)=>Dispatcher.BeginInvoke(ShowHome),null,Timeout.Infinite,false);
        settings=SettingsStore.Load(out var warning);
        updates=new UpdateService(new VelopackBackend());updates.Changed+=()=>{if(updates.Snapshot.Stage==UpdateStage.Ready && !capturing)status.Text=$"업데이트 {updates.Snapshot.TargetVersion} 준비됨 · 설정 → 업데이트에서 다시 시작하세요.";};
        updatePoll.Tick+=async(_,_)=>{if(settings.AutoUpdate)await updates.CheckAsync(true);};updatePoll.Start();
        _=CheckUpdatesAfterStartupAsync();
        try {
            hotkeys=new HotkeyService();hotkeys.Pressed+=(command,timestamp)=>_=RunCommandAsync(command,timestamp);hotkeys.TrayClicked+=ShowTrayMenu;
            tray=new TrayIcon(hotkeys.Handle);
            try{hotkeys.Apply(settings.Hotkeys);}catch(Exception error){warning=error.Message+"\n설정에서 단축키를 바꿔 주세요. 트레이 메뉴는 사용할 수 있습니다.";}
        } catch(Exception error){Error(error);}
        foregroundPoll.Tick+=(_,_)=>{
            var hwnd=Native.GetForegroundWindow();Native.GetWindowThreadProcessId(hwnd,out var pid);
            if(pid!=Environment.ProcessId && hwnd!=0)lastExternalWindow=hwnd;
        };foregroundPoll.Start();
        CleanDragFiles();
        if(e.Args.Contains("--demo")){var demo=ImageActions.Demo();Received(demo,"합성 데모 이미지");Edit(demo);}
        else ShowHome();
        if(warning is not null)status.Text=warning;
        foreach(var path in e.Args.Where(File.Exists)){try{Edit(ImageActions.Load(path));}catch(Exception error){Error(error);}}
    }
    private async Task RunCommandAsync(string command,long? hotkeyTimestamp=null,EditorWindow? destination=null) {
        if(quitting)return;
        if(command=="scroll-add"){if(scrollWindow is not null)await scrollWindow.AddFrameAsync();return;}
        if(command=="scroll-finish"){scrollWindow?.Finish();return;}
        if(command=="last"){if(history.FirstOrDefault() is BitmapSource last)Edit(last);else ShowHome();return;}
        if(scrollWindow is not null){scrollWindow.Message("스크롤 캡처가 진행 중입니다. F9 또는 완료 버튼으로 마쳐 주세요.");return;}
        if(capturing)return;
        var foreground=Native.GetForegroundWindow();Native.GetWindowThreadProcessId(foreground,out var pid);
        if(pid==Environment.ProcessId)foreground=lastExternalWindow;
        capturing=true;activeCapture=new CancellationTokenSource();var token=activeCapture.Token;
        var hidden=new List<Window>();
        var clock=Stopwatch.StartNew();
        var diagnostics=new CaptureDiagnostics(command,hotkeyTimestamp);
        try {
            foreach(Window window in Windows)if(window.IsVisible && window is not SelectionWindow){hidden.Add(window);window.Hide();}
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
            // Wait for compositor presentation only when own windows were hidden.
            // A UI Render-priority callback alone does not synchronize DWM.
            if(hidden.Count>0) {var flushResult=await Task.Run(()=>Native.DwmFlush(),token);diagnostics.Mark(flushResult>=0?"compositorFlushReturnedMs":"compositorFlushFailedMs");}
            diagnostics.Mark("ownWindowsHiddenMs");
            if(command=="delayed") {status.Text="3초 후 현재 화면을 캡처합니다…";await Task.Delay(3000,token);}
            CapturedFrame result;
            if(command=="window") {
                if(foreground==0 || !Native.IsWindow(foreground))throw new InvalidOperationException("캡처할 창을 찾지 못했습니다. 대상 창을 활성화하고 단축키를 눌러 주세요.");
                result=await engine.CaptureWindowAsync(foreground,token);
            } else if(command is "region" or "pick-window" or "scroll") {
                var displays=Native.Displays();
                var targets=command=="pick-window"?Native.Windows():null;
                var shots=await Task.WhenAll(displays.Select(d=>engine.CaptureMonitorAsync(d.Handle,token)));
                diagnostics.Frames(shots);
                var selection=await SelectAsync(displays,shots,targets,token,diagnostics);
                if(selection is null)return;
                if(selection.Window!=0)result=await engine.CaptureWindowAsync(selection.Window,token);
                else {
                    var rect=selection.Region!.Value;
                    var index=Array.FindIndex(shots,f=>Contains(f,rect));
                    if(index<0)throw new InvalidOperationException("선택 영역의 화면 구성이 바뀌었습니다. 다시 캡처해 주세요.");
                    result=Crop(shots[index],rect);repeatRegion=rect;repeatMonitor=displays[index].Name;repeatMonitorBounds=displays[index].Bounds;
                    if(command=="scroll") {
                        var monitorName=displays[index].Name;
                        var monitorBounds=displays[index].Bounds;
                        scrollWindow=new ManualScrollWindow(result.Image,async cancel=>{
                            var current=Native.Displays().FirstOrDefault(d=>d.Name==monitorName)??throw new InvalidOperationException("캡처 중 모니터가 변경됐습니다.");
                            if(current.Bounds.Left!=monitorBounds.Left || current.Bounds.Top!=monitorBounds.Top || current.Bounds.Right!=monitorBounds.Right || current.Bounds.Bottom!=monitorBounds.Bottom)throw new InvalidOperationException("모니터 위치나 해상도가 바뀌었습니다. 스크롤 캡처를 다시 시작해 주세요.");
                            return Crop(await engine.CaptureMonitorAsync(current.Handle,cancel),rect).Image;
                        });
                        scrollWindow.Finished+=image=>{Received(image,"수동 스크롤 캡처 · 겹침 검증 완료");Edit(image);};
                        scrollWindow.Closed+=(_,_)=>{hotkeys?.UnregisterScrollKeys();scrollWindow=null;};
                        scrollWindow.Show();
                        try{hotkeys?.RegisterScrollKeys();}catch(Exception error){scrollWindow.Message(error.Message);}
                        return;
                    }
                }
            } else if(command=="repeat") {
                if(repeatRegion is not Int32Rect rect)throw new InvalidOperationException("먼저 영역을 캡처해 주세요.");
                var display=Native.Displays().FirstOrDefault(d=>d.Name==repeatMonitor && rect.X>=d.Bounds.Left && rect.Y>=d.Bounds.Top && rect.X+rect.Width<=d.Bounds.Right && rect.Y+rect.Height<=d.Bounds.Bottom)??throw new InvalidOperationException("이전 영역의 화면 구성이 바뀌었습니다. 영역을 다시 선택해 주세요.");
                if(repeatMonitorBounds is not Native.RECT oldBounds || oldBounds.Left!=display.Bounds.Left || oldBounds.Top!=display.Bounds.Top || oldBounds.Right!=display.Bounds.Right || oldBounds.Bottom!=display.Bounds.Bottom)throw new InvalidOperationException("이전 영역의 모니터 위치나 해상도가 바뀌었습니다. 영역을 다시 선택해 주세요.");
                result=Crop(await engine.CaptureMonitorAsync(display.Handle,token),rect);
            } else if(command=="desktop")result=await engine.CaptureDesktopAsync(token);
            else result=await engine.CaptureMonitorAsync(Native.PointerDisplay().Handle,token);
            if(command is not ("region" or "pick-window" or "scroll")) {diagnostics.Frames([result]);diagnostics.Complete("snapshot-ready");}
            if(destination!=null)destination.AddCapturedImage(result.Image);
            else Received(result.Image,$"{result.Engine} · {result.Image.PixelWidth} × {result.Image.PixelHeight} px · 처리 {clock.ElapsedMilliseconds}ms (선택 시간 포함)");
        } catch(OperationCanceledException) {diagnostics.Complete("cancelled");status.Text="캡처를 취소했습니다.";}
        catch(Exception error){diagnostics.Complete("error:"+error.GetType().Name);Error(error);}
        finally {
            diagnostics.Complete("finished-before-overlay-render");
            activeCapture.Dispose();activeCapture=null;capturing=false;
            foreach(var window in hidden) {
                if(window is PreviewWindow p){if(previews.Contains(p))p.Show();}
                else if(Windows.Cast<Window>().Contains(window)) {var activate=window.ShowActivated;window.ShowActivated=false;window.Show();window.ShowActivated=activate;}
            }
            if(destination!=null && !quitting && Windows.Cast<Window>().Contains(destination))destination.ShowDocument();
        }
    }
    private Task<SelectionResult?> SelectAsync(IReadOnlyList<DisplayInfo> displays,CapturedFrame[] shots,IReadOnlyList<PickableWindow>? targets,CancellationToken token,CaptureDiagnostics diagnostics) {
        var completion=new TaskCompletionSource<SelectionResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlays=new List<SelectionWindow>();
        bool completed=false;int rendered=0;
        diagnostics.Mark("overlayCreateStartedMs");
        void Finish(SelectionResult? result) {
            if(completed)return;completed=true;
            foreach(var overlay in overlays)overlay.Close();completion.TrySetResult(result);
        }
        for(var i=0;i<displays.Count;i++) {
            var overlay=new SelectionWindow(displays[i],shots[i].Image,targets);overlays.Add(overlay);overlay.Completed+=Finish;
            overlay.Closed+=(_,_)=>{if(!completed)Finish(null);};
            overlay.ContentRendered+=(_,_)=>{if(++rendered==displays.Count){diagnostics.Mark("overlayContentRenderedMs");diagnostics.Complete("overlay-rendered");}};
        }
        try {
            foreach(var overlay in overlays)overlay.Show();
            diagnostics.Mark("overlaysShownMs");
            var pointer=Native.PointerDisplay();var index=displays.ToList().FindIndex(d=>d.Handle==pointer.Handle);
            if(index<0)throw new InvalidOperationException("화면 구성이 변경됐습니다. 다시 캡처해 주세요.");
            overlays[index].Activate();
        } catch {Finish(null);throw;}
        var registration=token.Register(()=>Dispatcher.BeginInvoke(()=>Finish(null)));
        _=completion.Task.ContinueWith(_=>registration.Dispose(),TaskScheduler.Default);
        return completion.Task;
    }
    private static bool Contains(CapturedFrame f,Int32Rect r)=>r.X>=f.X && r.Y>=f.Y && r.X+r.Width<=f.X+f.Image.PixelWidth && r.Y+r.Height<=f.Y+f.Image.PixelHeight;
    private static CapturedFrame Crop(CapturedFrame source,Int32Rect r) {
        if(!Contains(source,r))throw new InvalidOperationException("선택 영역이 캡처 화면을 벗어났습니다.");
        var image=new CroppedBitmap(source.Image,new Int32Rect(r.X-source.X,r.Y-source.Y,r.Width,r.Height));image.Freeze();return new(image,r.X,r.Y,source.Engine) {CaptureMilliseconds=source.CaptureMilliseconds,FallbackReason=source.FallbackReason};
    }
    private void Received(BitmapSource image,string detail) {
        var revision=++captureRevision;
        var copyLease=settings.CopyAfterCapture?ImageActions.ReserveClipboard():0;
        history.Insert(0,image);
        while(history.Count>1 && (history.Count>12 || history.Sum(x=>(long)x.PixelWidth*x.PixelHeight*4)>256_000_000))history.RemoveAt(history.Count-1);
        home?.UpdateRecent(history);status.Text=detail;
        foreach(var old in previews.Where(p=>!p.Pinned).ToArray())old.Close();
        if(settings.ShowPreview) {
            var preview=new PreviewWindow(image,settings);previews.Add(preview);
            preview.EditRequested+=Edit;preview.OcrRequested+=source=>_=RecognizeAsync(source);
            preview.Closed+=(_,_)=>previews.Remove(preview);preview.Show();
        }
        _=AfterCaptureAsync(image,settings,revision,copyLease);
    }
    private async Task AfterCaptureAsync(BitmapSource image,AppSettings snapshot,long revision,long copyLease) {
        backgroundExports++;
        try {
            // Let the preview paint before encoding and clipboard work.
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
            if(snapshot.CopyAfterCapture && revision==captureRevision)await ImageActions.CopyAsync(image,()=>revision==captureRevision,copyLease);
            if(snapshot.SaveAfterCapture){Directory.CreateDirectory(snapshot.SaveFolder);var path=Path.Combine(snapshot.SaveFolder,ImageActions.DefaultName());await Task.Run(()=>ImageActions.Save(image,path));}
        } catch(Exception error){Error(error);}
        finally {backgroundExports--;}
    }
    private void Edit(BitmapSource image) {
        if(editors.TryGetValue(image,out var existing)){existing.ShowDocument();return;}
        // Release older hidden documents; visible editors are owned by the user.
        foreach(var pair in editors.Where(p=>!p.Value.IsVisible).ToArray()) {
            if(editors.Count<12 && editors.Values.Sum(window=>window.EstimatedImageBytes)<256_000_000)break;
            pair.Value.CloseForExit();editors.Remove(pair.Key);
        }
        var window=new EditorWindow(image);editors[image]=window;
        window.OcrRequested+=source=>_=RecognizeAsync(source);window.RasterizedRequested+=Edit;
        window.AddCaptureRequested+=()=>RunCommandAsync("region",null,window);
        window.PinRequested+=bitmap=>{
            var pin=new PreviewWindow(bitmap,settings,true);previews.Add(pin);pin.EditRequested+=Edit;pin.OcrRequested+=source=>_=RecognizeAsync(source);
            pin.Closed+=(_,_)=>previews.Remove(pin);pin.Show();
        };
        window.Closed+=(_,_)=>editors.Remove(image);Exclude(window);window.ShowDocument();
    }
    private async Task RecognizeAsync(BitmapSource image) {
        try {
            status.Text="텍스트를 읽는 중…";
            var text=await TesseractOcr.RecognizeAsync(image,CancellationToken.None);
            var window=new OcrWindow(text);Exclude(window);window.Show();window.Activate();status.Text="텍스트 추출을 완료했습니다.";
        } catch(Exception error){Error(error);}
    }
    private void OpenImage() {
        var dialog=new OpenFileDialog {Filter="이미지|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif"};
        if(dialog.ShowDialog()!=true)return;
        try{Edit(ImageActions.Load(dialog.FileName));}catch(Exception error){Error(error);}
    }
    private void ClipboardImage() {try{if(Clipboard.GetImage() is BitmapSource image){image.Freeze();Edit(image);}else status.Text="클립보드에 이미지가 없습니다.";}catch(Exception error){Error(error);}}
    private void ShowSettings()=>ShowSettingsPage("capture");
    private void ShowSettingsPage(string page) {
        if(preferences is not null){preferences.SelectPage(page);preferences.Show();preferences.Activate();return;}
        preferences=new SettingsWindow(settings,next=>{
            var previous=settings;
            hotkeys?.Apply(next.Hotkeys);
            try{SettingsStore.Save(next);settings=next;}catch{hotkeys?.Apply(previous.Hotkeys);throw;}
            home?.UpdateSettings(settings);status.Text="설정을 저장했습니다.";
        },updates,RestartForUpdate);preferences.SelectPage(page);preferences.Closed+=(_,_)=>preferences=null;Exclude(preferences);preferences.Show();
    }
    private async Task CheckUpdatesAfterStartupAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if(!quitting && settings.AutoUpdate && updates!=null)await updates.CheckAsync(true);
    }
    private void RestartForUpdate()
    {
        if(updates?.Snapshot.Stage!=UpdateStage.Ready)return;
        if(capturing || scrollWindow!=null || backgroundExports>0 || editors.Values.Any(editor=>editor.IsExporting)){UI.NoticeWindow.Alert(preferences,"현재 작업을 먼저 마쳐 주세요","진행 중인 캡처·복사·저장을 완료한 뒤 업데이트할 수 있습니다.");return;}
        if((history.Count>0 || editors.Count>0) && !UI.NoticeWindow.Confirm(preferences,"업데이트하고 다시 시작할까요?","최근 캡처와 저장하지 않은 편집은 다시 시작하면 사라집니다. 필요한 이미지를 먼저 복사하거나 저장하세요.","업데이트하고 다시 시작"))return;
        if(capturing || scrollWindow!=null || backgroundExports>0 || editors.Values.Any(editor=>editor.IsExporting))return;
        updates.ApplyAndRestart(()=>{quitting=true;foreach(var editor in editors.Values.ToArray())editor.CloseForExit();Shutdown();});
    }
    private void ShowTrayMenu() {
        var menu=new ContextMenu {Placement=PlacementMode.MousePoint};
        void Add(string text,Action action){var item=new MenuItem {Header=text};item.Click+=(_,_)=>action();menu.Items.Add(item);}
        foreach(var (text,command) in new[]{("영역 캡처","region"),("활성 창 캡처","window"),("창 선택 캡처","pick-window"),("현재 화면 캡처","display"),("모든 화면 캡처","desktop"),("3초 후 화면 캡처","delayed"),("직전 영역 반복","repeat"),("최근 캡처 편집","last")}) {var c=command;Add(text,()=>_=RunCommandAsync(c));}
        Add("수동 스크롤 캡처…",()=>_=RunCommandAsync("scroll"));
        var recent=new MenuItem {Header="최근 캡처 (메모리)"};foreach(var image in history){var bitmap=image;var item=new MenuItem {Header=$"{bitmap.PixelWidth} × {bitmap.PixelHeight} px",Icon=new Image{Source=bitmap,Width=40,Height=26,Stretch=Stretch.Uniform}};item.Click+=(_,_)=>Edit(bitmap);recent.Items.Add(item);}menu.Items.Add(recent);
        menu.Items.Add(new Separator());Add("이미지 열기…",OpenImage);Add("클립보드 이미지 편집",ClipboardImage);Add("설정…",ShowSettings);Add("시작 화면",ShowHome);Add("업데이트…",()=>ShowSettingsPage("updates"));Add("Snaplet 종료",()=>Shutdown());
        menu.IsOpen=true;
    }
    private void ShowHome() {
        if(home is not null){home.UpdateSettings(settings);home.UpdateRecent(history);home.Show();home.Activate();return;}
        home=new HomeWindow(settings,command=>_=RunCommandAsync(command),OpenImage,ClipboardImage,()=>Edit(ImageActions.Demo()),ShowSettings,Edit,status);
        home.UpdateRecent(history);
        home.Closing+=(_,e)=>{if(!quitting){e.Cancel=true;home.Hide();}};
        Exclude(home);home.Show();home.Activate();
    }
    private static void Exclude(Window window) {window.SourceInitialized+=(_,_)=>Native.SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle,0x11);}
    private void Error(Exception error) {status.Text=error.Message;UI.NoticeWindow.Alert(home,"작업을 완료하지 못했습니다",error.Message);}
    private async void RunSelfTests(string[] args) {
        var index=Array.IndexOf(args,"--self-test");var path=index+1<args.Length?args[index+1]:Path.Combine(Environment.CurrentDirectory,"smoke-results");
        try {
            Directory.CreateDirectory(path);
            System.Windows.Media.RenderOptions.ProcessRenderMode=RenderMode.SoftwareOnly;
            EditorSmokeTest.Run(path);
            UI.UiSmokeTest.Run(path);
            await UpdateSmokeTest.RunAsync(path);
            GdiSnapshot.RunSelfTest();
            File.WriteAllText(Path.Combine(path,"gdi-smoke.txt"),"PASS: synthetic 2x2 DIB→BitBlt pixel order, opaque alpha, native resource lifetime and pre-cancellation. No desktop capture.");
            if(args.Contains("--ocr-test")) {
                var visual=new DrawingVisual();using(var dc=visual.RenderOpen()) {
                    dc.DrawRectangle(Brushes.White,null,new Rect(0,0,1100,180));
                    dc.DrawText(new FormattedText("CAPTURE OCR 12345",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Arial"),65,Brushes.Black,1),new Point(30,45));
                }
                var fixture=new RenderTargetBitmap(1100,180,96,96,PixelFormats.Pbgra32);fixture.Render(visual);fixture.Freeze();
                var text=await TesseractOcr.RecognizeAsync(fixture);File.WriteAllText(Path.Combine(path,"ocr-result.txt"),text);
                var textRegions=await TesseractOcr.FindTextRegionsAsync(fixture);
                if(textRegions.Count<3 || textRegions.Any(r=>r.X<0 || r.Y<0 || r.Right>fixture.PixelWidth || r.Bottom>fixture.PixelHeight))throw new InvalidOperationException("텍스트 영역 OCR 검증 실패");
                File.WriteAllText(Path.Combine(path,"ocr-text-regions.txt"),$"PASS: {textRegions.Count} recognized word regions within fixture bounds.");
                var normalized=new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                if(!normalized.Contains("CAPTUREOCR12345"))throw new InvalidOperationException("OCR smoke fixture mismatch: "+text);
            }
            File.WriteAllText(Path.Combine(path,"result.txt"),"PASS: renderer/model and synthetic GDI smoke tests. Real desktop capture/hotkeys/focus were not tested.");Shutdown(0);
        } catch(Exception error){Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"result.txt"),error.ToString());Shutdown(1);}
    }
    private static void CleanDragFiles() {
        try {var folder=Path.Combine(SettingsStore.DirectoryPath,"Drag");if(Directory.Exists(folder))foreach(var path in Directory.GetFiles(folder,"*.png"))if(File.GetLastWriteTimeUtc(path)<DateTime.UtcNow.AddDays(-1))File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    protected override void OnExit(ExitEventArgs e) {
        quitting=true;updates?.Dispose();updatePoll.Stop();activeCapture?.Cancel();foregroundPoll.Stop();wakeRegistration?.Unregister(null);wake?.Dispose();tray?.Dispose();hotkeys?.Dispose();singleton?.Dispose();base.OnExit(e);
    }
}
