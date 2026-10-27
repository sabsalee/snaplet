using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capture.Windows.Platform;

/// <summary>
/// The visible mouse pointer at the moment a capture starts, as an owned image placed at its hotspot.
/// GDI and WGC snapshots exclude the pointer, so it is composited afterwards when the user asks for it.
/// </summary>
internal sealed record CursorSnapshot(BitmapSource Image,int Left,int Top)
{
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int Size; public int Flags; public nint Cursor; public Native.POINT Position; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool IsIcon; public int HotspotX; public int HotspotY; public nint Mask; public nint Color; }
    [DllImport("user32.dll",SetLastError=true)] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool GetIconInfo(nint icon,out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    private const int CursorShowing=1;

    /// <summary>Returns null when the pointer is hidden (e.g. while typing or in full-screen video) or cannot be read.</summary>
    public static CursorSnapshot? Capture()
    {
        var info=new CURSORINFO{Size=Marshal.SizeOf<CURSORINFO>()};
        if(!GetCursorInfo(ref info) || (info.Flags&CursorShowing)==0 || info.Cursor==0)return null;
        var icon=CopyIcon(info.Cursor);
        if(icon==0)return null;
        try {
            if(!GetIconInfo(icon,out var iconInfo))return null;
            try {
                var image=Imaging.CreateBitmapSourceFromHIcon(icon,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return new(image,info.Position.X-iconInfo.HotspotX,info.Position.Y-iconInfo.HotspotY);
            } finally {
                if(iconInfo.Mask!=0)DeleteObject(iconInfo.Mask);
                if(iconInfo.Color!=0)DeleteObject(iconInfo.Color);
            }
        } catch(Exception error) when(error is COMException or ArgumentException or ExternalException) {
            return null;
        } finally {DestroyIcon(icon);}
    }

    /// <summary>Draws the pointer onto a captured frame when it overlaps it; otherwise returns the frame unchanged.</summary>
    public CapturedFrame DrawOnto(CapturedFrame frame)
    {
        var bounds=new Int32Rect(frame.X,frame.Y,frame.Image.PixelWidth,frame.Image.PixelHeight);
        if(Left>=bounds.X+bounds.Width || Top>=bounds.Y+bounds.Height || Left+Image.PixelWidth<=bounds.X || Top+Image.PixelHeight<=bounds.Y)return frame;
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen()) {
            dc.DrawImage(frame.Image,new Rect(0,0,bounds.Width,bounds.Height));
            dc.DrawImage(Image,new Rect(Left-bounds.X,Top-bounds.Y,Image.PixelWidth,Image.PixelHeight));
        }
        var output=new RenderTargetBitmap(bounds.Width,bounds.Height,96,96,PixelFormats.Pbgra32);
        output.Render(visual);output.Freeze();
        return frame with {Image=output};
    }
}
