namespace PARScope.Core.Geo;

public static class Units
{
    public const double FeetPerMeter = 3.280839895;
    public const double MetersPerFoot = 0.3048;
    public const double MetersPerNm = 1852.0;
    public const double FeetPerNm = MetersPerNm * FeetPerMeter;
    public const double MpsPerKnot = MetersPerNm / 3600.0;

    public static double DegToRad(double deg) => deg * Math.PI / 180.0;
    public static double RadToDeg(double rad) => rad * 180.0 / Math.PI;

    /// <summary>Normalizes to [0, 360).</summary>
    public static double Norm360(double deg)
    {
        deg %= 360.0;
        return deg < 0 ? deg + 360.0 : deg;
    }

    /// <summary>Normalizes to (-180, 180].</summary>
    public static double Norm180(double deg)
    {
        deg = Norm360(deg);
        return deg > 180.0 ? deg - 360.0 : deg;
    }
}
