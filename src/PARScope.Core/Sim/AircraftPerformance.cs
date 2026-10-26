using System.Globalization;
using System.Reflection;

namespace PARScope.Core.Sim;

/// <summary>An aircraft type's approach performance.</summary>
/// <param name="ApproachSpeedKt">Approach speed (1.3 Vso at maximum landing weight), knots.</param>
/// <param name="ApproachCategory">Aircraft approach category A–E.</param>
public sealed record AircraftType(string Icao, string Name, double? ApproachSpeedKt, string ApproachCategory, string WakeCategory);

/// <summary>
/// Approach speeds and categories from the FAA Aircraft Characteristics Database
/// (faa.gov/airports/engineering/aircraft_char_database), embedded as aircraft.csv.
/// </summary>
public static class AircraftPerformance
{
    private static readonly Lazy<Dictionary<string, AircraftType>> Table = new(Load);

    public static IReadOnlyCollection<AircraftType> All => Table.Value.Values;

    public static AircraftType? Find(string? icao) =>
        icao != null && Table.Value.TryGetValue(icao.Trim(), out var t) ? t : null;

    private static Dictionary<string, AircraftType> Load()
    {
        var dict = new Dictionary<string, AircraftType>(StringComparer.OrdinalIgnoreCase);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PARScope.Core.Sim.aircraft.csv");
        if (stream == null) return dict;
        using var reader = new StreamReader(stream);
        reader.ReadLine(); // header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = SplitCsv(line);
            if (f.Count < 5 || f[0].Length == 0) continue;
            double? speed = double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : null;
            dict.TryAdd(f[0], new AircraftType(f[0], f[1], speed, f[3], f[4]));
        }
        return dict;
    }

    internal static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
