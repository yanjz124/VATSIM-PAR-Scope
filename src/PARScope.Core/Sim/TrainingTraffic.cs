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

public sealed record SpawnOptions
{
    public double RangeNm { get; init; } = 8;
    public double CrossFt { get; init; } = 0;
    /// <summary>Height relative to the glidepath at the spawn range (negative = below).</summary>
    public double HeightOffsetFt { get; init; } = -300;
    /// <summary>Approach speed; 0 = typical for the aircraft type chosen.</summary>
    public double SpeedKt { get; init; }
    public double InterceptAngleDeg { get; init; } = 0;
    public PilotMode Mode { get; init; } = PilotMode.ControllerDirected;
    /// <summary>0 = sloppy, 1 = precise.</summary>
    public double Skill { get; init; } = 0.6;
    public double WindFromDeg { get; init; } = 0;
    public double WindKt { get; init; } = 0;
    public string? TypeCode { get; init; }
}

/// <summary>Simulated arrivals for PAR training. All kinematics run in the runway's approach frame.</summary>
public sealed class TrainingTraffic
{
    /// <summary>
    /// Callsign styles used by US military traffic at a GCA field. Service prefixes per FAA JO 7110.65
    /// §2-4-20 (VV "Navy", VM "Marine", R "Army", AF "Air Force"); mission callsigns REACH (AMC airlift),
    /// PAT (Army priority air transport), CONVOY (Navy fleet logistics), RAWHIDE (C-2 COD); squadron
    /// tactical callsigns are two-digit flight numbers. Each style carries plausible types and approach speeds.
    /// </summary>
    private sealed record CallsignStyle(string Prefix, int Digits, string[] Types, int SpeedLo, int SpeedHi);

    private static readonly CallsignStyle[] Styles =
    [
        new("VV", 4, ["T45", "T6", "P8", "E2"], 120, 140),
        new("VM", 4, ["C130", "MV22", "UC35"], 110, 130),
        new("R", 5, ["C12", "UH60", "C26"], 100, 120),
        new("AF", 4, ["C130", "T38", "C17"], 120, 140),
        new("REACH", 3, ["C17", "C5M", "KC46"], 130, 145),
        new("PAT", 3, ["C12", "UC35"], 105, 120),
        new("CONVOY", 4, ["C40", "C130"], 130, 140),
        new("RAWHIDE", 2, ["C2"], 110, 120),
        new("VIPER", 2, ["F35"], 135, 150),
        new("RAGE", 2, ["F18S"], 135, 150),
        new("BOLT", 2, ["F18S"], 135, 150),
        new("SHOGUN", 2, ["AV8B"], 120, 140),
        new("VIKING", 2, ["P8"], 135, 145),
    ];

    private readonly List<SimAircraft> _aircraft = [];
    private readonly Random _rng = new();
    private double _lastStep = double.NaN;
    private double _lastReport;

    public IReadOnlyList<SimAircraft> Aircraft => _aircraft;
    public ParGeometry? Geometry { get; set; }
    public bool Paused { get; set; }

    public SimAircraft Spawn(SpawnOptions o)
    {
        var geo = Geometry ?? throw new InvalidOperationException("Select a runway before spawning training traffic.");
        var style = Styles[_rng.Next(Styles.Length)];
        string cs;
        do
        {
            // Tactical flights are numbered like 21, 32; the leading digit is never zero.
            int lo = (int)Math.Pow(10, style.Digits - 1), hi = (int)Math.Pow(10, style.Digits);
            cs = style.Prefix + _rng.Next(lo, hi);
        }
        while (_aircraft.Any(a => a.Callsign == cs));

        double rangeFt = o.RangeNm * Units.FeetPerNm;
        var ac = new SimAircraft(cs, o.TypeCode ?? style.Types[_rng.Next(style.Types.Length)], o, _rng)
        {
            RangeFt = rangeFt,
            CrossFt = o.CrossFt,
            HeightFt = geo.GlidepathHeightFt(rangeFt) + o.HeightOffsetFt,
            TrackRelDeg = -o.InterceptAngleDeg * Math.Sign(o.CrossFt == 0 ? 1 : o.CrossFt),
            SpeedKt = o.SpeedKt > 0 ? o.SpeedKt : _rng.Next(style.SpeedLo, style.SpeedHi + 1),
            GearDown = _rng.NextDouble() < 0.5,
        };
        ac.HeadingRelDeg = ac.TrackRelDeg;
        ac.CommandedHeadingRelDeg = ac.HeadingRelDeg;
        _aircraft.Add(ac);
        return ac;
    }

