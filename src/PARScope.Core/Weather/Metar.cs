using System.Globalization;
using System.Text.RegularExpressions;

namespace PARScope.Core.Weather;

public enum FlightCategory { Unknown, Vfr, Mvfr, Ifr, Lifr }

public enum MetarSource { Vatsim, Noaa }

/// <summary>The parts of a METAR a final controller uses: wind, altimeter, ceiling and visibility.</summary>
public sealed partial record Metar(string Raw)
{
    public string Station => Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    public int? WindDirDeg { get; private init; }
    public int? WindSpeedKt { get; private init; }
    public int? GustKt { get; private init; }
    public bool WindVariable { get; private init; }
    /// <summary>Altimeter in inHg (converted from hPa when reported as Q).</summary>
    public double? AltimeterInHg { get; private init; }
    public int? AltimeterHpa { get; private init; }
    public double? VisibilitySm { get; private init; }
    public int? CeilingFt { get; private init; }

    public FlightCategory Category =>
        CeilingFt is null && VisibilitySm is null ? FlightCategory.Unknown
        : CeilingFt < 500 || VisibilitySm < 1 ? FlightCategory.Lifr
        : CeilingFt < 1000 || VisibilitySm < 3 ? FlightCategory.Ifr
        : CeilingFt <= 3000 || VisibilitySm <= 5 ? FlightCategory.Mvfr
        : FlightCategory.Vfr;

    /// <summary>"WIND 270 AT 10 GUSTS 18, ALTIMETER 2992" as spoken on PAR final.</summary>
    public string Spoken
    {
        get
        {
            var parts = new List<string>();
            if (WindSpeedKt == 0) parts.Add("WIND CALM");
            else if (WindSpeedKt.HasValue)
                parts.Add($"WIND {(WindVariable ? "VARIABLE" : WindDirDeg?.ToString("000"))} AT {WindSpeedKt}" + (GustKt.HasValue ? $" GUSTS {GustKt}" : ""));
            if (AltimeterHpa.HasValue) parts.Add($"QNH {AltimeterHpa}");
            else if (AltimeterInHg.HasValue) parts.Add($"ALTIMETER {AltimeterInHg.Value * 100:0000}");
            return string.Join(", ", parts);
        }
    }

    /// <summary>Compact display form for scope overlays, e.g. "WIND 300/09G18  ALT 30.02".</summary>
    public string Short
    {
        get
        {
            string wind = WindSpeedKt is null ? "WIND ---"
                : WindSpeedKt == 0 ? "WIND CALM"
                : $"WIND {(WindVariable ? "VRB" : WindDirDeg?.ToString("000"))}/{WindSpeedKt:00}" + (GustKt.HasValue ? $"G{GustKt}" : "");
            string alt = AltimeterHpa is int q ? $"QNH {q}" : AltimeterInHg is double a ? $"ALT {a:00.00}" : "ALT --.--";
            return $"{wind}  {alt}";
        }
    }

    public static Metar Parse(string raw)
    {
        raw = WhitespaceRx().Replace(raw.Trim(), " ");
        // Ignore remarks and trend groups for the decoded fields.
        string body = raw.Split(" RMK ")[0];
        foreach (var marker in new[] { " TEMPO ", " BECMG ", " NOSIG" }) body = body.Split(marker)[0];

        int? dir = null, spd = null, gust = null; bool vrb = false;
        var w = WindRx().Match(body);
        if (w.Success)
        {
            vrb = w.Groups["dir"].Value == "VRB";
            if (!vrb) dir = int.Parse(w.Groups["dir"].Value, CultureInfo.InvariantCulture);
            double s = double.Parse(w.Groups["spd"].Value, CultureInfo.InvariantCulture);
            double? g = w.Groups["gst"].Success ? double.Parse(w.Groups["gst"].Value, CultureInfo.InvariantCulture) : null;
            if (w.Groups["unit"].Value == "MPS") { s *= 1.94384; g *= 1.94384; }
            spd = (int)Math.Round(s);
            gust = g.HasValue ? (int)Math.Round(g.Value) : null;
        }

        double? inHg = null; int? hpa = null;
        var a = AltimeterRx().Match(body);
        if (a.Success)
        {
            int v = int.Parse(a.Groups["v"].Value, CultureInfo.InvariantCulture);
            if (a.Groups["u"].Value == "A") inHg = v / 100.0;
            else { hpa = v; inHg = v * 0.0295300; }
        }

        return new Metar(raw)
        {
            WindDirDeg = dir, WindSpeedKt = spd, GustKt = gust, WindVariable = vrb,
            AltimeterInHg = inHg, AltimeterHpa = hpa,
            VisibilitySm = ParseVisibility(body),
            CeilingFt = ParseCeiling(body),
        };
    }

