using System.Globalization;
using System.Text.RegularExpressions;
using PARScope.Core.Geo;
using PARScope.Core.Sim;

namespace PARScope.Core.Data;

/// <summary>
/// A controller frequency at an airport from FAA NASR FRQ.csv: <paramref name="Use"/> is LCL (tower), GND, APCH
/// or DEP; <paramref name="Primary"/> the "/P" ones; <paramref name="Sector"/> NASR's sectorization ("RWY 14L,
/// 32L", "010-310", "NORTH"), if any.
/// </summary>
public sealed record Frequency(double Mhz, string Use, bool Primary, string? Sector = null)
{
    /// <summary>UHF military band (225–400 MHz); VHF airband is 118–137 MHz.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Uhf => Mhz >= 225;
}

public static partial class Frequencies
{
    /// <summary>
    /// The frequency to give an aircraft for <paramref name="use"/>: UHF for military aircraft where the field has
    /// one, VHF otherwise (civil aircraft are always VHF); the one sectorized for this runway or for the side the
    /// aircraft is on (true bearing from the field) when NASR splits them, primary before secondary.
    /// </summary>
    public static double? Pick(IReadOnlyList<Frequency>? list, string use, bool military, string? runwayId = null, double? bearingFromFieldDeg = null)
    {
        if (list == null) return null;
        var candidates = list.Where(f => f.Use == use && (military || !f.Uhf)).ToList();
        if (candidates.Count == 0) return null;
        return candidates
            .OrderByDescending(f => military && f.Uhf)
            .ThenByDescending(f => SectorScore(f.Sector, runwayId, bearingFromFieldDeg))
            .ThenByDescending(f => f.Primary)
            .ThenBy(f => f.Mhz)
            .First().Mhz;
    }

    /// <summary>2 = the sector names this runway or bearing; 1 = no sector; 0 = some other sector.</summary>
    private static int SectorScore(string? sector, string? runwayId, double? bearing)
    {
        if (string.IsNullOrWhiteSpace(sector)) return 1;
        if (runwayId != null && Regex.IsMatch(sector, $@"(?<![0-9]){Regex.Escape(runwayId)}(?![0-9A-Z])", RegexOptions.IgnoreCase)) return 2;
        if (bearing is double b)
        {
            foreach (Match m in RangeRx().Matches(sector))
            {
                double from = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), to = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                bool inside = from <= to ? b >= from && b <= to : b >= from || b <= to;
                if (inside) return 2;
            }
            string? side = Units.Norm360(b) switch { >= 315 or < 45 => "NORTH", < 135 => "EAST", < 225 => "SOUTH", _ => "WEST" };
            if (sector.Contains(side, StringComparison.OrdinalIgnoreCase)) return 2;
        }
        return 0;
    }

    /// <summary>"three four zero point two", "two six niner point zero two five".</summary>
    public static string Spoken(double mhz)
    {
        string s = mhz.ToString("0.0##", CultureInfo.InvariantCulture);
        int dot = s.IndexOf('.');
        return $"{RadioSpeech.Digits(s[..dot])} point {RadioSpeech.Digits(s[(dot + 1)..])}";
    }

    /// <summary>Whether a callsign is a military aircraft (UHF radios): service and squadron callsigns are; an
    /// N-number or a civil operator's ICAO designator (FAA JO 7340.2) is not.</summary>
    public static bool IsMilitary(string callsign)
    {
        if (Regex.IsMatch(callsign, @"^N\d", RegexOptions.IgnoreCase)) return false;
        var m = Regex.Match(callsign, @"^([A-Za-z]{3})\d");
        return !(m.Success && !RadioSpeech.ServicePrefixes.ContainsKey(m.Groups[1].Value) && IcaoTelephony.IsCivil(m.Groups[1].Value));
    }

    [GeneratedRegex(@"\b(\d{3})-(\d{3})\b")] private static partial Regex RangeRx();
}
