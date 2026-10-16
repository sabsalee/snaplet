using Velopack;

namespace Capture.Windows;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Installer hooks must finish before WPF, mutexes, tray icons or capture devices are initialized.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app=new App();app.InitializeComponent();app.Run();
    }
}
