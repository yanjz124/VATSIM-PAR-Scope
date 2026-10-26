using System.IO;
using System.Net.Http;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PARScope.App.Services;

/// <summary>
/// The simulated radio and interphone: pilot, tower and approach voices from the selected speech engine,
/// played on the chosen output device through an optional radio effect. Each line starts synthesising as
/// soon as it is queued (so a slow neural voice is ready by the time the pilot would answer) and plays at its
/// due time, one at a time in order. Playback pauses while the controller holds push-to-talk.
/// </summary>
public sealed class RadioVoice : IDisposable
{
    private sealed record Line(Task<(float[] Samples, int Rate)> Audio, DateTime Due, Action? OnStart, int Generation);

    /// <summary>Lines queued before the last <see cref="StopAll"/> are dropped.</summary>
    private int _generation;

    /// <summary>Raised (on the audio thread) when the last queued line has finished or was stopped.</summary>
    public event Action? Idle;

    private readonly Channel<Line> _queue = Channel.CreateUnbounded<Line>();
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _notTransmitting = new(true);
    private WasapiOut? _current;
    private ISpeechEngine _engine = new WindowsSpeechEngine();

    public RadioVoice() => _ = Task.Run(PlayLoopAsync);

    public string EngineName => _engine.Name;
    public IReadOnlyList<string> Voices => _engine.Voices;
    public string? PilotVoice { get; set; }
    public string? TowerVoice { get; set; }
    public string? ApproachVoice { get; set; }
    /// <summary>Each pilot gets their own voice, picked from the callsign.</summary>
    public bool VaryPilotVoices { get; set; } = true;
    public bool RadioEffectEnabled { get; set; } = true;
    public string? OutputDeviceId { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Switches speech engine (downloading a neural model on first use) and resets voices to its defaults.</summary>
    public async Task UseEngineAsync(string id, HttpClient http, IProgress<string>? progress, CancellationToken ct)
    {
        var engine = await VoiceEngineOption.CreateAsync(id, http, progress, ct);
        var old = _engine;
        _engine = engine;
        PilotVoice = engine.DefaultPilot;
        TowerVoice = engine.DefaultTower;
        ApproachVoice = engine.DefaultApproach;
        old.Dispose();
    }

    public void Pilot(string text, string? callsign = null, double delaySec = 0, Action? onStart = null) =>
        Enqueue(text, PilotVoiceFor(callsign), delaySec, onStart);
    public void Tower(string text, double delaySec = 0, Action? onStart = null) => Enqueue(text, TowerVoice, delaySec, onStart);
    public void Approach(string text, double delaySec = 0, Action? onStart = null) => Enqueue(text, ApproachVoice, delaySec, onStart);

    /// <summary>The voice a pilot speaks with: their own (stable per callsign, never tower's or approach's), or the chosen pilot voice.</summary>
    private string? PilotVoiceFor(string? callsign)
    {
        if (!VaryPilotVoices || callsign == null) return PilotVoice;
        var pool = Voices.Where(v => v != TowerVoice && v != ApproachVoice).ToList();
        if (pool.Count == 0) return PilotVoice;
        uint h = 2166136261;
        foreach (char c in callsign.ToUpperInvariant()) h = (h ^ c) * 16777619;
        return pool[(int)(h % (uint)pool.Count)];
    }

    private void Enqueue(string text, string? voice, double delaySec, Action? onStart)
    {
        var due = DateTime.UtcNow.AddSeconds(delaySec);
        if (!Enabled || Voices.Count == 0)
        {
            // Silent: still report the line when it would have been heard.
            if (onStart != null) _ = Task.Delay(TimeSpan.FromSeconds(delaySec)).ContinueWith(_ => onStart());
            return;
        }
        var engine = _engine;
        bool effect = RadioEffectEnabled;
        var audio = Task.Run(() =>
        {
            var (s, rate) = engine.Synthesize(text, voice ?? engine.DefaultPilot);
            return effect ? (RadioEffect.Apply(s, rate, text.GetHashCode()), rate) : (s, rate);
        });
        _queue.Writer.TryWrite(new Line(audio, due, onStart, _generation));
    }

    /// <summary>Stops what is playing and drops everything queued.</summary>
    public void StopAll()
    {
        Interlocked.Increment(ref _generation);
        try { _current?.Stop(); } catch (InvalidOperationException) { }
    }

    public void PauseForTransmit()
    {
        _notTransmitting.Reset();
        try { _current?.Pause(); } catch (InvalidOperationException) { }
    }

    public void ResumeAfterTransmit()
    {
        _notTransmitting.Set();
        try { _current?.Play(); } catch (InvalidOperationException) { }
    }

    private async Task PlayLoopAsync()
    {
        await foreach (var line in _queue.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                if (line.Generation != _generation) continue;
                var (samples, rate) = await line.Audio;
                var wait = line.Due - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _cts.Token);
                _notTransmitting.Wait(_cts.Token);
                if (line.Generation != _generation) continue;
                line.OnStart?.Invoke();
                await PlayAsync(samples, rate, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
            {
                line.OnStart?.Invoke();
            }
            finally
            {
                if (!_cts.IsCancellationRequested && _queue.Reader.Count == 0) Idle?.Invoke();
            }
        }
    }

    private async Task PlayAsync(float[] samples, int rate, CancellationToken ct)
    {
        var device = AudioDevices.Resolve(OutputDeviceId, DataFlow.Render);
        if (device == null) return;
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        using var reader = new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
        var done = new TaskCompletionSource();
        output.PlaybackStopped += (_, _) => done.TrySetResult();
        output.Init(reader);
        _current = output;
        output.Play();
        if (!_notTransmitting.IsSet) output.Pause();
        await using (ct.Register(() => done.TrySetCanceled())) await done.Task;
        _current = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try { _current?.Stop(); } catch (InvalidOperationException) { }
        _engine.Dispose();
    }
}
