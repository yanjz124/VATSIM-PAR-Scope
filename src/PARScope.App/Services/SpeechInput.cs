using System.Globalization;
using System.Speech.Recognition;

namespace PARScope.App.Services;

/// <summary>
/// Push-to-talk recogniser for final-controller phraseology, using the offline Windows speech engine.
/// A closed grammar of PAR phrases (rather than free dictation) keeps recognition accurate for
/// spoken digits and the few dozen words a final controller uses.
/// </summary>
public sealed class SpeechInput : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private bool _listening;

    public event Action<string>? Heard;
    public event Action<string>? StateChanged;

    public bool Available => _engine != null;
    public string State { get; private set; } = "Voice not started";

    public void Start()
    {
        try
        {
            var info = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(r => r.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (info == null)
            {
                SetState("No English speech recogniser installed (Windows Settings › Time & language › Speech)");
                return;
            }
            var engine = new SpeechRecognitionEngine(info);
            engine.LoadGrammar(BuildGrammar(info.Culture));
            engine.SetInputToDefaultAudioDevice();
            engine.SpeechRecognized += (_, e) =>
            {
                if (e.Result.Confidence >= 0.45) Heard?.Invoke(e.Result.Text);
                else SetState($"Not understood ({e.Result.Confidence:P0}): {e.Result.Text}");
            };
            engine.SpeechRecognitionRejected += (_, _) => SetState("Not understood, try again");
            engine.RecognizeCompleted += (_, _) => { _listening = false; };
            _engine = engine;
            SetState("Hold Space to talk");
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException or UnauthorizedAccessException)
        {
            SetState("Voice unavailable: " + ex.Message);
        }
    }

    public void PressToTalk()
    {
        if (_engine == null || _listening) return;
        try
        {
            _engine.RecognizeAsync(RecognizeMode.Multiple);
            _listening = true;
            SetState("Listening…");
        }
        catch (InvalidOperationException ex) { SetState("Microphone error: " + ex.Message); }
    }

    public void ReleaseToTalk()
    {
        if (_engine == null || !_listening) return;
        // Stop (not cancel) so speech already captured is still recognised.
        _engine.RecognizeAsyncStop();
        SetState("Hold Space to talk");
    }

    private void SetState(string s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>One transmission is a sequence of up to six phrases, e.g.
    /// "turn right heading two four four, slightly below glidepath and coming up, on course".</summary>
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
            G("do not acknowledge further transmissions"),
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

    public void Dispose()
    {
        try { _engine?.RecognizeAsyncCancel(); } catch (InvalidOperationException) { }
        _engine?.Dispose();
    }
}
