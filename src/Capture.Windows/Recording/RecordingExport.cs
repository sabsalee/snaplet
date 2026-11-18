using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Capture.Windows.Recording;

/// <summary>
/// Edits a finished recording with the built-in Windows media composition: trims, mutes and resizes into a new MP4,
/// turns a range into a looping GIF, or takes one frame as an image. The original file is never changed.
/// </summary>
internal static class RecordingExport
{
    public static async Task<(TimeSpan Duration, int Width, int Height)> ProbeAsync(string source)
    {
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(source));
        var video = clip.GetVideoEncodingProperties();
        return (clip.OriginalDuration, (int)video.Width, (int)video.Height);
    }

    public static async Task SaveVideoAsync(string source, string target, TrimRange range, bool mute, int maxLongSide, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (composition, clip) = await ComposeAsync(source, range);
        var video = clip.GetVideoEncodingProperties();
        var (width, height) = RecordingOutput.Size((int)video.Width, (int)video.Height, maxLongSide);
        uint fps = video.FrameRate.Denominator == 0 ? 30 : Math.Clamp(video.FrameRate.Numerator / video.FrameRate.Denominator, 1, 120);
        var profile = ScreenRecorder.Profile(width, height, (int)fps, !mute);
        var file = await CreateAsync(target);
        try
        {
            var render = composition.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile);
            render.Progress = (_, percent) => progress?.Report(percent / 100);
            var failure = await render.AsTask(cancellationToken);
            if (failure != TranscodeFailureReason.None) throw new InvalidOperationException($"영상을 저장하지 못했습니다 ({failure}).");
        }
        catch { TryDelete(target); throw; }
    }

    public static async Task SaveGifAsync(string source, string target, TrimRange range, int fps, int maxLongSide, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Frame times are positions in the original video, so the clip is left untrimmed.
        var (composition, clip) = await ComposeAsync(source, null);
        var video = clip.GetVideoEncodingProperties();
        var (width, height) = GifPlan.Size((int)video.Width, (int)video.Height, maxLongSide);
        var times = GifPlan.Frames(range, fps, out int delay);
        var encoder = new GifBitmapEncoder();
        for (int i = 0; i < times.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await FrameAsync(composition, times[i], width, height);
            // A palette chosen from each frame keeps screen colours accurate instead of a fixed web palette.
            var indexed = new FormatConvertedBitmap(frame, PixelFormats.Indexed8, new BitmapPalette(frame, 256), 0);
            indexed.Freeze();
            encoder.Frames.Add(BitmapFrame.Create(indexed));
            progress?.Report((i + 1) / (double)times.Count * .95);
        }
        using var memory = new MemoryStream();
        encoder.Save(memory);
        var timed = GifTiming.Apply(memory.ToArray(), delay);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, timed, cancellationToken); File.Move(temp, target, true); }
        finally { TryDelete(temp); }
        progress?.Report(1);
    }

    /// <summary>One frame at <paramref name="at"/>, full size, for the image editor.</summary>
    public static async Task<BitmapSource> FrameAsync(string source, TimeSpan at)
    {
        var (composition, clip) = await ComposeAsync(source, null);
        var video = clip.GetVideoEncodingProperties();
        return await FrameAsync(composition, at, (int)video.Width, (int)video.Height);
    }

    private static async Task<BitmapSource> FrameAsync(MediaComposition composition, TimeSpan at, int width, int height)
    {
        using var stream = await composition.GetThumbnailAsync(at, width, height, VideoFramePrecision.NearestFrame);
        using var managed = stream.AsStream();
        var decoder = BitmapDecoder.Create(managed, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource image = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        image.Freeze();
        return image;
    }

    private static async Task<(MediaComposition, MediaClip)> ComposeAsync(string source, TrimRange? range)
    {
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(source));
        if (range is TrimRange kept)
        {
            clip.TrimTimeFromStart = kept.Start;
            clip.TrimTimeFromEnd = clip.OriginalDuration > kept.End ? clip.OriginalDuration - kept.End : TimeSpan.Zero;
        }
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        return (composition, clip);
    }

    private static async Task<StorageFile> CreateAsync(string path)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        return await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
