using PARScope.Core.Par;

namespace PARScope.Core.Data;

/// <summary>
/// OurAirports open data (public domain, worldwide). Fills in everything outside the US. It has no
/// TCH, glidepath angle or magnetic variation: variation comes from the World Magnetic Model, the
/// others from site defaults.
/// </summary>
public static class OurAirportsLoader
{
    public const string AirportsUrl = "https://davidmegginson.github.io/ourairports-data/airports.csv";
    public const string RunwaysUrl = "https://davidmegginson.github.io/ourairports-data/runways.csv";

    private static readonly HashSet<string> UsefulTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "large_airport", "medium_airport", "small_airport",
    };

    public static List<AirportRecord> Parse(TextReader airportsCsv, TextReader runwaysCsv)
    {
        var airports = new Dictionary<string, (string Ident, string? Icao, string Name, double Lat, double Lon, double Elev)>();
        var a = new CsvReader(airportsCsv);
        while (a.Read())
        {
            if (!UsefulTypes.Contains(a.Get("type"))) continue;
            string ident = a.Get("ident");
            string icao = a.Get("icao_code");
            if (icao.Length == 0 && a.Get("gps_code") is { Length: 4 } gps && gps.All(char.IsLetter)) icao = gps;
            airports[ident] = (ident, icao.Length > 0 ? icao : null, a.Get("name"),
                a.GetDouble("latitude_deg") ?? 0, a.GetDouble("longitude_deg") ?? 0, a.GetDouble("elevation_ft") ?? 0);
        }

        var byAirport = new Dictionary<string, List<RunwayEnd>>();
        var r = new CsvReader(runwaysCsv);
        while (r.Read())
        {
            if (r.Get("closed") == "1") continue;
            string ident = r.Get("airport_ident");
            if (!airports.TryGetValue(ident, out var apt)) continue;
            if (r.GetDouble("le_latitude_deg") is not double leLat || r.GetDouble("le_longitude_deg") is not double leLon) continue;
            if (r.GetDouble("he_latitude_deg") is not double heLat || r.GetDouble("he_longitude_deg") is not double heLon) continue;
            // Helipads are listed as runways ("H1", "06H/24H"); they are not PAR runways.
            if (IsHelipad(r.Get("le_ident")) || IsHelipad(r.Get("he_ident"))) continue;

            double len = r.GetDouble("length_ft") ?? 0, wid = r.GetDouble("width_ft") ?? 0;
            var list = byAirport.TryGetValue(ident, out var l) ? l : byAirport[ident] = [];
            string id = apt.Icao ?? apt.Ident;
            list.Add(End(id, apt.Icao, r, "le", leLat, leLon, heLat, heLon, apt.Elev, len, wid));
            list.Add(End(id, apt.Icao, r, "he", heLat, heLon, leLat, leLon, apt.Elev, len, wid));
        }

        return byAirport
            .Select(kv =>
            {
                var apt = airports[kv.Key];
                return new AirportRecord(apt.Icao ?? apt.Ident, apt.Icao, apt.Name, apt.Lat, apt.Lon, apt.Elev, kv.Value, "OurAirports");
            })
            .ToList();
    }

    private static bool IsHelipad(string ident) =>
        ident.StartsWith('H') || (ident.Length > 1 && ident.EndsWith('H') && char.IsDigit(ident[0]));

    private static RunwayEnd End(string airportId, string? icao, CsvReader r, string p,
        double lat, double lon, double otherLat, double otherLon, double fieldElev, double len, double wid)
    {
        double course = RunwayMath.TrueCourseDeg(lat, lon, otherLat, otherLon);
        var (thrLat, thrLon) = RunwayMath.Displace(lat, lon, course, r.GetDouble(p + "_displaced_threshold_ft") ?? 0);
        return new RunwayEnd(airportId, icao, r.Get(p + "_ident"), thrLat, thrLon,
            r.GetDouble(p + "_elevation_ft") ?? fieldElev, course,
            MagVarEastDeg: Math.Round(Geo.Wmm.DeclinationDeg(thrLat, thrLon), 1),
            LengthFt: len > 0 ? len : null, WidthFt: wid > 0 ? wid : null, Source: "OurAirports");
    }
}
