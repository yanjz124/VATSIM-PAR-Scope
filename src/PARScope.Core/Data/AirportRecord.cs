using PARScope.Core.Geo;
using PARScope.Core.Par;

namespace PARScope.Core.Data;

public sealed record AirportRecord(
    string Id,
    string? Icao,
    string Name,
    double LatDeg,
    double LonDeg,
    double ElevationFt,
    IReadOnlyList<RunwayEnd> Runways,
    string Source,
    string? ApproachCall = null,
    string? TowerCall = null)
{
    /// <summary>ICAO when known, else the local identifier.</summary>
    public string MetarId => string.IsNullOrEmpty(Icao) ? Id : Icao;

    /// <summary>
    /// Radio name of the radar facility for "(facility) final": the approach control that serves the field
    /// (FAA NASR, e.g. "Potomac" for DCA, "Dover" for the Dover RAPCON), else the tower's call, else the first
    /// full word of the airport name ("Miramar" from "MIRAMAR MCAS (JOE FOSS FLD)").
    /// </summary>
    public string FacilityCall
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ApproachCall)) return TitleCase(ApproachCall);
            if (!string.IsNullOrWhiteSpace(TowerCall)) return TitleCase(TowerCall);
            var words = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string w = words.FirstOrDefault(x => x.Length > 3 && x.All(char.IsLetter)) ?? words.FirstOrDefault() ?? "Approach";
            return TitleCase(w);
        }
    }

    private static string TitleCase(string s) =>
        string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    public override string ToString() => string.IsNullOrEmpty(Icao) || Icao == Id ? $"{Id}  {Name}" : $"{Id} / {Icao}  {Name}";
}

internal static class RunwayMath
{
    /// <summary>True course from one surveyed runway end to the other. Far more precise than the
    /// whole-degree alignment published in NASR (0.5° is ~530 ft of course error at 10 NM).</summary>
    public static double TrueCourseDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var frame = new LocalFrame(new GeoPoint(lat1, lon1, 0));
        var v = frame.ToEnu(new GeoPoint(lat2, lon2, 0));
        return Units.Norm360(Units.RadToDeg(Math.Atan2(v.X, v.Y)));
    }

    /// <summary>Moves a runway end along the runway to its displaced threshold.</summary>
    public static (double Lat, double Lon) Displace(double lat, double lon, double trueCourseDeg, double displacedFt)
    {
        if (displacedFt <= 0) return (lat, lon);
        var p = Wgs84.Offset(new GeoPoint(lat, lon, 0), trueCourseDeg, displacedFt * Units.MetersPerFoot);
        return (p.LatDeg, p.LonDeg);
    }
}
