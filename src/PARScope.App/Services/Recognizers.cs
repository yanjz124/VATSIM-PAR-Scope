using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using Whisper.net;
using Whisper.net.Ggml;

namespace PARScope.App.Services;

/// <summary>Turns one push-to-talk recording (16 kHz mono) into text.</summary>
public interface IPhraseRecognizer : IDisposable
{
    string Name { get; }
    /// <summary>Prepares the engine (e.g. downloads a model). Returns a status message.</summary>
    Task<string> PrepareAsync(IProgress<string>? progress, CancellationToken ct);
    Task<string?> RecognizeAsync(float[] samples16k, CancellationToken ct);
}

public sealed record RecognizerOption(string Id, string Name)
{
    public override string ToString() => Name;

    public static readonly IReadOnlyList<RecognizerOption> All =
    [
        new("windows", "Windows Speech (grammar, offline)"),
        new("whisper-tiny.en", "Whisper tiny.en (~75 MB, fastest)"),
        new("whisper-base.en", "Whisper base.en (~140 MB)"),
        new("whisper-small.en", "Whisper small.en (~470 MB, most accurate)"),
    ];

    public static IPhraseRecognizer Create(string id) => id switch
    {
        "whisper-tiny.en" => new WhisperRecognizer(GgmlType.TinyEn),
        "whisper-base.en" => new WhisperRecognizer(GgmlType.BaseEn),
        "whisper-small.en" => new WhisperRecognizer(GgmlType.SmallEn),
        _ => new WindowsGrammarRecognizer(),
    };
}

/// <summary>
/// OpenAI Whisper running locally (whisper.cpp via Whisper.net), on the graphics card (Vulkan) when one is
/// usable, otherwise the CPU. Each call gets a fresh processor, with the audio context cut to the clip's
/// length (Whisper otherwise always works on a 30 s window) and a short prompt naming the phraseology and
/// the callsigns on frequency, so they come out spelled right. Text then goes through the same
/// phraseology parser as typed input.
/// </summary>
public sealed class WhisperRecognizer(GgmlType model) : IPhraseRecognizer
{
    // Whisper's prompt steers it toward these words: the calls a PAR final controller repeats every few seconds,
    // as they are actually said ("the glide", "miles from touchdown", "correcting slowly").
    private const string BasePrompt =
        "PAR final controller. Turn left heading two four zero. Approaching glidepath, wheels should be down. Begin descent. " +
        "On glide. Slightly above the glide and coming down. Below the glide and holding. Well above glidepath. " +
        "On course. Slightly left of course and correcting slowly. Right of course and going further right. " +
        "Four miles from touchdown. Two miles from touchdown. One mile from touchdown. Decision height. " +
        "Do not acknowledge further transmissions. Radar contact. Execute missed approach. Cleared to land.";

    private WhisperFactory? _factory;

    public string Name => $"Whisper {model}";

    /// <summary>"GPU" or "CPU", once loaded.</summary>
    public string? Backend { get; private set; }

    /// <summary>Spoken callsigns currently on frequency, e.g. "Bones six two, Navy one two three four".</summary>
    public string Vocabulary { get; set; } = "";

    /// <summary>
    /// How many phrases may be decoded at once (each processor shares the loaded model): two on a graphics
    /// card or a CPU with 8+ cores, otherwise one.
    /// </summary>
    public int MaxParallel => Backend == "GPU" || Environment.ProcessorCount >= 8 ? 2 : 1;

    /// <summary>CPU threads per decode, leaving two cores for the scope, sim and audio so they never stall.</summary>
    private int Threads => Math.Clamp((Environment.ProcessorCount - 2) / MaxParallel, 1, 8);

    private string ModelPath => Path.Combine(AppSettings.DataFolder, "models", $"ggml-{model.ToString().ToLowerInvariant()}.bin");

