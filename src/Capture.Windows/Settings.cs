using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Capture.Windows.Platform;

namespace Capture.Windows;
public sealed record AppSettings
{
    public bool AutoUpdate {get;init;}=true;
    public bool CopyAfterCapture {get;init;}=true;
    public bool SaveAfterCapture {get;init;}
    public bool ShowPreview {get;init;}=true;
    public bool ClosePreviewOutside {get;init;}=true;
    public string SaveFolder {get;init;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),"Snaplet");
    public List<HotkeyBinding> Hotkeys {get;init;}=DefaultHotkeys();
    public static List<HotkeyBinding> DefaultHotkeys()=>[
        new("region",Key.D1,ModifierKeys.Control|ModifierKeys.Shift),
        new("window",Key.D2,ModifierKeys.Control|ModifierKeys.Shift),
        new("display",Key.D3,ModifierKeys.Control|ModifierKeys.Shift),
        new("delayed",Key.D4,ModifierKeys.Control|ModifierKeys.Shift),
        new("last",Key.D5,ModifierKeys.Control|ModifierKeys.Shift),
        new("repeat",Key.D6,ModifierKeys.Control|ModifierKeys.Shift)];
}
internal static class SettingsStore
{
    public static string DirectoryPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Snaplet");
    private static string FilePath=>Path.Combine(DirectoryPath,"settings.json");
    private static readonly JsonSerializerOptions Options=new() { WriteIndented=true,Converters={new JsonStringEnumConverter()} };
    public static AppSettings Load(out string? warning) {
        warning=null;
        if(!File.Exists(FilePath))return new();
        try {
            var s=JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath),Options)??throw new InvalidDataException();
            if(s.Hotkeys is null || s.Hotkeys.Count!=6 || s.Hotkeys.Any(b=>b is null) || s.Hotkeys.Select(x=>x.Command).Distinct().Count()!=6 || s.Hotkeys.Any(b=>!AppSettings.DefaultHotkeys().Any(d=>d.Command==b.Command)) || string.IsNullOrWhiteSpace(s.SaveFolder))throw new InvalidDataException();
            return s;
        } catch(Exception e) when(e is JsonException or IOException or InvalidDataException or ArgumentException) {
            warning="설정 파일을 읽지 못해 기본 설정으로 시작합니다. 원본 파일은 그대로 보존했습니다."; return new();
        }
    }
    public static void Save(AppSettings settings) {
        Directory.CreateDirectory(DirectoryPath);
        var temp=FilePath+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(settings,Options));
        File.Move(temp,FilePath,true);
    }
}
