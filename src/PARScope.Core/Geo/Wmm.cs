using System.Globalization;
using System.Reflection;

namespace PARScope.Core.Geo;

/// <summary>
/// World Magnetic Model (NOAA/BGS WMM2025, valid 2025.0–2030.0; coefficients are public domain and
/// embedded as WMM2025.COF). Gives magnetic declination where airport data has no variation of record,
/// i.e. outside the FAA NASR. Spherical-harmonic synthesis to degree 12 with Schmidt semi-normalised
/// associated Legendre functions, following the WMM technical report.
/// </summary>
public static class Wmm
{
    private const int MaxDegree = 12;
    private const double ReferenceRadiusKm = 6371.2;
    private static readonly Lazy<(double Epoch, double[,] G, double[,] H, double[,] Gdot, double[,] Hdot)> Model = new(Load);

    /// <summary>Declination (variation), degrees, east positive, at a geodetic position and decimal year.</summary>
    public static double DeclinationDeg(double latDeg, double lonDeg, double altitudeKm = 0, double? year = null)
    {
        var (x, y, _) = Field(latDeg, lonDeg, altitudeKm, year ?? DecimalYear(DateTime.UtcNow));
        return Units.RadToDeg(Math.Atan2(y, x));
    }

    public static double DecimalYear(DateTime utc) =>
        utc.Year + (utc.DayOfYear - 1 + utc.TimeOfDay.TotalDays) / (DateTime.IsLeapYear(utc.Year) ? 366.0 : 365.0);

    /// <summary>North, east and down field components in nT, geodetic frame.</summary>
    public static (double X, double Y, double Z) Field(double latDeg, double lonDeg, double altitudeKm, double year)
    {
        var (epoch, g0, h0, gd, hd) = Model.Value;
        double dt = year - epoch;

        // Geodetic → geocentric spherical (WGS-84).
        const double a = 6378.137, f = 1 / 298.257223563;
        double e2 = f * (2 - f);
        double lat = Units.DegToRad(latDeg), lon = Units.DegToRad(lonDeg);
        double rc = a / Math.Sqrt(1 - e2 * Math.Sin(lat) * Math.Sin(lat));
        double p = (rc + altitudeKm) * Math.Cos(lat), z = (rc * (1 - e2) + altitudeKm) * Math.Sin(lat);
        double r = Math.Sqrt(p * p + z * z);
        double latC = Math.Asin(z / r);
        double cosT = Math.Sin(latC), sinT = Math.Cos(latC); // θ = colatitude

        // Legendre functions P(n,m)(cos θ) and dP/dθ, Gauss normalised, then Schmidt factors.
        var P = new double[MaxDegree + 1, MaxDegree + 1];
        var dP = new double[MaxDegree + 1, MaxDegree + 1];
        var S = new double[MaxDegree + 1, MaxDegree + 1];
        P[0, 0] = 1; S[0, 0] = 1;
        for (int n = 1; n <= MaxDegree; n++)
        {
            S[n, 0] = S[n - 1, 0] * (2 * n - 1) / n;
            for (int m = 0; m <= n; m++)
            {
                if (m > 0) S[n, m] = S[n, m - 1] * Math.Sqrt((n - m + 1) * (m == 1 ? 2.0 : 1.0) / (n + m));
                if (n == m)
                {
                    P[n, m] = sinT * P[n - 1, m - 1];
                    dP[n, m] = sinT * dP[n - 1, m - 1] + cosT * P[n - 1, m - 1];
                }
                else if (n == 1)
                {
                    P[n, m] = cosT * P[n - 1, m];
                    dP[n, m] = cosT * dP[n - 1, m] - sinT * P[n - 1, m];
                }
                else
                {
                    double k = ((n - 1) * (n - 1) - m * m) / (double)((2 * n - 1) * (2 * n - 3));
                    P[n, m] = cosT * P[n - 1, m] - k * P[n - 2, m];
                    dP[n, m] = cosT * dP[n - 1, m] - sinT * P[n - 1, m] - k * dP[n - 2, m];
                }
            }
        }

        double br = 0, bt = 0, bp = 0;
        for (int n = 1; n <= MaxDegree; n++)
        {
            double ar = Math.Pow(ReferenceRadiusKm / r, n + 2);
            for (int m = 0; m <= n; m++)
            {
                double g = (g0[n, m] + dt * gd[n, m]) * S[n, m], h = (h0[n, m] + dt * hd[n, m]) * S[n, m];
                double cm = Math.Cos(m * lon), sm = Math.Sin(m * lon);
                br += (n + 1) * ar * (g * cm + h * sm) * P[n, m];
                bt -= ar * (g * cm + h * sm) * dP[n, m];
                if (sinT > 1e-12) bp -= ar * m * (-g * sm + h * cm) * P[n, m] / sinT;
            }
        }

        // Spherical (north = −Bθ, east = Bφ, down = −Br), rotated from geocentric to geodetic latitude.
        double xs = -bt, ys = bp, zs = -br, psi = latC - lat;
        return (xs * Math.Cos(psi) - zs * Math.Sin(psi), ys, xs * Math.Sin(psi) + zs * Math.Cos(psi));
    }

    private static (double, double[,], double[,], double[,], double[,]) Load()
    {
        var g = new double[MaxDegree + 1, MaxDegree + 1];
        var h = new double[MaxDegree + 1, MaxDegree + 1];
        var gd = new double[MaxDegree + 1, MaxDegree + 1];
        var hd = new double[MaxDegree + 1, MaxDegree + 1];
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PARScope.Core.Geo.WMM2025.COF")
                      ?? throw new InvalidOperationException("WMM coefficients missing");
        using var reader = new StreamReader(s);
        double epoch = double.Parse(reader.ReadLine()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 6 || f[0].StartsWith("9999")) break;
            int n = int.Parse(f[0], CultureInfo.InvariantCulture), m = int.Parse(f[1], CultureInfo.InvariantCulture);
            g[n, m] = double.Parse(f[2], CultureInfo.InvariantCulture);
            h[n, m] = double.Parse(f[3], CultureInfo.InvariantCulture);
            gd[n, m] = double.Parse(f[4], CultureInfo.InvariantCulture);
            hd[n, m] = double.Parse(f[5], CultureInfo.InvariantCulture);
        }
        return (epoch, g, h, gd, hd);
    }
}
