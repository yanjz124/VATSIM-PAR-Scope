using System.IO;
using System.Net.Http;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PARScope.App.Services;

/// <summary>
/// The simulated radio and interphone: pilot, tower and approach voices from the selected speech engine,
/// played on the chosen output device through an optional radio effect. Each line starts synthesizing as
/// soon as it is queued (so a slow neural voice is ready by the time the pilot would answer) and plays at its
/// due time. Two lanes play independently, each one line at a time in order: the radio (pilots on the final
/// frequency), which pauses while the controller holds push-to-talk, and the landline (tower and approach on
/// the interphone), which a radio transmission does not block.
/// </summary>
public sealed class RadioVoice : IDisposable
{
    private sealed record Line(Task<(float[] Samples, int Rate)> Audio, DateTime Due, Action? OnStart, int Generation, Action? OnDone = null);

    private sealed class Lane(bool pausesForTransmit)
    {
        public readonly Channel<Line> Queue = Channel.CreateUnbounded<Line>();
        public readonly bool PausesForTransmit = pausesForTransmit;
        public volatile WasapiOut? Current;
        public volatile bool Playing;
        /// <summary>Lines "said" with the voices off, still taking their time.</summary>
        public int Silent;
        public bool Busy => Playing || Queue.Reader.Count > 0 || Volatile.Read(ref Silent) > 0;
    }

    /// <summary>Lines queued before the last <see cref="StopAll"/> are dropped.</summary>
    private int _generation;

    /// <summary>Raised (on the audio thread) when the last queued line has finished or was stopped.</summary>
    public event Action? Idle;

    private readonly Lane _radio = new(pausesForTransmit: true), _landline = new(pausesForTransmit: false);
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _notTransmitting = new(true);
    private ISpeechEngine _engine = new WindowsSpeechEngine();

    public RadioVoice()
    {
        _ = Task.Run(() => PlayLoopAsync(_radio));
        _ = Task.Run(() => PlayLoopAsync(_landline));
    }

