using PARScope.Core.Geo;

namespace PARScope.Core.Traffic;

/// <summary>
/// Continuous estimate of one aircraft's true position, built from irregular network reports.
///
/// Reports arrive at up to 5 Hz with velocities (VATSIM Velocity) or every ~5 s without. Between
/// reports the position is dead-reckoned from the velocity. When a report disagrees with the
/// prediction, the difference is blended out exponentially rather than snapped, the same idea xPilot
/// uses for its error velocities. Everything is kept in ECEF so it is independent of the selected runway.
/// </summary>
public sealed class TargetTrack
{
    /// <summary>Longest a position is extrapolated past the last report before it is held, unless the
    /// source's own update interval is longer (the 15 s data feed).</summary>
    public const double MaxExtrapolationSec = 12.0;
    private const double SnapDistanceM = 1500.0;
    private const double SnapGapSec = 20.0;
    private const double FastTauSec = 0.35;
    private const double SlowTauSec = 1.5;
    private const double FastFreshSec = 1.5;

    private Vec3 _measEcef;
    private double _measTime;
    private Vec3 _velEcef;
    private Vec3 _offsetEcef;
    private double _tau = SlowTauSec;
    private readonly RateMeter _fastRate = new();
    private readonly RateMeter _slowRate = new();
    private double _slowInterval;

    public TargetTrack(string callsign) => Callsign = callsign;

    public string Callsign { get; }
    public string? TypeCode { get; private set; }
    public double? PressureAltFt { get; private set; }
    public double? HeadingDeg { get; private set; }
    public double? GroundSpeedKt { get; private set; }
    /// <summary>Transponder code (four octal digits as a number, e.g. 1200), when the source sends it.</summary>
    public int? Beacon { get; private set; }
    public bool HasVelocity { get; private set; }
    public double LastReportTime { get; private set; } = double.NegativeInfinity;
    public double LastFastTime { get; private set; } = double.NegativeInfinity;
    public bool IsSimulated { get; private set; }
    /// <summary>Training aircraft held still by a training pause; MTI keeps it on the scope.</summary>
    public bool Frozen { get; private set; }
    /// <summary>Last report came from the 15 s public data feed.</summary>
    public bool IsFeed { get; private set; }
    /// <summary>Altitude only in 100 ft Mode C steps (CRC STARS data): the PAR does not paint the aircraft.</summary>
    public bool CoarseAltitude { get; private set; }
    /// <summary>Where the last report came from.</summary>
    public ReportSource LastSource { get; private set; }
    public bool Initialized { get; private set; }

    public bool IsFastActive(double now) => now - LastFastTime < FastFreshSec;
    public double FastRateHz(double now) => _fastRate.Rate(now);
    public double SlowRateHz(double now) => _slowRate.Rate(now);
    public double Age(double now) => now - LastReportTime;

    /// <summary>
    /// Typical seconds between this aircraft's reports: 0.2 s on Velocity fast data, otherwise learned from the
    /// gaps (about 1 s from the CRC feed, 5 s from vPilot's slow updates, 15 s from the data feed).
    /// </summary>
    public double ExpectedIntervalSec(double now) =>
        IsSimulated || IsFastActive(now) ? 0.2 : _slowInterval > 0 ? _slowInterval : 5.0;

    /// <summary>
    /// Still being heard: no more than about two and a half expected intervals since the last report. A silent
    /// aircraft (signal lost) is not painted by the radar, which then coasts its track instead of drawing
    /// returns from an extrapolated position.
    /// </summary>
    public bool IsReporting(double now) => IsSimulated || Age(now) <= Math.Max(2.0, 2.5 * ExpectedIntervalSec(now));

