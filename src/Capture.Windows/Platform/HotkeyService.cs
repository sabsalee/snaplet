using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Capture.Windows.Platform;

public sealed record HotkeyBinding(string Command,Key Key,ModifierKeys Modifiers)
{
    private string KeyLabel=>Key is >=Key.D0 and <=Key.D9?((int)Key-(int)Key.D0).ToString():Key.ToString();
    public override string ToString()=>$"{(Modifiers.HasFlag(ModifierKeys.Control)?"Ctrl+":"")}{(Modifiers.HasFlag(ModifierKeys.Alt)?"Alt+":"")}{(Modifiers.HasFlag(ModifierKeys.Shift)?"Shift+":"")}{KeyLabel}";
}
internal sealed class HotkeyService : IDisposable
{
    private readonly HwndSource source;
    private readonly Dictionary<int,HotkeyBinding> registered=[];
    private readonly HashSet<int> temporary=[];
    public event Action<string,long>? Pressed;
    public event Action? TrayClicked;
    public nint Handle=>source.Handle;
    public HotkeyService() {
        source=new HwndSource(new HwndSourceParameters("Snaplet command window") { Width=0,Height=0,WindowStyle=0 });
        source.AddHook(Hook);
    }
    public void Apply(IReadOnlyList<HotkeyBinding> bindings) {
        if(bindings.GroupBy(b=>(b.Key,b.Modifiers)).Any(g=>g.Count()>1)) throw new InvalidOperationException("같은 단축키를 두 작업에 지정할 수 없습니다.");
        var previous=registered.Values.ToArray(); Clear();
        try { Register(bindings); }
        catch { Clear(); Register(previous); throw; }
    }
    private void Register(IReadOnlyList<HotkeyBinding> bindings) {
        for(var i=0;i<bindings.Count;i++) {
            var b=bindings[i];
            if(b.Key==Key.F12 || b.Key==Key.None || b.Modifiers.HasFlag(ModifierKeys.Windows)) throw new InvalidOperationException("F12와 Windows 키 조합은 예약되어 사용할 수 없습니다.");
            uint m=0x4000;
            if(b.Modifiers.HasFlag(ModifierKeys.Alt))m|=1;
            if(b.Modifiers.HasFlag(ModifierKeys.Control))m|=2;
            if(b.Modifiers.HasFlag(ModifierKeys.Shift))m|=4;
            if(!Native.RegisterHotKey(source.Handle,i+1,m,(uint)KeyInterop.VirtualKeyFromKey(b.Key))) throw new Win32Exception(Marshal.GetLastWin32Error(),$"{b}: 다른 앱이 사용 중이어서 등록하지 못했습니다.");
            registered[i+1]=b;
        }
    }
    public void RegisterScrollKeys() {
        try {
            if(!Native.RegisterHotKey(source.Handle,100,0x4000,0x77))throw new InvalidOperationException("F8이 다른 앱에서 사용 중입니다. 진행 창의 프레임 추가 버튼을 사용하세요.");
            temporary.Add(100);
            if(!Native.RegisterHotKey(source.Handle,101,0x4000,0x78))throw new InvalidOperationException("F9가 다른 앱에서 사용 중입니다. 진행 창의 완료 버튼을 사용하세요.");
            temporary.Add(101);
        } catch(Exception error) {UnregisterScrollKeys();throw new InvalidOperationException("F8/F9 단축키를 등록하지 못했습니다. 진행 창의 프레임 추가·완료 버튼을 사용하세요.",error);}
    }
    public void UnregisterScrollKeys() {foreach(var id in temporary)Native.UnregisterHotKey(source.Handle,id);temporary.Clear();}
    private nint Hook(nint hwnd,int message,nint wparam,nint lparam,ref bool handled) {
        if(message==Native.WM_HOTKEY && registered.TryGetValue((int)wparam,out var b)) { handled=true; Pressed?.Invoke(b.Command,Stopwatch.GetTimestamp()); }
        else if(message==Native.WM_HOTKEY && temporary.Contains((int)wparam)) {handled=true;Pressed?.Invoke((int)wparam==100?"scroll-add":"scroll-finish",Stopwatch.GetTimestamp());}
        if(message==Native.WM_APP_TRAY && ((int)lparam==Native.WM_LBUTTONUP || (int)lparam==Native.WM_RBUTTONUP)) { handled=true; TrayClicked?.Invoke(); }
        return 0;
    }
    private void Clear() { foreach(var id in registered.Keys)Native.UnregisterHotKey(source.Handle,id); registered.Clear(); }
    public void Dispose() { UnregisterScrollKeys();Clear();source.RemoveHook(Hook);source.Dispose(); }
}
