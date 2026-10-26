using System.Reflection;

namespace PARScope.Core.Sim;

/// <summary>A unit's tactical callword and the aircraft it flies.</summary>
/// <param name="FlightNumbers">Fighter/trainer formations numbered 11–14, 21–24…; otherwise a two-digit mission number.</param>
public sealed record TacticalCallsign(string Callword, string TypeCode, string Unit, bool FlightNumbers);

/// <summary>
/// US military tactical callwords from Ron's Military Callsign List (henney.com/chm/callsign.htm, as of
/// 31 Dec 2024), embedded as callsigns.csv. Only entries whose aircraft is in the FAA Aircraft
/// Characteristics Database are kept, so every type has a sourced approach speed.
/// </summary>
public static class MilitaryCallsigns
{
    private static readonly Lazy<IReadOnlyList<TacticalCallsign>> Table = new(Load);

    public static IReadOnlyList<TacticalCallsign> All => Table.Value;

    /// <summary>Words in airport names that say nothing about which base it is.</summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AIR", "AIRPORT", "ARPT", "BASE", "FIELD", "FLD", "NAVAL", "STATION", "MARINE", "CORPS", "FORCE", "JOINT",
        "INTL", "INTERNATIONAL", "REGIONAL", "RGNL", "MUNICIPAL", "MUNI", "COUNTY", "ARMY", "NATIONAL", "GUARD",
        "MCAS", "NAS", "AFB", "ARB", "ANGB", "NAF", "OLF", "AAF", "RAF", "ANG", "ARS", "THE", "AND",
    };

    /// <summary>
    /// Units based at an airport, matched by its distinctive name words against the unit's base
    /// ("MIRAMAR MCAS (JOSEPH J FOSS FLD)" finds "VMFA(AW)-242 MCAS Miramar CA").
    /// </summary>
    public static IReadOnlyList<TacticalCallsign> BasedAt(string? airportName)
    {
        if (string.IsNullOrWhiteSpace(airportName)) return [];
        var words = airportName.Split([' ', '(', ')', '/', '-', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && !GenericWords.Contains(w)).ToList();
        if (words.Count == 0) return [];
        return All.Where(c => c.Unit.Split([' ', '(', ')', '/', '-', ','], StringSplitOptions.RemoveEmptyEntries)
            .Any(u => words.Contains(u, StringComparer.OrdinalIgnoreCase))).ToList();
    }

    private static IReadOnlyList<TacticalCallsign> Load()
    {
        var list = new List<TacticalCallsign>();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PARScope.Core.Sim.callsigns.csv");
        if (stream == null) return list;
        using var reader = new StreamReader(stream);
        reader.ReadLine(); // header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = AircraftPerformance.SplitCsv(line);
            if (f.Count < 4 || f[0].Length == 0) continue;
            list.Add(new TacticalCallsign(f[0], f[1], f[2], f[3] == "flight"));
        }
        return list;
    }
}
