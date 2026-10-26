using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Radar;
using PARScope.Core.Traffic;

namespace PARScope.Core.Tests;

/// <summary>Track coasting for the three ways data stops: signal lost, data lost, out of coverage.</summary>
public class CoastingTests
{
    private const double Nm = Units.FeetPerNm;

    private sealed class Rig
    {
        public readonly ParGeometry Geo = new(GeometryTests.Runway, GeometryTests.Site);
        public readonly TrackStore Store = new();
        public readonly RadarSimulator Radar;
        public double T;

        public Rig() => Radar = new RadarSimulator(RadarPreset.Fpn68 with { DetectionProbability = 1 }, Geo, seed: 3) { ClutterEnabled = false };

        /// <summary>One CRC-style report (1 Hz, no velocity) at an approach-frame position.</summary>
        public void Report(double rangeFt, double crossFt)
        {
            var p = new ApproachPoint(rangeFt, crossFt, Geo.GlidepathHeightFt(rangeFt));
            Store.Apply(new PositionReport("RCH481", T, ReportSource.Crc, Wgs84.FromEcef(Geo.ToEcef(p)), GroundSpeedKt: 140));
        }

        /// <summary>Runs <paramref name="seconds"/>; <paramref name="report"/> decides the position reported each second (null = silent).</summary>
        public void Run(double seconds, Func<double, (double Range, double Cross)?> report)
        {
            double end = T + seconds;
            for (; T <= end; T += 0.05)
            {
                if (Math.Abs(T - Math.Round(T)) < 0.025 && report(T) is { } pos) Report(pos.Range, pos.Cross);
                Radar.Advance(T, Store.Snapshot(T));
            }
        }

        public RadarTrack? Track => Radar.Tracks.TryGetValue("RCH481", out var t) ? t : null;
    }

    // Inbound on final at 140 kt from 6 NM.
    private static (double, double)? OnFinal(double t) => (6 * Nm - 140 * Nm / 3600 * t, 0);

    [Fact]
    public void Signal_lost_coasts_without_new_paints_then_drops()
    {
        var rig = new Rig();
        rig.Run(10, OnFinal);
        var tr = Assert.IsType<RadarTrack>(rig.Track);
        Assert.False(tr.IsCoasting);

        rig.Run(3, _ => null); // reports stop; past 2.5 intervals the aircraft counts as silent
        int plots = rig.Radar.Plots.Count;
        rig.Run(2, _ => null);
        Assert.Equal(CoastReason.SignalLost, rig.Track!.Coast);
        Assert.Equal(plots, rig.Radar.Plots.Count); // no phantom returns from an extrapolated position
        // The coasting track keeps moving inbound on its measured rate.
        double scan = RadarPreset.Fpn68.ElScanSec;
        var (range, _) = rig.Track.DisplayAt(ScopeKind.Elevation, rig.T, scan);
        Assert.True(range < rig.Track.RangeAtElevationFt - 100);
        // It steps once per scan rather than gliding: unchanged within a scan, moved by the next.
        double t0 = rig.Track.LastElPaint + 3 * scan + 0.01;
        Assert.Equal(rig.Track.DisplayAt(ScopeKind.Elevation, t0, scan).RangeFt, rig.Track.DisplayAt(ScopeKind.Elevation, t0 + scan * 0.9, scan).RangeFt);
        Assert.NotEqual(rig.Track.DisplayAt(ScopeKind.Elevation, t0, scan).RangeFt, rig.Track.DisplayAt(ScopeKind.Elevation, t0 + scan, scan).RangeFt);

        rig.Run(10, _ => null);
        Assert.Null(rig.Track); // dropped after the signal-lost coast limit
    }

    [Fact]
    public void Data_lost_coasts_longest_and_recovers_when_data_returns()
    {
        var rig = new Rig();
        rig.Run(10, OnFinal);
        rig.Radar.DataLost = true;
        rig.Run(20, _ => null);
        Assert.Equal(CoastReason.DataLost, rig.Track!.Coast); // held well past the signal-lost limit

        rig.Radar.DataLost = false;
        rig.Run(5, OnFinal);
        Assert.False(rig.Track!.IsCoasting);
    }

    [Fact]
    public void Out_of_coverage_drops_soonest()
    {
        var rig = new Rig();
        rig.Run(10, OnFinal);
        // Still reporting, but well outside the azimuth sector.
        rig.Run(4, t => (5 * Nm, 3 * Nm));
        Assert.Equal(CoastReason.OutOfCoverage, rig.Track!.Coast);
        rig.Run(4, t => (5 * Nm, 3 * Nm));
        Assert.Null(rig.Track);
    }

    [Fact]
    public void Stars_only_aircraft_are_not_painted_on_the_par()
    {
        var rig = new Rig();
        for (; rig.T <= 10; rig.T += 0.05)
        {
            if (Math.Abs(rig.T - Math.Round(rig.T)) < 0.025)
            {
                double r = 6 * Nm - 140 * Nm / 3600 * rig.T;
                var p = new ApproachPoint(r, 0, Math.Round(rig.Geo.GlidepathHeightFt(r) / 100) * 100);
                rig.Store.Apply(new PositionReport("RCH481", rig.T, ReportSource.Crc, Wgs84.FromEcef(rig.Geo.ToEcef(p)), CoarseAltitude: true));
            }
            rig.Radar.Advance(rig.T, rig.Store.Snapshot(rig.T));
        }
        Assert.Null(rig.Track);
        Assert.Empty(rig.Radar.Plots.Where(p => p.Callsign == "RCH481"));
    }

    [Fact]
    public void Expected_interval_and_reporting_follow_the_source()
    {
        var t = new TargetTrack("X");
        var pos = new GeoPoint(38.9, -77.0, 1000);
        for (int i = 0; i <= 5; i++) t.Apply(new PositionReport("X", i, ReportSource.Crc, pos));
        Assert.InRange(t.ExpectedIntervalSec(5), 0.9, 1.1);
        Assert.True(t.IsReporting(6.5));
        Assert.False(t.IsReporting(8));
    }
}
