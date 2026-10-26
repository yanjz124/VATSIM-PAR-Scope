using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Radar;
using PARScope.Core.Sim;
using PARScope.Core.Traffic;
using PARScope.Core.Weather;

namespace PARScope.Core.Tests;

public class RadarAndAdvisoryTests
{
    private static (ParGeometry Geo, TrackStore Store, TrainingTraffic Sim) Setup()
    {
        var geo = new ParGeometry(GeometryTests.Runway, GeometryTests.Site);
        return (geo, new TrackStore(), new TrainingTraffic { Geometry = geo });
    }

    [Theory]
    [InlineData("fpn63", 1.0)]
    [InlineData("fpn68", 0.5)]
    public void Each_scope_paints_a_target_once_per_scan(string presetId, double period)
    {
        var (geo, store, sim) = Setup();
        sim.Spawn(new SpawnOptions { RangeNm = 6, HeightOffsetFt = 0, Mode = PilotMode.SelfFlying, Skill = 1 });
        var preset = RadarPreset.ById(presetId) with { DetectionProbability = 1 };
        var radar = new RadarSimulator(preset, geo, seed: 1) { ClutterEnabled = false };

        for (double t = 0; t <= 20; t += 1 / 60.0)
        {
            foreach (var m in sim.Step(t)) store.Apply(m);
            radar.Advance(t, store.Snapshot(t));
        }

        int az = radar.Plots.Count(p => p.Scope == ScopeKind.Azimuth);
        int el = radar.Plots.Count(p => p.Scope == ScopeKind.Elevation);
        double expected = 20 / period;
        Assert.InRange(az, expected - 3, expected + 3);
        Assert.InRange(el, expected - 3, expected + 3);
    }

    [Fact]
    public void Paint_noise_matches_icao_budget()
    {
        var (geo, store, sim) = Setup();
        sim.Spawn(new SpawnOptions { RangeNm = 6, HeightOffsetFt = 0, Mode = PilotMode.SelfFlying, Skill = 1 });
        var radar = new RadarSimulator(RadarPreset.Icao with { NoiseFactor = 1, DetectionProbability = 1 }, geo, seed: 7) { ClutterEnabled = false, SimulateErrors = true };
        var errs = new List<double>();
        for (double t = 0; t <= 40; t += 1 / 30.0)
        {
            foreach (var m in sim.Step(t)) store.Apply(m);
            var tracks = store.Snapshot(t);
            int before = radar.Plots.Count;
            radar.Advance(t, tracks);
            foreach (var p in radar.Plots.Skip(before).Where(p => p.Scope == ScopeKind.Azimuth))
                errs.Add(p.Point.CrossFt - geo.FromEcef(tracks[0].PositionAt(t)).CrossFt);
        }
        double sigma = Math.Sqrt(errs.Average(e => e * e));
        // σ = 0.2 % of ~6.5 NM antenna range + 10 ft ≈ 90 ft; ICAO limit at 6 NM is ~220 ft.
        Assert.InRange(sigma, 50, 150);
    }

    [Fact]
    public void Mti_rejects_slow_targets()
    {
        var (geo, store, sim) = Setup();
        sim.Spawn(new SpawnOptions { RangeNm = 3, SpeedKt = 20, Mode = PilotMode.ControllerDirected });
        var radar = new RadarSimulator(RadarPreset.Fpn68, geo, seed: 3) { ClutterEnabled = false };
        for (double t = 0; t <= 5; t += 0.05)
        {
            foreach (var m in sim.Step(t)) store.Apply(m);
            radar.Advance(t, store.Snapshot(t));
        }
        Assert.Empty(radar.Plots);
    }

    private static readonly DeviationBands Uk = new() { Standard = DeviationStandard.UkRa3291 };

