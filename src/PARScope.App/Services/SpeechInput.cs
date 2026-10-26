using System.IO;
using System.Net.Http;

namespace PARScope.App.Services;

/// <summary>
/// Push-to-talk speech input: records from the chosen microphone while the key is held. With Whisper,
/// each phrase is transcribed as soon as the controller pauses, while still talking, so on release only
/// the last phrase is left; the pieces are joined into one transmission. The Windows grammar engine
/// (already fast, and needing whole phrases) gets the full recording on release.
/// </summary>
public sealed class SpeechInput : IDisposable
{
    private readonly PttRecorder _recorder = new();
    private IPhraseRecognizer? _recognizer;
    private string? _recognizerId;
    private CancellationTokenSource _cts = new();
    private bool _recording;
    private readonly List<Task<string?>> _pieces = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _sentSamples;
    private CancellationTokenSource? _liveCts;
    private Task? _live;

    /// <summary>Callsigns on frequency, as spoken, to help Whisper spell them.</summary>
    public string Vocabulary
    {
        get => (_recognizer as WhisperRecognizer)?.Vocabulary ?? "";
        set { if (_recognizer is WhisperRecognizer w) w.Vocabulary = value; }
    }

    public event Action<string>? Heard;
    public event Action<string>? StateChanged;

    public string State { get; private set; } = "Voice not started";
    /// <summary>Name of the push-to-talk key, for the status text.</summary>
    public string PttKeyName { get; set; } = "Space";
    private string ReadyText => $"Hold {PttKeyName} to talk";
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
            SetState(Ready ? ReadyText + (rec is WhisperRecognizer { Backend: { } b } ? $" · Whisper on {b}" : "") : status);
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
        _pieces.Clear();
        _sentSamples = 0;
        if (_recording && _recognizer is WhisperRecognizer)
        {
            _liveCts = new CancellationTokenSource();
            _live = LiveAsync(_liveCts.Token);
        }
    }

    /// <summary>While talking: cut the recording at each pause and start transcribing the finished phrase.</summary>
    private async Task LiveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct);
                var samples = await Task.Run(_recorder.Snapshot, ct);
                int cut = Vad.FindPause(samples, _sentSamples);
                if (cut <= _sentSamples) continue;
                _pieces.Add(RecognizePieceAsync(samples[_sentSamples..cut]));
                _sentSamples = cut;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Pieces are transcribed one at a time, in order.</summary>
    private async Task<string?> RecognizePieceAsync(float[] piece)
    {
        var rec = _recognizer;
        if (rec == null || !Vad.HasSpeech(piece)) return null;
        await _gate.WaitAsync();
        try
        {
            string? text = await rec.RecognizeAsync(piece, _cts.Token);
            if (_recording && text is { Length: > 0 }) SetState($"Transmitting… {text}");
            return text;
        }
        finally { _gate.Release(); }
    }

    public async void ReleaseToTalk()
    {
        if (!_recording) return;
        _recording = false;
        _liveCts?.Cancel();
        if (_live != null) await _live;
        _live = null;
        var samples = _recorder.Stop();
        if (samples.Length < 16000 * 0.3) { SetState(ReadyText); return; } // under 0.3 s: a tap
        var rec = _recognizer;
        if (rec == null) return;
        SetState("Recognising…");
        try
        {
            string? text;
            if (rec is WhisperRecognizer)
            {
                // Only the part after the last pause is still to do; the rest was transcribed while talking.
                if (_sentSamples < samples.Length) _pieces.Add(RecognizePieceAsync(samples[_sentSamples..]));
                var parts = await Task.WhenAll(_pieces);
                text = string.Join(" ", parts.Where(p => p is { Length: > 0 }));
            }
            else text = await rec.RecognizeAsync(samples, _cts.Token);
            if (text is { Length: > 0 }) Heard?.Invoke(text);
            SetState(text is { Length: > 0 } ? ReadyText : "Not understood, try again");
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