    public SimAircraft SpawnRandom(PilotMode mode)
    {
        return Spawn(new SpawnOptions
        {
            RangeNm = 6 + _rng.NextDouble() * 4,
            CrossFt = (_rng.NextDouble() - 0.5) * 1600,
            HeightOffsetFt = -400 + _rng.NextDouble() * 300,
            SpeedKt = 0, // from the callsign style's aircraft
            InterceptAngleDeg = _rng.NextDouble() * 10,
            Mode = mode,
            Skill = 0.4 + _rng.NextDouble() * 0.5,
            WindFromDeg = _rng.Next(0, 36) * 10,
            WindKt = _rng.Next(0, 20),
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
            foreach (var a in _aircraft) a.Step(dt, now, geo);

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
                geoPt.AltFt, Units.Norm360(courseTrue + a.HeadingRelDeg), a.GroundSpeedKt);
        }
    }
}

public sealed class SimAircraft
{
    private readonly SpawnOptions _opt;
    private readonly Random _rng;
    private double _headingWander, _vsWander;
    private readonly Queue<(double At, Action Do)> _pending = new();

    internal SimAircraft(string callsign, string typeCode, SpawnOptions opt, Random rng)
    {
        Callsign = callsign;
        TypeCode = typeCode;
        _opt = opt;
        _rng = rng;
        Mode = opt.Mode;
        Descending = opt.Mode == PilotMode.SelfFlying;
    }

    public string Callsign { get; }
    public string TypeCode { get; }
    public PilotMode Mode { get; set; }

    // Approach-frame state. Angles are relative to the final course (positive = right).
    public double RangeFt { get; internal set; }
    public double CrossFt { get; internal set; }
    public double HeightFt { get; internal set; }
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
    public bool ClearedToLand { get; private set; }

    private double ReactionDelay => 1.0 + (1 - _opt.Skill) * 2.0 + _rng.NextDouble();

    /// <summary>
    /// Initial call to the final controller: "the last assigned heading, altitude, and the current status
    /// of the gear" (NAVAIR 00-80T-112 ¶25.3.1.1).
    /// </summary>
    public string CheckInCall(string facility, double courseMagDeg, double thresholdElevFt)
    {
        double hdg = Units.Norm360(courseMagDeg + CommandedHeadingRelDeg);
        return $"{facility} final, {RadioSpeech.Callsign(Callsign)}, heading {RadioSpeech.Heading(hdg)}, "
               + $"{RadioSpeech.Altitude(HeightFt + thresholdElevFt)}, gear {(GearDown ? "down" : "up")}.";
    }

    /// <summary>What the pilot is doing, for the controller's training display.</summary>
    public string Status(double courseMagDeg, double thresholdElevFt)
    {
        double hdg = Units.Norm360(courseMagDeg + HeadingRelDeg);
        string vs = Math.Abs(VerticalSpeedFpm) < 100 ? "level" : VerticalSpeedFpm < 0 ? $"descending {-VerticalSpeedFpm:0} fpm" : $"climbing {VerticalSpeedFpm:0} fpm";
        string phase = Landed ? "landed" : GoingAround ? "going missed" : !Descending ? "awaiting descent"
            : ClearedToLand ? "on final, cleared to land" : "on final";
        return $"HDG {(hdg < 0.5 ? 360 : hdg):000} · {HeightFt + thresholdElevFt:#,0} FT · {vs} · {SpeedKt:0} KT · GEAR {(GearDown ? "DOWN" : "UP")} · {phase}";
    }

    /// <summary>
    /// A controller transmission. The pilot repeats headings and clearances "until the final controller
    /// advises 'do not acknowledge further transmissions'" (AFMAN 11-217v1 ¶12.3.3), then flies silently,
    /// adjusting rate of descent in proportion to glidepath calls. A missed approach is always acknowledged
    /// (NAVEDTRA 14342A ch. 10). Actions happen after a human reaction delay.
    /// </summary>
    public PilotResponse Receive(Transmission t, double now, double courseMagDeg, double recommendedFpm)
    {
        double delay = ReactionDelay;
        var actions = new List<string>();
        var readback = new List<string>();
        string cs = RadioSpeech.Callsign(Callsign);
        bool wasNoAcknowledge = NoAcknowledge;

        if (t.MissedApproach)
        {
            _pending.Enqueue((now + delay, () => GoingAround = true));
            return new PilotResponse("going missed", $"{cs}, going missed.");
        }
        if (t.HowDoYouHear)
        {
            actions.Add("radio check");
            readback.Add("loud and clear");
        }
        if (t.WheelsDown && !GearDown)
        {
            _pending.Enqueue((now + delay, () => GearDown = true));
            actions.Add("lowering gear");
            readback.Add("gear down");
        }
        if (t.ClearedToLand)
        {
            ClearedToLand = true;
            actions.Add("cleared to land");
            readback.Add("cleared to land");
        }
        if (t.HeadingDeg is double hdg)
        {
            double rel = Units.Norm180(hdg - courseMagDeg);
            _pending.Enqueue((now + delay, () => CommandedHeadingRelDeg = rel));
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
            _pending.Enqueue((now + delay, () => { Descending = true; CommandedVsFpm = NominalVs(recommendedFpm); }));
            actions.Add("descending");
        }
        else if (Descending && t.Glidepath != GlidepathCall.None)
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
            _pending.Enqueue((now + delay, () => CommandedVsFpm = vs));
            actions.Add($"{-vs:0} fpm");
        }
        LastReadback = string.Join(", ", actions);

