using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Traffic;

namespace PARScope.Core.Radar;

public enum ScopeKind { Elevation, Azimuth }

/// <summary>One radar return as painted on a scope.</summary>
public sealed record Plot(ScopeKind Scope, string? Callsign, double Time, ApproachPoint Point, RadarPolar Polar, bool IsClutter);

/// <summary>
/// Simulates the PAR's sensing: two beams sweep their sectors, a target is painted only when a beam
/// crosses it, and each return carries range/angle noise. This is what makes the scope behave like a
/// radar (discrete, slightly jittery paints) rather than a moving map.
///
/// Angle noise is one third of the ICAO Annex 10 error budget, scaled by the preset:
///   azimuth   σ = 0.2 % of antenna range + 10 ft
///   elevation σ = 0.13 % of antenna range + 7 ft, plus multipath lobing near the horizon
///   range     σ = 30 ft + 0.3 % of distance from touchdown. This is tighter than a third of ICAO's
///             100 ft + 3 %, closer to modern equipment; range error feeds the glidepath deviation (δr·tan GPA).
/// </summary>
public sealed class RadarSimulator
{
    public const double HistorySec = 45.0;
    /// <summary>MTI rejects anything slower than this (PAR-2020 processes 40–240 kt targets).</summary>
    public const double MtiMinSpeedKt = 40.0;

    private readonly Random _rng;
    private readonly List<Plot> _plots = [];
    private readonly Dictionary<string, RadarTrack> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ClutterCell> _clutter = [];
    private double _lastTime = double.NaN;

    public RadarSimulator(RadarPreset preset, ParGeometry geometry, int? seed = null)
    {
        Preset = preset;
        Geometry = geometry;
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
        BuildClutter();
    }

    public RadarPreset Preset { get; }
    public ParGeometry Geometry { get; }
    /// <summary>
    /// Adds measurement noise, multipath, ground clutter and missed paints. Off by default: targets plot at
    /// the aircraft's reported position, updated once per scan.
    /// </summary>
    public bool SimulateErrors { get; set; }
    public bool ClutterEnabled { get; set; } = true;
    public bool MtiEnabled { get; set; } = true;
    /// <summary>The traffic feed as a whole has gone silent (set by the owner each frame): tracks coast longer.</summary>
    public bool DataLost { get; set; }

    /// <summary>
    /// How long, since its last paint, a coasting track stays on the scope, by why it is coasting. Out of
    /// coverage goes soonest (the aircraft is somewhere else); a feed outage is held longest so a brief CRC
    /// reconnect does not wipe the picture.
    /// </summary>
    public static double CoastLimitSec(CoastReason reason) => reason switch
    {
        CoastReason.OutOfCoverage => 6,
        CoastReason.SignalLost => 12,
        CoastReason.DataLost => 30,
        CoastReason.CoarseData => 6,
        _ => 12,
    };

    /// <summary>No paint for about two scans: the track coasts.</summary>
    public double CoastAfterSec => 2 * Math.Max(Preset.AzScanSec, Preset.ElScanSec) + 0.25;

    /// <summary>Current beam angles, for drawing the scan.</summary>
    public double AzBeamDeg { get; private set; }
    public double ElBeamDeg { get; private set; }

    public IReadOnlyList<Plot> Plots => _plots;
    public IReadOnlyDictionary<string, RadarTrack> Tracks => _tracks;

    public void Advance(double now, IReadOnlyList<TargetTrack> targets)
    {
        if (double.IsNaN(_lastTime)) { _lastTime = now; UpdateBeams(now); return; }
        double prev = _lastTime;
        if (now <= prev) return;
        _lastTime = now;
        UpdateBeams(now);

        foreach (var t in targets)
        {
            if (!t.Initialized) continue;
            // A silent aircraft returns nothing: its track coasts rather than being painted from a guess.
            if (!t.IsReporting(now)) continue;
            // STARS-only data (Mode C in 100 ft steps) would draw a staircase on the elevation scope: not painted.
            if (t.CoarseAltitude && !t.IsSimulated) continue;
            var truth = Geometry.FromEcef(t.PositionAt(now));
            var polar = Geometry.ToPolar(truth);
            if (!InCoverage(polar, t)) continue;

            if (polar.ElevationDeg >= Preset.ElMinDeg && polar.ElevationDeg <= Preset.AzBeamElMaxDeg
                && Illuminated(prev, now, AzFraction(polar.AzimuthDeg), Preset.AzScanSec, 0.0))
                Paint(ScopeKind.Azimuth, t.Callsign, now, polar, truth);

            if (polar.ElevationDeg >= Preset.ElMinDeg && polar.ElevationDeg <= Preset.ElMaxDeg
                && Illuminated(prev, now, ElFraction(polar.ElevationDeg), Preset.ElScanSec, 0.5))
                Paint(ScopeKind.Elevation, t.Callsign, now, polar, truth);
        }

        if (SimulateErrors && ClutterEnabled) PaintClutter(prev, now);

        _plots.RemoveAll(p => now - p.Time > HistorySec);
        UpdateCoasting(now, targets);
    }

