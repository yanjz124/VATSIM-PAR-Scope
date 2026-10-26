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
    /// <summary>"HOW DO YOU HEAR ME?" (7110.65 §5-10-9).</summary>
    public bool HowDoYouHear { get; init; }
    /// <summary>"DO NOT ACKNOWLEDGE FURTHER TRANSMISSIONS" (§5-10-10).</summary>
    public bool DoNotAcknowledge { get; init; }
    /// <summary>"WHEELS SHOULD BE DOWN" (§2-1-25).</summary>
    public bool WheelsDown { get; init; }
    /// <summary>Relayed tower clearance: cleared to land / touch and go / low approach / option.</summary>
    public bool ClearedToLand => Clearance is not null;
    public LandingIntent? Clearance { get; init; }
    /// <summary>"Climb and maintain (altitude)", feet MSL.</summary>
    public double? ClimbToFt { get; init; }
    /// <summary>"In the event of missed approach, …": instructions to remember, not to fly now (§5-10-2).</summary>
    public bool MissedApproachInstructions { get; init; }
    /// <summary>"Contact approach/departure": frequency change off final.</summary>
    public bool ContactApproach { get; init; }
    /// <summary>"Contact tower/ground (after landing)" (§5-12-9).</summary>
    public bool ContactTower { get; init; }
    /// <summary>"(Reason), if runway/approach lights not in sight, execute missed approach" (§5-10-14): the pilot
    /// continues if they have the runway environment in sight.</summary>
    public bool ConditionalMissedApproach { get; init; }
    /// <summary>"Level off (your aircraft)": stop descending (e.g. dangerously below the glidepath).</summary>
    public bool LevelOff { get; init; }
    /// <summary>"Resume normal rate of descent" / "resume descent".</summary>
    public bool ResumeDescent { get; init; }
    /// <summary>"…, acknowledge": the controller asks for a reply even after "do not acknowledge".</summary>
    public bool AcknowledgeRequested { get; init; }
    /// <summary>"If no transmissions received for … seconds …": lost communications instructions to remember.</summary>
    public bool LostCommsInstructions { get; init; }

    public bool IsEmpty => HeadingDeg is null && Glidepath == GlidepathCall.None && !BeginDescent && !MissedApproach && CourseCall is null
                           && !HowDoYouHear && !DoNotAcknowledge && !WheelsDown && !ClearedToLand && ClimbToFt is null
                           && !MissedApproachInstructions && !ContactApproach && !ContactTower && !LevelOff && !ResumeDescent && !LostCommsInstructions;
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
        if (c.Success) course = c.Value.ToUpperInvariant().Replace("READ OF", "RIGHT OF");

        bool instructions = MissedInstructionsRx().IsMatch(s);
        bool missed = !instructions && MissedRx().IsMatch(s);
        double? climb = null;
        var cl = ClimbRx().Match(s);
        if (cl.Success && int.TryParse(cl.Groups["a"].Value, out int a))
        {
            double ft = cl.Groups["k"].Success ? a * 1000 + (int.TryParse(cl.Groups["h"].Value, out int hh) ? hh * 100 : 0)
                : cl.Groups["h"].Success ? a * 100 : a;
            if (ft is >= 500 and <= 20000) climb = ft;
        }
        bool descend = !missed && DescentRx().IsMatch(s);

        return new Transmission(text.Trim())
        {
            HeadingDeg = heading,
            Glidepath = gp,
            GlidepathTrend = trend,
            BeginDescent = descend,
            MissedApproach = missed,
            CourseCall = course,
            HowDoYouHear = s.Contains(" how do you hear"),
            DoNotAcknowledge = NoAckRx().IsMatch(s),
            WheelsDown = s.Contains(" wheels should be down") || s.Contains(" check wheels down"),
            Clearance = ClearedRx().Match(s) is { Success: true } cm
                ? cm.Value.Contains("touch") ? LandingIntent.TouchAndGo
                : cm.Value.Contains("low") ? LandingIntent.LowApproach
                : cm.Value.Contains("option") ? LandingIntent.Option
                : LandingIntent.FullStop
                : null,
            ClimbToFt = climb,
            MissedApproachInstructions = instructions,
            ContactApproach = ContactRx().IsMatch(s),
            ContactTower = ContactTowerRx().IsMatch(s),
            ConditionalMissedApproach = missed && NotInSightRx().IsMatch(s) && !TowerClearanceRx().IsMatch(s),
            LevelOff = LevelOffRx().IsMatch(s),
            ResumeDescent = ResumeRx().IsMatch(s),
            AcknowledgeRequested = AckRequestRx().IsMatch(s),
            LostCommsInstructions = LostCommsRx().IsMatch(s),
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
            : rest.Contains("coming down") || rest.Contains("coming up") || rest.Contains(" correct") ? CallTrend.Correcting
            : rest.Contains(" hold") ? CallTrend.Holding
            : CallTrend.None;
        return (call, trend);
    }

    /// <summary>Lower-cases, strips punctuation, and collapses spoken digit runs into numerals.</summary>
    public static string Normalise(string text)
    {
        // "2,000" from speech engines is one number, not "2 000".
        var words = PunctRx().Replace(ThousandsCommaRx().Replace(text.ToLowerInvariant(), ""), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var outWords = new List<string>();
        var run = new System.Text.StringBuilder();
        foreach (var w in words)
        {
            if (Digits.TryGetValue(w, out char d)) { run.Append(d); continue; }
            // Speech engines often write spoken digits as separate numerals ("2 4 0").
            if (w.Length == 1 && char.IsDigit(w[0])) { run.Append(w[0]); continue; }
            if (run.Length > 0) { outWords.Add(run.ToString()); run.Clear(); }
            outWords.Add(w);
        }
        if (run.Length > 0) outWords.Add(run.ToString());
        // Shortened and misheard forms controllers and recognisers produce: "well above glide", "glide slope".
        string joined = " " + string.Join(' ', outWords) + " ";
        return GlideRx().Replace(joined, "glidepath");
    }

    [GeneratedRegex(@"[^a-z0-9 ]")] private static partial Regex PunctRx();
    [GeneratedRegex(@"\bheading (?<h>\d{3})\b")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"\bon glidepath\b")] private static partial Regex OnGlidepathRx();
    [GeneratedRegex(@"\b(?:(?<going>going) |(?<mag>slightly|well) )?(?<side>above|below) (?:the )?glidepath\b")] private static partial Regex GlidepathRx();
    // "read of course" is a common mishearing of "right of course".
    [GeneratedRegex(@"\b(?:on (?:the )?(?:on ?)?course|(?:going |drifting |slightly |well )?(?:left|right|read) of (?:the )?(?:on ?)?course)\b")] private static partial Regex CourseRx();
    [GeneratedRegex(@"\b(?:begin|start|initiate|commence) (?:your )?descent\b")] private static partial Regex DescentRx();
    [GeneratedRegex(@"\bglide(?: ?path| ?slope)?\b")] private static partial Regex GlideRx();
    [GeneratedRegex(@"\bexecute missed approach\b|\bgo around\b|\bwave ?off\b|\btower clearance (?:not received|cancell?ed)\b")] private static partial Regex MissedRx();
    // "Do not acknowledge further transmissions" (§5-10-10) and the ways people actually say it.
    [GeneratedRegex(@"\b(?:do not|don t|dont|to not|no need to|not necessary to) (?:acknowledge|read ?back)\b|\backnowledg(?:e|ement)s? not (?:required|necessary)\b|\bno (?:further )?(?:acknowledgements?|acknowledgments?|readbacks?)\b")] private static partial Regex NoAckRx();
    [GeneratedRegex(@"(?<=\d),(?=\d{3}\b)")] private static partial Regex ThousandsCommaRx();
    [GeneratedRegex(@"\blevel off\b")] private static partial Regex LevelOffRx();
    [GeneratedRegex(@"\bresume (?:normal )?(?:rate of )?descent\b|\bresume normal rate\b")] private static partial Regex ResumeRx();
    // "…, acknowledge" at the end of a call, not "do not acknowledge".
    [GeneratedRegex(@"(?<!\bnot |\bno need to |\bdon t |\bdont |\bto not )\backnowledge\s*$")] private static partial Regex AckRequestRx();
    [GeneratedRegex(@"\bif no transmissions? (?:are )?received\b|\blost comm(?:unication)?s?\b")] private static partial Regex LostCommsRx();
    // Instructions to remember, not to fly now: "in the event of missed approach…", 7110.65 §5-10-11 "your missed
    // approach procedure is…", §5-10-12 "after completing low approach/touch and go: climb and maintain…".
    [GeneratedRegex(@"\b(?:in the event of (?:a )?missed approach|if (?:you )?(?:go |are )?missed approach|missed approach instructions|your missed approach procedure is|after (?:completing |your )?(?:a |the )?(?:low approach|touch and go|option)|climb ?out instructions)\b")] private static partial Regex MissedInstructionsRx();
    [GeneratedRegex(@"\bif (?:the )?(?:runway|approach lights|runway lights)\b[^.]*?\bnot in sight\b")] private static partial Regex NotInSightRx();
    [GeneratedRegex(@"\btower clearance (?:not received|cancell?ed)\b")] private static partial Regex TowerClearanceRx();
    [GeneratedRegex(@"\b(?:contact|switch to|monitor) (?:tower|ground)\b")] private static partial Regex ContactTowerRx();
    // "climb and maintain 2 thousand 5 hundred" after digit merging, or "climb and maintain 2500".
    [GeneratedRegex(@"\bclimb (?:and maintain |to )?(?<a>\d+)(?: (?<k>thousand))?(?: (?<h>\d) hundred)?\b")] private static partial Regex ClimbRx();
    [GeneratedRegex(@"\b(?:contact|switch to|monitor) (?:approach|departure|arrival|radar)\b")] private static partial Regex ContactRx();
    [GeneratedRegex(@"\bcleared (?:to land|for (?:the )?option|touch and go|low approach)\b")] private static partial Regex ClearedRx();
}
