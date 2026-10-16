using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capture.Windows.Updates;

internal static class UpdateSmokeTest
{
    internal static async Task RunAsync(string directory)
    {
        var checks=new List<string>();void Check(bool value,string label){if(!value)throw new InvalidOperationException("Update check failed: "+label);checks.Add(label);}
        using(var zip=new UpdateService(new VelopackBackend())) {await zip.CheckAsync();Check(zip.Snapshot.Stage==UpdateStage.NotInstalled,"actual ZIP launch initializes the updater safely without requesting installation metadata");}
        var notInstalled=new FakeBackend {IsInstalled=false};using(var service=new UpdateService(notInstalled)) {
            await service.CheckAsync(true);Check(service.Snapshot.Stage==UpdateStage.NotInstalled && notInstalled.Checks==0,"plain ZIP does not contact an update feed or attempt self replacement");
            Check(!service.ApplyAndRestart(()=>throw new Exception()),"uninstalled or unverified updates cannot restart the app");
        }
        var newer=new FakeBackend();using(var service=new UpdateService(newer)) {
            await service.CheckAsync();Check(service.Snapshot.Stage==UpdateStage.Available && newer.Downloads==0,"manual check presents an offer without downloading or restarting");
            await service.DownloadAsync();Check(service.Snapshot.Stage==UpdateStage.Ready && service.Snapshot.Progress==100,"verified download becomes ready for an explicit restart");
            newer.LateProgress?.Invoke(2);Check(service.Snapshot.Stage==UpdateStage.Ready && service.Snapshot.Progress==100,"late progress callbacks cannot revert a ready update");
            Check(newer.Applies==0,"background download never installs or restarts by itself");
            int exits=0;Check(service.ApplyAndRestart(()=>exits++) && exits==1 && newer.Applies==1,"explicit restart schedules updater before graceful application exit");
            Check(!service.ApplyAndRestart(()=>exits++) && newer.Applies==1,"double click cannot apply the same update twice");
        }
        var latest=new FakeBackend {Offer=null};using(var service=new UpdateService(latest)) {await service.CheckAsync(true);Check(service.Snapshot.Stage==UpdateStage.UpToDate && latest.Downloads==0,"up-to-date installations do not download a package");}
        var blocked=new FakeBackend {CheckGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        using(var service=new UpdateService(blocked)) {
            var first=service.CheckAsync();await service.CheckAsync();Check(blocked.Checks==1 && service.Snapshot.Stage==UpdateStage.Checking,"concurrent automatic and manual checks share one operation");
            blocked.CheckGate.SetResult("0.7.1");await first;
        }
        var failed=new FakeBackend {FailDownload=true};using(var service=new UpdateService(failed)) {
            await service.CheckAsync(true);Check(service.Snapshot.Stage==UpdateStage.Failed && failed.Applies==0,"failed download leaves the current app running");
            failed.FailDownload=false;await service.CheckAsync(true);Check(service.Snapshot.Stage==UpdateStage.Ready,"a failed download can be retried without restarting the current app");
        }
        var unverified=new FakeBackend {SkipVerification=true};using(var service=new UpdateService(unverified)) {
            await service.CheckAsync(true);Check(service.Snapshot.Stage==UpdateStage.Failed && !service.ApplyAndRestart(()=>throw new Exception()),"download completion without a verified pending package cannot enable apply");
        }
        var pending=new FakeBackend {PendingVersion="0.7.1"};using(var service=new UpdateService(pending)) {
            Check(service.Snapshot.Stage==UpdateStage.Ready,"downloaded update remains available after an app relaunch");
            pending.FailApply=true;int exits=0;Check(!service.ApplyAndRestart(()=>exits++) && exits==0,"updater launch failure never shuts down the existing app");
            pending.FailApply=false;await service.CheckAsync();Check(service.Snapshot.Stage==UpdateStage.Ready,"updater launch failure can return to the already downloaded package");
        }
        var cancelled=new FakeBackend {BlockDownload=true};using(var service=new UpdateService(cancelled)) {
            var work=service.CheckAsync(true);service.Dispose();await work;Check(cancelled.Applies==0 && service.Snapshot.Stage!=UpdateStage.Ready,"closing the app cancels an in-progress download without enabling installation");
        }
        using(var service=new UpdateService(new FakeBackend())) {
            await service.CheckAsync(true);int restartRequests=0;AppSettings? saved=null;
            var window=new SettingsWindow(new AppSettings(),value=>saved=value,service,()=>restartRequests++);
            try {
                window.Show();window.SelectPage("updates");window.UpdateLayout();
                var install=Find<Button>(window).Single(b=>AutomationProperties.GetAutomationId(b)=="updates-install");
                Check(install.IsVisible && install.IsEnabled && Equals(install.Content,"업데이트하고 다시 시작"),"settings shows an explicit restart action for a ready update");
                install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(restartRequests==1,"settings delegates restart to the application safeguard instead of forcing process exit");
                Shot(window,Path.Combine(directory,"ui-settings-updates-ready.png"));
                var automatic=Find<CheckBox>(window).Single(c=>AutomationProperties.GetName(c)=="업데이트 자동 확인 및 다운로드");automatic.IsChecked=false;
                Find<Button>(window).Single(b=>AutomationProperties.GetAutomationId(b)=="settings-save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(saved is {AutoUpdate:false} && saved.Hotkeys.Count==6,"automatic update preference persists alongside existing settings");
            }finally{if(window.IsVisible)window.Close();}
        }
        File.WriteAllText(Path.Combine(directory,"update-smoke.json"),JsonSerializer.Serialize(new{passed=checks.Count,checks,scope="state/operation and WPF controls; installed-update.json records the separate real installer/restart test"},new JsonSerializerOptions{WriteIndented=true}));
    }
    private sealed class FakeBackend : IUpdateBackend
    {
        public bool IsInstalled {get;init;}=true;
        public string CurrentVersion=>"0.7.0";
        public string? PendingVersion {get;set;}
        internal string? Offer="0.7.1";
        internal int Checks,Downloads,Applies;
        internal bool FailDownload,FailApply,SkipVerification,BlockDownload;
        internal TaskCompletionSource<string?>? CheckGate;
        internal Action<int>? LateProgress;
        public async Task<string?> CheckAsync(){Checks++;return CheckGate!=null?await CheckGate.Task:Offer;}
        public async Task DownloadAsync(Action<int> progress,CancellationToken cancellation) {
            Downloads++;if(FailDownload)throw new IOException("offline");
            if(BlockDownload)await Task.Delay(Timeout.Infinite,cancellation);
            progress(40);progress(20);LateProgress=progress;if(!SkipVerification)PendingVersion=Offer;
        }
        public void ApplyAndRestart(){if(FailApply)throw new IOException("locked");Applies++;}
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject {
        if(parent is T value)yield return value;for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)foreach(var child in Find<T>(VisualTreeHelper.GetChild(parent,i)))yield return child;
    }
    private static void Shot(Window window,string path) {
        window.UpdateLayout();var root=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
}
