using PARScope.Core.Geo;
using PARScope.Core.Traffic;

namespace PARScope.Core.Radar;

/// <summary>A surveillance paint: position in the ASR's local frame (metres east/north) plus Mode C altitude.</summary>
public sealed record AsrPlot(string Callsign, double Time, double EastM, double NorthM, double AltFt);

/// <summary>
/// Airport surveillance radar for the plan view: a 4.8 s rotating sweep (ASR-9 class) that paints each
/// target as the antenna passes its bearing, with ASR-grade noise (σ ≈ 0.08° azimuth, 60 m range).
/// GCA facilities pair the PAR with an ASR for vectoring onto final.
/// </summary>
public sealed class AsrSimulator
{
    public const double SweepSec = 4.8;
    public const double HistorySec = 60;
    public const double MinSpeedKt = 40;
    private readonly LocalFrame _frame;
    private readonly Random _rng = new();
    private readonly List<AsrPlot> _plots = [];
    private double _last = double.NaN;

    public AsrSimulator(GeoPoint site, double rangeNm = 60)
    {
        _frame = new LocalFrame(site);
        RangeNm = rangeNm;
    }

    public LocalFrame Frame => _frame;
    public double RangeNm { get; }
    /// <summary>Antenna bearing, degrees true.</summary>
    public double SweepDeg { get; private set; }
    public IReadOnlyList<AsrPlot> Plots => _plots;

    public void Advance(double now, IReadOnlyList<TargetTrack> targets)
    {
        SweepDeg = 360.0 * Frac(now / SweepSec);
        if (double.IsNaN(_last)) { _last = now; return; }
        double prev = _last;
        if (now <= prev) return;
        _last = now;

        foreach (var t in targets)
        {
            if (!t.Initialized) continue;
            // Parked and taxiing aircraft: MTI cancels the primary return and the transponder is in
            // standby, so a real ASR does not show them.
            if (Math.Max(t.VelocityEcef.Length / Units.MpsPerKnot, t.GroundSpeedKt ?? 0) < MinSpeedKt) continue;
            var ecef = t.PositionAt(now);
            var enu = _frame.EcefToEnu(ecef);
            double range = enu.HorizontalLength;
            if (range > RangeNm * Units.MetersPerNm) continue;
            double brg = Units.Norm360(Units.RadToDeg(Math.Atan2(enu.X, enu.Y)));
            double f = brg / 360.0;
            if (Math.Floor(now / SweepSec - f) <= Math.Floor(prev / SweepSec - f)) continue;

            double r = range + Gauss() * 60;
            double b = Units.DegToRad(brg + Gauss() * 0.08);
            // Data blocks show altitude MSL (Mode C corrected for the local altimeter ≈ true altitude).
            _plots.Add(new AsrPlot(t.Callsign, now, r * Math.Sin(b), r * Math.Cos(b), Wgs84.FromEcef(ecef).AltFt));
        }
        _plots.RemoveAll(p => now - p.Time > HistorySec);
    }

    private static double Frac(double x) => x - Math.Floor(x);

    private double Gauss()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
