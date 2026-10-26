using PARScope.Core.Geo;
using PARScope.Core.Traffic;

namespace PARScope.Core.Tests;

public class TrafficTests
{
    private static readonly GeoPoint Start = new(36.0, -120.0, 600);

    private static PositionReport Fast(double t, GeoPoint p, Vec3 v) => new("TEST1", t, ReportSource.Fast, p, v);

    [Fact]
    public void Dead_reckons_between_fast_updates()
    {
        var track = new TargetTrack("TEST1");
        var v = new Vec3(0, 70, -3); // 70 m/s north, descending
        track.Apply(Fast(0, Start, v));
        var predicted = Wgs84.FromEcef(track.PositionAt(1.0));
        var expected = new LocalFrame(Start).ToGeo(new Vec3(0, 70, -3));
        Assert.Equal(expected.LatDeg, predicted.LatDeg, 7);
        Assert.Equal(expected.AltM, predicted.AltM, 1);
    }

    [Fact]
    public void Corrections_blend_instead_of_jumping()
    {
        var track = new TargetTrack("TEST1");
        var frame = new LocalFrame(Start);
        var v = new Vec3(0, 70, 0);
        track.Apply(Fast(0, Start, v));
        // Next report is 10 m east of where dead reckoning expects it.
        var actual = frame.ToGeo(new Vec3(10, 14, 0));
        var before = frame.EcefToEnu(track.PositionAt(0.2));
        track.Apply(Fast(0.2, actual, v));
        var after = frame.EcefToEnu(track.PositionAt(0.2));
        Assert.Equal(before.X, after.X, 3); // continuous at the report instant
        var later = frame.EcefToEnu(track.PositionAt(2.0));
        Assert.Equal(10, later.X, 0); // converged onto the reported path
    }

    [Fact]
    public void Slow_reports_estimate_velocity_from_position_changes()
    {
        var track = new TargetTrack("TEST1");
        var frame = new LocalFrame(Start);
        track.Apply(new PositionReport("TEST1", 0, ReportSource.Slow, Start));
        track.Apply(new PositionReport("TEST1", 5, ReportSource.Slow, frame.ToGeo(new Vec3(0, 350, 0))));
        var mid = frame.EcefToEnu(track.PositionAt(7.5));
        Assert.InRange(mid.Y, 400, 560); // roughly 350 m + 2.5 s × ~42–70 m/s
    }

    [Fact]
    public void Extrapolation_is_capped()
    {
        var track = new TargetTrack("TEST1");
        track.Apply(Fast(0, Start, new Vec3(0, 100, 0)));
        var a = track.PositionAt(TargetTrack.MaxExtrapolationSec);
        var b = track.PositionAt(TargetTrack.MaxExtrapolationSec + 30);
        Assert.Equal(0, (a - b).Length, 6);
    }

    [Fact]
    public void Parses_v2_fast_and_v1_legacy_lines()
    {
        var parser = new ProtocolParser();
        var fast = parser.ParseLine("{\"v\":2,\"type\":\"pos\",\"t\":1000,\"src\":\"fast\",\"cs\":\"AAL1\",\"lat\":36.1,\"lon\":-120.2,\"alt\":3000,\"ve\":1.5,\"vu\":-2,\"vn\":60}", 50.0);
        var r = Assert.IsType<PositionReport>(fast);
        Assert.Equal(ReportSource.Fast, r.Source);
        Assert.Equal(new Vec3(1.5, 60, -2), r.VelocityEnu);
        Assert.Equal(3000, r.Position.AltFt, 6);

        var legacy = parser.ParseLine("{\"type\":\"update\",\"t\":1,\"callsign\":\"DAL2\",\"lat\":1,\"lon\":2,\"alt_ft\":500,\"speed_kts\":140}", 1.0);
        var l = Assert.IsType<PositionReport>(legacy);
        Assert.Equal(ReportSource.Slow, l.Source);
        Assert.Equal(140, l.GroundSpeedKt);

        Assert.IsType<AllTargetsRemoved>(parser.ParseLine("{\"v\":2,\"type\":\"net\",\"t\":5,\"state\":\"disconnected\"}", 2));
        Assert.Null(parser.ParseLine("{\"v\":2,\"type\":\"pos\",\"t\":5}", 2)); // missing fields
        Assert.Empty(parser.ParseDatagram("not json\n{broken", 0));
    }

    [Fact]
    public void Clock_sync_preserves_sender_spacing_within_a_batch()
    {
        var sync = new ClockSync();
        double a = sync.ToLocal(10_000, 100.0);
        double b = sync.ToLocal(10_200, 100.2);
        // Two lines batched into one datagram arrive together, after the later one was sent.
        double c = sync.ToLocal(10_400, 100.65);
        double d = sync.ToLocal(10_600, 100.65);
        Assert.Equal(0.2, b - a, 3);
        Assert.Equal(0.2, d - c, 2);
    }
}
