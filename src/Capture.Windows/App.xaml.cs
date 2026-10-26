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
    private readonly CancellationTokenSource commandListener=new();
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
        var forwarded=CommandChannel.Parse(e.Args);
        singleton=new Mutex(true,@"Local\Snaplet.Windows11.User",out var created);
        if(!created && forwarded!=null && CommandChannel.Send(forwarded)){Shutdown();return;}
        if(!created){try{using var handle=EventWaitHandle.OpenExisting(@"Local\Snaplet.Windows11.Show");handle.Set();}catch(WaitHandleCannotBeOpenedException){}Shutdown();return;}
        wake=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\Snaplet.Windows11.Show");
        wakeRegistration=ThreadPool.RegisterWaitForSingleObject(wake,(_,_)=>Dispatcher.BeginInvoke(ShowHome),null,Timeout.Infinite,false);
        settings=SettingsStore.Load(out var warning);
        _=CommandChannel.ListenAsync(command=>Dispatcher.BeginInvoke(()=>_=RunCommandAsync(command)),commandListener.Token);
        if(settings.EnableDeepLinks)CommandChannel.SetProtocolRegistration(true);
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
        if(settings.KeepHistoryOnDisk){try{history.AddRange(HistoryStore.Load());}catch(Exception error){warning??="최근 캡처를 불러오지 못했습니다: "+error.Message;}}
        if(e.Args.Contains("--demo")){var demo=ImageActions.Demo();Received(demo,"합성 데모 이미지");Edit(demo);}
        else if(forwarded!=null)Dispatcher.BeginInvoke(()=>_=RunCommandAsync(forwarded),DispatcherPriority.ApplicationIdle);
        else ShowHome();
        if(warning is not null)status.Text=warning;
        foreach(var path in e.Args.Where(File.Exists)){try{Edit(ImageActions.Load(path));}catch(Exception error){Error(error);}}
    }
    private async Task RunCommandAsync(string command,long? hotkeyTimestamp=null,EditorWindow? destination=null) {
        if(quitting)return;
        if(command=="scroll-add"){if(scrollWindow is not null)await scrollWindow.AddFrameAsync();return;}
        if(command=="scroll-finish"){scrollWindow?.Finish();return;}
        if(command=="last"){if(history.FirstOrDefault() is BitmapSource last)Edit(last);else ShowHome();return;}
        if(command=="home"){ShowHome();return;}
        if(scrollWindow is not null){scrollWindow.Message("스크롤 캡처가 진행 중입니다. F9 또는 완료 버튼으로 마쳐 주세요.");return;}
        if(capturing)return;
        var foreground=Native.GetForegroundWindow();Native.GetWindowThreadProcessId(foreground,out var pid);
        if(pid==Environment.ProcessId)foreground=lastExternalWindow;
        capturing=true;activeCapture=new CancellationTokenSource();var token=activeCapture.Token;
        var hidden=new List<Window>();
        var clock=Stopwatch.StartNew();
        var diagnostics=new CaptureDiagnostics(command,hotkeyTimestamp);
        // The pointer is read before our windows hide or the overlay appears, so it matches what the user saw.
        var cursor=settings.IncludeCursor && command is not ("ocr" or "scroll")?CursorSnapshot.Capture():null;
        bool windowCapture=command=="window";
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
            } else if(command is "region" or "pick-window" or "scroll" or "ocr") {
                var displays=Native.Displays();
                var targets=command=="pick-window"?Native.Windows():null;
                var shots=await Task.WhenAll(displays.Select(d=>engine.CaptureMonitorAsync(d.Handle,token)));
                diagnostics.Frames(shots);
                var purpose=command switch {"ocr"=>"텍스트·QR 인식","scroll"=>"스크롤 캡처 영역",_=>destination!=null?"편집 중인 이미지에 추가":null};
                var options=new SelectionOptions(settings.ShowLoupe,settings.AdjustSelectionBeforeCapture || command=="scroll",purpose);
                var selection=await SelectAsync(displays,shots,targets,token,diagnostics,options);
                if(selection is null)return;
                if(selection.Window!=0){result=await engine.CaptureWindowAsync(selection.Window,token);windowCapture=true;}
                else {
                    var rect=selection.Region!.Value;
                    var index=Array.FindIndex(shots,f=>Contains(f,rect));
                    if(index<0)throw new InvalidOperationException("선택 영역의 화면 구성이 바뀌었습니다. 다시 캡처해 주세요.");
                    result=Crop(shots[index],rect);
                    if(command=="ocr"){_=InstantRecognizeAsync(result.Image);return;}
                    repeatRegion=rect;repeatMonitor=displays[index].Name;repeatMonitorBounds=displays[index].Bounds;
                    if(command=="scroll") {
                        var monitorName=displays[index].Name;
                        var monitorBounds=displays[index].Bounds;
                        scrollWindow=new ManualScrollWindow(result.Image,async cancel=>{
                            var current=Native.Displays().FirstOrDefault(d=>d.Name==monitorName)??throw new InvalidOperationException("캡처 중 모니터가 변경됐습니다.");
                            if(current.Bounds.Left!=monitorBounds.Left || current.Bounds.Top!=monitorBounds.Top || current.Bounds.Right!=monitorBounds.Right || current.Bounds.Bottom!=monitorBounds.Bottom)throw new InvalidOperationException("모니터 위치나 해상도가 바뀌었습니다. 스크롤 캡처를 다시 시작해 주세요.");
                            return Crop(await engine.CaptureMonitorAsync(current.Handle,cancel),rect).Image;
                        },notches=>Native.WheelAt(rect.X+rect.Width/2,rect.Y+rect.Height/2,notches));
                        Native.GetCursorPos(out var pointerBefore);
                        scrollWindow.Closed+=(_,_)=>Native.SetCursorPos(pointerBefore.X,pointerBefore.Y);
                        scrollWindow.Loaded+=(_,_)=>PlaceBeside(scrollWindow,rect,displays[index]);
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
            if(command is not ("region" or "pick-window" or "scroll" or "ocr")) {diagnostics.Frames([result]);diagnostics.Complete("snapshot-ready");}
            if(command=="delayed")cursor=settings.IncludeCursor?CursorSnapshot.Capture():null;
            if(cursor!=null)result=cursor.DrawOnto(result);
            if(windowCapture && settings.WindowStyle!=WindowCaptureStyle.Plain)result=result with{Image=BackdropRenderer.Compose(result.Image,WindowBackdrop(settings.WindowStyle))};
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
    private Task<SelectionResult?> SelectAsync(IReadOnlyList<DisplayInfo> displays,CapturedFrame[] shots,IReadOnlyList<PickableWindow>? targets,CancellationToken token,CaptureDiagnostics diagnostics,SelectionOptions options) {
        var completion=new TaskCompletionSource<SelectionResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlays=new List<SelectionWindow>();
        bool completed=false;int rendered=0;
        diagnostics.Mark("overlayCreateStartedMs");
        void Finish(SelectionResult? result) {
            if(completed)return;completed=true;
            foreach(var overlay in overlays)overlay.Close();completion.TrySetResult(result);
        }
        for(var i=0;i<displays.Count;i++) {
            var overlay=new SelectionWindow(displays[i],shots[i].Image,targets,options);overlays.Add(overlay);overlay.Completed+=Finish;
            overlay.SelectionStarted+=()=>{foreach(var other in overlays)if(other!=overlay)other.ClearSelection();};
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
    /// <summary>Window framing presets; the editor's backdrop panel can refine them afterwards on a new capture.</summary>
    internal static Capture.Core.Backdrop WindowBackdrop(WindowCaptureStyle style)=>style==WindowCaptureStyle.Shadow
        ?new(Enabled:true,Fill:Capture.Core.BackdropFill.Transparent,Padding:36,CornerRadius:8,Shadow:.7)
        :new(Enabled:true,Fill:Capture.Core.BackdropFill.Gradient,Preset:4,Padding:56,CornerRadius:8,Shadow:.6);
    /// <summary>Keeps the scroll panel off the captured area so automatic wheel input never lands on Snaplet itself.</summary>
    private static void PlaceBeside(Window window,Int32Rect region,DisplayInfo display)
    {
        var hwnd=new WindowInteropHelper(window).Handle;if(hwnd==0 || !Native.GetWindowRect(hwnd,out var r))return;
        var work=display.Work;int w=r.Width,h=r.Height;
        int x=region.X+region.Width+16<=work.Right-w?region.X+region.Width+16:region.X-16-w>=work.Left?region.X-16-w:work.Right-w-16;
        int y=Math.Clamp(region.Y,work.Top,Math.Max(work.Top,work.Bottom-h));
        Native.SetWindowPos(hwnd,(nint)(-1),x,y,0,0,0x1|0x10);
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
        if(settings.KeepHistoryOnDisk)_=Task.Run(()=>{try{HistoryStore.Save(image);}catch(Exception error) when(error is IOException or UnauthorizedAccessException){}});
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
        var window=new EditorWindow(image){EscapeAction=settings.EscapeAction,EscapeSaveFolder=settings.SaveFolder};editors[image]=window;
        window.OcrRequested+=source=>_=RecognizeAsync(source);window.RasterizedRequested+=Edit;
        window.AddCaptureRequested+=()=>RunCommandAsync("region",null,window);
        window.PinRequested+=bitmap=>{
            var pin=new PreviewWindow(bitmap,settings,true);previews.Add(pin);pin.EditRequested+=Edit;pin.OcrRequested+=source=>_=RecognizeAsync(source);
            pin.Closed+=(_,_)=>previews.Remove(pin);pin.Show();
        };
        window.Closed+=(_,_)=>editors.Remove(image);Exclude(window);window.ShowDocument();
    }
    /// <summary>OCR button in preview/editor: decoded QR codes first, then recognised text.</summary>
    private async Task RecognizeAsync(BitmapSource image) {
        try {
            status.Text="텍스트와 QR 코드를 읽는 중…";
            var codes=await Task.Run(()=>QrReader.Decode(image));
            var text=JoinIfWanted(await TesseractOcr.RecognizeAsync(image,CancellationToken.None));
            var combined=string.Join("\n\n",codes.Concat(string.IsNullOrWhiteSpace(text)?Array.Empty<string>():new[]{text}));
            var window=new OcrWindow(combined,codes.Count>0?$"QR 코드 {codes.Count}개를 읽었습니다.":null);Exclude(window);window.Show();window.Activate();status.Text="텍스트 추출을 완료했습니다.";
        } catch(Exception error){Error(error);}
    }
    /// <summary>
    /// Instant recognition hotkey: a QR/2D code wins (its exact payload is copied); otherwise OCR text is copied.
    /// The result window opens for review, already copied, like the original's "Instant Text/QR Recognition".
    /// </summary>
    private async Task InstantRecognizeAsync(BitmapSource image) {
        try {
            status.Text="텍스트와 QR 코드를 읽는 중…";
            var codes=await Task.Run(()=>QrReader.Decode(image));
            string text;string notice;
            if(codes.Count>0){text=string.Join("\n",codes);notice=codes.Count==1?"QR 코드 내용을 클립보드에 복사했습니다.":$"QR 코드 {codes.Count}개를 클립보드에 복사했습니다.";}
            else {text=JoinIfWanted(await TesseractOcr.RecognizeAsync(image,CancellationToken.None)).Trim();notice=text.Length>0?"인식한 텍스트를 클립보드에 복사했습니다.":"인식된 텍스트가 없습니다.";}
            if(text.Length>0){try{Clipboard.SetText(text);}catch(ExternalException){notice="다른 앱이 클립보드를 사용 중이라 복사하지 못했습니다. 아래에서 복사하세요.";}}
            status.Text=notice;
            var window=new OcrWindow(text,notice);Exclude(window);window.Show();window.Activate();
        } catch(Exception error){Error(error);}
    }
    private string JoinIfWanted(string text)=>settings.OcrJoinLines?System.Text.RegularExpressions.Regex.Replace(text.Trim(),@"\s*\r?\n\s*"," "):text;
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
            if(previous.KeepHistoryOnDisk && !next.KeepHistoryOnDisk)HistoryStore.Clear();
            if(previous.EnableDeepLinks!=next.EnableDeepLinks)CommandChannel.SetProtocolRegistration(next.EnableDeepLinks);
            if(!previous.KeepHistoryOnDisk && next.KeepHistoryOnDisk){var oldestFirst=history.AsEnumerable().Reverse().ToArray();_=Task.Run(()=>{foreach(var image in oldestFirst){try{HistoryStore.Save(image);Thread.Sleep(2);}catch(Exception error) when(error is IOException or UnauthorizedAccessException){}}});}
            hotkeys?.Apply(next.Hotkeys);
            try{SettingsStore.Save(next);settings=next;}catch{hotkeys?.Apply(previous.Hotkeys);throw;}
            foreach(var editor in editors.Values){editor.EscapeAction=settings.EscapeAction;editor.EscapeSaveFolder=settings.SaveFolder;}
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
        foreach(var (text,command) in new[]{("영역 캡처","region"),("활성 창 캡처","window"),("창 선택 캡처","pick-window"),("현재 화면 캡처","display"),("모든 화면 캡처","desktop"),("3초 후 화면 캡처","delayed"),("직전 영역 반복","repeat"),("텍스트·QR 바로 인식","ocr"),("최근 캡처 편집","last")}) {var c=command;Add(text,()=>_=RunCommandAsync(c));}
        Add("수동 스크롤 캡처…",()=>_=RunCommandAsync("scroll"));
        var recent=new MenuItem {Header=settings.KeepHistoryOnDisk?"최근 캡처":"최근 캡처 (메모리)"};foreach(var image in history){var bitmap=image;var item=new MenuItem {Header=$"{bitmap.PixelWidth} × {bitmap.PixelHeight} px",Icon=new Image{Source=bitmap,Width=40,Height=26,Stretch=Stretch.Uniform}};item.Click+=(_,_)=>Edit(bitmap);recent.Items.Add(item);}menu.Items.Add(recent);
        if(previews.Any(p=>p.ClickThrough))Add("고정 이미지 클릭 통과 해제",()=>{foreach(var p in previews)p.SetClickThrough(false);});
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
            UI.SelectionSmokeTest.Run(path);
            BackdropSmokeTest.Run(path);
            await UI.ScrollSmokeTest.RunAsync(path);
            FidelitySheet.Run(path);
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
        quitting=true;commandListener.Cancel();updates?.Dispose();updatePoll.Stop();activeCapture?.Cancel();foregroundPoll.Stop();wakeRegistration?.Unregister(null);wake?.Dispose();tray?.Dispose();hotkeys?.Dispose();singleton?.Dispose();base.OnExit(e);
    }
}
