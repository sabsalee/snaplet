using System.IO;
using Microsoft.Win32;

namespace Capture.Windows.Platform;

/// <summary>Per-user start at sign-in through the HKCU Run key: no admin rights, removed with the value.</summary>
internal static class StartupRegistration
{
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run",Name="Snaplet";
    /// <summary>--background is informational: every start without arguments already stays in the tray.</summary>
    internal static string Command(string exe)=>$"\"{exe}\" --background";
    /// <summary>Writes or removes the Run value. Re-applied at startup so the path follows the installed executable.</summary>
    internal static bool Apply(bool enabled)
    {
        try {
            using var key=Registry.CurrentUser.CreateSubKey(RunKey,true);
            if(enabled && Environment.ProcessPath is string exe)key.SetValue(Name,Command(exe));
            else key.DeleteValue(Name,false);
            return true;
        } catch(Exception error) when(error is UnauthorizedAccessException or IOException or System.Security.SecurityException){return false;}
    }
}
