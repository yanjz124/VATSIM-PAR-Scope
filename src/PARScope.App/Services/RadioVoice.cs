using System.Speech.Synthesis;

namespace PARScope.App.Services;

/// <summary>
/// Text-to-speech for simulated pilots and tower, using the offline Windows voices. Pilot and tower get
/// different voices when more than one is installed. Output pauses while the controller holds
/// push-to-talk, since you cannot hear the frequency while transmitting.
/// </summary>
public sealed class RadioVoice : IDisposable
{
    private readonly SpeechSynthesizer? _synth;
    private readonly VoiceInfo? _pilot;
    private readonly VoiceInfo? _tower;
    private bool _paused;

    public RadioVoice()
    {
        try
        {
            _synth = new SpeechSynthesizer();
            _synth.SetOutputToDefaultAudioDevice();
            _synth.Rate = 1;
            var voices = _synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo)
                .Where(v => v.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)).ToList();
            _pilot = voices.FirstOrDefault(v => v.Gender == VoiceGender.Male) ?? voices.FirstOrDefault();
            _tower = voices.FirstOrDefault(v => v != _pilot) ?? _pilot;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            _synth = null;
        }
    }

    public bool Available => _synth != null && _pilot != null;
    public bool Enabled { get; set; } = true;

    public void Pilot(string text) => Speak(text, _pilot);
    public void Tower(string text) => Speak(text, _tower);

    private void Speak(string text, VoiceInfo? voice)
    {
        if (!Enabled || _synth == null || voice == null) return;
        var pb = new PromptBuilder();
        pb.StartVoice(voice);
        pb.AppendText(text);
        pb.EndVoice();
        _synth.SpeakAsync(pb); // queued in order
    }

    public void PauseForTransmit()
    {
        if (_synth == null || _paused) return;
        _synth.Pause();
        _paused = true;
    }

    public void ResumeAfterTransmit()
    {
        if (_synth == null || !_paused) return;
        _synth.Resume();
        _paused = false;
    }

    public void Dispose()
    {
        if (_synth == null) return;
        if (_paused) _synth.Resume();
        _synth.SpeakAsyncCancelAll();
        _synth.Dispose();
    }
}
