using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Traffic;

namespace PARScope.Core.Sim;

public enum PilotMode
{
    /// <summary>Flies the approach on their own (like a VATSIM pilot on the ILS), with imperfect tracking.</summary>
    SelfFlying,
    /// <summary>Holds heading and rate until the controller says otherwise: a true no-gyro/PAR talk-down.</summary>
    ControllerDirected,
}

/// <summary>Training scenario difficulty: how hard the pilots and the weather make the talk-down.</summary>
public enum TrainingDifficulty { Easy, Normal, Hard, Expert }

/// <summary>Spawn and pilot parameters for a difficulty level.</summary>
/// <summary>Where approach hands an arrival to final.</summary>
public enum HandoffLeg { Final, Dogleg, Base }

/// <summary>Which handoff positions training arrivals use.</summary>
public enum HandoffMix { FinalOnly, Mixed, BaseAndDogleg }

public sealed record DifficultyProfile(
    double SkillLo, double SkillHi, int WindKtLo, int WindKtHi, double CrossFt, double InterceptDeg,
    double HeadingDriftDegPerMin, double VsDriftFpmPerMin, double WindVeerDeg, double WindShearKt, double GustKt)
{
    public static DifficultyProfile For(TrainingDifficulty d) => d switch
    {
        TrainingDifficulty.Easy => new(0.8, 1.0, 0, 8, 400, 5, 0, 0, 0, 0, 0),
        TrainingDifficulty.Normal => new(0.4, 0.9, 0, 19, 800, 10, 0, 0, 0, 0, 0),
        // Pilots let heading and rate creep (gyro precession, divided attention); wind veers and drops on descent.
        TrainingDifficulty.Hard => new(0.3, 0.6, 8, 25, 1200, 15, 3, 120, 25, 8, 3),
        _ => new(0.15, 0.4, 15, 32, 1600, 20, 6, 220, 45, 14, 7),
    };

    public static string Describe(TrainingDifficulty d) => d switch
    {
        TrainingDifficulty.Easy => "Light wind, precise pilots, close to the centerline.",
        TrainingDifficulty.Normal => "Moderate wind, average pilots.",
        TrainingDifficulty.Hard => "Strong wind that shifts on descent; pilots drift off heading and rate unless corrected.",
        _ => "Gusty wind with shear; sloppy, slow pilots who drift steadily. Expect constant corrections.",
    };
}

public sealed record SpawnOptions
{
    public double RangeNm { get; init; } = 8;
    public double CrossFt { get; init; } = 0;
    /// <summary>Height relative to the glidepath at the spawn range (negative = below).</summary>
    public double HeightOffsetFt { get; init; } = -300;
    /// <summary>Height above the threshold to spawn level at (a GCA pattern altitude); overrides <see cref="HeightOffsetFt"/>.
    /// Kept at least 300 ft under the glidepath so the aircraft intercepts it from below.</summary>
    public double? AltitudeFt { get; init; }
    /// <summary>Where approach hands the arrival over (sets nothing by itself; for the handoff call).</summary>
    public HandoffLeg Leg { get; init; } = HandoffLeg.Final;
    /// <summary>Approach speed; 0 = typical for the aircraft type chosen.</summary>
    public double SpeedKt { get; init; }
    public double InterceptAngleDeg { get; init; } = 0;
    public PilotMode Mode { get; init; } = PilotMode.ControllerDirected;
    /// <summary>0 = sloppy, 1 = precise.</summary>
    public double Skill { get; init; } = 0.6;
    /// <summary>Round the initial heading to tens, as approach assigns it (generated arrivals).</summary>
    public bool HeadingInTens { get; init; }
    public double WindFromDeg { get; init; } = 0;
    public double WindKt { get; init; } = 0;
    /// <summary>Steady heading creep while holding a heading, °/min; reset when a new heading is assigned.</summary>
    public double HeadingDriftDegPerMin { get; init; }
    /// <summary>Steady descent-rate creep, fpm per minute; mostly corrected after each glidepath call.</summary>
    public double VsDriftFpmPerMin { get; init; }
    /// <summary>Wind veer between the spawn height and the surface, degrees (surface wind backs by this much).</summary>
    public double WindVeerDeg { get; init; }
    /// <summary>Extra wind at the spawn height over the surface wind, knots.</summary>
    public double WindShearKt { get; init; }
    /// <summary>Gust amplitude, knots.</summary>
    public double GustKt { get; init; }
    public string? TypeCode { get; init; }
    /// <summary>Gear at handoff; null = random (the wheels check matters).</summary>
    public bool? GearDown { get; init; }
    /// <summary>Custom callsign; null = generated.</summary>
    public string? Callsign { get; init; }
}

/// <summary>Simulated arrivals for PAR training. All kinematics run in the runway's approach frame.</summary>
public sealed class TrainingTraffic
{
    /// <summary>
    /// Service-prefix and mission callsigns used by US military traffic at a GCA field. Service prefixes per
    /// FAA JO 7110.65 §2-4-20 (VV "Navy", VM "Marine", R "Army", AF "Air Force"); mission callsigns REACH
    /// (AMC airlift), PAT (Army priority air transport), CONVOY (Navy fleet logistics). Squadron tactical
    /// callwords come from <see cref="MilitaryCallsigns"/>. Types are ICAO designators covered by the FAA
    /// Aircraft Characteristics Database, which supplies their approach speeds (see <see cref="AircraftPerformance"/>).
    /// </summary>
    private sealed record CallsignStyle(string Prefix, int Digits, string[] Types, bool FlightNumbers = false);

    private static readonly CallsignStyle[] Styles =
    [
        new("VV", 4, ["TEX2", "P8", "F18S", "F18H", "P3", "B737"]),
        new("VM", 4, ["C30J", "F18H", "BE20", "C560"]),
        new("R", 5, ["BE20", "C560"]),
        new("AF", 4, ["C17", "C30J", "T38", "TEX2", "K35R"]),
        new("REACH", 3, ["C17", "K35R", "C30J"]),
        new("PAT", 3, ["BE20", "C560"]),
        new("CONVOY", 4, ["B737", "C30J"]),
    ];

    /// <summary>Longest generated callsign, as shown on the radar (0 = no limit). Longer ones are abbreviated.</summary>
    public int MaxCallsignLength { get; set; }

    /// <summary>
    /// The letters and number of digits of a generated callsign within <see cref="MaxCallsignLength"/>: mission
    /// numbers shorten first (to two digits); formation numbers stay two digits; then the callword is
    /// abbreviated, to its official designator (REACH → RCH) or a form of four letters or more that still reads
    /// as the word (BANSHEE → BNSH), as in a flight plan's seven-character callsign.
    /// </summary>
    /// <summary>Null when no form of this callword fits without looking like a civil operator's designator.</summary>
    private (string Prefix, int Digits)? FitCallsign(string prefix, int digits, bool flight)
    {
        int max = MaxCallsignLength;
        if (max <= 0 || prefix.Length + digits <= max) return IsCivilLooking(prefix) ? null : (prefix, digits);
        if (!flight) digits = Math.Max(2, max - prefix.Length);
        if (prefix.Length + digits <= max) return IsCivilLooking(prefix) ? null : (prefix, digits);
        if (Abbreviations(prefix, Math.Max(1, max - digits)).FirstOrDefault(a => !IsCivilLooking(a)) is not { } abbr) return null;
        if (!flight) digits = Math.Clamp(max - abbr.Length, 2, digits);
        return (abbr, digits);
    }

    /// <summary>Generated callsigns are military: never a civil operator's ICAO three-letter designator (FAA JO 7340.2).</summary>
    private static bool IsCivilLooking(string prefix) => prefix.Length == 3 && IcaoTelephony.IsCivil(prefix);

    /// <summary>The preferred abbreviation of a callword (BANSHEE → BNSH), or null if none fits.</summary>
    public static string? Abbreviate(string word, int letters) => Abbreviations(word, letters).FirstOrDefault();

