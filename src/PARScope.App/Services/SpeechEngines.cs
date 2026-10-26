using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace PARScope.App.Services;

/// <summary>Turns text into mono audio for the radio.</summary>
public interface ISpeechEngine : IDisposable
{
    string Name { get; }
    IReadOnlyList<string> Voices { get; }
    string DefaultPilot { get; }
    string DefaultTower { get; }
    string DefaultApproach { get; }
    (float[] Samples, int SampleRate) Synthesize(string text, string voice);
}

public sealed record VoiceEngineOption(string Id, string Name)
{
    public override string ToString() => Name;

    public static readonly IReadOnlyList<VoiceEngineOption> All =
    [
        new("windows", "Windows voices (built in)"),
        new("piper", "Neural: Piper LibriTTS-R (78 MB, fast, 900 voices)"),
        new("kokoro", "Neural: Kokoro (330 MB, most natural)"),
    ];

    /// <summary>Creates and prepares an engine, downloading its model on first use.</summary>
    public static async Task<ISpeechEngine> CreateAsync(string id, HttpClient http, IProgress<string>? progress, CancellationToken ct) => id switch
    {
        "piper" => await SherpaSpeechEngine.CreateAsync(SherpaSpeechEngine.Piper, http, progress, ct),
        "kokoro" => await SherpaSpeechEngine.CreateAsync(SherpaSpeechEngine.Kokoro, http, progress, ct),
        _ => new WindowsSpeechEngine(),
    };
}

/// <summary>The offline Windows (SAPI) voices.</summary>
public sealed class WindowsSpeechEngine : ISpeechEngine
{
    public WindowsSpeechEngine()
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            Voices = synth.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                .Select(v => v.VoiceInfo.Name).ToList();
        }
        catch (PlatformNotSupportedException) { Voices = []; }
        DefaultPilot = Voices.FirstOrDefault(v => v.Contains("David") || v.Contains("Mark")) ?? Voices.FirstOrDefault() ?? "";
        DefaultTower = Voices.FirstOrDefault(v => v != DefaultPilot) ?? DefaultPilot;
        DefaultApproach = Voices.FirstOrDefault(v => v != DefaultPilot && v != DefaultTower) ?? DefaultTower;
    }

    public string Name => "Windows voices";
    public IReadOnlyList<string> Voices { get; }
    public string DefaultPilot { get; }
    public string DefaultTower { get; }
    public string DefaultApproach { get; }

    public (float[] Samples, int SampleRate) Synthesize(string text, string voice)
    {
        using var synth = new SpeechSynthesizer { Rate = 1 };
        if (Voices.Contains(voice)) synth.SelectVoice(voice);
        using var ms = new MemoryStream();
        synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(22050, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Speak(text);
        var b = ms.ToArray();
        var s = new float[b.Length / 2];
        for (int i = 0; i < s.Length; i++) s[i] = BitConverter.ToInt16(b, i * 2) / 32768f;
        return (s, 22050);
    }

    public void Dispose() { }
}

/// <summary>
/// Local neural voices through sherpa-onnx: Kokoro-82M (Apache-2.0) or Piper LibriTTS-R (CC BY 4.0, 904
/// speakers). Models come from the sherpa-onnx releases on GitHub and are kept in the app data folder.
/// </summary>
public sealed class SherpaSpeechEngine : ISpeechEngine
{
    public sealed record ModelSpec(string Kind, string Folder, string Url, string Name, float Speed,
        IReadOnlyList<(string Label, int Speaker)> Voices, string Pilot, string Tower, string Approach);

    public static readonly ModelSpec Kokoro = new("kokoro", "kokoro-en-v0_19",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-en-v0_19.tar.bz2", "Kokoro", 1.1f,
        [
            ("Adam (US male)", 5), ("Michael (US male)", 6), ("George (UK male)", 9), ("Lewis (UK male)", 10),
            ("Sarah (US female)", 3), ("Bella (US female)", 1), ("Nicole (US female)", 2), ("Sky (US female)", 4),
            ("Emma (UK female)", 7), ("Isabella (UK female)", 8), ("Alloy (US female)", 0),
        ],
        "Michael (US male)", "Sarah (US female)", "Adam (US male)");

    /// <summary>A spread of LibriTTS-R speakers; the model has 904.</summary>
    public static readonly ModelSpec Piper = new("piper", "vits-piper-en_US-libritts_r-medium",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-libritts_r-medium.tar.bz2", "Piper LibriTTS-R", 1.05f,
        Enumerable.Range(0, 40).Select(i => ($"Voice {i + 1}", i * 22 + 3)).ToList(),
        "Voice 2", "Voice 11", "Voice 25");

    private readonly SherpaOnnx.OfflineTts _tts;
    private readonly ModelSpec _spec;
    private readonly Lock _lock = new();

