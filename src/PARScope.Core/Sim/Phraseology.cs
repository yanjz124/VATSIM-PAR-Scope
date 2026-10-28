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
    /// <summary>"This will be a P-A-R approach to runway…" (§5-10-2).</summary>
    public bool ApproachBrief { get; init; }
    /// <summary>Range or position information: "(n) miles from touchdown", "over approach lights", "at decision altitude".</summary>
    public bool RangeInfo { get; init; }
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
    /// <summary>No-gyro (7110.65 §5-10-3): "turn left/right" with no heading, −1 left, +1 right; turn until told to stop.</summary>
    public int NoGyroTurn { get; init; }
    /// <summary>"Turn (n) degrees left/right" (7110.65 §5-6-2): a turn by that many degrees, negative = left.</summary>
    public double? TurnByDeg { get; init; }
    /// <summary>No-gyro: "stop turn".</summary>
    public bool StopTurn { get; init; }
    /// <summary>No-gyro rate: "make half standard rate turns" (true) or "standard rate turns" (false); null = not said.</summary>
    public bool? HalfStandardRate { get; init; }
    /// <summary>"This will be a no-gyro approach".</summary>
    public bool NoGyroBrief { get; init; }
    /// <summary>"(Callsign,) disregard": cancel the previous transmission.</summary>
    public bool Disregard { get; init; }
    /// <summary>An instruction then "disregard" in the same breath: this transmission is cancelled; nothing to do.</summary>
    public bool Cancelled { get; init; }

    public bool IsEmpty => HeadingDeg is null && Glidepath == GlidepathCall.None && !BeginDescent && !MissedApproach && CourseCall is null
                           && !HowDoYouHear && !DoNotAcknowledge && !WheelsDown && !ClearedToLand && ClimbToFt is null
                           && !MissedApproachInstructions && !ContactApproach && !ContactTower && !ApproachBrief && !RangeInfo && !LevelOff && !ResumeDescent && !LostCommsInstructions
                           && NoGyroTurn == 0 && TurnByDeg is null && !StopTurn && HalfStandardRate is null && !NoGyroBrief && !Disregard && !Cancelled;
}

