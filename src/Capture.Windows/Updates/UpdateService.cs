using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Capture.Windows.Updates;

internal enum UpdateStage { NotInstalled, Idle, Checking, Available, Downloading, Ready, UpToDate, Failed, Applying }
internal sealed record UpdateSnapshot(UpdateStage Stage,string CurrentVersion,string? TargetVersion=null,int Progress=0,string Message="");
internal interface IUpdateBackend
{
    bool IsInstalled {get;}
    string CurrentVersion {get;}
    string? PendingVersion {get;}
    Task<string?> CheckAsync();
    Task DownloadAsync(Action<int> progress,CancellationToken cancellation);
    void ApplyAndRestart();
}

internal static class UpdateDistribution
{
    internal const string Repository="https://github.com/sabsalee/snaplet";
    internal const string Releases=Repository+"/releases";
    internal const string Channel="win-x64";
    internal const string PackId="SnapletApp"; // Separate from the existing %LocalAppData%\Snaplet settings folder.
}

internal sealed class VelopackBackend : IUpdateBackend
{
    private readonly UpdateManager manager;
    private UpdateInfo? offer;
    private readonly string[] restartArgs;
    internal VelopackBackend():this(new UpdateManager(new GithubSource(UpdateDistribution.Repository,null,true),
        new UpdateOptions {ExplicitChannel=UpdateDistribution.Channel,AllowVersionDowngrade=false})){}
    internal VelopackBackend(UpdateManager manager,string[]? restartArgs=null){this.manager=manager;this.restartArgs=restartArgs ?? [];}
    public bool IsInstalled=>manager.IsInstalled;
    public string CurrentVersion=>manager.CurrentVersion?.ToString() ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string? PendingVersion=>manager.IsInstalled && manager.UpdatePendingRestart is { } pending && pending.Version>manager.CurrentVersion?pending.Version.ToString():null;
    public async Task<string?> CheckAsync(){offer=await manager.CheckForUpdatesAsync().ConfigureAwait(false);return offer?.TargetFullRelease.Version.ToString();}
    public Task DownloadAsync(Action<int> progress,CancellationToken cancellation)=>offer is null
        ?throw new InvalidOperationException("먼저 업데이트를 확인하세요.") :manager.DownloadUpdatesAsync(offer,progress,cancellation);
    public void ApplyAndRestart()
    {
        var pending=manager.UpdatePendingRestart ?? throw new InvalidOperationException("검증된 업데이트 파일이 없습니다. 다시 다운로드하세요.");
        manager.WaitExitThenApplyUpdates(pending,silent:false,restart:true,restartArgs:restartArgs);
    }
}

/// <summary>One update operation at a time. Downloads never trigger an implicit restart.</summary>
internal sealed class UpdateService : IDisposable
{
    private readonly IUpdateBackend backend;
    private readonly SynchronizationContext? context=SynchronizationContext.Current;
    private readonly CancellationTokenSource lifetime=new();
    private readonly object sync=new();
    private UpdateSnapshot snapshot;
    private int busy;
    internal event Action? Changed;
    internal UpdateSnapshot Snapshot {get {lock(sync)return snapshot;}}
    internal UpdateService(IUpdateBackend backend)
    {
        this.backend=backend;
        snapshot=new(backend.IsInstalled?backend.PendingVersion!=null?UpdateStage.Ready:UpdateStage.Idle:UpdateStage.NotInstalled,
            backend.CurrentVersion,backend.PendingVersion,backend.PendingVersion!=null?100:0);
    }
    internal async Task CheckAsync(bool downloadAutomatically=false)
    {
        if(!backend.IsInstalled || lifetime.IsCancellationRequested || Interlocked.CompareExchange(ref busy,1,0)!=0)return;
        try {
            if(backend.PendingVersion is string ready){Publish(new(UpdateStage.Ready,backend.CurrentVersion,ready,100));return;}
            Publish(new(UpdateStage.Checking,backend.CurrentVersion));
            var version=await backend.CheckAsync();
            if(lifetime.IsCancellationRequested)return;
            Publish(new(version is null?UpdateStage.UpToDate:UpdateStage.Available,backend.CurrentVersion,version));
            if(version!=null && downloadAutomatically)await DownloadLockedAsync(version);
        } catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){Fail(error);}
        finally {Interlocked.Exchange(ref busy,0);}
    }
    internal async Task DownloadAsync()
    {
        var state=Snapshot;
        if(state.Stage!=UpdateStage.Available || state.TargetVersion is null || lifetime.IsCancellationRequested || Interlocked.CompareExchange(ref busy,1,0)!=0)return;
        try{await DownloadLockedAsync(state.TargetVersion);}
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){Fail(error);}
        finally {Interlocked.Exchange(ref busy,0);}
    }
    private async Task DownloadLockedAsync(string version)
    {
        Publish(new(UpdateStage.Downloading,backend.CurrentVersion,version));
        await backend.DownloadAsync(value=>{
            lock(sync) {
                if(snapshot.Stage!=UpdateStage.Downloading)return;
                snapshot=snapshot with {Progress=Math.Max(snapshot.Progress,Math.Clamp(value,0,100))};
            }
            Notify();
        },lifetime.Token);
        if(lifetime.IsCancellationRequested)return;
        if(backend.PendingVersion!=version)throw new InvalidOperationException("업데이트 파일 검증을 완료하지 못했습니다.");
        Publish(new(UpdateStage.Ready,backend.CurrentVersion,version,100));
    }
    internal bool ApplyAndRestart(Action shutdown)
    {
        var state=Snapshot;
        if(state.Stage!=UpdateStage.Ready || backend.PendingVersion!=state.TargetVersion || lifetime.IsCancellationRequested || Interlocked.CompareExchange(ref busy,1,0)!=0)return false;
        try {Publish(state with {Stage=UpdateStage.Applying});backend.ApplyAndRestart();shutdown();return true;}
        catch(Exception error){Fail(error);return false;}
        finally {Interlocked.Exchange(ref busy,0);}
    }
    private void Fail(Exception error)
    {
        var message=error is Velopack.Exceptions.ChecksumFailedException
            ?"다운로드한 파일 검증에 실패했습니다. 업데이트를 다시 확인하세요."
            :"업데이트를 완료하지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도하세요.";
        Publish(new(UpdateStage.Failed,backend.CurrentVersion,Message:message));
    }
    private void Publish(UpdateSnapshot value){lock(sync)snapshot=value;Notify();}
    private void Notify()
    {
        if(lifetime.IsCancellationRequested)return;
        if(context!=null && SynchronizationContext.Current!=context)context.Post(_=>{if(!lifetime.IsCancellationRequested)Changed?.Invoke();},null);
        else Changed?.Invoke();
    }
    public void Dispose(){lifetime.Cancel();}
}

internal sealed class UninstalledBackend : IUpdateBackend
{
    public bool IsInstalled=>false;
    public string CurrentVersion=>typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string? PendingVersion=>null;
    public Task<string?> CheckAsync()=>Task.FromResult<string?>(null);
    public Task DownloadAsync(Action<int> progress,CancellationToken cancellation)=>Task.CompletedTask;
    public void ApplyAndRestart()=>throw new InvalidOperationException();
}
