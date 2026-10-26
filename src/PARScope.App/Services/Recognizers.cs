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
/// OpenAI Whisper running locally (whisper.cpp via Whisper.net). Free-form recognition, primed with
/// PAR phraseology so headings and glidepath terms come out right; the text then goes through the same
/// phraseology parser as typed input.
/// </summary>
public sealed class WhisperRecognizer(GgmlType model) : IPhraseRecognizer
{
    private const string Prompt =
        "PAR final controller. Turn left heading two four zero. Turn right heading three six zero. " +
        "Slightly above glidepath and coming down. Well below glidepath. On glidepath. Slightly left of course and correcting. " +
        "On course. Begin descent. Approaching glidepath. Wheels should be down. How do you hear me. " +
        "Do not acknowledge further transmissions. Too low for safe approach, execute missed approach. " +
        "Wind three one zero at seven, runway two four right, cleared to land. Navy, Marine, Army, Reach, niner. " +
        "In the event of missed approach, climb and maintain two thousand, turn left heading one eight zero. Contact approach. " +
        "Tower, final, Navy one two three four, four mile final, full stop. Approach, final, Banshee two one, missed approach, runway heading, climbing to two thousand.";

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public string Name => $"Whisper {model}";

    private string ModelPath => Path.Combine(AppSettings.DataFolder, "models", $"ggml-{model.ToString().ToLowerInvariant()}.bin");

    public async Task<string> PrepareAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (_processor != null) return $"{Name} ready";
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
        await Task.Run(() =>
        {
            _factory = WhisperFactory.FromPath(ModelPath);
            _processor = _factory.CreateBuilder().WithLanguage("en").WithPrompt(Prompt).WithNoContext().Build();
        }, ct);
        return $"{Name} ready";
    }

    public async Task<string?> RecognizeAsync(float[] samples16k, CancellationToken ct)
    {
        if (_processor == null) return null;
        var sb = new System.Text.StringBuilder();
        await foreach (var seg in _processor.ProcessAsync(samples16k, ct)) sb.Append(seg.Text);
        string text = sb.ToString().Trim();
        return text.Length == 0 || text.StartsWith('[') ? null : text;
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
    }
}

/// <summary>
/// Windows offline recogniser with a closed grammar of PAR phrases: fast and accurate for standard
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
        if (info == null) return Task.FromResult("No English Windows speech recogniser installed (Settings › Time & language › Speech)");
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
            G("begin descent"),
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
            G(new Choices(G("in the event of missed approach"), G("missed approach instructions")),
              Opt(G("climb and maintain", digit, "thousand", Opt(G(digit, "hundred")))),
              Opt(G(Opt(G("turn", new Choices("left", "right"))), "heading", digit, digit, digit))),
            G("climb and maintain", digit, "thousand", Opt(G(digit, "hundred"))),
            G(new Choices("contact", "switch to"), new Choices("approach", "departure")),
            G("radar contact"),
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
