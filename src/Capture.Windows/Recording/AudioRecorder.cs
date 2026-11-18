using System.Runtime.InteropServices;
using Capture.Core;

namespace Capture.Windows.Recording;

/// <summary>
/// System sound (WASAPI loopback of the default output) and the default microphone, converted to 48 kHz stereo,
/// mixed and lined up with the video's media time. A source that cannot open is reported and left out; the
/// recording itself continues. Sound captured while paused is dropped.
/// </summary>
internal sealed class AudioRecorder : IDisposable
{
    private readonly Func<bool> recording;
    private readonly object gate = new();
    private readonly AudioMixer mixer = new();
    private readonly AudioTimeline timeline = new();
    private readonly List<string> problems = new();
    private Source? system, microphone;
    private Thread? thread;
    private volatile bool stopping;
    private DateTime lastMicrophoneSound = DateTime.UtcNow;

    public AudioRecorder(bool systemSound, bool microphoneSound, Func<bool> recording)
    {
        this.recording = recording;
        // WASAPI objects are opened and read on MTA threads, never on the UI thread.
        Task.Run(() =>
        {
            if (systemSound) system = Open(0, true, "컴퓨터 소리");
            if (microphoneSound) microphone = Open(1, false, "마이크");
        }).Wait();
    }

    /// <summary>True when at least one source opened, so the video gets a sound track.</summary>
    public bool HasSound => system is not null || microphone is not null;
    public bool HasMicrophone => microphone is not null;
    /// <summary>Why a requested source is missing, in words for the user.</summary>
    public IReadOnlyList<string> Problems { get { lock (gate) return problems.ToList(); } }
    /// <summary>Peak level of the latest sound, 0..1.</summary>
    public float Level { get; private set; }
    /// <summary>The microphone has delivered only silence for three seconds, as when it is muted.</summary>
    public bool MicrophoneQuiet => microphone is not null && DateTime.UtcNow - lastMicrophoneSound > TimeSpan.FromSeconds(3);

    public void Start()
    {
        if (!HasSound) return;
        thread = new Thread(Pump) { IsBackground = true, Name = "Snaplet recording sound", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>16-bit stereo PCM for media time up to <paramref name="until"/>; silence where nothing arrived.</summary>
    public byte[] Take(TimeSpan until) { lock (gate) return timeline.Take(until, TimeSpan.FromMilliseconds(200)); }
    public TimeSpan Written { get { lock (gate) return TimeSpan.FromTicks(timeline.Written * TimeSpan.TicksPerSecond / AudioConverter.Rate); } }

    private void Pump()
    {
        var systemFrames = new List<float>(); var microphoneFrames = new List<float>(); var mixed = new List<float>();
        try { system?.Client.Start(); microphone?.Client.Start(); }
        catch (COMException error) { lock (gate) problems.Add($"소리 장치를 시작하지 못해 소리 없이 녹화합니다 (0x{error.HResult:X8})."); return; }
        while (!stopping)
        {
            systemFrames.Clear(); microphoneFrames.Clear(); mixed.Clear();
            try
            {
                system?.Read(systemFrames);
                microphone?.Read(microphoneFrames);
            }
            catch (COMException error)
            {
                // A device that disappears (unplugged, default changed) ends that source; the rest continues.
                lock (gate) problems.Add($"녹화 중 소리 장치가 바뀌어 소리가 끊겼습니다 (0x{error.HResult:X8}).");
                system?.Dispose(); microphone?.Dispose(); system = microphone = null;
                return;
            }
            if (microphoneFrames.Count > 0 && AudioMixer.Peak(microphoneFrames) > .002f) lastMicrophoneSound = DateTime.UtcNow;
            if (recording())
            {
                lock (gate)
                {
                    if (systemFrames.Count > 0) mixer.AddSystem(systemFrames);
                    if (microphoneFrames.Count > 0) mixer.AddMicrophone(microphoneFrames);
                    // Loopback sends nothing during silence, so it never holds the microphone back.
                    mixer.Drain(mixed, system is not null, microphone is not null, flush: systemFrames.Count == 0 || microphoneFrames.Count == 0);
                    timeline.Add(mixed);
                }
                Level = Math.Max(AudioMixer.Peak(mixed), Level * .85f);
            }
            else lastMicrophoneSound = DateTime.UtcNow;
            Thread.Sleep(10);
        }
    }

    private Source? Open(int flow, bool loopback, string name)
    {
        try { return new Source(flow, loopback); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            problems.Add(error is COMException { HResult: unchecked((int)0x80070490) } ? $"{name} 장치를 찾을 수 없어 {name} 없이 녹화합니다." : $"{name}를 열지 못해 {name} 없이 녹화합니다: {error.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        stopping = true;
        thread?.Join(500);
        system?.Dispose(); microphone?.Dispose();
    }

    /// <summary>One shared-mode WASAPI capture stream.</summary>
    private sealed class Source : IDisposable
    {
        public readonly IAudioClient Client;
        private readonly IAudioCaptureClient capture;
        private readonly AudioConverter converter;
        private readonly AudioFormat format;

        public Source(int flow, bool loopback)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(flow, 0, out var device));
            var clientId = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref clientId, 1, 0, out var activated));
            Client = (IAudioClient)activated;
            Marshal.ThrowExceptionForHR(Client.GetMixFormat(out var mix));
            try
            {
                format = Read(mix);
                converter = new AudioConverter(format);
                // 200 ms shared buffer; loopback captures what the default output plays.
                Marshal.ThrowExceptionForHR(Client.Initialize(0, loopback ? 0x00020000 : 0, 2_000_000, 0, mix, 0));
            }
            finally { Marshal.FreeCoTaskMem(mix); }
            var captureId = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(Client.GetService(ref captureId, out var service));
            capture = (IAudioCaptureClient)service;
        }

        public void Read(List<float> output)
        {
            while (true)
            {
                Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out int packet));
                if (packet == 0) return;
                Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out int frames, out int flags, out _, out _));
                try
                {
                    int bytes = frames * format.BlockAlign;
                    var buffer = new byte[bytes];
                    // AUDCLNT_BUFFERFLAGS_SILENT: the data is to be treated as silence.
                    if ((flags & 0x2) == 0) Marshal.Copy(data, buffer, 0, bytes);
                    converter.Convert(buffer, output);
                }
                finally { capture.ReleaseBuffer(frames); }
            }
        }

        private static AudioFormat Read(nint pointer)
        {
            short tag = Marshal.ReadInt16(pointer), channels = Marshal.ReadInt16(pointer, 2), bits = Marshal.ReadInt16(pointer, 14);
            int rate = Marshal.ReadInt32(pointer, 4);
            bool isFloat = tag == 3;
            if (tag == unchecked((short)0xFFFE))
            {
                // WAVEFORMATEXTENSIBLE: the sub-format GUID decides float or integer.
                var subFormat = Marshal.PtrToStructure<Guid>(pointer + 24);
                isFloat = subFormat == new Guid("00000003-0000-0010-8000-00AA00389B71");
                if (!isFloat && subFormat != new Guid("00000001-0000-0010-8000-00AA00389B71")) throw new InvalidOperationException("지원하지 않는 소리 형식입니다.");
            }
            else if (tag is not (1 or 3)) throw new InvalidOperationException("지원하지 않는 소리 형식입니다.");
            return new(rate, channels, bits, isFloat);
        }

        public void Dispose()
        {
            try { Client.Stop(); } catch (COMException) { }
            Marshal.ReleaseComObject(capture); Marshal.ReleaseComObject(Client);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int state, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid id, int context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, nint format, nint session);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int frames);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }
}