    private static double? ParseVisibility(string body)
    {
        if (body.Contains(" CAVOK")) return 10;
        var sm = VisSmRx().Match(body);
        if (sm.Success)
        {
            double whole = sm.Groups["w"].Success ? double.Parse(sm.Groups["w"].Value, CultureInfo.InvariantCulture) : 0;
            var v = sm.Groups["v"].Value.Split('/');
            double part = v.Length == 2
                ? double.Parse(v[0], CultureInfo.InvariantCulture) / double.Parse(v[1], CultureInfo.InvariantCulture)
                : double.Parse(v[0], CultureInfo.InvariantCulture);
            // "M1/4SM" means less than a quarter mile.
            return sm.Groups["m"].Success ? Math.Max(0, whole + part - 0.01) : whole + part;
        }
        var m = VisMetersRx().Match(body);
        if (m.Success) return int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture) / 1609.344;
        return null;
    }

    private static int? ParseCeiling(string body)
    {
        if (body.Contains(" CAVOK") || body.Contains(" SKC") || body.Contains(" CLR") || body.Contains(" NSC")) return null;
        foreach (Match c in CloudRx().Matches(body))
        {
            if (c.Groups["c"].Value is "BKN" or "OVC" or "VV")
                return int.Parse(c.Groups["h"].Value, CultureInfo.InvariantCulture) * 100;
        }
        return null;
    }

    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRx();
    [GeneratedRegex(@"\b(?<dir>\d{3}|VRB)(?<spd>\d{2,3})(G(?<gst>\d{2,3}))?(?<unit>KT|MPS)\b")] private static partial Regex WindRx();
    [GeneratedRegex(@"\b(?<u>[AQ])(?<v>\d{4})\b")] private static partial Regex AltimeterRx();
    [GeneratedRegex(@"\s(?<m>M)?(?:(?<w>\d{1,2})\s(?=\d/))?(?<v>\d{1,2}(?:/\d{1,2})?)SM\b")] private static partial Regex VisSmRx();
    [GeneratedRegex(@"\s(?<v>\d{4})(?:NDV)?\s")] private static partial Regex VisMetersRx();
    [GeneratedRegex(@"\b(?<c>FEW|SCT|BKN|OVC|VV)(?<h>\d{3})")] private static partial Regex CloudRx();
}

public sealed class MetarService(HttpClient http)
{
    public async Task<Metar?> FetchAsync(string icao, MetarSource source, CancellationToken ct)
    {
        string url = source == MetarSource.Vatsim
            ? $"https://metar.vatsim.net/{Uri.EscapeDataString(icao)}"
            : $"https://aviationweather.gov/api/data/metar?ids={Uri.EscapeDataString(icao)}&format=raw";
        string text = (await http.GetStringAsync(url, ct)).Trim();
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(icao, StringComparison.OrdinalIgnoreCase)
                                                                              || l.StartsWith("METAR " + icao) || l.StartsWith("SPECI " + icao));
        if (line == null) return null;
        if (line.StartsWith("METAR ") || line.StartsWith("SPECI ")) line = line[6..];
        return Metar.Parse(line);
    }
}
