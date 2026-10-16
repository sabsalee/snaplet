using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Capture.Windows.Platform;

/// <summary>Owned, frozen SDR pixels and their physical desktop origin.</summary>
public sealed record CapturedFrame(BitmapSource Image, int X, int Y, string Engine)
{
    public double CaptureMilliseconds {get;init;}
    public string? FallbackReason {get;init;}
}

/// <summary>
/// On-demand GDI desktop snapshots with explicit WGC fallback and WGC window capture.
/// WGC uses a free-threaded frame pool and an owned CPU copy. BGRA8 output is SDR; HDR tone mapping
/// is not implemented. Protected content and secure desktops cannot be guaranteed.
/// </summary>
public sealed class CaptureEngine
{
    private const string EngineName = "WGC · SDR (BGRA8)";
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    public Task<CapturedFrame> CaptureMonitorAsync(nint hmonitor, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var clock=Stopwatch.StartNew();
            var bounds = GetMonitorBounds(hmonitor);
            // WGC remains selectable for A/B comparison and for explicit fallback.
            // "gdi" deliberately fails visibly instead of concealing a GDI failure.
            var mode=Environment.GetEnvironmentVariable("SNAPLET_CAPTURE_ENGINE")?.ToLowerInvariant();
            CapturedFrame result;
            if(mode=="wgc")result=await CaptureAsync(hmonitor,false,bounds.Left,bounds.Top,cancellationToken).ConfigureAwait(false);
            else {
                try {result=GdiSnapshot.Capture(bounds.Left,bounds.Top,bounds.Right-bounds.Left,bounds.Bottom-bounds.Top,cancellationToken);}
                catch(Win32Exception error) when(mode!="gdi") {
                    cancellationToken.ThrowIfCancellationRequested();
                    result=(await CaptureAsync(hmonitor,false,bounds.Left,bounds.Top,cancellationToken).ConfigureAwait(false)) with {FallbackReason=$"GDI Win32 error {error.NativeErrorCode}"};
                }
            }
            var after = GetMonitorBounds(hmonitor);
            if (bounds.Left != after.Left || bounds.Top != after.Top || bounds.Right != after.Right || bounds.Bottom != after.Bottom ||
                result.Image.PixelWidth != bounds.Right - bounds.Left || result.Image.PixelHeight != bounds.Bottom - bounds.Top)
                throw new InvalidOperationException("The display configuration changed during capture. Please capture again.");
            return result with {CaptureMilliseconds=clock.Elapsed.TotalMilliseconds};
        }, cancellationToken);

    public Task<CapturedFrame> CaptureWindowAsync(nint hwnd, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var clock=Stopwatch.StartNew();
            if (!IsWindow(hwnd) || IsIconic(hwnd))
                throw new InvalidOperationException("The target window is closed or minimized. Restore it before capturing.");
            // WGC excludes invisible resize borders; DWM extended bounds share its origin.
            if (DwmGetWindowAttribute(hwnd, 9, out NativeRect bounds, Marshal.SizeOf<NativeRect>()) < 0 &&
                !GetWindowRect(hwnd, out bounds))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return (await CaptureAsync(hwnd, true, bounds.Left, bounds.Top, cancellationToken).ConfigureAwait(false)) with {CaptureMilliseconds=clock.Elapsed.TotalMilliseconds};
        }, cancellationToken);

    public Task<CapturedFrame> CaptureDesktopAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var clock=Stopwatch.StartNew();
            var monitors = new List<nint>();
            MonitorEnum callback = (nint monitor, nint _, ref NativeRect rect, nint data) =>
            {
                monitors.Add(monitor);
                return true;
            };
            if (!EnumDisplayMonitors(0, 0, callback, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (monitors.Count == 0) throw new InvalidOperationException("No active monitors are available.");
            var captures = await Task.WhenAll(monitors.Select(m => CaptureMonitorAsync(m, cancellationToken))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            int left = captures.Min(c => c.X), top = captures.Min(c => c.Y);
            int width = checked(captures.Max(c => c.X + c.Image.PixelWidth) - left);
            int height = checked(captures.Max(c => c.Y + c.Image.PixelHeight) - top);
            int stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            // Opaque black for gaps between monitors in irregular arrangements.
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            foreach (var capture in captures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int offset = checked((capture.Y - top) * stride + (capture.X - left) * 4);
                capture.Image.CopyPixels(Int32Rect.Empty, pixels, stride, offset);
            }
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            image.Freeze();
            return new CapturedFrame(image, left, top, string.Join(" + ",captures.Select(c=>c.Engine).Distinct())) {CaptureMilliseconds=clock.Elapsed.TotalMilliseconds,FallbackReason=string.Join("; ",captures.Where(c=>c.FallbackReason is not null).Select(c=>c.FallbackReason))};
        }, cancellationToken);

    private static async Task<CapturedFrame> CaptureAsync(nint target, bool window, int x, int y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows Graphics Capture is unavailable on this system.");
        var item = CreateItem(target, window);
        if (item.Size.Width <= 0 || item.Size.Height <= 0)
            throw new InvalidOperationException("The capture target has no visible pixels.");
        using var device = CreateDevice();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        // Keep the system capture border: removing it requires a separate consent flow.
        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        Direct3D11CaptureFrame? frame = null;
        bool stopped = false;
        void OnFrame(Direct3D11CaptureFramePool sender, object args)
        {
            lock (gate)
            {
                if (stopped || frame is not null) return;
                try
                {
                    frame = sender.TryGetNextFrame();
                    if (frame is not null) arrived.TrySetResult(true);
                }
                catch (Exception error) { arrived.TrySetException(error); }
            }
        }
        void OnClosed(GraphicsCaptureItem sender, object args) =>
            arrived.TrySetException(new InvalidOperationException("The capture target closed before a frame arrived."));
        pool.FrameArrived += OnFrame;
        item.Closed += OnClosed;
        try
        {
            session.StartCapture();
            await arrived.Task.WaitAsync(FrameTimeout, cancellationToken).ConfigureAwait(false);
            var captured = frame ?? throw new InvalidOperationException("Capture returned no frame.");
            if (captured.ContentSize.Width <= 0 || captured.ContentSize.Height <= 0)
                throw new InvalidOperationException("The capture target became empty.");
            // A frame is owned until GPU -> CPU copying completes, including during cancellation.
            using var software = await SoftwareBitmap.CreateCopyFromSurfaceAsync(captured.Surface);
            cancellationToken.ThrowIfCancellationRequested();
            int width = captured.ContentSize.Width, height = captured.ContentSize.Height;
            if (width > software.PixelWidth || height > software.PixelHeight)
                throw new InvalidOperationException("The target resized during capture. Please capture again.");
            int surfaceStride = checked(software.PixelWidth * 4);
            var buffer = new global::Windows.Storage.Streams.Buffer(checked((uint)(surfaceStride * software.PixelHeight)));
            software.CopyToBuffer(buffer);
            var pixels = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            if (software.BitmapAlphaMode == BitmapAlphaMode.Ignore)
                for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
            // Respect the CPU bitmap's alpha metadata before exposing straight BGRA32
            // to clipboard/export and the desktop compositor.
            var sourceFormat = software.BitmapAlphaMode == BitmapAlphaMode.Premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;
            BitmapSource full = BitmapSource.Create(software.PixelWidth, software.PixelHeight, 96, 96, sourceFormat, null, pixels, surfaceStride);
            if (sourceFormat != PixelFormats.Bgra32) full = new FormatConvertedBitmap(full, PixelFormats.Bgra32, null, 0);
            BitmapSource image = width == software.PixelWidth && height == software.PixelHeight
                ? full : new CroppedBitmap(full, new Int32Rect(0, 0, width, height));
            image.Freeze();
            return new CapturedFrame(image, x, y, EngineName);
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException("No capture frame arrived within five seconds. Check that the desktop is unlocked and the target is visible.", error);
        }
        finally
        {
            lock (gate) stopped = true;
            pool.FrameArrived -= OnFrame;
            item.Closed -= OnClosed;
            frame?.Dispose();
        }
    }

    private static GraphicsCaptureItem CreateItem(nint target, bool window)
    {
        nint className = 0, factoryPointer = 0, itemPointer = 0;
        object? factoryObject = null;
        try
        {
            const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out className));
            var interopId = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref interopId, out factoryPointer));
            factoryObject = Marshal.GetObjectForIUnknown(factoryPointer);
            var factory = (IGraphicsCaptureItemInterop)factoryObject;
            var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            int result = window ? factory.CreateForWindow(target, ref itemId, out itemPointer)
                : factory.CreateForMonitor(target, ref itemId, out itemPointer);
            Marshal.ThrowExceptionForHR(result);
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            if (itemPointer != 0) Marshal.Release(itemPointer);
            if (factoryObject is not null && Marshal.IsComObject(factoryObject)) Marshal.ReleaseComObject(factoryObject);
            if (factoryPointer != 0) Marshal.Release(factoryPointer);
            if (className != 0) WindowsDeleteString(className);
        }
    }

    private static IDirect3DDevice CreateDevice()
    {
        nint native = 0, dxgi = 0, inspectable = 0;
        try
        {
            // D3D11_CREATE_DEVICE_BGRA_SUPPORT; hardware is required, no silent GDI fallback.
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out native, out _, 0));
            var dxgiId = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in dxgiId, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (native != 0) Marshal.Release(native);
        }
    }

    private static NativeRect GetMonitorBounds(nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.Monitor;
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint window, ref Guid iid, out nint item);
        [PreserveSig] int CreateForMonitor(nint monitor, ref Guid iid, out nint item);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorEnum(nint monitor, nint hdc, ref NativeRect rect, nint data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnum callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out NativeRect value, int size);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint className, ref Guid iid, out nint factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint graphicsDevice);
}