/// <summary>
/// Parses final-controller phraseology (FAA JO 7110.65 §5-10/§5-12) from speech-recognizer output or
/// typed text: "TURN RIGHT HEADING TWO FOUR FOUR, SLIGHTLY BELOW GLIDEPATH AND COMING UP".
/// Spoken digits ("two four four", "niner") and numerals are both accepted.
/// </summary>
public static partial class Phraseology
{
    private static readonly Dictionary<string, char> Digits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = '0', ["one"] = '1', ["two"] = '2', ["three"] = '3', ["tree"] = '3', ["four"] = '4',
        ["five"] = '5', ["fife"] = '5', ["fyfe"] = '5', ["six"] = '6', ["seven"] = '7', ["eight"] = '8', ["nine"] = '9', ["niner"] = '9',
        ["nyner"] = '9', ["nainer"] = '9',
    };

    /// <summary>
    /// A transmission, with the controller's own fixes applied: "disregard" cancels everything said before it
    /// (only what follows counts; nothing after it cancels the previous transmission), and "correction" replaces
    /// what it corrects while the rest stands ("heading 270, correction, heading 280"; a bare number after
    /// "correction" fixes the heading).
    /// </summary>
    public static Transmission Parse(string text)
    {
        string s = Normalize(text);
        if (DisregardRx().Matches(s) is { Count: > 0 } ds)
        {
            var last = ds[^1];
            bool saidBefore = !ParseNormalized(text, s[..last.Index] + " ").IsEmpty;
            s = " " + s[(last.Index + last.Length)..].TrimStart();
            if (string.IsNullOrWhiteSpace(s))
                return saidBefore ? new Transmission(text.Trim()) { Cancelled = true } : new Transmission(text.Trim()) { Disregard = true };
        }
        var parts = CorrectionRx().Split(s);
        var result = ParseNormalized(text, parts[0]);
        foreach (var part in parts.Skip(1))
        {
            var fix = ParseNormalized(text, " " + part.Trim() + " ");
            if (fix.HeadingDeg is null && result.HeadingDeg is not null && BareHeadingRx().Match(part) is { Success: true } bh
                && int.TryParse(bh.Groups["h"].Value, out int bare) && bare is >= 1 and <= 360)
                fix = fix with { HeadingDeg = bare };
            result = Corrected(result, fix);
        }
        return result;
    }

    /// <summary>What a correction said replaces the same item; everything it did not mention stands.</summary>
    private static Transmission Corrected(Transmission was, Transmission fix) => was with
    {
        HeadingDeg = fix.HeadingDeg ?? (fix.TurnByDeg is null && fix.NoGyroTurn == 0 ? was.HeadingDeg : null),
        Glidepath = fix.Glidepath != GlidepathCall.None ? fix.Glidepath : was.Glidepath,
        GlidepathTrend = fix.Glidepath != GlidepathCall.None ? fix.GlidepathTrend : was.GlidepathTrend,
        CourseCall = fix.CourseCall ?? was.CourseCall,
        ClimbToFt = fix.ClimbToFt ?? was.ClimbToFt,
        Clearance = fix.Clearance ?? was.Clearance,
        TurnByDeg = fix.TurnByDeg ?? (fix.HeadingDeg is null ? was.TurnByDeg : null),
        NoGyroTurn = fix.NoGyroTurn != 0 ? fix.NoGyroTurn : was.NoGyroTurn,
        HalfStandardRate = fix.HalfStandardRate ?? was.HalfStandardRate,
        BeginDescent = was.BeginDescent || fix.BeginDescent,
        MissedApproach = was.MissedApproach || fix.MissedApproach,
        HowDoYouHear = was.HowDoYouHear || fix.HowDoYouHear,
        DoNotAcknowledge = was.DoNotAcknowledge || fix.DoNotAcknowledge,
        WheelsDown = was.WheelsDown || fix.WheelsDown,
        MissedApproachInstructions = was.MissedApproachInstructions || fix.MissedApproachInstructions,
        ContactApproach = was.ContactApproach || fix.ContactApproach,
        ContactTower = was.ContactTower || fix.ContactTower,
        ApproachBrief = was.ApproachBrief || fix.ApproachBrief,
        RangeInfo = was.RangeInfo || fix.RangeInfo,
        ConditionalMissedApproach = was.ConditionalMissedApproach || fix.ConditionalMissedApproach,
        LevelOff = was.LevelOff || fix.LevelOff,
        ResumeDescent = was.ResumeDescent || fix.ResumeDescent,
        AcknowledgeRequested = was.AcknowledgeRequested || fix.AcknowledgeRequested,
        LostCommsInstructions = was.LostCommsInstructions || fix.LostCommsInstructions,
        StopTurn = was.StopTurn || fix.StopTurn,
        NoGyroBrief = was.NoGyroBrief || fix.NoGyroBrief,
    };

    private static Transmission ParseNormalized(string text, string s)
    {

        double? heading = null;
        var h = HeadingRx().Match(s);
        if (h.Success && int.TryParse(h.Groups["h"].Value, out int hv) && hv is >= 1 and <= 360)
            heading = hv;

        var (gp, trend) = ParseGlidepath(s);

        string? course = null;
        var c = CourseRx().Match(s);
        // Said as "(slightly) left of course", whatever the recognizer did to the "of".
        if (c.Success) course = CourseOfRx().Replace(c.Value, "${s} of course").ToUpperInvariant().Replace("READ OF", "RIGHT OF");

        bool instructions = MissedInstructionsRx().IsMatch(s);
        // Lost communications instructions end "...and execute missed approach": what to do later, not now.
        bool lostComms = LostCommsRx().IsMatch(s);
        bool missed = !instructions && !lostComms && MissedRx().IsMatch(s);
        double? climb = null;
        var cl = ClimbRx().Match(s);
        if (cl.Success && int.TryParse(cl.Groups["a"].Value, out int a))
        {
            double ft = cl.Groups["k"].Success ? a * 1000 + (int.TryParse(cl.Groups["h"].Value, out int hh) ? hh * 100 : 0)
                : cl.Groups["h"].Success ? a * 100 : a;
            if (ft is >= 500 and <= 20000) climb = ft;
        }
        bool descend = !missed && DescentRx().IsMatch(s);
        // No-gyro turns: "turn left/right" with no heading after it.
        double? turnBy = null;
        if (heading is null && TurnByRx().Match(s) is { Success: true } tb && DegreesWord(tb.Groups["n"].Success ? tb.Groups["n"].Value : tb.Groups["n2"].Value) is int deg && deg is > 0 and <= 180)
            turnBy = ((tb.Groups["d"].Success ? tb.Groups["d"].Value : tb.Groups["d2"].Value) == "left" ? -1 : 1) * deg;
        int noGyro = 0;
        if (heading is null && NoGyroTurnRx().Match(s) is { Success: true } ng) noGyro = ng.Groups["d"].Value == "left" ? -1 : 1;
        bool? halfRate = HalfRateRx().IsMatch(s) ? true : StandardRateRx().IsMatch(s) ? false : null;

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
            WheelsDown = WheelsRx().IsMatch(s),
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
            ApproachBrief = BriefRx().IsMatch(s),
            RangeInfo = RangeInfoRx().IsMatch(s),
            ConditionalMissedApproach = missed && NotInSightRx().IsMatch(s) && !TowerClearanceRx().IsMatch(s),
            LevelOff = LevelOffRx().IsMatch(s),
            ResumeDescent = ResumeRx().IsMatch(s),
            AcknowledgeRequested = AckRequestRx().IsMatch(s),
            LostCommsInstructions = lostComms,
            NoGyroTurn = noGyro,
            TurnByDeg = turnBy,
            StopTurn = StopTurnRx().IsMatch(s),
            HalfStandardRate = halfRate,
            NoGyroBrief = s.Contains(" no gyro"),
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
    public static string Normalize(string text)
    {
        // "2,000" from speech engines is one number, not "2 000".
        string lower = MisheardNinerRx().Replace(text.ToLowerInvariant(), "niner");
        var words = PunctRx().Replace(ThousandsCommaRx().Replace(lower, ""), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
        // Shortened and misheard forms controllers and recognizers produce: "well above glide", "glide slope",
        // and what Whisper made of real calls (see Misheard).
        string joined = " " + string.Join(' ', outWords) + " ";
        foreach (var (heard, meant) in Misheard) joined = joined.Replace(heard, meant);
        return GlideRx().Replace(joined, "glidepath");
    }

    /// <summary>
    /// Whisper's renderings of PAR calls from real sessions, as whole words: "slightly above decline" (the glide),
    /// "arm glide", "unglide", "I'm glide" (on glide), "a public glide path" (above the glidepath), "slowly left"
    /// (slightly left).
    /// </summary>
    private static readonly (string Heard, string Meant)[] Misheard =
    [
        (" decline ", " the glide "), (" arm glide", " on glide"), (" unglide ", " on glide "), (" unglyde ", " on glide "),
        (" i m glide ", " on glide "), (" im glide ", " on glide "), (" a public ", " above the "), (" above the light ", " above the glide "),
        (" slowly left ", " slightly left "), (" slowly right ", " slightly right "),
        (" wild left ", " well left "), (" wild right ", " well right "), (" while left ", " well left "), (" while right ", " well right "),
        (" while red ", " well right "), (" red course ", " right course "), (" red of course ", " right of course "),
        (" doers ", " degrees "), (" write ", " right "), (" calling up", " coming up"), (" calling down", " coming down"),
        (" study above ", " slightly above "), (" study below ", " slightly below "), (" study left ", " slightly left "), (" study right ", " slightly right "),
        (" slowly above ", " slightly above "), (" slowly below ", " slightly below "),
        (" climbing tank ", " climb and maintain "), (" climb in maintain ", " climb and maintain "), (" climbing maintain ", " climb and maintain "),
    ];

    /// <summary>"Niner" as Whisper sometimes writes it, between digits: "zero nine or zero", "zero minor zero".</summary>
    [GeneratedRegex(@"(?<=\b(?:zero|one|two|three|tree|four|five|fife|six|seven|eight|nine|niner|\d) )(?:nine or|minor|nina)(?= (?:zero|one|two|three|tree|four|five|fife|six|seven|eight|nine|niner|\d)\b)")] private static partial Regex MisheardNinerRx();

    [GeneratedRegex(@"[^a-z0-9 ]")] private static partial Regex PunctRx();
    [GeneratedRegex(@"\bdisregard(?: (?:that|the last|my last|last)(?: transmission| instruction| heading)?)?\b")] private static partial Regex DisregardRx();
    [GeneratedRegex(@"\bcorrection\b")] private static partial Regex CorrectionRx();
    [GeneratedRegex(@"^\s*(?:heading )?(?<h>\d{3})\b")] private static partial Regex BareHeadingRx();
    [GeneratedRegex(@"\bheading (?<h>\d{3})\b")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"\bon glidepath\b")] private static partial Regex OnGlidepathRx();
    // "turn left" / "turn right" not followed by a heading (or "stop turn").
    [GeneratedRegex(@"(?<!stop )\bturn (?<d>left|right)\b(?! heading)(?! \d)")] private static partial Regex NoGyroTurnRx();
    [GeneratedRegex(@"\bstop (?:the )?turn\b")] private static partial Regex StopTurnRx();
    // "turn two degrees left" and "turn left two degrees".
    [GeneratedRegex(@"\bturn (?:(?<n>\d{1,3}|ten|fifteen|twenty|thirty|forty|forty five|fifty|sixty|ninety) degrees? (?<d>left|right)|(?<d2>left|right) (?<n2>\d{1,3}|ten|fifteen|twenty|thirty|forty|forty five|fifty|sixty|ninety) degrees?)\b")] private static partial Regex TurnByRx();

    /// <summary>Degrees as said: digits ("1 0", already joined to "10") or the round numbers controllers say as words.</summary>
    private static int? DegreesWord(string w) => int.TryParse(w, out int n) ? n : w switch
    {
        "ten" => 10, "fifteen" => 15, "twenty" => 20, "thirty" => 30, "forty" => 40, "forty five" => 45,
        "fifty" => 50, "sixty" => 60, "ninety" => 90, _ => null,
    };
    [GeneratedRegex(@"\bhalf (?:a )?standard rate\b")] private static partial Regex HalfRateRx();
    [GeneratedRegex(@"(?<!half (?:a )?)\bstandard rate\b")] private static partial Regex StandardRateRx();
    [GeneratedRegex(@"\b(?:(?<going>going) |(?<mag>slightly|well) )?(?<side>above|below) (?:the )?glidepath\b")] private static partial Regex GlidepathRx();
    // "read of course" is a common mishearing of "right of course"; the "of" is often lost or heard as another
    // short word ("left course", "left the course", "right up course", "left on course", "left of a course",
    // "left field course", "left horse"). A side named before "course" always wins over "on course".
    [GeneratedRegex(@"\b(?:(?:going |drifting |slightly |well )?(?:left|right|read) (?:(?:of|up|a|the|on|field) ){0,2}(?:on ?)?(?:course|horse)|on (?:the )?(?:on ?)?course)\b")] private static partial Regex CourseRx();
    [GeneratedRegex(@"\b(?<s>left|right|read) (?:(?:of|up|a|the|on|field) ){0,2}(?:on ?)?(?:course|horse)\b")] private static partial Regex CourseOfRx();
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
    [GeneratedRegex(@"\b(?:in the event of (?:a )?missed approach|if (?:you )?(?:go |are )?missed approach|miss(?:ed)? approach instructions|your missed approach procedure is|after (?:completing |your )?(?:a |the )?(?:low approach|touch and go|option)|climb ?out instructions)\b")] private static partial Regex MissedInstructionsRx();
    [GeneratedRegex(@"\bif (?:the )?(?:runway|approach lights|runway lights)\b[^.]*?\bnot in sight\b")] private static partial Regex NotInSightRx();
    [GeneratedRegex(@"\btower clearance (?:not received|cancell?ed)\b")] private static partial Regex TowerClearanceRx();
    [GeneratedRegex(@"\b(?:contact|switch to|monitor) (?:tower|ground)\b")] private static partial Regex ContactTowerRx();
    [GeneratedRegex(@"\bthis will be a (?:p a r|par|precision|copter p a r)\b")] private static partial Regex BriefRx();
    // Range calls end in "touchdown", which survives even when the number and "miles" do not ("fire mode from
    // touchdown", "three muslim touch step", "one moven touch out").
    [GeneratedRegex(@"\b(?:\d+ (?:and (?:one )?half )?miles? from touchdown|(?:from )?touch ?(?:down|out|step|town)\b(?! zone)|\b\d+ miles? from (?:a |the )?t[aou][a-z]*\b|\bfrom (?:the )?touch\b(?! and go)|over (?:the )?approach lights|over (?:the )?landing threshold|(?:at )?decision (?:altitude|height))\b")] private static partial Regex RangeInfoRx();
    // "climb and maintain 2 thousand 5 hundred" after digit merging, or "climb and maintain 2500".
    // "climb and maintain", and "climb maintain" as recognizers often hear it.
    [GeneratedRegex(@"\bclimb (?:(?:and )?maintain |to )?(?<a>\d+)(?: (?<k>thousand))?(?: (?<h>\d) hundred)?\b")] private static partial Regex ClimbRx();
    [GeneratedRegex(@"\b(?:wheels|gears?) should be down\b|\bcheck (?:wheels|gears?) down\b")] private static partial Regex WheelsRx();
    [GeneratedRegex(@"\b(?:contact|switch to|monitor) (?:approach|departure|arrival|radar)\b")] private static partial Regex ContactRx();
    // "clear to land" as recognizers often drop the "-ed".
    [GeneratedRegex(@"\bclear(?:ed)? (?:to land|for (?:the )?option|touch and go|low approach)\b")] private static partial Regex ClearedRx();
}
