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
    /// "VV1234" → "Navy one two three four", "REACH481" → "Reach four eight one", "BANSHEE21" → "Banshee two one",
    /// "N123AB" → "November one two three Alpha Bravo". A leading service prefix is spoken as the service
    /// (7110.65 §2-4-20); a leading word callsign as a word; other letters (registrations) phonetically.
    /// </summary>
    public static string Callsign(string cs)
    {
        if (!cs.Any(char.IsDigit)) return Spell(cs); // all letters: a registration such as G-ABCD
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

    public static string Wind(int? dir, int? speed, bool variable)
    {
        if (speed is null) return "";
        if (speed == 0) return "wind calm";
        return $"wind {(variable ? "variable" : Digits(dir!.Value.ToString("000")))} at {Digits(speed.Value.ToString())}";
    }
}