    /// <summary>
    /// Abbreviations of a callword to at most <paramref name="letters"/> letters, best first: its official military
    /// designator (REACH → RCH); then, never under four letters, the listed form (callword-abbreviations.csv) and
    /// the same rule for words not in the list.
    /// </summary>
    public static IEnumerable<string> Abbreviations(string word, int letters)
    {
        if (word.Length <= letters) { yield return word; yield break; }
        if (IcaoTelephony.MilitaryDesignator(word) is { } code && code.Length <= letters) yield return code;
        if (letters < 4) yield break; // shorter than four letters only as an official designator
        int n = Math.Min(letters, 6);
        if (CallwordAbbreviations.Find(word, n) is { } listed) yield return listed;
        yield return CallwordAbbreviations.Natural(word, n);
    }

    /// <summary>Name of the field being worked; squadrons based there fly most of its traffic.</summary>
    public string? HomeAirportName
    {
        get => _homeAirportName;
        set { _homeAirportName = value; _homeUnits = MilitaryCallsigns.BasedAt(value); }
    }
    private string? _homeAirportName;
    private IReadOnlyList<TacticalCallsign> _homeUnits = [];

    /// <summary>
    /// Half the random arrivals use a squadron tactical callword, half a service/mission callsign. Where
    /// squadrons are based at the field, most tactical arrivals are theirs (the rest are visitors).
    /// </summary>
    private CallsignStyle PickStyle()
    {
        var tactical = MilitaryCallsigns.All;
        if (tactical.Count > 0 && _rng.NextDouble() < 0.5)
        {
            var pool = _homeUnits.Count > 0 && _rng.NextDouble() < 0.75 ? _homeUnits : tactical;
            var t = pool[_rng.Next(pool.Count)];
            return new CallsignStyle(t.Callword, 2, [t.TypeCode], t.FlightNumbers);
        }
        return Styles[_rng.Next(Styles.Length)];
    }

    /// <summary>Approach speed for a type from the FAA database, with a little pilot variation; 130 kt if unknown.</summary>
    private double ApproachSpeed(string type) =>
        (AircraftPerformance.Find(type)?.ApproachSpeedKt ?? 130) + _rng.Next(0, 9);

    private readonly List<SimAircraft> _aircraft = [];
    private readonly Random _rng;

    /// <param name="seed">Repeatable traffic (tests); null = different every time.</param>
    public TrainingTraffic(int? seed = null) => _rng = seed is int s ? new Random(s) : new Random();
    private double _lastStep = double.NaN;
    private double _lastReport;

    public IReadOnlyList<SimAircraft> Aircraft => _aircraft;
    public ParGeometry? Geometry { get; set; }
    public bool Paused { get; set; }
    public TrainingDifficulty Difficulty { get; set; } = TrainingDifficulty.Normal;

    /// <summary>Aircraft that go missed (or fly a touch and go / low approach) are vectored round approach's radar
    /// pattern and handed off again for another approach, as in GCA pattern work. They stay on radar throughout.</summary>
    public bool ReturnForAnotherApproach { get; set; } = true;

    /// <summary>Handoffs on final only, or also on a dogleg or base leg for final to turn in.</summary>
    public HandoffMix Handoffs { get; set; } = HandoffMix.FinalOnly;

    /// <summary>Aircraft that have flown approach's pattern back to final, reset for another approach; approach
    /// then offers them to the final controller.</summary>
    public IReadOnlyList<SimAircraft> TakeReturningAircraft()
    {
        var back = _aircraft.Where(a => a.PatternDone).ToList();
        foreach (var a in back) a.ResetForApproach();
        return back;
    }

    /// <summary>Aircraft in approach's pattern and the leg they are on.</summary>
    public IEnumerable<(string Callsign, string Leg)> InPattern => _aircraft.Where(a => a.InPattern).Select(a => (a.Callsign, a.PatternLegName));
    /// <summary>Ceiling (ft AGL) and visibility (SM) at the field, e.g. from the live METAR; null = good weather.
    /// Pilots compare them with the decision height to decide whether they can see the runway.</summary>
    public (double? CeilingFt, double? VisibilitySm) FieldWeather { get; set; }

    /// <summary>How approaches ended (landed where, touch and go, low approach, ground contact short), oldest first.</summary>
    public IEnumerable<(string Callsign, string Text)> TakeResults()
    {
        foreach (var a in _aircraft)
            while (a.Results.TryDequeue(out var text)) yield return (a.Callsign, text);
    }

    /// <summary>Things pilots said on their own (e.g. why they are going around), oldest first.</summary>
    public IEnumerable<(string Callsign, string Text)> TakePilotCalls()
    {
        foreach (var a in _aircraft)
            while (a.Calls.TryDequeue(out var text)) yield return (a.Callsign, text);
    }
    /// <summary>Surface wind for new arrivals (true direction, knots, gust knots), e.g. from the live METAR;
    /// null = a random wind for the difficulty. A null direction means variable.</summary>
    public (double? FromDeg, double Kt, double? GustKt)? SurfaceWind { get; set; }
    /// <summary>Simulation clock, seconds: stops while paused, so pilot reaction delays wait out a pause.</summary>
    public double SimTime { get; private set; }

    public SimAircraft Spawn(SpawnOptions o)
    {
        var geo = Geometry ?? throw new InvalidOperationException("Select a runway before spawning training traffic.");
        string cs = o.Callsign is { Length: > 0 } custom ? custom.Trim().ToUpperInvariant().Replace(" ", "") : "";
        bool generated = cs.Length == 0;
        // Pick a military callsign style whose callsign fits the radar and is not a civil designator.
        CallsignStyle style;
        (string Prefix, int Digits)? fit;
        int tries = 0;
        do
        {
            style = PickStyle();
            fit = FitCallsign(style.Prefix, style.FlightNumbers ? 2 : style.Digits, style.FlightNumbers);
        } while (fit == null && ++tries < 50);
        var (prefix, digits) = fit ?? ("VV", 3);
        if (fit == null) style = Styles[0];
        while (cs.Length == 0 || _aircraft.Any(a => a.Callsign == cs))
        {
            // Fighter flights are numbered 11–14, 21–24, …; others use mission/tail-style numbers.
            int number = style.FlightNumbers
                ? _rng.Next(1, 9) * 10 + _rng.Next(1, 5)
                : _rng.Next((int)Math.Pow(10, digits - 1), (int)Math.Pow(10, digits));
            cs = prefix + number;
        }
        // A generated callsign is spoken from its own callword ("BNS21": "Banshee two one"), never looked up as an
        // airline. A typed one ("RPA4567") is spoken as typed, through the FAA list.
        if (generated && !RadioSpeech.ServicePrefixes.ContainsKey(style.Prefix)) RadioSpeech.RegisterSpokenCallword(cs, style.Prefix);

        // A custom callsign built on a listed callword ("BANSHEE21") flies that unit's aircraft unless a type is given.
        string word = new(cs.TakeWhile(char.IsLetter).ToArray());
        var unit = o.Callsign is { Length: > 0 } ? MilitaryCallsigns.All.FirstOrDefault(m => m.Callword == word) : null;
        string type = o.TypeCode is { Length: > 0 } t ? t.Trim().ToUpperInvariant()
            : unit?.TypeCode ?? style.Types[_rng.Next(style.Types.Length)];
        double rangeFt = o.RangeNm * Units.FeetPerNm;
        var ac = new SimAircraft(cs, type, o, _rng)
        {
            RangeFt = rangeFt,
            CrossFt = o.CrossFt,
            ThresholdRangeFt = geo.ThresholdRangeFt,
            HeightFt = o.AltitudeFt is double alt
                ? Math.Min(alt - SimAircraft.CurvatureDropFt(rangeFt - geo.ThresholdRangeFt, o.CrossFt), geo.GlidepathHeightFt(rangeFt) - 300)
                : geo.GlidepathHeightFt(rangeFt) + o.HeightOffsetFt,
            TrackRelDeg = -o.InterceptAngleDeg * Math.Sign(o.CrossFt == 0 ? 1 : o.CrossFt),
            SpeedKt = o.SpeedKt > 0 ? o.SpeedKt : ApproachSpeed(type),
            GearDown = o.GearDown ?? _rng.NextDouble() < 0.5,
        };
        if (o.HeadingInTens)
        {
            // Approach assigns headings in tens: the arrival is on one when it is handed off.
            double course = geo.Runway.MagneticCourseDeg;
            double assigned = Math.Round(Units.Norm360(course + ac.TrackRelDeg) / 10) * 10;
            ac.TrackRelDeg = Units.Norm180(assigned - course);
        }
        ac.HeadingRelDeg = ac.TrackRelDeg;
        ac.CommandedHeadingRelDeg = ac.HeadingRelDeg;
        _aircraft.Add(ac);
        return ac;
    }

