using System.IO;
using System.Windows.Media.Imaging;

namespace Capture.Windows;

/// <summary>
/// Optional on-disk copy of recent captures (originals only, PNG) so they survive restarts and updates.
/// Off by default because it writes screenshots to disk; turning it off deletes the folder.
/// </summary>
internal static class HistoryStore
{
    public const int Limit=12;
    public static string FolderPath=>Path.Combine(SettingsStore.DirectoryPath,"History");

    public static void Save(BitmapSource image,string? folder=null)
    {
        folder??=FolderPath;Directory.CreateDirectory(folder);
        ImageActions.Save(image,Path.Combine(folder,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")[..6]+".png"));
        Trim(folder);
    }
    /// <summary>Newest first. Unreadable files are skipped, never fatal.</summary>
    public static List<BitmapSource> Load(string? folder=null)
    {
        folder??=FolderPath;var result=new List<BitmapSource>();
        if(!Directory.Exists(folder))return result;
        foreach(var path in Directory.GetFiles(folder,"*.png").OrderByDescending(Path.GetFileName,StringComparer.Ordinal).Take(Limit)) {
            try{result.Add(ImageActions.Load(path));}catch(Exception error) when(error is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or ArgumentException){}
        }
        return result;
    }
    public static void Clear(string? folder=null)
    {
        folder??=FolderPath;
        try{if(Directory.Exists(folder))Directory.Delete(folder,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private static void Trim(string folder)
    {
        foreach(var old in Directory.GetFiles(folder,"*.png").OrderByDescending(Path.GetFileName,StringComparer.Ordinal).Skip(Limit)) {
            try{File.Delete(old);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
}
