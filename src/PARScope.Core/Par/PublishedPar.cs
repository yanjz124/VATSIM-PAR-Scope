using System.Globalization;
using System.Reflection;

namespace PARScope.Core.Par;

/// <summary>A runway's published PAR: glidepath, threshold crossing height, runway point of intercept and minimums.</summary>
public sealed record PublishedParMinimums(string Icao, string RunwayId, double GlidepathDeg, double TchFt, double RpiFt,
    double DecisionAltitudeFt, double HatFt, string Amendment)
{
    public string Source => $"FAA d-TPP Radar Minimums, {Amendment}";
}

/// <summary>
/// Published PAR data for US military and joint-use airports (embedded par-minimums.csv, transcribed from the
/// FAA d-TPP Radar Instrument Approach Minimums pages). Used to pre-fill a runway's PAR site configuration.
/// </summary>
public static class PublishedPar
{
    private static readonly Lazy<Dictionary<(string, string), PublishedParMinimums>> Table = new(Load);

    public static IReadOnlyCollection<PublishedParMinimums> All => Table.Value.Values;

    /// <summary>Looks up by ICAO code (or FAA LID with a K prefix added) and runway id ("6", "06" and "06L" forms).</summary>
    public static PublishedParMinimums? Find(string? icaoOrLid, string runwayId)
    {
        if (string.IsNullOrEmpty(icaoOrLid)) return null;
        string rwy = Normalize(runwayId);
        if (Table.Value.TryGetValue((icaoOrLid.ToUpperInvariant(), rwy), out var p)) return p;
        return icaoOrLid.Length == 3 && Table.Value.TryGetValue(("K" + icaoOrLid.ToUpperInvariant(), rwy), out p) ? p : null;
    }

    /// <summary>Site configuration with the published glidepath, TCH, RPI and DH; antenna siting is kept.</summary>
    public static ParSiteConfig Apply(ParSiteConfig site, PublishedParMinimums p) => site with
    {
        GlidepathDeg = p.GlidepathDeg,
        TchFt = p.TchFt,
        RpiFt = p.RpiFt,
        DecisionHeightFt = p.HatFt,
        Source = p.Source,
    };

    private static string Normalize(string id)
    {
        id = id.Trim().ToUpperInvariant();
        int digits = id.TakeWhile(char.IsDigit).Count();
        return digits == 1 ? "0" + id : id;
    }

    private static Dictionary<(string, string), PublishedParMinimums> Load()
    {
        var dict = new Dictionary<(string, string), PublishedParMinimums>();
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PARScope.Core.Par.par-minimums.csv");
        if (s == null) return dict;
        using var reader = new StreamReader(s);
        string? line;
        bool header = true;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            if (header) { header = false; continue; }
            var f = line.Split(',');
            double D(int i) => double.Parse(f[i], CultureInfo.InvariantCulture);
            var p = new PublishedParMinimums(f[0], Normalize(f[1]), D(2), D(3), D(4), D(5), D(6), f[7]);
            dict[(p.Icao, p.RunwayId)] = p;
        }
        return dict;
    }
}
