using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capture.Windows.Platform;

/// <summary>On-demand SDR desktop snapshot. No continuous screen cache or capture session.</summary>
internal static class GdiSnapshot
{
    internal static CapturedFrame Capture(int x,int y,int width,int height,CancellationToken cancellationToken)=>
        CaptureFromDc(x,y,width,height,cancellationToken,0);

    private static CapturedFrame CaptureFromDc(int x,int y,int width,int height,CancellationToken cancellationToken,nint sourceDc)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int stride=checked(width*4),length=checked(stride*height);
        if(width<=0 || height<=0)throw new InvalidOperationException("The monitor has no visible pixels.");
        nint screen=0,memory=0,bitmap=0,previous=0;
        try {
            screen=sourceDc==0?GetDC(0):sourceDc;if(screen==0)throw Failure("GetDC");
            memory=CreateCompatibleDC(screen);if(memory==0)throw Failure("CreateCompatibleDC");
            var info=new BitmapInfo { Header=new BitmapInfoHeader { Size=(uint)Marshal.SizeOf<BitmapInfoHeader>(),Width=width,Height=-height,Planes=1,BitCount=32,SizeImage=(uint)length } };
            bitmap=CreateDIBSection(screen,ref info,0,out var pixels,0,0);
            if(bitmap==0 || pixels==0)throw Failure("CreateDIBSection");
            previous=SelectObject(memory,bitmap);
            if(previous==0 || previous==(nint)(-1))throw Failure("SelectObject");
            // CAPTUREBLT includes layered windows. The DIB is top-down and in physical pixels.
            if(!BitBlt(memory,0,0,width,height,screen,x,y,0x00CC0020|0x40000000))throw Failure("BitBlt");
            if(!GdiFlush())throw Failure("GdiFlush");
            cancellationToken.ThrowIfCancellationRequested();
            // GDI's 32-bit BI_RGB fourth byte is undefined, not alpha. Bgr32 makes
            // WPF ignore it; conversion produces owned, opaque BGRA32 for exports.
            var bgr=BitmapSource.Create(width,height,96,96,PixelFormats.Bgr32,null,pixels,length,stride);
            var image=new FormatConvertedBitmap(bgr,PixelFormats.Bgra32,null,0);
            image.Freeze();
            return new CapturedFrame(image,x,y,"GDI BitBlt · SDR (BGRA8)");
        } finally {
            // Never delete a bitmap while selected in a DC. Free all native resources
            // on the same worker thread, including failures and cancellation.
            if(memory!=0 && previous!=0 && previous!=(nint)(-1))SelectObject(memory,previous);
            if(bitmap!=0)DeleteObject(bitmap);
            if(memory!=0)DeleteDC(memory);
            if(screen!=0 && sourceDc==0)ReleaseDC(0,screen);
        }
    }
    /// <summary>Exercises native DIB/BitBlt ownership without accessing the user's screen.</summary>
    internal static void RunSelfTest()
    {
        nint dc=0,bitmap=0,previous=0;
        CapturedFrame? frame=null;
        // Deliberately transparent/garbage high bytes: 32-bit BI_RGB must be opaque.
        byte[] expected=[1,2,3,0, 4,5,6,17, 7,8,9,0, 10,11,12,99];
        try {
            dc=CreateCompatibleDC(0);if(dc==0)throw Failure("fixture CreateCompatibleDC");
            var info=new BitmapInfo {Header=new BitmapInfoHeader {Size=(uint)Marshal.SizeOf<BitmapInfoHeader>(),Width=2,Height=-2,Planes=1,BitCount=32,SizeImage=16}};
            bitmap=CreateDIBSection(0,ref info,0,out var pixels,0,0);if(bitmap==0 || pixels==0)throw Failure("fixture CreateDIBSection");
            previous=SelectObject(dc,bitmap);if(previous==0 || previous==(nint)(-1))throw Failure("fixture SelectObject");
            Marshal.Copy(expected,0,pixels,expected.Length);
            frame=CaptureFromDc(0,0,2,2,CancellationToken.None,dc);
        } finally {
            if(dc!=0 && previous!=0 && previous!=(nint)(-1))SelectObject(dc,previous);
            if(bitmap!=0)DeleteObject(bitmap);
            if(dc!=0)DeleteDC(dc);
        }
        // Read after BOTH native source and destination resources were freed.
        byte[] actual=new byte[16];frame!.Image.CopyPixels(actual,8,0);
        for(int i=0;i<actual.Length;i++) {
            byte wanted=i%4==3?(byte)255:expected[i];
            if(actual[i]!=wanted)throw new InvalidOperationException($"GDI fixture pixel byte {i}: expected {wanted}, received {actual[i]}.");
        }
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try {CaptureFromDc(0,0,2,2,cancelled.Token,0);throw new InvalidOperationException("Cancelled GDI capture did not stop.");}
        catch(OperationCanceledException) {}
    }
    private static Win32Exception Failure(string operation)=>new(Marshal.GetLastWin32Error(),$"{operation} failed while taking a desktop snapshot.");
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size;public int Width,Height;public ushort Planes,BitCount;public uint Compression,SizeImage;public int XPelsPerMeter,YPelsPerMeter;public uint ClrUsed,ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header;public uint Color; }
    [DllImport("user32.dll",SetLastError=true)] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern nint CreateDIBSection(nint dc,ref BitmapInfo info,uint usage,out nint pixels,nint section,uint offset);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern nint SelectObject(nint dc,nint value);
    [DllImport("gdi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(nint destination,int x,int y,int width,int height,nint source,int sourceX,int sourceY,uint operation);
    [DllImport("gdi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
