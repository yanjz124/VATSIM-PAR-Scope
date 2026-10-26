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
