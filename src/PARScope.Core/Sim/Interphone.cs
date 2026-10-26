using System.Text.RegularExpressions;

namespace PARScope.Core.Sim;

public enum Facility { Tower, Approach }

/// <summary>What a controller-to-controller call was about, read loosely from free wording.</summary>
public sealed record InterphoneCall(Facility To, string Text, LandingIntent? Intent, bool MissedApproach);

/// <summary>
/// Recognizes coordination calls on the same push-to-talk as the final frequency: anything that opens by
/// addressing another controller ("Tower, final…", "Local, …", "Approach, final, …", "Departure…").
/// Wording is read loosely, so non-standard and abbreviated calls work: the landing intent, a missed
/// approach and the aircraft (by callsign word or number) are picked out wherever they appear.
/// </summary>
public static partial class Interphone
{
    public static InterphoneCall? Parse(string text)
    {
        string s = Phraseology.Normalize(text);
        var m = AddresseeRx().Match(s);
        if (!m.Success) return null;
        var to = m.Groups["to"].Value is "tower" or "local" ? Facility.Tower : Facility.Approach;

        LandingIntent? intent = TouchAndGoRx().IsMatch(s) ? LandingIntent.TouchAndGo
            : LowApproachRx().IsMatch(s) ? LandingIntent.LowApproach
            : OptionRx().IsMatch(s) ? LandingIntent.Option
            : FullStopRx().IsMatch(s) ? LandingIntent.FullStop
            : null;
        return new InterphoneCall(to, text.Trim(), intent, MissedRx().IsMatch(s));
    }

    /// <summary>
    /// The training aircraft a call refers to: one whose callsign number is spoken, else whose callsign word
    /// is, else null. "Navy one two three four", "one two three four", "Banshee two one" and "Banshee" all work.
    /// </summary>
    public static SimAircraft? FindAircraft(string text, IEnumerable<SimAircraft> aircraft)
    {
        string s = Phraseology.Normalize(text);
        var list = aircraft.ToList();
        var numbers = NumberRx().Matches(s).Select(x => x.Value).ToList();
        var byNumber = list.Where(a => Number(a.Callsign) is { Length: > 0 } n && numbers.Any(x => x == n || (n.Length >= 3 && x.EndsWith(n[^3..])))).ToList();
        if (byNumber.Count == 1) return byNumber[0];
        var byWord = (byNumber.Count > 1 ? byNumber : list)
            .Where(a => s.Contains(" " + RadioSpeech.Callsign(a.Callsign).Split(' ')[0].ToLowerInvariant() + " ")).ToList();
        return byWord.Count == 1 ? byWord[0] : null;
    }

    private static string Number(string callsign) => new(callsign.SkipWhile(char.IsLetter).ToArray());

    // The addressee opens the call, optionally after a filler; "tower clearance not received" is a pilot call.
    [GeneratedRegex(@"^ (?:(?:hey|uh|um|ok|okay) )?(?<to>tower|local|approach|arrival|departure|radar)\b(?! clearance)")] private static partial Regex AddresseeRx();
    [GeneratedRegex(@"\btouch (?:and|n) go\b|\btouch and goes\b")] private static partial Regex TouchAndGoRx();
    [GeneratedRegex(@"\blow approach\b|\blow pass\b")] private static partial Regex LowApproachRx();
    [GeneratedRegex(@"\boption\b")] private static partial Regex OptionRx();
    [GeneratedRegex(@"\bfull stop\b|\bto land\b|\blanding\b")] private static partial Regex FullStopRx();
    [GeneratedRegex(@"\bmissed\b|\bgo ?around\b|\bwave ?off\b|\bclimb ?out\b")] private static partial Regex MissedRx();
    [GeneratedRegex(@"\b\d{2,5}\b")] private static partial Regex NumberRx();
}
