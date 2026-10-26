using System.Text;

namespace PARScope.Core.Sim;

/// <summary>
/// Turns numbers, runways and callsigns into how they are spoken on the radio:
/// headings digit by digit ("two four zero"), "niner", altitudes in thousands/hundreds
/// ("two thousand", "one thousand six hundred"), runways with left/right/center.
/// </summary>
public static class RadioSpeech
{
    private static readonly string[] DigitWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"];

    public static string Digits(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (char.IsDigit(c)) sb.Append(DigitWords[c - '0']).Append(' ');
        }
        return sb.ToString().Trim();
    }

    public static string Heading(double deg)
    {
        int h = (int)Math.Round(deg) % 360;
        return Digits((h == 0 ? 360 : h).ToString("000"));
    }

    public static string Altitude(double ft)
    {
        int rounded = (int)(Math.Round(ft / 100.0) * 100);
        if (rounded < 1000) return Digits((rounded / 100).ToString()) + " hundred";
        int thousands = rounded / 1000, hundreds = rounded % 1000 / 100;
        string t = Digits(thousands.ToString()) + " thousand";
        return hundreds == 0 ? t : $"{t} {Digits(hundreds.ToString())} hundred";
    }

    public static string Runway(string id)
    {
        string digits = new(id.TakeWhile(char.IsDigit).ToArray());
        string suffix = id[digits.Length..].ToUpperInvariant() switch
        {
            "L" => " left",
            "R" => " right",
            "C" => " center",
            _ => "",
        };
        return Digits(digits) + suffix;
    }

    /// <summary>Military service prefixes and how they are spoken (FAA JO 7110.65 §2-4-20).</summary>
    public static readonly IReadOnlyDictionary<string, string> ServicePrefixes = new Dictionary<string, string>
    {
        ["VV"] = "Navy",
        ["VM"] = "Marine",
        ["R"] = "Army",
        ["AF"] = "Air Force",
        ["C"] = "Coast Guard",
    };

    private static readonly string[] Nato =
    [
        "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliett", "Kilo", "Lima", "Mike",
        "November", "Oscar", "Papa", "Quebec", "Romeo", "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "X-ray", "Yankee", "Zulu",
    ];

    /// <summary>Mission callsigns spoken as words although they are short.</summary>
    private static readonly HashSet<string> ShortWords = new(StringComparer.OrdinalIgnoreCase) { "PAT", "SAM", "BAT", "CAT", "BUD", "AXE", "ACE", "APE", "BOW" };

    /// <summary>A letter group spelled in the ICAO phonetic alphabet: "AB" → "Alpha Bravo".</summary>
    public static string Spell(string letters) =>
        string.Join(' ', letters.Where(char.IsLetter).Select(c => Nato[char.ToUpperInvariant(c) - 'A']));

    /// <summary>
    /// ICAO three-letter airline designators and their radiotelephony callsigns (ICAO Doc 8585 / FAA JO 7340.2),
    /// for the carriers most often seen in US airspace and on VATSIM.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Airlines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // US passenger
        ["AAL"] = "American", ["UAL"] = "United", ["DAL"] = "Delta", ["SWA"] = "Southwest", ["JBU"] = "JetBlue",
        ["ASA"] = "Alaska", ["FFT"] = "Frontier Flight", ["NKS"] = "Spirit Wings", ["HAL"] = "Hawaiian",
        ["AAY"] = "Allegiant", ["SCX"] = "Sun Country", ["MXY"] = "Moxy", ["VXP"] = "Avelo",
        // US regional
        ["SKW"] = "SkyWest", ["RPA"] = "Brickyard", ["ENY"] = "Envoy", ["EDV"] = "Endeavor", ["JIA"] = "Blue Streak",
        ["PDT"] = "Piedmont", ["ASH"] = "Air Shuttle", ["GJS"] = "Lakes", ["QXE"] = "Horizon",
        // US cargo and charter
        ["FDX"] = "FedEx", ["UPS"] = "UPS", ["GTI"] = "Giant", ["ABX"] = "Abex", ["CKS"] = "Connie", ["ATN"] = "Air Transport",
        ["EJA"] = "Execjet", ["LXJ"] = "Flexjet",
        // US military airlift (ICAO designators)
        ["RCH"] = "Reach", ["CNV"] = "Convoy",
        // Canada and Latin America
        ["ACA"] = "Air Canada", ["WJA"] = "WestJet", ["JZA"] = "Jazz", ["TSC"] = "Air Transat", ["AMX"] = "Aeromexico",
        ["VOI"] = "Volaris", ["CMP"] = "Copa", ["AVA"] = "Avianca",
        // Europe, Middle East, Asia-Pacific
        ["BAW"] = "Speedbird", ["VIR"] = "Virgin", ["DLH"] = "Lufthansa", ["AFR"] = "Airfrans", ["KLM"] = "KLM",
        ["IBE"] = "Iberia", ["EIN"] = "Shamrock", ["SWR"] = "Swiss", ["AUA"] = "Austrian", ["SAS"] = "Scandinavian",
        ["FIN"] = "Finnair", ["TAP"] = "Air Portugal", ["ICE"] = "Iceair", ["THY"] = "Turkish", ["RYR"] = "Ryanair",
        ["EZY"] = "Easy", ["UAE"] = "Emirates", ["QTR"] = "Qatari", ["ETD"] = "Etihad", ["SIA"] = "Singapore",
        ["CPA"] = "Cathay", ["JAL"] = "Japan Air", ["ANA"] = "All Nippon", ["KAL"] = "Korean Air", ["AAR"] = "Asiana",
        ["EVA"] = "Eva", ["CAL"] = "Dynasty", ["CCA"] = "Air China", ["CES"] = "China Eastern", ["CSN"] = "China Southern",
        ["PAL"] = "Philippine", ["QFA"] = "Qantas", ["ANZ"] = "New Zealand",
    };

    private static readonly string[] Ones = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// <summary>A two-digit group as spoken: "34" → "thirty-four", "05" → "zero five", "7" → "seven".</summary>
    private static string Pair(string d)
    {
        int n = int.Parse(d);
        if (d.Length == 2 && d[0] == '0') return "zero " + Digits(d[1..]);
        return n < 20 ? Ones[n] : Tens[n / 10] + (n % 10 == 0 ? "" : "-" + Ones[n % 10]);
    }

    /// <summary>
    /// An airline flight number in group form (7110.65 §2-4-20): "1234" → "twelve thirty-four", "211" → "two
    /// eleven", "100" → "one hundred", "1200" → "twelve hundred", "3" → "three". Five or more digits: one at a time.
    /// </summary>
    public static string GroupNumber(string d)
    {
        d = d.TrimStart('0');
        if (d.Length == 0) return "zero";
        return d.Length switch
        {
            1 or 2 => Pair(d),
            3 => d[1..] == "00" ? $"{Pair(d[..1])} hundred" : $"{Pair(d[..1])} {Pair(d[1..])}",
            4 => d[1..] == "000" ? $"{Pair(d[..1])} thousand" : d[2..] == "00" ? $"{Pair(d[..2])} hundred" : $"{Pair(d[..2])} {Pair(d[2..])}",
            _ => Digits(d),
        };
    }

    /// <summary>
    /// "VV1234" → "Navy one two three four", "REACH481" → "Reach four eight one", "BANSHEE21" → "Banshee two one",
    /// "N123AB" → "November one two three Alpha Bravo", "UAL1234" → "United twelve thirty-four". A leading service
    /// prefix is spoken as the service (7110.65 §2-4-20); an ICAO airline designator as its telephony callsign
    /// with the flight number in group form; a leading word callsign as a word; other letters (registrations)
    /// phonetically. Military flight numbers stay digit by digit.
    /// </summary>
    public static string Callsign(string cs)
    {
        if (!cs.Any(char.IsDigit)) return Spell(cs); // all letters: a registration such as G-ABCD
        var airline = System.Text.RegularExpressions.Regex.Match(cs, @"^([A-Za-z]{3})(\d{1,4})([A-Za-z]{0,2})$");
        if (airline.Success && Airlines.TryGetValue(airline.Groups[1].Value, out var telephony) && !ServicePrefixes.ContainsKey(airline.Groups[1].Value))
        {
            string tail = airline.Groups[3].Value;
            bool military = telephony is "Reach" or "Convoy";
            string number = military ? Digits(airline.Groups[2].Value) : GroupNumber(airline.Groups[2].Value);
            return $"{telephony} {number}" + (tail.Length > 0 ? " " + Spell(tail) : "");
        }
        var parts = new List<string>();
        int i = 0;
        bool first = true;
        while (i < cs.Length)
        {
            bool letters = char.IsLetter(cs[i]);
            int j = i;
            while (j < cs.Length && char.IsLetterOrDigit(cs[j]) && char.IsLetter(cs[j]) == letters) j++;
            if (j == i) { i++; continue; } // separators such as '-'
            string run = cs[i..j];
            if (!letters) parts.Add(Digits(run));
            else if (first && ServicePrefixes.TryGetValue(run.ToUpperInvariant(), out var service)) parts.Add(service);
            else if (first && (run.Length >= 4 || ShortWords.Contains(run))) parts.Add(char.ToUpperInvariant(run[0]) + run[1..].ToLowerInvariant());
            else parts.Add(Spell(run));
            first = false;
            i = j;
        }
        return string.Join(' ', parts);
    }

    /// <summary>A true (METAR) wind direction as ATC issues it: magnetic, to the nearest 10°, 360 for north.</summary>
    public static int MagneticWindDeg(int trueDeg, double magVarEastDeg)
    {
        int d = (int)(Math.Round(Geo.Units.Norm360(trueDeg - magVarEastDeg) / 10) * 10) % 360;
        return d == 0 ? 360 : d;
    }

    public static string Wind(int? dir, int? speed, bool variable)
    {
        if (speed is null) return "";
        if (speed == 0) return "wind calm";
        return $"wind {(variable ? "variable" : Digits(dir!.Value.ToString("000")))} at {Digits(speed.Value.ToString())}";
    }
}
