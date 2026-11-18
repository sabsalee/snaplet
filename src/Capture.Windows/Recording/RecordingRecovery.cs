using System.IO;
using Capture.Core;

namespace Capture.Windows.Recording;

/// <summary>
/// Recordings are written to a work folder and moved when they finish. A file still there at start-up was cut off
/// by a crash or power loss: one that can still be played is moved to the save folder; one that cannot is removed.
/// </summary>
internal static class RecordingRecovery
{
    public static (IReadOnlyList<string> Recovered, int Lost) Run(string saveFolder, string? workFolder = null)
    {
        workFolder ??= RecordingJob.WorkFolder;
        var recovered = new List<string>(); int lost = 0;
        if (!Directory.Exists(workFolder)) return (recovered, 0);
        foreach (var path in Directory.GetFiles(workFolder, "*.mp4"))
        {
            try
            {
                Mp4Info? info;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) info = file.Length == 0 ? null : Mp4Reader.Read(file);
                var input = Path.ChangeExtension(path, ".input");
                if (info is not null && (info.Fragments > 0 || info.Duration > TimeSpan.Zero))
                {
                    Directory.CreateDirectory(saveFolder);
                    var target = RecordingFiles.UniquePath(saveFolder, $"복구된 녹화 {File.GetLastWriteTime(path):yyyyMMdd-HHmmss}", ".mp4", File.Exists);
                    File.Move(path, target); recovered.Add(target);
                }
                else { File.Delete(path); lost++; }
                if (File.Exists(input)) File.Delete(input);
            }
            // A file another Snaplet is still writing is left alone.
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (recovered, lost);
    }
}
