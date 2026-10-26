using System.Text.RegularExpressions;

namespace PARScope.Core.Sim;

public enum GlidepathCall { None, On, SlightlyAbove, Above, WellAbove, SlightlyBelow, Below, WellBelow }

public enum CallTrend { None, Holding, Correcting, Worsening, Rapidly }

/// <summary>What a controller transmission asked the pilot to do.</summary>
public sealed record Transmission(string Text)
{
    public double? HeadingDeg { get; init; }
    public GlidepathCall Glidepath { get; init; }
    public CallTrend GlidepathTrend { get; init; }
    public bool BeginDescent { get; init; }
    public bool MissedApproach { get; init; }
    /// <summary>Course call, signed: negative = left of course. Informational to the pilot.</summary>
    public string? CourseCall { get; init; }

    public bool IsEmpty => HeadingDeg is null && Glidepath == GlidepathCall.None && !BeginDescent && !MissedApproach && CourseCall is null;
}

/// <summary>
/// Parses final-controller phraseology (FAA JO 7110.65 §5-10/§5-12) from speech-recogniser output or
/// typed text: "TURN RIGHT HEADING TWO FOUR FOUR, SLIGHTLY BELOW GLIDEPATH AND COMING UP".
/// Spoken digits ("two four four", "niner") and numerals are both accepted.
/// </summary>
public static partial class Phraseology
{
    private static readonly Dictionary<string, char> Digits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = '0', ["one"] = '1', ["two"] = '2', ["three"] = '3', ["tree"] = '3', ["four"] = '4',
        ["five"] = '5', ["fife"] = '5', ["six"] = '6', ["seven"] = '7', ["eight"] = '8', ["nine"] = '9', ["niner"] = '9',
    };

    public static Transmission Parse(string text)
    {
        string s = Normalise(text);

        double? heading = null;
        var h = HeadingRx().Match(s);
        if (h.Success && int.TryParse(h.Groups["h"].Value, out int hv) && hv is >= 1 and <= 360)
            heading = hv;

        var (gp, trend) = ParseGlidepath(s);

        string? course = null;
        var c = CourseRx().Match(s);
        if (c.Success) course = c.Value.ToUpperInvariant();

        bool missed = MissedRx().IsMatch(s);
        bool descend = !missed && DescentRx().IsMatch(s);

        return new Transmission(text.Trim())
        {
            HeadingDeg = heading,
            Glidepath = gp,
            GlidepathTrend = trend,
            BeginDescent = descend,
            MissedApproach = missed,
            CourseCall = course,
        };
    }

    private static (GlidepathCall, CallTrend) ParseGlidepath(string s)
    {
        if (OnGlidepathRx().IsMatch(s)) return (GlidepathCall.On, CallTrend.None);

        var m = GlidepathRx().Match(s);
        if (!m.Success) return (GlidepathCall.None, CallTrend.None);

        bool above = m.Groups["side"].Value == "above";
        string mag = m.Groups["mag"].Value;
        bool going = m.Groups["going"].Success;
        var call = (mag, above) switch
        {
            ("slightly", true) => GlidepathCall.SlightlyAbove,
            ("well", true) => GlidepathCall.WellAbove,
            (_, true) => going ? GlidepathCall.SlightlyAbove : GlidepathCall.Above,
            ("slightly", false) => GlidepathCall.SlightlyBelow,
            ("well", false) => GlidepathCall.WellBelow,
            _ => going ? GlidepathCall.SlightlyBelow : GlidepathCall.Below,
        };

        string rest = s[(m.Index + m.Length)..];
        var trend = going ? CallTrend.Worsening
            : rest.Contains("rapidly") ? CallTrend.Rapidly
            : rest.Contains("coming down") || rest.Contains("coming up") ? CallTrend.Correcting
            : rest.Contains("holding") ? CallTrend.Holding
            : CallTrend.None;
        return (call, trend);
    }

    /// <summary>Lower-cases, strips punctuation, and collapses spoken digit runs into numerals.</summary>
    public static string Normalise(string text)
    {
        var words = PunctRx().Replace(text.ToLowerInvariant(), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var outWords = new List<string>();
        var run = new System.Text.StringBuilder();
        foreach (var w in words)
        {
            if (Digits.TryGetValue(w, out char d)) { run.Append(d); continue; }
            if (run.Length > 0) { outWords.Add(run.ToString()); run.Clear(); }
            outWords.Add(w);
        }
        if (run.Length > 0) outWords.Add(run.ToString());
        return " " + string.Join(' ', outWords).Replace("glide path", "glidepath") + " ";
    }

    [GeneratedRegex(@"[^a-z0-9 ]")] private static partial Regex PunctRx();
    [GeneratedRegex(@"\bheading (?<h>\d{3})\b")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"\bon glidepath\b")] private static partial Regex OnGlidepathRx();
    [GeneratedRegex(@"\b(?:(?<going>going) |(?<mag>slightly|well) )?(?<side>above|below) (?:the )?glidepath\b")] private static partial Regex GlidepathRx();
    [GeneratedRegex(@"\b(?:on course|(?:going |slightly |well )?(?:left|right) of course)\b")] private static partial Regex CourseRx();
    [GeneratedRegex(@"\bbegin descent\b|\bstart descent\b")] private static partial Regex DescentRx();
    [GeneratedRegex(@"\bexecute missed approach\b|\bgo around\b|\bwave ?off\b")] private static partial Regex MissedRx();
}