    /// <summary>Within the radar's range, MTI speed gate and azimuth sector (elevation is checked per beam).</summary>
    private bool InCoverage(RadarPolar polar, TargetTrack t) =>
        polar.RangeFt <= Preset.EffectiveMaxRangeNm(MtiEnabled) * Units.FeetPerNm
        && !(MtiEnabled && !t.Frozen && t.VelocityEcef.Length / Units.MpsPerKnot < MtiMinSpeedKt)
        && polar.AzimuthDeg >= -Preset.AzLeftDeg && polar.AzimuthDeg <= Preset.AzRightDeg
        && polar.ElevationDeg >= Preset.ElMinDeg && polar.ElevationDeg <= Preset.ElMaxDeg;

    /// <summary>
    /// Tracks without a paint for about two scans coast: shown extrapolated with a coast indicator, then dropped.
    /// The reason decides how long: the whole feed silent (data lost), this aircraft silent (signal lost),
    /// still reporting but outside the radar's coverage (out of coverage), or in coverage but not painted.
    /// </summary>
    private void UpdateCoasting(double now, IReadOnlyList<TargetTrack> targets)
    {
        var byCallsign = targets.ToDictionary(t => t.Callsign, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, tr) in _tracks.ToList())
        {
            double since = now - tr.LastUpdate;
            if (since <= CoastAfterSec) { tr.Coast = CoastReason.None; continue; }
            tr.Coast = DataLost ? CoastReason.DataLost
                : !byCallsign.TryGetValue(key, out var t) || !t.IsReporting(now) ? CoastReason.SignalLost
                : t.CoarseAltitude ? CoastReason.CoarseData
                : !InCoverage(Geometry.ToPolar(Geometry.FromEcef(t.PositionAt(now))), t) ? CoastReason.OutOfCoverage
                : CoastReason.Missed;
            if (since > CoastLimitSec(tr.Coast)) _tracks.Remove(key);
        }
    }

    // ---- Scan timing ---------------------------------------------------------------------------

    private double AzFraction(double azDeg) => (azDeg + Preset.AzLeftDeg) / (Preset.AzLeftDeg + Preset.AzRightDeg);
    private double ElFraction(double elDeg) => (elDeg - Preset.ElMinDeg) / (Preset.ElMaxDeg - Preset.ElMinDeg);

    private void UpdateBeams(double t)
    {
        AzBeamDeg = -Preset.AzLeftDeg + BeamFraction(t, Preset.AzScanSec, 0.0) * (Preset.AzLeftDeg + Preset.AzRightDeg);
        ElBeamDeg = Preset.ElMinDeg + BeamFraction(t, Preset.ElScanSec, 0.5) * (Preset.ElMaxDeg - Preset.ElMinDeg);
    }

    /// <summary>Beam position across the sector in [0, 1]. Mechanical antennas sweep back and forth;
    /// electronic beams restart at the sector edge every frame.</summary>
    private double BeamFraction(double t, double period, double phase)
    {
        double u = t / period + phase;
        if (Preset.Pattern == ScanPattern.Electronic) return u - Math.Floor(u);
        double m = u - 2 * Math.Floor(u / 2);
        return m <= 1 ? m : 2 - m;
    }

    /// <summary>True if the beam passed sector fraction <paramref name="f"/> between two frame times.
    /// Counted analytically so detection does not depend on frame rate.</summary>
    private bool Illuminated(double t0, double t1, double f, double period, double phase)
    {
        f = Math.Clamp(f, 0, 1);
        double u0 = t0 / period + phase, u1 = t1 / period + phase;
        if (Preset.Pattern == ScanPattern.Electronic)
            return Math.Floor(u1 - f) > Math.Floor(u0 - f);
        // Back-and-forth: the beam is at f when u ≡ f or u ≡ 2 − f (mod 2).
        return Math.Floor((u1 - f) / 2) > Math.Floor((u0 - f) / 2)
               || Math.Floor((u1 - (2 - f)) / 2) > Math.Floor((u0 - (2 - f)) / 2);
    }

    // ---- Measurement ---------------------------------------------------------------------------

    private void Paint(ScopeKind scope, string callsign, double now, RadarPolar truePolar, ApproachPoint truth)
    {
        if (!SimulateErrors)
        {
            AddPlot(new Plot(scope, callsign, now, truth, truePolar, false));
            return;
        }
        if (_rng.NextDouble() > Preset.DetectionProbability) return;

        double nf = Preset.NoiseFactor;
        double r = truePolar.RangeFt;
        double rangeSigma = (30 + 0.003 * Math.Max(0, truth.RangeFt)) * nf;
        double azSigmaDeg = Units.RadToDeg((0.002 * r + 10) * nf / Math.Max(r, 100));
        double elSigmaDeg = Units.RadToDeg((0.0013 * r + 7) * nf / Math.Max(r, 100));

        double el = truePolar.ElevationDeg;
        double multipathZone = 2.0 * Preset.ElBeamwidthDeg;
        if (el < multipathZone)
        {
            // Ground-reflection lobing: a bias that oscillates with range, strongest at the horizon.
            double strength = 1 - Math.Max(0, el) / multipathZone;
            el += 0.2 * Preset.ElBeamwidthDeg * strength * Math.Sin(r / 900.0 + StableHash(callsign) % 7);
            elSigmaDeg += 0.08 * Preset.ElBeamwidthDeg * strength;
        }

        // Each antenna measures its own angle accurately and the other one only coarsely (fan beam).
        var measured = scope == ScopeKind.Azimuth
            ? new RadarPolar(r + Gauss() * rangeSigma, truePolar.AzimuthDeg + Gauss() * azSigmaDeg, truePolar.ElevationDeg + Gauss() * elSigmaDeg * 3)
            : new RadarPolar(r + Gauss() * rangeSigma, truePolar.AzimuthDeg + Gauss() * azSigmaDeg * 3, el + Gauss() * elSigmaDeg);

        AddPlot(new Plot(scope, callsign, now, Geometry.FromPolar(measured), measured, false));
    }

    private void AddPlot(Plot plot)
    {
        _plots.Add(plot);
        if (!_tracks.TryGetValue(plot.Callsign!, out var track))
            _tracks[plot.Callsign!] = track = new RadarTrack(plot.Callsign!);
        track.Update(plot);
    }

    private double Gauss()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    // ---- Clutter -------------------------------------------------------------------------------

    private sealed record ClutterCell(double RangeFt, double CrossFt, double HeightFt, double Strength);

    /// <summary>Approach light system length by NASR/FAA type code (FAA Order 6850.2: ALSF/MALSR 2,400 ft; SSALR/MALS 1,400 ft).</summary>
    public static double ApproachLightLengthFt(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return 0;
        code = code.ToUpperInvariant().Replace("-", "");
        if (code.StartsWith("ALSF") || code.StartsWith("MALSR") || code.StartsWith("MALSF") || code == "CALVERT") return 2400;
        if (code.StartsWith("SSAL") || code.StartsWith("MALS") || code.StartsWith("SALS") || code.StartsWith("ODALS")) return 1400;
        return 0;
    }

    /// <summary>
    /// Fixed ground returns built from what is actually around a runway threshold: the approach light
    /// stations (every 200 ft along the centerline, from the published lighting type), the threshold
    /// light bar, and a few low terrain/structure returns. Real PAR video shows exactly this kind of
    /// clutter close to touchdown; MTI suppresses most of it because none of it moves.
    /// </summary>
    private void BuildClutter()
    {
        double thr = Geometry.ThresholdRangeFt;
        double lights = ApproachLightLengthFt(Geometry.Runway.ApproachLights);
        for (double d = 200; d <= lights; d += 200)
            _clutter.Add(new ClutterCell(thr + d, 0, 8 + d * 0.004, 0.9));
        foreach (double cross in new[] { -60.0, 0, 60 })
            _clutter.Add(new ClutterCell(thr, cross, 3, 0.8));

        // Deterministic per runway so the clutter map is the same every session.
        var rng = new Random(StableHash(Geometry.Runway.AirportId + "/" + Geometry.Runway.RunwayId));
        for (int i = 0; i < 8; i++)
        {
            double range = thr + 500 + rng.NextDouble() * 6000;
            double cross = (rng.NextDouble() - 0.5) * 3000;
            _clutter.Add(new ClutterCell(range, cross, rng.NextDouble() * 30, 0.4 + 0.4 * rng.NextDouble()));
        }
    }

    /// <summary>FNV-1a; string.GetHashCode is randomised per process.</summary>
    private static int StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char ch in s) { h ^= ch; h *= 16777619; }
        return (int)(h & 0x7fffffff);
    }

    private void PaintClutter(double t0, double t1)
    {
        // MTI cancels fixed returns; a little residue leaks through, as on real systems.
        double keep = MtiEnabled ? 0.06 : 0.9;
        foreach (var c in _clutter)
        {
            var pt = new ApproachPoint(c.RangeFt, c.CrossFt, c.HeightFt);
            var polar = Geometry.ToPolar(pt);
            if (polar.AzimuthDeg < -Preset.AzLeftDeg || polar.AzimuthDeg > Preset.AzRightDeg) continue;
            if (Illuminated(t0, t1, AzFraction(polar.AzimuthDeg), Preset.AzScanSec, 0.0) && _rng.NextDouble() < keep * c.Strength)
                _plots.Add(new Plot(ScopeKind.Azimuth, null, t1, pt, polar, true));
            if (Illuminated(t0, t1, ElFraction(polar.ElevationDeg), Preset.ElScanSec, 0.5) && _rng.NextDouble() < keep * c.Strength)
                _plots.Add(new Plot(ScopeKind.Elevation, null, t1, pt, polar, true));
        }
    }
}
