using System.IO;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows;

/// <summary>Saves and opens ".snaplet" projects. A save never leaves a half-written file behind.</summary>
internal static class ProjectFileIO
{
    internal const string DialogFilter="Snaplet 프로젝트 (*.snaplet)|*.snaplet";
    internal static bool IsProject(string path)=>string.Equals(Path.GetExtension(path),ProjectFile.Extension,StringComparison.OrdinalIgnoreCase);

    internal static void Save(BitmapSource source,EditableDocument document,string path)
    {
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));
        using var image=new MemoryStream();encoder.Save(image);
        var temporary=path+".tmp";
        try {
            using(var file=File.Create(temporary))ProjectFile.Write(file,image.GetBuffer().AsSpan(0,(int)image.Length),document);
            File.Move(temporary,path,true);
        } finally {
            try{File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
    /// <exception cref="InvalidDataException">The message can be shown to the user.</exception>
    internal static (BitmapSource Image,EditableDocument Document) Load(string path)
    {
        ProjectContents contents;
        using(var file=File.OpenRead(path))contents=ProjectFile.Read(file);
        BitmapSource image;
        try {
            using var stream=new MemoryStream(contents.SourceImage);
            // The project wrote this PNG itself, so its pixels are taken as stored, without colour-profile conversion.
            BitmapSource decoded=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat|BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.OnLoad).Frames[0];
            // Captures are premultiplied; matching that keeps the reopened document compositing like the original.
            image=decoded.Format==System.Windows.Media.PixelFormats.Pbgra32?decoded:new FormatConvertedBitmap(decoded,System.Windows.Media.PixelFormats.Pbgra32,null,0);image.Freeze();
        } catch(Exception error) when(error is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException) {throw new InvalidDataException("프로젝트의 원본 이미지를 읽을 수 없습니다.",error);}
        if(image.PixelWidth!=contents.Document.Width || image.PixelHeight!=contents.Document.Height)throw new InvalidDataException("프로젝트의 원본 이미지 크기가 맞지 않습니다.");
        return (image,contents.Document);
    }
}
