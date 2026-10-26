using PARScope.Core.Geo;
using PARScope.Core.Par;

namespace PARScope.Core.Tests;

public class GeometryTests
{
    // NAS Lemoore (NLC) runway 32L-ish geometry, rounded; any mid-latitude runway works.
    internal static readonly RunwayEnd Runway = new("NLC", "KNLC", "32L", 36.3200, -119.9400, 228, 315.0, TchFt: 55, MagVarEastDeg: 13);
    internal static readonly ParSiteConfig Site = new() { TchFt = 55 };

    [Fact]
    public void Ecef_round_trip_is_sub_millimetre()
    {
        var p = new GeoPoint(36.32, -119.94, 1234.5);
        var back = Wgs84.FromEcef(Wgs84.ToEcef(p));
        Assert.Equal(p.LatDeg, back.LatDeg, 9);
        Assert.Equal(p.LonDeg, back.LonDeg, 9);
        Assert.Equal(p.AltM, back.AltM, 3);
    }

    [Fact]
    public void Glidepath_point_has_zero_deviation_at_all_ranges()
    {
        var geo = new ParGeometry(Runway, Site);
        foreach (double nm in new[] { 0.5, 2, 5, 10, 18 })
        {
            double r = nm * Units.FeetPerNm;
            var dev = geo.DeviationOf(new ApproachPoint(r, 0, geo.GlidepathHeightFt(r)));
            Assert.Equal(0, dev.AboveGlidepathFt, 6);
            Assert.Equal(0, dev.RightOfCourseFt, 6);
        }
    }

    [Fact]
    public void Approach_coordinates_round_trip_through_geodetic()
    {
        var geo = new ParGeometry(Runway, Site);
        var p = new ApproachPoint(8 * Units.FeetPerNm, -350, 2600);
        var g = geo.Frame.ToGeo(geo.ToEnu(p));
        var back = geo.FromGeo(g);
        Assert.Equal(p.RangeFt, back.RangeFt, 2);
        Assert.Equal(p.CrossFt, back.CrossFt, 2);
        Assert.Equal(p.HeightFt, back.HeightFt, 2);
    }

    [Fact]
    public void Constant_msl_altitude_reads_lower_at_range_because_of_curvature()
    {
        // An aircraft holding 2,000 ft MSL 10 NM out sits ~66 ft below the threshold's tangent plane.
        var geo = new ParGeometry(Runway, Site);
        var thr = geo.Frame.Origin;
        var far = Wgs84.Offset(thr, Runway.TrueHeadingDeg + 180, 10 * Units.MetersPerNm);
        var ac = geo.FromGeo(GeoPoint.FromFeet(far.LatDeg, far.LonDeg, 2000));
        double drop = 2000 - Runway.ThresholdElevFt - ac.HeightFt;
        Assert.InRange(drop, 55, 95);
        Assert.InRange(ac.RangeFt, 10 * Units.FeetPerNm, 10 * Units.FeetPerNm + 2000);
    }

    [Fact]
    public void Right_of_course_is_pilots_right()
    {
        // Runway heading north: pilot's right is east.
        var north = Runway with { TrueHeadingDeg = 0 };
        var geo = new ParGeometry(north, Site);
        var thr = geo.Frame.Origin;
        var p = Wgs84.Offset(Wgs84.Offset(thr, 180, 5000), 90, 100);
        Assert.Equal(100 * Units.FeetPerMeter, geo.FromGeo(p).CrossFt, 0);
    }

    [Fact]
    public void Polar_round_trip_and_antenna_offset()
    {
        var geo = new ParGeometry(Runway, Site);
        var p = new ApproachPoint(6 * Units.FeetPerNm, 120, 1900);
        var polar = geo.ToPolar(p);
        var back = geo.FromPolar(polar);
        Assert.Equal(p.RangeFt, back.RangeFt, 3);
        Assert.Equal(p.CrossFt, back.CrossFt, 3);
        Assert.Equal(p.HeightFt, back.HeightFt, 3);

        // Antenna sits 400 ft right of centreline, so a centreline target appears left of boresight.
        var onCl = geo.ToPolar(new ApproachPoint(3 * Units.FeetPerNm, 0, 1000));
        Assert.True(onCl.AzimuthDeg < 0);
    }

    [Fact]
    public void Terps_ocs_starts_at_200ft_and_slopes_34_to_1_for_3_degrees()
    {
        var geo = new ParGeometry(Runway, Site with { GlidepathDeg = 3.0, TchFt = 50 });
        Assert.Equal(0, geo.ObstacleClearanceHeightFt(geo.ThresholdRangeFt + 200), 6);
        double h = geo.ObstacleClearanceHeightFt(geo.ThresholdRangeFt + 200 + 3400);
        Assert.Equal(100, h, 0);
        // The OCS is always below the glidepath.
        for (double nm = 0.5; nm < 20; nm += 0.5)
            Assert.True(geo.ObstacleClearanceHeightFt(nm * Units.FeetPerNm) < geo.GlidepathHeightFt(nm * Units.FeetPerNm));
    }

    [Fact]
    public void Gpi_and_decision_height_range()
    {
        var geo = new ParGeometry(Runway, Site with { TchFt = 50, GlidepathDeg = 3, DecisionHeightFt = 200 });
        Assert.Equal(954, geo.GpiFromThresholdFt, 0);
        Assert.Equal(3816, geo.DecisionHeightRangeFt, 0);
    }
}