    public SimAircraft SpawnRandom(PilotMode mode, string? callsign = null, string? typeCode = null, double? skill = null)
    {
        var d = DifficultyProfile.For(Difficulty);
        double Between(double lo, double hi) => lo + _rng.NextDouble() * (hi - lo);
        var live = SurfaceWind;
        var leg = Handoffs switch
        {
            HandoffMix.FinalOnly => HandoffLeg.Final,
            HandoffMix.BaseAndDogleg => _rng.Next(2) == 0 ? HandoffLeg.Dogleg : HandoffLeg.Base,
            _ => _rng.Next(4) switch { 0 => HandoffLeg.Dogleg, 1 => HandoffLeg.Base, _ => HandoffLeg.Final },
        };
        int side = _rng.Next(2) == 0 ? -1 : 1;
        // Final: 12-15 NM out at 2,000-3,000 ft, time for the radio check, brief and missed approach
        // instructions before glidepath intercept (about 6-9 NM on a 3° path). Dogleg: 14-16 NM out, 1.5-3 NM off
        // the centerline, 30-45° toward it. Base: 3-5 NM abeam, heading straight for the centerline, for final
        // to turn in (outside the PAR's azimuth coverage until then; on the ASR).
        var (rangeNm, crossFt, intercept) = leg switch
        {
            HandoffLeg.Dogleg => (14 + _rng.NextDouble() * 2, side * (1.5 + _rng.NextDouble() * 1.5) * Units.FeetPerNm, 30 + _rng.NextDouble() * 15),
            HandoffLeg.Base => (9 + _rng.NextDouble() * 2, side * (3 + _rng.NextDouble() * 2) * Units.FeetPerNm, 90.0),
            _ => (12 + _rng.NextDouble() * 3, (_rng.NextDouble() - 0.5) * 2 * d.CrossFt, _rng.NextDouble() * d.InterceptDeg),
        };
        return Spawn(new SpawnOptions
        {
            Callsign = callsign,
            TypeCode = typeCode,
            RangeNm = rangeNm,
            CrossFt = crossFt,
            AltitudeFt = 2000 + _rng.Next(0, 11) * 100,
            SpeedKt = 0, // from the callsign style's aircraft
            InterceptAngleDeg = intercept,
            Leg = leg,
            HeadingInTens = true,
            Mode = mode,
            Skill = skill ?? Between(d.SkillLo, d.SkillHi),
            WindFromDeg = live?.FromDeg ?? _rng.Next(0, 36) * 10,
            WindKt = live?.Kt ?? _rng.Next(d.WindKtLo, d.WindKtHi + 1),
            HeadingDriftDegPerMin = d.HeadingDriftDegPerMin * Between(0.6, 1.2),
            VsDriftFpmPerMin = d.VsDriftFpmPerMin * Between(0.6, 1.2),
            WindVeerDeg = d.WindVeerDeg * Between(0.5, 1.0),
            WindShearKt = d.WindShearKt * Between(0.5, 1.0),
            GustKt = live?.GustKt is double g ? Math.Max(d.GustKt, g - live.Value.Kt) : d.GustKt,
        });
    }

    public void Remove(string callsign) => _aircraft.RemoveAll(a => a.Callsign == callsign);

    public void Clear() => _aircraft.Clear();

    public SimAircraft? Find(string callsign) => _aircraft.FirstOrDefault(a => a.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));

    /// <summary>Advances the simulation and returns position reports (5 Hz) for the track store.</summary>
    public IEnumerable<TrafficMessage> Step(double now)
    {
        var geo = Geometry;
        if (geo == null) { _lastStep = now; yield break; }
        double dt = double.IsNaN(_lastStep) ? 0 : Math.Min(now - _lastStep, 0.5);
        _lastStep = now;
        if (!Paused && dt > 0)
        {
            SimTime += dt;
            foreach (var a in _aircraft)
            {
                a.ReturnsToPattern = ReturnForAnotherApproach;
                a.Step(dt, SimTime, geo, FieldWeather);
            }
        }

        // A full-stop landing ends the sortie; anything that climbed out is flying approach's pattern instead.
        foreach (var gone in _aircraft.Where(a => a.Finished).ToList())
        {
            _aircraft.Remove(gone);
            yield return new TargetRemoved(gone.Callsign);
        }

        if (now - _lastReport < 0.2) yield break;
        _lastReport = now;
        double courseTrue = geo.Runway.TrueHeadingDeg;
        foreach (var a in _aircraft)
        {
            var p = new ApproachPoint(a.RangeFt, a.CrossFt, a.HeightFt);
            var geoPt = geo.Frame.ToGeo(geo.ToEnu(p));
            double trackTrue = Units.DegToRad(courseTrue + a.TrackRelDeg);
            double gs = Paused ? 0 : a.GroundSpeedKt * Units.MpsPerKnot;
            var vel = new Vec3(gs * Math.Sin(trackTrue), gs * Math.Cos(trackTrue), Paused ? 0 : a.VerticalSpeedFpm / 60.0 * Units.MetersPerFoot);
            yield return new PositionReport(a.Callsign, now, ReportSource.Simulated, geoPt, vel, a.TypeCode,
                geoPt.AltFt, Units.Norm360(courseTrue + a.HeadingRelDeg), a.GroundSpeedKt, Frozen: Paused);
        }
    }
}

public sealed class SimAircraft
{
    private readonly SpawnOptions _opt;
    private readonly Random _rng;
    private double _headingWander, _vsWander;
    private double _headingBias, _vsBias, _gust, _spawnHeightFt = double.NaN;
    private readonly int _driftSign, _vsDriftSign;
    private readonly Queue<(double At, Action Do)> _pending = new();
    /// <summary>The assigned heading before the last transmission changed it, for "disregard".</summary>
    private double? _headingBeforeLast;

    internal SimAircraft(string callsign, string typeCode, SpawnOptions opt, Random rng)
    {
        Callsign = callsign;
        TypeCode = typeCode;
        _opt = opt;
        _rng = rng;
        HandoffLeg = opt.Leg;
        Mode = opt.Mode;
        Descending = opt.Mode == PilotMode.SelfFlying;
        // A self-flying arrival is like a pilot flying the ILS on another frequency: tower has cleared them.
        if (opt.Mode == PilotMode.SelfFlying) Clearance = LandingIntent.FullStop;
        _driftSign = rng.Next(2) == 0 ? -1 : 1;
        _vsDriftSign = rng.Next(2) == 0 ? -1 : 1;
    }

    public string Callsign { get; }
    public string TypeCode { get; }
    public PilotMode Mode { get; set; }

    // Approach-frame state. Angles are relative to the final course (positive = right).
    public double RangeFt { get; internal set; }
    public double CrossFt { get; internal set; }
    /// <summary>Height above the threshold's local tangent plane (the frame the glidepath is straight in).</summary>
    public double HeightFt { get; internal set; }
    internal double ThresholdRangeFt { get; init; }

    /// <summary>
    /// Altitude above threshold elevation (geodetic): the tangent plane falls away from the earth by d²/2R, about
    /// 200 ft at 15 NM. This is what the altimeter, Mode C and the pilot read; level flight holds it constant.
    /// </summary>
    public double AltitudeAboveThresholdFt => HeightFt + CurvatureDropFt(RangeFt - ThresholdRangeFt, CrossFt);