    private SherpaSpeechEngine(ModelSpec spec, string dir)
    {
        _spec = spec;
        var config = new SherpaOnnx.OfflineTtsConfig();
        config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        string espeak = Path.Combine(dir, "espeak-ng-data");
        if (spec.Kind == "kokoro")
        {
            config.Model.Kokoro.Model = Path.Combine(dir, "model.onnx");
            config.Model.Kokoro.Voices = Path.Combine(dir, "voices.bin");
            config.Model.Kokoro.Tokens = Path.Combine(dir, "tokens.txt");
            config.Model.Kokoro.DataDir = espeak;
        }
        else
        {
            config.Model.Vits.Model = Directory.GetFiles(dir, "*.onnx")[0];
            config.Model.Vits.Tokens = Path.Combine(dir, "tokens.txt");
            config.Model.Vits.DataDir = espeak;
        }
        _tts = new SherpaOnnx.OfflineTts(config);
    }

    public static async Task<ISpeechEngine> CreateAsync(ModelSpec spec, HttpClient http, IProgress<string>? progress, CancellationToken ct)
    {
        string root = Path.Combine(AppSettings.DataFolder, "models", "tts");
        string dir = Path.Combine(root, spec.Folder);
        if (!File.Exists(Path.Combine(dir, "tokens.txt")) || !Directory.Exists(Path.Combine(dir, "espeak-ng-data")))
        {
            progress?.Report($"Downloading {spec.Name} voices…");
            Directory.CreateDirectory(root);
            string archive = Path.Combine(root, spec.Folder + ".tar.bz2");
            await using (var src = await http.GetStreamAsync(spec.Url, ct))
            await using (var dst = File.Create(archive + ".part"))
                await src.CopyToAsync(dst, ct);
            File.Move(archive + ".part", archive, true);
            progress?.Report($"Unpacking {spec.Name}…");
            // Windows 10+ ships bsdtar, which reads .tar.bz2.
            using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"), $"-xjf \"{archive}\" -C \"{root}\"")
                   { CreateNoWindow = true, UseShellExecute = false })!)
                await p.WaitForExitAsync(ct);
            File.Delete(archive);
            if (!File.Exists(Path.Combine(dir, "tokens.txt"))) throw new IOException($"{spec.Name} download could not be unpacked.");
        }
        progress?.Report($"Loading {spec.Name}…");
        return await Task.Run(() => new SherpaSpeechEngine(spec, dir), ct);
    }

    public string Name => _spec.Name;
    public IReadOnlyList<string> Voices => _spec.Voices.Select(v => v.Label).ToList();
    public string DefaultPilot => _spec.Pilot;
    public string DefaultTower => _spec.Tower;
    public string DefaultApproach => _spec.Approach;

    public (float[] Samples, int SampleRate) Synthesize(string text, string voice)
    {
        int sid = _spec.Voices.FirstOrDefault(v => v.Label == voice).Speaker;
        lock (_lock)
        {
            var audio = _tts.Generate(text, _spec.Speed, sid);
            return (audio.Samples, audio.SampleRate);
        }
    }

    public void Dispose() => _tts.Dispose();
}

/// <summary>
/// Makes speech sound like it came over a UHF/VHF radio: 300–3,000 Hz band-pass, soft compression, a low noise
/// floor, and the squelch opening and closing.
/// </summary>
public static class RadioEffect
{
    public static float[] Apply(float[] input, int rate, int seed)
    {
        var rng = new Random(seed);
        int open = rate * 40 / 1000, tail = rate * 130 / 1000;
        var x = new float[open + input.Length + tail];
        Array.Copy(input, 0, x, open, input.Length);
        var hp = Biquad.HighPass(rate, 300);
        var lp = Biquad.LowPass(rate, 3000);
        var hp2 = Biquad.HighPass(rate, 300);
        var lp2 = Biquad.LowPass(rate, 3000);
        float peak = 1e-6f;
        foreach (var v in input) peak = Math.Max(peak, Math.Abs(v));
        const float drive = 2.2f;
        float norm = (float)Math.Tanh(drive);
        for (int i = 0; i < x.Length; i++)
        {
            float s = x[i] / peak;
            bool squelchNoise = i < open || i >= open + input.Length;
            s += (float)((rng.NextDouble() - 0.5) * (squelchNoise ? 0.35 : 0.02));
            s = lp2.Process(hp2.Process(lp.Process(hp.Process(s))));
            x[i] = (float)Math.Tanh(drive * s) / norm * 0.75f;
        }
        return x;
    }

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1, _x2, _y1, _y2;

        public float Process(float x)
        {
            double y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return (float)y;
        }

        // RBJ audio EQ cookbook, Q = 1/√2.
        public static Biquad LowPass(int rate, double f)
        {
            double w = 2 * Math.PI * f / rate, c = Math.Cos(w), al = Math.Sin(w) / Math.Sqrt(2), a0 = 1 + al;
            return new((1 - c) / 2 / a0, (1 - c) / a0, (1 - c) / 2 / a0, -2 * c / a0, (1 - al) / a0);
        }

        public static Biquad HighPass(int rate, double f)
        {
            double w = 2 * Math.PI * f / rate, c = Math.Cos(w), al = Math.Sin(w) / Math.Sqrt(2), a0 = 1 + al;
            return new((1 + c) / 2 / a0, -(1 + c) / a0, (1 + c) / 2 / a0, -2 * c / a0, (1 - al) / a0);
        }
    }
}