    [Theory]
    [InlineData(0, 0, "ON GLIDEPATH")]
    [InlineData(40, 0, "SLIGHTLY ABOVE GLIDEPATH AND HOLDING")]
    [InlineData(80, -5, "ABOVE GLIDEPATH AND COMING DOWN")]
    [InlineData(-150, 0, "WELL BELOW GLIDEPATH AND HOLDING")]
    [InlineData(-30, -6, "GOING BELOW GLIDEPATH")]
    public void Glidepath_phraseology(double aboveFt, double rate, string expected)
    {
        Assert.Equal(expected, AdvisoryEngine.GlidepathPhrase(aboveFt, rate, Uk));
    }

    [Theory]
    [InlineData(10, 0, "ON COURSE")]
    [InlineData(-200, 0, "SLIGHTLY LEFT OF COURSE AND HOLDING")]
    [InlineData(-200, -5, "GOING LEFT OF COURSE")]
    [InlineData(1500, -8, "RIGHT OF COURSE AND CORRECTING")]
    [InlineData(4000, 0, "WELL RIGHT OF COURSE AND HOLDING")]
    public void Course_phraseology_uses_2_and_5_degree_lines(double rightFt, double rate, string expected)
    {
        // At 5 NM the 2° line is ~1,060 ft and the 5° line ~2,660 ft from the centreline.
        Assert.Equal(expected, AdvisoryEngine.CoursePhrase(rightFt, rate, 5 * Units.FeetPerNm, Uk));
    }

    [Fact]
    public void Talkdown_of_simulated_aircraft_produces_heading_toward_centreline()
    {
        var (geo, store, sim) = Setup();
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 6, CrossFt = -800, HeightOffsetFt = 0, Mode = PilotMode.ControllerDirected, Skill = 1 });
        var radar = new RadarSimulator(RadarPreset.Fpn68, geo, seed: 5) { ClutterEnabled = false };
        for (double t = 0; t <= 6; t += 1 / 30.0)
        {
            foreach (var m in sim.Step(t)) store.Apply(m);
            radar.Advance(t, store.Snapshot(t));
        }
        var adv = AdvisoryEngine.Evaluate(radar.Tracks[ac.Callsign], geo, RadarPreset.Fpn68, new DeviationBands(), GeometryTests.Runway.MagneticCourseDeg);
        Assert.Contains("LEFT OF COURSE", adv.CoursePhrase);
        Assert.NotNull(adv.SuggestedHeadingDeg);
        Assert.True(Units.Norm180(adv.SuggestedHeadingDeg!.Value - GeometryTests.Runway.MagneticCourseDeg) > 0, "should turn right toward course");
        Assert.InRange(adv.RecommendedDescentFpm, 450, 900); // depends on the type's approach speed
    }

    [Fact]
    public void Metar_decoding()
    {
        var m = Metar.Parse("KNLC 251756Z 31012G20KT 1 1/2SM BR BKN008 OVC015 18/16 A2992 RMK AO2");
        Assert.Equal(310, m.WindDirDeg);
        Assert.Equal(12, m.WindSpeedKt);
        Assert.Equal(20, m.GustKt);
        Assert.Equal(1.5, m.VisibilitySm);
        Assert.Equal(800, m.CeilingFt);
        Assert.Equal(FlightCategory.Ifr, m.Category);
        Assert.Equal("WIND 310 AT 12 GUSTS 20, ALTIMETER 2992", m.Spoken);

        var q = Metar.Parse("EGQS 251750Z 24008KT 9999 SCT030 12/08 Q1013");
        Assert.Equal(1013, q.AltimeterHpa);
        Assert.Equal(FlightCategory.Vfr, q.Category);
    }
}

