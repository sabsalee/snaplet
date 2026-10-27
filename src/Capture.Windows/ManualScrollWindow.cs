using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;
using Capture.Windows.Platform;
using Capture.Windows.UI;

namespace Capture.Windows;

/// <summary>
/// Downward scroll stitching. Manual mode: the user scrolls and adds frames (F8). Auto mode: Snaplet sends wheel notches to
/// the window under the selected area, adapts the step to the measured overlap, and stops at the end of the content,
/// at the size limit, or after repeated ambiguous frames. Sticky headers/footers are kept once.
/// </summary>
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
    private readonly Button? autoButton;
    private readonly Func<int,bool>? wheel;
    private bool auto;
    public event Action<BitmapSource>? Finished;
    public ManualScrollWindow(BitmapSource first,Func<CancellationToken,Task<BitmapSource>> captureFrame,Func<int,bool>? wheelNotches=null) {
        capture=captureFrame;wheel=wheelNotches;
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
        if(wheel!=null) {
            autoButton=Ui.Button("자동 스크롤",()=>{},"spark","PrimaryButton");autoButton.Margin=new Thickness(0,0,8,0);DockPanel.SetDock(autoButton,Dock.Right);
            row.Children.Insert(row.Children.IndexOf(finish)+1,autoButton);
            autoButton.Click+=async (_,_)=>{if(auto)StopAuto("자동 스크롤을 멈췄습니다. 수동으로 이어 가거나 F9로 완료하세요.");else await AutoAsync();};
        }
        SourceInitialized+=(_,_)=>{
            var hwnd=new WindowInteropHelper(this).Handle;Native.SetWindowDisplayAffinity(hwnd,0x11);
            Native.SetWindowLongPtr(hwnd,-20,(nint)(Native.GetWindowLongPtr(hwnd,-20).ToInt64()|0x08000000L|0x80L));
        };
        Closed+=(_,_)=>{ended=true;cancel.Cancel();};
    }
    public void Message(string text)=>status.Text=text;
    public bool IsAuto=>auto;
    public void StopAuto(string message){if(!auto)return;auto=false;status.Text=message;}
    /// <summary>Automatic scrolling loop. Each step waits for the page to repaint, captures, and appends only confident overlaps.</summary>
    public async Task AutoAsync()
    {
        if(wheel is null || auto || busy || ended)return;
        auto=true;if(autoButton!=null)autoButton.Content="중지";add.IsEnabled=false;
        int notches=3,ambiguous=0,unchanged=0;
        status.Foreground=Ui.Muted;status.Text="자동으로 스크롤하는 중… 중지하려면 버튼이나 F9(완료)를 누르세요.";
        try {
            while(auto && !ended) {
                if(!wheel(notches)){status.Text="Windows가 스크롤 입력을 막았습니다. 관리자 권한 앱이면 수동 모드를 사용하세요.";break;}
                await Task.Delay(280,cancel.Token);
                busy=true;
                try {
                    var image=await capture(cancel.Token);
                    if(ended)break;
                    if(image.PixelWidth!=session.Width || image.PixelHeight!=session.FrameHeight)throw new InvalidOperationException("화면 크기가 바뀌었습니다. 새 캡처를 시작해 주세요.");
                    var frame=Bytes(image);
                    bool accepted=await Task.Run(()=>session.Append(frame),cancel.Token);
                    if(accepted) {
                        frames++;ambiguous=0;unchanged=0;length.Text=$"{session.OutputHeight:N0} px";
                        // Keep roughly a third to two thirds of the frame moving per step.
                        var moved=session.LastMatch.Offset;
                        if(moved<session.FrameHeight/4 && notches<12)notches++;else if(moved>session.FrameHeight*2/3 && notches>1)notches--;
                        status.Foreground=Ui.Brush("SuccessBrush");status.Text=$"자동 스크롤 중 · {frames}개 화면";
                    } else if(session.ReachedEnd) {
                        if(++unchanged>=2){status.Foreground=Ui.Brush("SuccessBrush");status.Text="끝까지 담았습니다.";break;}
                    } else if(session.LastMatch.Accepted) {
                        status.Foreground=Ui.Brush("WarningBrush");status.Text="최대 길이에 도달해 멈췄습니다.";break;
                    } else {
                        notches=Math.Max(1,notches/2);
                        if(++ambiguous>=3){status.Foreground=Ui.Brush("WarningBrush");status.Text="겹치는 내용을 찾지 못해 멈췄습니다. 움직이는 요소가 없는 영역을 고르거나 수동 모드로 이어 가세요.";break;}
                    }
                } finally {busy=false;}
                if(finishAfterFrame)break;
            }
        } catch(OperationCanceledException){}
        catch(Exception error){if(!ended)status.Text=error.Message;}
        finally {
            bool finishNow=finishAfterFrame || (auto && frames>1 && !ended);
            auto=false;if(autoButton!=null)autoButton.Content="자동 스크롤";add.IsEnabled=true;
            if(finishNow && !ended)Finish();
        }
    }
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
