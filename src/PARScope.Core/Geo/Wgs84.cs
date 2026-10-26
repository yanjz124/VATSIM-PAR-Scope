namespace PARScope.Core.Geo;

/// <summary>Geodetic position. Altitude is metres above the ellipsoid (MSL is used as a stand-in; geoid
/// undulation is effectively constant over a PAR's 20 NM footprint, so it cancels out).</summary>
public readonly record struct GeoPoint(double LatDeg, double LonDeg, double AltM)
{
    public static GeoPoint FromFeet(double latDeg, double lonDeg, double altFt) => new(latDeg, lonDeg, altFt * Units.MetersPerFoot);
    public double AltFt => AltM * Units.FeetPerMeter;
}

public static class Wgs84
{
    public const double A = 6378137.0;
    public const double F = 1.0 / 298.257223563;
    public const double E2 = F * (2 - F);
    public const double B = A * (1 - F);
    private const double Ep2 = E2 / (1 - E2);

    public static Vec3 ToEcef(GeoPoint p)
    {
        double lat = Units.DegToRad(p.LatDeg), lon = Units.DegToRad(p.LonDeg);
        double sLat = Math.Sin(lat), cLat = Math.Cos(lat);
        double n = A / Math.Sqrt(1 - E2 * sLat * sLat);
        return new Vec3(
            (n + p.AltM) * cLat * Math.Cos(lon),
            (n + p.AltM) * cLat * Math.Sin(lon),
            (n * (1 - E2) + p.AltM) * sLat);
    }

    /// <summary>ECEF to geodetic (Bowring's method, sub-millimetre at aviation altitudes).</summary>
    public static GeoPoint FromEcef(Vec3 e)
    {
        double p = Math.Sqrt(e.X * e.X + e.Y * e.Y);
        double theta = Math.Atan2(e.Z * A, p * B);
        double sT = Math.Sin(theta), cT = Math.Cos(theta);
        double lat = Math.Atan2(e.Z + Ep2 * B * sT * sT * sT, p - E2 * A * cT * cT * cT);
        double lon = Math.Atan2(e.Y, e.X);
        double sLat = Math.Sin(lat);
        double n = A / Math.Sqrt(1 - E2 * sLat * sLat);
        double alt = p / Math.Cos(lat) - n;
        return new GeoPoint(Units.RadToDeg(lat), Units.RadToDeg(lon), alt);
    }

    /// <summary>Rotates a local east/north/up vector at the given position into ECEF axes.</summary>
    public static Vec3 EnuToEcefVector(double latDeg, double lonDeg, Vec3 enu)
    {
        double lat = Units.DegToRad(latDeg), lon = Units.DegToRad(lonDeg);
        double sLat = Math.Sin(lat), cLat = Math.Cos(lat), sLon = Math.Sin(lon), cLon = Math.Cos(lon);
        return new Vec3(
            -sLon * enu.X - sLat * cLon * enu.Y + cLat * cLon * enu.Z,
            cLon * enu.X - sLat * sLon * enu.Y + cLat * sLon * enu.Z,
            cLat * enu.Y + sLat * enu.Z);
    }

    /// <summary>Destination point along a great-circle-ish course on the local tangent plane (adequate within ~50 NM).</summary>
    public static GeoPoint Offset(GeoPoint from, double trueCourseDeg, double distanceM, double upM = 0)
    {
        var frame = new LocalFrame(from);
        double c = Units.DegToRad(trueCourseDeg);
        return frame.ToGeo(new Vec3(distanceM * Math.Sin(c), distanceM * Math.Cos(c), upM));
    }
}

/// <summary>Local east-north-up tangent frame anchored at an origin. Exact (no flat-earth approximation),
/// so earth curvature drop (~25 m at 10 NM) is represented correctly in the Up component.</summary>
public sealed class LocalFrame
{
    private readonly Vec3 _originEcef;
    private readonly double _sLat, _cLat, _sLon, _cLon;

    public LocalFrame(GeoPoint origin)
    {
        Origin = origin;
        _originEcef = Wgs84.ToEcef(origin);
        double lat = Units.DegToRad(origin.LatDeg), lon = Units.DegToRad(origin.LonDeg);
        _sLat = Math.Sin(lat); _cLat = Math.Cos(lat);
        _sLon = Math.Sin(lon); _cLon = Math.Cos(lon);
    }

    public GeoPoint Origin { get; }

    public Vec3 EcefToEnu(Vec3 ecef)
    {
        var d = ecef - _originEcef;
        return new Vec3(
            -_sLon * d.X + _cLon * d.Y,
            -_sLat * _cLon * d.X - _sLat * _sLon * d.Y + _cLat * d.Z,
            _cLat * _cLon * d.X + _cLat * _sLon * d.Y + _sLat * d.Z);
    }

    public Vec3 EnuToEcef(Vec3 enu) => _originEcef + Wgs84.EnuToEcefVector(Origin.LatDeg, Origin.LonDeg, enu);

    public Vec3 ToEnu(GeoPoint p) => EcefToEnu(Wgs84.ToEcef(p));
    public GeoPoint ToGeo(Vec3 enu) => Wgs84.FromEcef(EnuToEcef(enu));
}