    public async Task<string> PrepareAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (_factory != null) return $"{Name} ready";
        if (!File.Exists(ModelPath))
        {
            progress?.Report($"Downloading {Name} model…");
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            string tmp = ModelPath + ".part";
            await using (var src = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model, cancellationToken: ct))
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst, ct);
            File.Move(tmp, ModelPath, overwrite: true);
        }
        progress?.Report($"Loading {Name}…");
        await Task.Run(async () =>
        {
            _factory = WhisperFactory.FromPath(ModelPath);
            // Warm up short and long clips so the first real calls are not slowed by GPU start-up.
            foreach (int seconds in new[] { 1, 12 })
            {
                using var p = Builder(16000 * seconds).Build();
                await foreach (var _ in p.ProcessAsync(new float[16000 * seconds], ct)) { }
            }
        }, ct);
        Backend = Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary is Whisper.net.LibraryLoader.RuntimeLibrary.Cpu
            or Whisper.net.LibraryLoader.RuntimeLibrary.CpuNoAvx or null ? "CPU" : "GPU";
        return $"{Name} ready";
    }

    private WhisperProcessorBuilder Builder(int samples)
    {
        // 50 audio-context frames per second of audio, plus headroom, rounded up to a multiple of 128 so
        // the GPU sees only a few sizes.
        int ctx = Math.Clamp(((int)Math.Ceiling(samples / 16000.0 * 50) + 64 + 127) / 128 * 128, 128, 1500);
        string prompt = Vocabulary.Length > 0 ? $"{BasePrompt} {Vocabulary}." : BasePrompt;
        // A phrase is a few words a second; cap the tokens so a decoder stuck repeating itself on noise stops
        // quickly, and decode once (no temperature fallback, which re-runs a hard phrase up to five more times).
        int maxTokens = Math.Clamp((int)(samples / 16000.0 * 8) + 16, 32, 224);
        return _factory!.CreateBuilder().WithLanguage("en").WithPrompt(prompt).WithNoContext().WithSingleSegment()
            .WithThreads(Threads).WithAudioContextSize(ctx).WithMaxTokensPerSegment(maxTokens).WithTemperatureInc(0f);
    }

    public async Task<string?> RecognizeAsync(float[] samples16k, CancellationToken ct)
    {
        if (_factory == null) return null;
        // Very short clips confuse the decoder; pad to one second.
        if (samples16k.Length < 16000) Array.Resize(ref samples16k, 16000);
        var audio = samples16k;
        string text = await Task.Run(async () =>
        {
            using var processor = Builder(audio.Length).Build();
            var sb = new System.Text.StringBuilder();
            await foreach (var seg in processor.ProcessAsync(audio, ct)) sb.Append(seg.Text);
            return sb.ToString().Trim();
        }, ct);
        return text.Length == 0 || text.StartsWith('[') || text.StartsWith('(') ? null : WithoutRepeats(text);
    }

    /// <summary>Small Whisper models sometimes repeat the last phrase; drop a phrase identical to the one before.</summary>
    internal static string WithoutRepeats(string text)
    {
        var phrases = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.,!?])\s+");
        var kept = new List<string>();
        foreach (var p in phrases)
        {
            string key = new string(p.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (kept.Count > 0 && key.Length > 0 && key == new string(kept[^1].Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()) continue;
            kept.Add(p);
        }
        return string.Join(" ", kept).TrimStart(' ', ',', '.');
    }

    public void Dispose() => _factory?.Dispose();
}

/// <summary>
/// Windows offline recognizer with a closed grammar of PAR phrases: fast and accurate for standard
/// phraseology, but it only hears what the grammar allows.
/// </summary>
public sealed class WindowsGrammarRecognizer : IPhraseRecognizer
{
    private SpeechRecognitionEngine? _engine;

    public string Name => "Windows Speech";

    public Task<string> PrepareAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (_engine != null) return Task.FromResult("Windows Speech ready");
        var info = SpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(r => r.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        if (info == null) return Task.FromResult("No English Windows speech recognizer installed (Settings › Time & language › Speech)");
        _engine = new SpeechRecognitionEngine(info);
        _engine.LoadGrammar(BuildGrammar(info.Culture));
        return Task.FromResult("Windows Speech ready");
    }

    public Task<string?> RecognizeAsync(float[] samples16k, CancellationToken ct)
    {
        var engine = _engine;
        if (engine == null) return Task.FromResult<string?>(null);
        return Task.Run(() =>
        {
            var pcm = new byte[samples16k.Length * 2];
            for (int i = 0; i < samples16k.Length; i++)
            {
                short v = (short)Math.Clamp(samples16k[i] * 32767f, short.MinValue, short.MaxValue);
                pcm[2 * i] = (byte)v;
                pcm[2 * i + 1] = (byte)(v >> 8);
            }
            lock (engine)
            {
                engine.SetInputToAudioStream(new MemoryStream(pcm), new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                var result = engine.Recognize();
                engine.SetInputToNull();
                return result is { Confidence: >= 0.45f } ? result.Text : null;
            }
        }, ct);
    }

    public void Dispose() => _engine?.Dispose();

    /// <summary>One transmission is a sequence of up to six phrases, e.g.
    /// "turn right heading two four four, slightly below glidepath and coming up, on course".</summary>
    private static readonly string[] NatoWords =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliett", "kilo", "lima", "mike",
        "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "x-ray", "yankee", "zulu",
    ];

    /// <summary>Spoken callsign words: service prefixes, mission callsigns and squadron callwords.</summary>
    private static string[] CallsignWords() =>
        new[] { "navy", "marine", "army", "air force", "coast guard", "reach", "pat", "convoy" }
            .Concat(PARScope.Core.Sim.MilitaryCallsigns.All.Select(m => m.Callword.ToLowerInvariant()))
            .Distinct().ToArray();

