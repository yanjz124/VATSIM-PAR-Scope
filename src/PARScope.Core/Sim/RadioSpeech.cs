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

    /// <summary>"VV1234" → "Navy one two three four", "REACH481" → "Reach four eight one".</summary>
    public static string Callsign(string cs)
    {
        string letters = new(cs.TakeWhile(char.IsLetter).ToArray());
        string rest = cs[letters.Length..];
        string word = ServicePrefixes.TryGetValue(letters.ToUpperInvariant(), out var spoken) ? spoken
            : letters.Length == 0 ? "" : char.ToUpperInvariant(letters[0]) + letters[1..].ToLowerInvariant();
        return $"{word} {Digits(rest)}".Trim();
    }

    public static string Wind(int? dir, int? speed, bool variable)
    {
        if (speed is null) return "";
        if (speed == 0) return "wind calm";
        return $"wind {(variable ? "variable" : Digits(dir!.Value.ToString("000")))} at {Digits(speed.Value.ToString())}";
    }
}
