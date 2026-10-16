using System.Runtime.InteropServices;

namespace Capture.Windows.Platform;
internal sealed class TrayIcon : IDisposable
{
    private Native.NOTIFYICONDATA data;
    public TrayIcon(nint hwnd) {
        data=new() { Size=Marshal.SizeOf<Native.NOTIFYICONDATA>(),Hwnd=hwnd,Id=1,Flags=1|2|4,Callback=Native.WM_APP_TRAY,Icon=Native.LoadIcon(0,(nint)32516),Tip="Snaplet · 빠른 캡처",Info="",InfoTitle="" };
        if(!Native.Shell_NotifyIcon(0,ref data)) throw new InvalidOperationException("트레이 아이콘을 만들지 못했습니다.");
    }
    public void Dispose()=>Native.Shell_NotifyIcon(2,ref data);
}
