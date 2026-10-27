using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Capture.Windows.Editor;

/// <summary>The editor follows the Windows app light/dark setting like the reference app follows the system appearance.
/// Dark uses the application theme as is; light overrides the shared brush keys on the editor window and its popups.</summary>
internal static class EditorTheme
{
    /// <summary>Test hook: render a fixed theme regardless of the runner's Windows setting.</summary>
    internal static bool? Override {get;set;}
    internal static bool SystemUsesDark()
    {
        if(Override is bool forced)return forced;
        try {
            using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light==0;
        } catch(Exception error) when(error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) {return false;}
    }
    private static ResourceDictionary? light;
    internal static ResourceDictionary Light=>light ??= Build(new() {
        ["PageBrush"]="#F3F3F5",["CardBrush"]="#FFFFFF",["RaisedBrush"]="#F4F4F6",["BorderBrush"]="#D8D8DE",
        ["TextBrush"]="#1D1D1F",["MutedBrush"]="#6E6E76",["AccentBrush"]="#0A64D8",["AccentFillBrush"]="#2F6FE0",
        ["HoverBrush"]="#E9E9ED",["HoverBorderBrush"]="#C6C6CE",["PressedBrush"]="#DCDCE2",["InputBrush"]="#FFFFFF",
        ["CheckBorderBrush"]="#A0A0AA",["HighlightBrush"]="#E5EDFB",["SelectedBrush"]="#D9E5FA",["TrackBrush"]="#C9C9D0",
        ["ThumbBrush"]="#FFFFFF",["ThumbBorderBrush"]="#A8A8B0",["ScrollThumbBrush"]="#BDBDC4",
        ["ToolbarBrush"]="#F6F6F7",["FloatingBrush"]="#FFFFFF",["FloatingBorderBrush"]="#DDDDE2",["SelectedToolBrush"]="#E2E2E7",
        ["WorkspaceBrush"]="#E7E7EA",["HandleBorderBrush"]="#8E8E93",
    });
    private static ResourceDictionary Build(Dictionary<string,string> colors)
    {
        var dictionary=new ResourceDictionary();
        foreach(var (key,value) in colors) {var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));brush.Freeze();dictionary[key]=brush;}
        return dictionary;
    }
    /// <summary>Applies the palette to a root that has no logical parent in the window, such as a popup or context menu.</summary>
    internal static T Themed<T>(T element,bool dark) where T:FrameworkElement
    {
        element.Resources.MergedDictionaries.Remove(Light);
        if(!dark)element.Resources.MergedDictionaries.Add(Light);
        return element;
    }
    internal static Color Ink(FrameworkElement scope)=>scope.TryFindResource("TextBrush") is SolidColorBrush brush?brush.Color:Colors.White;
}