    private const double EarthRadiusFt = 6_371_000 * 3.280839895;

    /// <summary>How far the threshold's tangent plane is above the earth's surface at a horizontal offset, feet.</summary>
    public static double CurvatureDropFt(double alongFt, double crossFt) => (alongFt * alongFt + crossFt * crossFt) / (2 * EarthRadiusFt);
    public double HeadingRelDeg { get; internal set; }
    public double TrackRelDeg { get; internal set; }
    public double SpeedKt { get; internal set; }
    public double GroundSpeedKt { get; private set; }
    public double VerticalSpeedFpm { get; private set; }

    public double CommandedHeadingRelDeg { get; internal set; }
    public double CommandedVsFpm { get; private set; }
    public bool Descending { get; private set; }
    public bool GoingAround { get; private set; }
    public bool Landed { get; private set; }
    public bool Finished { get; private set; }
    public string? LastReadback { get; private set; }

    /// <summary>Landing gear; the USN/USA wheels-down check (§2-1-25) lowers it if it is up.</summary>
    public bool GearDown { get; internal set; }
    /// <summary>Told "DO NOT ACKNOWLEDGE FURTHER TRANSMISSIONS" (§5-10-10): no more readbacks.</summary>
    public bool NoAcknowledge { get; private set; }
    /// <summary>The tower clearance relayed by final (land, touch and go, low approach, option); null = none.</summary>
    public LandingIntent? Clearance { get; private set; }
    public bool ClearedToLand => Clearance is not null;
    /// <summary>Why the pilot went around on their own at decision height, if they did.</summary>
    public string? GoAroundReason { get; private set; }
    internal Queue<string> Calls { get; } = new();
    /// <summary>How the approach ended, for the controller (see <see cref="TrainingTraffic.TakeResults"/>).</summary>
    internal System.Collections.Concurrent.ConcurrentQueue<string> Results { get; } = new();
    private ApproachGrader _grade = new();

    /// <summary>Grades a talk-down (controller-directed only) when it ends; the grade joins the results.</summary>
    private void Grade(int outcomePoints, string outcome, bool fail = false)
    {
        if (Mode != PilotMode.ControllerDirected) return;
        if (_grade.Finish(outcomePoints, outcome, GearReportedDown, fail) is { } line) Results.Enqueue(line);
    }
    private bool _decided;
    private (double? CeilingFt, double? VisibilitySm) _wx;

    /// <summary>Where approach handed this arrival over: on final, a dogleg or a base leg.</summary>
    public HandoffLeg HandoffLeg { get; internal set; }

    /// <summary>Back from approach's pattern for another approach (says so on check-in).</summary>
    public bool AnotherApproach { get; internal set; }

    /// <summary>Continued at decision height with the runway in sight: flying the last part visually, lining up
    /// with the runway and following the glidepath on their own.</summary>
    public bool Visual { get; private set; }

    // ---- Approach's radar pattern (after a missed approach) ----
    public enum PatternLeg { Upwind, Crosswind, Downwind, Base, Intercept }
    /// <summary>Set by the traffic sim: missed approaches join approach's pattern instead of leaving.</summary>
    internal bool ReturnsToPattern { get; set; }
    /// <summary>With approach, being vectored round for another approach (off the final frequency).</summary>
    public bool InPattern { get; private set; }
    /// <summary>Back on final, ready to be handed off again.</summary>
    public bool PatternDone { get; private set; }
    public PatternLeg Leg { get; private set; }
    public string PatternLegName => Leg switch { PatternLeg.Intercept => "turning final", _ => Leg.ToString().ToLowerInvariant() };
    private int _patternSide = 1;
    private double _patternEntryFt, _patternSpeedKt, _approachSpeedKt, _patternSec;

    /// <summary>Gear reported down to the final controller (check-in or wheels check), so the reminder can be dropped (§2-1-25).</summary>
    public bool GearReportedDown { get; private set; }

    /// <summary>Below the ceiling and within the visibility of the runway environment.</summary>
    public bool RunwayInSight
    {
        get
        {
            double distNm = Math.Max(0, RangeFt - ThresholdRangeFt) / Units.FeetPerNm;
            bool belowCeiling = _wx.CeilingFt is not double c || AltitudeAboveThresholdFt <= c;
            double visNm = (_wx.VisibilitySm ?? 10) * 0.869;
            return belowCeiling && distNm <= Math.Min(visNm, 5) && (_wx.VisibilitySm ?? 10) >= 0.5;
        }
    }
    /// <summary>No-gyro: turning left (−1) or right (+1) until "stop turn"; turns at half standard rate once told to.</summary>
    private int _noGyroTurn;
    private bool _halfRate;
    public int NoGyroTurnDirection => _noGyroTurn;
    /// <summary>Told to level off; holds altitude until told to resume descent.</summary>
    private bool _leveled;
    private double _rollSec;
    /// <summary>Missed approach instructions given in advance: heading (relative to course) and height above threshold.</summary>
    public double? MissedHeadingRelDeg { get; private set; }
    public double? MissedClimbToFt { get; private set; }
    /// <summary>Height above the threshold the pilot levels at after going missed.</summary>
    public double ClimbToFt { get; private set; } = 2000;
    /// <summary>Told to contact approach/departure; leaves the frequency shortly after reading back.</summary>
    public bool HandedOff { get; private set; }

    /// <summary>Surface wind for this arrival (true direction, knots) and the pilot's current uncorrected drift.</summary>
    public double WindFromDeg => _opt.WindFromDeg;
    public double WindKt => _opt.WindKt;
    public double SkillLevel => _opt.Skill;
    /// <summary>How far off the assigned heading and rate the pilot is flying right now (drift plus wobble).</summary>
    public double HeadingDriftDeg => _headingBias + _headingWander;
    public double VsDriftFpm => _vsBias + _vsWander;

    /// <summary>Mean-reverting noise: standard deviation <paramref name="sd"/>, correlation time <paramref name="tauSec"/>.</summary>
    private double Wobble(double x, double sd, double tauSec, double dt, double limit) =>
        Math.Clamp(x - x / tauSec * dt + sd * Math.Sqrt(2 * dt / tauSec) * Gauss(), -limit, limit);

    private double ReactionDelay => 1.0 + (1 - _opt.Skill) * 2.0 + _rng.NextDouble();

    /// <summary>
    /// Initial call to the final controller: "the last assigned heading, altitude, and the current status
    /// of the gear" (NAVAIR 00-80T-112 ¶25.3.1.1).
    /// </summary>
    public string CheckInCall(string facility, double courseMagDeg, double thresholdElevFt)
    {
        GearReportedDown |= GearDown;
        double hdg = Units.Norm360(courseMagDeg + CommandedHeadingRelDeg);
        return $"{facility} final, {RadioSpeech.Callsign(Callsign)}, heading {RadioSpeech.Heading(hdg)}, "
               + $"{RadioSpeech.Altitude(AltitudeAboveThresholdFt + thresholdElevFt)}, gear {(GearDown ? "down" : "up")}"
               + (AnotherApproach ? ", for another approach." : ".");
    }

    /// <summary>What the pilot is doing, for the controller's training display.</summary>
    public string Status(double courseMagDeg, double thresholdElevFt)
    {
        double hdg = Units.Norm360(courseMagDeg + HeadingRelDeg);
        string vs = Math.Abs(VerticalSpeedFpm) < 100 ? "level" : VerticalSpeedFpm < 0 ? $"descending {-VerticalSpeedFpm:0} fpm" : $"climbing {VerticalSpeedFpm:0} fpm";
        string phase = Landed ? "landed" : GoingAround ? "going missed" : !Descending ? "awaiting descent"
            : Clearance is { } c ? $"on final, cleared {c switch { LandingIntent.TouchAndGo => "touch and go", LandingIntent.LowApproach => "low approach", LandingIntent.Option => "option", _ => "to land" }}"
            : "on final";
        return $"HDG {(hdg < 0.5 ? 360 : hdg):000} · {AltitudeAboveThresholdFt + thresholdElevFt:#,0} FT · {vs} · {SpeedKt:0} KT · GEAR {(GearDown ? "DOWN" : "UP")} · {phase}";
    }

