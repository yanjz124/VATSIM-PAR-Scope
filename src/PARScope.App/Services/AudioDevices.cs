using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PARScope.App.Services;

/// <summary>An audio endpoint; an empty Id means "Windows default".</summary>
public sealed record AudioDevice(string Id, string Name)
{
    public static readonly AudioDevice Default = new("", "Windows default");
    public override string ToString() => Name;
}

public static class AudioDevices
{
    public static IReadOnlyList<AudioDevice> Inputs() => List(DataFlow.Capture);
    public static IReadOnlyList<AudioDevice> Outputs() => List(DataFlow.Render);

    private static IReadOnlyList<AudioDevice> List(DataFlow flow)
    {
        var list = new List<AudioDevice> { AudioDevice.Default };
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName));
        }
        catch (System.Runtime.InteropServices.COMException) { }
        return list;
    }

    internal static MMDevice? Resolve(string? id, DataFlow flow)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(id))
            {
                var d = en.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault(x => x.ID == id);
                if (d != null) return d;
            }
            return en.GetDefaultAudioEndpoint(flow, flow == DataFlow.Capture ? Role.Communications : Role.Multimedia);
        }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }
}

/// <summary>Records from the chosen microphone while push-to-talk is held; returns 16 kHz mono samples.</summary>
public sealed class PttRecorder : IDisposable
{
    private WasapiCapture? _capture;
    private MemoryStream? _buffer;
    private WaveFormat? _format;
    private readonly Lock _lock = new();

    public string? DeviceId { get; set; }
    public float Level { get; private set; }

    public bool Start()
    {
        Stop();
        var device = AudioDevices.Resolve(DeviceId, DataFlow.Capture);
        if (device == null) return false;
        try
        {
            _capture = new WasapiCapture(device);
            _format = _capture.WaveFormat;
            _buffer = new MemoryStream();
            _capture.DataAvailable += (_, e) =>
            {
                lock (_lock) _buffer?.Write(e.Buffer, 0, e.BytesRecorded);
                Level = PeakLevel(e.Buffer, e.BytesRecorded);
            };
            _capture.StartRecording();
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _capture?.Dispose();
            _capture = null;
            return false;
        }
    }

    /// <summary>Stops recording and returns the audio as 16 kHz mono float samples.</summary>
    public float[] Stop()
    {
        if (_capture == null || _format == null) return [];
        _capture.StopRecording();
        _capture.Dispose();
        _capture = null;
        byte[] raw;
        lock (_lock) { raw = _buffer?.ToArray() ?? []; _buffer = null; }
        Level = 0;
        return To16kMono(raw, _format);
    }

    /// <summary>The audio so far, as 16 kHz mono, while still recording.</summary>
    public float[] Snapshot()
    {
        var format = _format;
        byte[] raw;
        lock (_lock) raw = _buffer?.ToArray() ?? [];
        return format == null ? [] : To16kMono(raw, format);
    }

    private static float[] To16kMono(byte[] raw, WaveFormat format)
    {
        if (raw.Length == 0) return [];
        ISampleProvider src = new RawSourceWaveStream(new MemoryStream(raw), format).ToSampleProvider();
        if (src.WaveFormat.Channels > 1) src = new StereoToMonoSampleProvider(DownmixTo2(src)) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (src.WaveFormat.SampleRate != 16000) src = new WdlResamplingSampleProvider(src, 16000);

        var samples = new List<float>(raw.Length / 4);
        var block = new float[16000];
        int n;
        while ((n = src.Read(block, 0, block.Length)) > 0) samples.AddRange(block.AsSpan(0, n).ToArray());
        return samples.ToArray();
    }

    private static ISampleProvider DownmixTo2(ISampleProvider src) =>
        src.WaveFormat.Channels == 2 ? src : new MultiplexingSampleProvider([src], 2);

    private float PeakLevel(byte[] buf, int count)
    {
        if (_format?.Encoding != WaveFormatEncoding.IeeeFloat || _format.BitsPerSample != 32) return 0;
        float peak = 0;
        for (int i = 0; i + 4 <= count; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buf, i)));
        return peak;
    }

    public void Dispose() => Stop();
}
