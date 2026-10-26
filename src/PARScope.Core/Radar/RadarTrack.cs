using PARScope.Core.Par;

namespace PARScope.Core.Radar;

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

    public ApproachPoint Point => new(RangeFt, CrossFt, HeightFt);

    public double GroundSpeedKt => Math.Abs(RangeRateFps) * 3600 / Geo.Units.FeetPerNm;

    public void Update(Plot p)
    {
        _range.Update(p.Point.RangeFt, p.Time);
        if (p.Scope == ScopeKind.Azimuth)
        {
            _cross.Update(p.Point.CrossFt, p.Time);
            _crossTrend.Add(p.Time, p.Point.CrossFt);
            LastAzPaint = p.Time;
            AzPaints++;
        }
        else
        {
            _height.Update(p.Point.HeightFt, p.Time);
            _heightTrend.Add(p.Time, p.Point.HeightFt);
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
        public double Value { get; private set; }
        public double Rate { get; private set; }

        public void Update(double z, double t)
        {
            if (double.IsNaN(_t)) { Value = z; _t = t; return; }
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
