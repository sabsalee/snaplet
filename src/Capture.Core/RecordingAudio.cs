namespace Capture.Core;

/// <summary>A device's mix format as WASAPI reports it: interleaved 32-bit float or 16/24/32-bit integer PCM.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample, bool Float)
{
    public int BlockAlign => Channels * BitsPerSample / 8;
}

/// <summary>
/// Converts device audio to the recording format (48 kHz stereo float) with a linear resampler that keeps
/// its position between buffers, so there are no clicks at buffer edges.
/// </summary>
public sealed class AudioConverter
{
    public const int Rate = 48000;
    private readonly AudioFormat format;
    private double position;
    private float lastLeft, lastRight;
    private bool primed;

    public AudioConverter(AudioFormat format)
    {
        if (format.SampleRate < 8000 || format.Channels < 1 || format.BitsPerSample is not (16 or 24 or 32) || format.Float && format.BitsPerSample != 32)
            throw new ArgumentException("지원하지 않는 오디오 형식입니다.", nameof(format));
        this.format = format;
    }

    /// <summary>Appends converted stereo frames (left, right interleaved) for one device buffer.</summary>
    public void Convert(ReadOnlySpan<byte> data, List<float> output)
    {
        int frames = data.Length / format.BlockAlign;
        double step = (double)format.SampleRate / Rate;
        for (int i = 0; i < frames; i++)
        {
            ReadFrame(data, i, out float left, out float right);
            if (!primed)
            {
                // The very first input frame is the first output frame; later outputs are measured from it.
                output.Add(left); output.Add(right);
                lastLeft = left; lastRight = right; primed = true; position = step;
                continue;
            }
            // Emit every output frame whose time falls after the previous input frame and up to this one.
            while (position <= 1)
            {
                output.Add(lastLeft + (left - lastLeft) * (float)position);
                output.Add(lastRight + (right - lastRight) * (float)position);
                position += step;
            }
            position -= 1;
            lastLeft = left; lastRight = right;
        }
    }

    private void ReadFrame(ReadOnlySpan<byte> data, int frame, out float left, out float right)
    {
        int offset = frame * format.BlockAlign, bytes = format.BitsPerSample / 8;
        left = Sample(data.Slice(offset, bytes));
        // Mono is copied to both sides; more than two channels keep the front left and right.
        right = format.Channels > 1 ? Sample(data.Slice(offset + bytes, bytes)) : left;
    }

    private float Sample(ReadOnlySpan<byte> s)
    {
        if (format.Float) return BitConverter.ToSingle(s);
        return format.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(s) / 32768f,
            24 => (s[0] | s[1] << 8 | (sbyte)s[2] << 16) / 8388608f,
            _ => BitConverter.ToInt32(s) / 2147483648f,
        };
    }
}

/// <summary>
/// Lines audio up with the video's media time. Sound that arrives while paused is dropped, and when a
/// source goes quiet (loopback sends nothing during silence) the gap is filled with silence, so sound
/// never drifts ahead of or behind the picture.
/// </summary>
public sealed class AudioTimeline
{
    private readonly List<float> pending = new();
    private long written;

    /// <summary>Stereo frames handed to the encoder so far.</summary>
    public long Written => written;

    /// <summary>Adds converted frames that were captured while recording (not paused).</summary>
    public void Add(List<float> stereo) => pending.AddRange(stereo);

    /// <summary>
    /// Returns 16-bit stereo PCM covering media time up to <paramref name="until"/>. Frames beyond it stay queued;
    /// missing frames are silence. A backlog more than <paramref name="maxLag"/> behind is trimmed to keep sync.
    /// </summary>
    public byte[] Take(TimeSpan until, TimeSpan maxLag)
    {
        long target = until.Ticks * AudioConverter.Rate / TimeSpan.TicksPerSecond;
        long frames = target - written;
        if (frames <= 0) return [];
        long queued = pending.Count / 2;
        long excess = queued - frames - maxLag.Ticks * AudioConverter.Rate / TimeSpan.TicksPerSecond;
        if (excess > 0) pending.RemoveRange(0, (int)excess * 2);
        var pcm = new byte[frames * 4];
        int available = (int)Math.Min(frames, pending.Count / 2);
        for (int i = 0; i < available * 2; i++)
        {
            short value = (short)Math.Clamp((int)Math.Round(pending[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8);
        }
        pending.RemoveRange(0, available * 2);
        written = target;
        return pcm;
    }
}

/// <summary>Sums two stereo float streams (system sound and microphone) frame by frame, keeping the remainder of the longer one.</summary>
public sealed class AudioMixer
{
    private readonly List<float> system = new(), microphone = new();
    public void AddSystem(List<float> frames) => system.AddRange(frames);
    public void AddMicrophone(List<float> frames) => microphone.AddRange(frames);

    /// <summary>
    /// Moves mixed frames to <paramref name="output"/>. With both sources active only frames present in both are mixed;
    /// <paramref name="flush"/> releases the rest, e.g. when one source has stopped delivering.
    /// </summary>
    public void Drain(List<float> output, bool systemActive, bool microphoneActive, bool flush = false)
    {
        int count = systemActive && microphoneActive && !flush ? Math.Min(system.Count, microphone.Count) : Math.Max(systemActive ? system.Count : 0, microphoneActive ? microphone.Count : 0);
        for (int i = 0; i < count; i++)
        {
            float a = systemActive && i < system.Count ? system[i] : 0, b = microphoneActive && i < microphone.Count ? microphone[i] : 0;
            output.Add(Math.Clamp(a + b, -1f, 1f));
        }
        system.RemoveRange(0, Math.Min(count, system.Count));
        microphone.RemoveRange(0, Math.Min(count, microphone.Count));
    }

    /// <summary>Peak level of a stereo float buffer, 0..1, for the bar's sound indicator.</summary>
    public static float Peak(List<float> frames)
    {
        float peak = 0;
        foreach (var value in frames) peak = Math.Max(peak, Math.Abs(value));
        return Math.Min(1, peak);
    }
}
