using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Capture.Windows.Updates;

/// <summary>Opt-in CI fixture: real installed Update.exe, checksum rejection, replacement, restart and settings preservation.</summary>
internal static class UpdateIntegration
{
    private sealed record Report(string Status,string Version,string Target,string Feed,string Marker,string SettingsHash,string ExpectedMarker,bool ChecksumRejected=false,string? Error=null);
    internal static async Task RunAsync(string[] args,Action<int> shutdown)
    {
        if(Environment.GetEnvironmentVariable("SNAPLET_UPDATE_TESTS")!="1"){shutdown(2);return;}
        var reportPath=args[0]=="--verify-update"?args[1]:args[3];Report? report=null;
        try {
            if(args[0]=="--verify-update") {
                report=JsonSerializer.Deserialize<Report>(File.ReadAllText(reportPath))!;
                var backend=new VelopackBackend(Manager(report.Feed));
                Require(backend.IsInstalled && backend.CurrentVersion==report.Target,"updated process has the target installed version");
                Require(!File.Exists(Path.Combine(AppContext.BaseDirectory,"baseline-only.marker")),"old application directory was replaced, including removal of stale files");
                Require(File.ReadAllText(report.Marker)==report.ExpectedMarker,"settings-directory marker survives update and restart");
                Require(SettingsHash()==report.SettingsHash,"existing settings file remains byte-for-byte unchanged");
                Require(report.ChecksumRejected,"corrupted update was rejected before the real update");
                File.Delete(report.Marker);Write(reportPath,report with {Status="passed",Version=backend.CurrentVersion});shutdown(0);return;
            }
            var manager=Manager(args[1]);var expected=args[5];
            Require(manager.IsInstalled && manager.CurrentVersion?.ToString()==args[4],"test starts from the baseline installation");
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            var marker=Path.Combine(SettingsStore.DirectoryPath,"update-check-"+Guid.NewGuid().ToString("N")+".txt");var markerText=Guid.NewGuid().ToString();File.WriteAllText(marker,markerText);
            report=new("checking",manager.CurrentVersion!.ToString(),expected,args[1],marker,SettingsHash(),markerText);Write(reportPath,report);
            var badCache=Path.Combine(Path.GetDirectoryName(reportPath)!,"bad-update-cache");Directory.CreateDirectory(badCache);
            var badManager=new UpdateManager(new SimpleFileSource(new DirectoryInfo(args[2])),Options(),new TestVelopackLocator(UpdateDistribution.PackId,args[4],badCache));
            using(var corrupt=new UpdateService(new VelopackBackend(badManager))) {
                await corrupt.CheckAsync(true);
                Require(corrupt.Snapshot.Stage==UpdateStage.Failed && badManager.UpdatePendingRestart is null,"hash mismatch blocks a corrupted package from becoming ready");
            }
            report=report with {ChecksumRejected=true};Write(reportPath,report);
            using var service=new UpdateService(new VelopackBackend(manager,["--verify-update",reportPath]));
            await service.CheckAsync(true);
            Require(service.Snapshot.Stage==UpdateStage.Ready && service.Snapshot.TargetVersion==expected,"real installation checks and downloads the newer package");
            report=report with {Status="restarting"};Write(reportPath,report);
            Require(service.ApplyAndRestart(()=>shutdown(0)),"updater launches, then application shuts down gracefully");
        } catch(Exception error) {
            if(report!=null)Write(reportPath,report with {Status="failed",Error=error.ToString()});
            else File.WriteAllText(reportPath,JsonSerializer.Serialize(new {Status="failed",Error=error.ToString()}));
            shutdown(1);
        }
    }
    private static UpdateOptions Options()=>new(){ExplicitChannel=UpdateDistribution.Channel,AllowVersionDowngrade=false};
    private static UpdateManager Manager(string feed)=>new(new SimpleFileSource(new DirectoryInfo(feed)),Options());
    private static void Require(bool value,string label){if(!value)throw new InvalidOperationException(label);}
    private static string SettingsHash(){var path=Path.Combine(SettingsStore.DirectoryPath,"settings.json");return File.Exists(path)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))):"absent";}
    private static void Write(string path,Report value){File.WriteAllText(path+".tmp",JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));File.Move(path+".tmp",path,true);}
}
