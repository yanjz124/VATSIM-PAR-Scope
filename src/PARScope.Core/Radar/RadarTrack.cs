using PARScope.Core.Par;

namespace PARScope.Core.Radar;

/// <summary>Why a track has no recent paints.</summary>
public enum CoastReason
{
    /// <summary>Painted recently: not coasting.</summary>
    None,
    /// <summary>Reporting and in coverage, but not painted (a weak or missed return).</summary>
    Missed,
    /// <summary>Still reporting, but outside the radar's range, sector or elevation limits.</summary>
    OutOfCoverage,
    /// <summary>This aircraft's position reports have stopped.</summary>
    SignalLost,
    /// <summary>The whole traffic feed has gone silent.</summary>
    DataLost,
}

/// <summary>
/// Track-while-scan state built only from radar paints (never from network truth), so data blocks,
/// deviations and trends carry the same noise and latency a real PAR controller sees.
/// Range comes from both scopes; height from elevation paints; lateral position from azimuth paints.
/// Each axis is an alpha-beta filter, which copes with the irregular dt between paints.
/// </summary>
public sealed class RadarTrack
{
    private readonly AlphaBeta _range = new(0.5, 0.15);
    private readonly AlphaBeta _cross = new(0.45, 0.12);
    private readonly AlphaBeta _height = new(0.45, 0.12);
    private readonly SlopeWindow _heightTrend = new(TrendWindowSec);
    private readonly SlopeWindow _crossTrend = new(TrendWindowSec);

    /// <summary>Seconds of paints used to judge trends ("and holding", "going above"), as a controller
    /// watches several returns before calling one.</summary>
    public const double TrendWindowSec = 8.0;

    public RadarTrack(string callsign) => Callsign = callsign;

    public string Callsign { get; }
    public double LastUpdate { get; private set; }
    /// <summary>Coasting, and why (set by the radar each frame).</summary>
    public CoastReason Coast { get; internal set; }
    public bool IsCoasting => Coast != CoastReason.None;

    /// <summary>
    /// Where the track is drawn on a scope: its last paint while being painted; while coasting, that paint moved
    /// on by the measured rates (at most 15 s), as a tracker coasts a lost target.
    /// </summary>
    public (double RangeFt, double OffsetFt) DisplayAt(ScopeKind scope, double now)
    {
        bool el = scope == ScopeKind.Elevation;
        double range = el ? RangeAtElevationFt : RangeAtAzimuthFt, offset = el ? HeightFt : CrossFt;
        if (!IsCoasting) return (range, offset);
        double dt = Math.Clamp(now - (el ? LastElPaint : LastAzPaint), 0, 15);
        return (range + RangeRateFps * dt, offset + (el ? HeightRateFps : CrossRateFps) * dt);
    }
    public double LastAzPaint { get; private set; } = double.NegativeInfinity;
    public double LastElPaint { get; private set; } = double.NegativeInfinity;
    public int AzPaints { get; private set; }
    public int ElPaints { get; private set; }

    public double RangeFt => _range.Value;
    public double CrossFt => _cross.Value;
    public double HeightFt => _height.Value;
    /// <summary>Closure rate, ft/s (negative while inbound).</summary>
    public double RangeRateFps => _range.Rate;
    /// <summary>Lateral rate from a least-squares fit over the trend window, ft/s.</summary>
    public double CrossRateFps => _crossTrend.Slope;
    /// <summary>Vertical rate from a least-squares fit over the trend window, ft/s.</summary>
    public double HeightRateFps => _heightTrend.Slope;

    public bool HasAzimuth => AzPaints > 0;
    public bool HasElevation => ElPaints > 0;
    /// <summary>Seen in both scopes, the condition for taking control on a PAR.</summary>
    public bool IsCorrelated => AzPaints >= 2 && ElPaints >= 2;

    /// <summary>Range when the last elevation / azimuth return was painted. Height is only measured by the
    /// elevation scan, so the glidepath deviation must use the range of that same paint: pairing it with a
    /// newer range (from an azimuth paint) reads a closing aircraft as high by closure × Δt × tan GPA.</summary>
    public double RangeAtElevationFt { get; private set; }
    public double RangeAtAzimuthFt { get; private set; }

    /// <summary>Position for glidepath and course deviation: height at the range it was measured at.</summary>
    public ApproachPoint Point => new(HasElevation ? RangeAtElevationFt : RangeFt, CrossFt, HeightFt);

    public double GroundSpeedKt => Math.Abs(RangeRateFps) * 3600 / Geo.Units.FeetPerNm;

    public void Update(Plot p)
    {
        _range.Update(p.Point.RangeFt, p.Time);
        if (p.Scope == ScopeKind.Azimuth)
        {
            _cross.Update(p.Point.CrossFt, p.Time);
            _crossTrend.Add(p.Time, p.Point.CrossFt);
            RangeAtAzimuthFt = _range.Value;
            LastAzPaint = p.Time;
            AzPaints++;
        }
        else
        {
            _height.Update(p.Point.HeightFt, p.Time);
            _heightTrend.Add(p.Time, p.Point.HeightFt);
            RangeAtElevationFt = _range.Value;
            LastElPaint = p.Time;
            ElPaints++;
        }
        LastUpdate = p.Time;
    }

    /// <summary>Slope of a straight-line least-squares fit to the samples in a sliding time window.</summary>
    private sealed class SlopeWindow(double windowSec)
    {
        private readonly Queue<(double T, double V)> _samples = new();

        public double Slope { get; private set; }

        public void Add(double t, double v)
        {
            _samples.Enqueue((t, v));
            while (_samples.Count > 0 && t - _samples.Peek().T > windowSec) _samples.Dequeue();
            if (_samples.Count < 3) { Slope = 0; return; }
            double mt = _samples.Average(s => s.T), mv = _samples.Average(s => s.V);
            double num = 0, den = 0;
            foreach (var (st, sv) in _samples) { num += (st - mt) * (sv - mv); den += (st - mt) * (st - mt); }
            Slope = den > 1e-6 ? num / den : 0;
        }
    }

    private sealed class AlphaBeta(double alpha, double beta)
    {
        private double _t = double.NaN;
        private bool _seeded;
        public double Value { get; private set; }
        public double Rate { get; private set; }

        public void Update(double z, double t)
        {
            if (double.IsNaN(_t)) { Value = z; _t = t; return; }
            // Seed the rate from the first two measurements instead of letting it grow from zero.
            if (!_seeded && t - _t > 1e-3 && t - _t <= 6) { Rate = (z - Value) / (t - _t); Value = z; _t = t; _seeded = true; return; }
            double dt = t - _t;
            if (dt <= 1e-3) { Value += alpha * (z - Value); return; }
            if (dt > 6) { Value = z; Rate = 0; _t = t; return; }
            double pred = Value + Rate * dt;
            double resid = z - pred;
            Value = pred + alpha * resid;
            Rate += beta * resid / dt;
            _t = t;
        }
    }
}