    /// <summary>
    /// A controller transmission. The pilot repeats headings and clearances "until the final controller
    /// advises 'do not acknowledge further transmissions'" (AFMAN 11-217v1 ¶12.3.3), then flies silently,
    /// adjusting rate of descent in proportion to glidepath calls. A missed approach is always acknowledged
    /// (NAVEDTRA 14342A ch. 10). Actions happen after a human reaction delay.
    /// </summary>
    /// <summary>The controller transmitted but nothing could be made of it: no action, but not silence either.</summary>
    public void HeardUnclear(double now, double? startedAt = null)
    {
        if (Mode == PilotMode.ControllerDirected) _grade.Keyed(now, startedAt);
    }

    /// <param name="startedAt">When the controller began this transmission (sim time), for the grade's silence.</param>
    public PilotResponse Receive(Transmission t, double now, double courseMagDeg, double recommendedFpm, double thresholdElevFt = 0, double? startedAt = null)
    {
        double delay = ReactionDelay;
        if (Mode == PilotMode.ControllerDirected) _grade.Heard(t, now, RangeFt, startedAt);
        var actions = new List<string>();
        var readback = new List<string>();
        string cs = RadioSpeech.Callsign(Callsign);
        bool wasNoAcknowledge = NoAcknowledge;
        if (t.Cancelled) return new PilotResponse("nothing (the controller disregarded that transmission)", null);
        if (t.Disregard)
        {
            // "Disregard": drop what the last transmission asked for; a heading already taken goes back.
            _pending.Clear();
            if (_headingBeforeLast is double back) CommandedHeadingRelDeg = back;
            _headingBeforeLast = null;
            LastReadback = "disregarded the last instruction";
            return new PilotResponse(LastReadback, null);
        }
        _headingBeforeLast = t.HeadingDeg is not null || t.TurnByDeg is not null ? CommandedHeadingRelDeg : null;
        double? hdgRel = t.HeadingDeg is double hd ? Units.Norm180(hd - courseMagDeg) : null;
        double? climbAgl = t.ClimbToFt is double c ? c - thresholdElevFt : null;
        string Climb(double agl) => $"climb and maintain {RadioSpeech.Altitude(agl + thresholdElevFt)}";
        string Hdg(double rel) => $"heading {RadioSpeech.Heading(Units.Norm360(rel + courseMagDeg))}";

        if (t.MissedApproachInstructions)
        {
            // Remembered for later; read back like any instruction (before "do not acknowledge").
            if (hdgRel is double mh) MissedHeadingRelDeg = mh;
            if (climbAgl is double ma) MissedClimbToFt = ma;
            LastReadback = "missed approach instructions noted";
            // Read back as instructions for later ("Roger, in the event of missed approach, …"), so it is not
            // mistaken for the pilot going missed now.
            var parts = new List<string> { "Roger, in the event of missed approach" };
            if (climbAgl is double ra) parts.Add(Climb(ra));
            parts.Add(hdgRel is double rh ? Hdg(rh) : "runway heading");
            string? rb = NoAcknowledge ? null : $"{string.Join(", ", parts)}, {cs}.";
            return new PilotResponse(LastReadback, rb);
        }
        if (t.MissedApproach && t.ConditionalMissedApproach && RunwayInSight)
        {
            // "…if runway or approach lights not in sight, execute missed approach": they are in sight.
            LastReadback = "runway in sight, continuing";
            return new PilotResponse(LastReadback, $"Runway in sight, {cs}.");
        }
        if (t.ContactTower)
        {
            LastReadback = $"will contact {(t.Text.Contains("ground", StringComparison.OrdinalIgnoreCase) ? "ground" : "tower")} after landing";
            return new PilotResponse(LastReadback, NoAcknowledge ? null : $"Wilco, {cs}.");
        }
        if (t.MissedApproach)
        {
            // Fly what this transmission says, else the missed approach instructions given earlier, else
            // runway heading and 2,000 ft. A missed approach is always acknowledged.
            double? heading = hdgRel ?? MissedHeadingRelDeg;
            double climb = climbAgl ?? MissedClimbToFt ?? 2000;
            if (!GoingAround) Grade(5, "missed approach on the controller's instruction");
            _pending.Enqueue((now + delay, () => StartMissed(heading, climb)));
            NoAcknowledge = false;
            var parts = new List<string> { "going missed" };
            if (climbAgl is double ra) parts.Add(Climb(ra));
            if (hdgRel is double rh) parts.Add(Hdg(rh));
            LastReadback = $"going missed, {(heading is double h2 ? $"heading {Units.Norm360(h2 + courseMagDeg):000}" : "runway heading")}, climbing to {climb + thresholdElevFt:0} ft";
            return new PilotResponse(LastReadback, $"{char.ToUpperInvariant(parts[0][0])}{string.Join(", ", parts)[1..]}, {cs}.");
        }
        if (t.LostCommsInstructions)
        {
            LastReadback = "lost communications instructions noted";
            return new PilotResponse(LastReadback, NoAcknowledge && !t.AcknowledgeRequested ? null : $"Roger, {cs}.");
        }
        if (t.ContactApproach)
        {
            _pending.Enqueue((now + delay + 4, () => HandedOff = true));
            LastReadback = "switching to approach";
            return new PilotResponse(LastReadback, $"Switching, {cs}.");
        }
        if (climbAgl is double climbTo && GoingAround)
        {
            _pending.Enqueue((now + delay, () => ClimbToFt = climbTo));
            actions.Add($"climbing to {climbTo + thresholdElevFt:0} ft");
            readback.Add(Climb(climbTo));
        }
        if (t.LevelOff)
        {
            _pending.Enqueue((now + delay, () => { CommandedVsFpm = 0; _leveled = true; }));
            actions.Add("leveling off");
            readback.Add("leveling off");
        }
        if (t.ResumeDescent && Descending)
        {
            double vs = NominalVs(recommendedFpm);
            _pending.Enqueue((now + delay, () => { _leveled = false; CommandedVsFpm = vs; _vsBias = 0; }));
            actions.Add("normal rate of descent");
        }
        if (t.ApproachBrief)
        {
            actions.Add("approach brief");
            readback.Add("roger");
        }
        if (t.HowDoYouHear)
        {
            actions.Add("radio check");
            readback.Add("loud and clear");
        }
        if (t.WheelsDown && !GearDown)
        {
            _pending.Enqueue((now + delay, () => GearDown = true));
            GearReportedDown = true;
            actions.Add("lowering gear");
            readback.Add("gear down");
        }
        if (t.Clearance is { } clearance)
        {
            Clearance = clearance;
            string words = TowerSim.ClearanceWords(clearance);
            actions.Add(words);
            readback.Add(words);
        }
        if (t.HeadingDeg is double hdg)
        {
            double rel = Units.Norm180(hdg - courseMagDeg);
            _pending.Enqueue((now + delay, () => { CommandedHeadingRelDeg = rel; _headingBias = 0; _headingWander = 0; _noGyroTurn = 0; }));
            actions.Add($"heading {hdg:000}");
            readback.Add($"heading {RadioSpeech.Heading(hdg)}");
        }
        else if (t.CourseCall != null && Math.Abs(_headingBias) > 1)
        {
            // Course information is not an instruction: the pilot does not turn toward the centerline, but
            // hearing they are off course they check the heading and stop drifting off the assigned one.
            _pending.Enqueue((now + delay, () => _headingBias = 0));
            actions.Add("back on assigned heading");
        }
        if (t.HalfStandardRate is bool half)
        {
            _pending.Enqueue((now + delay, () => _halfRate = half));
            actions.Add(half ? "half standard rate turns" : "standard rate turns");
            readback.Add(half ? "half standard rate" : "standard rate");
        }
        if (t.NoGyroBrief && t.HalfStandardRate is null) { actions.Add("no-gyro approach"); readback.Add("roger"); }
        if (t.TurnByDeg is double by && t.HeadingDeg is null)
        {
            // From the heading they were assigned (or are holding), not from wherever a wander has taken them.
            _pending.Enqueue((now + delay, () => { CommandedHeadingRelDeg = Units.Norm180(CommandedHeadingRelDeg + by); _headingBias = 0; _noGyroTurn = 0; }));
            string words = $"{(by < 0 ? "left" : "right")} {RadioSpeech.Digits(((int)Math.Abs(by)).ToString())} degree{(Math.Abs(by) == 1 ? "" : "s")}";
            actions.Add($"turning {words}");
            readback.Add(words);
        }
        if (t.NoGyroTurn != 0)
        {
            int dir = t.NoGyroTurn;
            _pending.Enqueue((now + delay, () => { _noGyroTurn = dir; _headingBias = 0; }));
            actions.Add(dir < 0 ? "turning left" : "turning right");
        }
        if (t.StopTurn)
        {
            // Rolls out on whatever heading they have when they hear it.
            _pending.Enqueue((now + delay, () => { _noGyroTurn = 0; CommandedHeadingRelDeg = HeadingRelDeg; _headingBias = 0; }));
            actions.Add("stopping turn");
        }
        if (t.DoNotAcknowledge)
        {
            NoAcknowledge = true;
            actions.Add("no further acknowledgements");
        }
        if (t.BeginDescent)
        {
            _pending.Enqueue((now + delay, () => { Descending = true; _leveled = false; CommandedVsFpm = NominalVs(recommendedFpm); }));
            actions.Add("descending");
            readback.Add("descending");
        }
        else if (Descending && !_leveled && !t.LevelOff && t.Glidepath != GlidepathCall.None)
        {
            double vs = GlidepathResponse(t.Glidepath, t.GlidepathTrend, NominalVs(recommendedFpm), CommandedVsFpm);
            _pending.Enqueue((now + delay, () => { CommandedVsFpm = vs; _vsBias *= 0.3; }));
            actions.Add($"{-vs:0} fpm");
        }
        else if (!Descending && !GoingAround && !_leveled && !t.LevelOff
                 && t.Glidepath is GlidepathCall.On or GlidepathCall.SlightlyAbove or GlidepathCall.Above or GlidepathCall.WellAbove)
        {
            // Still level, told they are on or above the glidepath: they have reached it, so they start down
            // (and correct if above). Told "below", they hold altitude until they meet it.
            double nominal = NominalVs(recommendedFpm);
            double vs = GlidepathResponse(t.Glidepath, t.GlidepathTrend, nominal, nominal);
            _pending.Enqueue((now + delay, () => { Descending = true; CommandedVsFpm = vs; }));
            actions.Add($"descending, {-vs:0} fpm");
        }
        LastReadback = string.Join(", ", actions);

        // A radio check is always answered; everything else only before "do not acknowledge".
        bool mayReadBack = (!wasNoAcknowledge && !t.DoNotAcknowledge) || t.AcknowledgeRequested;
        var spoken = readback.Where(r => mayReadBack || r == "loud and clear").ToList();
        if (spoken.Count == 0 && t.AcknowledgeRequested) spoken.Add("roger");
        string? reply = spoken.Count == 0 ? null : $"{string.Join(", ", spoken)}, {cs}.";
        if (reply != null) reply = char.ToUpperInvariant(reply[0]) + reply[1..];
        return new PilotResponse(actions.Count > 0 ? LastReadback : "no action", reply);
    }

