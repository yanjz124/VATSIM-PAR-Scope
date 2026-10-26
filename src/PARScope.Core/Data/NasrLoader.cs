using System.Globalization;
using System.IO.Compression;
using PARScope.Core.Par;

namespace PARScope.Core.Data;

/// <summary>
/// FAA NASR 28-day subscription, APT CSV product (US airports, including military fields).
/// Provides thresholds, displaced thresholds, TCH, visual glidepath angle, approach lights and
/// magnetic variation.
/// </summary>
public static class NasrLoader
{
    private const string BaseUrl = "https://nfdc.faa.gov/webContent/28DaySub/extra/";
    /// <summary>A known cycle effective date; cycles repeat every 28 days.</summary>
    private static readonly DateTime CycleAnchor = new(2025, 10, 2);

    /// <summary>Candidate download URLs, newest cycle first.</summary>
    public static IEnumerable<(DateTime Cycle, string Url)> CandidateUrls(DateTime todayUtc)
    {
        int n = (int)Math.Floor((todayUtc.Date - CycleAnchor).TotalDays / 28.0);
        for (int k = n; k >= n - 2; k--)
        {
            var d = CycleAnchor.AddDays(28 * k);
            yield return (d, $"{BaseUrl}{d.ToString("dd_MMM_yyyy", CultureInfo.InvariantCulture)}_APT_CSV.zip");
        }
    }

    public static List<AirportRecord> ParseZip(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        ZipArchiveEntry Entry(string name) => zip.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                                              ?? throw new InvalidDataException(name + " not found in NASR archive");

        var bases = new Dictionary<string, (string Name, string? Icao, double Lat, double Lon, double Elev, double MagVarEast)>(StringComparer.OrdinalIgnoreCase);
        using (var r = new StreamReader(Entry("APT_BASE.csv").Open()))
        {
            var csv = new CsvReader(r);
            while (csv.Read())
            {
                string id = csv.Get("ARPT_ID");
                if (id.Length == 0) continue;
                double mag = csv.GetDouble("MAG_VARN") ?? 0;
                if (csv.Get("MAG_HEMIS").StartsWith('W')) mag = -mag;
                string icao = csv.Get("ICAO_ID");
                bases[id] = (csv.Get("ARPT_NAME"), icao.Length > 0 ? icao : null,
                    csv.GetDouble("LAT_DECIMAL") ?? 0, csv.GetDouble("LONG_DECIMAL") ?? 0, csv.GetDouble("ELEV") ?? 0, mag);
            }
        }

        var dims = new Dictionary<(string, string), (double? Len, double? Wid)>();
        using (var r = new StreamReader(Entry("APT_RWY.csv").Open()))
        {
            var csv = new CsvReader(r);
            while (csv.Read())
                dims[(csv.Get("ARPT_ID"), csv.Get("RWY_ID"))] = (csv.GetDouble("RWY_LEN"), csv.GetDouble("RWY_WIDTH"));
        }

        var ends = new List<EndRow>();
        using (var r = new StreamReader(Entry("APT_RWY_END.csv").Open()))
        {
            var csv = new CsvReader(r);
            while (csv.Read())
            {
                if (csv.GetDouble("LAT_DECIMAL") is not double lat || csv.GetDouble("LONG_DECIMAL") is not double lon) continue;
                ends.Add(new EndRow(
                    csv.Get("ARPT_ID"), csv.Get("RWY_ID"), csv.Get("RWY_END_ID"), lat, lon,
                    csv.GetDouble("TRUE_ALIGNMENT"),
                    csv.GetDouble("RWY_END_ELEV"),
                    csv.GetDouble("LAT_DISPLACED_THR_DECIMAL"), csv.GetDouble("LONG_DISPLACED_THR_DECIMAL"),
                    csv.GetDouble("DISPLACED_THR_ELEV"), csv.GetDouble("TDZ_ELEV"),
                    csv.GetDouble("THR_CROSSING_HGT"), csv.GetDouble("VISUAL_GLIDE_PATH_ANGLE"),
                    csv.Get("APCH_LGT_SYSTEM_CODE")));
            }
        }

        var result = new List<AirportRecord>();
        foreach (var apt in ends.GroupBy(e => e.Airport, StringComparer.OrdinalIgnoreCase))
        {
            if (!bases.TryGetValue(apt.Key, out var b)) continue;
            var runways = new List<RunwayEnd>();
            foreach (var pair in apt.GroupBy(e => e.RunwayPair))
            {
                var list = pair.ToList();
                dims.TryGetValue((apt.Key, pair.Key), out var dim);
                foreach (var end in list)
                {
                    var other = list.FirstOrDefault(o => !ReferenceEquals(o, end));
                    double course = other != null
                        ? RunwayMath.TrueCourseDeg(end.Lat, end.Lon, other.Lat, other.Lon)
                        : end.TrueAlignment ?? 0;
                    if (other == null && end.TrueAlignment == null) continue;

                    double thrLat = end.DisplacedLat ?? end.Lat, thrLon = end.DisplacedLon ?? end.Lon;
                    double elev = end.DisplacedElev ?? end.Elev ?? end.TdzElev ?? b.Elev;
                    runways.Add(new RunwayEnd(apt.Key, b.Icao, end.EndId, thrLat, thrLon, elev, course,
                        end.Tch is > 0 ? end.Tch : null,
                        end.Vgpa is > 0 ? end.Vgpa : null,
                        b.MagVarEast,
                        end.Lights.Length > 0 ? end.Lights : null,
                        dim.Len, dim.Wid, "NASR"));
                }
            }
            if (runways.Count > 0)
                result.Add(new AirportRecord(apt.Key, b.Icao, b.Name, b.Lat, b.Lon, b.Elev, runways, "NASR"));
        }
        return result;
    }

    private sealed record EndRow(string Airport, string RunwayPair, string EndId, double Lat, double Lon,
        double? TrueAlignment, double? Elev, double? DisplacedLat, double? DisplacedLon, double? DisplacedElev,
        double? TdzElev, double? Tch, double? Vgpa, string Lights);
}
