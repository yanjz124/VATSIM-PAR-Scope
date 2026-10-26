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
        TrainingDifficulty.Easy => "Light wind, precise pilots, close to the centreline.",
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
    /// <summary>Approach speed; 0 = typical for the aircraft type chosen.</summary>
    public double SpeedKt { get; init; }
    public double InterceptAngleDeg { get; init; } = 0;
    public PilotMode Mode { get; init; } = PilotMode.ControllerDirected;
    /// <summary>0 = sloppy, 1 = precise.</summary>
    public double Skill { get; init; } = 0.6;
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

    /// <summary>Half the random arrivals use a squadron tactical callword, half a service/mission callsign.</summary>
    private CallsignStyle PickStyle()
    {
        var tactical = MilitaryCallsigns.All;
        if (tactical.Count > 0 && _rng.NextDouble() < 0.5)
        {
            var t = tactical[_rng.Next(tactical.Count)];
            return new CallsignStyle(t.Callword, 2, [t.TypeCode], t.FlightNumbers);
        }
        return Styles[_rng.Next(Styles.Length)];
    }

    /// <summary>Approach speed for a type from the FAA database, with a little pilot variation; 130 kt if unknown.</summary>
    private double ApproachSpeed(string type) =>
        (AircraftPerformance.Find(type)?.ApproachSpeedKt ?? 130) + _rng.Next(0, 9);

    private readonly List<SimAircraft> _aircraft = [];
    private readonly Random _rng = new();
    private double _lastStep = double.NaN;
    private double _lastReport;

    public IReadOnlyList<SimAircraft> Aircraft => _aircraft;
    public ParGeometry? Geometry { get; set; }
    public bool Paused { get; set; }
    public TrainingDifficulty Difficulty { get; set; } = TrainingDifficulty.Normal;

    /// <summary>Aircraft that go missed (or fly a touch and go / low approach) rejoin approach's radar pattern and
    /// are handed off again for another approach, as in GCA pattern work.</summary>
    public bool ReturnForAnotherApproach { get; set; } = true;
    /// <summary>Simulated time in approach's pattern before the next handoff, seconds (compressed from a real pattern).</summary>
    public double PatternSec { get; set; } = 120;
    private readonly List<(string Callsign, string TypeCode, PilotMode Mode, double Skill, double Due)> _returning = [];

    /// <summary>Aircraft back from the pattern and due to be handed off again; they are spawned 12–15 NM out.</summary>
    public IReadOnlyList<SimAircraft> SpawnDueReturns()
    {
        var due = _returning.Where(r => r.Due <= SimTime && Find(r.Callsign) == null).ToList();
        var spawned = new List<SimAircraft>();
        foreach (var r in due)
        {
            _returning.Remove(r);
            var ac = SpawnRandom(r.Mode, r.Callsign, r.TypeCode, r.Skill);
            ac.AnotherApproach = true;
            spawned.Add(ac);
        }
        return spawned;
    }

    /// <summary>Callsigns in approach's pattern and the simulated seconds until each comes back.</summary>
    public IEnumerable<(string Callsign, double InSec)> InPattern => _returning.Select(r => (r.Callsign, Math.Max(0, r.Due - SimTime)));
    /// <summary>Ceiling (ft AGL) and visibility (SM) at the field, e.g. from the live METAR; null = good weather.
    /// Pilots compare them with the decision height to decide whether they can see the runway.</summary>
    public (double? CeilingFt, double? VisibilitySm) FieldWeather { get; set; }

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
        var style = PickStyle();
        string cs = o.Callsign is { Length: > 0 } custom ? custom.Trim().ToUpperInvariant().Replace(" ", "") : "";
        while (cs.Length == 0 || _aircraft.Any(a => a.Callsign == cs))
        {
            // Fighter flights are numbered 11–14, 21–24, …; others use mission/tail-style numbers.
            int number = style.FlightNumbers
                ? _rng.Next(1, 9) * 10 + _rng.Next(1, 5)
                : _rng.Next((int)Math.Pow(10, style.Digits - 1), (int)Math.Pow(10, style.Digits));
            cs = style.Prefix + number;
        }

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
        return Spawn(new SpawnOptions
        {
            Callsign = callsign,
            TypeCode = typeCode,
            // Handed to final 12-15 NM out at 2,000-3,000 ft: time for the radio check, approach brief and
            // missed approach instructions before glidepath intercept (about 6-9 NM on a 3° path).
            RangeNm = 12 + _rng.NextDouble() * 3,
            CrossFt = (_rng.NextDouble() - 0.5) * 2 * d.CrossFt,
            AltitudeFt = 2000 + _rng.Next(0, 11) * 100,
            SpeedKt = 0, // from the callsign style's aircraft
            InterceptAngleDeg = _rng.NextDouble() * d.InterceptDeg,
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

    public void Remove(string callsign)
    {
        _aircraft.RemoveAll(a => a.Callsign == callsign);
        _returning.RemoveAll(r => r.Callsign == callsign);
    }

    public void Clear()
    {
        _aircraft.Clear();
        _returning.Clear();
    }

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
            foreach (var a in _aircraft) a.Step(dt, SimTime, geo, FieldWeather);
        }

        foreach (var gone in _aircraft.Where(a => a.Finished).ToList())
        {
            _aircraft.Remove(gone);
            // A full-stop landing ends the sortie; anything that climbed out comes round again.
            if (ReturnForAnotherApproach && gone.GoingAround)
                _returning.Add((gone.Callsign, gone.TypeCode, gone.Mode, gone.SkillLevel, SimTime + PatternSec * (0.8 + 0.4 * _rng.NextDouble())));
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

    internal SimAircraft(string callsign, string typeCode, SpawnOptions opt, Random rng)
    {
        Callsign = callsign;
        TypeCode = typeCode;
        _opt = opt;
        _rng = rng;
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
    private bool _decided;
    private (double? CeilingFt, double? VisibilitySm) _wx;

    /// <summary>Back from approach's pattern for another approach (says so on check-in).</summary>
    public bool AnotherApproach { get; internal set; }

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
    /// <summary>Told to level off; holds altitude until told to resume descent.</summary>
    private bool _levelled;
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
    public double HeadingDriftDeg => _headingBias;
    public double VsDriftFpm => _vsBias;

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
    public PilotResponse Receive(Transmission t, double now, double courseMagDeg, double recommendedFpm, double thresholdElevFt = 0)
    {
        double delay = ReactionDelay;
        var actions = new List<string>();
        var readback = new List<string>();
        string cs = RadioSpeech.Callsign(Callsign);
        bool wasNoAcknowledge = NoAcknowledge;
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
            var parts = new List<string> { "missed approach" };
            if (climbAgl is double ra) parts.Add(Climb(ra));
            if (hdgRel is double rh) parts.Add(Hdg(rh));
            string? rb = NoAcknowledge ? null : $"{char.ToUpperInvariant(parts[0][0])}{string.Join(", ", parts)[1..]}, {cs}.";
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
            LastReadback = "will contact tower after landing";
            return new PilotResponse(LastReadback, NoAcknowledge ? null : $"Wilco, {cs}.");
        }
        if (t.MissedApproach)
        {
            // Fly what this transmission says, else the missed approach instructions given earlier, else
            // runway heading and 2,000 ft. A missed approach is always acknowledged.
            double? heading = hdgRel ?? MissedHeadingRelDeg;
            double climb = climbAgl ?? MissedClimbToFt ?? 2000;
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
            _pending.Enqueue((now + delay, () => { CommandedVsFpm = 0; _levelled = true; }));
            actions.Add("levelling off");
            readback.Add("levelling off");
        }
        if (t.ResumeDescent && Descending)
        {
            double vs = NominalVs(recommendedFpm);
            _pending.Enqueue((now + delay, () => { _levelled = false; CommandedVsFpm = vs; _vsBias = 0; }));
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
            _pending.Enqueue((now + delay, () => { CommandedHeadingRelDeg = rel; _headingBias = 0; }));
            actions.Add($"heading {hdg:000}");
            readback.Add($"heading {RadioSpeech.Heading(hdg)}");
        }
        if (t.DoNotAcknowledge)
        {
            NoAcknowledge = true;
            actions.Add("no further acknowledgements");
        }
        if (t.BeginDescent)
        {
            _pending.Enqueue((now + delay, () => { Descending = true; _levelled = false; CommandedVsFpm = NominalVs(recommendedFpm); }));
            actions.Add("descending");
        }
        else if (Descending && !_levelled && !t.LevelOff && t.Glidepath != GlidepathCall.None)
        {
            double adjust = t.Glidepath switch
            {
                GlidepathCall.WellAbove => -400,
                GlidepathCall.Above => -250,
                GlidepathCall.SlightlyAbove => -150,
                GlidepathCall.SlightlyBelow => +150,
                GlidepathCall.Below => +250,
                GlidepathCall.WellBelow => +400,
                _ => 0,
            };
            // Already correcting: ease off rather than pile on.
            if (t.GlidepathTrend == CallTrend.Correcting) adjust *= 0.4;
            if (t.GlidepathTrend == CallTrend.Rapidly) adjust *= 1.4;
            double vs = NominalVs(recommendedFpm) + adjust;
            _pending.Enqueue((now + delay, () => { CommandedVsFpm = vs; _vsBias *= 0.3; }));
            actions.Add($"{-vs:0} fpm");
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
        ClimbToFt = climbToFt;
        CommandedHeadingRelDeg = headingRel ?? 0;
        _headingBias = 0;
    }

    private static double NominalVs(double recommendedFpm) => -Math.Abs(recommendedFpm);

    /// <summary>
    /// The pilot's decision at decision height: continue only with the runway environment in sight (ceiling
    /// and visibility against the DH and 1/2 SM), a landing clearance, the gear down, and a stabilized
    /// approach (near the glidepath and centreline, sane rate of descent); otherwise go around and say why.
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
        if (reason == null) return;
        GoAroundReason = reason;
        StartMissed(MissedHeadingRelDeg, MissedClimbToFt ?? 2000);
        Calls.Enqueue($"Going around, {reason}, {RadioSpeech.Callsign(Callsign)}.");
    }

    internal void Step(double dt, double now, ParGeometry geo, (double? CeilingFt, double? VisibilitySm) wx = default)
    {
        _wx = wx;
        if (!_decided && Descending && !GoingAround && !Landed && RangeFt <= geo.DecisionHeightRangeFt) DecideAtMinimums(geo, wx);
        // Low approach: go around over the runway without touching down.
        if (Clearance == LandingIntent.LowApproach && !GoingAround && !Landed && RangeFt <= geo.ThresholdRangeFt && HeightFt <= 100)
            StartMissed(MissedHeadingRelDeg, MissedClimbToFt ?? 2000);
        while (_pending.Count > 0 && _pending.Peek().At <= now) _pending.Dequeue().Do();

        // Imperfect pilots: slow random walks on heading and rate.
        double sloppiness = 1 - _opt.Skill;
        _headingWander = Math.Clamp(_headingWander + Gauss() * 0.6 * sloppiness * Math.Sqrt(dt), -4, 4);
        _vsWander = Math.Clamp(_vsWander + Gauss() * 60 * sloppiness * Math.Sqrt(dt), -250, 250);
        // Harder scenarios: steady creep away from the assigned heading and rate until the controller corrects it.
        _headingBias = Math.Clamp(_headingBias + _driftSign * _opt.HeadingDriftDegPerMin / 60 * dt, -15, 15);
        if (Descending) _vsBias = Math.Clamp(_vsBias + _vsDriftSign * _opt.VsDriftFpmPerMin / 60 * dt, -400, 400);
        if (_opt.GustKt > 0) _gust = Math.Clamp(_gust + Gauss() * _opt.GustKt * Math.Sqrt(dt) - _gust * 0.3 * dt, -_opt.GustKt, _opt.GustKt * 1.5);

        if (Mode == PilotMode.SelfFlying && !GoingAround) SelfFly(geo);

        if (GoingAround)
        {
            // Climb to the missed approach altitude and level off; headings as assigned.
            CommandedVsFpm = Math.Clamp((ClimbToFt - AltitudeAboveThresholdFt) * 3, -500, 1500);
            SpeedKt = Math.Min(SpeedKt + 2 * dt, 180);
        }
        else if (!Descending)
        {
            CommandedVsFpm = 0;
        }

        // Half-standard-rate turns for small corrections, standard rate otherwise.
        double hdgErr = Units.Norm180(CommandedHeadingRelDeg + _headingWander + _headingBias - HeadingRelDeg);
        double rate = Math.Abs(hdgErr) < 5 ? 1.5 : 3.0;
        HeadingRelDeg += Math.Clamp(hdgErr, -rate * dt, rate * dt);

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

        if (!Landed && !GoingAround && HeightFt <= 0 && RangeFt < geo.ThresholdRangeFt + 200)
        {
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
        if (HeightFt < 0 && !Landed) HeightFt = 0; // flew into the ground short: hold at surface
        if (HandedOff || RangeFt < (GoingAround ? -10 : -3) * Units.FeetPerNm || RangeFt > 30 * Units.FeetPerNm) Finished = true;
    }

    /// <summary>Simple ILS-like pilot: intercept and track the centreline and glidepath with lag and error.</summary>
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
