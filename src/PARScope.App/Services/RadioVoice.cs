using System.IO;
using System.Speech.Synthesis;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PARScope.App.Services;

/// <summary>
/// Text-to-speech for simulated pilots and tower using the offline Windows voices, played on the chosen
/// output device. Lines are spoken one at a time in order; playback pauses while the controller holds
/// push-to-talk, since you cannot hear the frequency while transmitting.
/// </summary>
public sealed class RadioVoice : IDisposable
{
    private readonly Channel<(string Text, string? Voice)> _queue = Channel.CreateUnbounded<(string, string?)>();
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _notTransmitting = new(true);
    private WasapiOut? _current;

    public RadioVoice()
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            Voices = synth.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                .Select(v => v.VoiceInfo.Name).ToList();
        }
        catch (PlatformNotSupportedException) { Voices = []; }
        PilotVoice = Voices.FirstOrDefault(v => v.Contains("David") || v.Contains("Mark")) ?? Voices.FirstOrDefault();
        TowerVoice = Voices.FirstOrDefault(v => v != PilotVoice) ?? PilotVoice;
        _ = Task.Run(PlayLoopAsync);
    }

    public IReadOnlyList<string> Voices { get; }
    public string? PilotVoice { get; set; }
    public string? TowerVoice { get; set; }
    /// <summary>Approach on the interphone: a third voice if one is installed, else the tower's.</summary>
    public string? ApproachVoice => Voices.FirstOrDefault(v => v != PilotVoice && v != TowerVoice) ?? TowerVoice;
    public string? OutputDeviceId { get; set; }
    public bool Enabled { get; set; } = true;

    public void Pilot(string text) => Enqueue(text, PilotVoice);
    public void Tower(string text) => Enqueue(text, TowerVoice);
    public void Approach(string text) => Enqueue(text, ApproachVoice);

    private void Enqueue(string text, string? voice)
    {
        if (Enabled && Voices.Count > 0) _queue.Writer.TryWrite((text, voice));
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
        await foreach (var (text, voice) in _queue.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                var wav = Synthesize(text, voice);
                _notTransmitting.Wait(_cts.Token);
                await PlayAsync(wav, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException) { }
        }
    }

    private static byte[] Synthesize(string text, string? voice)
    {
        using var synth = new SpeechSynthesizer { Rate = 1 };
        if (voice != null) synth.SelectVoice(voice);
        using var ms = new MemoryStream();
        synth.SetOutputToWaveStream(ms);
        synth.Speak(text);
        return ms.ToArray();
    }

    private async Task PlayAsync(byte[] wav, CancellationToken ct)
    {
        var device = AudioDevices.Resolve(OutputDeviceId, DataFlow.Render);
        if (device == null) return;
        using var reader = new WaveFileReader(new MemoryStream(wav));
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
    }
}