    public void BeginDescent(double now, double recommendedFpm)
    {
        _pending.Enqueue((now + ReactionDelay, () =>
        {
            Descending = true;
            _leveled = false;
            CommandedVsFpm = NominalVs(recommendedFpm);
        }));
    }

    public void SetHeading(double headingMagDeg, double courseMagDeg, double now)
    {
        double rel = Units.Norm180(headingMagDeg - courseMagDeg);
        _pending.Enqueue((now + ReactionDelay, () => { CommandedHeadingRelDeg = rel; _headingBias = 0; }));
    }

    public void GoAround(double now) => _pending.Enqueue((now + ReactionDelay, () => StartMissed(MissedHeadingRelDeg, MissedClimbToFt ?? 2000)));

    private void StartMissed(double? headingRel, double climbToFt)
    {
        if (GoingAround) return;
        GoingAround = true;
        _noGyroTurn = 0;
        ClimbToFt = climbToFt;
        CommandedHeadingRelDeg = headingRel ?? 0;
        _headingBias = 0;
    }

    private static double NominalVs(double recommendedFpm) => -Math.Abs(recommendedFpm);

    /// <summary>
    /// The rate of descent a pilot flies after a glidepath call, starting from what they are flying now.
    /// Plain call: correct by an amount that grows with the deviation (never less than they already are).
    /// "…and holding": the correction isn't working, so add to it. "Going above/below": firmly. "…coming
    /// down/up": it is working, keep it. "…rapidly": over-correcting, ease back toward the normal rate.
    /// "On glidepath": back to the normal rate.
    /// </summary>
    public static double GlidepathResponse(GlidepathCall call, CallTrend trend, double nominalFpm, double currentFpm)
    {
        if (call == GlidepathCall.On) return nominalFpm;
        // Negative = descend faster (aircraft is above the glidepath).
        double adjust = call switch
        {
            GlidepathCall.WellAbove => -400,
            GlidepathCall.Above => -250,
            GlidepathCall.SlightlyAbove => -150,
            GlidepathCall.SlightlyBelow => +150,
            GlidepathCall.Below => +250,
            GlidepathCall.WellBelow => +400,
            _ => 0,
        };
        bool above = adjust < 0;
        double More(double a, double b) => above ? Math.Min(a, b) : Math.Max(a, b);
        double target = nominalFpm + adjust;
        double vs = trend switch
        {
            CallTrend.Correcting => currentFpm,
            CallTrend.Rapidly => currentFpm + (nominalFpm - currentFpm) * 0.6,
            CallTrend.Holding => More(currentFpm + adjust * 0.5, target),
            CallTrend.Worsening => More(currentFpm, target) + adjust * 0.5,
            _ => More(currentFpm, target),
        };
        // Within what a pilot on a precision approach would fly: no climbing, no dive.
        return Math.Clamp(vs, nominalFpm - 700, 0);
    }

    /// <summary>
    /// The pilot's decision at decision height: continue only with the runway environment in sight (ceiling
    /// and visibility against the DH and 1/2 SM), a landing clearance, the gear down, and a stabilized
    /// approach (near the glidepath and centerline, sane rate of descent); otherwise go around and say why.
    /// </summary>
    private void DecideAtMinimums(ParGeometry geo, (double? CeilingFt, double? VisibilitySm) wx)
    {
        _decided = true;
        double dh = geo.Site.DecisionHeightFt;
        double gpDev = HeightFt - geo.GlidepathHeightFt(Math.Max(RangeFt, 0));
        double halfWidth = (geo.Runway.WidthFt ?? 150) / 2;
        string? reason =
            wx.CeilingFt < dh || wx.VisibilitySm < 0.5 ? "no runway environment"
            : Clearance is null ? "no landing clearance"
            : !GearDown ? "gear not down"
            : Math.Abs(CrossFt) > halfWidth + 150 ? "not lined up"
            : gpDev > 100 ? "too high"
            : gpDev < -60 ? "too low"
            : VerticalSpeedFpm < -1400 ? "unstable"
            : null;
        if (reason == null) { Visual = true; return; }
        GoAroundReason = reason;
        // Weather below minimums is nobody's fault; everything else was the controller's to prevent.
        Grade(reason == "no runway environment" ? 10 : 0, $"went around: {reason}");
        StartMissed(MissedHeadingRelDeg, MissedClimbToFt ?? 2000);
        Calls.Enqueue($"Going around, {reason}, {RadioSpeech.Callsign(Callsign)}.");
    }

