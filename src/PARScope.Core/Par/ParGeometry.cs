using PARScope.Core.Geo;

namespace PARScope.Core.Par;

/// <summary>A point in approach coordinates, in feet, centred on the glidepath intercept (touchdown) point.</summary>
/// <param name="RangeFt">Distance from touchdown along the final approach course; positive = out on final.</param>
/// <param name="CrossFt">Distance from the extended centreline; positive = right of course as the pilot sees it.</param>
/// <param name="HeightFt">Height above threshold elevation.</param>
public readonly record struct ApproachPoint(double RangeFt, double CrossFt, double HeightFt)
{
    public double RangeNm => RangeFt / Units.FeetPerNm;
}

/// <summary>Antenna-referenced polar measurement, the quantities a PAR actually measures.</summary>
/// <param name="AzimuthDeg">Positive = toward the pilot's right of the final course.</param>
public readonly record struct RadarPolar(double RangeFt, double AzimuthDeg, double ElevationDeg);

/// <summary>
/// Geometry of one PAR installation on one runway: approach coordinates, antenna-relative polar
/// coordinates, and the reference lines drawn on the scope.
///
/// Approach coordinates use an exact local tangent frame at the threshold, so a straight glidepath in
/// space is a straight line here and earth curvature is not mistaken for a below-glidepath deviation.
/// </summary>
public sealed class ParGeometry
{
    private readonly LocalFrame _frame;
    private readonly Vec3 _inbound;   // unit vector in the landing direction (ENU)
    private readonly Vec3 _right;     // unit vector to the pilot's right
    private readonly Vec3 _antennaEnu;
    private readonly double _tanGp;

    public ParGeometry(RunwayEnd runway, ParSiteConfig site)
    {
        Runway = runway;
        Site = site;
        _frame = new LocalFrame(GeoPoint.FromFeet(runway.ThresholdLat, runway.ThresholdLon, runway.ThresholdElevFt));
        double h = Units.DegToRad(runway.TrueHeadingDeg);
        _inbound = new Vec3(Math.Sin(h), Math.Cos(h), 0);
        _right = new Vec3(Math.Cos(h), -Math.Sin(h), 0);
        _tanGp = Math.Tan(Units.DegToRad(site.GlidepathDeg));
        GpiFromThresholdFt = site.TchFt / _tanGp;

        double alongFt = GpiFromThresholdFt + site.AntennaSetbackFt;
        _antennaEnu = (_inbound * alongFt + _right * site.AntennaOffsetFt + new Vec3(0, 0, site.AntennaHeightFt)) * Units.MetersPerFoot;
    }

    public RunwayEnd Runway { get; }
    public ParSiteConfig Site { get; }
    public LocalFrame Frame => _frame;

    /// <summary>Glidepath intercept point distance past the threshold (TCH / tan GPA), feet.</summary>
    public double GpiFromThresholdFt { get; }

    /// <summary>Threshold position on the range axis (negative range is past touchdown).</summary>
    public double ThresholdRangeFt => GpiFromThresholdFt;

    public ApproachPoint AntennaPoint => FromEnu(_antennaEnu);

    // ---- Coordinate conversions -------------------------------------------------------------

    public ApproachPoint FromEcef(Vec3 ecef) => FromEnu(_frame.EcefToEnu(ecef));

    public ApproachPoint FromGeo(GeoPoint p) => FromEnu(_frame.ToEnu(p));

    public ApproachPoint FromEnu(Vec3 enuM)
    {
        var f = enuM * Units.FeetPerMeter;
        double alongFromThreshold = Vec3.Dot(f, _inbound); // positive down the runway
        return new ApproachPoint(GpiFromThresholdFt - alongFromThreshold, Vec3.Dot(f, _right), f.Z);
    }

    public Vec3 ToEnu(ApproachPoint p)
    {
        double alongFromThreshold = GpiFromThresholdFt - p.RangeFt;
        return (_inbound * alongFromThreshold + _right * p.CrossFt + new Vec3(0, 0, p.HeightFt)) * Units.MetersPerFoot;
    }

    public Vec3 ToEcef(ApproachPoint p) => _frame.EnuToEcef(ToEnu(p));

