using System.Reflection;

namespace PARScope.Core.Sim;

/// <summary>
/// Shortened forms of tactical callwords for flight-plan callsigns with a length limit (BANSHEE21 → BNSH21), from
/// callword-abbreviations.csv, built by scripts/callword-abbreviations.py: 4, 5 and 6 letters for every callword in
/// callsigns.csv, written so they still read and sound like the word (BLACKJACK → BLKJK, BANSHEE → BNSHE).
/// Words not in the list use the same rule (<see cref="Natural"/>).
/// </summary>
public static class CallwordAbbreviations
{
    private static readonly Lazy<Dictionary<string, string[]>> Table = new(Load);

    /// <summary>The listed form of <paramref name="word"/> with <paramref name="letters"/> letters (4–6), if any.</summary>
    public static string? Find(string word, int letters) =>
        letters is >= 3 and <= 6 && Table.Value.TryGetValue(word, out var forms) && forms[letters - 3] is { Length: > 0 } f ? f : null;

    /// <summary>
    /// The form a person would write and still say the same: doubled letters and CK collapse (BANSHEE → BANSHE,
    /// BLACKJACK → BLAKJAK), a silent final E goes, inner vowels go right to left (BLKJK, BNSHE, THNDR), then a
    /// sounded final vowel, then the end is cut.
    /// </summary>
    public static string Natural(string word, int letters)
    {
        if (word.Length <= letters) return word;
        string w = Collapse(word);
        if (w.Length <= letters) return w;
        var c = w.ToList();
        if (c[^1] == 'E' && !IsVowel(c[^2]) && !word.EndsWith("EE", StringComparison.Ordinal)) c.RemoveAt(c.Count - 1);
        for (int i = c.Count - 2; c.Count > letters && i >= 1; i--)
            if (IsVowel(c[i]) || c[i] == 'Y') c.RemoveAt(i);
        if (c.Count > letters && (IsVowel(c[^1]) || c[^1] == 'Y')) c.RemoveAt(c.Count - 1);
        string r = Collapse(new string([.. c]));
        return r.Length > letters ? r[..letters] : r;
    }

    private static bool IsVowel(char ch) => "AEIOU".Contains(ch);

    private static string Collapse(string w)
    {
        w = w.Replace("CK", "K");
        var sb = new System.Text.StringBuilder().Append(w[0]);
        foreach (char ch in w[1..]) if (ch != sb[^1]) sb.Append(ch);
        return sb.ToString();
    }

    private static Dictionary<string, string[]> Load()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PARScope.Core.Sim.callword-abbreviations.csv");
        if (stream == null) return map;
        using var reader = new StreamReader(stream);
        reader.ReadLine(); // header: callword,l3,l4,l5,l6
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = line.Split(',');
            if (f.Length == 5 && f[0].Length > 0) map[f[0]] = f[1..];
        }
        return map;
    }
}
