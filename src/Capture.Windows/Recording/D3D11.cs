using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;

namespace Capture.Windows.Recording;

/// <summary>
/// The few Direct3D 11 calls recording needs, without a wrapper package: a video-capable device shared by
/// capture and encoder, and a GPU copy of a region into a texture the encoder owns.
/// </summary>
internal sealed unsafe class D3D11 : IDisposable
{
    private static readonly Guid AccessId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), TextureId = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C"),
        DxgiDeviceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C"), DxgiSurfaceId = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC"), MultithreadId = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    private readonly nint device, context;
    private readonly object gate = new();
    public IDirect3DDevice Device { get; }

    private D3D11(nint device, nint context, IDirect3DDevice winrt) { this.device = device; this.context = context; Device = winrt; }

    public static D3D11 Create()
    {
        nint device = 0, context = 0, dxgi = 0, inspectable = 0;
        try
        {
            // BGRA support, plus video support so the encoder can use the same GPU textures.
            int result = D3D11CreateDevice(0, 1, 0, 0x20 | 0x800, 0, 0, 7, out device, out _, out context);
            if (result < 0) Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context));
            if (Marshal.QueryInterface(device, in MultithreadId, out var multithread) >= 0)
            {
                // Capture callbacks and the encoder use the immediate context from different threads.
                ((delegate* unmanaged[Stdcall]<nint, int, int>)(*(void***)multithread)[5])(multithread, 1);
                Marshal.Release(multithread);
            }
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in DxgiDeviceId, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            var created = new D3D11(device, context, WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable));
            device = context = 0;
            return created;
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (context != 0) Marshal.Release(context);
            if (device != 0) Marshal.Release(device);
        }
    }

    /// <summary>Copies a region of <paramref name="source"/> into a new BGRA texture of <paramref name="width"/> × <paramref name="height"/>.</summary>
    public IDirect3DSurface CopyRegion(IDirect3DSurface source, int x, int y, int width, int height, int sourceWidth, int sourceHeight)
    {
        nint sourceTexture = 0, texture = 0, surface = 0, inspectable = 0;
        try
        {
            sourceTexture = Texture(source);
            var description = new TextureDescription { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, BindFlags = 0x20 | 0x8 };
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, TextureDescription*, nint, nint*, int>)(*(void***)device)[5])(device, &description, 0, &texture));
            // Clip to the source so a display change cannot make the GPU read outside the frame.
            int left = Math.Clamp(x, 0, sourceWidth), top = Math.Clamp(y, 0, sourceHeight);
            var box = new Box { Left = (uint)left, Top = (uint)top, Front = 0, Right = (uint)Math.Min(sourceWidth, left + width), Bottom = (uint)Math.Min(sourceHeight, top + height), Back = 1 };
            if (box.Right > box.Left && box.Bottom > box.Top)
                lock (gate) ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)(*(void***)context)[46])(context, texture, 0, 0, 0, 0, sourceTexture, 0, &box);
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(texture, in DxgiSurfaceId, out surface));
            Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(surface, out inspectable));
            return WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (surface != 0) Marshal.Release(surface);
            if (texture != 0) Marshal.Release(texture);
            if (sourceTexture != 0) Marshal.Release(sourceTexture);
        }
    }

    private static nint Texture(IDirect3DSurface surface)
    {
        var abi = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in AccessId, out var access));
            try
            {
                nint texture; var id = TextureId;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)access)[3])(access, &id, &texture));
                return texture;
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(abi); }
    }

    public void Dispose()
    {
        Device.Dispose();
        Marshal.Release(context);
        Marshal.Release(device);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription { public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint graphicsDevice);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint surface, out nint graphicsSurface);
}