    private static Grammar BuildGrammar(CultureInfo culture)
    {
        var digit = new Choices("zero", "one", "two", "three", "tree", "four", "five", "fife", "six", "seven", "eight", "nine", "niner");
        GrammarBuilder G(params object[] parts)
        {
            var g = new GrammarBuilder { Culture = culture };
            foreach (var p in parts)
            {
                switch (p)
                {
                    case string s: g.Append(s); break;
                    case Choices c: g.Append(c); break;
                    case GrammarBuilder b: g.Append(b); break;
                }
            }
            return g;
        }
        GrammarBuilder Opt(object part) { var g = new GrammarBuilder { Culture = culture }; g.Append(part is string s ? new GrammarBuilder(s) : part is Choices c ? new GrammarBuilder(c) : (GrammarBuilder)part, 0, 1); return g; }

        var heading = G(Opt(G("turn", new Choices("left", "right"))), "heading", digit, digit, digit);
        var gpSide = new Choices("above", "below");
        var gpTrend = Opt(G("and", new Choices(G("coming down"), G("coming up"), G("holding"), G("going", gpSide, Opt("rapidly")))));
        var glidepath = new Choices(
            G("on glidepath"),
            G(Opt(new Choices("slightly", "well")), gpSide, "glidepath", gpTrend),
            G("going", gpSide, "glidepath", Opt("rapidly")));
        var side = new Choices("left", "right");
        var course = new Choices(
            G("on course"),
            G(Opt(new Choices("slightly", "well")), side, "of course", Opt(G("and", new Choices("correcting", "holding")))),
            G("going", side, "of course"));
        var misc = new Choices(
            G(new Choices("begin", "commence", "initiate"), "descent"),
            G("level off", Opt("your aircraft")),
            G("resume normal rate of descent"),
            G("adjust rate of descent"),
            G("acknowledge"),
            G(new Choices("drifting", "paralleling"), Opt(new Choices("left", "right")), Opt("of course")),
            G("approaching glidepath"),
            G(new Choices("execute missed approach", "go around", "wave off", "tower directs wave off")),
            G(new Choices("too low", "too high", "too far left", "too far right"), "for safe approach"),
            G("if runway or approach lights not in sight execute missed approach"),
            G("wheels should be down"),
            G("how do you hear me"),
            G("this will be a p a r approach to runway", digit, digit, Opt(new Choices("left", "right", "center"))),
            G("wind", digit, digit, digit, "at", digit, Opt(digit)),
            G("wind calm"),
            G(Opt(G("runway", digit, digit, Opt(new Choices("left", "right", "center")))),
              new Choices("cleared to land", "cleared touch and go", "cleared low approach", "cleared for the option")),
            G(new Choices(G("in the event of missed approach"), G("missed approach instructions"), G("your missed approach procedure is"),
                          G("after completing", new Choices("low approach", "touch and go", "the option"))),
              Opt(G("climb and maintain", digit, "thousand", Opt(G(digit, "hundred")))),
              Opt(G(Opt(G("turn", new Choices("left", "right"))), "heading", digit, digit, digit))),
            G("climb and maintain", digit, "thousand", Opt(G(digit, "hundred"))),
            G(new Choices("contact", "switch to"), new Choices("approach", "departure", "tower", "ground"), Opt("after landing")),
            // "(callsign) radar contact (operating initials)": initials are any two phonetic letters.
            G(Opt(G(new Choices(CallsignWords()), Opt(digit), Opt(digit), Opt(digit), Opt(digit), Opt(digit))), "radar contact",
              Opt(G(new Choices(NatoWords), new Choices(NatoWords)))),
            // Interphone: "tower, final, navy one two three four, four mile final, full stop".
            G(new Choices("tower", "local", "approach", "departure"), Opt("final"),
              Opt(G(new Choices(CallsignWords()), Opt(digit), Opt(digit), Opt(digit), Opt(digit), Opt(digit))),
              Opt(G(digit, "mile final")),
              Opt(new Choices("full stop", "touch and go", "low approach", "option", "missed approach", "request landing clearance"))),
            G("tower clearance not received"),
            G("tower clearance canceled"),
            G("do not acknowledge", Opt(G("further", new Choices("transmissions", "instructions", "calls")))),
            G("no need to acknowledge"),
            G("acknowledgement not required"),
            G(digit, new Choices("mile", "miles"), "from touchdown"),
            G("at decision height"),
            G("at decision altitude"),
            G("over approach lights"),
            G("over landing threshold"));

        var phrase = new Choices(heading, glidepath, course, misc);
        var utterance = new GrammarBuilder { Culture = culture };
        utterance.Append(new GrammarBuilder(phrase), 1, 6);
        return new Grammar(utterance) { Name = "PAR final" };
    }
}
