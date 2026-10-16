using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Capture.Windows.Platform;

/// <summary>Content-free capture-entry timings. File I/O never runs on the capture/UI path.</summary>
internal sealed class CaptureDiagnostics
{
    private static readonly object FileGate=new();
    private const long FileLimit=1024*1024;
    private readonly long started;
    private readonly string command;
    private readonly string source;
    private readonly Dictionary<string,double> marks=[];
    private object[] monitors=[];
    private int completed;
    internal CaptureDiagnostics(string command,long? hotkeyTimestamp) {
        this.command=command;source=hotkeyTimestamp.HasValue?"hotkey":"ui";
        started=hotkeyTimestamp??Stopwatch.GetTimestamp();Mark("commandEnteredMs");
    }
    internal void Mark(string name)=>marks[name]=Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds,2);
    internal void Frames(CapturedFrame[] frames) {
        monitors=frames.Select(f=>(object)new {width=f.Image.PixelWidth,height=f.Image.PixelHeight,engine=f.Engine,captureMs=Math.Round(f.CaptureMilliseconds,2),fallback=f.FallbackReason}).ToArray();
        Mark("snapshotsReadyMs");
    }
    internal void Complete(string outcome) {
        if(Interlocked.Exchange(ref completed,1)!=0)return;
        // Snapshot fields on the UI thread before returning to the background writer.
        var line=JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,id=Guid.NewGuid(),command,source,outcome,timings=new Dictionary<string,double>(marks),monitors});
        _=Task.Run(()=> {
            try {lock(FileGate) {
                var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Snaplet","logs");
                Directory.CreateDirectory(folder);var path=Path.Combine(folder,"capture-performance.jsonl");
                if(File.Exists(path) && new FileInfo(path).Length>=FileLimit)File.Move(path,path+".1",true);
                File.AppendAllText(path,line+Environment.NewLine);
            }} catch(IOException) {} catch(UnauthorizedAccessException) {}
        });
    }
}