    /// <summary>Range/azimuth/elevation as measured at the antenna (boresight parallel to the final course).</summary>
    public RadarPolar ToPolar(ApproachPoint p)
    {
        var d = ToEnu(p) - _antennaEnu;
        double fwd = -Vec3.Dot(d, _inbound), cross = Vec3.Dot(d, _right), up = d.Z;
        double horiz = Math.Sqrt(fwd * fwd + cross * cross);
        return new RadarPolar(
            d.Length * Units.FeetPerMeter,
            Units.RadToDeg(Math.Atan2(cross, fwd)),
            Units.RadToDeg(Math.Atan2(up, horiz)));
    }

    public ApproachPoint FromPolar(RadarPolar r)
    {
        double rm = r.RangeFt * Units.MetersPerFoot;
        double az = Units.DegToRad(r.AzimuthDeg), el = Units.DegToRad(r.ElevationDeg);
        var d = -_inbound * (rm * Math.Cos(el) * Math.Cos(az)) + _right * (rm * Math.Cos(el) * Math.Sin(az)) + new Vec3(0, 0, rm * Math.Sin(el));
        return FromEnu(_antennaEnu + d);
    }

    // ---- Reference lines ---------------------------------------------------------------------

    /// <summary>Glidepath height above threshold elevation at a range from touchdown, feet.</summary>
    public double GlidepathHeightFt(double rangeFt) => rangeFt * _tanGp;

    /// <summary>Range from touchdown at which the glidepath reaches decision height.</summary>
    public double DecisionHeightRangeFt => Site.DecisionHeightFt / _tanGp;

    /// <summary>
    /// Lower safe limit ("B") cursor: from the runway point of intercept at the commissioned glidepath angle
    /// less 0.5° (NAVAIR 00-80T-114 ¶7.2.6.7.3 "normally 0.5 degree less than the glide path angle";
    /// AFMAN 13-204v3 ¶7.4.1.1).
    /// </summary>
    public double LowerSafeLimitHeightFt(double rangeFt) =>
        Math.Max(0, rangeFt) * Math.Tan(Units.DegToRad(Site.GlidepathDeg - 0.5));

    /// <summary>
    /// TERPS final-segment obstacle clearance surface (FAA Order 8260.3F §10-2-2): slope 102/GPA starting
    /// at max(200, 1154 − TCH/tan GPA) ft from the threshold.
    /// </summary>
    public double ObstacleClearanceHeightFt(double rangeFt)
    {
        double fromThreshold = rangeFt - GpiFromThresholdFt;
        double start = Math.Max(200.0, 1154.0 - GpiFromThresholdFt);
        if (fromThreshold <= start) return 0;
        double slope = Site.GlidepathDeg / 102.0;
        return (fromThreshold - start) * slope;
    }

    /// <summary>
    /// Lateral safety limit either side of the course: 200 ft per mile from touchdown
    /// (AFMAN 13-204v3 Table 7.1: ½ NM 100 ft … 6 NM 1,200 ft).
    /// </summary>
    public static double LateralSafeLimitFt(double rangeFt) => Math.Max(100, rangeFt / Units.FeetPerNm * 200);

    /// <summary>Lateral distance of an azimuth line drawn from touchdown at <paramref name="deg"/>.</summary>
    public static double CourseLineFt(double rangeFt, double deg) => Math.Max(0, rangeFt) * Math.Tan(Units.DegToRad(deg));

    // ---- Deviations --------------------------------------------------------------------------

    public Deviation DeviationOf(ApproachPoint p) => new(
        p.RangeFt,
        p.HeightFt - GlidepathHeightFt(p.RangeFt),
        p.CrossFt,
        p.HeightFt - LowerSafeLimitHeightFt(p.RangeFt));
}

/// <param name="AboveGlidepathFt">Positive = above glidepath.</param>
/// <param name="RightOfCourseFt">Positive = right of course (pilot's view).</param>
/// <param name="AboveSafeLimitFt">Height above the lower safe-limit surface (negative = below it).</param>
public readonly record struct Deviation(double RangeFt, double AboveGlidepathFt, double RightOfCourseFt, double AboveSafeLimitFt)
{
    public double RangeNm => RangeFt / Units.FeetPerNm;
}