    public void Apply(PositionReport r)
    {
        if (r.TypeCode is { Length: > 0 }) TypeCode = r.TypeCode;
        if (r.PressureAltFt.HasValue) PressureAltFt = r.PressureAltFt;
        if (r.HeadingDeg.HasValue) HeadingDeg = r.HeadingDeg;
        if (r.GroundSpeedKt.HasValue) GroundSpeedKt = r.GroundSpeedKt;
        if (r.Beacon.HasValue) Beacon = r.Beacon;
        IsSimulated |= r.Source == ReportSource.Simulated;
        Frozen = r.Frozen;

        if (r.Source == ReportSource.Fast) { _fastRate.Mark(r.Time); LastFastTime = r.Time; }
        else
        {
            if (LastReportTime > double.NegativeInfinity && r.Time > LastReportTime) _slowInterval = Math.Max(_slowInterval * 0.7, r.Time - LastReportTime);
            _slowRate.Mark(r.Time);
        }
        IsFeed = r.Source == ReportSource.Feed;
        LastSource = r.Source;

        var pos = Wgs84.ToEcef(r.Position);

        if (!Initialized)
        {
            CoarseAltitude = r.CoarseAltitude;
            Initialized = true;
            _measEcef = pos;
            _measTime = r.Time;
            _offsetEcef = Vec3.Zero;
            if (r.VelocityEnu is { } v0) SetVelocity(r, v0);
            else if (r.GroundSpeedKt is { } gs && r.HeadingDeg is { } hdg) SeedVelocityFromSpeed(r, gs, hdg);
            LastReportTime = r.Time;
            return;
        }

        double dt = r.Time - _measTime;
        if (dt < -0.25) return; // late, out-of-order datagram

        // While Velocity data is flowing, slow reports only repeat the latest fast state, older than
        // its timestamp suggests, so they would drag the track backwards. Keep their metadata only.
        if (r.VelocityEnu is null && IsFastActive(r.Time) && !IsSimulated)
        {
            LastReportTime = Math.Max(LastReportTime, r.Time);
            return;
        }

        CoarseAltitude = r.CoarseAltitude;
        var predicted = PositionAt(r.Time);

        if (r.VelocityEnu is { } v)
        {
            SetVelocity(r, v);
        }
        else if (dt > 0.2)
        {
            var measured = (pos - _measEcef) / dt;
            _velEcef = HasVelocity ? _velEcef + (measured - _velEcef) * 0.6 : measured;
            HasVelocity = true;
        }

        var error = predicted - pos;
        _offsetEcef = error.Length > SnapDistanceM || dt > SnapGapSec ? Vec3.Zero : error;
        _tau = r.VelocityEnu is null ? SlowTauSec : FastTauSec;
        _measEcef = pos;
        _measTime = r.Time;
        LastReportTime = Math.Max(LastReportTime, r.Time);
    }

    /// <summary>Smoothed, dead-reckoned ECEF position at <paramref name="time"/>.</summary>
    public Vec3 PositionAt(double time)
    {
        double since = time - _measTime;
        double dr = Math.Clamp(since, 0, Math.Max(MaxExtrapolationSec, Math.Min(_slowInterval * 1.5, 30)));
        var p = _measEcef + _velEcef * dr;
        if (since > 0) p += _offsetEcef * Math.Exp(-since / _tau);
        else p += _offsetEcef;
        return p;
    }

    public Vec3 VelocityEcef => _velEcef;

    public GeoPoint GeoAt(double time) => Wgs84.FromEcef(PositionAt(time));

    private void SetVelocity(PositionReport r, Vec3 enu)
    {
        _velEcef = Wgs84.EnuToEcefVector(r.Position.LatDeg, r.Position.LonDeg, enu);
        HasVelocity = true;
    }

    private void SeedVelocityFromSpeed(PositionReport r, double gsKt, double headingDeg)
    {
        if (gsKt < 30) return; // taxiing or parked: heading says nothing about track
        // Feed headings are only a rough track proxy (wind, magnetic vs true); later reports refine it.
        double h = Units.DegToRad(headingDeg), s = gsKt * Units.MpsPerKnot;
        SetVelocity(r, new Vec3(s * Math.Sin(h), s * Math.Cos(h), 0));
    }

    /// <summary>Exponentially weighted update-rate estimate.</summary>
    private sealed class RateMeter
    {
        private double _last = double.NegativeInfinity;
        private double _interval = double.PositiveInfinity;

        public void Mark(double t)
        {
            double dt = t - _last;
            _last = t;
            if (dt <= 0 || double.IsInfinity(dt)) return;
            _interval = double.IsInfinity(_interval) ? dt : _interval + (dt - _interval) * 0.2;
        }

        public double Rate(double now)
        {
            if (double.IsInfinity(_interval) || now - _last > Math.Max(3 * _interval, 2.0)) return 0;
            return 1.0 / _interval;
        }
    }
}