        // A radio check is always answered; everything else only before "do not acknowledge".
        bool mayReadBack = !wasNoAcknowledge && !t.DoNotAcknowledge;
        var spoken = readback.Where(r => mayReadBack || r == "loud and clear").ToList();
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
        _pending.Enqueue((now + ReactionDelay, () => CommandedHeadingRelDeg = rel));
    }

    public void GoAround(double now) => _pending.Enqueue((now + ReactionDelay, () => GoingAround = true));

    private static double NominalVs(double recommendedFpm) => -Math.Abs(recommendedFpm);

    internal void Step(double dt, double now, ParGeometry geo)
    {
        while (_pending.Count > 0 && _pending.Peek().At <= now) _pending.Dequeue().Do();

        // Imperfect pilots: slow random walks on heading and rate.
        double sloppiness = 1 - _opt.Skill;
        _headingWander = Math.Clamp(_headingWander + Gauss() * 0.6 * sloppiness * Math.Sqrt(dt), -4, 4);
        _vsWander = Math.Clamp(_vsWander + Gauss() * 60 * sloppiness * Math.Sqrt(dt), -250, 250);

        if (Mode == PilotMode.SelfFlying && !GoingAround) SelfFly(geo);

        if (GoingAround)
        {
            CommandedHeadingRelDeg = 0;
            CommandedVsFpm = 1500;
            SpeedKt = Math.Min(SpeedKt + 2 * dt, 180);
        }
        else if (!Descending)
        {
            CommandedVsFpm = 0;
        }

        // Half-standard-rate turns for small corrections, standard rate otherwise.
        double hdgErr = Units.Norm180(CommandedHeadingRelDeg + _headingWander - HeadingRelDeg);
        double rate = Math.Abs(hdgErr) < 5 ? 1.5 : 3.0;
        HeadingRelDeg += Math.Clamp(hdgErr, -rate * dt, rate * dt);

        double vsTarget = CommandedVsFpm + (Descending ? _vsWander : 0);
        VerticalSpeedFpm += Math.Clamp(vsTarget - VerticalSpeedFpm, -400 * dt, 400 * dt);

        // Wind triangle in the approach frame. Wind "from" is a true direction.
        double windTo = Units.DegToRad(_opt.WindFromDeg + 180 - geo.Runway.TrueHeadingDeg);
        double h = Units.DegToRad(HeadingRelDeg);
        // Approach frame: inbound component (toward the runway) and right component.
        double vin = SpeedKt * Math.Cos(h) + _opt.WindKt * Math.Cos(windTo);
        double vright = SpeedKt * Math.Sin(h) + _opt.WindKt * Math.Sin(windTo);
        GroundSpeedKt = Math.Sqrt(vin * vin + vright * vright);
        TrackRelDeg = Units.RadToDeg(Math.Atan2(vright, vin));

        double fps = Units.FeetPerNm / 3600.0;
        RangeFt -= vin * fps * dt;
        CrossFt += vright * fps * dt;
        HeightFt += VerticalSpeedFpm / 60.0 * dt;

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
            SpeedKt = Math.Max(0, SpeedKt - 6 * dt);
            if (SpeedKt < 30) Finished = true;
        }
        if (HeightFt < 0 && !Landed) HeightFt = 0; // flew into the ground short: hold at surface
        if (RangeFt < -3 * Units.FeetPerNm || RangeFt > 30 * Units.FeetPerNm) Finished = true;
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
        CommandedVsFpm = nominal - Math.Clamp(gpErr * 3, -500, 500);

        // Missed approach at DH if badly off course.
        if (RangeFt < geo.DecisionHeightRangeFt && Math.Abs(CrossFt) > 300) GoingAround = true;
    }

    private double Gauss()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}

/// <summary>What the pilot does with a transmission, and what (if anything) they say back.</summary>
public sealed record PilotResponse(string Action, string? Reply);