    public string EngineName => _engine.Name;
    public IReadOnlyList<string> Voices => _engine.Voices;
    public string? PilotVoice { get; set; }
    public string? TowerVoice { get; set; }
    public string? ApproachVoice { get; set; }
    /// <summary>The AI final controller.</summary>
    public string? FinalVoice { get; set; }
    /// <summary>Something is queued or playing on the final frequency, or on the landline.</summary>
    public bool RadioBusy => _radio.Busy;
    public bool LandlineBusy => _landline.Busy;
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
        FinalVoice = engine.Voices.FirstOrDefault(v => v != PilotVoice && v != TowerVoice && v != ApproachVoice) ?? ApproachVoice;
        old.Dispose();
    }

    public void Pilot(string text, string? callsign = null, double delaySec = 0, Action? onStart = null) =>
        Enqueue(_radio, text, PilotVoiceFor(callsign), delaySec, onStart);
    /// <summary>
    /// Tower on the landline. <paramref name="opensCall"/>: tower is calling you, so the line's tone plays first;
    /// a reply to your call has none. <paramref name="landline"/> false queues it after the radio lines (voice test).
    /// </summary>
    public void Tower(string text, double delaySec = 0, Action? onStart = null, bool landline = true, bool opensCall = false) =>
        Enqueue(landline ? _landline : _radio, text, TowerVoice, delaySec, onStart, opensCall);
    public void Approach(string text, double delaySec = 0, Action? onStart = null, bool landline = true, bool opensCall = false) =>
        Enqueue(landline ? _landline : _radio, text, ApproachVoice, delaySec, onStart, opensCall);
    /// <summary>
    /// The AI final controller, on the final frequency or (<paramref name="landline"/>) calling tower or approach.
    /// <paramref name="onDone"/> runs once it has been said, as when the controller lets go of push-to-talk.
    /// <paramref name="opensCall"/>: a landline call final starts (not an answer to approach's), so the tone plays first.
    /// </summary>
    public void Final(string text, bool landline, bool opensCall, Action? onStart = null, Action? onDone = null) =>
        Enqueue(landline ? _landline : _radio, text, FinalVoice, 0, onStart, landline && opensCall, onDone);

    /// <summary>The voice a pilot speaks with: their own (stable per callsign, never a controller's), or the chosen pilot voice.</summary>
    private string? PilotVoiceFor(string? callsign)
    {
        if (!VaryPilotVoices || callsign == null) return PilotVoice;
        var pool = Voices.Where(v => v != TowerVoice && v != ApproachVoice && v != FinalVoice).ToList();
        if (pool.Count == 0) return PilotVoice;
        uint h = 2166136261;
        foreach (char c in callsign.ToUpperInvariant()) h = (h ^ c) * 16777619;
        return pool[(int)(h % (uint)pool.Count)];
    }

    private void Enqueue(Lane lane, string text, string? voice, double delaySec, Action? onStart, bool opensCall = false, Action? onDone = null)
    {
        var due = DateTime.UtcNow.AddSeconds(delaySec);
        if (!Enabled || Voices.Count == 0)
        {
            // Silent: still report the line when it would have been heard, and take about as long as saying it.
            if (onStart != null) _ = Task.Delay(TimeSpan.FromSeconds(delaySec)).ContinueWith(_ => onStart());
            if (onDone != null)
            {
                Interlocked.Increment(ref lane.Silent);
                double speak = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.8;
                _ = Task.Delay(TimeSpan.FromSeconds(delaySec + speak)).ContinueWith(_ =>
                {
                    Interlocked.Decrement(ref lane.Silent);
                    onDone();
                });
            }
            return;
        }
        var engine = _engine;
        bool effect = RadioEffectEnabled, phone = lane == _landline, tone = phone && opensCall;
        var audio = Task.Run(() =>
        {
            var (s, rate) = engine.Synthesize(text, voice ?? engine.DefaultPilot);
            // The landline sounds like a telephone line, not a radio.
            if (effect) s = phone ? TelephoneEffect.Apply(s, rate) : RadioEffect.Apply(s, rate, text.GetHashCode());
            // A landline call opens with the voice switch's tone; the replies in that exchange do not.
            if (tone) s = [.. TelephoneEffect.OverrideTone(rate), .. s];
            return (s, rate);
        });
        lane.Queue.Writer.TryWrite(new Line(audio, due, onStart, _generation, onDone));
    }

    /// <summary>Stops what is playing and drops everything queued.</summary>
    public void StopAll()
    {
        Interlocked.Increment(ref _generation);
        foreach (var lane in new[] { _radio, _landline })
            try { lane.Current?.Stop(); } catch (InvalidOperationException) { }
    }

    /// <summary>Push-to-talk held: the radio waits (the frequency is blocked while you transmit); the landline does not.</summary>
    public void PauseForTransmit()
    {
        _notTransmitting.Reset();
        try { _radio.Current?.Pause(); } catch (InvalidOperationException) { }
    }

    public void ResumeAfterTransmit()
    {
        _notTransmitting.Set();
        try { _radio.Current?.Play(); } catch (InvalidOperationException) { }
    }

    private async Task PlayLoopAsync(Lane lane)
    {
        await foreach (var line in lane.Queue.Reader.ReadAllAsync(_cts.Token))
        {
            lane.Playing = true;
            try
            {
                if (line.Generation != _generation) continue;
                var (samples, rate) = await line.Audio;
                var wait = line.Due - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _cts.Token);
                if (lane.PausesForTransmit) _notTransmitting.Wait(_cts.Token);
                if (line.Generation != _generation) continue;
                line.OnStart?.Invoke();
                await PlayAsync(lane, samples, rate, _cts.Token);
                line.OnDone?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
            {
                line.OnStart?.Invoke();
                line.OnDone?.Invoke();
            }
            finally
            {
                lane.Playing = lane.Queue.Reader.Count > 0;
                var other = lane == _radio ? _landline : _radio;
                if (!_cts.IsCancellationRequested && !lane.Playing && !other.Playing && other.Queue.Reader.Count == 0) Idle?.Invoke();
            }
        }
    }

    private async Task PlayAsync(Lane lane, float[] samples, int rate, CancellationToken ct)
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
        lane.Current = output;
        output.Play();
        if (lane.PausesForTransmit && !_notTransmitting.IsSet) output.Pause();
        await using (ct.Register(() => done.TrySetCanceled())) await done.Task;
        lane.Current = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var lane in new[] { _radio, _landline })
        {
            lane.Queue.Writer.TryComplete();
            try { lane.Current?.Stop(); } catch (InvalidOperationException) { }
        }
        _engine.Dispose();
    }
}
