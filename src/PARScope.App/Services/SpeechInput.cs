using System.IO;
using System.Net.Http;

namespace PARScope.App.Services;

/// <summary>
/// Push-to-talk speech input: records from the chosen microphone while the key is held. With Whisper,
/// each phrase is transcribed as soon as the controller pauses, while still talking, so on release only
/// the last phrase is left; the pieces are joined into one transmission. The Windows grammar engine
/// (already fast, and needing whole phrases) gets the full recording on release.
/// Phrases are decoded on worker threads, several at once where the machine allows, each with a time limit
/// so one bad phrase cannot hold up the next transmission; transmissions are still delivered in order.
/// </summary>
public sealed class SpeechInput : IDisposable
{
    private readonly PttRecorder _recorder = new();
    private IPhraseRecognizer? _recognizer;
    private string? _recognizerId;
    private CancellationTokenSource _cts = new();
    private bool _recording;
    private List<Task<string?>> _pieces = [];
    private SemaphoreSlim _gate = new(1, 1);
    /// <summary>The previous transmission's delivery: each waits for the one before, so commands keep their order.</summary>
    private Task _delivered = Task.CompletedTask;
    private int _sentSamples;

    /// <summary>Time from releasing the key to the transmission being heard, for the session log.</summary>
    public event Action<string, double>? HeardAfter;
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
            _gate = new SemaphoreSlim(rec is WhisperRecognizer w ? w.MaxParallel : 1);
            Ready = status.EndsWith("ready");
            if (Ready) _recorder.Open(); // keep the microphone open so the first word is not clipped
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
        _pieces = [];
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
                _pieces.Add(RecognizePieceAsync(samples[_sentSamples..cut], _cts.Token));
                _sentSamples = cut;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// A phrase, transcribed on a worker thread once a decoder is free. A decode that runs far longer than the
    /// audio (Whisper stuck on noise) is abandoned so it cannot hold up later transmissions.
    /// </summary>
    private async Task<string?> RecognizePieceAsync(float[] piece, CancellationToken ct)
    {
        var rec = _recognizer;
        if (rec == null || !Vad.HasSpeech(piece)) return null;
        var gate = _gate;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(Math.Max(8, piece.Length / 16000.0 * 4)));
            string? text = await Task.Run(() => rec.RecognizeAsync(piece, limit.Token), limit.Token).ConfigureAwait(false);
            if (_recording && text is { Length: > 0 }) SetState($"Transmitting… {text}");
            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // timed out: drop this phrase
        finally { gate.Release(); }
    }

    public async void ReleaseToTalk()
    {
        if (!_recording) return;
        _recording = false;
        // A quarter second more: people let go of the key as the last word ends ("from touch…").
        await Task.Delay(250);
        _liveCts?.Cancel();
        if (_live != null) await _live;
        _live = null;
        var samples = _recorder.Stop();
        if (samples.Length < 16000 * 0.3) { SetState(ReadyText); return; } // under 0.3 s: a tap
        var rec = _recognizer;
        if (rec == null) return;
        SetState("Recognizing…");
        var released = System.Diagnostics.Stopwatch.StartNew();
        // This transmission's own pieces: the next one can start (and decode) while this one finishes.
        var pieces = _pieces;
        Task<string?> recognized;
        if (rec is WhisperRecognizer)
        {
            // Only the part after the last pause is still to do; the rest was transcribed while talking.
            if (_sentSamples < samples.Length) pieces.Add(RecognizePieceAsync(samples[_sentSamples..], _cts.Token));
            recognized = JoinAsync(pieces);
        }
        else recognized = RecognizePieceAsync(samples, _cts.Token);

        var previous = _delivered;
        var delivery = DeliverAsync(previous, recognized, released);
        _delivered = delivery;
        await delivery;
    }

    private static async Task<string?> JoinAsync(List<Task<string?>> pieces)
    {
        var parts = await Task.WhenAll(pieces).ConfigureAwait(false);
        return string.Join(" ", parts.Where(p => p is { Length: > 0 }));
    }

    /// <summary>Hands a transmission over once it is recognized and every earlier one has been delivered.</summary>
    private async Task DeliverAsync(Task previous, Task<string?> recognized, System.Diagnostics.Stopwatch released)
    {
        string? text = null;
        try { text = await recognized; }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            SetState("Recognition failed: " + ex.Message);
        }
        try { await previous; } catch { /* an earlier failure is already reported */ }
        if (text is { Length: > 0 })
        {
            Heard?.Invoke(text);
            HeardAfter?.Invoke(text, released.Elapsed.TotalSeconds);
        }
        if (!_recording) SetState(text is { Length: > 0 } ? ReadyText : "Not understood, try again");
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
