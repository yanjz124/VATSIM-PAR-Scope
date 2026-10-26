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
    public double SpeedKt { get; init; } = 140;
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
    private static readonly string[] Prefixes = ["NAVY", "MARINE", "REACH", "TOPCAT", "VIPER", "HAWK", "JAKE", "SPAR", "TALON", "BOLT"];
    private static readonly string[] Types = ["F18S", "C130", "T45", "C17", "F35", "KC46", "P8", "MV22", "T6", "B738"];

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
        string cs;
        do cs = Prefixes[_rng.Next(Prefixes.Length)] + _rng.Next(10, 99);
        while (_aircraft.Any(a => a.Callsign == cs));

        double rangeFt = o.RangeNm * Units.FeetPerNm;
        var ac = new SimAircraft(cs, o.TypeCode ?? Types[_rng.Next(Types.Length)], o, _rng)
        {
            RangeFt = rangeFt,
            CrossFt = o.CrossFt,
            HeightFt = geo.GlidepathHeightFt(rangeFt) + o.HeightOffsetFt,
            TrackRelDeg = -o.InterceptAngleDeg * Math.Sign(o.CrossFt == 0 ? 1 : o.CrossFt),
            SpeedKt = o.SpeedKt,
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
            SpeedKt = 120 + _rng.Next(0, 6) * 10,
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

    private double ReactionDelay => 1.0 + (1 - _opt.Skill) * 2.0 + _rng.NextDouble();

    /// <summary>
    /// A controller transmission. On PAR final the pilot does not read back; they fly the heading and
    /// adjust rate of descent in proportion to the glidepath call, after a human reaction delay.
    /// </summary>
    /// <returns>What the pilot will do, for the status line.</returns>
    public string Receive(Transmission t, double now, double courseMagDeg, double recommendedFpm)
    {
        double delay = ReactionDelay;
        var actions = new List<string>();

        if (t.MissedApproach)
        {
            _pending.Enqueue((now + delay, () => GoingAround = true));
            return "going missed";
        }
        if (t.HeadingDeg is double hdg)
        {
            double rel = Units.Norm180(hdg - courseMagDeg);
            _pending.Enqueue((now + delay, () => CommandedHeadingRelDeg = rel));
            actions.Add($"heading {hdg:000}");
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
        return actions.Count > 0 ? LastReadback : "no action";
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
