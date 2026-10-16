using System.Runtime.InteropServices;
using System.Text;

namespace Capture.Windows.Platform;

internal static class Native
{
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    public const int WM_HOTKEY=0x0312, WM_APP_TRAY=0x8001, WM_LBUTTONUP=0x0202, WM_RBUTTONUP=0x0205;
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; public int Width=>Right-Left; public int Height=>Bottom-Top; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct MONITORINFOEX {
        public int Size; public RECT Monitor,Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
    }
    public delegate bool MonitorEnum(nint monitor,nint dc,ref RECT bounds,nint data);
    public delegate bool WindowEnum(nint window,nint data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint dc,nint clip,MonitorEnum callback,nint data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfo(nint monitor,ref MONITORINFOEX info);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT p,uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd,out RECT bounds);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(WindowEnum callback,nint data);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd,out uint processId);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(nint hwnd,StringBuilder text,int max);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd,int attr,out int value,int size);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(nint hwnd,uint affinity);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint monitor,int type,out uint x,out uint y);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool RegisterHotKey(nint hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd,int id);
    [DllImport("user32.dll")] public static extern nint LoadIcon(nint instance,nint icon);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] public static extern bool Shell_NotifyIcon(uint message,ref NOTIFYICONDATA data);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct NOTIFYICONDATA {
        public int Size; public nint Hwnd; public uint Id,Flags,Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    public static IReadOnlyList<DisplayInfo> Displays() {
        var result=new List<DisplayInfo>();
        EnumDisplayMonitors(0,0,(nint m,nint dc,ref RECT b,nint d)=>{
            var info=new MONITORINFOEX { Size=Marshal.SizeOf<MONITORINFOEX>(),Device="" };
            if(GetMonitorInfo(m,ref info)) result.Add(new(m,info.Monitor,info.Work,info.Device));
            return true;
        },0);
        return result;
    }
    public static DisplayInfo PointerDisplay() {
        GetCursorPos(out var point); var handle=MonitorFromPoint(point,2);
        return Displays().First(d=>d.Handle==handle);
    }
    public static IReadOnlyList<PickableWindow> Windows() {
        var list=new List<PickableWindow>();
        EnumWindows((hwnd,_)=>{
            GetWindowThreadProcessId(hwnd,out var pid);
            if(pid==Environment.ProcessId || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            DwmGetWindowAttribute(hwnd,14,out var cloaked,4);
            if(cloaked!=0 || !GetWindowRect(hwnd,out var r) || r.Width<40 || r.Height<30) return true;
            var text=new StringBuilder(512); GetWindowText(hwnd,text,text.Capacity);
            if(text.Length>0) list.Add(new(hwnd,r,text.ToString()));
            return true;
        },0);
        return list;
    }
}
internal sealed record DisplayInfo(nint Handle,Native.RECT Bounds,Native.RECT Work,string Name);
internal sealed record PickableWindow(nint Handle,Native.RECT Bounds,string Title);
