using System.IO;
using System.Net.Http;

namespace PARScope.App.Services;

/// <summary>
/// Push-to-talk speech input: records from the chosen microphone while the key is held, then hands the
/// recording to the selected recogniser (Windows grammar or local Whisper) when it is released.
/// </summary>
public sealed class SpeechInput : IDisposable
{
    private readonly PttRecorder _recorder = new();
    private IPhraseRecognizer? _recognizer;
    private string? _recognizerId;
    private CancellationTokenSource _cts = new();
    private bool _recording;

    public event Action<string>? Heard;
    public event Action<string>? StateChanged;

    public string State { get; private set; } = "Voice not started";
    public bool Ready { get; private set; }

    public string? MicrophoneId
    {
        get => _recorder.DeviceId;
        set => _recorder.DeviceId = value;
    }

    public float InputLevel => _recorder.Level;

    /// <summary>Switches recognition engine, preparing it (and downloading a Whisper model if needed).</summary>
    public async Task UseRecognizerAsync(string id)
    {
        if (id == _recognizerId && Ready) return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _recognizer?.Dispose();
        _recognizer = null;
        Ready = false;
        _recognizerId = id;
        var rec = RecognizerOption.Create(id);
        try
        {
            string status = await rec.PrepareAsync(new Progress<string>(SetState), ct);
            if (ct.IsCancellationRequested) { rec.Dispose(); return; }
            _recognizer = rec;
            Ready = status.EndsWith("ready");
            SetState(Ready ? "Hold Space to talk" : status);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            rec.Dispose();
            SetState(ct.IsCancellationRequested ? State : "Speech engine unavailable: " + ex.Message);
        }
    }

    public void PressToTalk()
    {
        if (!Ready || _recording) return;
        _recording = _recorder.Start();
        SetState(_recording ? "Transmitting…" : "Microphone unavailable");
    }

    public async void ReleaseToTalk()
    {
        if (!_recording) return;
        _recording = false;
        var samples = _recorder.Stop();
        if (samples.Length < 16000 * 0.3) { SetState("Hold Space to talk"); return; } // under 0.3 s: a tap
        var rec = _recognizer;
        if (rec == null) return;
        SetState("Recognising…");
        try
        {
            string? text = await rec.RecognizeAsync(samples, _cts.Token);
            if (text is { Length: > 0 }) Heard?.Invoke(text);
            SetState(text is { Length: > 0 } ? "Hold Space to talk" : "Not understood, try again");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            SetState("Recognition failed: " + ex.Message);
        }
    }

    private void SetState(string s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _recorder.Dispose();
        _recognizer?.Dispose();
    }
}