    internal void Step(double dt, double now, ParGeometry geo, (double? CeilingFt, double? VisibilitySm) wx = default)
    {
        _wx = wx;
        if (_approachSpeedKt == 0) _approachSpeedKt = SpeedKt;
        if (!_decided && Descending && !GoingAround && !Landed && RangeFt <= geo.DecisionHeightRangeFt) DecideAtMinimums(geo, wx);
        // Low approach: go around over the runway without touching down.
        if (Clearance == LandingIntent.LowApproach && !GoingAround && !Landed && RangeFt <= geo.ThresholdRangeFt && HeightFt <= 100)
        {
            Results.Enqueue($"low approach, over the threshold at {HeightFt:0} ft, {Offset(CrossFt)}");
            Grade(Math.Abs(CrossFt) <= (geo.Runway.WidthFt ?? 150) / 2 ? 10 : 5, "low approach");
            StartMissed(MissedHeadingRelDeg, MissedClimbToFt ?? 2000);
        }
        while (_pending.Count > 0 && _pending.Peek().At <= now) _pending.Dequeue().Do();

        // Imperfect pilots wobble around what they were told (about ±2° and ±150 fpm for a sloppy one) and keep
        // coming back to it, averaging out over about 20 s; a steady creep off heading is the drift below.
        double sloppiness = 1 - _opt.Skill;
        _headingWander = Wobble(_headingWander, 2.0 * sloppiness, 20, dt, 4);
        _vsWander = Wobble(_vsWander, 150 * sloppiness, 15, dt, 250);
        // Harder scenarios: steady creep away from the assigned heading and rate until the controller corrects it.
        _headingBias = Math.Clamp(_headingBias + _driftSign * _opt.HeadingDriftDegPerMin / 60 * dt, -15, 15);
        if (Descending) _vsBias = Math.Clamp(_vsBias + _vsDriftSign * _opt.VsDriftFpmPerMin / 60 * dt, -400, 400);
        if (_opt.GustKt > 0) _gust = Math.Clamp(_gust + Gauss() * _opt.GustKt * Math.Sqrt(dt) - _gust * 0.3 * dt, -_opt.GustKt, _opt.GustKt * 1.5);

        // Flying visually or on approach's vectors: no more creep off the controller's heading and rate.
        if (Visual || InPattern) { _headingBias = 0; _vsBias = 0; }
        if (Visual) { _headingWander = 0; _vsWander = 0; }

        if (Visual && !GoingAround && !Landed) VisualFly(geo);
        else if (Mode == PilotMode.SelfFlying && !GoingAround) SelfFly(geo);

        if (InPattern) FlyPattern(dt);
        else if (GoingAround)
        {
            // Climb to the missed approach altitude and level off; headings as assigned.
            CommandedVsFpm = Math.Clamp((ClimbToFt - AltitudeAboveThresholdFt) * 3, -500, 1500);
            SpeedKt = Math.Min(SpeedKt + 2 * dt, 180);
        }
        else if (!Descending)
        {
            CommandedVsFpm = 0;
        }

        if (_noGyroTurn != 0 && !Visual)
        {
            // No-gyro: a steady turn (standard rate, or half standard rate when told) until "stop turn".
            double ngRate = _halfRate ? 1.5 : 3.0;
            HeadingRelDeg = Units.Norm180(HeadingRelDeg + _noGyroTurn * ngRate * dt);
            CommandedHeadingRelDeg = HeadingRelDeg;
        }
        else
        {
            // Half-standard-rate turns for small corrections, standard rate otherwise.
            double hdgErr = Units.Norm180(CommandedHeadingRelDeg + _headingWander + _headingBias - HeadingRelDeg);
            double rate = Math.Abs(hdgErr) < 5 || _halfRate ? 1.5 : 3.0;
            HeadingRelDeg += Math.Clamp(hdgErr, -rate * dt, rate * dt);
        }

        double vsTarget = CommandedVsFpm + (Descending ? _vsWander + _vsBias : 0);
        VerticalSpeedFpm += Math.Clamp(vsTarget - VerticalSpeedFpm, -400 * dt, 400 * dt);

        // Wind triangle in the approach frame. Wind "from" is a true direction (the surface wind); aloft it is
        // stronger and veered, blending to the surface wind on descent, plus gusts.
        if (double.IsNaN(_spawnHeightFt)) _spawnHeightFt = Math.Max(HeightFt, 1);
        double aloft = Math.Clamp(HeightFt / _spawnHeightFt, 0, 1);
        double windKt = Math.Max(0, _opt.WindKt + _opt.WindShearKt * aloft + _gust);
        double windTo = Units.DegToRad(_opt.WindFromDeg + _opt.WindVeerDeg * aloft + 180 - geo.Runway.TrueHeadingDeg);
        double h = Units.DegToRad(HeadingRelDeg);
        // Approach frame: inbound component (toward the runway) and right component.
        // Approach speeds are indicated; true airspeed is ~2 % higher per 1,000 ft of altitude.
        double tas = SpeedKt * (1 + 0.02 * Math.Max(0, AltitudeAboveThresholdFt + geo.Runway.ThresholdElevFt) / 1000);
        double vin = tas * Math.Cos(h) + windKt * Math.Cos(windTo);
        double vright = tas * Math.Sin(h) + windKt * Math.Sin(windTo);
        GroundSpeedKt = Math.Sqrt(vin * vin + vright * vright);
        TrackRelDeg = Units.RadToDeg(Math.Atan2(vright, vin));

        double fps = Units.FeetPerNm / 3600.0;
        double dropBefore = CurvatureDropFt(RangeFt - ThresholdRangeFt, CrossFt);
        RangeFt -= vin * fps * dt;
        CrossFt += vright * fps * dt;
        HeightFt += VerticalSpeedFpm / 60.0 * dt;
        // Level flight holds altitude (geodetic), so in the tangent frame it follows the earth's curvature.
        if (!Descending && !GoingAround && !Landed)
            HeightFt -= CurvatureDropFt(RangeFt - ThresholdRangeFt, CrossFt) - dropBefore;

        if (Mode == PilotMode.ControllerDirected && Descending && !GoingAround && !Landed && !InPattern && RangeFt > geo.DecisionHeightRangeFt)
            _grade.Sample(now, geo, RangeFt, CrossFt, HeightFt);

        if (!Landed && !GoingAround && HeightFt <= 0 && RangeFt < geo.ThresholdRangeFt + 200)
        {
            Results.Enqueue(Touchdown(geo));
            GradeTouchdown(geo);
            Landed = true;
            HeightFt = 0;
            Descending = false;
        }
        if (Landed)
        {
            HeightFt = 0;
            VerticalSpeedFpm = 0;
            _rollSec += dt;
            if (Clearance is LandingIntent.TouchAndGo or LandingIntent.Option && _rollSec > 6)
            {
                // Touch and go: back into the air on the climb-out.
                Landed = false;
                StartMissed(MissedHeadingRelDeg, MissedClimbToFt ?? 2000);
            }
            else if (Clearance is not (LandingIntent.TouchAndGo or LandingIntent.Option))
            {
                SpeedKt = Math.Max(0, SpeedKt - 6 * dt);
                if (SpeedKt < 30) Finished = true;
            }
        }
        if (HeightFt <= 0 && !Landed && !GoingAround && !InPattern && RangeFt >= geo.ThresholdRangeFt + 200)
        {
            // Flew into the ground before the runway: the approach ends here.
            Results.Enqueue($"hit the ground {(RangeFt - geo.ThresholdRangeFt) / Units.FeetPerNm:0.0} NM short of the runway, {Offset(CrossFt)}");
            Grade(0, "ground contact short of the runway", fail: true);
            HeightFt = 0;
            Finished = true;
            return;
        }
        if (HeightFt < 0 && !Landed) HeightFt = 0;
        // A missed approach handed to approach (or flown well past the field without a word) joins the pattern.
        if (GoingAround && ReturnsToPattern && !InPattern && !Landed && (HandedOff || RangeFt < -4 * Units.FeetPerNm)) JoinPattern();
        if (!InPattern && (HandedOff || RangeFt < (GoingAround ? -10 : -3) * Units.FeetPerNm || RangeFt > 30 * Units.FeetPerNm)) Finished = true;
    }

