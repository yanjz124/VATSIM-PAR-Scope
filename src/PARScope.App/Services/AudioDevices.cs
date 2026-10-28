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

/// <summary>
/// Records from the chosen microphone while push-to-talk is held; returns 16 kHz mono samples. The microphone
/// stays open between transmissions (<see cref="Open"/>), keeping only the last half second in memory, so a
/// transmission starts with the audio from just before the key went down: opening the device on the key press
/// takes long enough to clip the first syllable ("on glide" heard as "unglide").
/// </summary>
public sealed class PttRecorder : IDisposable
{
    private const double PreRollSec = 0.5;
    private WasapiCapture? _capture;
    private MemoryStream? _buffer;
    private byte[] _ring = [];
    private int _ringLen;
    private WaveFormat? _format;
    private readonly Lock _lock = new();
    private string? _deviceId;

    public string? DeviceId
    {
        get => _deviceId;
        set
        {
            if (_deviceId == value) return;
            _deviceId = value;
            if (_capture != null) { Close(); Open(); }
        }
    }

    public float Level { get; private set; }

    /// <summary>Opens the microphone and keeps the last half second, ready for push-to-talk.</summary>
    public bool Open()
    {
        if (_capture != null) return true;
        var device = AudioDevices.Resolve(DeviceId, DataFlow.Capture);
        if (device == null) return false;
        try
        {
            var capture = new WasapiCapture(device);
            var format = capture.WaveFormat;
            lock (_lock)
            {
                _format = format;
                _ring = new byte[(int)(format.AverageBytesPerSecond * PreRollSec) / format.BlockAlign * format.BlockAlign];
                _ringLen = 0;
            }
            capture.DataAvailable += (_, e) =>
            {
                lock (_lock)
                {
                    if (_buffer != null) _buffer.Write(e.Buffer, 0, e.BytesRecorded);
                    else KeepRecent(e.Buffer, e.BytesRecorded);
                }
                Level = _buffer != null ? PeakLevel(e.Buffer, e.BytesRecorded) : 0;
            };
            capture.StartRecording();
            _capture = capture;
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _capture?.Dispose();
            _capture = null;
            return false;
        }
    }

    /// <summary>Keeps the newest bytes in the pre-roll buffer (whole sample frames).</summary>
    private void KeepRecent(byte[] data, int count)
    {
        if (_ring.Length == 0) return;
        if (count >= _ring.Length)
        {
            Buffer.BlockCopy(data, count - _ring.Length, _ring, 0, _ring.Length);
            _ringLen = _ring.Length;
            return;
        }
        int keep = Math.Min(_ringLen, _ring.Length - count);
        Buffer.BlockCopy(_ring, _ringLen - keep, _ring, 0, keep);
        Buffer.BlockCopy(data, 0, _ring, keep, count);
        _ringLen = keep + count;
    }

    /// <summary>Push-to-talk pressed: records from half a second ago.</summary>
    public bool Start()
    {
        if (!Open()) return false;
        lock (_lock)
        {
            _buffer = new MemoryStream();
            _buffer.Write(_ring, 0, _ringLen);
            _ringLen = 0;
        }
        return true;
    }

    /// <summary>Push-to-talk released: returns the transmission as 16 kHz mono float samples; the microphone stays open.</summary>
    public float[] Stop()
    {
        byte[] raw;
        WaveFormat? format;
        lock (_lock) { raw = _buffer?.ToArray() ?? []; _buffer = null; format = _format; }
        Level = 0;
        return format == null ? [] : To16kMono(raw, format);
    }

    /// <summary>Releases the microphone.</summary>
    public void Close()
    {
        var capture = _capture;
        _capture = null;
        if (capture == null) return;
        try { capture.StopRecording(); } catch (System.Runtime.InteropServices.COMException) { }
        capture.Dispose();
        lock (_lock) { _buffer = null; _ringLen = 0; }
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

    public void Dispose() => Close();
}