public class AdvisoryTrendTests
{
    [Fact]
    public void Level_aircraft_below_glidepath_is_coming_up_to_it()
    {
        var geo = new ParGeometry(GeometryTests.Runway, GeometryTests.Site);
        var store = new TrackStore();
        var sim = new TrainingTraffic { Geometry = geo };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 9, HeightOffsetFt = -400, Mode = PilotMode.ControllerDirected, Skill = 1, SpeedKt = 140, TypeCode = "C30J" });
        var radar = new RadarSimulator(RadarPreset.Fpn68 with { NoiseFactor = 0.25 }, geo, seed: 11) { ClutterEnabled = false, SimulateErrors = true };
        for (double t = 0; t <= 25; t += 1 / 30.0)
        {
            foreach (var m in sim.Step(t)) store.Apply(m);
            radar.Advance(t, store.Snapshot(t));
        }
        var tr = radar.Tracks[ac.Callsign];
        var adv = AdvisoryEngine.Evaluate(tr, geo, RadarPreset.Fpn68, new DeviationBands(), GeometryTests.Runway.MagneticCourseDeg);
        Assert.True(tr.RangeRateFps < -150, $"range rate {tr.RangeRateFps}");
        Assert.InRange(tr.HeightRateFps, -3, 3);
        Assert.Contains("BELOW GLIDEPATH AND COMING UP", adv.GlidepathPhrase);
    }
}

public class NavyStandardTests
{
    [Theory]
    // Target half-size 120 ft: on ≤ 15, slightly ≤ 60, plain ≤ 120, well beyond.
    [InlineData(10, "ON GLIDEPATH")]
    [InlineData(50, "SLIGHTLY ABOVE GLIDEPATH AND HOLDING")]
    [InlineData(-100, "BELOW GLIDEPATH AND HOLDING")]
    [InlineData(-130, "WELL BELOW GLIDEPATH AND HOLDING")]
    public void Glidepath_terms_follow_target_quarters(double aboveFt, string expected)
    {
        Assert.Equal(expected, AdvisoryEngine.GlidepathPhrase(aboveFt, 0, new DeviationBands(), targetHalfFt: 120));
    }

    [Fact]
    public void Target_size_grows_with_range()
    {
        var p = RadarPreset.Fpn63;
        Assert.True(p.ElevationTargetHalfFt(6 * Units.FeetPerNm) > 2 * p.ElevationTargetHalfFt(2 * Units.FeetPerNm));
    }

    [Fact]
    public void Lower_safe_limit_is_half_a_degree_below_glidepath_and_lateral_limit_200ft_per_mile()
    {
        var geo = new ParGeometry(GeometryTests.Runway, GeometryTests.Site with { GlidepathDeg = 3.0 });
        double r = 4 * Units.FeetPerNm;
        Assert.Equal(r * Math.Tan(Units.DegToRad(2.5)), geo.LowerSafeLimitHeightFt(r), 3);
        Assert.Equal(800, ParGeometry.LateralSafeLimitFt(r), 3);
        Assert.Equal(100, ParGeometry.LateralSafeLimitFt(0.5 * Units.FeetPerNm), 3);
    }
}

public class ErrorFreeDefaultTests
{
    [Fact]
    public void Default_paints_are_exact()
    {
        var geo = new ParGeometry(GeometryTests.Runway, GeometryTests.Site);
        var store = new TrackStore();
        var sim = new TrainingTraffic { Geometry = geo };
        sim.Spawn(new SpawnOptions { RangeNm = 5, HeightOffsetFt = 0, Mode = PilotMode.ControllerDirected });
        var radar = new RadarSimulator(RadarPreset.Fpn63, geo, seed: 1);
        for (double t = 0; t <= 5; t += 1 / 30.0)
        {
            foreach (var m in sim.Step(t)) store.Apply(m);
            var tracks = store.Snapshot(t);
            int before = radar.Plots.Count;
            radar.Advance(t, tracks);
            foreach (var p in radar.Plots.Skip(before))
            {
                var truth = geo.FromEcef(tracks[0].PositionAt(t));
                Assert.Equal(truth.CrossFt, p.Point.CrossFt, 6);
                Assert.Equal(truth.HeightFt, p.Point.HeightFt, 6);
            }
        }
        Assert.DoesNotContain(radar.Plots, p => p.IsClutter);
        Assert.NotEmpty(radar.Plots);
    }
}