    private static string Offset(double crossFt) =>
        Math.Abs(crossFt) < 10 ? "on the centerline" : $"{Math.Abs(crossFt):0} ft {(crossFt > 0 ? "right" : "left")} of centerline";

    private void GradeTouchdown(ParGeometry geo)
    {
        double past = ThresholdRangeFt - RangeFt;
        bool onRunway = past >= 0 && Math.Abs(CrossFt) <= (geo.Runway.WidthFt ?? 150) / 2
                        && (geo.Runway.LengthFt is not double len || past <= len);
        if (!onRunway) { Grade(0, "touched down off the runway", fail: past < 0); return; }
        // Touchdown zone: the first 3,000 ft (AIM, FAA-H-8083-15).
        if (-VerticalSpeedFpm > 900) Grade(7, "hard landing");
        else if (past > 3000) Grade(7, "landed long, past the touchdown zone");
        else Grade(10, "landed in the touchdown zone");
    }

    /// <summary>Where and how hard the aircraft touched down, as the result of the approach.</summary>
    private string Touchdown(ParGeometry geo)
    {
        double past = ThresholdRangeFt - RangeFt;
        double halfWidth = (geo.Runway.WidthFt ?? 150) / 2;
        string what = Clearance is LandingIntent.TouchAndGo or LandingIntent.Option ? "touch and go" : "landed";
        if (past < 0) return $"touched down {-past:0} ft short of the threshold, {Offset(CrossFt)}";
        if (Math.Abs(CrossFt) > halfWidth) return $"touched down off the side of the runway, {Offset(CrossFt)}";
        if (geo.Runway.LengthFt is double len && past > len) return "touched down beyond the end of the runway";
        double sink = -VerticalSpeedFpm;
        return $"{what}, {past:#,0} ft past the threshold, {Offset(CrossFt)}, {sink:0} fpm{(sink > 900 ? " (hard landing)" : "")}";
    }

    /// <summary>
    /// The last part of the approach once the pilot has the runway at decision height: line up with the
    /// runway and follow the glidepath (visual slope) to touchdown, like any pilot landing visually.
    /// </summary>
    private void VisualFly(ParGeometry geo)
    {
        double crossTarget = Math.Clamp(-CrossFt / 20.0, -10, 10);
        CommandedHeadingRelDeg = Math.Clamp(crossTarget - (TrackRelDeg - HeadingRelDeg), -20, 20);
        double gpErr = HeightFt - geo.GlidepathHeightFt(Math.Max(RangeFt, 0));
        double nominal = -GroundSpeedKt * Units.FeetPerNm / 60.0 * Math.Tan(Units.DegToRad(geo.Site.GlidepathDeg));
        CommandedVsFpm = HeightFt < 30 ? -150 : Math.Clamp(nominal - gpErr * 3, -1200, -100); // flare
    }

    /// <summary>
    /// Approach takes the aircraft after a missed approach and vectors it round a radar pattern back to final:
    /// upwind, a crosswind turn to 2½ NM abeam, downwind, base and a 30° intercept about 11–13 NM out, at
    /// the missed approach altitude. On radar the whole way.
    /// </summary>
    private void JoinPattern()
    {
        InPattern = true;
        HandedOff = true;
        Leg = PatternLeg.Upwind;
        _patternSec = 0;
        _patternSide = Math.Abs(CrossFt) > 1500 ? Math.Sign(CrossFt) : (_rng.Next(2) == 0 ? -1 : 1);
        // Base 12-14 NM out, so the handoff comes about 11-13 NM out with time for the brief, like a new arrival.
        _patternEntryFt = (12 + _rng.NextDouble() * 2) * Units.FeetPerNm;
        // Faster round the pattern than on final (jets about 1.5× approach speed), slowing on base.
        _patternSpeedKt = Math.Clamp(_approachSpeedKt * 1.5, 160, 250);
    }

    private void FlyPattern(double dt)
    {
        double nm = Units.FeetPerNm, side = _patternSide * CrossFt;
        // Turns are led by the turn radius (standard rate, 3°/s) so each leg rolls out where it should.
        double radius = GroundSpeedKt * nm / 3600 / (3 * Math.PI / 180);
        _patternSec += dt;
        switch (Leg)
        {
            case PatternLeg.Upwind:
                if (_patternSec > 30 && (RangeFt < -nm || _patternSec > 90)) Leg = PatternLeg.Crosswind;
                break;
            case PatternLeg.Crosswind:
                CommandedHeadingRelDeg = 90 * _patternSide;
                if (side >= 2.5 * nm - radius) Leg = PatternLeg.Downwind;
                break;
            case PatternLeg.Downwind:
                CommandedHeadingRelDeg = 180;
                if (RangeFt >= _patternEntryFt - radius) Leg = PatternLeg.Base;
                break;
            case PatternLeg.Base:
                CommandedHeadingRelDeg = -90 * _patternSide;
                if (side <= 1.3 * nm + 0.5 * radius) Leg = PatternLeg.Intercept;
                break;
            case PatternLeg.Intercept:
                CommandedHeadingRelDeg = -30 * _patternSide;
                if (side <= 0.15 * nm + 0.13 * radius) { CommandedHeadingRelDeg = 0; PatternDone = true; }
                break;
        }
        CommandedVsFpm = Math.Clamp((ClimbToFt - AltitudeAboveThresholdFt) * 3, -1000, 1500);
        double target = Leg >= PatternLeg.Base ? _approachSpeedKt : _patternSpeedKt;
        SpeedKt += Math.Clamp(target - SpeedKt, -1.5 * dt, 2 * dt);
    }

    /// <summary>Back on final from approach's pattern: a fresh approach with the same pilot and aircraft.</summary>
    internal void ResetForApproach()
    {
        InPattern = PatternDone = GoingAround = HandedOff = Landed = Visual = _decided = _leveled = false;
        _grade = new ApproachGrader();
        NoAcknowledge = GearReportedDown = false;
        AnotherApproach = true;
        HandoffLeg = HandoffLeg.Final;
        Clearance = Mode == PilotMode.SelfFlying ? LandingIntent.FullStop : null;
        Descending = Mode == PilotMode.SelfFlying;
        GoAroundReason = null;
        MissedHeadingRelDeg = null;
        MissedClimbToFt = null;
        ClimbToFt = 2000;
        CommandedVsFpm = 0;
        CommandedHeadingRelDeg = HeadingRelDeg;
        _headingBias = _vsBias = 0;
        _noGyroTurn = 0;
        _halfRate = false;
        _rollSec = 0;
        _spawnHeightFt = Math.Max(HeightFt, 1);
        _pending.Clear();
    }

    /// <summary>Simple ILS-like pilot: intercept and track the centerline and glidepath with lag and error.</summary>
    private void SelfFly(ParGeometry geo)
    {
        double rangeFt = Math.Max(RangeFt, 500);
        double crossTarget = Math.Clamp(-CrossFt / 40.0, -20, 20);
        CommandedHeadingRelDeg = Math.Clamp(crossTarget - (TrackRelDeg - HeadingRelDeg), -25, 25);

        double gpErr = HeightFt - geo.GlidepathHeightFt(rangeFt);
        double nominal = -GroundSpeedKt * Units.FeetPerNm / 60.0 * Math.Tan(Units.DegToRad(geo.Site.GlidepathDeg));
        if (gpErr < -150 && RangeFt > 3 * Units.FeetPerNm) { CommandedVsFpm = 0; return; } // level until intercept
        Descending = true;
        GearDown = true;
        CommandedVsFpm = nominal - Math.Clamp(gpErr * 3, -500, 500);

    }

    private double Gauss()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}

/// <summary>What the pilot does with a transmission, and what (if anything) they say back.</summary>
public sealed record PilotResponse(string Action, string? Reply);
