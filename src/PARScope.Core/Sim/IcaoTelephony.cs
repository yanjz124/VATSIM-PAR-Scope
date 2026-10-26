using System.Reflection;

namespace PARScope.Core.Sim;

/// <summary>An aircraft operating agency: its radiotelephony callsign and whether it is a US military operator.</summary>
public sealed record IcaoOperator(string Code, string Telephony, string Company, string Country, bool Military)
{
    /// <summary>The telephony as spoken: "FRONTIER FLIGHT" → "Frontier Flight".</summary>
    public string Spoken => string.Join(' ', Telephony.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
}

/// <summary>
/// ICAO three-letter operator designators and their telephony from FAA JO 7340.2P (Contractions), chapter 3
/// section 3, embedded as airlines.csv: 5,703 designators. Used to speak callsigns typed by the controller
/// ("RPA4567": "Brickyard forty-five sixty-seven"); generated training callsigns are military only and are
/// kept clear of civil designators. US military operators (REACH, CONVOY, PAT, SAM…) are marked.
/// </summary>
public static class IcaoTelephony
{
    private static readonly Lazy<Dictionary<string, IcaoOperator>> Table = new(Load);

    public static IcaoOperator? Find(string code) => Table.Value.TryGetValue(code, out var op) ? op : null;

    /// <summary>A designator assigned to a civil operator, which a generated military callsign must not use.</summary>
    public static bool IsCivil(string code) => Find(code) is { Military: false };

    public static int Count => Table.Value.Count;

    private static Dictionary<string, IcaoOperator> Load()
    {
        var map = new Dictionary<string, IcaoOperator>(StringComparer.OrdinalIgnoreCase);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PARScope.Core.Sim.airlines.csv");
        if (stream == null) return map;
        using var reader = new StreamReader(stream);
        reader.ReadLine(); // header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = AircraftPerformance.SplitCsv(line);
            if (f.Count < 5 || f[0].Length != 3 || f[1].Length == 0) continue;
            map[f[0]] = new IcaoOperator(f[0], f[1], f[2], f[3], f[4] == "1");
        }
        return map;
    }
}
