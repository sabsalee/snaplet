using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Capture.Windows.Platform;

namespace Capture.Windows;

/// <summary>What Esc does when it closes an editor, matching the original's "action when hiding with Esc".</summary>
public enum EscapeAction { None, Copy, Save }

/// <summary>How window captures are framed: as captured, with a transparent margin and soft shadow, or on a gradient backdrop.</summary>
public enum WindowCaptureStyle { Plain, Shadow, Backdrop }

public sealed record AppSettings
{
    /// <summary>Bumped whenever stored fields change meaning. Older files are migrated in <see cref="SettingsStore.Normalize"/>.</summary>
    public const int CurrentSchema = 2;
    public int SchemaVersion {get;init;}=CurrentSchema;
    public bool AutoUpdate {get;init;}=true;
    public bool CopyAfterCapture {get;init;}=true;
    public bool SaveAfterCapture {get;init;}
    public bool ShowPreview {get;init;}=true;
    public bool ClosePreviewOutside {get;init;}=true;
    /// <summary>Keep the drawn area adjustable (handles, arrow keys, smart fit) until Enter instead of capturing on mouse release.</summary>
    public bool AdjustSelectionBeforeCapture {get;init;}
    /// <summary>Show the pixel loupe and crosshair in the selection overlay.</summary>
    public bool ShowLoupe {get;init;}=true;
    /// <summary>Join recognised lines with spaces before copying text.</summary>
    public bool OcrJoinLines {get;init;}
    public EscapeAction EscapeAction {get;init;}=EscapeAction.None;
    /// <summary>Composite the mouse pointer (as it was when the capture started) into the image.</summary>
    public bool IncludeCursor {get;init;}
    public WindowCaptureStyle WindowStyle {get;init;}=WindowCaptureStyle.Plain;
    public string SaveFolder {get;init;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),"Snaplet");
    public List<HotkeyBinding> Hotkeys {get;init;}=DefaultHotkeys();

    /// <summary>Every command that can own a global shortcut, in display order. New commands start unassigned to avoid stealing keys from other apps.</summary>
    public static readonly IReadOnlyList<(string Command,string Name)> Commands=[
        ("region","영역 캡처"),("window","활성 창 캡처"),("display","현재 화면 캡처"),("delayed","3초 후 화면 캡처"),
        ("last","최근 캡처 편집"),("repeat","직전 영역 반복"),("desktop","모든 화면 캡처"),("pick-window","창 선택 캡처"),
        ("scroll","스크롤 캡처"),("ocr","텍스트·QR 바로 인식"),("home","Snaplet 열기")];
    public static List<HotkeyBinding> DefaultHotkeys()=>[
        new("region",Key.D1,ModifierKeys.Control|ModifierKeys.Shift),
        new("window",Key.D2,ModifierKeys.Control|ModifierKeys.Shift),
        new("display",Key.D3,ModifierKeys.Control|ModifierKeys.Shift),
        new("delayed",Key.D4,ModifierKeys.Control|ModifierKeys.Shift),
        new("last",Key.D5,ModifierKeys.Control|ModifierKeys.Shift),
        new("repeat",Key.D6,ModifierKeys.Control|ModifierKeys.Shift),
        new("desktop",Key.None,ModifierKeys.None),
        new("pick-window",Key.None,ModifierKeys.None),
        new("scroll",Key.None,ModifierKeys.None),
        new("ocr",Key.None,ModifierKeys.None),
        new("home",Key.None,ModifierKeys.None)];
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
            return Parse(File.ReadAllText(FilePath),out warning);
        } catch(Exception e) when(e is JsonException or IOException or InvalidDataException or ArgumentException or NotSupportedException) {
            warning="설정 파일을 읽지 못해 기본 설정으로 시작합니다. 원본 파일은 그대로 보존했습니다."; return new();
        }
    }

    /// <summary>Parses stored JSON and migrates it. Separate from file access so the self-test can exercise old files.</summary>
    internal static AppSettings Parse(string json,out string? warning) {
        var stored=JsonSerializer.Deserialize<AppSettings>(json,Options)??throw new InvalidDataException();
        return Normalize(stored,out warning);
    }

    /// <summary>
    /// Keeps every valid stored binding, adds commands introduced by newer versions (unassigned unless the default key is free),
    /// drops unknown commands and duplicates, and repairs missing fields. Earlier versions discarded the whole file when the
    /// shortcut count changed; this keeps user choices across updates.
    /// </summary>
    internal static AppSettings Normalize(AppSettings stored,out string? warning) {
        warning=null;
        var defaults=AppSettings.DefaultHotkeys();
        var known=AppSettings.Commands.Select(c=>c.Command).ToHashSet();
        var kept=new Dictionary<string,HotkeyBinding>();
        var used=new HashSet<(Key,ModifierKeys)>();
        bool dropped=false;
        foreach(var binding in stored.Hotkeys??[]) {
            if(binding is null || binding.Command is null || !known.Contains(binding.Command) || kept.ContainsKey(binding.Command)){dropped=true;continue;}
            if(binding.Key!=Key.None && !used.Add((binding.Key,binding.Modifiers))){kept[binding.Command]=binding with{Key=Key.None,Modifiers=ModifierKeys.None};dropped=true;continue;}
            kept[binding.Command]=binding.Key==Key.None?binding with{Modifiers=ModifierKeys.None}:binding;
        }
        var hotkeys=new List<HotkeyBinding>();
        foreach(var (command,_) in AppSettings.Commands) {
            if(kept.TryGetValue(command,out var binding)){hotkeys.Add(binding);continue;}
            var fallback=defaults.First(d=>d.Command==command);
            hotkeys.Add(fallback.Key!=Key.None && used.Add((fallback.Key,fallback.Modifiers))?fallback:fallback with{Key=Key.None,Modifiers=ModifierKeys.None});
        }
        if(dropped)warning="일부 단축키 설정이 중복되거나 알 수 없어 해제했습니다. 설정 → 단축키에서 확인해 주세요.";
        var folder=string.IsNullOrWhiteSpace(stored.SaveFolder) || !Path.IsPathFullyQualified(stored.SaveFolder)?new AppSettings().SaveFolder:stored.SaveFolder;
        var escape=Enum.IsDefined(stored.EscapeAction)?stored.EscapeAction:EscapeAction.None;
        var windowStyle=Enum.IsDefined(stored.WindowStyle)?stored.WindowStyle:WindowCaptureStyle.Plain;
        return stored with{SchemaVersion=AppSettings.CurrentSchema,Hotkeys=hotkeys,SaveFolder=folder,EscapeAction=escape,WindowStyle=windowStyle};
    }

    public static void Save(AppSettings settings) {
        Directory.CreateDirectory(DirectoryPath);
        var temp=FilePath+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(settings,Options));
        File.Move(temp,FilePath,true);
    }
}
