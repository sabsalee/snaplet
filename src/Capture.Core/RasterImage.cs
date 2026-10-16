namespace Capture.Core;

/// <summary>Owned immutable BGRA pixels. Document history shares this asset without copying bitmap data.</summary>
public sealed class RasterImage
{
    private readonly byte[] pixels;
    public int Width {get;}
    public int Height {get;}
    public RasterImage(int width,int height,ReadOnlySpan<byte> bgra)
    {
        if(width<=0 || height<=0 || (long)width*height>64_000_000 || (long)width*height*4!=bgra.Length)
            throw new ArgumentException("Image must contain at most 64 million BGRA pixels.");
        Width=width;Height=height;pixels=bgra.ToArray();
    }
    public byte[] CopyPixels()=>pixels.ToArray();
}
