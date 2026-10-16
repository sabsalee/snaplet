using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;
using Capture.Windows.Platform;
using Capture.Windows.UI;

namespace Capture.Windows;

/// <summary>Explicit, manual downward scroll stitching; no input is injected into another app.</summary>
internal sealed class ManualScrollWindow : Window
{
    private readonly Func<CancellationToken,Task<BitmapSource>> capture;
    private readonly ScrollStitchSession session;
    private readonly TextBlock status=Ui.Label("",12,Ui.Muted);
    private readonly TextBlock length=Ui.Label("",25,weight:FontWeights.SemiBold);
    private readonly ProgressBar progress=new(){Height=3,IsIndeterminate=true,Visibility=Visibility.Collapsed};
    private int frames=1;
    private readonly CancellationTokenSource cancel=new();
    private bool busy;
    private bool ended;
    private bool finishAfterFrame;
    private readonly Button add;
    public event Action<BitmapSource>? Finished;
    public ManualScrollWindow(BitmapSource first,Func<CancellationToken,Task<BitmapSource>> captureFrame) {
        capture=captureFrame;
        var pixels=Bytes(first);
        if(first.PixelHeight<64)throw new InvalidOperationException("스크롤 캡처는 높이가 64px 이상인 영역을 선택해 주세요.");
        session=new ScrollStitchSession(first.PixelWidth,first.PixelHeight,pixels,maxOutputBytes:256*1024*1024,maxHeight:32768,minimumOverlap:Math.Min(64,first.PixelHeight/3));
        Title="Snaplet · 긴 화면";Width=460;SizeToContent=SizeToContent.Height;Topmost=true;ShowInTaskbar=false;ShowActivated=false;ResizeMode=ResizeMode.NoResize;
        var root=new StackPanel();Content=new Border{Background=Ui.Page,Padding=new Thickness(22),Child=root};
        root.Children.Add(Ui.Header("긴 화면 캡처","스크롤하고, 프레임을 추가하면 한 장으로 이어집니다."));
        var summary=new DockPanel();var badge=Ui.Keycap("수동 모드");DockPanel.SetDock(badge,Dock.Right);summary.Children.Add(badge);
        length.Text=$"{first.PixelHeight:N0} px";summary.Children.Add(length);root.Children.Add(Ui.CardOf(summary,16));
        var steps=Ui.Label("1. 대상 앱에서 아래로 조금 스크롤하세요.\n2. 이전 내용이 절반 이상 남도록 겹쳐 주세요.\n3. F8로 추가하고, 모두 담았으면 F9로 완료하세요.",12,Ui.Muted);steps.LineHeight=23;steps.Margin=new Thickness(0,18,0,15);root.Children.Add(steps);
        root.Children.Add(progress);status.Text="첫 화면을 담았습니다. 스크롤한 뒤 프레임을 추가하세요.";status.MinHeight=46;status.Margin=new Thickness(0,12,0,10);root.Children.Add(status);
        var row=new DockPanel();root.Children.Add(row);
        var stop=Ui.Button("취소",Close,style:"QuietButton");DockPanel.SetDock(stop,Dock.Right);row.Children.Add(stop);
        var finish=Ui.Button("완료 · F9",Finish,style:"PrimaryButton");finish.Margin=new Thickness(0,0,8,0);DockPanel.SetDock(finish,Dock.Right);row.Children.Add(finish);
        add=Ui.Button("추가 · F8",()=>{},"scroll");add.Margin=new Thickness(0,0,8,0);row.Children.Add(add);add.Click+=async (_,_)=>await AddFrameAsync();
        SourceInitialized+=(_,_)=>{
            var hwnd=new WindowInteropHelper(this).Handle;Native.SetWindowDisplayAffinity(hwnd,0x11);
            Native.SetWindowLongPtr(hwnd,-20,(nint)(Native.GetWindowLongPtr(hwnd,-20).ToInt64()|0x08000000L|0x80L));
        };
        Closed+=(_,_)=>{ended=true;cancel.Cancel();};
    }
    public void Message(string text)=>status.Text=text;
    public async Task AddFrameAsync() {
        if(busy || ended)return;busy=true;add.IsEnabled=false;progress.Visibility=Visibility.Visible;status.Foreground=Ui.Muted;status.Text="새 화면을 확인하고 있습니다…";
        try {
            var image=await capture(cancel.Token);
            if(ended)return;
            if(image.PixelWidth!=session.Width || image.PixelHeight!=session.FrameHeight)throw new InvalidOperationException("화면 크기가 바뀌었습니다. 새 캡처를 시작해 주세요.");
            var frame=Bytes(image);
            bool accepted=await Task.Run(()=>session.Append(frame),cancel.Token);
            if(ended)return;
            if(accepted){frames++;length.Text=$"{session.OutputHeight:N0} px";status.Foreground=Ui.Brush("SuccessBrush");status.Text=$"{frames}개 화면을 이어 붙였습니다. 더 스크롤하거나 F9로 완료하세요.";}
            else {status.Foreground=Ui.Brush("WarningBrush");status.Text="겹치는 내용을 확인하지 못해 추가하지 않았습니다. 조금 덜 스크롤한 뒤 다시 시도하세요.";}
        } catch(OperationCanceledException){}
        catch(Exception error){if(!ended)status.Text=error.Message;}
        finally {busy=false;add.IsEnabled=true;progress.Visibility=Visibility.Collapsed;if(finishAfterFrame && !ended)Finish();}
    }
    public void Finish() {
        if(ended)return;
        if(busy){finishAfterFrame=true;status.Text="현재 프레임 검사를 마치고 완료합니다…";return;}
        var bytes=session.ToArray();var image=BitmapSource.Create(session.Width,session.OutputHeight,96,96,PixelFormats.Bgra32,null,bytes,session.Width*4);image.Freeze();
        ended=true;Close();Finished?.Invoke(image);
    }
    private static byte[] Bytes(BitmapSource source) {
        var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);var bytes=new byte[checked(source.PixelWidth*source.PixelHeight*4)];converted.CopyPixels(bytes,source.PixelWidth*4,0);return bytes;
    }
}
