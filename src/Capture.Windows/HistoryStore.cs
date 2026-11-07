using System.IO;
using System.Windows.Media.Imaging;
using Capture.Core;

namespace Capture.Windows;

/// <summary>
/// Optional on-disk copy of captures (originals only, PNG) so they survive restarts and updates.
/// Off by default because it writes screenshots to disk; turning it off deletes the folder.
/// Startup loads only the recent few; the gallery reads the rest as thumbnails.
/// </summary>
internal static class HistoryStore
{
    public const int Limit=12,DiskLimit=200;
    public static string FolderPath=>Path.Combine(SettingsStore.DirectoryPath,"History");

    public static string Save(BitmapSource image,string? folder=null)
    {
        folder??=FolderPath;Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,HistoryNames.Create(DateTime.UtcNow,Guid.NewGuid().ToString("N")[..6]));
        ImageActions.Save(image,path);
        Trim(folder);
        return path;
    }
    /// <summary>Kept captures, newest first, without reading their pixels. Files Snaplet did not name are ignored.</summary>
    public static List<(string Path,DateTime When)> Entries(string? folder=null)
    {
        folder??=FolderPath;var result=new List<(string,DateTime)>();
        if(!Directory.Exists(folder))return result;
        foreach(var path in Directory.GetFiles(folder,"*.png").OrderByDescending(Path.GetFileName,StringComparer.Ordinal))
            if(HistoryNames.TryParseUtc(Path.GetFileName(path),out var utc))result.Add((path,utc.ToLocalTime()));
        return result;
    }
    /// <summary>Newest first. Unreadable files are skipped, never fatal.</summary>
    public static List<BitmapSource> Load(string? folder=null,Action<BitmapSource,string,DateTime>? loaded=null)
    {
        var result=new List<BitmapSource>();
        foreach(var (path,when) in Entries(folder)) {
            if(result.Count==Limit)break;
            try{var image=ImageActions.Load(path);result.Add(image);loaded?.Invoke(image,path,when);}catch(Exception error) when(error is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or ArgumentException){}
        }
        return result;
    }
    /// <summary>A small frozen copy for the gallery, with the size of the original. Decoding at a reduced size keeps 200 captures cheap.</summary>
    public static (BitmapSource Image,int Width,int Height) Thumbnail(string path,int width)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var frame=BitmapFrame.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);int originalWidth=frame.PixelWidth,originalHeight=frame.PixelHeight;
        stream.Position=0;
        var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;
        if(originalWidth>width)image.DecodePixelWidth=width;
        image.EndInit();image.Freeze();
        return (image,originalWidth,originalHeight);
    }
    public static void Delete(string path)
    {
        try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    public static void Clear(string? folder=null)
    {
        folder??=FolderPath;
        try{if(Directory.Exists(folder))Directory.Delete(folder,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private static void Trim(string folder)
    {
        foreach(var old in Directory.GetFiles(folder,"*.png").OrderByDescending(Path.GetFileName,StringComparer.Ordinal).Skip(DiskLimit)) {
            try{File.Delete(old);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
}
